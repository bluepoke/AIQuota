package eu.schicht8.aiquota.core

import java.time.Duration
import java.time.Instant
import java.time.ZoneId
import kotlin.test.Test
import kotlin.test.assertEquals

class UsageAlertsTest {
    private val strings = Strings(AppLanguage.English, ZoneId.of("Europe/Berlin"))

    /** 10:00 in Berlin. */
    private val now = Instant.parse("2026-11-24T09:00:00Z")
    private val sessionReset = Instant.parse("2026-11-24T13:30:00Z")
    private val weekReset = Instant.parse("2026-11-30T08:00:00Z")

    private fun snapshot(session: Int, week: Int = 10, limits: List<UsageLimitInfo> = emptyList(), resets: LimitResetStatus? = null) =
        UsageSnapshot(session, week, sessionReset, weekReset, now, additionalLimits = limits, limitResets = resets)

    private fun evaluate(snapshot: UsageSnapshot, state: AlertState = AlertState(), forecast: UsageForecast = UsageForecast.NONE, name: String? = null) =
        UsageAlerts.evaluate(snapshot, forecast, state, name, now, strings)

    @Test
    fun `a threshold is notified once, and again only after usage dropped below it`() {
        val first = evaluate(snapshot(session = 92))
        assertEquals(listOf(UsageAlert(UsageAlertKind.Session, "Session usage (5h) at 92% · resets 14:30", sessionReset)), first.alerts)

        val still = evaluate(snapshot(session = 95), first.state)
        assertEquals(emptyList(), still.alerts)

        val dropped = evaluate(snapshot(session = 10), still.state)
        assertEquals(emptyList(), dropped.alerts)
        assertEquals(1, evaluate(snapshot(session = 90), dropped.state).alerts.size)
    }

    @Test
    fun `week and each additional limit have their own notification, with the account's name when there are several`() {
        val opus = UsageLimitInfo("weekly:opus", "weekly", "Opus", 91.0, weekReset)
        val result = evaluate(snapshot(session = 20, week = 90, limits = listOf(opus)), name = "Work")

        assertEquals(
            listOf(
                UsageAlert(UsageAlertKind.Weekly, "Work: Weekly usage at 90% · resets Mon 09:00", weekReset),
                UsageAlert(UsageAlertKind.AdditionalLimit, "Work: Week · Opus at 91% · resets Mon 09:00", weekReset, "weekly:opus"),
            ),
            result.alerts,
        )

        // A limit that's no longer reported is forgotten, so it's notified again when it comes back.
        val gone = evaluate(snapshot(session = 20, week = 90), result.state)
        assertEquals(1, evaluate(snapshot(session = 20, week = 90, limits = listOf(opus)), gone.state).alerts.size)
    }

    @Test
    fun `a session forecast within 30 minutes is notified once, re-armed only beyond 45 minutes`() {
        fun limitIn(minutes: Long) = UsageForecast(ForecastKind.Limit, Duration.ofMinutes(minutes), 100.0)

        val first = evaluate(snapshot(session = 60), forecast = limitIn(25))
        assertEquals(
            listOf(UsageAlert(UsageAlertKind.SessionForecast, "Session limit (5h) in ~25min at current rate", sessionReset)),
            first.alerts,
        )

        val between = evaluate(snapshot(session = 60), first.state, limitIn(40))
        assertEquals(emptyList(), evaluate(snapshot(session = 60), between.state, limitIn(20)).alerts, "not re-armed at 40 minutes")

        val later = evaluate(snapshot(session = 60), between.state, limitIn(50))
        assertEquals(1, evaluate(snapshot(session = 60), later.state, limitIn(20)).alerts.size)
    }

    @Test
    fun `new limit resets are notified once, and once more when an unused one is about to end`() {
        fun grant(id: String, endsIn: Duration) = LimitResetGrant(id, LimitResetKind.Weekly, 1, 1, null, now.plus(endsIn), paused = false, useRequiresLimit = false)
        val later = grant("g1", Duration.ofDays(5))

        val offered = evaluate(snapshot(session = 10, resets = LimitResetStatus(true, listOf(later))))
        assertEquals(
            listOf(
                UsageAlert(
                    UsageAlertKind.LimitResetAvailable,
                    "Limit reset available: 1 weekly reset · use by Sun 10:00. Use it in Claude Code with /limit-reset.",
                    now.plus(Duration.ofDays(5)),
                ),
            ),
            offered.alerts,
        )
        assertEquals(emptyList(), evaluate(snapshot(session = 10, resets = LimitResetStatus(true, listOf(later))), offered.state).alerts)

        val endingSoon = later.copy(endsAt = now.plus(Duration.ofHours(20)))
        assertEquals(
            listOf(
                UsageAlert(
                    UsageAlertKind.LimitResetExpiring,
                    "Unused weekly reset ends Wed 06:00 (1 left). Use it in Claude Code with /limit-reset.",
                    now.plus(Duration.ofHours(20)),
                ),
            ),
            evaluate(snapshot(session = 10, resets = LimitResetStatus(true, listOf(endingSoon))), offered.state).alerts,
        )

        // One that first shows up close to its end only gets the first one - it says when it ends.
        val lateGrant = grant("g2", Duration.ofHours(3))
        assertEquals(
            listOf(UsageAlertKind.LimitResetAvailable),
            evaluate(snapshot(session = 10, resets = LimitResetStatus(true, listOf(lateGrant)))).alerts.map { it.kind },
        )
    }
}
