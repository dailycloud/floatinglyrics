using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Windows.Media.Control;

namespace FloatingLyrics.Services;

public sealed class SmtcNowPlayingService : INowPlayingService
{
    private static readonly string ArtworkCacheFolder =
        Path.Combine(Path.GetTempPath(), "FloatingLyrics", "Artwork");
    private const int MaxArtworkCacheFiles = 280;
    private const long MaxArtworkCacheBytes = 96L * 1024 * 1024;
    private static readonly TimeSpan SnapshotFallbackLifetime = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan FreshTimelineWindow = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan StoppedSessionGracePeriod = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan MetadataRefreshInterval = TimeSpan.FromSeconds(1.8);
    private static readonly TimeSpan MetadataRetryThrottle = TimeSpan.FromMilliseconds(450);
    private static readonly string[] BrowserSessionMarkers = ["chrome", "msedge", "firefox", "opera", "browser"];
    private static readonly string[] MusicSessionMarkers =
    [
        "spotify",
        "music",
        "applemusic",
        "deezer",
        "tidal",
        "foobar",
        "vlc",
        "aimp",
        "winamp",
        "mpc",
        "yandexmusic"
    ];

    private Task<GlobalSystemMediaTransportControlsSessionManager>? managerTask;
    private PlaybackSnapshot? lastKnownSnapshot;
    private string? lastKnownSessionId;
    private DateTimeOffset lastKnownSnapshotAtUtc = DateTimeOffset.MinValue;
    private string? stoppedSessionId;
    private DateTimeOffset stoppedSessionSinceUtc = DateTimeOffset.MinValue;
    private string lastTraceState = string.Empty;
    private string? cachedMetadataSessionId;
    private TrackInfo? cachedTrackInfo;
    private DateTimeOffset cachedMetadataFetchedAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset lastMetadataAttemptAtUtc = DateTimeOffset.MinValue;
    private static int artworkCacheWriteCount;

    public async Task<PlaybackSnapshot?> GetPlaybackSnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            var manager = await GetManagerAsync();
            var sessions = SafeGetSessions(manager);
            var currentSession = SafeGetCurrentSession(manager);
            var selectedSession = ResolveSession(currentSession, sessions);

            if (selectedSession is null)
            {
                // If SMTC has no selected/usable session, return null immediately.
                // Returning detached cached snapshots here keeps stale UI visible.
                ClearStoppedSessionState();
                ClearLastKnownSnapshot();
                TraceSnapshotDecision(
                    "no-session",
                    sessions.Count,
                    null,
                    null,
                    null,
                    false,
                    null);
                return null;
            }

            var sessionId = selectedSession.SourceAppUserModelId ?? string.Empty;

            if (!TryGetPlaybackStatus(selectedSession, out var playbackStatus))
            {
                var cached = TryBuildSnapshotFromCache(sessionId, null, lastKnownSnapshot?.IsPlaying);
                TraceSnapshotDecision(
                    "status-unavailable",
                    sessions.Count,
                    sessionId,
                    null,
                    cached?.Track.Title,
                    cached is not null,
                    cached);
                return cached;
            }

            UpdateStoppedSessionState(sessionId, playbackStatus);

            var timeline = TryGetTimeline(selectedSession);
            var duration = GetTimelineDuration(timeline);
            var isPlaying = playbackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var position = timeline is not null
                ? CalculatePlaybackPosition(timeline, isPlaying)
                : lastKnownSnapshot?.Position ?? TimeSpan.Zero;

            if (playbackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed)
            {
                var cached = TryBuildSnapshotFromCache(sessionId, position, false);
                TraceSnapshotDecision(
                    "closed",
                    sessions.Count,
                    sessionId,
                    playbackStatus,
                    cached?.Track.Title,
                    cached is not null,
                    cached);
                if (cached is null)
                {
                    ClearLastKnownSnapshot();
                }

                return cached;
            }

            var allowStoppedGrace = playbackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped ||
                IsWithinStoppedGrace(sessionId);
            if (!allowStoppedGrace)
            {
                TraceSnapshotDecision(
                    "stopped-expired",
                    sessions.Count,
                    sessionId,
                    playbackStatus,
                    lastKnownSnapshot?.Track.Title,
                    false,
                    null);
                ClearLastKnownSnapshot();
                return null;
            }

            if (TryGetFreshCachedTrackInfo(sessionId, out var cachedTrackInfo))
            {
                var snapshotTrack = cachedTrackInfo with
                {
                    Duration = duration ?? cachedTrackInfo.Duration
                };
                var snapshotFromMetadataCache = new PlaybackSnapshot(snapshotTrack, position, isPlaying);
                CacheSnapshot(sessionId, snapshotFromMetadataCache);
                TraceSnapshotDecision(
                    "live-meta-cache",
                    sessions.Count,
                    sessionId,
                    playbackStatus,
                    snapshotTrack.Title,
                    true,
                    snapshotFromMetadataCache);
                return snapshotFromMetadataCache;
            }

            if (ShouldThrottleMetadataRequest(sessionId))
            {
                var cached = TryBuildSnapshotFromCache(sessionId, position, isPlaying);
                TraceSnapshotDecision(
                    "metadata-throttled",
                    sessions.Count,
                    sessionId,
                    playbackStatus,
                    cached?.Track.Title,
                    cached is not null,
                    cached);
                return cached;
            }

            GlobalSystemMediaTransportControlsSessionMediaProperties? mediaProperties = null;
            lastMetadataAttemptAtUtc = DateTimeOffset.UtcNow;
            try
            {
                mediaProperties = await selectedSession.TryGetMediaPropertiesAsync();
            }
            catch
            {
                // Some players intermittently fail metadata retrieval even with an active session.
            }

            if (mediaProperties is null || string.IsNullOrWhiteSpace(mediaProperties.Title))
            {
                var cached = TryBuildSnapshotFromCache(sessionId, position, isPlaying);
                TraceSnapshotDecision(
                    "metadata-miss",
                    sessions.Count,
                    sessionId,
                    playbackStatus,
                    cached?.Track.Title,
                    cached is not null,
                    cached);
                return cached;
            }

            var artworkPath = await TryCacheArtworkAsync(mediaProperties, cancellationToken);
            var track = new TrackInfo(
                mediaProperties.Title,
                mediaProperties.Artist ?? string.Empty,
                artworkPath,
                selectedSession.SourceAppUserModelId,
                duration);

            CacheTrackInfo(sessionId, track);
            var snapshot = new PlaybackSnapshot(track, position, isPlaying);
            CacheSnapshot(sessionId, snapshot);
            TraceSnapshotDecision(
                "live",
                sessions.Count,
                sessionId,
                playbackStatus,
                track.Title,
                false,
                snapshot);
            return snapshot;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var cached = TryBuildSnapshotFromCache(null, null, lastKnownSnapshot?.IsPlaying);
            TraceSnapshotDecision(
                $"exception:{ex.GetType().Name}",
                null,
                null,
                null,
                cached?.Track.Title,
                cached is not null,
                cached);
            return cached;
        }
    }

    private Task<GlobalSystemMediaTransportControlsSessionManager> GetManagerAsync()
    {
        managerTask ??= GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask();
        return managerTask;
    }

    private GlobalSystemMediaTransportControlsSession? ResolveSession(
        GlobalSystemMediaTransportControlsSession? currentSession,
        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions)
    {
        if (currentSession is not null &&
            TryGetPlaybackStatus(currentSession, out var currentStatus) &&
            currentStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
        {
            return currentSession;
        }

        var candidates = BuildCandidateSessions(currentSession, sessions);
        var bestSession = SelectBestSession(candidates);
        if (bestSession is not null)
        {
            return bestSession;
        }

        return IsUsableCurrentSession(currentSession)
            ? currentSession
            : null;
    }

    private static IReadOnlyList<GlobalSystemMediaTransportControlsSession> BuildCandidateSessions(
        GlobalSystemMediaTransportControlsSession? currentSession,
        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions)
    {
        if (currentSession is null)
        {
            return sessions;
        }

        foreach (var session in sessions)
        {
            if (ReferenceEquals(session, currentSession))
            {
                return sessions;
            }

            var sessionId = session.SourceAppUserModelId ?? string.Empty;
            var currentSessionId = currentSession.SourceAppUserModelId ?? string.Empty;
            if (string.Equals(sessionId, currentSessionId, StringComparison.OrdinalIgnoreCase))
            {
                return sessions;
            }
        }

        var merged = new List<GlobalSystemMediaTransportControlsSession>(sessions.Count + 1)
        {
            currentSession
        };

        foreach (var session in sessions)
        {
            merged.Add(session);
        }

        return merged;
    }

    private GlobalSystemMediaTransportControlsSession? TryFindStickySession(
        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions)
    {
        if (string.IsNullOrWhiteSpace(lastKnownSessionId))
        {
            return null;
        }

        foreach (var session in sessions)
        {
            var sessionId = session.SourceAppUserModelId ?? string.Empty;
            if (!string.Equals(sessionId, lastKnownSessionId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryGetPlaybackStatus(session, out var status))
            {
                continue;
            }

            if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed)
            {
                continue;
            }

            if (status != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped ||
                IsWithinStoppedGrace(sessionId))
            {
                return session;
            }
        }

        return null;
    }

    private GlobalSystemMediaTransportControlsSession? SelectBestSession(
        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions)
    {
        if (sessions.Count == 0)
        {
            return null;
        }

        GlobalSystemMediaTransportControlsSession? bestSession = null;
        var bestScore = int.MinValue;

        foreach (var session in sessions)
        {
            if (!TryGetPlaybackStatus(session, out var status))
            {
                continue;
            }

            var score = ScoreSession(session, status);
            if (score <= bestScore)
            {
                continue;
            }

            bestScore = score;
            bestSession = session;
        }

        return bestSession;
    }

    private int ScoreSession(
        GlobalSystemMediaTransportControlsSession session,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus status)
    {
        var sessionId = (session.SourceAppUserModelId ?? string.Empty).ToLowerInvariant();
        var score = status switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => 260,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => 95,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Opened => 65,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => 55,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => IsWithinStoppedGrace(session.SourceAppUserModelId ?? string.Empty) ? 35 : -200,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => -1000,
            _ => 0
        };

        if (!string.IsNullOrWhiteSpace(lastKnownSessionId) &&
            string.Equals(session.SourceAppUserModelId, lastKnownSessionId, StringComparison.OrdinalIgnoreCase))
        {
            score += 35;
        }

        if (ContainsMarker(sessionId, MusicSessionMarkers))
        {
            score += 55;
        }

        if (ContainsMarker(sessionId, BrowserSessionMarkers))
        {
            score -= 60;
        }

        if (IsTimelineFresh(session, FreshTimelineWindow))
        {
            score += 24;
        }

        var timeline = TryGetTimeline(session);
        if (timeline is not null && timeline.Position > TimeSpan.Zero)
        {
            score += 8;
        }

        return score;
    }

    private static bool ContainsMarker(string source, IReadOnlyList<string> markers)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        foreach (var marker in markers)
        {
            if (source.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static TimeSpan CalculatePlaybackPosition(
        GlobalSystemMediaTransportControlsSessionTimelineProperties timeline,
        bool isPlaying)
    {
        var position = timeline.Position;
        if (isPlaying && timeline.LastUpdatedTime != DateTimeOffset.MinValue)
        {
            var drift = DateTimeOffset.UtcNow - timeline.LastUpdatedTime;
            if (drift > TimeSpan.Zero && drift < TimeSpan.FromMinutes(10))
            {
                position += drift;
            }
        }

        if (timeline.MaxSeekTime > TimeSpan.Zero && position > timeline.MaxSeekTime)
        {
            position = timeline.MaxSeekTime;
        }

        if (position < TimeSpan.Zero)
        {
            position = TimeSpan.Zero;
        }

        return position;
    }

    private static TimeSpan? GetTimelineDuration(GlobalSystemMediaTransportControlsSessionTimelineProperties? timeline)
    {
        if (timeline is null)
        {
            return null;
        }

        var duration = timeline.MaxSeekTime;
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromHours(12))
        {
            return null;
        }

        return duration;
    }

    private static bool IsUsableCurrentSession(GlobalSystemMediaTransportControlsSession? session)
    {
        return session is not null &&
            TryGetPlaybackStatus(session, out var status) &&
            status != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
    }

    private static IReadOnlyList<GlobalSystemMediaTransportControlsSession> SafeGetSessions(
        GlobalSystemMediaTransportControlsSessionManager manager)
    {
        try
        {
            return manager.GetSessions();
        }
        catch
        {
            return Array.Empty<GlobalSystemMediaTransportControlsSession>();
        }
    }

    private static GlobalSystemMediaTransportControlsSession? SafeGetCurrentSession(
        GlobalSystemMediaTransportControlsSessionManager manager)
    {
        try
        {
            return manager.GetCurrentSession();
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetPlaybackStatus(
        GlobalSystemMediaTransportControlsSession session,
        out GlobalSystemMediaTransportControlsSessionPlaybackStatus status)
    {
        try
        {
            status = session.GetPlaybackInfo().PlaybackStatus;
            return true;
        }
        catch
        {
            status = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
            return false;
        }
    }

    private static bool IsTimelineFresh(
        GlobalSystemMediaTransportControlsSession session,
        TimeSpan maxAge)
    {
        try
        {
            var timeline = session.GetTimelineProperties();
            if (timeline.LastUpdatedTime == DateTimeOffset.MinValue)
            {
                return false;
            }

            var age = DateTimeOffset.UtcNow - timeline.LastUpdatedTime;
            return age >= TimeSpan.Zero && age <= maxAge;
        }
        catch
        {
            return false;
        }
    }

    private static GlobalSystemMediaTransportControlsSessionTimelineProperties? TryGetTimeline(
        GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            return session.GetTimelineProperties();
        }
        catch
        {
            return null;
        }
    }

    private PlaybackSnapshot? TryBuildSnapshotFromCache(
        string? currentSessionId,
        TimeSpan? currentPosition,
        bool? currentIsPlaying)
    {
        // Never emit cache when there is no active session id.
        // This avoids ghost playback after session loss.
        if (string.IsNullOrWhiteSpace(currentSessionId))
        {
            return null;
        }

        if (lastKnownSnapshot is null)
        {
            return null;
        }

        if (DateTimeOffset.UtcNow - lastKnownSnapshotAtUtc > SnapshotFallbackLifetime)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(currentSessionId) &&
            !string.Equals(lastKnownSessionId, currentSessionId, StringComparison.Ordinal))
        {
            return null;
        }

        var snapshot = currentPosition.HasValue && currentIsPlaying.HasValue
            ? new PlaybackSnapshot(lastKnownSnapshot.Track, currentPosition.Value, currentIsPlaying.Value)
            : lastKnownSnapshot;

        CacheSnapshot(lastKnownSessionId ?? currentSessionId ?? string.Empty, snapshot);
        return snapshot;
    }

    private void CacheSnapshot(string sessionId, PlaybackSnapshot snapshot)
    {
        lastKnownSessionId = sessionId;
        lastKnownSnapshot = snapshot;
        lastKnownSnapshotAtUtc = DateTimeOffset.UtcNow;
    }

    private void UpdateStoppedSessionState(
        string sessionId,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus status)
    {
        if (status != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped)
        {
            ClearStoppedSessionState();
            return;
        }

        if (string.Equals(stoppedSessionId, sessionId, StringComparison.Ordinal))
        {
            return;
        }

        stoppedSessionId = sessionId;
        stoppedSessionSinceUtc = DateTimeOffset.UtcNow;
    }

    private bool IsWithinStoppedGrace(string sessionId)
    {
        if (!string.Equals(stoppedSessionId, sessionId, StringComparison.Ordinal))
        {
            return false;
        }

        if (stoppedSessionSinceUtc == DateTimeOffset.MinValue)
        {
            return false;
        }

        return DateTimeOffset.UtcNow - stoppedSessionSinceUtc <= StoppedSessionGracePeriod;
    }

    private void ClearStoppedSessionState()
    {
        stoppedSessionId = null;
        stoppedSessionSinceUtc = DateTimeOffset.MinValue;
    }

    private void ClearLastKnownSnapshot()
    {
        lastKnownSessionId = null;
        lastKnownSnapshot = null;
        lastKnownSnapshotAtUtc = DateTimeOffset.MinValue;
        ClearTrackInfoCache();
    }

    private bool TryGetFreshCachedTrackInfo(string sessionId, out TrackInfo trackInfo)
    {
        trackInfo = default!;
        if (cachedTrackInfo is null ||
            string.IsNullOrWhiteSpace(cachedTrackInfo.Title) ||
            !string.Equals(cachedMetadataSessionId, sessionId, StringComparison.Ordinal))
        {
            return false;
        }

        var age = DateTimeOffset.UtcNow - cachedMetadataFetchedAtUtc;
        if (age < TimeSpan.Zero || age > MetadataRefreshInterval)
        {
            return false;
        }

        trackInfo = cachedTrackInfo;
        return true;
    }

    private bool ShouldThrottleMetadataRequest(string sessionId)
    {
        if (!string.Equals(cachedMetadataSessionId, sessionId, StringComparison.Ordinal))
        {
            return false;
        }

        if (lastMetadataAttemptAtUtc == DateTimeOffset.MinValue)
        {
            return false;
        }

        return (DateTimeOffset.UtcNow - lastMetadataAttemptAtUtc) < MetadataRetryThrottle;
    }

    private void CacheTrackInfo(string sessionId, TrackInfo trackInfo)
    {
        cachedMetadataSessionId = sessionId;
        cachedTrackInfo = trackInfo;
        cachedMetadataFetchedAtUtc = DateTimeOffset.UtcNow;
    }

    private void ClearTrackInfoCache()
    {
        cachedMetadataSessionId = null;
        cachedTrackInfo = null;
        cachedMetadataFetchedAtUtc = DateTimeOffset.MinValue;
        lastMetadataAttemptAtUtc = DateTimeOffset.MinValue;
    }

    private void TraceSnapshotDecision(
        string reason,
        int? sessionCount,
        string? sessionId,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus? status,
        string? title,
        bool fromCache,
        PlaybackSnapshot? snapshot)
    {
        var state = string.Join(
            "|",
            reason,
            sessionCount?.ToString() ?? "?",
            sessionId ?? "<none>",
            status?.ToString() ?? "<none>",
            title ?? "<none>",
            fromCache ? "cache" : "live",
            snapshot?.Track.Title ?? "<null>",
            snapshot?.IsPlaying.ToString() ?? "<null>");

        if (string.Equals(lastTraceState, state, StringComparison.Ordinal))
        {
            return;
        }

        lastTraceState = state;
        DebugTrace.Write(
            $"SMTC: reason={reason}; sessions={sessionCount?.ToString() ?? "?"}; session={sessionId ?? "<none>"}; " +
            $"status={status?.ToString() ?? "<none>"}; title={title ?? "<none>"}; source={(fromCache ? "cache" : "live")}; " +
            $"result={(snapshot is null ? "<null>" : $"{snapshot.Track.Title} | playing={snapshot.IsPlaying} | pos={snapshot.Position:mm\\:ss}")}");
    }

    private static async Task<string?> TryCacheArtworkAsync(
        GlobalSystemMediaTransportControlsSessionMediaProperties mediaProperties,
        CancellationToken cancellationToken)
    {
        if (mediaProperties.Thumbnail is null)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(ArtworkCacheFolder);
            if (Interlocked.Increment(ref artworkCacheWriteCount) % 48 == 0)
            {
                PruneArtworkCacheBestEffort();
            }

            var cacheKey = ComputeArtworkCacheKey(mediaProperties.Title, mediaProperties.Artist);
            var artworkPath = Path.Combine(ArtworkCacheFolder, $"{cacheKey}.img");
            if (File.Exists(artworkPath))
            {
                return artworkPath;
            }

            using var thumbnailStream = await mediaProperties.Thumbnail.OpenReadAsync();
            if (thumbnailStream.Size == 0)
            {
                return null;
            }

            await using var fileStream = File.Create(artworkPath);
            using var readStream = thumbnailStream.AsStreamForRead();
            await readStream.CopyToAsync(fileStream, cancellationToken);
            return artworkPath;
        }
        catch
        {
            return null;
        }
    }

    private static void PruneArtworkCacheBestEffort()
    {
        try
        {
            var directory = new DirectoryInfo(ArtworkCacheFolder);
            if (!directory.Exists)
            {
                return;
            }

            var files = directory
                .EnumerateFiles("*.img", SearchOption.TopDirectoryOnly)
                .OrderByDescending(static file => file.LastWriteTimeUtc)
                .ToList();

            long totalBytes = 0;
            foreach (var file in files)
            {
                totalBytes += file.Length;
            }

            for (var i = files.Count - 1; i >= 0; i--)
            {
                var shouldTrimByCount = i >= MaxArtworkCacheFiles;
                var shouldTrimBySize = totalBytes > MaxArtworkCacheBytes;
                if (!shouldTrimByCount && !shouldTrimBySize)
                {
                    continue;
                }

                var file = files[i];
                try
                {
                    totalBytes -= file.Length;
                    file.Delete();
                }
                catch
                {
                    // Best-effort trim.
                }
            }
        }
        catch
        {
            // Best-effort trim.
        }
    }

    private static string ComputeArtworkCacheKey(string title, string? artist)
    {
        var normalized = $"{artist ?? string.Empty}\u001f{title}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hashBytes);
    }
}
