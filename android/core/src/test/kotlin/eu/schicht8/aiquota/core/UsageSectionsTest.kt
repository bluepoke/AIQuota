package eu.schicht8.aiquota.core

import java.math.BigDecimal
import java.time.Duration
import java.time.Instant
import java.time.ZoneId
import kotlin.test.Test
import kotlin.test.assertEquals

class UsageSectionsTest {
    // Tuesday, 11:00 in Berlin.
    private val now = Instant.parse("2026-11-24T10:00:00Z")
    private val berlin = ZoneId.of("Europe/Berlin")
    private val english = Strings(AppLanguage.English, berlin)
    private val german = Strings(AppLanguage.German, berlin)

    private val weekReset = Instant.parse("2026-11-30T08:00:00Z") // Monday 09:00 in Berlin
    private val snapshot = UsageSnapshot(
        sessionPercent = 62,
        weeklyPercent = 23,
        sessionResetsAt = Instant.parse("2026-11-24T12:30:00Z"), // 13:30 today
        weeklyResetsAt = weekReset,
        fetchedAt = now,
        creditUsed = BigDecimal("12.34"),
        creditLimit = BigDecimal("50.00"),
        creditCurrency = "EUR",
        additionalLimits = listOf(
            UsageLimitInfo("weekly_scoped:opus", "weekly", "Opus", 41.0, weekReset, allowanceShareOfWeekly = 50.0),
            UsageLimitInfo("session_scoped:claude code", "session", "Claude Code", 12.0, null),
        ),
        limitResets = LimitResetStatus(
            eligible = true,
            grants = listOf(
                LimitResetGrant("g1", LimitResetKind.Weekly, 1, 1, null, Instant.parse("2026-11-29T22:59:00Z"), paused = false, useRequiresLimit = true),
            ),
        ),
    )

    @Test
    fun `builds session, its limits, week, other limits and credits with their details`() {
        val sections = UsageSections.build(
            snapshot,
            sessionForecast = UsageForecast(ForecastKind.Limit, Duration.ofMinutes(40), 100.0),
            weeklyForecast = UsageForecast(ForecastKind.ResetFirst, projectedPercent = 60.0),
            showPaceMarker = true,
            now = now,
            strings = english,
        )

        assertEquals(
            listOf(
                UsageSection(
                    "session", "Session (5h)", 62, outlineOnly = false, paceFraction = 0.5, projectedPercent = 100.0, sections = 5,
                    details = listOf(
                        SectionLine("Resets 13:30 · in 2.5h · pace 50%"),
                        SectionLine("↳ Limit in ~40min at current rate", emphasized = true),
                    ),
                ),
                UsageSection(
                    "limit:session_scoped:claude code", "Session (5h) · Claude Code", 12, outlineOnly = false,
                    paceFraction = null, projectedPercent = null, sections = 5, details = emptyList(),
                ),
                UsageSection(
                    "week", "Week", 23, outlineOnly = false, paceFraction = 26.0 / 168.0, projectedPercent = 60.0, sections = 7,
                    details = listOf(
                        SectionLine("Resets Monday 09:00 · in 5d22h · pace 15%"),
                        SectionLine("↳ On track to reset first"),
                        SectionLine("↺ 1 weekly reset available · use by Sun 23:59 (in 5d12h)", emphasized = true, opensUsagePage = true),
                    ),
                ),
                UsageSection(
                    "limit:weekly_scoped:opus", "Week · Opus", 41, outlineOnly = false,
                    paceFraction = null, projectedPercent = null, sections = 7,
                    details = listOf(
                        SectionLine("Resets Monday 09:00 · in 5d22h"),
                        SectionLine("Can use up to 50% of the weekly limit"),
                    ),
                ),
                UsageSection(
                    "credits", "Usage credits", 25, outlineOnly = false, paceFraction = null, projectedPercent = null, sections = 4,
                    details = listOf(SectionLine("12.34 EUR of 50.00 EUR")),
                ),
            ),
            sections,
        )
    }

    @Test
    fun `shows a session that hasn't started as an outline, in German, without pace markers`() {
        val idle = snapshot.copy(
            sessionPercent = 0, sessionResetsAt = null, additionalLimits = emptyList(), limitResets = null,
            creditUsed = BigDecimal("1234.5"), creditLimit = BigDecimal("2000"),
        )

        val sections = UsageSections.build(idle, UsageForecast.NONE, UsageForecast.NONE, showPaceMarker = false, now = now, strings = german)

        assertEquals(
            listOf(
                UsageSection(
                    "session", "Sitzung (5h)", 0, outlineOnly = true, paceFraction = null, projectedPercent = null, sections = 5,
                    details = listOf(SectionLine("Nicht gestartet – beginnt mit deiner nächsten Nachricht")),
                ),
                UsageSection(
                    "week", "Woche", 23, outlineOnly = false, paceFraction = null, projectedPercent = null, sections = 7,
                    details = listOf(SectionLine("Reset Montag 09:00 · in 5d22h")),
                ),
                UsageSection(
                    "credits", "Nutzungsguthaben", 62, outlineOnly = false, paceFraction = null, projectedPercent = null, sections = 4,
                    details = listOf(SectionLine("1.234,50 EUR von 2.000,00 EUR")),
                ),
            ),
            sections,
        )
    }

    @Test
    fun `formats durations and countdowns as the Windows app does`() {
        assertEquals("45min", Strings.formatDuration(Duration.ofMinutes(45)))
        assertEquals("1min", Strings.formatDuration(Duration.ofSeconds(20)))
        assertEquals("1h", Strings.formatDuration(Duration.ofMinutes(60)))
        assertEquals("1.3h", Strings.formatDuration(Duration.ofMinutes(75)))
        assertEquals("23.9h", Strings.formatDuration(Duration.ofMinutes(23 * 60 + 55)))
        assertEquals("1d", Strings.formatDuration(Duration.ofHours(24)))
        assertEquals("4d15h", Strings.formatDuration(Duration.ofHours(4 * 24 + 15).plusMinutes(59)))

        assertEquals("Resets 11:00 · shortly", english.resetLine(now, now))
        assertEquals("Reset Mittwoch 01:00 · in 14h", german.resetLine(Instant.parse("2026-11-25T00:00:00Z"), now))
    }

    @Test
    fun `names additional limits after their window and scope`() {
        fun label(group: String, name: String?) = english.limitLabel(UsageLimitInfo("k", group, name, 1.0, null))

        assertEquals("Week · Opus", label("weekly", "Opus"))
        assertEquals("Session (5h)", label("session", null))
        assertEquals("Monthly Spend · Opus", label("monthly_spend", "Opus"))
        assertEquals(4, UsageSections.sectionsFor("monthly_spend"))
    }
}
