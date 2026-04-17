namespace FloatingLyrics.Services;

public sealed class NoOpNowPlayingService : INowPlayingService
{
    public Task<PlaybackSnapshot?> GetPlaybackSnapshotAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<PlaybackSnapshot?>(null);
    }
}

