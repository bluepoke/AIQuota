using System.Text.Json;

namespace AIQuota;

/// <summary>
/// A newer published release than the one currently running. <see cref="SelfContainedAssetUrl"/>
/// and <see cref="FrameworkDependentAssetUrl"/> are the direct download URLs for the two zip
/// variants published by the release workflow - either can be missing if the release doesn't
/// (yet) have that asset, in which case <see cref="SelfUpdater"/> falls back to <see cref="ReleaseUrl"/>.
/// <see cref="Changelog"/> is the "yyyy-MM-dd: subject" commit log between <see cref="PreviousVersion"/>
/// and <see cref="Version"/>, oldest first, or null if it couldn't be fetched.
/// </summary>
public sealed record NewVersionInfo(
    string Version,
    string PreviousVersion,
    string ReleaseUrl,
    string? Changelog,
    string? SelfContainedAssetUrl,
    string? FrameworkDependentAssetUrl);

/// <summary>
/// Checks the GitHub Releases API for a newer published version than the one currently running.
/// </summary>
public static class NewVersionChecker
{
    private const string RepoApiUrl = "https://api.github.com/repos/bluepoke/AIQuota";
    private const string LatestReleaseApiUrl = RepoApiUrl + "/releases/latest";

    public static async Task<NewVersionInfo?> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"AIQuota/{AppInfo.Version}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            using var response = await http.GetAsync(LatestReleaseApiUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (!document.RootElement.TryGetProperty("tag_name", out var tagProperty) ||
                tagProperty.GetString() is not { Length: > 0 } tag)
                return null;

            var latestVersion = tag.TrimStart('v', 'V');
            if (!IsNewer(latestVersion, AppInfo.Version))
                return null;

            var previousVersion = StripPreReleaseSuffix(AppInfo.Version);

            var releaseUrl = document.RootElement.TryGetProperty("html_url", out var urlProperty)
                ? urlProperty.GetString()
                : null;

            var changelog = await FetchChangelogAsync(http, previousVersion, latestVersion, cancellationToken);

            var (selfContainedUrl, frameworkDependentUrl) = FindAssetUrls(document.RootElement);

            return new NewVersionInfo(
                latestVersion,
                previousVersion,
                releaseUrl ?? $"{AppInfo.RepositoryUrl}/releases/latest",
                changelog,
                selfContainedUrl,
                frameworkDependentUrl);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Fetches the commit log between two tags via the GitHub compare API and
    /// formats it as "yyyy-MM-dd: subject" lines, oldest first (the order the API returns
    /// commits in). Returns null if the comparison can't be fetched or has no commits -
    /// this is best-effort, so failures here must not fail the whole version check.</summary>
    private static async Task<string?> FetchChangelogAsync(HttpClient http, string fromVersion, string toVersion, CancellationToken cancellationToken)
    {
        try
        {
            var compareUrl = $"{RepoApiUrl}/compare/v{fromVersion}...v{toVersion}";
            using var response = await http.GetAsync(compareUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (!document.RootElement.TryGetProperty("commits", out var commits) || commits.ValueKind != JsonValueKind.Array)
                return null;

            var lines = new List<string>();
            foreach (var commitEntry in commits.EnumerateArray())
            {
                if (!commitEntry.TryGetProperty("commit", out var commit))
                    continue;

                var date = commit.TryGetProperty("committer", out var committer) &&
                    committer.TryGetProperty("date", out var dateProperty) &&
                    DateTimeOffset.TryParse(dateProperty.GetString(), out var committedAt)
                    ? committedAt.ToString("yyyy-MM-dd")
                    : null;

                var subject = commit.TryGetProperty("message", out var messageProperty) &&
                    messageProperty.GetString() is { Length: > 0 } message
                    ? message.Split('\n')[0].TrimEnd()
                    : null;

                if (date is null || subject is not { Length: > 0 })
                    continue;

                lines.Add($"{date}: {subject}");
            }

            return lines.Count > 0 ? string.Join('\n', lines) : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (string? SelfContained, string? FrameworkDependent) FindAssetUrls(JsonElement release)
    {
        string? selfContainedUrl = null;
        string? frameworkDependentUrl = null;

        if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var nameProperty) ||
                    nameProperty.GetString() is not { } name ||
                    !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!asset.TryGetProperty("browser_download_url", out var urlProperty) ||
                    urlProperty.GetString() is not { } assetUrl)
                    continue;

                if (name.Contains("framework-dependent", StringComparison.OrdinalIgnoreCase))
                    frameworkDependentUrl = assetUrl;
                else
                    selfContainedUrl = assetUrl;
            }
        }

        return (selfContainedUrl, frameworkDependentUrl);
    }

    private static bool IsNewer(string latest, string current) =>
        Version.TryParse(StripPreReleaseSuffix(latest), out var latestVersion) &&
        Version.TryParse(StripPreReleaseSuffix(current), out var currentVersion) &&
        latestVersion > currentVersion;

    /// <summary>Cuts off any "-beta"/"+commitsha" suffix (e.g. from an informational
    /// version like "1.0.6+a1b2c3d") so <see cref="Version.TryParse(string, out Version)"/> doesn't reject it.</summary>
    private static string StripPreReleaseSuffix(string version)
    {
        var cut = version.IndexOfAny(['-', '+']);
        return cut >= 0 ? version[..cut] : version;
    }
}
