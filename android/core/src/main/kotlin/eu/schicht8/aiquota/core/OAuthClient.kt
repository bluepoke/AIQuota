package eu.schicht8.aiquota.core

import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.serialization.SerializationException
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import java.io.IOException
import java.net.URLEncoder
import java.security.MessageDigest
import java.security.SecureRandom
import java.time.Instant
import java.util.Base64
import java.util.concurrent.ConcurrentHashMap

/** Why a login failed - formatted for display by [Strings.loginError]. */
sealed class LoginError : Exception() {
    data class Rejected(val error: String) : LoginError()
    data object InvalidResponse : LoginError() {
        private fun readResolve(): Any = InvalidResponse
    }
    data class TokenExchangeFailed(val status: Int, val text: String) : LoginError()
    data object NoPort : LoginError() {
        private fun readResolve(): Any = NoPort
    }
}

/** A started login: open [authorizeUrl], then pass the redirect to [OAuthClient.completeLogin]. */
data class LoginRequest(val authorizeUrl: String, val verifier: String, val state: String, val redirectUri: String)

/**
 * The PKCE login and token refresh with the official Claude Code client - a port of
 * AIQuota/Auth/OAuthClient.cs. Every account is a separate, explicit login; the tokens go to
 * [tokenStore], per account.
 */
class OAuthClient(
    private val transport: HttpTransport,
    private val tokenStore: TokenStore,
    private val clock: () -> Instant = Instant::now,
) : AccessTokenProvider {
    /** One lock per account, so a token is never refreshed twice at once. */
    private val refreshLocks = ConcurrentHashMap<String, Mutex>()

    fun beginLogin(redirectUri: String): LoginRequest {
        val verifier = randomUrlSafe()
        val challenge = base64Url(MessageDigest.getInstance("SHA-256").digest(verifier.toByteArray(Charsets.US_ASCII)))
        val state = randomUrlSafe()
        val query = linkedMapOf(
            "response_type" to "code",
            "client_id" to OAuthConfig.CLIENT_ID,
            "redirect_uri" to redirectUri,
            "scope" to OAuthConfig.SCOPE,
            "code_challenge" to challenge,
            "code_challenge_method" to "S256",
            "state" to state,
        ).entries.joinToString("&") { (k, v) -> "${escape(k)}=${escape(v)}" }
        return LoginRequest("${OAuthConfig.AUTHORIZE_URL}?$query", verifier, state, redirectUri)
    }

    /** Checks the redirect and exchanges its code for tokens; throws a [LoginError]. */
    suspend fun completeLogin(request: LoginRequest, redirect: AuthCodeResult): TokenSet {
        redirect.error?.let { throw LoginError.Rejected(it) }
        val code = redirect.code
        if (code == null || redirect.state != request.state) throw LoginError.InvalidResponse

        val body = buildJsonObject {
            put("grant_type", "authorization_code")
            put("code", code)
            put("redirect_uri", request.redirectUri)
            put("client_id", OAuthConfig.CLIENT_ID)
            put("code_verifier", request.verifier)
            put("state", request.state)
        }
        val response = post(body)
        if (!response.isSuccess) throw LoginError.TokenExchangeFailed(response.status, response.body)
        return toTokenSet(response.body, previousRefreshToken = null) ?: throw LoginError.TokenExchangeFailed(response.status, response.body)
    }

    override suspend fun validAccessToken(accountKey: String): String? {
        val tokens = tokenStore.load(accountKey) ?: return null
        if (!tokens.isExpiredOrExpiringSoon(clock())) return tokens.accessToken
        if (tokens.refreshToken == null) return tokens.accessToken
        return refresh(accountKey)
    }

    private suspend fun refresh(accountKey: String): String? =
        refreshLocks.getOrPut(accountKey) { Mutex() }.withLock {
            // Another caller may have refreshed while we waited for the lock.
            val current = tokenStore.load(accountKey) ?: return@withLock null
            if (!current.isExpiredOrExpiringSoon(clock())) return@withLock current.accessToken

            val response = post(
                buildJsonObject {
                    put("grant_type", "refresh_token")
                    put("refresh_token", current.refreshToken)
                    put("client_id", OAuthConfig.CLIENT_ID)
                    put("scope", OAuthConfig.SCOPE)
                },
            )
            if (response.status in 400..499) {
                // Refresh token no longer valid - drop it, so the account shows "login
                // required" instead of looping. Other accounts aren't affected.
                tokenStore.clear(accountKey)
                return@withLock null
            }
            if (!response.isSuccess) throw IOException("HTTP ${response.status}")

            val refreshed = toTokenSet(response.body, current.refreshToken)
                ?: throw IOException("Empty response during token refresh")
            // The account may have been removed while the request ran - don't bring it back.
            if (!tokenStore.exists(accountKey)) return@withLock null
            tokenStore.save(accountKey, refreshed)
            refreshed.accessToken
        }

    private suspend fun post(body: JsonObject) = transport.execute(
        HttpRequest("POST", OAuthConfig.TOKEN_URL, mapOf("Content-Type" to "application/json"), body.toString()),
    )

    private fun toTokenSet(body: String, previousRefreshToken: String?): TokenSet? = try {
        val json = Json.parseToJsonElement(body) as? JsonObject
        val accessToken = json?.string("access_token")
        if (json == null || accessToken == null) null
        else TokenSet(
            accessToken,
            json.string("refresh_token") ?: previousRefreshToken,
            json.long("expires_in")?.let { clock().plusSeconds(it) },
            json.string("scope"),
        )
    } catch (_: SerializationException) {
        null
    }

    private companion object {
        val random = SecureRandom()

        fun randomUrlSafe(): String = base64Url(ByteArray(32).also(random::nextBytes))

        fun base64Url(bytes: ByteArray): String = Base64.getUrlEncoder().withoutPadding().encodeToString(bytes)

        // The String overload: the Charset one exists on Android only from API 33.
        fun escape(text: String): String = URLEncoder.encode(text, "UTF-8").replace("+", "%20")
    }
}
