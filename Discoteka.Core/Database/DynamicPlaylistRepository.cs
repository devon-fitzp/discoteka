using Discoteka.Core.Models;
using Microsoft.Data.Sqlite;

namespace Discoteka.Core.Database;

public interface IDynamicPlaylistRepository
{
    Task<IReadOnlyList<DynamicPlaylist>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<DynamicPlaylist> InsertAsync(DynamicPlaylist playlist, CancellationToken cancellationToken = default);
    Task DeleteAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates the playlist rule against TrackLibrary and returns matching tracks,
    /// ordered by plays descending.
    /// </summary>
    Task<IReadOnlyList<TrackLibraryTrack>> EvaluateAsync(DynamicPlaylist playlist, CancellationToken cancellationToken = default);
}

public sealed class DynamicPlaylistRepository : IDynamicPlaylistRepository
{
    private readonly string _dbPath;

    public DynamicPlaylistRepository(string? dbPath = null)
    {
        _dbPath = dbPath ?? DbPaths.GetDefaultDbPath();
    }

    public async Task<IReadOnlyList<DynamicPlaylist>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(DbPaths.BuildConnectionString(_dbPath));
        await connection.OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, RuleField, Operator, ValueA, ValueB FROM DynamicPlaylists ORDER BY Name;";

        var results = new List<DynamicPlaylist>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new DynamicPlaylist
            {
                Id = reader.GetInt64(0),
                Name = reader.GetString(1),
                RuleField = reader.GetString(2),
                Operator = reader.GetString(3),
                ValueA = reader.GetInt32(4),
                ValueB = reader.IsDBNull(5) ? null : reader.GetInt32(5)
            });
        }

        return results;
    }

    public async Task<DynamicPlaylist> InsertAsync(DynamicPlaylist playlist, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(DbPaths.BuildConnectionString(_dbPath));
        await connection.OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO DynamicPlaylists (Name, RuleField, Operator, ValueA, ValueB)
VALUES ($name, $field, $op, $a, $b);
SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$name", playlist.Name);
        command.Parameters.AddWithValue("$field", playlist.RuleField);
        command.Parameters.AddWithValue("$op", playlist.Operator);
        command.Parameters.AddWithValue("$a", playlist.ValueA);
        command.Parameters.AddWithValue("$b", (object?)playlist.ValueB ?? DBNull.Value);

        var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        playlist.Id = id;
        return playlist;
    }

    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(DbPaths.BuildConnectionString(_dbPath));
        await connection.OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM DynamicPlaylists WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TrackLibraryTrack>> EvaluateAsync(DynamicPlaylist playlist, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(DbPaths.BuildConnectionString(_dbPath));
        await connection.OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();
        var whereClause = playlist.Operator switch
        {
            ">=" => "COALESCE(Plays, 0) >= $a",
            "<=" => "COALESCE(Plays, 0) <= $a",
            "between" => "COALESCE(Plays, 0) >= $a AND COALESCE(Plays, 0) <= $b",
            _ => throw new InvalidOperationException($"Unknown playlist operator: {playlist.Operator}")
        };

        command.CommandText = $@"
SELECT TrackId, TrackTitle, TrackArtist, AlbumTitle, AlbumArtist, Genre, TrackNumber,
       Duration, Plays, AppleMusicId, RekordboxId, FilePath, DjTags
FROM TrackLibrary
WHERE {whereClause}
ORDER BY COALESCE(Plays, 0) DESC;";

        command.Parameters.AddWithValue("$a", playlist.ValueA);
        if (playlist.ValueB.HasValue)
        {
            command.Parameters.AddWithValue("$b", playlist.ValueB.Value);
        }

        var results = new List<TrackLibraryTrack>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new TrackLibraryTrack
            {
                TrackId = reader.IsDBNull(0) ? null : reader.GetInt64(0).ToString(),
                TrackTitle = reader.IsDBNull(1) ? null : reader.GetString(1),
                TrackArtist = reader.IsDBNull(2) ? null : reader.GetString(2),
                AlbumTitle = reader.IsDBNull(3) ? null : reader.GetString(3),
                AlbumArtist = reader.IsDBNull(4) ? null : reader.GetString(4),
                Genre = reader.IsDBNull(5) ? null : reader.GetString(5),
                TrackNumber = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                Duration = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                Plays = reader.IsDBNull(8) ? null : reader.GetInt32(8),
                AppleMusicId = reader.IsDBNull(9) ? null : reader.GetString(9),
                RekordboxId = reader.IsDBNull(10) ? null : reader.GetString(10),
                FilePath = reader.IsDBNull(11) ? null : reader.GetString(11),
                DjTags = reader.IsDBNull(12) ? null : reader.GetString(12)
            });
        }

        return results;
    }
}
