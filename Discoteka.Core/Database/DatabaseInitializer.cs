using Microsoft.Data.Sqlite;

namespace Discoteka.Core.Database;

/// <summary>
/// Creates and migrates the Discoteka.Desktop SQLite schema on first run and after updates.
/// <para>
/// Schema changes are always additive: new columns are added via <see cref="EnsureColumnExists"/>,
/// which silently no-ops if the column already exists. Tables and indices are created with
/// <c>IF NOT EXISTS</c> guards, so this can be called safely on every startup.
/// </para>
/// </summary>
public static class DatabaseInitializer
{
    /// <summary>
    /// Monotonically increasing integer written into <c>discotekaMeta.DbVer</c> on first run.
    /// Not yet used for conditional migrations — reserved for future schema versioning.
    /// </summary>
    public const int CurrentDbVersion = 1;

    /// <summary>
    /// Ensures the database file exists, creates all tables and indices, runs any pending
    /// additive column migrations, and returns the resolved absolute path to the DB file.
    /// </summary>
    /// <param name="dbPath">
    /// Optional override for the database path.
    /// Defaults to <see cref="DbPaths.GetDefaultDbPath"/>.
    /// </param>
    /// <returns>The absolute path to the database file that was initialized.</returns>
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
    TrackNumber INTEGER,
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
    TrackNumber INTEGER,
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
    TrackNumber INTEGER,
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
    TrackNumber INTEGER,
    Duration INTEGER,
    Bitrate INTEGER,
    SampleRate INTEGER,
    FileType TEXT,
    Path TEXT,
    MusicalKey TEXT,
    BPM REAL,
    Features TEXT,
    DjTags TEXT,
    CleanConfidence REAL,
    CleanLog TEXT
);

-- M:M join tables linking canonical TrackLibrary rows to source records
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

-- Playback history; a row is inserted whenever a track crosses 50% of its duration
CREATE TABLE IF NOT EXISTS RecentActivity (
    ActivityId INTEGER PRIMARY KEY AUTOINCREMENT,
    TrackId INTEGER NOT NULL,
    PlayedAtUtc TEXT NOT NULL,
    FOREIGN KEY (TrackId) REFERENCES TrackLibrary(TrackId)
);

-- Pre-built artist/album index tables, populated by TrackLibraryIndexBuilder.Rebuild().
-- These are wiped and rebuilt from scratch on each import or match pass.
CREATE TABLE IF NOT EXISTS TrackArtists (
    ArtistId INTEGER PRIMARY KEY AUTOINCREMENT,
    ArtistName TEXT NOT NULL,
    ArtistKey TEXT NOT NULL UNIQUE,
    AlbumCount INTEGER NOT NULL DEFAULT 0,
    TrackCount INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS TrackAlbums (
    AlbumId INTEGER PRIMARY KEY AUTOINCREMENT,
    AlbumTitle TEXT NOT NULL,
    AlbumArtistName TEXT NOT NULL,
    AlbumKey TEXT NOT NULL UNIQUE,
    ReleaseYear INTEGER,
    TrackCount INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS ArtistToAlbum (
    ArtistId INTEGER NOT NULL,
    AlbumId INTEGER NOT NULL,
    PRIMARY KEY (ArtistId, AlbumId),
    FOREIGN KEY (ArtistId) REFERENCES TrackArtists(ArtistId),
    FOREIGN KEY (AlbumId) REFERENCES TrackAlbums(AlbumId)
);

CREATE TABLE IF NOT EXISTS AlbumToTrack (
    AlbumId INTEGER NOT NULL,
    TrackId INTEGER NOT NULL,
    SortOrder INTEGER NOT NULL DEFAULT 0,
    TrackNumber INTEGER,
    PRIMARY KEY (AlbumId, TrackId),
    FOREIGN KEY (AlbumId) REFERENCES TrackAlbums(AlbumId),
    FOREIGN KEY (TrackId) REFERENCES TrackLibrary(TrackId)
);

CREATE UNIQUE INDEX IF NOT EXISTS IX_TrackLibrary_TrackId ON TrackLibrary(TrackId);
CREATE UNIQUE INDEX IF NOT EXISTS IX_AppleLibrary_AppleMusicId ON AppleLibrary(AppleMusicId);
CREATE UNIQUE INDEX IF NOT EXISTS IX_Rekordbox_TrackId ON Rekordbox(TrackId);
CREATE UNIQUE INDEX IF NOT EXISTS IX_FileLibrary_FileId ON FileLibrary(FileId);
CREATE INDEX IF NOT EXISTS IX_RecentActivity_TrackId_PlayedAtUtc ON RecentActivity(TrackId, PlayedAtUtc DESC);

CREATE TABLE IF NOT EXISTS DynamicPlaylists (
    Id        INTEGER PRIMARY KEY AUTOINCREMENT,
    Name      TEXT    NOT NULL,
    RuleField TEXT    NOT NULL,
    Operator  TEXT    NOT NULL,
    ValueA    INTEGER NOT NULL,
    ValueB    INTEGER
);";
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

        // Additive column migrations: columns added after the initial schema release.
        // EnsureColumnExists is a no-op if the column already exists.
        EnsureColumnExists(command, "TrackLibrary", "TrackNumber", "INTEGER");
        EnsureColumnExists(command, "AppleLibrary", "TrackNumber", "INTEGER");
        EnsureColumnExists(command, "Rekordbox", "TrackNumber", "INTEGER");
        EnsureColumnExists(command, "FileLibrary", "TrackNumber", "INTEGER");
        EnsureColumnExists(command, "FileLibrary", "SampleRate", "INTEGER");
        EnsureColumnExists(command, "TrackArtists", "ArtistKey", "TEXT");
        EnsureColumnExists(command, "TrackArtists", "AlbumCount", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnExists(command, "TrackArtists", "TrackCount", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnExists(command, "TrackAlbums", "AlbumArtistName", "TEXT");
        EnsureColumnExists(command, "TrackAlbums", "AlbumKey", "TEXT");
        EnsureColumnExists(command, "TrackAlbums", "ReleaseYear", "INTEGER");
        EnsureColumnExists(command, "TrackAlbums", "TrackCount", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnExists(command, "AlbumToTrack", "SortOrder", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnExists(command, "AlbumToTrack", "TrackNumber", "INTEGER");

        command.Parameters.Clear();
        command.CommandText = @"
CREATE UNIQUE INDEX IF NOT EXISTS IX_TrackArtists_ArtistKey ON TrackArtists(ArtistKey);
CREATE UNIQUE INDEX IF NOT EXISTS IX_TrackAlbums_AlbumKey ON TrackAlbums(AlbumKey);
CREATE INDEX IF NOT EXISTS IX_ArtistToAlbum_ArtistId ON ArtistToAlbum(ArtistId);
CREATE INDEX IF NOT EXISTS IX_ArtistToAlbum_AlbumId ON ArtistToAlbum(AlbumId);
CREATE INDEX IF NOT EXISTS IX_AlbumToTrack_AlbumId_SortOrder ON AlbumToTrack(AlbumId, SortOrder, TrackNumber, TrackId);
CREATE INDEX IF NOT EXISTS IX_AlbumToTrack_TrackId ON AlbumToTrack(TrackId);";
        command.ExecuteNonQuery();

        transaction.Commit();
        Console.WriteLine($"[Database] Using DB file at: {Path.GetFullPath(path)}");
        return path;
    }

    /// <summary>
    /// Issues <c>ALTER TABLE … ADD COLUMN</c> and silently swallows the
    /// <c>duplicate column name</c> error if the column already exists.
    /// This is the only safe way to add columns to an existing SQLite table without
    /// a full schema migration.
    /// <para>
    /// Note: all three parameters are hardcoded at every call site — they are never
    /// derived from user input — so interpolating them directly into DDL is safe here.
    /// </para>
    /// </summary>
    private static void EnsureColumnExists(SqliteCommand command, string tableName, string columnName, string columnType)
    {
        try
        {
            command.Parameters.Clear();
            command.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnType};";
            command.ExecuteNonQuery();
            Console.WriteLine($"[Database] Added column {tableName}.{columnName} ({columnType}).");
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {
            // Column already exists — nothing to do.
        }
    }
}
