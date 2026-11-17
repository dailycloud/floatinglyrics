using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FloatingLyrics.Services;

/// <summary>
/// Word-by-word (syllable) and line-synced lyrics from the Spicy Lyrics API.
///
/// 1. <c>GET /v1/tracks/search?title=&amp;artist=&amp;duration=</c> resolves the Spotify track id
///    (SMTC only gives title/artist); only an "exact" tier match, or a same-length match, is used,
///    since another version of a song has other timings.
/// 2. <c>GET /v1/lyrics/{spotifyId}</c> returns <c>Syllable</c>, <c>Line</c> or <c>Static</c> bodies.
///
/// The API key is read at call time (settings.json or the FLOATING_LYRICS_SPICY_KEY variable),
/// never compiled in. The API rate-limits per window; on 429 the provider rests until the reset.
/// </summary>
public sealed class SpicyLyricsProvider(HttpClient httpClient, Func<string?> apiKeyProvider) : ILyricsProvider
{
    private const string BaseUrl = "https://api.spicylyrics.org/v1";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    private DateTimeOffset restUntilUtc = DateTimeOffset.MinValue;

    public async Task<LyricsResult?> GetLyricsAsync(TrackInfo track, CancellationToken cancellationToken)
    {
        var key = apiKeyProvider()?.Trim();
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(track.Title) || DateTimeOffset.UtcNow < restUntilUtc)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            var trackId = await ResolveSpotifyIdAsync(track, key, timeout.Token);
            if (trackId is null && track.Duration is not null)
            {
                // SMTC can still report the previous song's length right after a change; a wrong
                // duration hides the exact match, so retry by title/artist only.
                trackId = await ResolveSpotifyIdAsync(track with { Duration = null }, key, timeout.Token);
            }
            if (trackId is null)
            {
                DebugTrace.Write($"Spicy: no exact match for '{track.Artist} - {track.Title}'");
                return null;
            }

            var result = await FetchLyricsAsync(trackId, key, timeout.Token);
            DebugTrace.Write($"Spicy: {trackId} -> {(result is null ? "none" : $"{result.Lines.Count} lines, words={result.Lines.Any(l => l.HasWordTiming)}")}");
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            DebugTrace.Write("Spicy: request timed out");
            return null;
        }
        catch (HttpRequestException ex)
        {
            DebugTrace.Write($"Spicy: network error {ex.Message}");
            return null;
        }
    }

    private async Task<string?> ResolveSpotifyIdAsync(TrackInfo track, string key, CancellationToken cancellationToken)
    {
        var query = new StringBuilder($"{BaseUrl}/tracks/search?title={Uri.EscapeDataString(Trim(track.Title))}");
        if (!string.IsNullOrWhiteSpace(track.Artist))
        {
            query.Append("&artist=").Append(Uri.EscapeDataString(Trim(track.Artist)));
        }

        if (track.Duration is { TotalSeconds: > 1 } duration)
        {
            query.Append("&duration=").Append(duration.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        }

        using var document = await GetJsonAsync(query.ToString(), key, cancellationToken);
        if (document is null ||
            !document.RootElement.TryGetProperty("Body", out var body) ||
            !body.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? sameLength = null;
        foreach (var result in results.EnumerateArray())
        {
            var id = GetString(result, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            if (string.Equals(GetString(result, "tier"), "exact", StringComparison.OrdinalIgnoreCase))
            {
                return id;
            }

            if (sameLength is null &&
                track.Duration is { } trackDuration &&
                result.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number &&
                Math.Abs(d.GetDouble() - trackDuration.TotalSeconds) <= 2 &&
                TitlesMatch(GetString(result, "title"), track.Title))
            {
                sameLength = id;
            }
        }

        return sameLength;
    }

    private async Task<LyricsResult?> FetchLyricsAsync(string trackId, string key, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync($"{BaseUrl}/lyrics/{Uri.EscapeDataString(trackId)}", key, cancellationToken);
        if (document is null ||
            !document.RootElement.TryGetProperty("Body", out var body) ||
            body.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var type = GetString(body, "Type") ?? string.Empty;
        var lines = new List<LyricLine>();
        if (body.TryGetProperty("Content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                if (string.Equals(GetString(item, "Type"), "Interlude", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var line = item.TryGetProperty("Lead", out var lead) && lead.ValueKind == JsonValueKind.Object
                    ? ParseSyllableLine(lead, item)
                    : ParseTimedLine(item);
                if (line is not null)
                {
                    lines.Add(line);
                }
            }
        }
        else if (body.TryGetProperty("Lines", out var staticLines) && staticLines.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in staticLines.EnumerateArray())
            {
                var text = GetString(item, "Text");
                if (!string.IsNullOrWhiteSpace(text))
                {
                    lines.Add(new LyricLine(text.Trim(), null));
                }
            }
        }

        if (lines.Count == 0)
        {
            return null;
        }

        var synced = lines.Any(l => l.Timestamp.HasValue);
        var label = type.Equals("Syllable", StringComparison.OrdinalIgnoreCase)
            ? "Spicy Lyrics · по словам"
            : "Spicy Lyrics";
        return new LyricsResult(lines, label, synced);
    }

    /// <summary>Builds the line text from syllables and records each syllable's character range.</summary>
    private static LyricLine? ParseSyllableLine(JsonElement lead, JsonElement item)
    {
        if (!lead.TryGetProperty("Syllables", out var syllables) || syllables.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var text = new StringBuilder();
        var parts = new List<LyricSyllable>();
        AppendSyllables(text, parts, syllables);
        if (parts.Count == 0 || string.IsNullOrWhiteSpace(text.ToString()))
        {
            return null;
        }

        var lineStart = TryGetSeconds(lead, "StartTime", out var s) ? s : parts[0].Start;
        var lineEnd = TryGetSeconds(lead, "EndTime", out var e) ? e : parts[^1].End;

        // Background vocals: a separate, smaller line under the lead, with its own timing.
        var backgroundText = new StringBuilder();
        var backgroundParts = new List<LyricSyllable>();
        if (item.TryGetProperty("Background", out var backgrounds) && backgrounds.ValueKind == JsonValueKind.Array)
        {
            foreach (var group in backgrounds.EnumerateArray())
            {
                if (group.TryGetProperty("Syllables", out var bgSyllables) && bgSyllables.ValueKind == JsonValueKind.Array)
                {
                    if (backgroundText.Length > 0)
                    {
                        backgroundText.Append(' ');
                    }

                    AppendSyllables(backgroundText, backgroundParts, bgSyllables);
                }
            }
        }

        // The line is sung until its lead or background vocals end, whichever is later.
        if (backgroundParts.Count > 0 && backgroundParts[^1].End > lineEnd)
        {
            lineEnd = backgroundParts[^1].End;
        }

        var opposite = item.TryGetProperty("OppositeAligned", out var oa) && oa.ValueKind == JsonValueKind.True;
        return new LyricLine(
            text.ToString(),
            lineStart,
            parts,
            lineEnd,
            backgroundParts.Count > 0 ? backgroundText.ToString() : null,
            backgroundParts.Count > 0 ? backgroundParts : null,
            opposite);
    }

    private static void AppendSyllables(StringBuilder text, List<LyricSyllable> parts, JsonElement syllables)
    {
        var joinWithPrevious = true;
        foreach (var syllable in syllables.EnumerateArray())
        {
            var piece = GetString(syllable, "Text");
            if (string.IsNullOrEmpty(piece) ||
                !TryGetSeconds(syllable, "StartTime", out var start) ||
                !TryGetSeconds(syllable, "EndTime", out var end))
            {
                continue;
            }

            // "IsPartOfWord" says this syllable continues into the NEXT one (no space after it).
            if (text.Length > 0 && !joinWithPrevious)
            {
                text.Append(' ');
            }

            parts.Add(new LyricSyllable(text.Length, piece.Length, start, end < start ? start : end));
            text.Append(piece);
            joinWithPrevious = syllable.TryGetProperty("IsPartOfWord", out var partOf) &&
                partOf.ValueKind == JsonValueKind.True;
        }
    }

    private static LyricLine? ParseTimedLine(JsonElement item)
    {
        var text = GetString(item, "Text");
        if (string.IsNullOrWhiteSpace(text) || !TryGetSeconds(item, "StartTime", out var start))
        {
            return null;
        }

        TimeSpan? end = TryGetSeconds(item, "EndTime", out var e) ? e : null;
        var opposite = item.TryGetProperty("OppositeAligned", out var oa) && oa.ValueKind == JsonValueKind.True;
        return new LyricLine(text.Trim(), start, null, end, null, null, opposite);
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, string key, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var reset = response.Headers.TryGetValues("RateLimit-Reset", out var values) &&
                int.TryParse(values.FirstOrDefault(), out var seconds)
                    ? seconds
                    : 60;
            restUntilUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(reset, 5, 600));
            DebugTrace.Write($"Spicy: rate limited for {reset}s");
            return null;
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            DebugTrace.Write($"Spicy: key rejected (HTTP {(int)response.StatusCode})");
            restUntilUtc = DateTimeOffset.UtcNow.AddMinutes(10);
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGetSeconds(JsonElement element, string name, out TimeSpan value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var raw) ||
            raw.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        value = TimeSpan.FromSeconds(Math.Max(0, raw.GetDouble()));
        return true;
    }

    private static string Trim(string value) => value.Length > 200 ? value[..200] : value;

    private static bool TitlesMatch(string? candidate, string title)
    {
        static string Normalize(string? value) => new string((value ?? string.Empty)
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());

        var a = Normalize(candidate);
        var b = Normalize(title);
        return a.Length > 0 && b.Length > 0 && (a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal));
    }
}
