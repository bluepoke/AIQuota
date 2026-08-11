using System.Text.Json;

namespace AIQuota;

public sealed record NewVersionInfo(string Version, string Url);

/// <summary>
/// Checks the GitHub Releases API for a newer published version than the one currently
/// running. There is no in-place download/install (releases are plain zips, not an
/// installer) - a detected new version just links the user to the release page.
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

            var url = document.RootElement.TryGetProperty("html_url", out var urlProperty)
                ? urlProperty.GetString()
                : null;

            return new NewVersionInfo(latestVersion, url ?? $"{AppInfo.RepositoryUrl}/releases/latest");
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
