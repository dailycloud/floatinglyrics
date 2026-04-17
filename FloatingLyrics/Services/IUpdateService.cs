namespace FloatingLyrics.Services;

public interface IUpdateService
{
    Task<UpdateCheckResult> CheckForUpdatesAsync(
        string repositoryOwner,
        string repositoryName,
        Version currentVersion,
        CancellationToken cancellationToken);
}
