using System.Xml.Linq;
using discoteka_cli.Database;
using discoteka_cli.Models;
using Microsoft.Data.Sqlite;

namespace discoteka_cli.ImporterModules;

public class AppleMusicLibrary : IXmlModule
{
    private XDocument? _document;
    private readonly List<AppleMusicTrack> _tracks = new();

    public IReadOnlyList<AppleMusicTrack> Tracks => _tracks;

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

        var plistDict = _document.Root?.Element("dict");
        if (plistDict == null)
        {
            return 0;
        }

        var rootDict = ParseDict(plistDict);
        if (!rootDict.TryGetValue("Tracks", out var tracksElement))
        {
            return 0;
        }

        var tracksDict = ParseDict(tracksElement);
        foreach (var entry in tracksDict)
        {
            if (entry.Value.Name.LocalName != "dict")
            {
                continue;
            }

            var trackDict = ParseDict(entry.Value);
            var track = new AppleMusicTrack
            {
                AppleMusicId = GetString(trackDict, "Persistent ID") ?? GetString(trackDict, "Track ID"),
                TrackTitle = GetString(trackDict, "Name"),
                TrackArtist = GetString(trackDict, "Artist"),
                AlbumTitle = GetString(trackDict, "Album"),
                AlbumArtist = GetString(trackDict, "Album Artist"),
                Genre = GetString(trackDict, "Genre"),
                Duration = GetInt(trackDict, "Total Time"),
                Plays = GetInt(trackDict, "Play Count")
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
FROM AppleLibrary
WHERE (
    AppleMusicId = $appleMusicId
    OR ($appleMusicId IS NULL AND TrackTitle = $trackTitle AND TrackArtist = $trackArtist AND AlbumTitle = $albumTitle)
)
LIMIT 1;";

        using var insertCommand = connection.CreateCommand();
        insertCommand.Transaction = transaction;
        insertCommand.CommandText = @"
INSERT INTO AppleLibrary (
    AppleMusicId,
    TrackTitle,
    TrackArtist,
    TrackTitleRaw,
    TrackArtistRaw,
    AlbumTitle,
    AlbumArtist,
    Genre,
    Duration,
    Plays,
    MusicalKey,
    BPM,
    Features,
    DjTags,
    CleanConfidence,
    CleanLog
)
VALUES (
    $appleMusicId,
    $trackTitle,
    $trackArtist,
    $trackTitleRaw,
    $trackArtistRaw,
    $albumTitle,
    $albumArtist,
    $genre,
    $duration,
    $plays,
    $musicalKey,
    $bpm,
    $features,
    $djTags,
    $cleanConfidence,
    $cleanLog
);";

        var inserted = 0;
        foreach (var track in _tracks)
        {
            existsCommand.Parameters.Clear();
            existsCommand.Parameters.AddWithValue("$appleMusicId", (object?)track.AppleMusicId ?? DBNull.Value);
            existsCommand.Parameters.AddWithValue("$trackTitle", (object?)track.TrackTitle ?? DBNull.Value);
            existsCommand.Parameters.AddWithValue("$trackArtist", (object?)track.TrackArtist ?? DBNull.Value);
            existsCommand.Parameters.AddWithValue("$albumTitle", (object?)track.AlbumTitle ?? DBNull.Value);

            var exists = existsCommand.ExecuteScalar();
            if (exists != null)
            {
                continue;
            }

            insertCommand.Parameters.Clear();
            insertCommand.Parameters.AddWithValue("$appleMusicId", (object?)track.AppleMusicId ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$trackTitle", (object?)track.TrackTitle ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$trackArtist", (object?)track.TrackArtist ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$trackTitleRaw", (object?)track.TrackTitle ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$trackArtistRaw", (object?)track.TrackArtist ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$albumTitle", (object?)track.AlbumTitle ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$albumArtist", (object?)track.AlbumArtist ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$genre", (object?)track.Genre ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$duration", (object?)track.Duration ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$plays", (object?)track.Plays ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$musicalKey", DBNull.Value);
            insertCommand.Parameters.AddWithValue("$bpm", DBNull.Value);
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

    private static Dictionary<string, XElement> ParseDict(XElement dictElement)
    {
        var elements = dictElement.Elements().ToList();
        var dict = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < elements.Count - 1; i += 2)
        {
            if (elements[i].Name.LocalName != "key")
            {
                continue;
            }

            var key = elements[i].Value;
            var value = elements[i + 1];
            dict[key] = value;
        }

        return dict;
    }

    private static string? GetString(Dictionary<string, XElement> dict, string key)
    {
        if (!dict.TryGetValue(key, out var element))
        {
            return null;
        }

        return element.Name.LocalName switch
        {
            "string" => element.Value,
            "integer" => element.Value,
            "date" => element.Value,
            "true" => "true",
            "false" => "false",
            _ => element.Value
        };
    }

    private static int? GetInt(Dictionary<string, XElement> dict, string key)
    {
        var value = GetString(dict, key);
        if (int.TryParse(value, out var parsed))
        {
            return parsed;
        }

        return null;
    }
}
