package eu.schicht8.aiquota.core

/**
 * Endpoints and the public PKCE client id of the official Claude Code CLI - the same
 * "installed app" OAuth client the Windows app uses (see AIQuota/Auth/OAuthConfig.cs). No
 * client secret is involved, by design.
 */
object OAuthConfig {
    const val CLIENT_ID = "9d1c250a-e61b-44d9-88ed-5944d1962f5e"

    const val AUTHORIZE_URL = "https://claude.com/cai/oauth/authorize"
    const val TOKEN_URL = "https://platform.claude.com/v1/oauth/token"

    /** `cedar_ember=1` adds the limit reset status (#21) to the response. */
    const val USAGE_URL = "https://api.anthropic.com/api/oauth/usage?cedar_ember=1"

    /** The account profile - the access token itself carries no identity. */
    const val PROFILE_URL = "https://api.anthropic.com/api/oauth/profile"

    /** Minimal scope for a read-only usage viewer, as on Windows. */
    const val SCOPE = "user:inference user:profile"

    /** The registered loopback redirect ports, in order. */
    val LOOPBACK_PORTS = listOf(3000, 4000)

    fun redirectUri(port: Int) = "http://localhost:$port/callback"
}
