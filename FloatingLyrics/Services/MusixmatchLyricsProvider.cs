using System.Text.Json;

namespace FloatingLyrics.Services;

public sealed class MusixmatchLyricsProvider(HttpClient httpClient) : ILyricsProvider
{
    private const string BaseUrl = "https://api.musixmatch.com/ws/1.1";
    private readonly HttpClient httpClient = httpClient;
    private readonly string? apiKey = Environment.GetEnvironmentVariable("MUSIXMATCH_API_KEY");

    public async Task<LyricsResult?> GetLyricsAsync(TrackInfo track, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var candidates = TrackQueryNormalization.BuildCandidates(track);
        foreach (var candidate in candidates)
        {
            var synced = await TryGetSyncedLyricsAsync(candidate, cancellationToken);
            if (synced.Count > 0)
            {
                var source = candidate.IsNormalized
                    ? "Musixmatch subtitle (normalized query)"
                    : "Musixmatch subtitle";
                return new LyricsResult(synced, source, true);
            }

            var plainTextLines = await TryGetPlainLyricsAsync(candidate, cancellationToken);
            if (plainTextLines.Count > 0)
            {
                var source = candidate.IsNormalized
                    ? "Musixmatch lyrics (normalized query)"
                    : "Musixmatch lyrics";
                return new LyricsResult(plainTextLines, source, false);
            }
        }

        return null;
    }

    private async Task<List<LyricLine>> TryGetSyncedLyricsAsync(LyricsQueryCandidate query, CancellationToken cancellationToken)
    {
        using var document = await FetchJsonAsync("matcher.subtitle.get", query, cancellationToken);
        if (document is null || !IsSuccessResponse(document.RootElement))
        {
            return [];
        }

        var subtitle = TryGetString(document.RootElement, "message", "body", "subtitle", "subtitle_body");
        if (string.IsNullOrWhiteSpace(subtitle))
        {
            return [];
        }

        return LyricsParsing.ParseLrc(subtitle);
    }

    private async Task<List<LyricLine>> TryGetPlainLyricsAsync(LyricsQueryCandidate query, CancellationToken cancellationToken)
    {
        using var document = await FetchJsonAsync("matcher.lyrics.get", query, cancellationToken);
        if (document is null || !IsSuccessResponse(document.RootElement))
        {
            return [];
        }

        var lyrics = TryGetString(document.RootElement, "message", "body", "lyrics", "lyrics_body");
        if (string.IsNullOrWhiteSpace(lyrics))
        {
            return [];
        }

        return LyricsParsing.ParsePlainLyrics(lyrics);
    }

    private async Task<JsonDocument?> FetchJsonAsync(
        string endpoint,
        LyricsQueryCandidate query,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var requestUri =
            $"{BaseUrl}/{endpoint}" +
            $"?q_track={Uri.EscapeDataString(query.Title)}" +
            $"&q_artist={Uri.EscapeDataString(query.Artist)}" +
            $"&apikey={Uri.EscapeDataString(apiKey)}";

        using var response = await httpClient.GetAsync(requestUri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
    }

    private static bool IsSuccessResponse(JsonElement root)
    {
        if (!TryGetProperty(root, out var header, "message", "header"))
        {
            return false;
        }

        if (!header.TryGetProperty("status_code", out var statusCode))
        {
            return false;
        }

        return statusCode.ValueKind == JsonValueKind.Number && statusCode.GetInt32() == 200;
    }

    private static string? TryGetString(JsonElement root, params string[] path)
    {
        if (!TryGetProperty(root, out var value, path))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static bool TryGetProperty(JsonElement root, out JsonElement value, params string[] path)
    {
        value = root;
        foreach (var segment in path)
        {
            if (!value.TryGetProperty(segment, out value))
            {
                return false;
            }
        }

        return true;
    }
}
