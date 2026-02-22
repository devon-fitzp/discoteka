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
    public int? Duration { get; set; }
    public int? Plays { get; set; }
    public string? AppleMusicId { get; set; }
    public string? RekordboxId { get; set; }
    public string? FilePath { get; set; }
}

public class AppleMusicTrack
{
    public string? AppleMusicId { get; set; }
    public string? TrackTitle { get; set; }
    public string? TrackArtist { get; set; }
    public string? AlbumTitle { get; set; }
    public string? AlbumArtist { get; set; }
    public string? Genre { get; set; }
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
    public int? Duration { get; set; }
    public int? Bitrate { get; set; }
    public string? FileType { get; set; }
    public string? Path { get; set; }
}

public class RecentActivityEntry
{
    public long ActivityId { get; set; }
    public long TrackId { get; set; }
    public DateTime PlayedAtUtc { get; set; }
}
