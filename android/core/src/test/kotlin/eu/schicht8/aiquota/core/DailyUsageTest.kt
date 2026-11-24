package eu.schicht8.aiquota.core

import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class DailyUsageTest {
    private val berlin = ZoneId.of("Europe/Berlin")
    private val weekReset = Instant.parse("2026-11-27T09:00:00Z")

    private fun record(time: String, week: Double, session: Double = 0.0, weekResetsAt: Instant? = weekReset) =
        UsageHistoryRecord(Instant.parse(time), session, null, week, weekResetsAt)

    @Test
    fun `a day's usage is the sum of the weekly increases, attributed to the local day of the later sample`() {
        val days = DailyUsageAggregator.build(
            listOf(
                record("2026-11-23T10:00:00Z", 10.0, session = 30.0),
                record("2026-11-23T12:00:00Z", 14.5, session = 55.0),
                // 23:30 UTC is already the 24th in Berlin.
                record("2026-11-23T23:30:00Z", 20.0, session = 12.0),
                // A value that went down within the window (API noise) counts as nothing.
                record("2026-11-24T08:00:00Z", 19.0),
            ),
            berlin,
        )

        assertEquals(listOf(LocalDate.of(2026, 11, 23), LocalDate.of(2026, 11, 24)), days.map { it.date })
        assertEquals(4.5, days[0].weekPoints, "the first sample has nothing to compare with")
        assertEquals(55.0, days[0].sessionPeak)
        assertEquals(2, days[0].activeHours, "11:00 and 13:00 local")
        assertEquals(5.5, days[1].weekPoints)
        assertEquals(12.0, days[1].sessionPeak)
        assertEquals((1 shl 0) or (1 shl 9), days[1].hourMask)
        assertEquals(weekReset, days[1].weekResetsAt)
    }

    @Test
    fun `a weekly reset between two samples counts the new window's whole value, never a negative`() {
        val next = weekReset.plusSeconds(7 * 86_400)
        val before = record("2026-11-27T08:00:00Z", 80.0)
        val after = record("2026-11-27T11:00:00Z", 3.0, weekResetsAt = next)

        assertEquals(3.0, DailyUsageAggregator.weekIncrease(before, after))
    }

    @Test
    fun `a reset time that moves while the old window is still running is the same window`() {
        val before = record("2026-11-24T08:00:00Z", 40.0)
        val after = record("2026-11-24T09:00:00Z", 42.0, weekResetsAt = weekReset.plusSeconds(3600))

        assertEquals(2.0, DailyUsageAggregator.weekIncrease(before, after), 1e-9)
    }
}

class DailyHistoryFileTest {
    private val berlin = ZoneId.of("Europe/Berlin")
    private val dir = java.nio.file.Files.createTempDirectory("aiquota-daily").toFile()
    private var now = Instant.parse("2026-11-24T10:00:00Z")
    private val weekReset = Instant.parse("2026-11-27T09:00:00Z")

    private fun history() = JsonlUsageHistory(dir, "account-1", clock = { now }, zone = berlin)
    private fun dailyFile() = java.io.File(dir, JsonlUsageHistory.fileNameFor("account-1") + ".daily.jsonl")

    private fun record(time: String, week: Double) = UsageHistoryRecord(Instant.parse(time), 10.0, null, week, weekReset)

    @Test
    fun `every append updates its day, and the day values survive a restart`() {
        history().apply {
            append(record("2026-11-24T08:00:00Z", 10.0))
            append(record("2026-11-24T09:00:00Z", 13.0))
        }

        val reopened = history()

        assertEquals(listOf(DailyUsageRecord(LocalDate.of(2026, 11, 24), 3.0, 10.0, (1 shl 9) or (1 shl 10), weekReset)), reopened.daily)
        assertEquals(1, dailyFile().readLines().count { it.isNotBlank() }, "compacted to one line per day when loaded")
    }

    @Test
    fun `reads the Windows app's daily file - the last line of a date wins - and skips broken lines`() {
        dailyFile().writeText(
            """
            {"date":"2026-11-20","weekPoints":4.5,"sessionPeak":30,"hours":3,"weekResetsAt":"2026-11-27T09:00:00+00:00"}
            {"date":"2026-11-21","weekPoints":1,"sessionPeak":5,"hours":1}
            {"date":"2026-11-21","weekPoints":2,"sessionPeak":8,"hours":3}
            {"date":"2026-11-2
            """.trimIndent() + "\n",
        )

        val days = history().daily

        assertEquals(
            listOf(
                DailyUsageRecord(LocalDate.of(2026, 11, 20), 4.5, 30.0, 3, weekReset),
                DailyUsageRecord(LocalDate.of(2026, 11, 21), 2.0, 8.0, 3, null),
            ),
            days,
        )
    }

    @Test
    fun `without a daily file the days are backfilled from the raw history`() {
        history().apply {
            append(record("2026-11-23T08:00:00Z", 10.0))
            append(record("2026-11-24T08:00:00Z", 16.0))
        }
        dailyFile().delete()

        val days = history().daily

        assertEquals(listOf(0.0, 6.0), days.map { it.weekPoints })
        assertTrue(dailyFile().exists())
    }

    @Test
    fun `days older than 400 days are dropped`() {
        dailyFile().writeText(
            """
            {"date":"2025-10-01","weekPoints":4,"sessionPeak":30,"hours":3}
            {"date":"2025-11-01","weekPoints":5,"sessionPeak":30,"hours":3}
            """.trimIndent() + "\n",
        )

        assertEquals(listOf(LocalDate.of(2025, 11, 1)), history().daily.map { it.date })
    }
}
