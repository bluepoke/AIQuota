package eu.schicht8.aiquota.core

import java.math.BigDecimal
import java.math.RoundingMode
import java.time.Duration
import java.time.Instant

/** One successful usage fetch of one account. Percentages are rounded for display; the
 * `*Utilization` values are the API's raw 0-100 numbers, which the history keeps. */
data class UsageSnapshot(
    val sessionPercent: Int,
    val weeklyPercent: Int,
    val sessionResetsAt: Instant?,
    val weeklyResetsAt: Instant?,
    val fetchedAt: Instant,
    val creditUsed: BigDecimal? = null,
    val creditLimit: BigDecimal? = null,
    val creditCurrency: String? = null,
    val sessionUtilization: Double? = null,
    val weeklyUtilization: Double? = null,
    val additionalLimits: List<UsageLimitInfo> = emptyList(),
    /** Null when the response had no (readable) limit reset status. */
    val limitResets: LimitResetStatus? = null,
) {
    /** No 5-hour session is running: it starts with the next message. */
    val sessionNotStarted: Boolean get() = sessionResetsAt == null

    /** Credits used, in percent of the limit; null without credits. */
    val creditPercent: Int?
        get() {
            val used = creditUsed ?: return null
            val limit = creditLimit ?: return null
            if (limit.signum() <= 0) return null
            return used.multiply(BigDecimal(100)).divide(limit, 0, RoundingMode.HALF_EVEN).toInt().coerceIn(0, 100)
        }
}

/** Rounds as .NET's Math.Round does by default - halves to the even neighbour - so a value
 * shows the same percentage as in the Windows app (12.5 → 12, 13.5 → 14). */
internal fun Double.roundHalfEven(): Int = Math.rint(this).toInt()

/** The signed-in account, from the profile endpoint: [id] keys the history (the account
 * UUID, else the email), [displayLabel] is "Name (email)", just the name, or just the email. */
data class AccountInfo(val id: String?, val displayLabel: String?)

enum class UsageFetchStatus { Ok, NotLoggedIn, AuthExpired, NetworkError }

/** Why a fetch failed - formatted for display by [Strings.fetchError], so the text follows the
 * language at the time it's shown. */
sealed interface FetchError {
    data object NoInternet : FetchError
    data object Timeout : FetchError
    data object EmptyResponse : FetchError
    data class Http(val status: Int) : FetchError
    data class UnexpectedFormat(val message: String) : FetchError
    data class Other(val message: String) : FetchError
}

/** [rateLimited]: the API answered 429 - the caller waits longer than usual before asking
 * for this account again, at least [retryAfter] if given. */
data class UsageFetchResult(
    val status: UsageFetchStatus,
    val snapshot: UsageSnapshot? = null,
    val error: FetchError? = null,
    val rateLimited: Boolean = false,
    val retryAfter: Duration? = null,
)
