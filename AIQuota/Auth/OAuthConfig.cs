namespace AIQuota.Auth;

/// <summary>
/// Endpoints and the public PKCE client id used by the official Claude Code CLI
/// (extracted by string-inspecting the locally installed claude.exe). This is the
/// same "installed app" OAuth client every Claude Code install already uses to log
/// into a Claude.ai Pro/Max subscription - no client secret is involved, by design.
/// </summary>
public static class OAuthConfig
{
    public const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    public const string AuthorizeUrl = "https://claude.com/cai/oauth/authorize";
    public const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
    /// <summary><c>cedar_ember=1</c> adds the limit reset status (#21) to the response, as
    /// Claude Code asks for it for its <c>/limit-reset</c> command.</summary>
    public const string UsageUrl = "https://api.anthropic.com/api/oauth/usage?cedar_ember=1";

    /// <summary>
    /// Same account-profile endpoint the official CLI calls to resolve the signed-in
    /// user's email - the access token itself carries no identity claims.
    /// </summary>
    public const string ProfileUrl = "https://api.anthropic.com/api/oauth/profile";

    /// <summary>
    /// Minimal scope for a read-only usage viewer: identify the account and read its
    /// inference rate-limit status. Deliberately narrower than the full CLI scope set
    /// (which also covers MCP servers, file uploads, remote sessions, ...).
    /// </summary>
    public const string Scope = "user:inference user:profile";

    /// <summary>Ports the real client binds its loopback redirect listener to, in order.</summary>
    public static readonly int[] LoopbackPorts = [3000, 4000];

    public static string RedirectUri(int port) => $"http://localhost:{port}/callback";
}
