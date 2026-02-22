using System.Xml.Linq;
using discoteka_cli.Database;
using discoteka_cli.Models;
using Microsoft.Data.Sqlite;

namespace discoteka_cli.ImporterModules;

public class RekordboxLibrary : IXmlModule
{
    private XDocument? _document;
    private readonly List<RekordboxTrack> _tracks = new();

    public IReadOnlyList<RekordboxTrack> Tracks => _tracks;

    public void Load(string filePath)
    {
        _document = XDocument.Load(filePath);
    }

    public int ParseTracks()
    {
        _tracks.Clear();
        if (_document == null)
        {
            throw new InvalidOperationException("XML document is not loaded. Call Load() first.");
        }

        var collection = _document.Root?.Element("COLLECTION");
        if (collection == null)
        {
            return 0;
        }

        foreach (var trackElement in collection.Elements("TRACK"))
        {
            var track = new RekordboxTrack
            {
                TrackId = GetAttribute(trackElement, "TrackID"),
                TrackTitle = GetAttribute(trackElement, "Name"),
                TrackArtist = GetAttribute(trackElement, "Artist"),
                AlbumTitle = GetAttribute(trackElement, "Album"),
                AlbumArtist = GetAttribute(trackElement, "AlbumArtist"),
                Duration = GetDurationMilliseconds(trackElement),
                BPM = GetDoubleAttribute(trackElement, "AverageBpm"),
                Key = GetAttribute(trackElement, "Tonality"),
                FilePath = GetAttribute(trackElement, "Location")
            };

            _tracks.Add(track);
        }

        return _tracks.Count;
    }

    public int AddToDatabase(string? dbPath = null)
    {
        if (_tracks.Count == 0)
        {
            return 0;
        }

        var path = DatabaseInitializer.Initialize(dbPath);
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();

        using var transaction = connection.BeginTransaction();
        using var existsCommand = connection.CreateCommand();
        existsCommand.Transaction = transaction;
        existsCommand.CommandText = @"
SELECT 1
FROM Rekordbox
WHERE (
    TrackId = $trackId
    OR ($trackId IS NULL AND FilePath = $filePath AND TrackTitle = $trackTitle AND TrackArtist = $trackArtist)
)
LIMIT 1;";

        using var insertCommand = connection.CreateCommand();
        insertCommand.Transaction = transaction;
        insertCommand.CommandText = @"
INSERT INTO Rekordbox (
    TrackId,
    TrackTitle,
    TrackArtist,
    TrackTitleRaw,
    TrackArtistRaw,
    AlbumTitle,
    AlbumArtist,
    Duration,
    BPM,
    Key,
    FilePath,
    MusicalKey,
    Features,
    DjTags,
    CleanConfidence,
    CleanLog
)
VALUES (
    $trackId,
    $trackTitle,
    $trackArtist,
    $trackTitleRaw,
    $trackArtistRaw,
    $albumTitle,
    $albumArtist,
    $duration,
    $bpm,
    $key,
    $filePath,
    $musicalKey,
    $features,
    $djTags,
    $cleanConfidence,
    $cleanLog
);";

        var inserted = 0;
        foreach (var track in _tracks)
        {
            existsCommand.Parameters.Clear();
            existsCommand.Parameters.AddWithValue("$trackId", (object?)track.TrackId ?? DBNull.Value);
            existsCommand.Parameters.AddWithValue("$filePath", (object?)track.FilePath ?? DBNull.Value);
            existsCommand.Parameters.AddWithValue("$trackTitle", (object?)track.TrackTitle ?? DBNull.Value);
            existsCommand.Parameters.AddWithValue("$trackArtist", (object?)track.TrackArtist ?? DBNull.Value);

            var exists = existsCommand.ExecuteScalar();
            if (exists != null)
            {
                continue;
            }

            insertCommand.Parameters.Clear();
            insertCommand.Parameters.AddWithValue("$trackId", (object?)track.TrackId ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$trackTitle", (object?)track.TrackTitle ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$trackArtist", (object?)track.TrackArtist ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$trackTitleRaw", (object?)track.TrackTitle ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$trackArtistRaw", (object?)track.TrackArtist ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$albumTitle", (object?)track.AlbumTitle ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$albumArtist", (object?)track.AlbumArtist ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$duration", (object?)track.Duration ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$bpm", (object?)track.BPM ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$key", (object?)track.Key ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$filePath", (object?)track.FilePath ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$musicalKey", DBNull.Value);
            insertCommand.Parameters.AddWithValue("$features", DBNull.Value);
            insertCommand.Parameters.AddWithValue("$djTags", DBNull.Value);
            insertCommand.Parameters.AddWithValue("$cleanConfidence", DBNull.Value);
            insertCommand.Parameters.AddWithValue("$cleanLog", DBNull.Value);

            insertCommand.ExecuteNonQuery();
            inserted++;
        }

        transaction.Commit();
        return inserted;
    }

    private static string? GetAttribute(XElement element, string name)
    {
        return element.Attribute(name)?.Value;
    }

    private static double? GetDoubleAttribute(XElement element, string name)
    {
        var value = GetAttribute(element, name);
        if (double.TryParse(value, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static int? GetDurationMilliseconds(XElement element)
    {
        var value = GetAttribute(element, "TotalTime");
        if (int.TryParse(value, out var seconds))
        {
            return seconds * 1000;
        }

        return null;
    }
}
