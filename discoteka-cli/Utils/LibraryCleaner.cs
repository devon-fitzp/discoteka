using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using discoteka_cli.Database;
using Microsoft.Data.Sqlite;

namespace discoteka_cli.Utils;

public static class LibraryCleaner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    public static void Run(double minConfidence, bool dryRun, string? dbPath = null)
    {
        var path = DatabaseInitializer.Initialize(dbPath);
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();

        var logCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var updated = 0;
        var unchanged = 0;
        var skipped = 0;

        updated += CleanTable(connection, "TrackLibrary", "TrackTitle", "TrackArtist", "TrackTitleRaw", "TrackArtistRaw", minConfidence, dryRun, logCounts, ref unchanged, ref skipped);
        updated += CleanTable(connection, "AppleLibrary", "TrackTitle", "TrackArtist", "TrackTitleRaw", "TrackArtistRaw", minConfidence, dryRun, logCounts, ref unchanged, ref skipped);
        updated += CleanTable(connection, "Rekordbox", "TrackTitle", "TrackArtist", "TrackTitleRaw", "TrackArtistRaw", minConfidence, dryRun, logCounts, ref unchanged, ref skipped);
        updated += CleanTable(connection, "FileLibrary", "Title", "Artist", "TitleRaw", "ArtistRaw", minConfidence, dryRun, logCounts, ref unchanged, ref skipped);

        Console.WriteLine($"Updated: {updated}");
        Console.WriteLine($"Unchanged: {unchanged}");
        Console.WriteLine($"Skipped (low confidence): {skipped}");

        var topTags = logCounts.OrderByDescending(kv => kv.Value).Take(20).ToList();
        if (topTags.Count > 0)
        {
            Console.WriteLine("Top log tags:");
            foreach (var tag in topTags)
            {
                Console.WriteLine($"  {tag.Key}: {tag.Value}");
            }
        }
    }

    private static int CleanTable(
        SqliteConnection connection,
        string tableName,
        string titleColumn,
        string artistColumn,
        string titleRawColumn,
        string artistRawColumn,
        double minConfidence,
        bool dryRun,
        Dictionary<string, int> logCounts,
        ref int unchanged,
        ref int skipped)
    {
        using var selectCommand = connection.CreateCommand();
        selectCommand.CommandText = $@"
SELECT rowid,
       {titleColumn},
       {artistColumn},
       {titleRawColumn},
       {artistRawColumn},
       MusicalKey,
       BPM,
       Features,
       DjTags,
       CleanConfidence,
       CleanLog
FROM {tableName};";

        using var transaction = connection.BeginTransaction();
        selectCommand.Transaction = transaction;

        using var updateCommand = connection.CreateCommand();
        updateCommand.Transaction = transaction;
        updateCommand.CommandText = $@"
UPDATE {tableName}
SET {titleColumn} = $title,
    {artistColumn} = $artist,
    MusicalKey = $musicalKey,
    BPM = $bpm,
    Features = $features,
    DjTags = $djTags,
    CleanConfidence = $cleanConfidence,
    CleanLog = $cleanLog
WHERE rowid = $rowId;";

        var updated = 0;
        using var reader = selectCommand.ExecuteReader();
        while (reader.Read())
        {
            var rowId = reader.GetInt64(0);
            var title = reader.IsDBNull(1) ? null : reader.GetString(1);
            var artist = reader.IsDBNull(2) ? null : reader.GetString(2);
            var titleRaw = reader.IsDBNull(3) ? null : reader.GetString(3);
            var artistRaw = reader.IsDBNull(4) ? null : reader.GetString(4);
            var existingKey = reader.IsDBNull(5) ? null : reader.GetString(5);
            var existingBpm = reader.IsDBNull(6) ? (double?)null : reader.GetDouble(6);
            var existingFeatures = reader.IsDBNull(7) ? null : reader.GetString(7);
            var existingDjTags = reader.IsDBNull(8) ? null : reader.GetString(8);
            var existingConfidence = reader.IsDBNull(9) ? (double?)null : reader.GetDouble(9);
            var existingLog = reader.IsDBNull(10) ? null : reader.GetString(10);

            var result = MetadataCleaner.Clean(title ?? titleRaw, artist ?? artistRaw);
            var finalKey = existingKey ?? result.MusicalKey;
            var finalBpm = existingBpm ?? result.Bpm;

            if (result.CleanConfidence < minConfidence)
            {
                skipped++;
                continue;
            }

            var hasChanges =
                !string.Equals(title, result.Title, StringComparison.Ordinal) ||
                !string.Equals(artist, result.Artist, StringComparison.Ordinal) ||
                !string.Equals(existingKey, finalKey, StringComparison.Ordinal) ||
                !Nullable.Equals(existingBpm, finalBpm) ||
                !string.Equals(existingFeatures, result.FeaturesJson, StringComparison.Ordinal) ||
                !string.Equals(existingDjTags, result.DjTagsJson, StringComparison.Ordinal) ||
                !Nullable.Equals(existingConfidence, result.CleanConfidence) ||
                !string.Equals(existingLog, result.CleanLogJson, StringComparison.Ordinal);

            if (!hasChanges)
            {
                unchanged++;
                continue;
            }

            if (dryRun)
            {
                Console.WriteLine($"{tableName} row {rowId}: \"{title}\" -> \"{result.Title}\", \"{artist}\" -> \"{result.Artist}\" (confidence {result.CleanConfidence:0.00})");
                AccumulateLogCounts(result.CleanLogJson, logCounts);
                continue;
            }

            updateCommand.Parameters.Clear();
            updateCommand.Parameters.AddWithValue("$title", (object?)result.Title ?? DBNull.Value);
            updateCommand.Parameters.AddWithValue("$artist", (object?)result.Artist ?? DBNull.Value);
            updateCommand.Parameters.AddWithValue("$musicalKey", (object?)finalKey ?? DBNull.Value);
            updateCommand.Parameters.AddWithValue("$bpm", (object?)finalBpm ?? DBNull.Value);
            updateCommand.Parameters.AddWithValue("$features", (object?)result.FeaturesJson ?? DBNull.Value);
            updateCommand.Parameters.AddWithValue("$djTags", (object?)result.DjTagsJson ?? DBNull.Value);
            updateCommand.Parameters.AddWithValue("$cleanConfidence", result.CleanConfidence);
            updateCommand.Parameters.AddWithValue("$cleanLog", (object?)result.CleanLogJson ?? DBNull.Value);
            updateCommand.Parameters.AddWithValue("$rowId", rowId);
            updateCommand.ExecuteNonQuery();
            AccumulateLogCounts(result.CleanLogJson, logCounts);
            updated++;
        }

        transaction.Commit();
        return updated;
    }

    private static void AccumulateLogCounts(string? logJson, Dictionary<string, int> logCounts)
    {
        if (string.IsNullOrWhiteSpace(logJson))
        {
            return;
        }

        try
        {
            var tags = JsonSerializer.Deserialize<string[]>(logJson, JsonOptions);
            if (tags == null)
            {
                return;
            }

            foreach (var tag in tags)
            {
                if (string.IsNullOrWhiteSpace(tag))
                {
                    continue;
                }

                logCounts[tag] = logCounts.TryGetValue(tag, out var count) ? count + 1 : 1;
            }
        }
        catch
        {
        }
    }
}
