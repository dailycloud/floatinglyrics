namespace FloatingLyrics.Services;

public sealed class NoOpLyricsProvider : ILyricsProvider
{
    public Task<LyricsResult?> GetLyricsAsync(TrackInfo track, CancellationToken cancellationToken)
    {
        return Task.FromResult<LyricsResult?>(null);
    }
}

