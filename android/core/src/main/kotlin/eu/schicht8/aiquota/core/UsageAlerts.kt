package eu.schicht8.aiquota.core

import java.time.Duration
import java.time.Instant

/** A kind of usage notification - one Android notification channel each, so every kind can
 * be turned off on its own. */
enum class UsageAlertKind {
    /** The 5-hour session crossed the warning threshold. */
    Session,

    /** The weekly usage crossed the warning threshold. */
    Weekly,

    /** At the current rate, the session is about to hit its limit. */
    SessionForecast,

    /** An additional limit crossed the warning threshold; one notification per limit. */
    AdditionalLimit,

    /** The account was offered new limit resets (#21). */
    LimitResetAvailable,

    /** An unused limit reset ends soon (#21). */
    LimitResetExpiring,
}

/** A notification to show. [expiresAt]: when it's out of date (the window resets) and may be
 * removed; null keeps it. [limitKey]: for [UsageAlertKind.AdditionalLimit], which limit - each
 * replaces only its own previous notification. */
data class UsageAlert(val kind: UsageAlertKind, val text: String, val expiresAt: Instant?, val limitKey: String? = null)

/**
 * What has been notified for one account, so a threshold isn't notified again until usage
 * dropped below it - kept on disk, since on Android the background fetch often runs in a
 * fresh process. [notifiedLimitResets]: "available:<grant id>" and "expiring:<grant id>".
 */
data class AlertState(
    val sessionWarningShown: Boolean = false,
    val weeklyWarningShown: Boolean = false,
    val sessionForecastWarningShown: Boolean = false,
    val limitWarningsShown: Set<String> = emptySet(),
    val notifiedLimitResets: Set<String> = emptySet(),
)

data class AlertEvaluation(val alerts: List<UsageAlert>, val state: AlertState)

/**
 * Which usage notifications a fetch calls for - the rules of the Windows app's
 * UsageTrayContext.MaybeWarn (#10, #21): a threshold once until usage drops below it again,
 * a session forecast once until it's comfortably far away again, and each limit reset grant
 * once when it appears and once when its unused resets are about to end.
 */
object UsageAlerts {
    const val WARN_THRESHOLD_PERCENT = 90

    /** Notify when the session limit is forecast within this time... */
    val FORECAST_WARN_WITHIN: Duration = Duration.ofMinutes(30)

    /** ...and again only after the forecast was beyond this (or gone). */
    val FORECAST_REARM_ABOVE: Duration = Duration.ofMinutes(45)

    /** Notify once that an unused limit reset ends within this time. */
    val LIMIT_RESET_EXPIRY_WARN_WITHIN: Duration = Duration.ofHours(24)

    /** [accountName]: prefixed to every text ("Work: …") - given when several accounts are
     * saved, so the notification says which one it's about. */
    fun evaluate(
        snapshot: UsageSnapshot,
        sessionForecast: UsageForecast,
        state: AlertState,
        accountName: String?,
        now: Instant,
        strings: Strings,
    ): AlertEvaluation {
        val alerts = mutableListOf<UsageAlert>()
        fun text(text: String) = if (accountName != null) "$accountName: $text" else text

        /** A threshold is notified once, and re-armed once usage is below it again. Returns
         * whether it's now shown. */
        fun threshold(percent: Int, shown: Boolean, alert: () -> UsageAlert): Boolean {
            if (percent < WARN_THRESHOLD_PERCENT) return false
            if (!shown) alerts += alert()
            return true
        }

        val sessionShown = threshold(snapshot.sessionPercent, state.sessionWarningShown) {
            UsageAlert(UsageAlertKind.Session, text(strings.notificationSessionWarning(snapshot.sessionPercent, snapshot.sessionResetsAt, now)), snapshot.sessionResetsAt)
        }
        val weeklyShown = threshold(snapshot.weeklyPercent, state.weeklyWarningShown) {
            UsageAlert(UsageAlertKind.Weekly, text(strings.notificationWeeklyWarning(snapshot.weeklyPercent, snapshot.weeklyResetsAt, now)), snapshot.weeklyResetsAt)
        }
        // Once per limit, until it drops below the threshold again or is no longer reported.
        val limitsShown = snapshot.additionalLimits.filter { limit ->
            threshold(limit.roundedPercent, limit.key in state.limitWarningsShown) {
                UsageAlert(UsageAlertKind.AdditionalLimit, text(strings.notificationLimitWarning(limit, now)), limit.resetsAt, limit.key)
            }
        }.map { it.key }.toSet()

        var forecastShown = state.sessionForecastWarningShown
        val limitIn = sessionForecast.limitIn
        if (limitIn != null && limitIn <= FORECAST_WARN_WITHIN) {
            if (!forecastShown) {
                alerts += UsageAlert(UsageAlertKind.SessionForecast, text(strings.notificationSessionForecast(limitIn)), snapshot.sessionResetsAt)
                forecastShown = true
            }
        } else if (limitIn == null || limitIn > FORECAST_REARM_ABOVE) {
            // No forecast any more (idle, reset first, new session) or comfortably far away.
            forecastShown = false
        }

        val notified = snapshot.limitResets?.let { limitResetAlerts(it, state.notifiedLimitResets, now, strings, ::text, alerts) }
            ?: state.notifiedLimitResets

        return AlertEvaluation(
            alerts,
            AlertState(sessionShown, weeklyShown, forecastShown, limitsShown, notified),
        )
    }

    /** Adds the limit reset notifications to [alerts] and returns the updated set of notified
     * grants. Grants the response no longer lists are forgotten. */
    private fun limitResetAlerts(
        resets: LimitResetStatus,
        previouslyNotified: Set<String>,
        now: Instant,
        strings: Strings,
        text: (String) -> String,
        alerts: MutableList<UsageAlert>,
    ): Set<String> {
        val known = resets.grants.map { it.id }.toSet()
        val notified = previouslyNotified.filter { it.substringAfter(':') in known }.toMutableSet()
        val available = if (resets.eligible) resets.grants.filter { it.isAvailable(now) } else emptyList()
        fun endsSoon(grant: LimitResetGrant) =
            grant.endsAt != null && Duration.between(now, grant.endsAt) <= LIMIT_RESET_EXPIRY_WARN_WITHIN

        val fresh = available.filter { "available:${it.id}" !in notified }
        if (fresh.isNotEmpty()) {
            val offers = resets.offers(now)
            alerts += UsageAlert(UsageAlertKind.LimitResetAvailable, text(strings.notificationLimitResetAvailable(offers, now)), offers.mapNotNull { it.firstEndsAt }.minOrNull())
            for (grant in fresh) {
                notified += "available:${grant.id}"
                if (endsSoon(grant)) notified += "expiring:${grant.id}"
            }
        }
        for (grant in available) {
            if (endsSoon(grant) && notified.add("expiring:${grant.id}")) {
                alerts += UsageAlert(UsageAlertKind.LimitResetExpiring, text(strings.notificationLimitResetExpiring(grant, now)), grant.endsAt)
            }
        }
        return notified
    }
}
