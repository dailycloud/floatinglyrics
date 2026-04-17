namespace FloatingLyrics.Services;

public sealed class HybridNowPlayingService(
    INowPlayingService primary,
    INowPlayingService fallback) : INowPlayingService
{
    public async Task<PlaybackSnapshot?> GetPlaybackSnapshotAsync(CancellationToken cancellationToken)
    {
        var snapshot = await primary.GetPlaybackSnapshotAsync(cancellationToken);
        if (snapshot is not null)
        {
            return snapshot;
        }

        return await fallback.GetPlaybackSnapshotAsync(cancellationToken);
    }
}
