namespace FloatingLyrics.Services;

public sealed class PlaybackVisibilityController
{
    private DateTimeOffset lastActivityAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset lastSnapshotAtUtc = DateTimeOffset.MinValue;
    private TimeSpan lastPosition = TimeSpan.Zero;
    private string lastTrackKey = string.Empty;
    private bool hasLastPosition;

    public bool IsVisible(
        PlaybackSnapshot? snapshot,
        bool hasActiveSession,
        bool hasTrackMetadata,
        string trackKey,
        bool hideOnPause,
        TimeSpan pauseHideDelay,
        DateTimeOffset nowUtc)
    {
        if (snapshot is not null)
        {
            lastSnapshotAtUtc = nowUtc;
            var trackChanged = !string.IsNullOrWhiteSpace(trackKey) &&
                !string.Equals(lastTrackKey, trackKey, StringComparison.Ordinal);

            if (trackChanged)
            {
                lastTrackKey = trackKey;
                lastPosition = snapshot.Position;
                hasLastPosition = true;
                lastActivityAtUtc = nowUtc;
            }

            if (hasLastPosition)
            {
                var deltaMs = Math.Abs((snapshot.Position - lastPosition).TotalMilliseconds);
                if (deltaMs >= 250)
                {
                    lastActivityAtUtc = nowUtc;
                }
            }

            lastPosition = snapshot.Position;
            hasLastPosition = true;

            if (snapshot.IsPlaying)
            {
                lastActivityAtUtc = nowUtc;
                return true;
            }

            // App can start when player is already paused.
            if (lastActivityAtUtc == DateTimeOffset.MinValue)
            {
                lastActivityAtUtc = nowUtc;
            }
        }

        if (!hasActiveSession && !hasTrackMetadata)
        {
            return false;
        }

        // Default behavior for unstable SMTC pause states:
        // keep overlay visible while a session/metadata exists.
        if (!hideOnPause)
        {
            return true;
        }

        // SMTC may briefly return null snapshots while session is still valid.
        if (snapshot is null &&
            lastSnapshotAtUtc != DateTimeOffset.MinValue &&
            nowUtc - lastSnapshotAtUtc <= TimeSpan.FromSeconds(8))
        {
            return true;
        }

        if (lastActivityAtUtc == DateTimeOffset.MinValue)
        {
            return false;
        }

        return nowUtc - lastActivityAtUtc <= pauseHideDelay;
    }

    public string GetDebugState(DateTimeOffset nowUtc)
    {
        var activityAgeMs = lastActivityAtUtc == DateTimeOffset.MinValue
            ? -1
            : Math.Round((nowUtc - lastActivityAtUtc).TotalMilliseconds, 0);
        var snapshotAgeMs = lastSnapshotAtUtc == DateTimeOffset.MinValue
            ? -1
            : Math.Round((nowUtc - lastSnapshotAtUtc).TotalMilliseconds, 0);

        return $"activityAgeMs={activityAgeMs}; snapshotAgeMs={snapshotAgeMs}; lastPosMs={Math.Round(lastPosition.TotalMilliseconds, 0)}; track={lastTrackKey}";
    }

    public void Reset()
    {
        lastActivityAtUtc = DateTimeOffset.MinValue;
        lastSnapshotAtUtc = DateTimeOffset.MinValue;
        lastPosition = TimeSpan.Zero;
        lastTrackKey = string.Empty;
        hasLastPosition = false;
    }
}
