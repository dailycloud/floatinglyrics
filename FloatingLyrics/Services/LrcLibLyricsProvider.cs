using System.Globalization;
using System.Text.Json;

namespace FloatingLyrics.Services;

public sealed class LrcLibLyricsProvider(HttpClient httpClient) : ILyricsProvider
{
    private const string BaseUrl = "https://lrclib.net";
    private readonly HttpClient httpClient = httpClient;

    public async Task<LyricsResult?> GetLyricsAsync(TrackInfo track, CancellationToken cancellationToken)
    {
        var candidates = TrackQueryNormalization.BuildCandidates(track);
        foreach (var candidate in candidates)
        {
            var direct = await TryRequestSingleAsync("api/get", candidate, track, cancellationToken);
            if (direct is not null)
            {
                return direct;
            }

            var searched = await TrySearchAsync(candidate, track, cancellationToken);
            if (searched is not null)
            {
                return searched;
            }
        }

        return null;
    }

    private async Task<LyricsResult?> TrySearchAsync(
        LyricsQueryCandidate query,
        TrackInfo track,
        CancellationToken cancellationToken)
    {
        var requestUri =
            $"{BaseUrl}/api/search" +
            $"?track_name={Uri.EscapeDataString(query.Title)}" +
            $"&artist_name={Uri.EscapeDataString(query.Artist)}";

        using var response = await httpClient.GetAsync(requestUri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var candidateResult in document.RootElement.EnumerateArray())
        {
            var parsed = ParseLyrics(candidateResult, query, track);
            if (parsed is not null)
            {
                return parsed;
            }
        }

        return null;
    }

    private async Task<LyricsResult?> TryRequestSingleAsync(
        string endpoint,
        LyricsQueryCandidate query,
        TrackInfo track,
        CancellationToken cancellationToken)
    {
        var requestUri =
            $"{BaseUrl}/{endpoint}" +
            $"?track_name={Uri.EscapeDataString(query.Title)}" +
            $"&artist_name={Uri.EscapeDataString(query.Artist)}";

        using var response = await httpClient.GetAsync(requestUri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        return ParseLyrics(document.RootElement, query, track);
    }

    private static LyricsResult? ParseLyrics(JsonElement root, LyricsQueryCandidate query, TrackInfo track)
    {
        var sourceDuration = TryGetDuration(root);
        var syncedLyrics = TryGetString(root, "syncedLyrics");
        if (!string.IsNullOrWhiteSpace(syncedLyrics))
        {
            var lines = LyricsParsing.ParseLrc(syncedLyrics);
            if (lines.Count > 0)
            {
                var scaledLines = MaybeScaleSyncedLines(lines, query, track.Duration, sourceDuration, out var wasScaled);
                var source = query.IsNormalized
                    ? "LRCLIB synced (normalized query)"
                    : "LRCLIB synced";
                if (wasScaled)
                {
                    source += " scaled";
                }

                return new LyricsResult(scaledLines, source, true, sourceDuration);
            }
        }

        var plainLyrics = TryGetString(root, "plainLyrics");
        if (!string.IsNullOrWhiteSpace(plainLyrics))
        {
            var lines = LyricsParsing.ParsePlainLyrics(plainLyrics);
            if (lines.Count > 0)
            {
                var source = query.IsNormalized
                    ? "LRCLIB plain (normalized query)"
                    : "LRCLIB plain";
                return new LyricsResult(lines, source, false, sourceDuration);
            }
        }

        return null;
    }

    private static IReadOnlyList<LyricLine> MaybeScaleSyncedLines(
        IReadOnlyList<LyricLine> lines,
        LyricsQueryCandidate query,
        TimeSpan? playbackDuration,
        TimeSpan? sourceDuration,
        out bool wasScaled)
    {
        wasScaled = false;
        if (!playbackDuration.HasValue || !sourceDuration.HasValue)
        {
            return lines;
        }

        var sourceSeconds = sourceDuration.Value.TotalSeconds;
        var playbackSeconds = playbackDuration.Value.TotalSeconds;
        if (sourceSeconds < 30 || playbackSeconds < 30)
        {
            return lines;
        }

        var ratio = playbackSeconds / sourceSeconds;
        if (!double.IsFinite(ratio))
        {
            return lines;
        }

        var deviation = Math.Abs(ratio - 1.0);
        var shouldScale = query.HasSpeedModifier
            ? deviation >= 0.015
            : deviation >= 0.18;
        if (!shouldScale)
        {
            return lines;
        }

        ratio = Math.Clamp(ratio, 0.5, 1.8);
        var scaled = new List<LyricLine>(lines.Count);
        TimeSpan? previousTimestamp = null;
        foreach (var line in lines)
        {
            if (!line.Timestamp.HasValue)
            {
                scaled.Add(line);
                continue;
            }

            var scaledTimestamp = TimeSpan.FromMilliseconds(line.Timestamp.Value.TotalMilliseconds * ratio);
            if (previousTimestamp.HasValue && scaledTimestamp <= previousTimestamp.Value)
            {
                scaledTimestamp = previousTimestamp.Value + TimeSpan.FromMilliseconds(1);
            }

            scaled.Add(new LyricLine(line.Text, scaledTimestamp));
            previousTimestamp = scaledTimestamp;
        }

        wasScaled = true;
        return scaled;
    }

    private static TimeSpan? TryGetDuration(JsonElement root)
    {
        if (!root.TryGetProperty("duration", out var value))
        {
            return null;
        }

        double seconds = value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(
                value.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => 0
        };

        if (!double.IsFinite(seconds) || seconds <= 0)
        {
            return null;
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private static string? TryGetString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
