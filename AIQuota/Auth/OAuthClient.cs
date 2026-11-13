using System.Collections.Concurrent;
using System.Net.Http.Json;
using AIQuota.Localization;

namespace AIQuota.Auth;

public sealed class OAuthClient
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>One lock per account, so two callers never refresh the same account's token
    /// at once, while different accounts don't wait for each other.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshLocks = new();

    /// <summary>
    /// Runs the full PKCE login and returns the tokens - the caller decides which account
    /// they belong to and stores them. <paramref name="openAuthorizeUrl"/> takes claude.ai's
    /// consent URL (by default: opens it in the system browser); then this waits for the
    /// loopback redirect carrying the authorization code.
    /// </summary>
    public async Task<TokenSet> LoginAsync(Action<string>? openAuthorizeUrl, CancellationToken cancellationToken)
    {
        using var listener = LoopbackListener.BindFirstAvailable(OAuthConfig.LoopbackPorts);
        var pkce = Pkce.Create();
        var state = Pkce.CreateState();
        var redirectUri = OAuthConfig.RedirectUri(listener.Port);

        var authorizeUrl = BuildAuthorizeUrl(redirectUri, pkce.Challenge, state);
        (openAuthorizeUrl ?? OpenInBrowser)(authorizeUrl);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));

        var result = await listener.WaitForRedirectAsync(timeoutCts.Token);

        if (result.Error is not null)
            throw new InvalidOperationException(Strings.LoginRejected(result.Error));
        if (result.Code is null || result.State != state)
            throw new InvalidOperationException(Strings.LoginInvalidResponse);

        var tokenResponse = await ExchangeCodeAsync(result.Code, state, pkce.Verifier, redirectUri, cancellationToken);
        return ToTokenSet(tokenResponse);
    }

    public static void OpenInBrowser(string url) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });

    /// <summary>Returns a currently-valid access token for the account, refreshing it first
    /// if needed; null if the account has no token (any more).</summary>
    public async Task<string?> GetValidAccessTokenAsync(string accountKey, CancellationToken cancellationToken)
    {
        var tokens = TokenStore.Load(accountKey);
        if (tokens is null)
            return null;

        if (!tokens.IsExpiredOrExpiringSoon)
            return tokens.AccessToken;

        return await RefreshAsync(accountKey, tokens, cancellationToken);
    }

    private async Task<string?> RefreshAsync(string accountKey, TokenSet tokens, CancellationToken cancellationToken)
    {
        if (tokens.RefreshToken is null)
            return tokens.AccessToken;

        var refreshLock = _refreshLocks.GetOrAdd(accountKey, _ => new SemaphoreSlim(1, 1));
        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            // Another caller may have refreshed while we waited for the lock.
            var current = TokenStore.Load(accountKey);
            if (current is null)
                return null;
            if (!current.IsExpiredOrExpiringSoon)
                return current.AccessToken;

            var body = new
            {
                grant_type = "refresh_token",
                refresh_token = current.RefreshToken ?? tokens.RefreshToken,
                client_id = OAuthConfig.ClientId,
                scope = OAuthConfig.Scope,
            };

            using var response = await _http.PostAsJsonAsync(OAuthConfig.TokenUrl, body, cancellationToken);
            if ((int)response.StatusCode is >= 400 and < 500)
            {
                // Refresh token no longer valid - drop this account's token, so it shows as
                // "login required" instead of looping. Other accounts aren't affected.
                TokenStore.Clear(accountKey);
                return null;
            }
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"HTTP {(int)response.StatusCode}");

            var tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: cancellationToken)
                ?? throw new InvalidOperationException(Strings.TokenRefreshEmptyResponse);

            var refreshed = ToTokenSet(tokenResponse) with { RefreshToken = tokenResponse.RefreshToken ?? current.RefreshToken };
            // The account may have been removed while the request ran - don't bring it back.
            if (!TokenStore.Exists(accountKey))
                return null;
            TokenStore.Save(accountKey, refreshed);
            return refreshed.AccessToken;
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private async Task<TokenResponse> ExchangeCodeAsync(string code, string state, string codeVerifier, string redirectUri, CancellationToken cancellationToken)
    {
        var body = new
        {
            grant_type = "authorization_code",
            code,
            redirect_uri = redirectUri,
            client_id = OAuthConfig.ClientId,
            code_verifier = codeVerifier,
            state,
        };

        using var response = await _http.PostAsJsonAsync(OAuthConfig.TokenUrl, body, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(Strings.TokenExchangeFailed((int)response.StatusCode, text));
        }

        return await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException(Strings.TokenExchangeEmptyResponse);
    }

    private static TokenSet ToTokenSet(TokenResponse response) => new(
        response.AccessToken,
        response.RefreshToken,
        response.ExpiresIn is { } seconds ? DateTimeOffset.UtcNow.AddSeconds(seconds) : null,
        response.Scope);

    private static string BuildAuthorizeUrl(string redirectUri, string codeChallenge, string state)
    {
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = OAuthConfig.ClientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = OAuthConfig.Scope,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
        };
        var queryString = string.Join("&", query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return $"{OAuthConfig.AuthorizeUrl}?{queryString}";
    }
}
