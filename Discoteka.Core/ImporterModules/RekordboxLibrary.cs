using System.Xml.Linq;
using Discoteka.Core.Database;
using Discoteka.Core.Models;
using Microsoft.Data.Sqlite;

namespace Discoteka.Core.ImporterModules;

/// <summary>
/// Imports a Rekordbox XML library export into the <c>Rekordbox</c> table.
/// <para>
/// The Rekordbox format uses a <c>&lt;DJ_PLAYLISTS&gt;&lt;COLLECTION&gt;</c> element
/// containing <c>&lt;TRACK&gt;</c> elements whose data is stored as XML attributes
/// rather than child elements. Duration is exported in whole seconds and converted
/// to milliseconds on import to match the rest of the library schema.
/// </para>
/// </summary>
public class RekordboxLibrary : IXmlModule
{
    private XDocument? _document;
    private readonly List<RekordboxTrack> _tracks = new();

    /// <summary>The tracks parsed by the last <see cref="ParseTracks"/> call.</summary>
    public IReadOnlyList<RekordboxTrack> Tracks => _tracks;

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
                Duration = GetDurationMilliseconds(trackElement),  // converted from seconds
                BPM = GetDoubleAttribute(trackElement, "AverageBpm"),
                Key = GetAttribute(trackElement, "Tonality"),      // raw Rekordbox key (e.g. "3A")
                FilePath = GetAttribute(trackElement, "Location")
            };

            _tracks.Add(track);
        }

        return _tracks.Count;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Deduplication key: <c>TrackId</c> (Rekordbox's internal ID) if present; otherwise
    /// the (FilePath, TrackTitle, TrackArtist) triple. Existing rows are never updated.
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

    /// <summary>Returns the string value of an XML attribute, or null if absent.</summary>
    private static string? GetAttribute(XElement element, string name)
    {
        return element.Attribute(name)?.Value;
    }

    /// <summary>Returns the double value of an XML attribute, or null if absent or non-numeric.</summary>
    private static double? GetDoubleAttribute(XElement element, string name)
    {
        var value = GetAttribute(element, name);
        if (double.TryParse(value, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    /// <summary>
    /// Reads Rekordbox's <c>TotalTime</c> attribute (in whole seconds) and converts to milliseconds.
    /// Returns null if the attribute is absent or not a valid integer.
    /// </summary>
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
