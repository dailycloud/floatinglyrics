namespace FloatingLyrics.Services;

public sealed class DemoNowPlayingService : INowPlayingService
{
    private static readonly TrackInfo[] Tracks =
    [
        new("Nightcall", "Kavinsky"),
        new("Blinding Lights", "The Weeknd"),
        new("Midnight City", "M83"),
        new("Take On Me", "a-ha")
    ];

    private readonly TimeSpan trackDuration = TimeSpan.FromMinutes(3);
    private int index;
    private DateTimeOffset trackStartedAt = DateTimeOffset.UtcNow;

    public Task<PlaybackSnapshot?> GetPlaybackSnapshotAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        while (now - trackStartedAt >= trackDuration)
        {
            trackStartedAt += trackDuration;
            index = (index + 1) % Tracks.Length;
        }

        var selected = Tracks[index] with { Duration = trackDuration };
        var position = now - trackStartedAt;
        var snapshot = new PlaybackSnapshot(selected, position, true);
        return Task.FromResult<PlaybackSnapshot?>(snapshot);
    }
}
