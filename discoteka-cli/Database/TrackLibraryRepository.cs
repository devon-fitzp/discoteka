using discoteka_cli.Models;
using Microsoft.Data.Sqlite;

namespace discoteka_cli.Database;

public interface ITrackLibraryRepository
{
    Task<IReadOnlyList<TrackLibraryTrack>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TrackLibraryTrack>> GetAvailableLocallyAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TrackLibraryTrack>> GetNoLocalFileAsync(CancellationToken cancellationToken = default);
    Task IncrementPlayCountAsync(long trackId, CancellationToken cancellationToken = default);
    Task InsertRecentActivityAsync(long trackId, DateTime playedAtUtc, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IndexedArtistAlbumEntry>> GetIndexedArtistAlbumsAsync(bool? requireLocalFile = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TrackLibraryTrack>> GetIndexedAlbumTracksAsync(long albumId, bool? requireLocalFile = null, CancellationToken cancellationToken = default);
    Task<TrackMetadataSnapshot?> GetTrackMetadataSnapshotAsync(long trackId, CancellationToken cancellationToken = default);
    Task SaveMetadataTabAsync(MetadataTabEntry tab, CancellationToken cancellationToken = default);
}

public sealed class TrackLibraryRepository : ITrackLibraryRepository
{
    private readonly string _dbPath;

    public TrackLibraryRepository(string? dbPath = null)
    {
        _dbPath = DatabaseInitializer.Initialize(dbPath);
    }

    public async Task<IReadOnlyList<TrackLibraryTrack>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await QueryTracksAsync(
            whereClause: null,
            cancellationToken);
    }

    public async Task<IReadOnlyList<TrackLibraryTrack>> GetAvailableLocallyAsync(CancellationToken cancellationToken = default)
    {
        return await QueryTracksAsync(
            whereClause: "t.FilePath IS NOT NULL AND TRIM(t.FilePath) <> ''",
            cancellationToken);
    }

    public async Task<IReadOnlyList<TrackLibraryTrack>> GetNoLocalFileAsync(CancellationToken cancellationToken = default)
    {
        return await QueryTracksAsync(
            whereClause: "t.FilePath IS NULL OR TRIM(t.FilePath) = ''",
            cancellationToken);
    }

    public async Task IncrementPlayCountAsync(long trackId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(DbPaths.BuildConnectionString(_dbPath));
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
UPDATE TrackLibrary
SET Plays = COALESCE(Plays, 0) + 1
WHERE TrackId = $trackId;";
        command.Parameters.AddWithValue("$trackId", trackId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task InsertRecentActivityAsync(long trackId, DateTime playedAtUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(DbPaths.BuildConnectionString(_dbPath));
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO RecentActivity (TrackId, PlayedAtUtc)
VALUES ($trackId, $playedAtUtc);";
        command.Parameters.AddWithValue("$trackId", trackId);
        command.Parameters.AddWithValue("$playedAtUtc", playedAtUtc.ToUniversalTime().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<IndexedArtistAlbumEntry>> GetIndexedArtistAlbumsAsync(bool? requireLocalFile = null, CancellationToken cancellationToken = default)
    {
        var results = new List<IndexedArtistAlbumEntry>();

        await using var connection = new SqliteConnection(DbPaths.BuildConnectionString(_dbPath));
        await connection.OpenAsync(cancellationToken);

        var localWhere = requireLocalFile switch
        {
            true => "t.FilePath IS NOT NULL AND TRIM(t.FilePath) <> ''",
            false => "(t.FilePath IS NULL OR TRIM(t.FilePath) = '')",
            _ => "1=1"
        };

        await using var command = connection.CreateCommand();
        command.CommandText = $@"
SELECT
    ar.ArtistId,
    ar.ArtistName,
    al.AlbumId,
    al.AlbumTitle,
    al.AlbumArtistName,
    al.ReleaseYear,
    COUNT(DISTINCT at.TrackId) AS AlbumTrackCount
FROM TrackArtists ar
JOIN ArtistToAlbum aa ON aa.ArtistId = ar.ArtistId
JOIN TrackAlbums al ON al.AlbumId = aa.AlbumId
JOIN AlbumToTrack at ON at.AlbumId = al.AlbumId
JOIN TrackLibrary t ON t.TrackId = at.TrackId
WHERE {localWhere}
GROUP BY ar.ArtistId, ar.ArtistName, al.AlbumId, al.AlbumTitle, al.AlbumArtistName, al.ReleaseYear
ORDER BY ar.ArtistName COLLATE NOCASE, al.AlbumTitle COLLATE NOCASE;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new IndexedArtistAlbumEntry
            {
                ArtistId = reader.GetInt64(0),
                ArtistName = ReadString(reader, 1) ?? "Unknown Artist",
                AlbumId = reader.GetInt64(2),
                AlbumTitle = ReadString(reader, 3) ?? "Unknown Album",
                AlbumArtistName = ReadString(reader, 4) ?? "Unknown Artist",
                ReleaseYear = ReadInt(reader, 5),
                AlbumTrackCount = reader.IsDBNull(6) ? 0 : reader.GetInt32(6)
            });
        }

        return results;
    }

    public async Task<IReadOnlyList<TrackLibraryTrack>> GetIndexedAlbumTracksAsync(long albumId, bool? requireLocalFile = null, CancellationToken cancellationToken = default)
    {
        var results = new List<TrackLibraryTrack>();

        await using var connection = new SqliteConnection(DbPaths.BuildConnectionString(_dbPath));
        await connection.OpenAsync(cancellationToken);

        var localWhere = requireLocalFile switch
        {
            true => "AND t.FilePath IS NOT NULL AND TRIM(t.FilePath) <> ''",
            false => "AND (t.FilePath IS NULL OR TRIM(t.FilePath) = '')",
            _ => string.Empty
        };

        await using var command = connection.CreateCommand();
        command.CommandText = $@"
SELECT
    t.TrackId,
    t.TrackTitle,
    t.TrackArtist,
    t.AlbumTitle,
    t.AlbumArtist,
    t.Genre,
    COALESCE(at.TrackNumber, t.TrackNumber, a.TrackNumber, f.TrackNumber) AS TrackNumber,
    t.Duration,
    t.Plays,
    t.AppleMusicId,
    t.RekordboxId,
    t.FilePath,
    t.DjTags
FROM AlbumToTrack at
JOIN TrackLibrary t ON t.TrackId = at.TrackId
LEFT JOIN AppleLibrary a ON a.AppleMusicId = t.AppleMusicId
LEFT JOIN FileLibrary f ON f.Path = t.FilePath
WHERE at.AlbumId = $albumId
{localWhere}
ORDER BY at.SortOrder, COALESCE(at.TrackNumber, t.TrackNumber, a.TrackNumber, f.TrackNumber, 2147483647), t.TrackTitle COLLATE NOCASE, t.TrackId;";
        command.Parameters.AddWithValue("$albumId", albumId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new TrackLibraryTrack
            {
                TrackId = ReadString(reader, 0),
                TrackTitle = ReadString(reader, 1),
                TrackArtist = ReadString(reader, 2),
                AlbumTitle = ReadString(reader, 3),
                AlbumArtist = ReadString(reader, 4),
                Genre = ReadString(reader, 5),
                TrackNumber = ReadInt(reader, 6),
                Duration = ReadInt(reader, 7),
                Plays = ReadInt(reader, 8),
                AppleMusicId = ReadString(reader, 9),
                RekordboxId = ReadString(reader, 10),
                FilePath = ReadString(reader, 11),
                DjTags = ReadString(reader, 12)
            });
        }

        return results;
    }

    public async Task<TrackMetadataSnapshot?> GetTrackMetadataSnapshotAsync(long trackId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(DbPaths.BuildConnectionString(_dbPath));
        await connection.OpenAsync(cancellationToken);

        var snapshot = new TrackMetadataSnapshot
        {
            TrackId = trackId
        };

        var mainTab = await QuerySingleTabAsync(connection, "TrackLibrary", "TrackId", trackId.ToString(), "Main", cancellationToken);
        if (mainTab == null)
        {
            return null;
        }

        snapshot.Tabs.Add(mainTab);

        var appleTabs = await QueryLinkedTabsAsync(
            connection,
            titlePrefix: "Apple Music",
            sql: @"
SELECT a.*
FROM AppleLibrary a
JOIN TrackToApple ta ON ta.AppleMusicId = a.AppleMusicId
WHERE ta.TrackId = $trackId;",
            trackId,
            keyColumn: "AppleMusicId",
            tableName: "AppleLibrary",
            cancellationToken);
        snapshot.Tabs.AddRange(appleTabs);

        var rekordboxTabs = await QueryLinkedTabsAsync(
            connection,
            titlePrefix: "Rekordbox",
            sql: @"
SELECT r.*
FROM Rekordbox r
JOIN TrackToRekordbox tr ON tr.RekordboxId = r.TrackId
WHERE tr.TrackId = $trackId;",
            trackId,
            keyColumn: "TrackId",
            tableName: "Rekordbox",
            cancellationToken);
        snapshot.Tabs.AddRange(rekordboxTabs);

        var fileTabs = await QueryLinkedTabsAsync(
            connection,
            titlePrefix: "File",
            sql: @"
SELECT f.*
FROM FileLibrary f
JOIN TrackToFile tf ON tf.FileId = f.FileId
WHERE tf.TrackId = $trackId;",
            trackId,
            keyColumn: "FileId",
            tableName: "FileLibrary",
            cancellationToken);

        foreach (var tab in fileTabs)
        {
            var fileType = tab.Fields.FirstOrDefault(field => field.Name == "FileType")?.Value;
            var path = tab.Fields.FirstOrDefault(field => field.Name == "Path")?.Value;
            var suffix = string.IsNullOrWhiteSpace(fileType) ? "File" : fileType!.ToUpperInvariant();
            var fileName = string.IsNullOrWhiteSpace(path) ? string.Empty : $" - {System.IO.Path.GetFileName(path)}";
            tab.Title = $"{suffix}{fileName}";
        }

        snapshot.Tabs.AddRange(fileTabs);
        return snapshot;
    }

    public async Task SaveMetadataTabAsync(MetadataTabEntry tab, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tab.TableName) || string.IsNullOrWhiteSpace(tab.KeyColumn) || string.IsNullOrWhiteSpace(tab.KeyValue))
        {
            throw new ArgumentException("Metadata tab is missing table/key identity.");
        }

        var editableFields = tab.Fields.Where(field => !field.IsPrimaryKey).ToList();
        if (editableFields.Count == 0)
        {
            return;
        }

        await using var connection = new SqliteConnection(DbPaths.BuildConnectionString(_dbPath));
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        var setClauses = new List<string>();
        for (var i = 0; i < editableFields.Count; i++)
        {
            var field = editableFields[i];
            var parameter = $"$p{i}";
            setClauses.Add($"{field.Name} = {parameter}");
            command.Parameters.AddWithValue(parameter, ConvertFieldValue(field));
        }

        command.Parameters.AddWithValue("$key", tab.KeyValue);
        command.CommandText = $@"
UPDATE {tab.TableName}
SET {string.Join(", ", setClauses)}
WHERE {tab.KeyColumn} = $key;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<TrackLibraryTrack>> QueryTracksAsync(string? whereClause, CancellationToken cancellationToken)
    {
        var results = new List<TrackLibraryTrack>();

        await using var connection = new SqliteConnection(DbPaths.BuildConnectionString(_dbPath));
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $@"
SELECT
    t.TrackId,
    t.TrackTitle,
    t.TrackArtist,
    t.AlbumTitle,
    t.AlbumArtist,
    t.Genre,
    COALESCE(t.TrackNumber, a.TrackNumber, f.TrackNumber) AS TrackNumber,
    t.Duration,
    t.Plays,
    t.AppleMusicId,
    t.RekordboxId,
    t.FilePath,
    t.DjTags
FROM TrackLibrary t
LEFT JOIN AppleLibrary a ON a.AppleMusicId = t.AppleMusicId
LEFT JOIN FileLibrary f ON f.Path = t.FilePath
{(string.IsNullOrWhiteSpace(whereClause) ? string.Empty : $"WHERE {whereClause}")}
ORDER BY t.TrackTitle;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new TrackLibraryTrack
            {
                TrackId = ReadString(reader, 0),
                TrackTitle = ReadString(reader, 1),
                TrackArtist = ReadString(reader, 2),
                AlbumTitle = ReadString(reader, 3),
                AlbumArtist = ReadString(reader, 4),
                Genre = ReadString(reader, 5),
                TrackNumber = ReadInt(reader, 6),
                Duration = ReadInt(reader, 7),
                Plays = ReadInt(reader, 8),
                AppleMusicId = ReadString(reader, 9),
                RekordboxId = ReadString(reader, 10),
                FilePath = ReadString(reader, 11),
                DjTags = ReadString(reader, 12)
            });
        }

        return results;
    }

    private static async Task<MetadataTabEntry?> QuerySingleTabAsync(
        SqliteConnection connection,
        string tableName,
        string keyColumn,
        string keyValue,
        string title,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {tableName} WHERE {keyColumn} = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", keyValue);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var schema = await GetTableSchemaAsync(connection, tableName, cancellationToken);
        return ReadMetadataTab(reader, schema, tableName, keyColumn, title);
    }

    private static async Task<List<MetadataTabEntry>> QueryLinkedTabsAsync(
        SqliteConnection connection,
        string titlePrefix,
        string sql,
        long trackId,
        string keyColumn,
        string tableName,
        CancellationToken cancellationToken)
    {
        var schema = await GetTableSchemaAsync(connection, tableName, cancellationToken);
        var tabs = new List<MetadataTabEntry>();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$trackId", trackId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var index = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            index++;
            tabs.Add(ReadMetadataTab(reader, schema, tableName, keyColumn, $"{titlePrefix}{(index > 1 ? $" #{index}" : string.Empty)}"));
        }

        return tabs;
    }

    private static MetadataTabEntry ReadMetadataTab(
        SqliteDataReader reader,
        Dictionary<string, (string DeclaredType, bool IsPrimaryKey)> schema,
        string tableName,
        string keyColumn,
        string title)
    {
        var fields = new List<MetadataFieldEntry>();
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            schema.TryGetValue(name, out var info);
            fields.Add(new MetadataFieldEntry
            {
                Name = name,
                DeclaredType = info.DeclaredType ?? string.Empty,
                Value = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i)),
                IsPrimaryKey = info.IsPrimaryKey || string.Equals(name, keyColumn, StringComparison.OrdinalIgnoreCase)
            });
        }

        var keyValue = fields.FirstOrDefault(field => string.Equals(field.Name, keyColumn, StringComparison.OrdinalIgnoreCase))?.Value;
        return new MetadataTabEntry
        {
            Title = title,
            TableName = tableName,
            KeyColumn = keyColumn,
            KeyValue = keyValue,
            Fields = fields
        };
    }

    private static async Task<Dictionary<string, (string DeclaredType, bool IsPrimaryKey)>> GetTableSchemaAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        var schema = new Dictionary<string, (string DeclaredType, bool IsPrimaryKey)>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.IsDBNull(1) ? null : reader.GetString(1);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var type = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            var pk = !reader.IsDBNull(5) && Convert.ToInt32(reader.GetValue(5)) > 0;
            schema[name] = (type, pk);
        }

        return schema;
    }

    private static object ConvertFieldValue(MetadataFieldEntry field)
    {
        if (string.IsNullOrWhiteSpace(field.Value))
        {
            return DBNull.Value;
        }

        var raw = field.Value.Trim();
        var type = (field.DeclaredType ?? string.Empty).ToUpperInvariant();

        if (type.Contains("INT", StringComparison.Ordinal))
        {
            return long.TryParse(raw, out var integer) ? integer : raw;
        }

        if (type.Contains("REAL", StringComparison.Ordinal) ||
            type.Contains("FLOA", StringComparison.Ordinal) ||
            type.Contains("DOUB", StringComparison.Ordinal))
        {
            return double.TryParse(raw, out var real) ? real : raw;
        }

        return raw;
    }

    private static string? ReadString(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static int? ReadInt(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }
}
