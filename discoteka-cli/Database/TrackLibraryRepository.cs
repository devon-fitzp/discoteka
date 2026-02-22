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
            whereClause: "FilePath IS NOT NULL AND TRIM(FilePath) <> ''",
            cancellationToken);
    }

    public async Task<IReadOnlyList<TrackLibraryTrack>> GetNoLocalFileAsync(CancellationToken cancellationToken = default)
    {
        return await QueryTracksAsync(
            whereClause: "FilePath IS NULL OR TRIM(FilePath) = ''",
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

    private async Task<IReadOnlyList<TrackLibraryTrack>> QueryTracksAsync(string? whereClause, CancellationToken cancellationToken)
    {
        var results = new List<TrackLibraryTrack>();

        await using var connection = new SqliteConnection(DbPaths.BuildConnectionString(_dbPath));
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $@"
SELECT
    TrackId,
    TrackTitle,
    TrackArtist,
    AlbumTitle,
    AlbumArtist,
    Genre,
    Duration,
    Plays,
    AppleMusicId,
    RekordboxId,
    FilePath
FROM TrackLibrary
{(string.IsNullOrWhiteSpace(whereClause) ? string.Empty : $"WHERE {whereClause}")}
ORDER BY TrackTitle;";

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
                Duration = ReadInt(reader, 6),
                Plays = ReadInt(reader, 7),
                AppleMusicId = ReadString(reader, 8),
                RekordboxId = ReadString(reader, 9),
                FilePath = ReadString(reader, 10)
            });
        }

        return results;
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
