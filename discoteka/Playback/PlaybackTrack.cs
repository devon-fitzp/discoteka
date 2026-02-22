namespace discoteka.Playback;

public sealed record PlaybackTrack(
    long TrackId,
    string Title,
    string? Artist,
    string? FilePath
);
