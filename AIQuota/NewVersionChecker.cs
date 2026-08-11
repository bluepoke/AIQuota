using System.Text.Json;

namespace AIQuota;

/// <summary>
/// A newer published release than the one currently running. <see cref="SelfContainedAssetUrl"/>
/// and <see cref="FrameworkDependentAssetUrl"/> are the direct download URLs for the two zip
/// variants published by the release workflow - either can be missing if the release doesn't
/// (yet) have that asset, in which case <see cref="SelfUpdater"/> falls back to <see cref="ReleaseUrl"/>.
/// </summary>
public sealed record NewVersionInfo(string Version, string ReleaseUrl, string? SelfContainedAssetUrl, string? FrameworkDependentAssetUrl);

/// <summary>
/// Checks the GitHub Releases API for a newer published version than the one currently running.
/// </summary>
public static class NewVersionChecker
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/bluepoke/AIQuota/releases/latest";

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

            var releaseUrl = document.RootElement.TryGetProperty("html_url", out var urlProperty)
                ? urlProperty.GetString()
                : null;

            var (selfContainedUrl, frameworkDependentUrl) = FindAssetUrls(document.RootElement);

            return new NewVersionInfo(
                latestVersion,
                releaseUrl ?? $"{AppInfo.RepositoryUrl}/releases/latest",
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
