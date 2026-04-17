namespace FloatingLyrics.Services;

public sealed class FallbackLyricsProvider : ILyricsProvider
{
    public Task<LyricsResult?> GetLyricsAsync(TrackInfo track, CancellationToken cancellationToken)
    {
        LyricsResult result = new(
            [
                new LyricLine($"Now playing: {track.Title}", null),
                new LyricLine($"Artist: {track.Artist}", null),
                new LyricLine("No online lyrics found yet.", null),
                new LyricLine("Try local .lrc files in the Lyrics folder.", null)
            ],
            "Built-in fallback",
            false);

        return Task.FromResult<LyricsResult?>(result);
    }
}
