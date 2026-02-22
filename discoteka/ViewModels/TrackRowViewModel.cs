namespace discoteka.ViewModels;

public sealed class TrackRowViewModel
{
    public TrackRowViewModel(
        long trackId,
        string title,
        string artist,
        string album,
        string durationText,
        int? durationSeconds,
        string genre,
        string formats,
        string playsText,
        int? plays,
        string? filePath)
    {
        TrackId = trackId;
        Title = title;
        Artist = artist;
        Album = album;
        DurationText = durationText;
        DurationSeconds = durationSeconds;
        Genre = genre;
        Formats = formats;
        PlaysText = playsText;
        Plays = plays;
        FilePath = filePath;
    }

    public long TrackId { get; }
    public string Title { get; }
    public string Artist { get; }
    public string Album { get; }
    public string DurationText { get; }
    public int? DurationSeconds { get; }
    public string Genre { get; }
    public string Formats { get; }
    public string PlaysText { get; }
    public int? Plays { get; }
    public string? FilePath { get; }
}
