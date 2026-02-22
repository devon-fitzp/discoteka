namespace discoteka_cli.Models;

public class Track
{
    public int TrackId { get; set; }
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? Genre { get; set; }
    public int? Duration { get; set; }
    public int? AppleMusic { get; set; } 
    public int? Rekordbox { get; set; } 
    public string? LocalFile { get; set; } 
}

public class TrackLibraryTrack
{
    public string? TrackId { get; set; }
    public string? TrackTitle { get; set; }
    public string? TrackArtist { get; set; }
    public string? AlbumTitle { get; set; }
    public string? AlbumArtist { get; set; }
    public string? Genre { get; set; }
    public int? TrackNumber { get; set; }
    public int? Duration { get; set; }
    public int? Plays { get; set; }
    public string? AppleMusicId { get; set; }
    public string? RekordboxId { get; set; }
    public string? FilePath { get; set; }
    public string? DjTags { get; set; }
}

public class AppleMusicTrack
{
    public string? AppleMusicId { get; set; }
    public string? TrackTitle { get; set; }
    public string? TrackArtist { get; set; }
    public string? AlbumTitle { get; set; }
    public string? AlbumArtist { get; set; }
    public string? Genre { get; set; }
    public int? TrackNumber { get; set; }
    public int? Duration { get; set; }
    public int? Plays { get; set; }
}

public class RekordboxTrack
{
    public string? TrackId { get; set; }
    public string? TrackTitle { get; set; }
    public string? TrackArtist { get; set; }
    public string? AlbumTitle { get; set; }
    public string? AlbumArtist { get; set; }
    public int? TrackNumber { get; set; }
    public int? Duration { get; set; }
    public double? BPM { get; set; }
    public string? Key { get; set; }
    public string? FilePath { get; set; }
}

public class FileLibraryTrack
{
    public long? FileId { get; set; }
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? AlbumArtist { get; set; }
    public int? TrackNumber { get; set; }
    public int? Duration { get; set; }
    public int? Bitrate { get; set; }
    public int? SampleRate { get; set; }
    public string? FileType { get; set; }
    public string? Path { get; set; }
}

public class RecentActivityEntry
{
    public long ActivityId { get; set; }
    public long TrackId { get; set; }
    public DateTime PlayedAtUtc { get; set; }
}

public sealed class MetadataFieldEntry
{
    public string Name { get; set; } = string.Empty;
    public string DeclaredType { get; set; } = string.Empty;
    public string? Value { get; set; }
    public bool IsPrimaryKey { get; set; }
}

public sealed class MetadataTabEntry
{
    public string Title { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public string KeyColumn { get; set; } = string.Empty;
    public string? KeyValue { get; set; }
    public List<MetadataFieldEntry> Fields { get; set; } = new();
}

public sealed class TrackMetadataSnapshot
{
    public long TrackId { get; set; }
    public List<MetadataTabEntry> Tabs { get; set; } = new();
}

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
