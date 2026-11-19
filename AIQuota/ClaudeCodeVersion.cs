using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AIQuota;

/// <summary>
/// The current Claude Code version, for the User-Agent of the usage request: the usage
/// endpoint only reports limit resets (#21) to Claude Code - without its User-Agent the
/// <c>cedar_ember</c> block says <c>"ineligible_reason": "surface"</c>, and an outdated
/// version may be turned away as well (<c>cli_version</c>).
///
/// The version is read from the release channel Claude Code's own installer uses
/// (<c>downloads.claude.ai/claude-code-releases/latest</c>, plain text), else from the npm
/// registry. Checked at most every <see cref="CheckInterval"/>; the last known version is
/// kept in the registry (<c>HKCU\Software\AIQuota\ClaudeCodeVersion</c>), so it's there right
/// after a start and while offline. Neither request carries anything about the user.
/// </summary>
internal static partial class ClaudeCodeVersion
{
    /// <summary>Used until a version has been read online once.</summary>
    private const string Fallback = "2.1.328";

    private const string ReleaseChannelUrl = "https://downloads.claude.ai/claude-code-releases/latest";
    private const string NpmUrl = "https://registry.npmjs.org/@anthropic-ai/claude-code/latest";
    private const string KeyPath = @"Software\AIQuota";
    private const string ValueName = "ClaudeCodeVersion";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    /// <summary>After a failed check, try again sooner than <see cref="CheckInterval"/>.</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(30);

    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string _version = Load() ?? Fallback;
    private static DateTimeOffset _nextCheck = DateTimeOffset.MinValue;

    /// <summary>The User-Agent Claude Code sends, e.g. "claude-cli/2.1.328 (external, cli)".</summary>
    public static string UserAgent => $"claude-cli/{_version} (external, cli)";

    /// <summary>Reads the current version online if the last check is older than <see
    /// cref="CheckInterval"/>. Never throws: on failure the last known version stays.</summary>
    public static async Task EnsureFreshAsync(CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow < _nextCheck)
            return;
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (DateTimeOffset.UtcNow < _nextCheck)
                return;
            var version = await FetchAsync(cancellationToken);
            if (version is not null)
            {
                if (version != _version)
                    Save(version);
                _version = version;
            }
            _nextCheck = DateTimeOffset.UtcNow + (version is null ? RetryInterval : CheckInterval);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<string?> FetchAsync(CancellationToken cancellationToken)
    {
        try
        {
            var text = (await Http.GetStringAsync(ReleaseChannelUrl, cancellationToken)).Trim();
            if (IsVersion(text))
                return text;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Try npm next.
        }

        try
        {
            using var document = JsonDocument.Parse(await Http.GetStringAsync(NpmUrl, cancellationToken));
            return document.RootElement.TryGetProperty("version", out var value)
                && value.ValueKind == JsonValueKind.String
                && value.GetString() is { } version && IsVersion(version)
                ? version
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private static bool IsVersion(string text) => VersionPattern().IsMatch(text);

    [GeneratedRegex(@"^\d{1,4}\.\d{1,4}\.\d{1,6}$")]
    private static partial Regex VersionPattern();

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"AIQuota/{AppInfo.Version}");
        return http;
    }

    private static string? Load()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        return key?.GetValue(ValueName) is string version && IsVersion(version) ? version : null;
    }

    private static void Save(string version)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        key.SetValue(ValueName, version, RegistryValueKind.String);
    }
}
