namespace Discoteka.Core.Models;

/// <summary>
/// Lightweight generic track record used in early prototyping.
/// Prefer <see cref="TrackLibraryTrack"/> for all current UI and query work.
/// </summary>
public class Track
{
    public int TrackId { get; set; }
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? Genre { get; set; }
    public int? Duration { get; set; }
    /// <summary>Non-null when this track has a linked Apple Music entry.</summary>
    public int? AppleMusic { get; set; }
    /// <summary>Non-null when this track has a linked Rekordbox entry.</summary>
    public int? Rekordbox { get; set; }
    /// <summary>Absolute path to the local audio file, if one has been matched.</summary>
    public string? LocalFile { get; set; }
}

/// <summary>
/// The canonical de-duplicated track record stored in <c>TrackLibrary</c>.
/// This is the primary model used by the GUI and all read queries.
/// <para>
/// Raw (un-cleaned) title/artist are preserved in the source tables
/// (AppleLibrary, Rekordbox, FileLibrary). The cleaned values live here.
/// </para>
/// </summary>
public class TrackLibraryTrack
{
    public string? TrackId { get; set; }
    public string? TrackTitle { get; set; }
    public string? TrackArtist { get; set; }
    public string? AlbumTitle { get; set; }
    public string? AlbumArtist { get; set; }
    public string? Genre { get; set; }
    public int? TrackNumber { get; set; }
    /// <summary>Duration in milliseconds.</summary>
    public int? Duration { get; set; }
    public int? Plays { get; set; }
    /// <summary>Foreign key into <c>AppleLibrary</c>. Null if no Apple Music match.</summary>
    public string? AppleMusicId { get; set; }
    /// <summary>Foreign key into <c>Rekordbox</c>. Null if no Rekordbox match.</summary>
    public string? RekordboxId { get; set; }
    /// <summary>Absolute path to the best-matched local file, or null if none.</summary>
    public string? FilePath { get; set; }
    /// <summary>JSON-serialized DJ tag array produced by <c>MetadataCleaner</c> (e.g. remix, radio edit).</summary>
    public string? DjTags { get; set; }
}

/// <summary>
/// A track record imported from an Apple Music / iTunes XML library export.
/// Duration is stored in milliseconds (Apple Music exports milliseconds natively).
/// </summary>
public class AppleMusicTrack
{
    /// <summary>Apple's "Persistent ID" (preferred) or "Track ID" as fallback.</summary>
    public string? AppleMusicId { get; set; }
    public string? TrackTitle { get; set; }
    public string? TrackArtist { get; set; }
    public string? AlbumTitle { get; set; }
    public string? AlbumArtist { get; set; }
    public string? Genre { get; set; }
    public int? TrackNumber { get; set; }
    /// <summary>Duration in milliseconds (from Apple's "Total Time" field).</summary>
    public int? Duration { get; set; }
    /// <summary>Lifetime play count from Apple Music.</summary>
    public int? Plays { get; set; }
}

/// <summary>
/// A track record imported from a Rekordbox XML library export.
/// Duration is stored in milliseconds; Rekordbox exports <c>TotalTime</c> in seconds,
/// so importers multiply by 1000 on read.
/// </summary>
public class RekordboxTrack
{
    /// <summary>Rekordbox's internal <c>TrackID</c> attribute.</summary>
    public string? TrackId { get; set; }
    public string? TrackTitle { get; set; }
    public string? TrackArtist { get; set; }
    public string? AlbumTitle { get; set; }
    public string? AlbumArtist { get; set; }
    public int? TrackNumber { get; set; }
    /// <summary>Duration in milliseconds (converted from Rekordbox's seconds).</summary>
    public int? Duration { get; set; }
    public double? BPM { get; set; }
    /// <summary>Raw Rekordbox tonality string (e.g. "3A", "Abm"). Normalized to Camelot by MetadataCleaner.</summary>
    public string? Key { get; set; }
    /// <summary>Absolute local file path as stored in the Rekordbox export.</summary>
    public string? FilePath { get; set; }
}

/// <summary>
/// A track record discovered and tagged during a filesystem scan via <c>FileLibraryScanner</c>.
/// Audio metadata is read by TagLibSharp.
/// </summary>
public class FileLibraryTrack
{
    public long? FileId { get; set; }
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? AlbumArtist { get; set; }
    public int? TrackNumber { get; set; }
    /// <summary>Duration in milliseconds.</summary>
    public int? Duration { get; set; }
    /// <summary>Audio bitrate in kbps, or null if unavailable.</summary>
    public int? Bitrate { get; set; }
    /// <summary>Sample rate in Hz, or null if unavailable.</summary>
    public int? SampleRate { get; set; }
    /// <summary>File extension without the dot, uppercased (e.g. "MP3", "FLAC").</summary>
    public string? FileType { get; set; }
    /// <summary>Absolute path to the file on disk.</summary>
    public string? Path { get; set; }
}

/// <summary>A single play event recorded in <c>RecentActivity</c>.</summary>
public class RecentActivityEntry
{
    public long ActivityId { get; set; }
    public long TrackId { get; set; }
    public DateTime PlayedAtUtc { get; set; }
}

/// <summary>
/// Represents one editable field within a <see cref="MetadataTabEntry"/>,
/// as introspected from the database schema via <c>PRAGMA table_info</c>.
/// </summary>
public sealed class MetadataFieldEntry
{
    public string Name { get; set; } = string.Empty;
    /// <summary>SQLite declared type (e.g. "TEXT", "INTEGER", "REAL").</summary>
    public string DeclaredType { get; set; } = string.Empty;
    /// <summary>Current field value as a string, or null for SQL NULL.</summary>
    public string? Value { get; set; }
    /// <summary>True for primary key columns, which are shown read-only in the UI.</summary>
    public bool IsPrimaryKey { get; set; }
}

/// <summary>
/// Represents one database table's worth of editable metadata for a track,
/// displayed as a tab in the Track Info dialog.
/// </summary>
public sealed class MetadataTabEntry
{
    /// <summary>Human-readable tab label shown in the UI (e.g. "Main", "Apple Music", "MP3 - filename.mp3").</summary>
    public string Title { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    /// <summary>Name of the primary key column used in the UPDATE WHERE clause.</summary>
    public string KeyColumn { get; set; } = string.Empty;
    /// <summary>Value of the primary key for this specific row.</summary>
    public string? KeyValue { get; set; }
    public List<MetadataFieldEntry> Fields { get; set; } = new();
}

/// <summary>
/// All metadata tabs for a single track, loaded by
/// <see cref="Discoteka.Core.Database.ITrackLibraryRepository.GetTrackMetadataSnapshotAsync"/>.
/// Contains one tab for TrackLibrary plus any linked Apple Music, Rekordbox, and File records.
/// </summary>
public sealed class TrackMetadataSnapshot
{
    public long TrackId { get; set; }
    public List<MetadataTabEntry> Tabs { get; set; } = new();
}

/// <summary>
/// A flattened row from the pre-built artist/album index, joining
/// <c>TrackArtists</c>, <c>TrackAlbums</c>, and <c>ArtistToAlbum</c>.
/// Used to populate the Artists view without re-scanning TrackLibrary at runtime.
/// </summary>
public sealed class IndexedArtistAlbumEntry
{
    public long ArtistId { get; set; }
    public string ArtistName { get; set; } = string.Empty;
    public long AlbumId { get; set; }
    public string AlbumTitle { get; set; } = string.Empty;
    public string AlbumArtistName { get; set; } = string.Empty;
    public int AlbumTrackCount { get; set; }
    public int? ReleaseYear { get; set; }
}
