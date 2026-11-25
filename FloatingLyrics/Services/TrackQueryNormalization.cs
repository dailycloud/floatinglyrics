using System.Text.RegularExpressions;

namespace FloatingLyrics.Services;

internal sealed record LyricsQueryCandidate(
    string Title,
    string Artist,
    bool IsNormalized,
    bool HasSpeedModifier);

internal static class TrackQueryNormalization
{
    private static readonly string[] TitleSplitSeparators = [" - ", " \u2013 ", " \u2014 ", " | "];
    private static readonly string[] SpeedMarkers =
    [
        "slowed",
        "slow + reverb",
        "slowed + reverb",
        "reverb",
        "sped up",
        "speed up",
        "speedup",
        "nightcore",
        "8d",
        "bass boosted",
        "pitch"
    ];
    private static readonly string[] NoiseMarkers =
    [
        "official video",
        "official music video",
        "official audio",
        "audio",
        "lyric video",
        "lyrics",
        "video",
        "visualizer",
        "mv",
        "prod by",
        "prod.",
        "remaster",
        "extended",
        "edit"
    ];

    private static readonly Regex BracketGroupRegex = new(
        @"[\(\[\{][^()\[\]\{\}]{1,120}[\)\]\}]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MultiSpaceRegex = new(
        @"\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TopicSuffixRegex = new(
        @"\s*[-–—]\s*topics?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> ExcludedArtists = new(StringComparer.OrdinalIgnoreCase)
    {
        "dj",
        "dj x",
        "ai dj",
        "spotify",
        "spotify dj"
    };

    private static readonly HashSet<string> ExcludedTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "dj x",
        "ai dj",
        "spotify dj"
    };

    public static bool ShouldSkipLyricsSearch(TrackInfo track)
    {
        var artist = NormalizeSpace(track.Artist);
        var title = NormalizeSpace(track.Title);

        if (ExcludedArtists.Contains(artist))
        {
            return true;
        }

        if (ExcludedTitles.Contains(title))
        {
            return true;
        }

        return title.StartsWith("dj x - ", StringComparison.OrdinalIgnoreCase) ||
               title.StartsWith("dj x:", StringComparison.OrdinalIgnoreCase);
    }

    public static string StripArtistTopicSuffix(string value)
    {
        var normalized = NormalizeSpace(value);
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        return NormalizeSpace(TopicSuffixRegex.Replace(normalized, string.Empty));
    }

    public static IReadOnlyList<LyricsQueryCandidate> BuildCandidates(TrackInfo track)
    {
        var rawTitle = NormalizeSpace(track.Title);
        var rawArtist = NormalizeSpace(track.Artist);
        var hasSpeedModifier = ContainsAnyMarker(rawTitle, SpeedMarkers) || ContainsAnyMarker(rawArtist, SpeedMarkers);

        var result = new List<LyricsQueryCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddCandidate(string title, string artist, bool isNormalized, bool hasSpeed)
        {
            var normalizedTitle = NormalizeSpace(title);
            var normalizedArtist = NormalizeSpace(artist);
            if (string.IsNullOrWhiteSpace(normalizedTitle))
            {
                return;
            }

            var key = $"{normalizedArtist}\u001f{normalizedTitle}";
            if (!seen.Add(key))
            {
                return;
            }

            result.Add(new LyricsQueryCandidate(
                normalizedTitle,
                normalizedArtist,
                isNormalized,
                hasSpeed));
        }

        AddCandidate(rawTitle, rawArtist, isNormalized: false, hasSpeedModifier);

        var cleanedTitle = CleanTitle(rawTitle, removeSpeedMarkers: true);
        var cleanedArtist = CleanArtist(rawArtist);
        var normalizedChanged =
            !string.Equals(cleanedTitle, rawTitle, StringComparison.Ordinal) ||
            !string.Equals(cleanedArtist, rawArtist, StringComparison.Ordinal);
        AddCandidate(cleanedTitle, cleanedArtist, normalizedChanged, hasSpeedModifier);

        if (TryExtractArtistTitle(rawTitle, out var extractedArtist, out var extractedTitle))
        {
            var titleFromContainer = CleanTitle(extractedTitle, removeSpeedMarkers: true);
            var artistFromContainer = CleanArtist(extractedArtist);
            var extractedHasSpeed = hasSpeedModifier || ContainsAnyMarker(extractedTitle, SpeedMarkers);

            AddCandidate(titleFromContainer, artistFromContainer, isNormalized: true, extractedHasSpeed);
            if (IsMissingArtist(cleanedArtist))
            {
                AddCandidate(titleFromContainer, artistFromContainer, isNormalized: true, extractedHasSpeed);
            }
            else
            {
                AddCandidate(titleFromContainer, cleanedArtist, isNormalized: true, extractedHasSpeed);
            }
        }

        if (result.Count == 0)
        {
            AddCandidate(rawTitle, rawArtist, isNormalized: false, hasSpeedModifier);
        }

        return result;
    }

    private static string CleanTitle(string value, bool removeSpeedMarkers)
    {
        var cleaned = NormalizeSpace(value);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return string.Empty;
        }

        cleaned = BracketGroupRegex.Replace(
            cleaned,
            match => ShouldDropBracketSegment(match.Value, removeSpeedMarkers) ? " " : match.Value);

        cleaned = TrimPipeNoise(cleaned, removeSpeedMarkers);
        cleaned = RemoveMarkers(cleaned, NoiseMarkers);
        if (removeSpeedMarkers)
        {
            cleaned = RemoveMarkers(cleaned, SpeedMarkers);
        }

        cleaned = TrimOuterSeparators(cleaned);
        return NormalizeSpace(cleaned);
    }

    private static string CleanArtist(string value)
    {
        var cleaned = StripArtistTopicSuffix(value);
        cleaned = RemoveMarkers(cleaned, NoiseMarkers);
        cleaned = TrimOuterSeparators(cleaned);
        return NormalizeSpace(cleaned);
    }

    private static string RemoveMarkers(string value, IReadOnlyList<string> markers)
    {
        var result = value;
        foreach (var marker in markers)
        {
            if (string.IsNullOrWhiteSpace(marker))
            {
                continue;
            }

            var regex = new Regex(
                Regex.Escape(marker),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            result = regex.Replace(result, " ");
        }

        return result;
    }

    private static bool ShouldDropBracketSegment(string segment, bool removeSpeedMarkers)
    {
        if (ContainsAnyMarker(segment, NoiseMarkers))
        {
            return true;
        }

        return removeSpeedMarkers && ContainsAnyMarker(segment, SpeedMarkers);
    }

    private static string TrimPipeNoise(string value, bool removeSpeedMarkers)
    {
        var separatorIndex = value.IndexOf(" | ", StringComparison.Ordinal);
        if (separatorIndex <= 0)
        {
            return value;
        }

        var right = value[(separatorIndex + 3)..];
        var shouldTrim = ContainsAnyMarker(right, NoiseMarkers) ||
            (removeSpeedMarkers && ContainsAnyMarker(right, SpeedMarkers));
        return shouldTrim ? value[..separatorIndex] : value;
    }

    private static bool TryExtractArtistTitle(string title, out string artist, out string songTitle)
    {
        artist = string.Empty;
        songTitle = string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var normalized = NormalizeSpace(title);
        var bestIndex = -1;
        var bestSeparator = string.Empty;
        foreach (var separator in TitleSplitSeparators)
        {
            var index = normalized.IndexOf(separator, StringComparison.Ordinal);
            if (index <= 0)
            {
                continue;
            }

            if (bestIndex < 0 || index < bestIndex)
            {
                bestIndex = index;
                bestSeparator = separator;
            }
        }

        if (bestIndex <= 0 || string.IsNullOrEmpty(bestSeparator))
        {
            return false;
        }

        var left = NormalizeSpace(normalized[..bestIndex]);
        var right = NormalizeSpace(normalized[(bestIndex + bestSeparator.Length)..]);
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        artist = left;
        songTitle = right;
        return true;
    }

    private static string TrimOuterSeparators(string value)
    {
        return value.Trim(' ', '-', '\u2013', '\u2014', '|', ':', '/', '\\', '_', '.');
    }

    private static string NormalizeSpace(string value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        return MultiSpaceRegex.Replace(trimmed, " ").Trim();
    }

    private static bool ContainsAnyMarker(string text, IReadOnlyList<string> markers)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var marker in markers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsMissingArtist(string artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
        {
            return true;
        }

        return artist.Equals("unknown artist", StringComparison.OrdinalIgnoreCase) ||
            artist.Equals("various artists", StringComparison.OrdinalIgnoreCase);
    }
}
