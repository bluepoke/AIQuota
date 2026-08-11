using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AIQuota.Auth;
using AIQuota.Localization;

namespace AIQuota;

public sealed record UsageWindow(
    [property: JsonPropertyName("utilization")] double Utilization,
    [property: JsonPropertyName("resets_at")] DateTimeOffset? ResetsAt)
{
    // The API already returns utilization as a 0-100 percentage, not a 0-1 fraction.
    public int Percent => (int)Math.Round(Math.Clamp(Utilization, 0, 100));
}

public sealed record UsageApiResponse(
    [property: JsonPropertyName("five_hour")] UsageWindow? FiveHour,
    [property: JsonPropertyName("seven_day")] UsageWindow? SevenDay);

public sealed record UsageSnapshot(
    int SessionPercent,
    int WeeklyPercent,
    DateTimeOffset? SessionResetsAt,
    DateTimeOffset? WeeklyResetsAt,
    DateTimeOffset FetchedAt);

public enum UsageFetchStatus
{
    Ok,
    NotLoggedIn,
    AuthExpired,
    NetworkError,
}

public sealed record UsageFetchResult(UsageFetchStatus Status, UsageSnapshot? Snapshot, string? Error = null);

/// <summary>
/// Calls the same GET /api/oauth/usage endpoint the official Claude Code CLI uses to
/// report the account's five-hour session and seven-day (weekly) rate-limit usage.
/// </summary>
public sealed class UsageApiClient(OAuthClient oauth)
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<UsageFetchResult> FetchAsync(CancellationToken cancellationToken)
    {
        try
        {
            var accessToken = await oauth.GetValidAccessTokenAsync(cancellationToken);
            if (accessToken is null)
                return new UsageFetchResult(UsageFetchStatus.NotLoggedIn, null);

            using var request = new HttpRequestMessage(HttpMethod.Get, OAuthConfig.UsageUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return new UsageFetchResult(UsageFetchStatus.AuthExpired, null);
            if (!response.IsSuccessStatusCode)
                return new UsageFetchResult(UsageFetchStatus.NetworkError, null, $"HTTP {(int)response.StatusCode}");

            var data = await response.Content.ReadFromJsonAsync<UsageApiResponse>(cancellationToken: cancellationToken);
            if (data is null)
                return new UsageFetchResult(UsageFetchStatus.NetworkError, null, Strings.UsageEmptyResponse);

            var snapshot = new UsageSnapshot(
                data.FiveHour?.Percent ?? 0,
                data.SevenDay?.Percent ?? 0,
                data.FiveHour?.ResetsAt,
                data.SevenDay?.ResetsAt,
                DateTimeOffset.Now);

            return new UsageFetchResult(UsageFetchStatus.Ok, snapshot);
        }
        catch (HttpRequestException ex) when (ex.InnerException is System.Net.Sockets.SocketException)
        {
            return new UsageFetchResult(UsageFetchStatus.NetworkError, null, Strings.UsageNoInternet);
        }
        catch (HttpRequestException ex)
        {
            return new UsageFetchResult(UsageFetchStatus.NetworkError, null, ex.Message);
        }
        catch (System.Net.Sockets.SocketException)
        {
            return new UsageFetchResult(UsageFetchStatus.NetworkError, null, Strings.UsageNoInternet);
        }
        catch (TaskCanceledException)
        {
            return new UsageFetchResult(UsageFetchStatus.NetworkError, null, Strings.UsageTimeout);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return new UsageFetchResult(UsageFetchStatus.NetworkError, null, Strings.UsageUnexpectedFormat(ex.Message));
        }
    }

    /// <summary>
    /// Calls the same GET /api/oauth/profile endpoint the official CLI uses to resolve
    /// the signed-in account's display name and email (neither is carried by the access
    /// token itself). Returns "Name (email)", just the name, or just the email,
    /// depending on what the account has set. Returns null on any failure - this is a
    /// "nice to have" label, not critical data.
    /// </summary>
    public async Task<string?> FetchAccountNameAsync(CancellationToken cancellationToken)
    {
        try
        {
            var accessToken = await oauth.GetValidAccessTokenAsync(cancellationToken);
            if (accessToken is null)
                return null;

            using var request = new HttpRequestMessage(HttpMethod.Get, OAuthConfig.ProfileUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (!document.RootElement.TryGetProperty("account", out var account))
                return null;

            string? name = null;
            foreach (var property in new[] { "display_name", "full_name" })
            {
                if (account.TryGetProperty(property, out var value) &&
                    value.GetString() is { Length: > 0 } text)
                {
                    name = text;
                    break;
                }
            }

            string? email = account.TryGetProperty("email", out var emailValue) &&
                emailValue.GetString() is { Length: > 0 } emailText
                    ? emailText
                    : null;

            return (name, email) switch
            {
                ({ } n, { } e) => $"{n} ({e})",
                ({ } n, null) => n,
                (null, { } e) => e,
                _ => null,
            };
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
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
