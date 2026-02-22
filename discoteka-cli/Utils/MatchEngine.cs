using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using discoteka_cli.Database;
using Microsoft.Data.Sqlite;

namespace discoteka_cli.Utils;

public static class MatchEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private static readonly string[] DjUtilityTokens =
    {
        "clean",
        "dirty",
        "intro",
        "outro",
        "lyrics",
        "on screen",
        "mastering"
    };

    private static readonly string[] VersionTokens =
    {
        "original mix",
        "extended mix",
        "radio edit",
        "club mix",
        "original edit",
        "extended edit",
        "club edit"
    };

    private static readonly string[] DjDescriptorTokens =
    {
        "remix",
        "edit",
        "vip",
        "flip",
        "bootleg"
    };

    private static readonly Regex FeatNormalize = new(@"\b(feat\.?|ft\.?|featuring)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BracketGroup = new(@"\((?<content>[^)]*)\)|\[(?<content>[^\]]*)\]|\{(?<content>[^}]*)\}", RegexOptions.Compiled);
    private static readonly Regex CamelotKey = new(@"\b(?<key>(1[0-2]|[1-9])[AB])\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private sealed record MatchRow(
        string Source,
        string Id,
        string? Title,
        string? Artist,
        string? Album,
        string? AlbumArtist,
        string? Genre,
        int? Plays,
        int? DurationMs,
        double? Bpm,
        string? MusicalKey,
        string? FeaturesJson,
        string? DjTagsJson,
        string? Path
    )
    {
        public string? NormalizedKey { get; set; }
        public string NormTitle { get; set; } = string.Empty;
        public string NormAlbum { get; set; } = string.Empty;
        public string NormArtistPrimary { get; set; } = string.Empty;
        public HashSet<string> NormArtistSet { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> FeatureSet { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> DjTagSet { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string TitleKey { get; set; } = string.Empty;
        public string TokenKey { get; set; } = string.Empty;
        public string ArtistKey { get; set; } = string.Empty;
        public int? DurationBucket { get; set; }
        public string? NormalizedPath { get; set; }
        public string? PathTailKey { get; set; }
    }

    private sealed record MatchCandidate(
        MatchRow A,
        MatchRow B,
        double Score,
        double TitleScore,
        double ArtistScore,
        double DurationScore,
        bool DurationMissing,
        double AlbumScore,
        double BpmScore,
        double KeyScore,
        double FeatureScore,
        double DjTagScore,
        double PathTailScore,
        List<string> Reasons,
        bool PathExact
    );

    public static void Run(bool dryRun, string? dbPath = null, double minAutoScore = 0.92)
    {
        var path = DatabaseInitializer.Initialize(dbPath);
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();

        var appleRows = LoadApple(connection);
        var rekordboxRows = LoadRekordbox(connection);
        var fileRows = LoadFile(connection);

        var trackIndex = LoadTrackLibraryIndex(connection);

        var allMatches = new List<MatchCandidate>();
        allMatches.AddRange(Match(appleRows, rekordboxRows, pathHint: false));
        allMatches.AddRange(Match(appleRows, fileRows, pathHint: false));
        allMatches.AddRange(Match(rekordboxRows, fileRows, pathHint: true));

        var autoLinks = allMatches
            .Where(c => c.Score >= minAutoScore && MeetsMinimums(c))
            .OrderByDescending(c => c.Score)
            .ToList();

        var reviewScore = Math.Clamp(minAutoScore - 0.06, 0.0, 1.0);
        var review = allMatches
            .Where(c => c.Score >= reviewScore && c.Score < minAutoScore && MeetsMinimums(c))
            .OrderByDescending(c => c.Score)
            .ToList();

        var assignedA = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assignedB = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var accepted = new List<MatchCandidate>();

        foreach (var match in autoLinks)
        {
            var keyA = $"{match.A.Source}:{match.A.Id}";
            var keyB = $"{match.B.Source}:{match.B.Id}";
            if (assignedA.Contains(keyA) || assignedB.Contains(keyB))
            {
                continue;
            }

            assignedA.Add(keyA);
            assignedB.Add(keyB);
            accepted.Add(match);
        }

        if (!dryRun)
        {
            ApplyMatches(connection, accepted, trackIndex);
            InsertUnmatchedApple(connection, appleRows, trackIndex);
            InsertUnmatchedRekordbox(connection, rekordboxRows, trackIndex);
            InsertUnmatchedFiles(connection, fileRows, trackIndex);
        }

        Console.WriteLine($"Auto-linked: {accepted.Count}");
        Console.WriteLine($"Review: {review.Count}");
        if (dryRun)
        {
            Console.WriteLine("Dry run: no database changes were applied.");
        }
    }

    private static bool MeetsMinimums(MatchCandidate candidate)
    {
        if (candidate.PathExact)
        {
            return true;
        }

        if (candidate.TitleScore >= 0.75)
        {
            return true;
        }

        if (candidate.DurationMissing && candidate.TitleScore >= 0.65 && candidate.ArtistScore >= 0.55)
        {
            return true;
        }

        return candidate.TitleScore >= 0.65 && candidate.DurationScore >= 0.85;
    }

    private static List<MatchCandidate> Match(List<MatchRow> source, List<MatchRow> target, bool pathHint)
    {
        var index = BuildIndex(target);
        var results = new List<MatchCandidate>();

        foreach (var row in source)
        {
            var candidates = new HashSet<MatchRow>();

            if (row.DurationBucket.HasValue && !string.IsNullOrWhiteSpace(row.TokenKey))
            {
                foreach (var bucket in new[] { row.DurationBucket.Value - 1, row.DurationBucket.Value, row.DurationBucket.Value + 1 })
                {
                    if (index.TokenKeyBuckets.TryGetValue((row.TokenKey, bucket), out var list))
                    {
                        foreach (var candidate in list)
                        {
                            candidates.Add(candidate);
                        }
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(row.TitleKey) && index.TitleKey.TryGetValue(row.TitleKey, out var titleMatches))
            {
                foreach (var candidate in titleMatches)
                {
                    candidates.Add(candidate);
                }
            }

            if (pathHint && !string.IsNullOrWhiteSpace(row.NormalizedPath) && index.PathKey.TryGetValue(row.NormalizedPath, out var pathMatches))
            {
                foreach (var candidate in pathMatches)
                {
                    candidates.Add(candidate);
                }
            }

            if (pathHint && !string.IsNullOrWhiteSpace(row.PathTailKey) && index.PathTailKey.TryGetValue(row.PathTailKey, out var pathTailMatches))
            {
                foreach (var candidate in pathTailMatches)
                {
                    candidates.Add(candidate);
                }
            }

            foreach (var candidate in candidates)
            {
                var match = Score(row, candidate, pathHint);
                if (match.Score >= 0.80)
                {
                    results.Add(match);
                }
            }
        }

        return results;
    }

    private static MatchCandidate Score(MatchRow a, MatchRow b, bool pathHint)
    {
        var reasons = new List<string>();

        var titleScore = ComputeTitleScore(a.NormTitle, b.NormTitle, reasons);
        var artistScore = ComputeArtistScore(a, b, reasons);
        var durationMissing = !a.DurationMs.HasValue || !b.DurationMs.HasValue;
        var durationScore = ComputeDurationScore(a.DurationMs, b.DurationMs, reasons);
        var albumScore = ComputeAlbumScore(a.NormAlbum, b.NormAlbum);
        var bpmScore = ComputeBpmScore(a.Bpm, b.Bpm);
        var keyScore = ComputeKeyScore(a.NormalizedKey, b.NormalizedKey);
        var featureScore = ComputeSetOverlapScore(a.FeatureSet, b.FeatureSet);
        var djTagScore = ComputeDjTagScore(a.DjTagSet, b.DjTagSet);
        var pathExact = pathHint && !string.IsNullOrWhiteSpace(a.NormalizedPath)
                                   && string.Equals(a.NormalizedPath, b.NormalizedPath, StringComparison.OrdinalIgnoreCase);
        var pathTailScore = pathHint ? ComputePathTailScore(a.NormalizedPath, b.NormalizedPath) : 0.0;

        var weightSum = 0.0;
        var total = 0.0;

        total += titleScore * 0.35;
        weightSum += 0.35;
        total += artistScore * 0.25;
        weightSum += 0.25;
        total += durationScore * 0.20;
        weightSum += 0.20;
        total += albumScore * 0.07;
        weightSum += 0.07;
        total += bpmScore * 0.05;
        weightSum += 0.05;
        total += keyScore * 0.04;
        weightSum += 0.04;
        total += featureScore * 0.03;
        weightSum += 0.03;
        total += djTagScore * 0.01;
        weightSum += 0.01;
        if (pathHint && pathTailScore > 0)
        {
            total += pathTailScore * 0.08;
            weightSum += 0.08;
        }

        var baseScore = weightSum > 0 ? total / weightSum : 0;
        var score = baseScore;

        var durationDelta = DurationDeltaSeconds(a.DurationMs, b.DurationMs);

        if (titleScore >= 0.99 && artistScore >= 0.99 && durationDelta.HasValue && durationDelta <= 2)
        {
            score += 0.15;
            reasons.Add("bonus_title_artist_duration");
        }
        else if (durationDelta.HasValue && durationDelta <= 2 && titleScore >= 0.9)
        {
            score += 0.10;
            reasons.Add("bonus_title_duration");
        }

        if (pathExact)
        {
            score += 0.25;
            reasons.Add("bonus_path_exact");
        }
        else if (pathHint && pathTailScore >= 0.99)
        {
            score += 0.12;
            reasons.Add("bonus_path_tail_exact");
        }
        else if (pathHint && pathTailScore >= 0.75)
        {
            score += 0.08;
            reasons.Add("bonus_path_tail_strong");
        }
        else if (pathHint && pathTailScore >= 0.50)
        {
            score += 0.04;
            reasons.Add("bonus_path_tail_partial");
        }

        if (titleScore < 0.6)
        {
            score -= 0.20;
            reasons.Add("penalty_title_low");
        }

        if (durationScore == 0 && !pathExact)
        {
            score -= 0.15;
            reasons.Add("penalty_duration_mismatch");
        }

        if (artistScore < 0.3 && !string.IsNullOrWhiteSpace(a.Artist) && !string.IsNullOrWhiteSpace(b.Artist))
        {
            score -= 0.10;
            reasons.Add("penalty_artist_mismatch");
        }

        score = Math.Clamp(score, 0.0, 1.0);

        return new MatchCandidate(
            a,
            b,
            score,
            titleScore,
            artistScore,
            durationScore,
            durationMissing,
            albumScore,
            bpmScore,
            keyScore,
            featureScore,
            djTagScore,
            pathTailScore,
            reasons,
            pathExact);
    }

    private static double ComputePathTailScore(string? a, string? b)
    {
        var segmentsA = GetPathSegments(a);
        var segmentsB = GetPathSegments(b);
        if (segmentsA.Count == 0 || segmentsB.Count == 0)
        {
            return 0.0;
        }

        var max = Math.Max(segmentsA.Count, segmentsB.Count);
        var min = Math.Min(segmentsA.Count, segmentsB.Count);

        // Compare from file name backward, weighting later segments more heavily.
        var weightedMatch = 0.0;
        var weightedTotal = 0.0;
        for (var i = 1; i <= min; i++)
        {
            var weight = i;
            weightedTotal += weight;
            if (string.Equals(segmentsA[^i], segmentsB[^i], StringComparison.OrdinalIgnoreCase))
            {
                weightedMatch += weight;
            }
            else
            {
                break;
            }
        }

        if (weightedTotal == 0)
        {
            return 0.0;
        }

        var suffixContinuityScore = weightedMatch / weightedTotal;
        var coveragePenalty = (double)min / max;
        return suffixContinuityScore * coveragePenalty;
    }

    private static double ComputeTitleScore(string a, string b, List<string> reasons)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return 0.5;
        }

        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            reasons.Add("title_exact");
            return 1.0;
        }

        var tokensA = Tokenize(a).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tokensB = Tokenize(b).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var jaccard = Jaccard(tokensA, tokensB);
        var lev = LevenshteinRatio(a, b);
        return Math.Max(jaccard, lev);
    }

    private static double ComputeArtistScore(MatchRow a, MatchRow b, List<string> reasons)
    {
        if (a.NormArtistSet.Count == 0 || b.NormArtistSet.Count == 0)
        {
            return 0.5;
        }

        if (!string.IsNullOrWhiteSpace(a.NormArtistPrimary) &&
            string.Equals(a.NormArtistPrimary, b.NormArtistPrimary, StringComparison.Ordinal))
        {
            reasons.Add("artist_exact");
        }

        var overlap = Jaccard(a.NormArtistSet, b.NormArtistSet);
        var primaryScore = LevenshteinRatio(a.NormArtistPrimary, b.NormArtistPrimary);
        return Math.Max(overlap, primaryScore);
    }

    private static double ComputeDurationScore(int? aMs, int? bMs, List<string> reasons)
    {
        if (!aMs.HasValue || !bMs.HasValue)
        {
            return 0.5;
        }

        var delta = Math.Abs(aMs.Value - bMs.Value) / 1000.0;
        if (delta == 0)
        {
            reasons.Add("duration_exact");
            return 1.0;
        }

        if (delta <= 2)
        {
            return 0.95;
        }

        if (delta <= 5)
        {
            return 0.85;
        }

        if (delta <= 10)
        {
            return 0.65;
        }

        if (delta <= 20)
        {
            return 0.35;
        }

        return 0.0;
    }

    private static double ComputeAlbumScore(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return 0.5;
        }

        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return 1.0;
        }

        var tokensA = Tokenize(a).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tokensB = Tokenize(b).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Jaccard(tokensA, tokensB);
    }

    private static double ComputeBpmScore(double? a, double? b)
    {
        if (!a.HasValue || !b.HasValue)
        {
            return 0.5;
        }

        var delta = Math.Abs(Math.Round(a.Value, 1) - Math.Round(b.Value, 1));
        if (delta <= 0.2)
        {
            return 1.0;
        }

        if (delta <= 0.6)
        {
            return 0.8;
        }

        if (delta <= 1.0)
        {
            return 0.6;
        }

        if (delta <= 2.0)
        {
            return 0.3;
        }

        return 0.0;
    }

    private static double ComputeKeyScore(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return 0.5;
        }

        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            return 1.0;
        }

        var aMatch = CamelotKey.Match(a);
        var bMatch = CamelotKey.Match(b);
        if (aMatch.Success && bMatch.Success && aMatch.Groups["key"].Value[..^1] == bMatch.Groups["key"].Value[..^1])
        {
            return 0.3;
        }

        return 0.0;
    }

    private static double ComputeSetOverlapScore(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return 0.5;
        }

        return Jaccard(a, b);
    }

    private static double ComputeDjTagScore(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return 0.5;
        }

        return Jaccard(a, b);
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return 0.0;
        }

        var intersect = a.Intersect(b, StringComparer.OrdinalIgnoreCase).Count();
        var union = a.Union(b, StringComparer.OrdinalIgnoreCase).Count();
        return union == 0 ? 0 : (double)intersect / union;
    }

    private static List<string> Tokenize(string value)
    {
        return value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Trim())
            .Where(token => token.Length >= 2)
            .ToList();
    }

    private static double LevenshteinRatio(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return 0.0;
        }

        var distance = LevenshteinDistance(a, b);
        var maxLen = Math.Max(a.Length, b.Length);
        return maxLen == 0 ? 1.0 : 1.0 - (double)distance / maxLen;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        var n = a.Length;
        var m = b.Length;
        var dp = new int[n + 1, m + 1];

        for (var i = 0; i <= n; i++)
        {
            dp[i, 0] = i;
        }

        for (var j = 0; j <= m; j++)
        {
            dp[0, j] = j;
        }

        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                dp[i, j] = Math.Min(
                    Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1),
                    dp[i - 1, j - 1] + cost);
            }
        }

        return dp[n, m];
    }

    private static int? DurationDeltaSeconds(int? aMs, int? bMs)
    {
        if (!aMs.HasValue || !bMs.HasValue)
        {
            return null;
        }

        return (int)Math.Abs(aMs.Value - bMs.Value) / 1000;
    }

    private static MatchIndex BuildIndex(List<MatchRow> rows)
    {
        var index = new MatchIndex();

        foreach (var row in rows)
        {
            if (!string.IsNullOrWhiteSpace(row.TitleKey))
            {
                if (!index.TitleKey.TryGetValue(row.TitleKey, out var list))
                {
                    list = new List<MatchRow>();
                    index.TitleKey[row.TitleKey] = list;
                }

                list.Add(row);
            }

            if (!string.IsNullOrWhiteSpace(row.TokenKey) && row.DurationBucket.HasValue)
            {
                var key = (row.TokenKey, row.DurationBucket.Value);
                if (!index.TokenKeyBuckets.TryGetValue(key, out var list))
                {
                    list = new List<MatchRow>();
                    index.TokenKeyBuckets[key] = list;
                }

                list.Add(row);
            }

            if (!string.IsNullOrWhiteSpace(row.NormalizedPath))
            {
                if (!index.PathKey.TryGetValue(row.NormalizedPath, out var list))
                {
                    list = new List<MatchRow>();
                    index.PathKey[row.NormalizedPath] = list;
                }

                list.Add(row);
            }

            if (!string.IsNullOrWhiteSpace(row.PathTailKey))
            {
                if (!index.PathTailKey.TryGetValue(row.PathTailKey, out var list))
                {
                    list = new List<MatchRow>();
                    index.PathTailKey[row.PathTailKey] = list;
                }

                list.Add(row);
            }
        }

        return index;
    }

    private sealed class MatchIndex
    {
        public Dictionary<string, List<MatchRow>> TitleKey { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<(string TokenKey, int Bucket), List<MatchRow>> TokenKeyBuckets { get; } = new();
        public Dictionary<string, List<MatchRow>> PathKey { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<MatchRow>> PathTailKey { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static List<MatchRow> LoadApple(SqliteConnection connection)
    {
        return LoadRows(connection, "AppleLibrary", "AppleMusicId", "TrackTitle", "TrackArtist", "AlbumTitle", "AlbumArtist", "Genre", "Plays", "Duration", "BPM", "MusicalKey", "Features", "DjTags", null);
    }

    private static List<MatchRow> LoadRekordbox(SqliteConnection connection)
    {
        return LoadRows(connection, "Rekordbox", "TrackId", "TrackTitle", "TrackArtist", "AlbumTitle", "AlbumArtist", null, null, "Duration", "BPM", "MusicalKey", "Features", "DjTags", "FilePath");
    }

    private static List<MatchRow> LoadFile(SqliteConnection connection)
    {
        return LoadRows(connection, "FileLibrary", "FileId", "Title", "Artist", "Album", "AlbumArtist", null, null, "Duration", "BPM", "MusicalKey", "Features", "DjTags", "Path");
    }

    private static List<MatchRow> LoadRows(
        SqliteConnection connection,
        string table,
        string idColumn,
        string titleColumn,
        string artistColumn,
        string albumColumn,
        string? albumArtistColumn,
        string? genreColumn,
        string? playsColumn,
        string durationColumn,
        string bpmColumn,
        string keyColumn,
        string featuresColumn,
        string djTagsColumn,
        string? pathColumn)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $@"
SELECT {idColumn},
       {titleColumn},
       {artistColumn},
       {albumColumn}
       {(albumArtistColumn != null ? "," + albumArtistColumn : string.Empty)}
       {(genreColumn != null ? "," + genreColumn : string.Empty)}
       {(playsColumn != null ? "," + playsColumn : string.Empty)},
       {durationColumn},
       {bpmColumn},
       {keyColumn},
       {featuresColumn},
       {djTagsColumn}
       {(pathColumn != null ? "," + pathColumn : string.Empty)}
FROM {table};";

        using var reader = command.ExecuteReader();
        var rows = new List<MatchRow>();
        while (reader.Read())
        {
            var id = reader.IsDBNull(0) ? null : reader.GetValue(0).ToString();
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var title = reader.IsDBNull(1) ? null : reader.GetString(1);
            var artist = reader.IsDBNull(2) ? null : reader.GetString(2);
            var album = reader.IsDBNull(3) ? null : reader.GetString(3);
            var offset = 4;
            var albumArtist = albumArtistColumn != null && !reader.IsDBNull(offset) ? reader.GetString(offset) : null;
            if (albumArtistColumn != null)
            {
                offset++;
            }

            var genre = genreColumn != null && !reader.IsDBNull(offset) ? reader.GetString(offset) : null;
            if (genreColumn != null)
            {
                offset++;
            }

            var plays = playsColumn != null && !reader.IsDBNull(offset) ? Convert.ToInt32(reader.GetValue(offset)) : (int?)null;
            if (playsColumn != null)
            {
                offset++;
            }

            var duration = reader.IsDBNull(offset) ? (int?)null : Convert.ToInt32(reader.GetValue(offset));
            var bpm = reader.IsDBNull(offset + 1) ? (double?)null : Convert.ToDouble(reader.GetValue(offset + 1));
            var key = reader.IsDBNull(offset + 2) ? null : reader.GetString(offset + 2);
            var features = reader.IsDBNull(offset + 3) ? null : reader.GetString(offset + 3);
            var djTags = reader.IsDBNull(offset + 4) ? null : reader.GetString(offset + 4);
            var path = pathColumn != null && !reader.IsDBNull(offset + 5) ? reader.GetString(offset + 5) : null;

            var row = new MatchRow(
                table,
                id,
                title,
                artist,
                album,
                albumArtist,
                genre,
                plays,
                duration,
                bpm,
                key,
                features,
                djTags,
                path);

            NormalizeRow(row);
            rows.Add(row);
        }

        return rows;
    }

    private static void NormalizeRow(MatchRow row)
    {
        row.NormTitle = NormTitle(row.Title);
        row.NormAlbum = NormTitle(row.Album);

        var artistTokens = NormArtist(row.Artist);
        row.NormArtistSet = artistTokens.ToHashSet(StringComparer.OrdinalIgnoreCase);
        row.NormArtistPrimary = artistTokens.FirstOrDefault() ?? string.Empty;
        row.ArtistKey = row.NormArtistPrimary;

        row.FeatureSet = ParseFeatures(row.FeaturesJson);
        row.DjTagSet = ParseDjTags(row.DjTagsJson);

        row.TitleKey = row.NormTitle.Length > 12 ? row.NormTitle[..12] : row.NormTitle;
        row.TokenKey = BuildTokenKey(row.NormTitle);

        if (row.DurationMs.HasValue)
        {
            row.DurationBucket = row.DurationMs.Value / 2000;
        }

        row.NormalizedKey = NormalizeKey(row.MusicalKey);
        row.NormalizedPath = NormalizePath(row.Path);
        row.PathTailKey = BuildPathTailKey(row.NormalizedPath);
    }

    private static string NormTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant().Trim();
        normalized = FeatNormalize.Replace(normalized, "feat");
        normalized = StripDjUtilityBrackets(normalized);
        normalized = RemoveVersionTokens(normalized);

        var builder = new StringBuilder();
        var lastWasSpace = false;
        foreach (var ch in normalized)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                lastWasSpace = false;
                continue;
            }

            if (IsJapanese(ch))
            {
                builder.Append(ch);
                lastWasSpace = false;
                continue;
            }

            if (!lastWasSpace)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        return Regex.Replace(builder.ToString().Trim(), @"\s+", " ");
    }

    private static string NormAlbum(string? value)
    {
        return NormTitle(value);
    }

    private static List<string> NormArtist(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new List<string>();
        }

        var normalized = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        normalized = normalized.Replace("dj ", "", StringComparison.Ordinal);
        normalized = normalized.Trim();

        var parts = Regex.Split(normalized, @"\s*(?:&|and|x|×|,|;)\s*")
            .Select(part => part.Trim())
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part.StartsWith("the ") ? part[4..] : part)
            .ToList();

        return parts;
    }

    private static HashSet<string> ParseFeatures(string? json)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json))
        {
            return set;
        }

        try
        {
            var list = JsonSerializer.Deserialize<string[]>(json, JsonOptions);
            if (list == null)
            {
                return set;
            }

            foreach (var entry in list)
            {
                var normalized = NormalizeName(entry);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    set.Add(normalized);
                }
            }
        }
        catch
        {
            return set;
        }

        return set;
    }

    private static HashSet<string> ParseDjTags(string? json)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json))
        {
            return set;
        }

        try
        {
            var list = JsonSerializer.Deserialize<string[]>(json, JsonOptions);
            if (list == null)
            {
                return set;
            }

            foreach (var entry in list)
            {
                if (string.IsNullOrWhiteSpace(entry))
                {
                    continue;
                }

                if (DjDescriptorTokens.Any(token => entry.Contains(token, StringComparison.OrdinalIgnoreCase)))
                {
                    set.Add(entry.Trim());
                }
            }
        }
        catch
        {
            return set;
        }

        return set;
    }

    private static string NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"\s+", " ");
        return normalized.Trim();
    }

    private static string StripDjUtilityBrackets(string input)
    {
        return BracketGroup.Replace(input, match =>
        {
            var content = match.Groups["content"].Value;
            return DjUtilityTokens.Any(token => content.Contains(token, StringComparison.OrdinalIgnoreCase)) ? "" : match.Value;
        });
    }

    private static string RemoveVersionTokens(string input)
    {
        foreach (var token in VersionTokens)
        {
            input = input.Replace(token, "", StringComparison.OrdinalIgnoreCase);
        }

        return Regex.Replace(input, @"\s+", " ").Trim();
    }

    private static string BuildTokenKey(string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle))
        {
            return string.Empty;
        }

        var tokens = normalizedTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .OrderByDescending(token => token.Length)
            .Take(3)
            .ToArray();

        return string.Join('|', tokens);
    }

    private static bool IsJapanese(char ch)
    {
        return ch is >= '\u3040' and <= '\u30FF' || ch is >= '\u4E00' and <= '\u9FAF';
    }

    private static string? NormalizeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = CamelotKey.Match(value);
        if (match.Success)
        {
            return match.Groups["key"].Value.ToUpperInvariant();
        }

        return value.Trim();
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var result = path.Trim();
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            result = Uri.UnescapeDataString(uri.LocalPath);
        }

        result = result.Replace('\\', '/');
        result = Regex.Replace(result, "/{2,}", "/");
        if (result.Length > 1 && result.EndsWith("/", StringComparison.Ordinal))
        {
            result = result.TrimEnd('/');
        }

        // Normalize Windows drive letter casing (e.g. c:/ => C:/).
        if (result.Length >= 2 && result[1] == ':' && char.IsLetter(result[0]))
        {
            result = char.ToUpperInvariant(result[0]) + result[1..];
        }

        return result;
    }

    private static string? BuildPathTailKey(string? normalizedPath)
    {
        var segments = GetPathSegments(normalizedPath);
        if (segments.Count == 0)
        {
            return null;
        }

        var take = Math.Min(3, segments.Count);
        var tail = segments.Skip(segments.Count - take);
        return string.Join('/', tail);
    }

    private static List<string> GetPathSegments(string? normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return new List<string>();
        }

        var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment.Trim())
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .ToList();

        // Drop Windows drive roots, we care about relative path suffix for matching.
        if (segments.Count > 0 &&
            segments[0].Length == 2 &&
            segments[0][1] == ':' &&
            char.IsLetter(segments[0][0]))
        {
            segments.RemoveAt(0);
        }

        return segments;
    }

    private static void ApplyMatches(SqliteConnection connection, List<MatchCandidate> matches, TrackIndex trackIndex)
    {
        using var transaction = connection.BeginTransaction();
        var nextTrackId = trackIndex.NextTrackId;

        foreach (var match in matches)
        {
            var trackId = ResolveTrackId(match, trackIndex, ref nextTrackId);
            if (trackId == null)
            {
                continue;
            }

            UpsertTrackLibrary(connection, transaction, trackId.Value, match, trackIndex);
            InsertLink(connection, transaction, match.A, trackId.Value);
            InsertLink(connection, transaction, match.B, trackId.Value);
        }

        transaction.Commit();
    }

    private static void InsertUnmatchedApple(SqliteConnection connection, List<MatchRow> appleRows, TrackIndex trackIndex)
    {
        using var transaction = connection.BeginTransaction();
        var nextTrackId = trackIndex.NextTrackId;

        foreach (var apple in appleRows)
        {
            if (trackIndex.ByApple.ContainsKey(apple.Id))
            {
                continue;
            }

            var trackId = nextTrackId++;
            InsertAppleOnly(connection, transaction, trackId, apple);
            trackIndex.Update(trackId, apple.Id, null, null);
        }

        trackIndex.NextTrackId = nextTrackId;
        transaction.Commit();
    }

    private static void InsertUnmatchedRekordbox(SqliteConnection connection, List<MatchRow> rekordboxRows, TrackIndex trackIndex)
    {
        using var transaction = connection.BeginTransaction();
        var nextTrackId = trackIndex.NextTrackId;

        foreach (var rekordbox in rekordboxRows)
        {
            if (trackIndex.ByRekordbox.ContainsKey(rekordbox.Id))
            {
                continue;
            }

            var trackId = nextTrackId++;
            InsertRekordboxOnly(connection, transaction, trackId, rekordbox);
            trackIndex.Update(trackId, null, rekordbox.Id, null);
        }

        trackIndex.NextTrackId = nextTrackId;
        transaction.Commit();
    }

    private static void InsertUnmatchedFiles(SqliteConnection connection, List<MatchRow> fileRows, TrackIndex trackIndex)
    {
        using var transaction = connection.BeginTransaction();
        var nextTrackId = trackIndex.NextTrackId;

        foreach (var file in fileRows)
        {
            var pathKey = NormalizePath(file.Path);
            if (pathKey != null && trackIndex.ByFilePath.ContainsKey(pathKey))
            {
                continue;
            }

            var trackId = nextTrackId++;
            InsertFileOnly(connection, transaction, trackId, file);
            trackIndex.Update(trackId, null, null, file.Path);
        }

        trackIndex.NextTrackId = nextTrackId;
        transaction.Commit();
    }

    private static void InsertAppleOnly(SqliteConnection connection, SqliteTransaction transaction, long trackId, MatchRow apple)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
INSERT INTO TrackLibrary (
    TrackId,
    TrackTitle,
    TrackArtist,
    TrackTitleRaw,
    TrackArtistRaw,
    AlbumTitle,
    AlbumArtist,
    Genre,
    Duration,
    Plays,
    AppleMusicId,
    RekordboxId,
    FilePath,
    MusicalKey,
    BPM,
    Features,
    DjTags
)
VALUES (
    $trackId,
    $title,
    $artist,
    $titleRaw,
    $artistRaw,
    $album,
    $albumArtist,
    $genre,
    $duration,
    $plays,
    $appleId,
    $rekordboxId,
    $filePath,
    $musicalKey,
    $bpm,
    $features,
    $djTags
);";

        command.Parameters.AddWithValue("$trackId", trackId);
        command.Parameters.AddWithValue("$title", (object?)apple.Title ?? DBNull.Value);
        command.Parameters.AddWithValue("$artist", (object?)apple.Artist ?? DBNull.Value);
        command.Parameters.AddWithValue("$titleRaw", (object?)apple.Title ?? DBNull.Value);
        command.Parameters.AddWithValue("$artistRaw", (object?)apple.Artist ?? DBNull.Value);
        command.Parameters.AddWithValue("$album", (object?)apple.Album ?? DBNull.Value);
        command.Parameters.AddWithValue("$albumArtist", (object?)apple.AlbumArtist ?? DBNull.Value);
        command.Parameters.AddWithValue("$genre", (object?)apple.Genre ?? DBNull.Value);
        command.Parameters.AddWithValue("$duration", apple.DurationMs.HasValue ? apple.DurationMs.Value / 1000 : DBNull.Value);
        command.Parameters.AddWithValue("$plays", apple.Plays.HasValue ? apple.Plays.Value : DBNull.Value);
        command.Parameters.AddWithValue("$appleId", apple.Id);
        command.Parameters.AddWithValue("$rekordboxId", DBNull.Value);
        command.Parameters.AddWithValue("$filePath", DBNull.Value);
        command.Parameters.AddWithValue("$musicalKey", (object?)apple.MusicalKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$bpm", (object?)apple.Bpm ?? DBNull.Value);
        command.Parameters.AddWithValue("$features", (object?)apple.FeaturesJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$djTags", (object?)apple.DjTagsJson ?? DBNull.Value);
        command.ExecuteNonQuery();

        using var link = connection.CreateCommand();
        link.Transaction = transaction;
        link.CommandText = "INSERT OR IGNORE INTO TrackToApple (TrackId, AppleMusicId) VALUES ($trackId, $appleId);";
        link.Parameters.AddWithValue("$trackId", trackId);
        link.Parameters.AddWithValue("$appleId", apple.Id);
        link.ExecuteNonQuery();
    }

    private static void InsertRekordboxOnly(SqliteConnection connection, SqliteTransaction transaction, long trackId, MatchRow rekordbox)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
INSERT INTO TrackLibrary (
    TrackId,
    TrackTitle,
    TrackArtist,
    TrackTitleRaw,
    TrackArtistRaw,
    AlbumTitle,
    AlbumArtist,
    Genre,
    Duration,
    Plays,
    AppleMusicId,
    RekordboxId,
    FilePath,
    MusicalKey,
    BPM,
    Features,
    DjTags
)
VALUES (
    $trackId,
    $title,
    $artist,
    $titleRaw,
    $artistRaw,
    $album,
    $albumArtist,
    $genre,
    $duration,
    $plays,
    $appleId,
    $rekordboxId,
    $filePath,
    $musicalKey,
    $bpm,
    $features,
    $djTags
);";

        command.Parameters.AddWithValue("$trackId", trackId);
        command.Parameters.AddWithValue("$title", (object?)rekordbox.Title ?? DBNull.Value);
        command.Parameters.AddWithValue("$artist", (object?)rekordbox.Artist ?? DBNull.Value);
        command.Parameters.AddWithValue("$titleRaw", (object?)rekordbox.Title ?? DBNull.Value);
        command.Parameters.AddWithValue("$artistRaw", (object?)rekordbox.Artist ?? DBNull.Value);
        command.Parameters.AddWithValue("$album", (object?)rekordbox.Album ?? DBNull.Value);
        command.Parameters.AddWithValue("$albumArtist", (object?)rekordbox.AlbumArtist ?? DBNull.Value);
        command.Parameters.AddWithValue("$genre", DBNull.Value);
        command.Parameters.AddWithValue("$duration", rekordbox.DurationMs.HasValue ? rekordbox.DurationMs.Value / 1000 : DBNull.Value);
        command.Parameters.AddWithValue("$plays", DBNull.Value);
        command.Parameters.AddWithValue("$appleId", DBNull.Value);
        command.Parameters.AddWithValue("$rekordboxId", rekordbox.Id);
        command.Parameters.AddWithValue("$filePath", DBNull.Value);
        command.Parameters.AddWithValue("$musicalKey", (object?)rekordbox.MusicalKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$bpm", (object?)rekordbox.Bpm ?? DBNull.Value);
        command.Parameters.AddWithValue("$features", (object?)rekordbox.FeaturesJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$djTags", (object?)rekordbox.DjTagsJson ?? DBNull.Value);
        command.ExecuteNonQuery();

        using var link = connection.CreateCommand();
        link.Transaction = transaction;
        link.CommandText = "INSERT OR IGNORE INTO TrackToRekordbox (TrackId, RekordboxId) VALUES ($trackId, $rekordboxId);";
        link.Parameters.AddWithValue("$trackId", trackId);
        link.Parameters.AddWithValue("$rekordboxId", rekordbox.Id);
        link.ExecuteNonQuery();
    }

    private static void InsertFileOnly(SqliteConnection connection, SqliteTransaction transaction, long trackId, MatchRow file)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
INSERT INTO TrackLibrary (
    TrackId,
    TrackTitle,
    TrackArtist,
    TrackTitleRaw,
    TrackArtistRaw,
    AlbumTitle,
    AlbumArtist,
    Genre,
    Duration,
    Plays,
    AppleMusicId,
    RekordboxId,
    FilePath,
    MusicalKey,
    BPM,
    Features,
    DjTags
)
VALUES (
    $trackId,
    $title,
    $artist,
    $titleRaw,
    $artistRaw,
    $album,
    $albumArtist,
    $genre,
    $duration,
    $plays,
    $appleId,
    $rekordboxId,
    $filePath,
    $musicalKey,
    $bpm,
    $features,
    $djTags
);";

        command.Parameters.AddWithValue("$trackId", trackId);
        command.Parameters.AddWithValue("$title", (object?)file.Title ?? DBNull.Value);
        command.Parameters.AddWithValue("$artist", (object?)file.Artist ?? DBNull.Value);
        command.Parameters.AddWithValue("$titleRaw", (object?)file.Title ?? DBNull.Value);
        command.Parameters.AddWithValue("$artistRaw", (object?)file.Artist ?? DBNull.Value);
        command.Parameters.AddWithValue("$album", (object?)file.Album ?? DBNull.Value);
        command.Parameters.AddWithValue("$albumArtist", (object?)file.AlbumArtist ?? DBNull.Value);
        command.Parameters.AddWithValue("$genre", DBNull.Value);
        command.Parameters.AddWithValue("$duration", file.DurationMs.HasValue ? file.DurationMs.Value / 1000 : DBNull.Value);
        command.Parameters.AddWithValue("$plays", DBNull.Value);
        command.Parameters.AddWithValue("$appleId", DBNull.Value);
        command.Parameters.AddWithValue("$rekordboxId", DBNull.Value);
        command.Parameters.AddWithValue("$filePath", (object?)file.Path ?? DBNull.Value);
        command.Parameters.AddWithValue("$musicalKey", (object?)file.MusicalKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$bpm", (object?)file.Bpm ?? DBNull.Value);
        command.Parameters.AddWithValue("$features", (object?)file.FeaturesJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$djTags", (object?)file.DjTagsJson ?? DBNull.Value);
        command.ExecuteNonQuery();

        using var link = connection.CreateCommand();
        link.Transaction = transaction;
        link.CommandText = "INSERT OR IGNORE INTO TrackToFile (TrackId, FileId) VALUES ($trackId, $fileId);";
        link.Parameters.AddWithValue("$trackId", trackId);
        link.Parameters.AddWithValue("$fileId", file.Id);
        link.ExecuteNonQuery();
    }

    private static long? ResolveTrackId(MatchCandidate match, TrackIndex index, ref long nextTrackId)
    {
        if (TryGetTrackId(index, match.A, out var trackId))
        {
            return trackId;
        }

        if (TryGetTrackId(index, match.B, out trackId))
        {
            return trackId;
        }

        var newId = nextTrackId++;
        index.AddNew(newId, match.A, match.B);
        return newId;
    }

    private static bool TryGetTrackId(TrackIndex index, MatchRow row, out long trackId)
    {
        if (row.Source == "AppleLibrary" && index.ByApple.TryGetValue(row.Id, out trackId))
        {
            return true;
        }

        if (row.Source == "Rekordbox" && index.ByRekordbox.TryGetValue(row.Id, out trackId))
        {
            return true;
        }

        var pathKey = NormalizePath(row.Path);
        if (row.Source == "FileLibrary" && pathKey != null && index.ByFilePath.TryGetValue(pathKey, out trackId))
        {
            return true;
        }

        trackId = 0;
        return false;
    }

    private static void UpsertTrackLibrary(SqliteConnection connection, SqliteTransaction transaction, long trackId, MatchCandidate match, TrackIndex index)
    {
        var apple = match.A.Source == "AppleLibrary" ? match.A : match.B.Source == "AppleLibrary" ? match.B : null;
        var rekordbox = match.A.Source == "Rekordbox" ? match.A : match.B.Source == "Rekordbox" ? match.B : null;
        var file = match.A.Source == "FileLibrary" ? match.A : match.B.Source == "FileLibrary" ? match.B : null;

        var title = apple?.Title ?? rekordbox?.Title ?? file?.Title;
        var artist = apple?.Artist ?? rekordbox?.Artist ?? file?.Artist;
        var album = apple?.Album ?? rekordbox?.Album ?? file?.Album;
        var albumArtist = apple?.AlbumArtist ?? rekordbox?.AlbumArtist ?? file?.AlbumArtist ?? apple?.Artist ?? rekordbox?.Artist ?? file?.Artist;
        var genre = apple?.Genre;
        var plays = apple?.Plays;
        var durationSeconds = MedianSeconds(new[] { apple?.DurationMs, rekordbox?.DurationMs, file?.DurationMs });
        var bpm = rekordbox?.Bpm ?? file?.Bpm ?? apple?.Bpm;
        var musicalKey = rekordbox?.MusicalKey ?? file?.MusicalKey ?? apple?.MusicalKey;
        var features = UnionJson(apple?.FeaturesJson, rekordbox?.FeaturesJson, file?.FeaturesJson);
        var djTags = UnionJson(apple?.DjTagsJson, rekordbox?.DjTagsJson, file?.DjTagsJson);
        var filePath = file?.Path;

        using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = "SELECT COUNT(1) FROM TrackLibrary WHERE TrackId = $trackId;";
        select.Parameters.AddWithValue("$trackId", trackId);
        var exists = Convert.ToInt32(select.ExecuteScalar()) > 0;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (!exists)
        {
            command.CommandText = @"
INSERT INTO TrackLibrary (
    TrackId,
    TrackTitle,
    TrackArtist,
    TrackTitleRaw,
    TrackArtistRaw,
    AlbumTitle,
    AlbumArtist,
    Genre,
    Duration,
    Plays,
    AppleMusicId,
    RekordboxId,
    FilePath,
    MusicalKey,
    BPM,
    Features,
    DjTags
)
VALUES (
    $trackId,
    $title,
    $artist,
    $titleRaw,
    $artistRaw,
    $album,
    $albumArtist,
    $genre,
    $duration,
    $plays,
    $appleId,
    $rekordboxId,
    $filePath,
    $musicalKey,
    $bpm,
    $features,
    $djTags
);";
        }
        else
        {
            command.CommandText = @"
UPDATE TrackLibrary
SET TrackTitle = COALESCE(TrackTitle, $title),
    TrackArtist = COALESCE(TrackArtist, $artist),
    TrackTitleRaw = COALESCE(TrackTitleRaw, $titleRaw),
    TrackArtistRaw = COALESCE(TrackArtistRaw, $artistRaw),
    AlbumTitle = COALESCE(AlbumTitle, $album),
    AlbumArtist = COALESCE(AlbumArtist, $albumArtist),
    Duration = COALESCE(Duration, $duration),
    AppleMusicId = COALESCE(AppleMusicId, $appleId),
    RekordboxId = COALESCE(RekordboxId, $rekordboxId),
    FilePath = COALESCE(FilePath, $filePath),
    MusicalKey = COALESCE(MusicalKey, $musicalKey),
    BPM = COALESCE(BPM, $bpm),
    Features = COALESCE(Features, $features),
    DjTags = COALESCE(DjTags, $djTags)
WHERE TrackId = $trackId;";
        }

        command.Parameters.AddWithValue("$trackId", trackId);
        command.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
        command.Parameters.AddWithValue("$artist", (object?)artist ?? DBNull.Value);
        command.Parameters.AddWithValue("$titleRaw", (object?)title ?? DBNull.Value);
        command.Parameters.AddWithValue("$artistRaw", (object?)artist ?? DBNull.Value);
        command.Parameters.AddWithValue("$album", (object?)album ?? DBNull.Value);
        command.Parameters.AddWithValue("$albumArtist", (object?)albumArtist ?? DBNull.Value);
        command.Parameters.AddWithValue("$genre", (object?)genre ?? DBNull.Value);
        command.Parameters.AddWithValue("$duration", durationSeconds.HasValue ? durationSeconds.Value : DBNull.Value);
        command.Parameters.AddWithValue("$plays", plays.HasValue ? plays.Value : DBNull.Value);
        command.Parameters.AddWithValue("$appleId", (object?)apple?.Id ?? DBNull.Value);
        command.Parameters.AddWithValue("$rekordboxId", (object?)rekordbox?.Id ?? DBNull.Value);
        command.Parameters.AddWithValue("$filePath", (object?)filePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$musicalKey", (object?)musicalKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$bpm", (object?)bpm ?? DBNull.Value);
        command.Parameters.AddWithValue("$features", (object?)features ?? DBNull.Value);
        command.Parameters.AddWithValue("$djTags", (object?)djTags ?? DBNull.Value);
        command.ExecuteNonQuery();

        index.Update(trackId, apple?.Id, rekordbox?.Id, filePath);
    }

    private static void InsertLink(SqliteConnection connection, SqliteTransaction transaction, MatchRow row, long trackId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        if (row.Source == "AppleLibrary")
        {
            command.CommandText = "INSERT OR IGNORE INTO TrackToApple (TrackId, AppleMusicId) VALUES ($trackId, $appleId);";
            command.Parameters.AddWithValue("$trackId", trackId);
            command.Parameters.AddWithValue("$appleId", row.Id);
        }
        else if (row.Source == "Rekordbox")
        {
            command.CommandText = "INSERT OR IGNORE INTO TrackToRekordbox (TrackId, RekordboxId) VALUES ($trackId, $rekordboxId);";
            command.Parameters.AddWithValue("$trackId", trackId);
            command.Parameters.AddWithValue("$rekordboxId", row.Id);
        }
        else if (row.Source == "FileLibrary")
        {
            command.CommandText = "INSERT OR IGNORE INTO TrackToFile (TrackId, FileId) VALUES ($trackId, $fileId);";
            command.Parameters.AddWithValue("$trackId", trackId);
            command.Parameters.AddWithValue("$fileId", row.Id);
        }
        else
        {
            return;
        }

        command.ExecuteNonQuery();
    }

    private sealed class TrackIndex
    {
        public Dictionary<string, long> ByApple { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, long> ByRekordbox { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, long> ByFilePath { get; } = new(StringComparer.OrdinalIgnoreCase);
        public long NextTrackId { get; set; }

        public void AddNew(long trackId, MatchRow a, MatchRow b)
        {
            Update(trackId, a.Source == "AppleLibrary" ? a.Id : null, a.Source == "Rekordbox" ? a.Id : null, a.Path);
            Update(trackId, b.Source == "AppleLibrary" ? b.Id : null, b.Source == "Rekordbox" ? b.Id : null, b.Path);
        }

        public void Update(long trackId, string? appleId, string? rekordboxId, string? filePath)
        {
            if (!string.IsNullOrWhiteSpace(appleId))
            {
                ByApple[appleId] = trackId;
            }

            if (!string.IsNullOrWhiteSpace(rekordboxId))
            {
                ByRekordbox[rekordboxId] = trackId;
            }

            var pathKey = NormalizePath(filePath);
            if (!string.IsNullOrWhiteSpace(pathKey))
            {
                ByFilePath[pathKey] = trackId;
            }
        }
    }

    private static TrackIndex LoadTrackLibraryIndex(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT TrackId, AppleMusicId, RekordboxId, FilePath FROM TrackLibrary;";
        using var reader = command.ExecuteReader();

        var index = new TrackIndex();
        long maxId = 0;

        while (reader.Read())
        {
            var trackId = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
            var appleId = reader.IsDBNull(1) ? null : reader.GetString(1);
            var rekordboxId = reader.IsDBNull(2) ? null : reader.GetString(2);
            var filePath = reader.IsDBNull(3) ? null : reader.GetString(3);

            if (trackId > maxId)
            {
                maxId = trackId;
            }

            index.Update(trackId, appleId, rekordboxId, filePath);
        }

        index.NextTrackId = maxId + 1;
        return index;
    }

    private static string? UnionJson(params string?[] jsonSets)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var json in jsonSets)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                continue;
            }

            try
            {
                var values = JsonSerializer.Deserialize<string[]>(json, JsonOptions);
                if (values == null)
                {
                    continue;
                }

                foreach (var value in values)
                {
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        set.Add(value);
                    }
                }
            }
            catch
            {
            }
        }

        return set.Count == 0 ? null : JsonSerializer.Serialize(set.ToArray(), JsonOptions);
    }

    private static int? MedianSeconds(int?[] durationsMs)
    {
        var seconds = durationsMs
            .Where(value => value.HasValue)
            .Select(value => value.Value / 1000)
            .OrderBy(value => value)
            .ToList();

        if (seconds.Count == 0)
        {
            return null;
        }

        return seconds[seconds.Count / 2];
    }
}
