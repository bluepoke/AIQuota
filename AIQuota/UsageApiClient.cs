using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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

public sealed record MoneyAmount(
    [property: JsonPropertyName("amount_minor")] long AmountMinor,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("exponent")] int Exponent)
{
    public decimal Value => AmountMinor / (decimal)Math.Pow(10, Exponent);
}

public sealed record UsageSpend(
    [property: JsonPropertyName("used")] MoneyAmount? Used,
    [property: JsonPropertyName("limit")] MoneyAmount? Limit,
    [property: JsonPropertyName("enabled")] bool Enabled);

public sealed record UsageApiResponse(
    [property: JsonPropertyName("five_hour")] UsageWindow? FiveHour,
    [property: JsonPropertyName("seven_day")] UsageWindow? SevenDay,
    [property: JsonPropertyName("spend")] UsageSpend? Spend,
    // Kept as raw JSON and read tolerantly by UsageLimitParser, so a change in their shape
    // can't fail the whole fetch.
    [property: JsonPropertyName("limits")] JsonElement? Limits = null,
    [property: JsonPropertyName("weekly_scoped_shares")] JsonElement? WeeklyScopedShares = null);

public sealed record UsageSnapshot(
    int SessionPercent,
    int WeeklyPercent,
    DateTimeOffset? SessionResetsAt,
    DateTimeOffset? WeeklyResetsAt,
    DateTimeOffset FetchedAt,
    decimal? CreditUsed = null,
    decimal? CreditLimit = null,
    string? CreditCurrency = null,
    double? SessionUtilization = null,
    double? WeeklyUtilization = null,
    IReadOnlyList<UsageLimitInfo>? AdditionalLimits = null);

/// <summary>The signed-in account, from the profile endpoint.</summary>
/// <param name="Id">Stable identifier (the account UUID, else the email) - keys the local
/// usage history; null if the profile carries neither.</param>
/// <param name="DisplayLabel">"Name (email)", just the name, or just the email.</param>
public sealed record AccountInfo(string? Id, string? DisplayLabel);

public enum UsageFetchStatus
{
    Ok,
    NotLoggedIn,
    AuthExpired,
    NetworkError,
}

/// <param name="RateLimited">The API answered 429: the caller should wait longer than usual
/// before asking for this account again - at least <paramref name="RetryAfter"/> if given.</param>
public sealed record UsageFetchResult(UsageFetchStatus Status, UsageSnapshot? Snapshot, string? Error = null,
    bool RateLimited = false, TimeSpan? RetryAfter = null);

/// <summary>
/// Calls the same GET /api/oauth/usage endpoint the official Claude Code CLI uses to
/// report the account's five-hour session and seven-day (weekly) rate-limit usage.
/// </summary>
public sealed class UsageApiClient(OAuthClient oauth)
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <param name="accountKey">The saved account to fetch for (<see cref="SavedAccount.Key"/>).</param>
    public async Task<UsageFetchResult> FetchAsync(string accountKey, CancellationToken cancellationToken)
    {
        try
        {
            var accessToken = await oauth.GetValidAccessTokenAsync(accountKey, cancellationToken);
            if (accessToken is null)
                return new UsageFetchResult(UsageFetchStatus.NotLoggedIn, null);

            using var request = new HttpRequestMessage(HttpMethod.Get, OAuthConfig.UsageUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return new UsageFetchResult(UsageFetchStatus.AuthExpired, null);
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                var retryAfter = response.Headers.RetryAfter switch
                {
                    { Delta: { } delta } => delta,
                    { Date: { } date } => date - DateTimeOffset.UtcNow,
                    _ => (TimeSpan?)null,
                };
                return new UsageFetchResult(UsageFetchStatus.NetworkError, null, "HTTP 429",
                    RateLimited: true, RetryAfter: retryAfter > TimeSpan.Zero ? retryAfter : null);
            }
            if (!response.IsSuccessStatusCode)
                return new UsageFetchResult(UsageFetchStatus.NetworkError, null, $"HTTP {(int)response.StatusCode}");

            var data = await response.Content.ReadFromJsonAsync<UsageApiResponse>(cancellationToken: cancellationToken);
            if (data is null)
                return new UsageFetchResult(UsageFetchStatus.NetworkError, null, Strings.UsageEmptyResponse);

            (MoneyAmount used, MoneyAmount limit)? spend = data.Spend is { Enabled: true, Used: { } used, Limit: { } limit } ? (used, limit) : null;

            var snapshot = new UsageSnapshot(
                data.FiveHour?.Percent ?? 0,
                data.SevenDay?.Percent ?? 0,
                data.FiveHour?.ResetsAt,
                data.SevenDay?.ResetsAt,
                DateTimeOffset.Now,
                spend?.used.Value,
                spend?.limit.Value,
                spend?.used.Currency,
                data.FiveHour is { } fiveHour ? Math.Clamp(fiveHour.Utilization, 0, 100) : null,
                data.SevenDay is { } sevenDay ? Math.Clamp(sevenDay.Utilization, 0, 100) : null,
                UsageLimitParser.ParseAdditional(data.Limits, data.WeeklyScopedShares));

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
    /// the signed-in account (not carried by the access token itself): its UUID as a stable
    /// ID, and a display label of "Name (email)", just the name, or just the email,
    /// depending on what the account has set. Returns null on any failure - the label is a
    /// "nice to have", and without an ID the usage history just isn't recorded until the
    /// next successful attempt.
    /// </summary>
    public async Task<AccountInfo?> FetchAccountAsync(string accountKey, CancellationToken cancellationToken)
    {
        try
        {
            var accessToken = await oauth.GetValidAccessTokenAsync(accountKey, cancellationToken);
            return accessToken is null ? null : await FetchAccountWithTokenAsync(accessToken, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary><see cref="FetchAccountAsync"/> for a token that isn't stored yet - right
    /// after a login, to find out which account it belongs to.</summary>
    public async Task<AccountInfo?> FetchAccountWithTokenAsync(string accessToken, CancellationToken cancellationToken)
    {
        try
        {
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

            string? uuid = account.TryGetProperty("uuid", out var uuidValue) &&
                uuidValue.ValueKind == System.Text.Json.JsonValueKind.String &&
                uuidValue.GetString() is { Length: > 0 } uuidText
                    ? uuidText
                    : null;

            var label = (name, email) switch
            {
                ({ } n, { } e) => $"{n} ({e})",
                ({ } n, null) => n,
                (null, { } e) => e,
                _ => null,
            };

            return new AccountInfo(uuid ?? email, label);
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
