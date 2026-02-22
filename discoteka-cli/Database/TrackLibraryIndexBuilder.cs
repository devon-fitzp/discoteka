using Microsoft.Data.Sqlite;

namespace discoteka_cli.Database;

public static class TrackLibraryIndexBuilder
{
    public static void Rebuild(string? dbPath = null)
    {
        var resolvedDbPath = DatabaseInitializer.Initialize(dbPath);
        Console.WriteLine("[Index] Rebuilding artist/album index...");

        using var connection = new SqliteConnection(DbPaths.BuildConnectionString(resolvedDbPath));
        connection.Open();
        using var transaction = connection.BeginTransaction();

        var rows = new List<TrackRow>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = @"
SELECT
    t.TrackId,
    COALESCE(NULLIF(TRIM(t.TrackArtist), ''), 'Unknown Artist') AS TrackArtist,
    COALESCE(NULLIF(TRIM(t.AlbumTitle), ''), 'Unknown Album') AS AlbumTitle,
    COALESCE(NULLIF(TRIM(t.AlbumArtist), ''), NULLIF(TRIM(t.TrackArtist), ''), 'Unknown Artist') AS AlbumArtist,
    COALESCE(t.TrackNumber, a.TrackNumber, f.TrackNumber) AS TrackNumber,
    COALESCE(NULLIF(TRIM(t.TrackTitle), ''), 'Untitled') AS TrackTitle
FROM TrackLibrary t
LEFT JOIN AppleLibrary a ON a.AppleMusicId = t.AppleMusicId
LEFT JOIN FileLibrary f ON f.Path = t.FilePath
ORDER BY TrackArtist COLLATE NOCASE, AlbumTitle COLLATE NOCASE, COALESCE(t.TrackNumber, a.TrackNumber, f.TrackNumber, 2147483647), TrackTitle COLLATE NOCASE, t.TrackId;";

            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new TrackRow(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    reader.GetString(5)));
            }
        }

        ExecuteNonQuery(connection, transaction, @"
DELETE FROM ArtistToAlbum;
DELETE FROM AlbumToTrack;
DELETE FROM TrackArtists;
DELETE FROM TrackAlbums;");

        var artistsByKey = new Dictionary<string, ArtistAccumulator>(StringComparer.Ordinal);
        var albumsByKey = new Dictionary<string, AlbumAccumulator>(StringComparer.Ordinal);
        var nextArtistId = 1L;
        var nextAlbumId = 1L;

        foreach (var row in rows)
        {
            var artistKey = NormalizeKey(row.TrackArtist);
            if (!artistsByKey.TryGetValue(artistKey, out var artist))
            {
                artist = new ArtistAccumulator(nextArtistId++, row.TrackArtist, artistKey);
                artistsByKey.Add(artistKey, artist);
            }

            var albumKey = $"{NormalizeKey(row.AlbumArtist)}|{NormalizeKey(row.AlbumTitle)}";
            if (!albumsByKey.TryGetValue(albumKey, out var album))
            {
                album = new AlbumAccumulator(nextAlbumId++, row.AlbumTitle, row.AlbumArtist, albumKey);
                albumsByKey.Add(albumKey, album);
            }

            artist.TrackCount++;
            if (artist.AlbumIds.Add(album.AlbumId))
            {
                artist.AlbumCount++;
            }

            album.TrackCount++;
            album.TrackEntries.Add(new AlbumTrackEntry(row.TrackId, row.TrackNumber, album.TrackEntries.Count));
            album.ArtistIds.Add(artist.ArtistId);
        }

        using (var insertArtist = connection.CreateCommand())
        {
            insertArtist.Transaction = transaction;
            insertArtist.CommandText = @"
INSERT INTO TrackArtists (ArtistId, ArtistName, ArtistKey, AlbumCount, TrackCount)
VALUES ($artistId, $artistName, $artistKey, $albumCount, $trackCount);";
            var pArtistId = insertArtist.CreateParameter();
            pArtistId.ParameterName = "$artistId";
            insertArtist.Parameters.Add(pArtistId);
            var pArtistName = insertArtist.CreateParameter();
            pArtistName.ParameterName = "$artistName";
            insertArtist.Parameters.Add(pArtistName);
            var pArtistKey = insertArtist.CreateParameter();
            pArtistKey.ParameterName = "$artistKey";
            insertArtist.Parameters.Add(pArtistKey);
            var pAlbumCount = insertArtist.CreateParameter();
            pAlbumCount.ParameterName = "$albumCount";
            insertArtist.Parameters.Add(pAlbumCount);
            var pTrackCount = insertArtist.CreateParameter();
            pTrackCount.ParameterName = "$trackCount";
            insertArtist.Parameters.Add(pTrackCount);

            foreach (var artist in artistsByKey.Values.OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                pArtistId.Value = artist.ArtistId;
                pArtistName.Value = artist.DisplayName;
                pArtistKey.Value = artist.ArtistKey;
                pAlbumCount.Value = artist.AlbumCount;
                pTrackCount.Value = artist.TrackCount;
                insertArtist.ExecuteNonQuery();
            }
        }

        using (var insertAlbum = connection.CreateCommand())
        {
            insertAlbum.Transaction = transaction;
            insertAlbum.CommandText = @"
INSERT INTO TrackAlbums (AlbumId, AlbumTitle, AlbumArtistName, AlbumKey, ReleaseYear, TrackCount)
VALUES ($albumId, $albumTitle, $albumArtistName, $albumKey, NULL, $trackCount);";
            var pAlbumId = insertAlbum.CreateParameter();
            pAlbumId.ParameterName = "$albumId";
            insertAlbum.Parameters.Add(pAlbumId);
            var pAlbumTitle = insertAlbum.CreateParameter();
            pAlbumTitle.ParameterName = "$albumTitle";
            insertAlbum.Parameters.Add(pAlbumTitle);
            var pAlbumArtistName = insertAlbum.CreateParameter();
            pAlbumArtistName.ParameterName = "$albumArtistName";
            insertAlbum.Parameters.Add(pAlbumArtistName);
            var pAlbumKey = insertAlbum.CreateParameter();
            pAlbumKey.ParameterName = "$albumKey";
            insertAlbum.Parameters.Add(pAlbumKey);
            var pTrackCount = insertAlbum.CreateParameter();
            pTrackCount.ParameterName = "$trackCount";
            insertAlbum.Parameters.Add(pTrackCount);

            foreach (var album in albumsByKey.Values.OrderBy(a => a.AlbumArtistName, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.AlbumTitle, StringComparer.OrdinalIgnoreCase))
            {
                pAlbumId.Value = album.AlbumId;
                pAlbumTitle.Value = album.AlbumTitle;
                pAlbumArtistName.Value = album.AlbumArtistName;
                pAlbumKey.Value = album.AlbumKey;
                pTrackCount.Value = album.TrackCount;
                insertAlbum.ExecuteNonQuery();
            }
        }

        using (var insertArtistToAlbum = connection.CreateCommand())
        {
            insertArtistToAlbum.Transaction = transaction;
            insertArtistToAlbum.CommandText = @"
INSERT INTO ArtistToAlbum (ArtistId, AlbumId)
VALUES ($artistId, $albumId);";
            var pArtistId = insertArtistToAlbum.CreateParameter();
            pArtistId.ParameterName = "$artistId";
            insertArtistToAlbum.Parameters.Add(pArtistId);
            var pAlbumId = insertArtistToAlbum.CreateParameter();
            pAlbumId.ParameterName = "$albumId";
            insertArtistToAlbum.Parameters.Add(pAlbumId);

            foreach (var album in albumsByKey.Values)
            {
                foreach (var artistId in album.ArtistIds)
                {
                    pArtistId.Value = artistId;
                    pAlbumId.Value = album.AlbumId;
                    insertArtistToAlbum.ExecuteNonQuery();
                }
            }
        }

        using (var insertAlbumToTrack = connection.CreateCommand())
        {
            insertAlbumToTrack.Transaction = transaction;
            insertAlbumToTrack.CommandText = @"
INSERT INTO AlbumToTrack (AlbumId, TrackId, SortOrder, TrackNumber)
VALUES ($albumId, $trackId, $sortOrder, $trackNumber);";
            var pAlbumId = insertAlbumToTrack.CreateParameter();
            pAlbumId.ParameterName = "$albumId";
            insertAlbumToTrack.Parameters.Add(pAlbumId);
            var pTrackId = insertAlbumToTrack.CreateParameter();
            pTrackId.ParameterName = "$trackId";
            insertAlbumToTrack.Parameters.Add(pTrackId);
            var pSortOrder = insertAlbumToTrack.CreateParameter();
            pSortOrder.ParameterName = "$sortOrder";
            insertAlbumToTrack.Parameters.Add(pSortOrder);
            var pTrackNumber = insertAlbumToTrack.CreateParameter();
            pTrackNumber.ParameterName = "$trackNumber";
            insertAlbumToTrack.Parameters.Add(pTrackNumber);

            foreach (var album in albumsByKey.Values)
            {
                foreach (var track in album.TrackEntries)
                {
                    pAlbumId.Value = album.AlbumId;
                    pTrackId.Value = track.TrackId;
                    pSortOrder.Value = track.SortOrder;
                    pTrackNumber.Value = track.TrackNumber.HasValue ? track.TrackNumber.Value : DBNull.Value;
                    insertAlbumToTrack.ExecuteNonQuery();
                }
            }
        }

        transaction.Commit();
        Console.WriteLine($"[Index] Rebuild complete. Artists={artistsByKey.Count}, Albums={albumsByKey.Count}, Tracks={rows.Count}");
    }

    private static void ExecuteNonQuery(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string NormalizeKey(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Join(' ', value.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private sealed record TrackRow(long TrackId, string TrackArtist, string AlbumTitle, string AlbumArtist, int? TrackNumber, string TrackTitle);

    private sealed class ArtistAccumulator
    {
        public ArtistAccumulator(long artistId, string displayName, string artistKey)
        {
            ArtistId = artistId;
            DisplayName = displayName;
            ArtistKey = artistKey;
        }

        public long ArtistId { get; }
        public string DisplayName { get; }
        public string ArtistKey { get; }
        public int AlbumCount { get; set; }
        public int TrackCount { get; set; }
        public HashSet<long> AlbumIds { get; } = new();
    }

    private sealed class AlbumAccumulator
    {
        public AlbumAccumulator(long albumId, string albumTitle, string albumArtistName, string albumKey)
        {
            AlbumId = albumId;
            AlbumTitle = albumTitle;
            AlbumArtistName = albumArtistName;
            AlbumKey = albumKey;
        }

        public long AlbumId { get; }
        public string AlbumTitle { get; }
        public string AlbumArtistName { get; }
        public string AlbumKey { get; }
        public int TrackCount { get; set; }
        public HashSet<long> ArtistIds { get; } = new();
        public List<AlbumTrackEntry> TrackEntries { get; } = new();
    }

    private sealed record AlbumTrackEntry(long TrackId, int? TrackNumber, int SortOrder);
}
