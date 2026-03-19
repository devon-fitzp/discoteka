using Discoteka.Core.Database;
using Discoteka.Core.Models;
using Microsoft.Data.Sqlite;
using TagLib;

namespace Discoteka.Core.ImporterModules;

/// <summary>
/// Scans a directory tree for audio files and imports their tag metadata
/// into the <c>FileLibrary</c> table via TagLibSharp.
/// <para>
/// Supported file types: .mp3, .m4a, .flac, .wav, .m4p.
/// macOS resource fork files (names starting with <c>._</c>) are silently skipped.
/// Files that TagLibSharp cannot parse are also silently skipped — this is intentional
/// to handle corrupted or partially-written files without aborting a large scan.
/// </para>
/// </summary>
public class FileLibraryScanner
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3",
        ".m4a",
        ".flac",
        ".wav",
        ".m4p"
    };

    /// <summary>
    /// Enumerates all supported audio files under <paramref name="rootPath"/>,
    /// reads their tags, and inserts any new entries into <c>FileLibrary</c>.
    /// Existing rows (matched by file path) are skipped.
    /// </summary>
    /// <param name="rootPath">Root directory to scan recursively.</param>
    /// <param name="dbPath">Optional database path override.</param>
    /// <returns>Number of new rows inserted.</returns>
    /// <exception cref="DirectoryNotFoundException">Thrown if <paramref name="rootPath"/> does not exist.</exception>
    public int ScanAndImport(string rootPath, string? dbPath = null)
    {
        if (!Directory.Exists(rootPath))
        {
            throw new DirectoryNotFoundException($"Directory not found: {rootPath}");
        }

        var files = EnumerateAudioFiles(rootPath).ToList();
        var total = files.Count;
        if (total == 0)
        {
            return 0;
        }

        var path = DatabaseInitializer.Initialize(dbPath);
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();

        using var transaction = connection.BeginTransaction();
        using var existsCommand = connection.CreateCommand();
        existsCommand.Transaction = transaction;
        existsCommand.CommandText = "SELECT 1 FROM FileLibrary WHERE Path = $path LIMIT 1;";

        using var insertCommand = connection.CreateCommand();
        insertCommand.Transaction = transaction;
        insertCommand.CommandText = @"
INSERT INTO FileLibrary (
    FileId,
    Title,
    Artist,
    TitleRaw,
    ArtistRaw,
    Album,
    AlbumArtist,
    TrackNumber,
    Duration,
    Bitrate,
    SampleRate,
    FileType,
    Path,
    MusicalKey,
    BPM,
    Features,
    DjTags,
    CleanConfidence,
    CleanLog
)
VALUES (
    $fileId,
    $title,
    $artist,
    $titleRaw,
    $artistRaw,
    $album,
    $albumArtist,
    $trackNumber,
    $duration,
    $bitrate,
    $sampleRate,
    $fileType,
    $path,
    $musicalKey,
    $bpm,
    $features,
    $djTags,
    $cleanConfidence,
    $cleanLog
);";

        // Compute the next FileId manually rather than relying on AUTOINCREMENT,
        // so we can reference the ID within the same transaction if needed later.
        using var maxIdCommand = connection.CreateCommand();
        maxIdCommand.Transaction = transaction;
        maxIdCommand.CommandText = "SELECT COALESCE(MAX(FileId), 0) FROM FileLibrary;";
        var nextId = Convert.ToInt64(maxIdCommand.ExecuteScalar()) + 1;

        var inserted = 0;
        for (var i = 0; i < total; i++)
        {
            var filePath = files[i];
            Console.WriteLine($"Parsing file {i + 1} of {total}: {filePath}");

            existsCommand.Parameters.Clear();
            existsCommand.Parameters.AddWithValue("$path", filePath);
            var exists = existsCommand.ExecuteScalar();
            if (exists != null)
            {
                continue;
            }

            FileLibraryTrack? track = null;
            try
            {
                using var file = TagLib.File.Create(filePath);
                var tag = file.Tag;
                var props = file.Properties;

                track = new FileLibraryTrack
                {
                    FileId = nextId++,
                    Title = string.IsNullOrWhiteSpace(tag.Title) ? null : tag.Title,
                    Artist = tag.Performers.FirstOrDefault(),
                    Album = string.IsNullOrWhiteSpace(tag.Album) ? null : tag.Album,
                    AlbumArtist = tag.AlbumArtists.FirstOrDefault(),
                    TrackNumber = tag.Track > 0 ? (int)tag.Track : null,
                    Duration = (int)props.Duration.TotalMilliseconds,
                    Bitrate = props.AudioBitrate > 0 ? props.AudioBitrate : null,
                    SampleRate = props.AudioSampleRate > 0 ? props.AudioSampleRate : null,
                    FileType = Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant(),
                    Path = filePath
                };
            }
            catch
            {
                // TagLibSharp throws for unsupported/corrupted files — skip silently.
                // TODO: accumulate failed paths and surface them in the job result.
                continue;
            }

            insertCommand.Parameters.Clear();
            insertCommand.Parameters.AddWithValue("$fileId", (object?)track.FileId ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$title", (object?)track.Title ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$artist", (object?)track.Artist ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$titleRaw", (object?)track.Title ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$artistRaw", (object?)track.Artist ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$album", (object?)track.Album ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$albumArtist", (object?)track.AlbumArtist ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$trackNumber", (object?)track.TrackNumber ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$duration", (object?)track.Duration ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$bitrate", (object?)track.Bitrate ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$sampleRate", (object?)track.SampleRate ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$fileType", (object?)track.FileType ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$path", (object?)track.Path ?? DBNull.Value);
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
    /// Recursively enumerates all files whose extension is in <see cref="SupportedExtensions"/>,
    /// excluding macOS resource fork files (names starting with <c>._</c>).
    /// </summary>
    private static IEnumerable<string> EnumerateAudioFiles(string rootPath)
    {
        return Directory.EnumerateFiles(rootPath, "*.*", SearchOption.AllDirectories)
            .Where(path =>
            {
                var fileName = Path.GetFileName(path);
                if (fileName.StartsWith("._", StringComparison.Ordinal))
                {
                    return false;
                }

                return SupportedExtensions.Contains(Path.GetExtension(path));
            });
    }
}
