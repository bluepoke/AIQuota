package eu.schicht8.aiquota.core

import java.math.BigDecimal
import java.math.RoundingMode
import java.time.Duration
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import kotlin.math.abs
import kotlin.math.max

/**
 * One local calendar day of usage, as kept for the yearly heatmap - the Windows app's
 * AIQuota/History/DailyUsageRecord.cs, in the same file format.
 * [weekPoints]: share of the weekly quota used that day, in percentage points (see
 * [DailyUsageAggregator.weekIncrease]). [sessionPeak]: highest session utilization seen that
 * day. [hourMask]: bit h is set if there was a sample in local hour h. [weekResetsAt]: the
 * weekly window's reset time at the day's last sample.
 */
data class DailyUsageRecord(
    val date: LocalDate,
    val weekPoints: Double,
    val sessionPeak: Double,
    val hourMask: Int,
    val weekResetsAt: Instant?,
) {
    /** Number of local hours with at least one sample. */
    val activeHours: Int get() = Integer.bitCount(hourMask)
}

/**
 * Turns raw usage samples into per-day values - a port of
 * AIQuota/History/DailyUsageAggregator.cs. The API only reports the utilization of rolling
 * windows, so a day's usage is derived: the sum of all increases of the weekly utilization
 * between consecutive samples, attributed to the local day of the later sample. A weekly
 * reset in between is never negative usage (the new window started at 0), and a reset time
 * that moves while the old window is still running counts as the same window.
 */
object DailyUsageAggregator {
    /** Two reset times closer than this belong to the same window - as in the forecast and
     * the 7-day chart. */
    val SAME_WINDOW_TOLERANCE: Duration = Duration.ofMinutes(10)

    fun dayOf(time: Instant, zone: ZoneId): LocalDate = time.atZone(zone).toLocalDate()

    /** Percentage points of the weekly quota used between [previous] and [current]; 0 without
     * a previous sample. */
    fun weekIncrease(previous: UsageHistoryRecord?, current: UsageHistoryRecord): Double {
        if (previous == null || current.time <= previous.time) return 0.0
        var increase = current.week - previous.week
        if (!sameWindow(previous.weekResetsAt, current.weekResetsAt)) {
            // The previous window has ended (or there was none): a new one started at 0 since
            // then. Anything else is a reset time that moved within a running window.
            val previousReset = previous.weekResetsAt
            val previousEnded = previousReset == null || previousReset <= current.time.plus(SAME_WINDOW_TOLERANCE)
            if (previousEnded) increase = current.week
        }
        return increase.coerceIn(0.0, 100.0)
    }

    /** [day] (that same day's record so far, or null) with [current] added. */
    fun add(day: DailyUsageRecord?, previous: UsageHistoryRecord?, current: UsageHistoryRecord, zone: ZoneId): DailyUsageRecord {
        val date = dayOf(current.time, zone)
        val base = if (day == null || day.date != date) DailyUsageRecord(date, 0.0, 0.0, 0, null) else day
        val hour = current.time.atZone(zone).hour
        return base.copy(
            weekPoints = round3(base.weekPoints + weekIncrease(previous, current)),
            sessionPeak = max(base.sessionPeak, round3(current.session)),
            hourMask = base.hourMask or (1 shl hour),
            weekResetsAt = current.weekResetsAt ?: base.weekResetsAt,
        )
    }

    /** All days of [records] (any order), oldest first - to backfill from a raw history. */
    fun build(records: Iterable<UsageHistoryRecord>, zone: ZoneId): List<DailyUsageRecord> {
        val days = mutableListOf<DailyUsageRecord>()
        var previous: UsageHistoryRecord? = null
        for (record in records.sortedBy { it.time }) {
            val last = days.lastOrNull()
            val updated = add(last, previous, record, zone)
            if (last != null && last.date == updated.date) days[days.lastIndex] = updated else days += updated
            previous = record
        }
        return days
    }

    /** .NET's Math.Round(value, 3): halves to even. */
    private fun round3(value: Double): Double = BigDecimal.valueOf(value).setScale(3, RoundingMode.HALF_EVEN).toDouble()

    /** Whether two weekly reset times belong to the same window (both null counts as same). */
    fun sameWindow(a: Instant?, b: Instant?): Boolean =
        if (a != null && b != null) abs(Duration.between(a, b).toMillis()) <= SAME_WINDOW_TOLERANCE.toMillis() else a == null && b == null
}
