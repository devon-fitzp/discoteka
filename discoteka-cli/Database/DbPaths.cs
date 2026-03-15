namespace discoteka_cli.Database;

/// <summary>
/// Provides the canonical database file path and SQLite connection string for discoteka.
/// The default location is <c>%LOCALAPPDATA%/discoteka/discoteka.db</c> on Windows
/// and the equivalent XDG path on Linux/macOS.
/// </summary>
public static class DbPaths
{
    public const string DatabaseFileName = "discoteka.db";

    /// <summary>Returns the default absolute path to the SQLite database file.</summary>
    public static string GetDefaultDbPath()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "discoteka");

        return Path.Combine(root, DatabaseFileName);
    }

    /// <summary>
    /// Builds a Microsoft.Data.Sqlite connection string for the given path.
    /// If <paramref name="dbPath"/> is null, the default path is used.
    /// </summary>
    public static string BuildConnectionString(string? dbPath = null)
    {
        var path = dbPath ?? GetDefaultDbPath();
        return $"Data Source={path}";
    }
}
