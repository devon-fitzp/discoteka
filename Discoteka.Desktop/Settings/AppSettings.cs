namespace Discoteka.Desktop.Settings;

/// <summary>
/// User-configurable application settings, persisted to JSON alongside the database.
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// How many steps back the user can navigate with Previous when shuffle is on.
    /// A value of 5 means pressing Previous up to 5 times will replay recently shuffled tracks.
    /// </summary>
    public int ShuffleHistorySize { get; set; } = 5;
}
