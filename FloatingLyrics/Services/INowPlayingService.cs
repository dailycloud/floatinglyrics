namespace FloatingLyrics.Services;

public interface INowPlayingService
{
    Task<PlaybackSnapshot?> GetPlaybackSnapshotAsync(CancellationToken cancellationToken);
}
