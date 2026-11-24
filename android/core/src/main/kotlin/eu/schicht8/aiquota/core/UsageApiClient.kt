package eu.schicht8.aiquota.core

import kotlinx.serialization.SerializationException
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import java.io.IOException
import java.math.BigDecimal
import java.net.ConnectException
import java.net.NoRouteToHostException
import java.net.SocketTimeoutException
import java.net.UnknownHostException
import java.time.Duration
import java.time.Instant
import java.time.ZonedDateTime
import java.time.format.DateTimeFormatter
import java.time.format.DateTimeParseException

/** A currently valid access token for a saved account, refreshed first if needed; null if
 * the account has no token (any more). Implemented by [OAuthClient]. */
fun interface AccessTokenProvider {
    suspend fun validAccessToken(accountKey: String): String?
}

/**
 * Calls the same GET /api/oauth/usage endpoint the official Claude Code CLI uses, and the
 * profile endpoint for the account's identity - a port of AIQuota/UsageApiClient.cs.
 * [userAgent] is Claude Code's User-Agent (see [ClaudeCodeVersion]): the endpoint reports
 * limit resets only to Claude Code.
 */
class UsageApiClient(
    private val transport: HttpTransport,
    private val tokens: AccessTokenProvider,
    private val userAgent: suspend () -> String,
    private val clock: () -> Instant = Instant::now,
) : UsageSource {
    override suspend fun fetch(accountKey: String): UsageFetchResult = try {
        val accessToken = tokens.validAccessToken(accountKey)
        if (accessToken == null) {
            UsageFetchResult(UsageFetchStatus.NotLoggedIn)
        } else {
            val response = transport.execute(
                HttpRequest(
                    "GET", OAuthConfig.USAGE_URL,
                    mapOf("Authorization" to "Bearer $accessToken", "User-Agent" to userAgent()),
                ),
            )
            when {
                response.status == 401 -> UsageFetchResult(UsageFetchStatus.AuthExpired)
                response.status == 429 -> UsageFetchResult(
                    UsageFetchStatus.NetworkError, error = FetchError.Http(429),
                    rateLimited = true, retryAfter = retryAfter(response.headers["retry-after"]),
                )
                !response.isSuccess -> UsageFetchResult(UsageFetchStatus.NetworkError, error = FetchError.Http(response.status))
                else -> when (val root = Json.parseToJsonElement(response.body)) {
                    is JsonNull -> UsageFetchResult(UsageFetchStatus.NetworkError, error = FetchError.EmptyResponse)
                    is JsonObject -> UsageFetchResult(UsageFetchStatus.Ok, snapshotOf(root))
                    else -> UsageFetchResult(UsageFetchStatus.NetworkError, error = FetchError.UnexpectedFormat("not an object"))
                }
            }
        }
    } catch (e: SerializationException) {
        UsageFetchResult(UsageFetchStatus.NetworkError, error = FetchError.UnexpectedFormat(e.message ?: ""))
    } catch (e: IOException) {
        UsageFetchResult(UsageFetchStatus.NetworkError, error = networkError(e))
    }

    /** The account's profile (see [fetchAccountWithToken]); null on any failure - the label is
     * a "nice to have", and without an ID the history just isn't recorded until a later try. */
    override suspend fun fetchAccount(accountKey: String): AccountInfo? = try {
        tokens.validAccessToken(accountKey)?.let { fetchAccountWithToken(it) }
    } catch (_: IOException) {
        null
    }

    /** The profile for a token that isn't stored yet - right after a login, to find out which
     * account it belongs to: the UUID as a stable ID (else the email), and "Name (email)",
     * just the name, or just the email as its label. Null on any failure. */
    suspend fun fetchAccountWithToken(accessToken: String): AccountInfo? = try {
        val response = transport.execute(
            HttpRequest("GET", OAuthConfig.PROFILE_URL, mapOf("Authorization" to "Bearer $accessToken")),
        )
        val account = if (response.isSuccess) (Json.parseToJsonElement(response.body) as? JsonObject)?.obj("account") else null
        account?.let {
            val name = listOf("display_name", "full_name").firstNotNullOfOrNull { field -> it.string(field)?.takeIf(String::isNotEmpty) }
            val email = it.string("email")?.takeIf(String::isNotEmpty)
            val uuid = it.string("uuid")?.takeIf(String::isNotEmpty)
            val label = when {
                name != null && email != null -> "$name ($email)"
                else -> name ?: email
            }
            AccountInfo(uuid ?: email, label)
        }
    } catch (_: SerializationException) {
        null
    } catch (_: IOException) {
        null
    }

    private fun snapshotOf(root: JsonObject): UsageSnapshot {
        val fiveHour = root.obj("five_hour")
        val sevenDay = root.obj("seven_day")
        val session = fiveHour?.number("utilization")?.coerceIn(0.0, 100.0)
        val week = sevenDay?.number("utilization")?.coerceIn(0.0, 100.0)
        val spend = root.obj("spend")?.takeIf { it.bool("enabled") == true }
        val used = spend?.obj("used")?.let(::money)
        val limit = spend?.obj("limit")?.let(::money)
        val credits = if (used != null && limit != null) used to limit else null

        return UsageSnapshot(
            sessionPercent = session?.roundHalfEven() ?: 0,
            weeklyPercent = week?.roundHalfEven() ?: 0,
            sessionResetsAt = fiveHour?.instant("resets_at"),
            weeklyResetsAt = sevenDay?.instant("resets_at"),
            fetchedAt = clock(),
            creditUsed = credits?.first?.value,
            creditLimit = credits?.second?.value,
            creditCurrency = credits?.first?.currency,
            sessionUtilization = session,
            weeklyUtilization = week,
            additionalLimits = UsageLimitParser.parseAdditional(root["limits"], root["weekly_scoped_shares"]),
            limitResets = LimitResetParser.parse(root["cedar_ember"]),
        )
    }

    /** `Retry-After` as seconds or as an HTTP date; null if missing, unreadable or not in the future. */
    private fun retryAfter(header: String?): Duration? {
        val text = header?.trim() ?: return null
        val delay = text.toLongOrNull()?.let(Duration::ofSeconds)
            ?: try {
                Duration.between(clock(), ZonedDateTime.parse(text, DateTimeFormatter.RFC_1123_DATE_TIME).toInstant())
            } catch (_: DateTimeParseException) {
                null
            }
        return delay?.takeIf { !it.isNegative && !it.isZero }
    }

    private class Money(val value: BigDecimal, val currency: String)

    /** `{ "amount_minor": 1234, "currency": "EUR", "exponent": 2 }` → 12.34 EUR. */
    private fun money(json: JsonObject): Money? {
        val minor = json.long("amount_minor") ?: return null
        val currency = json.string("currency") ?: return null
        val exponent = json.long("exponent")?.toInt() ?: return null
        return Money(BigDecimal.valueOf(minor, exponent), currency)
    }
}

/** The [FetchError] for a request that got no response. */
internal fun networkError(e: IOException): FetchError = when (e) {
    is UnknownHostException, is ConnectException, is NoRouteToHostException -> FetchError.NoInternet
    is SocketTimeoutException -> FetchError.Timeout
    else -> FetchError.Other(e.message ?: e.javaClass.simpleName)
}
