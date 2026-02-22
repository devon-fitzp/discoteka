using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization.Metadata;

namespace discoteka_cli.Utils;

public static class MetadataCleaner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    private static readonly Regex MultiWhitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex DashNormalize = new(@"[\u2013\u2014\u2212]", RegexOptions.Compiled);
    private static readonly Regex FileExtensionSuffix = new(@"\.(mp3|wav|flac|m4a|aiff|mov|mp4)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AudioVisualizerSuffix = new(@"\(([^)]*audio\s*visualizer[^)]*)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ArtistKeyBpmPattern = new(@"^(?<key>(1[0-2]|[1-9])[AB])\s*-\s*(?<bpm>\d{2,3}(?:\.\d)?)\s*-\s*(?<artist>.+)$", RegexOptions.Compiled);
    private static readonly Regex ArtistKeyPattern = new(@"^(?<key>(1[0-2]|[1-9])[AB])\s*-\s*(?<artist>.+)$", RegexOptions.Compiled);
    private static readonly Regex ArtistNumberPrefix = new(@"^\s*\d{1,3}\.\s+", RegexOptions.Compiled);
    private static readonly Regex BracketPerformer = new(@"^〔(?<artist>[^〕]+)〕", RegexOptions.Compiled);
    private static readonly Regex BracketFeat = new(@"〔\s*feat\.?\s+(?<names>[^〕]+)〕", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TitleTrackNumberPrefix = new(@"^\s*(?<num>\d{1,3})\.\s+", RegexOptions.Compiled);
    private static readonly Regex TitleSourceIdPrefix = new(@"^\s*(?<num>\d{4,})_", RegexOptions.Compiled);
    private static readonly Regex ArtistTitleSplit = new(@"\s-\s", RegexOptions.Compiled);
    private static readonly Regex BpmExplicit = new(@"\b(?<bpm>\d{2,3}(?:\.\d)?)\s*bpm\b|\bbpm\s*(?<bpm>\d{2,3}(?:\.\d)?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BpmBracketOnly = new(@"\[(?<bpm>\d{2,3})\]", RegexOptions.Compiled);
    private static readonly Regex KeyExplicit = new(@"\bkey\s*(?<key>(1[0-2]|[1-9])[AB])\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FeaturePattern = new(@"\((?:feat\.?|ft\.?)\s+(?<names>[^)]+)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FeatureInlinePattern = new(@"\b(?:feat\.?|ft\.?)\s+(?<names>[^-]+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] DjTagTokens =
    {
        "clean",
        "dirty",
        "intro",
        "outro",
        "transition",
        "quick hit"
    };

    private static readonly string[] MixTokens =
    {
        "remix",
        "edit",
        "vip",
        "flip",
        "bootleg",
        "version",
        "mix"
    };

    private static readonly string[] VersionSuffixes =
    {
        "original mix",
        "extended mix",
        "club mix",
        "radio edit",
        "original edit",
        "extended edit",
        "club edit"
    };

    private static readonly string[] JunkSuffixTokens =
    {
        "lyrics",
        "lyric video",
        "official video",
        "on screen",
        "audio"
    };

    public sealed record CleanResult(
        string? Title,
        string? Artist,
        string? MusicalKey,
        double? Bpm,
        string FeaturesJson,
        string DjTagsJson,
        string CleanLogJson,
        double CleanConfidence
    );

    public static CleanResult Clean(string? titleInput, string? artistInput)
    {
        var log = new List<string>();
        var features = new List<string>();
        var djTags = new List<string>();
        string? musicalKey = null;
        double? bpm = null;

        var title = Normalize(titleInput, log);
        var artist = Normalize(artistInput, log);

        var keyBpmApplied = false;
        var bracketPerformerApplied = false;
        var artistTitleSplitApplied = false;
        var explicitTokenApplied = false;
        var ambiguousDash = false;
        var overwriteArtistAttempt = false;

        if (!string.IsNullOrWhiteSpace(artist))
        {
            var numberPrefix = ArtistNumberPrefix.Match(artist);
            if (numberPrefix.Success)
            {
                artist = artist[numberPrefix.Length..].TrimStart();
                log.Add("artist_number_prefix");
            }

            var match = ArtistKeyBpmPattern.Match(artist);
            if (match.Success)
            {
                var candidateKey = match.Groups["key"].Value;
                var candidateBpm = ParseBpm(match.Groups["bpm"].Value);
                var remainder = match.Groups["artist"].Value.Trim();
                if (candidateBpm.HasValue)
                {
                    bpm = candidateBpm;
                    musicalKey = candidateKey;
                    artist = remainder;
                    keyBpmApplied = true;
                    log.Add("artist_key_bpm");
                }
            }

            if (!keyBpmApplied)
            {
                match = ArtistKeyPattern.Match(artist);
                if (match.Success)
                {
                    musicalKey = match.Groups["key"].Value;
                    artist = match.Groups["artist"].Value.Trim();
                    keyBpmApplied = true;
                    log.Add("artist_key");
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            title = StripFileExtension(title, log);
            title = StripAudioVisualizer(title, log);

            var bracketMatch = BracketPerformer.Match(title);
            while (bracketMatch.Success)
            {
                var performer = bracketMatch.Groups["artist"].Value.Trim();
                if (string.Equals(performer, "歌ってみた", StringComparison.OrdinalIgnoreCase))
                {
                    djTags.Add("cover");
                    log.Add("jp_cover_tag");
                }
                else if (performer.StartsWith("feat", StringComparison.OrdinalIgnoreCase))
                {
                    var featMatch = BracketFeat.Match(bracketMatch.Value);
                    if (featMatch.Success)
                    {
                        ExtractFeatures(featMatch.Groups["names"].Value, features, log, "jp_feat");
                    }
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(artist) || EqualsLoose(artist, performer))
                    {
                        artist = performer;
                        bracketPerformerApplied = true;
                        log.Add("jp_performer_prefix");
                    }
                }

                title = title[bracketMatch.Length..].TrimStart();
                bracketMatch = BracketPerformer.Match(title);
            }
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            var trackPrefix = TitleTrackNumberPrefix.Match(title);
            if (trackPrefix.Success)
            {
                title = title[trackPrefix.Length..].TrimStart();
                log.Add("source_id_dot_prefix");
            }

            var sourcePrefix = TitleSourceIdPrefix.Match(title);
            if (sourcePrefix.Success)
            {
                title = title[sourcePrefix.Length..].Replace('_', ' ').TrimStart();
                log.Add("source_id_underscore_prefix");
            }
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            var splitCount = ArtistTitleSplit.Matches(title).Count;
            if (splitCount > 1)
            {
                ambiguousDash = true;
            }

            if (splitCount >= 1)
            {
                var parts = title.Split(new[] { " - " }, 2, StringSplitOptions.None);
                if (parts.Length == 2)
                {
                    var left = parts[0].Trim();
                    var right = parts[1].Trim();
                    var leftHasArtistTokens = ContainsArtistTokens(left);

                    if (string.IsNullOrWhiteSpace(artist) || leftHasArtistTokens)
                    {
                        if (!string.IsNullOrWhiteSpace(artist) && !EqualsLoose(artist, left))
                        {
                            overwriteArtistAttempt = true;
                        }
                        else
                        {
                            artist = left;
                            title = right;
                            artistTitleSplitApplied = true;
                            log.Add("artist_title_split");
                        }
                    }
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            title = StripJunkSuffix(title, djTags, log);
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            var match = BpmExplicit.Match(title);
            if (match.Success)
            {
                var candidate = ParseBpm(match.Groups["bpm"].Value);
                if (candidate.HasValue)
                {
                    bpm ??= candidate;
                    title = BpmExplicit.Replace(title, "").Trim();
                    explicitTokenApplied = true;
                    log.Add("title_bpm_explicit");
                }
            }

            match = BpmBracketOnly.Match(title);
            if (match.Success)
            {
                var candidate = ParseBpm(match.Groups["bpm"].Value);
                if (candidate.HasValue)
                {
                    bpm ??= candidate;
                    title = BpmBracketOnly.Replace(title, "").Trim();
                    explicitTokenApplied = true;
                    log.Add("title_bpm_bracket");
                }
            }

            match = KeyExplicit.Match(title);
            if (match.Success)
            {
                musicalKey ??= match.Groups["key"].Value;
                title = KeyExplicit.Replace(title, "").Trim();
                explicitTokenApplied = true;
                log.Add("title_key_explicit");
            }
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            if (TryExtractBracketKey(title, out var cleanedTitle, out var extractedKey, out var usedLogTag))
            {
                title = cleanedTitle;
                musicalKey ??= extractedKey;
                explicitTokenApplied = true;
                log.Add(usedLogTag);
            }
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            title = ExtractMixTags(title, djTags, log);
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            var featureMatch = FeaturePattern.Match(title);
            while (featureMatch.Success)
            {
                ExtractFeatures(featureMatch.Groups["names"].Value, features, log, "feature_paren");
                title = title.Remove(featureMatch.Index, featureMatch.Length).Trim();
                featureMatch = FeaturePattern.Match(title);
            }

            featureMatch = FeatureInlinePattern.Match(title);
            if (featureMatch.Success)
            {
                ExtractFeatures(featureMatch.Groups["names"].Value, features, log, "feature_inline");
                title = title[..featureMatch.Index].Trim();
            }
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            title = CleanupSeparators(title);
        }

        var confidence = 0.50;
        if (keyBpmApplied)
        {
            confidence += 0.40;
        }

        if (bracketPerformerApplied)
        {
            confidence += 0.30;
        }

        if (artistTitleSplitApplied)
        {
            confidence += 0.25;
        }

        if (explicitTokenApplied)
        {
            confidence += 0.15;
        }

        if (ambiguousDash)
        {
            confidence -= 0.20;
        }

        if (overwriteArtistAttempt)
        {
            confidence -= 0.30;
        }

        confidence = Math.Clamp(confidence, 0.0, 1.0);

        var featuresJson = JsonSerializer.Serialize(features.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), JsonOptions);
        var djTagsJson = JsonSerializer.Serialize(djTags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), JsonOptions);
        var logJson = JsonSerializer.Serialize(log.ToArray(), JsonOptions);

        return new CleanResult(title, artist, musicalKey, bpm, featuresJson, djTagsJson, logJson, confidence);
    }

    private static string? Normalize(string? value, List<string> log)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Normalize(NormalizationForm.FormKC);
        normalized = DashNormalize.Replace(normalized, "-");
        normalized = MultiWhitespace.Replace(normalized.Trim(), " ");
        if (!string.Equals(normalized, value, StringComparison.Ordinal))
        {
            log.Add("normalized_text");
        }

        return normalized;
    }

    private static string StripFileExtension(string title, List<string> log)
    {
        var updated = FileExtensionSuffix.Replace(title, "");
        if (!string.Equals(updated, title, StringComparison.Ordinal))
        {
            log.Add("title_strip_extension");
        }

        return updated.Trim();
    }

    private static string StripAudioVisualizer(string title, List<string> log)
    {
        var updated = AudioVisualizerSuffix.Replace(title, "").Trim();
        if (!string.Equals(updated, title, StringComparison.Ordinal))
        {
            log.Add("title_strip_visualizer");
        }

        return updated;
    }

    private static double? ParseBpm(string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return null;
        }

        return parsed is >= 60 and <= 220 ? parsed : null;
    }

    private static bool TryExtractBracketKey(string title, out string cleaned, out string key, out string logTag)
    {
        cleaned = title;
        key = string.Empty;
        logTag = string.Empty;

        var match = Regex.Match(title, @"\((?<content>[^)]*)\)|\[(?<content>[^\]]*)\]");
        if (!match.Success)
        {
            return false;
        }

        var content = match.Groups["content"].Value;
        var keyMatch = Regex.Match(content, @"\b(?<key>(1[0-2]|[1-9])[AB])\b");
        if (!keyMatch.Success)
        {
            return false;
        }

        var hasContext = content.Contains("bpm", StringComparison.OrdinalIgnoreCase)
                         || DjTagTokens.Any(tag => content.Contains(tag, StringComparison.OrdinalIgnoreCase))
                         || MixTokens.Any(tag => content.Contains(tag, StringComparison.OrdinalIgnoreCase));
        if (!hasContext)
        {
            return false;
        }

        key = keyMatch.Groups["key"].Value;
        cleaned = title.Remove(match.Index, match.Length).Trim();
        logTag = "title_key_bracket";
        return true;
    }

    private static string ExtractMixTags(string title, List<string> djTags, List<string> log)
    {
        var updated = title;
        var suffixMatch = Regex.Match(updated, @"\((?<content>[^)]*)\)$");
        if (suffixMatch.Success && ContainsMixTokens(suffixMatch.Groups["content"].Value))
        {
            var content = suffixMatch.Groups["content"].Value.Trim();
            djTags.Add(content);
            log.Add("mix_suffix_paren");
            updated = updated[..suffixMatch.Index].Trim();
        }

        suffixMatch = Regex.Match(updated, @"\[(?<content>[^\]]*)\]$");
        if (suffixMatch.Success && ContainsMixTokens(suffixMatch.Groups["content"].Value))
        {
            var content = suffixMatch.Groups["content"].Value.Trim();
            djTags.Add(content);
            log.Add("mix_suffix_bracket");
            updated = updated[..suffixMatch.Index].Trim();
        }

        foreach (var token in DjTagTokens)
        {
            if (updated.EndsWith($" {token}", StringComparison.OrdinalIgnoreCase))
            {
                djTags.Add(token);
                log.Add("dj_tag_suffix");
                updated = updated[..^($" {token}".Length)].Trim();
                break;
            }
        }

        foreach (var suffix in VersionSuffixes)
        {
            if (updated.EndsWith($" {suffix}", StringComparison.OrdinalIgnoreCase))
            {
                djTags.Add(suffix);
                log.Add("mix_suffix");
                updated = updated[..^($" {suffix}".Length)].Trim();
                break;
            }
        }

        return updated;
    }

    private static string StripJunkSuffix(string title, List<string> djTags, List<string> log)
    {
        var parts = title.Split(new[] { " - " }, StringSplitOptions.None);
        if (parts.Length < 2)
        {
            return title;
        }

        var last = parts[^1].Trim();
        if (JunkSuffixTokens.Any(token => last.Contains(token, StringComparison.OrdinalIgnoreCase)))
        {
            djTags.Add(last);
            log.Add("title_junk_suffix");
            return string.Join(" - ", parts.Take(parts.Length - 1)).Trim();
        }

        return title;
    }

    private static bool ContainsMixTokens(string value)
    {
        return MixTokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase))
               || DjTagTokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static void ExtractFeatures(string rawNames, List<string> features, List<string> log, string logTag)
    {
        var split = rawNames.Split(new[] { ",", "&", "＆", "x", "×" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var entry in split)
        {
            var trimmed = entry.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                features.Add(trimmed);
            }
        }

        log.Add(logTag);
    }

    private static string CleanupSeparators(string value)
    {
        var cleaned = value;
        cleaned = cleaned.Replace("()", "", StringComparison.Ordinal);
        cleaned = cleaned.Replace("[]", "", StringComparison.Ordinal);
        cleaned = cleaned.Replace("〔〕", "", StringComparison.Ordinal);

        cleaned = Regex.Replace(cleaned, @"\s*-\s*-+\s*", " - ");
        cleaned = Regex.Replace(cleaned, @"\|\|+", "|");
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ");
        cleaned = cleaned.Trim(' ', '-', '|', '_', '.');
        return cleaned.Trim();
    }

    private static bool EqualsLoose(string a, string b)
    {
        var left = MultiWhitespace.Replace(a.Trim(), " ").ToLowerInvariant();
        var right = MultiWhitespace.Replace(b.Trim(), " ").ToLowerInvariant();
        return string.Equals(left, right, StringComparison.Ordinal);
    }

    private static bool ContainsArtistTokens(string value)
    {
        var lower = value.ToLowerInvariant();
        return lower.Contains("&") || lower.Contains(" x ") || lower.Contains("×") || lower.Contains("feat") || lower.Contains("ft");
    }
}
