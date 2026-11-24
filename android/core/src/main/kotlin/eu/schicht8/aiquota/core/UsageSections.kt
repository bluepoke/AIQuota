package eu.schicht8.aiquota.core

import java.time.Duration
import java.time.Instant

/** Lengths of the two usage windows, and the pace derived from them. */
object UsageWindows {
    val SESSION: Duration = Duration.ofHours(5)
    val WEEK: Duration = Duration.ofDays(7)

    /** One scale section per hour of the session (5). */
    val sessionHours: Int get() = maxOf(1, SESSION.toHours().toInt())

    /** One scale section per day of the week (7). */
    val weekDays: Int get() = maxOf(1, WEEK.toDays().toInt())

    /** Elapsed fraction (0-1) of the window of [length] that resets at [resetsAt] - where usage
     * would be if the quota were spread evenly over the window. */
    fun paceFraction(resetsAt: Instant, now: Instant, length: Duration): Double =
        (1.0 - Duration.between(now, resetsAt).toMillis().toDouble() / length.toMillis()).coerceIn(0.0, 1.0)
}

/** A line of text under a bar. [emphasized]: in the primary text colour (a limit forecast).
 * [opensUsagePage]: an available limit reset - opens the usage page on a tap. */
data class SectionLine(val text: String, val emphasized: Boolean = false, val opensUsagePage: Boolean = false)

/** One usage bar with everything shown around it. [key]: "session", "week", "credits" or
 * "limit:<limit key>". [sections]: how many equal parts the bar's scale ticks divide it into. */
data class UsageSection(
    val key: String,
    val label: String,
    val percent: Int,
    val outlineOnly: Boolean,
    val paceFraction: Double?,
    val projectedPercent: Double?,
    val sections: Int,
    val details: List<SectionLine>,
)

/**
 * Builds an account card's [UsageSection]s, in the order they're shown: session, additional
 * limits of the session window, week, all other additional limits, credits - a port of
 * AIQuota/UsageSections.cs, so the phone shows what the Windows flyout shows.
 */
object UsageSections {
    const val SESSION_KEY = "session"
    const val WEEK_KEY = "week"
    const val CREDITS_KEY = "credits"

    fun limitKey(limit: UsageLimitInfo) = "limit:" + limit.key

    fun build(
        snapshot: UsageSnapshot,
        sessionForecast: UsageForecast,
        weeklyForecast: UsageForecast,
        showPaceMarker: Boolean,
        now: Instant,
        strings: Strings,
    ): List<UsageSection> {
        val sections = mutableListOf<UsageSection>()
        val limits = snapshot.additionalLimits.sortedWith(compareBy(String.CASE_INSENSITIVE_ORDER) { strings.limitLabel(it) })
        // Filtered by `now`, so an offer that has ended disappears before the next fetch.
        val resetOffers = snapshot.limitResets?.offers(now).orEmpty()

        // Session - no pace before a session starts: there's no clock running yet.
        val sessionReset = snapshot.sessionResetsAt
        val sessionPace = sessionReset?.let { UsageWindows.paceFraction(it, now, UsageWindows.SESSION) }
        val sessionDetails = mutableListOf(
            SectionLine(
                if (sessionReset == null) strings.sessionNotStarted
                else strings.resetLine(sessionReset, now) + paceSuffix(strings, showPaceMarker, sessionPace),
            ),
        )
        addForecast(sessionDetails, sessionForecast, strings)
        addResetOffers(sessionDetails, resetOffers.filter { it.isSessionKind }, now, strings)
        sections += UsageSection(
            SESSION_KEY, strings.sessionTitle, snapshot.sessionPercent, outlineOnly = snapshot.sessionNotStarted,
            paceFraction = if (showPaceMarker) sessionPace else null, projectedPercent = sessionForecast.projectedPercent,
            sections = UsageWindows.sessionHours, details = sessionDetails,
        )

        sections += limits.filter { it.isSessionGroup }.map { limitSection(it, now, strings) }

        // Week
        val weekReset = snapshot.weeklyResetsAt
        val weekPace = weekReset?.let { UsageWindows.paceFraction(it, now, UsageWindows.WEEK) }
        val weekDetails = mutableListOf<SectionLine>()
        if (weekReset != null) {
            weekDetails += SectionLine(strings.resetLine(weekReset, now) + paceSuffix(strings, showPaceMarker, weekPace))
        }
        addForecast(weekDetails, weeklyForecast, strings)
        addResetOffers(weekDetails, resetOffers.filter { !it.isSessionKind }, now, strings)
        sections += UsageSection(
            WEEK_KEY, strings.weekTitle, snapshot.weeklyPercent, outlineOnly = false,
            paceFraction = if (showPaceMarker) weekPace else null, projectedPercent = weeklyForecast.projectedPercent,
            sections = UsageWindows.weekDays, details = weekDetails,
        )

        sections += limits.filter { !it.isSessionGroup }.map { limitSection(it, now, strings) }

        // Credits
        val creditPercent = snapshot.creditPercent
        val used = snapshot.creditUsed
        val limit = snapshot.creditLimit
        val currency = snapshot.creditCurrency
        if (creditPercent != null && used != null && limit != null && currency != null) {
            sections += UsageSection(
                CREDITS_KEY, strings.creditsTitle, creditPercent, outlineOnly = false, paceFraction = null,
                projectedPercent = null, sections = 4, details = listOf(SectionLine(strings.creditsAmount(used, limit, currency))),
            )
        }
        return sections
    }

    /** An additional limit: no pace or projection (there's no history for it), but its reset
     * time and its share of the weekly allowance when the API says so. */
    private fun limitSection(limit: UsageLimitInfo, now: Instant, strings: Strings): UsageSection {
        val details = buildList {
            limit.resetsAt?.let { add(SectionLine(strings.resetLine(it, now))) }
            limit.allowanceShareOfWeekly?.let { add(SectionLine(strings.limitAllowanceLine(it))) }
        }
        return UsageSection(
            limitKey(limit), strings.limitLabel(limit), limit.roundedPercent, outlineOnly = false,
            paceFraction = null, projectedPercent = null, sections = sectionsFor(limit.group), details = details,
        )
    }

    private fun paceSuffix(strings: Strings, show: Boolean, pace: Double?) =
        if (show && pace != null) strings.paceSuffix((pace * 100).roundHalfEven()) else ""

    /** "Limit in ~X at current rate" (emphasized) or "On track to reset first". */
    private fun addForecast(lines: MutableList<SectionLine>, forecast: UsageForecast, strings: Strings) {
        when (forecast.kind) {
            ForecastKind.Limit -> lines += SectionLine("↳ " + strings.forecastLimitIn(forecast.timeToLimit), emphasized = true)
            ForecastKind.ResetFirst -> lines += SectionLine("↳ " + strings.forecastResetFirst)
            ForecastKind.None -> Unit
        }
    }

    private fun addResetOffers(lines: MutableList<SectionLine>, offers: List<LimitResetOffer>, now: Instant, strings: Strings) {
        offers.forEach { lines += SectionLine(strings.limitResetLine(it, now), emphasized = true, opensUsagePage = true) }
    }

    /** One scale section per hour of the session, per day of the week, quarters otherwise. */
    fun sectionsFor(group: String): Int = when (group.lowercase()) {
        "session" -> UsageWindows.sessionHours
        "weekly" -> UsageWindows.weekDays
        else -> 4
    }
}
