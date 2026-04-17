namespace FloatingLyrics.Services;

public sealed class LocalLrcLyricsProvider : ILyricsProvider
{
    private readonly string lyricsDirectory;

    public LocalLrcLyricsProvider()
    {
        lyricsDirectory = Path.Combine(AppContext.BaseDirectory, "Lyrics");
    }

    public async Task<LyricsResult?> GetLyricsAsync(TrackInfo track, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(lyricsDirectory))
        {
            return null;
        }

        foreach (var path in BuildCandidatePaths(track))
        {
            if (!File.Exists(path))
            {
                continue;
            }

            var content = await File.ReadAllTextAsync(path, cancellationToken);
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            var lines = LyricsParsing.ParseLrc(content);
            if (lines.Count > 0)
            {
                return new LyricsResult(lines, $"Local LRC ({Path.GetFileName(path)})", true);
            }

            var plain = LyricsParsing.ParsePlainLyrics(content);
            if (plain.Count > 0)
            {
                return new LyricsResult(plain, $"Local text ({Path.GetFileName(path)})", false);
            }
        }

        return null;
    }

    private IEnumerable<string> BuildCandidatePaths(TrackInfo track)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {};

        var rawTitle = Sanitize(track.Title);
        if (!string.IsNullOrWhiteSpace(rawTitle))
        {
            _ = candidates.Add($"{rawTitle}.lrc");
        }

        foreach (var query in TrackQueryNormalization.BuildCandidates(track))
        {
            var title = Sanitize(query.Title);
            var artist = Sanitize(query.Artist);
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            _ = candidates.Add($"{title}.lrc");
            if (!string.IsNullOrWhiteSpace(artist))
            {
                _ = candidates.Add($"{artist} - {title}.lrc");
                _ = candidates.Add($"{title} - {artist}.lrc");
            }
        }

        foreach (var fileName in candidates.Where(static x => !string.IsNullOrWhiteSpace(x)))
        {
            yield return Path.Combine(lyricsDirectory, fileName);
        }
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = string.Concat(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch));
        return sanitized.Trim();
    }
}
