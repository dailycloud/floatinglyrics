using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FloatingLyrics.Services;

public sealed class GitHubUpdateService(HttpClient httpClient) : IUpdateService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(
        string repositoryOwner,
        string repositoryName,
        Version currentVersion,
        CancellationToken cancellationToken)
    {
        var owner = repositoryOwner?.Trim() ?? string.Empty;
        var repo = repositoryName?.Trim() ?? string.Empty;
        if (owner.Length == 0 || repo.Length == 0)
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.NotConfigured,
                "GitHub repository is not configured.",
                currentVersion,
                ReleaseUrl: BuildReleasesPageUrl(owner, repo));
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.github.com/repos/{owner}/{repo}/releases/latest");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.UserAgent.ParseAdd("FloatingLyrics-Updater");

            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.NoReleases,
                    "No published releases were found for this repository yet.",
                    currentVersion,
                    ReleaseUrl: BuildReleasesPageUrl(owner, repo));
            }

            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.Error,
                    $"GitHub returned {(int)response.StatusCode} {response.ReasonPhrase}.",
                    currentVersion,
                    ReleaseUrl: BuildReleasesPageUrl(owner, repo));
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<GitHubReleasePayload>(stream, JsonOptions, cancellationToken);
            if (payload is null || string.IsNullOrWhiteSpace(payload.TagName))
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.Error,
                    "GitHub returned an unexpected release payload.",
                    currentVersion,
                    ReleaseUrl: BuildReleasesPageUrl(owner, repo));
            }

            if (!TryParseVersion(payload.TagName, out var latestVersion))
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.Error,
                    $"Unable to parse release tag \"{payload.TagName}\" as a version.",
                    currentVersion,
                    ReleaseUrl: payload.HtmlUrl ?? BuildReleasesPageUrl(owner, repo),
                    ReleaseTitle: payload.Name ?? payload.TagName,
                    PublishedAtUtc: payload.PublishedAt);
            }

            var releaseUrl = payload.HtmlUrl ?? BuildReleasesPageUrl(owner, repo);
            if (latestVersion > currentVersion)
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.UpdateAvailable,
                    $"Update {latestVersion} is available.",
                    currentVersion,
                    LatestVersion: latestVersion,
                    ReleaseUrl: releaseUrl,
                    ReleaseTitle: payload.Name ?? payload.TagName,
                    PublishedAtUtc: payload.PublishedAt,
                    AssetUrl: payload.Assets?
                        .FirstOrDefault(a => string.Equals(a.Name, UpdateInstaller.AssetName, StringComparison.OrdinalIgnoreCase))?
                        .DownloadUrl);
            }

            return new UpdateCheckResult(
                UpdateCheckStatus.UpToDate,
                "You already have the latest release.",
                currentVersion,
                LatestVersion: latestVersion,
                ReleaseUrl: releaseUrl,
                ReleaseTitle: payload.Name ?? payload.TagName,
                PublishedAtUtc: payload.PublishedAt);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.Error,
                $"Unable to contact GitHub: {ex.Message}",
                currentVersion,
                ReleaseUrl: BuildReleasesPageUrl(owner, repo));
        }
    }

    public static string BuildReleasesPageUrl(string repositoryOwner, string repositoryName)
    {
        var owner = repositoryOwner?.Trim() ?? string.Empty;
        var repo = repositoryName?.Trim() ?? string.Empty;
        return owner.Length == 0 || repo.Length == 0
            ? string.Empty
            : $"https://github.com/{owner}/{repo}/releases";
    }

    private static bool TryParseVersion(string rawTag, out Version version)
    {
        var normalized = rawTag.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        var plusIndex = normalized.IndexOf('+', StringComparison.Ordinal);
        if (plusIndex >= 0)
        {
            normalized = normalized[..plusIndex];
        }

        var dashIndex = normalized.IndexOf('-', StringComparison.Ordinal);
        if (dashIndex >= 0)
        {
            normalized = normalized[..dashIndex];
        }

        return Version.TryParse(normalized, out version!);
    }

    private sealed class GitHubReleasePayload
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? DownloadUrl { get; set; }
    }
}
