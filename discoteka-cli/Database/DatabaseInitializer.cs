using Microsoft.Data.Sqlite;

namespace discoteka_cli.Database;

public static class DatabaseInitializer
{
    public const int CurrentDbVersion = 1;

    public static string Initialize(string? dbPath = null)
    {
        var path = dbPath ?? DbPaths.GetDefaultDbPath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var connection = new SqliteConnection(DbPaths.BuildConnectionString(path));
        connection.Open();

        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
CREATE TABLE IF NOT EXISTS discotekaMeta (
    DbVer INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS TrackLibrary (
    TrackId INTEGER PRIMARY KEY,
    TrackTitle TEXT,
    TrackArtist TEXT,
    TrackTitleRaw TEXT,
    TrackArtistRaw TEXT,
    AlbumTitle TEXT,
    AlbumArtist TEXT,
    Genre TEXT,
    Duration INTEGER,
    Plays INTEGER,
    AppleMusicId TEXT,
    RekordboxId TEXT,
    FilePath TEXT,
    MusicalKey TEXT,
    BPM REAL,
    Features TEXT,
    DjTags TEXT,
    CleanConfidence REAL,
    CleanLog TEXT
);

CREATE TABLE IF NOT EXISTS AppleLibrary (
    AppleMusicId TEXT PRIMARY KEY,
    TrackTitle TEXT,
    TrackArtist TEXT,
    TrackTitleRaw TEXT,
    TrackArtistRaw TEXT,
    AlbumTitle TEXT,
    AlbumArtist TEXT,
    Genre TEXT,
    Duration INTEGER,
    Plays INTEGER,
    MusicalKey TEXT,
    BPM REAL,
    Features TEXT,
    DjTags TEXT,
    CleanConfidence REAL,
    CleanLog TEXT
);

CREATE TABLE IF NOT EXISTS Rekordbox (
    TrackId TEXT PRIMARY KEY,
    TrackTitle TEXT,
    TrackArtist TEXT,
    TrackTitleRaw TEXT,
    TrackArtistRaw TEXT,
    AlbumTitle TEXT,
    AlbumArtist TEXT,
    Duration INTEGER,
    BPM REAL,
    Key TEXT,
    FilePath TEXT,
    MusicalKey TEXT,
    Features TEXT,
    DjTags TEXT,
    CleanConfidence REAL,
    CleanLog TEXT
);

CREATE TABLE IF NOT EXISTS FileLibrary (
    FileId INTEGER PRIMARY KEY,
    Title TEXT,
    Artist TEXT,
    TitleRaw TEXT,
    ArtistRaw TEXT,
    Album TEXT,
    AlbumArtist TEXT,
    Duration INTEGER,
    Bitrate INTEGER,
    FileType TEXT,
    Path TEXT,
    MusicalKey TEXT,
    BPM REAL,
    Features TEXT,
    DjTags TEXT,
    CleanConfidence REAL,
    CleanLog TEXT
);

CREATE TABLE IF NOT EXISTS TrackToFile (
    TrackId INTEGER,
    FileId INTEGER,

    PRIMARY KEY (TrackId, FileId),
    FOREIGN KEY (TrackId) REFERENCES TrackLibrary(TrackId),
    FOREIGN KEY (FileId) REFERENCES FileLibrary(FileId)
);

CREATE TABLE IF NOT EXISTS TrackToApple (
    TrackId INTEGER,
    AppleMusicId TEXT,

    PRIMARY KEY (TrackId, AppleMusicId),
    FOREIGN KEY (TrackId) REFERENCES TrackLibrary(TrackId),
    FOREIGN KEY (AppleMusicId) REFERENCES AppleLibrary(AppleMusicId)
);

CREATE TABLE IF NOT EXISTS TrackToRekordbox (
    TrackId INTEGER,
    RekordboxId TEXT,

    PRIMARY KEY (TrackId, RekordboxId),
    FOREIGN KEY (TrackId) REFERENCES TrackLibrary(TrackId),
    FOREIGN KEY (RekordboxId) REFERENCES Rekordbox(TrackId)
);

CREATE TABLE IF NOT EXISTS RecentActivity (
    ActivityId INTEGER PRIMARY KEY AUTOINCREMENT,
    TrackId INTEGER NOT NULL,
    PlayedAtUtc TEXT NOT NULL,
    FOREIGN KEY (TrackId) REFERENCES TrackLibrary(TrackId)
);

CREATE UNIQUE INDEX IF NOT EXISTS IX_TrackLibrary_TrackId ON TrackLibrary(TrackId);
CREATE UNIQUE INDEX IF NOT EXISTS IX_AppleLibrary_AppleMusicId ON AppleLibrary(AppleMusicId);
CREATE UNIQUE INDEX IF NOT EXISTS IX_Rekordbox_TrackId ON Rekordbox(TrackId);
CREATE UNIQUE INDEX IF NOT EXISTS IX_FileLibrary_FileId ON FileLibrary(FileId);
CREATE INDEX IF NOT EXISTS IX_RecentActivity_TrackId_PlayedAtUtc ON RecentActivity(TrackId, PlayedAtUtc DESC);";
        command.ExecuteNonQuery();

        command.CommandText = "SELECT COUNT(1) FROM discotekaMeta;";
        var metaCount = Convert.ToInt32(command.ExecuteScalar());
        if (metaCount == 0)
        {
            command.CommandText = "INSERT INTO discotekaMeta (DbVer) VALUES ($ver);";
            command.Parameters.AddWithValue("$ver", CurrentDbVersion);
            command.ExecuteNonQuery();
            command.Parameters.Clear();
        }

        transaction.Commit();
        Console.WriteLine($"[Database] Using DB file at: {Path.GetFullPath(path)}");
        return path;
    }
}
