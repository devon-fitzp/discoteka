using System.Xml.Linq;
using Discoteka.Core.Database;
using Discoteka.Core.Models;
using Microsoft.Data.Sqlite;

namespace Discoteka.Core.ImporterModules;

/// <summary>
/// Imports an Apple Music / iTunes XML library export into the <c>AppleLibrary</c> table.
/// <para>
/// Apple's plist format uses a root <c>&lt;plist&gt;&lt;dict&gt;</c> containing a "Tracks"
/// key whose value is a <c>&lt;dict&gt;</c> of track-ID → track-dict pairs. Each track dict
/// is a sequence of alternating <c>&lt;key&gt;</c> / value-element pairs.
/// </para>
/// </summary>
public class AppleMusicLibrary : IXmlModule
{
    private XDocument? _document;
    private readonly List<AppleMusicTrack> _tracks = new();

    /// <summary>The tracks parsed by the last <see cref="ParseTracks"/> call.</summary>
    public IReadOnlyList<AppleMusicTrack> Tracks => _tracks;

    /// <inheritdoc/>
    public void Load(string filePath)
    {
        _document = XDocument.Load(filePath);
    }

    /// <inheritdoc/>
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
                // Prefer the stable Persistent ID; fall back to the session-scoped Track ID
                AppleMusicId = GetString(trackDict, "Persistent ID") ?? GetString(trackDict, "Track ID"),
                TrackTitle = GetString(trackDict, "Name"),
                TrackArtist = GetString(trackDict, "Artist"),
                AlbumTitle = GetString(trackDict, "Album"),
                AlbumArtist = GetString(trackDict, "Album Artist"),
                TrackNumber = GetInt(trackDict, "Track Number"),
                Genre = GetString(trackDict, "Genre"),
                Duration = GetInt(trackDict, "Total Time"),  // already in milliseconds
                Plays = GetInt(trackDict, "Play Count")
            };

            _tracks.Add(track);
        }

        return _tracks.Count;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Deduplication key: <c>AppleMusicId</c> if present; otherwise the
    /// (TrackTitle, TrackArtist, AlbumTitle) triple. Existing rows are never updated —
    /// run LibraryCleaner + MatchEngine afterward to propagate any metadata changes.
    /// </remarks>
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
    TrackNumber,
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
    $trackNumber,
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

            // Raw fields store the original un-cleaned values; MetadataCleaner will populate
            // the cleaned versions and metadata columns (MusicalKey, BPM, etc.) later.
            insertCommand.Parameters.Clear();
            insertCommand.Parameters.AddWithValue("$appleMusicId", (object?)track.AppleMusicId ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$trackTitle", (object?)track.TrackTitle ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$trackArtist", (object?)track.TrackArtist ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$trackTitleRaw", (object?)track.TrackTitle ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$trackArtistRaw", (object?)track.TrackArtist ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$albumTitle", (object?)track.AlbumTitle ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$albumArtist", (object?)track.AlbumArtist ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$trackNumber", (object?)track.TrackNumber ?? DBNull.Value);
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

    /// <summary>
    /// Converts a plist <c>&lt;dict&gt;</c> element into a string→XElement map.
    /// The plist dict format interleaves <c>&lt;key&gt;</c> and value elements;
    /// we step through them in pairs.
    /// </summary>
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

    /// <summary>Returns the string representation of any plist value element, or null if the key is absent.</summary>
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

    /// <summary>Returns the integer value of a plist element, or null if absent or non-numeric.</summary>
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
