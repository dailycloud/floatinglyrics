namespace FloatingLyrics.Services;

public enum UpdateCheckStatus
{
    NotConfigured,
    NoReleases,
    UpToDate,
    UpdateAvailable,
    Error
}

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    string Message,
    Version CurrentVersion,
    Version? LatestVersion = null,
    string ReleaseUrl = "",
    string ReleaseTitle = "",
    DateTimeOffset? PublishedAtUtc = null,
    string? AssetUrl = null);
