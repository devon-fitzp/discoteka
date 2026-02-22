namespace discoteka_cli.Database;

public static class DbPaths
{
    public const string DatabaseFileName = "discoteka.db";

    public static string GetDefaultDbPath()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "discoteka");

        return Path.Combine(root, DatabaseFileName);
    }

    public static string BuildConnectionString(string? dbPath = null)
    {
        var path = dbPath ?? GetDefaultDbPath();
        return $"Data Source={path}";
    }
}
