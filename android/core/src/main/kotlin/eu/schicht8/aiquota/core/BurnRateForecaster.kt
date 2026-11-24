package eu.schicht8.aiquota.core

import java.time.Duration
import java.time.Instant
import kotlin.math.abs
import kotlin.math.min
import kotlin.math.roundToLong

enum class ForecastKind {
    /** No forecast: too little recent data, usage not rising, limit already reached, or the
     * session hasn't started. */
    None,

    /** Usage is rising, but the window resets before it reaches 100%. */
    ResetFirst,

    /** At the current rate, usage reaches 100% before the reset, in [UsageForecast.timeToLimit]. */
    Limit,
}

/** [projectedPercent]: usage the window would reach by its reset at the current rate,
 * capped at 100 - the hatched extension of a bar. Null for [ForecastKind.None]. */
data class UsageForecast(
    val kind: ForecastKind,
    val timeToLimit: Duration = Duration.ZERO,
    val projectedPercent: Double? = null,
) {
    /** The time to the limit if it's reached before the reset, else null. */
    val limitIn: Duration? get() = if (kind == ForecastKind.Limit) timeToLimit else null

    companion object {
        val NONE = UsageForecast(ForecastKind.None)
    }
}

/** One usage value of one window (session or week) at a point in time. */
data class UsagePoint(val time: Instant, val percent: Double, val resetsAt: Instant?)

/**
 * Projects when a usage window will hit 100% if usage keeps growing at its recent rate: the
 * slope of a least-squares line through the window's recent samples. A port of
 * AIQuota/History/BurnRateForecaster.cs - same settings, same rules.
 */
object BurnRateForecaster {
    /** [lookback]: how far back samples are used. [maxGap]: walking back from the newest
     * sample, stop at the first gap longer than this. [minSpan]: minimum time between the
     * oldest and newest sample used. */
    data class Settings(val lookback: Duration, val maxGap: Duration, val minSpan: Duration, val minSamples: Int = 3)

    /** The last 30 minutes, split at gaps over 15 minutes. */
    val SESSION = Settings(Duration.ofMinutes(30), Duration.ofMinutes(15), Duration.ofMinutes(10))

    /** The last 24 hours, not split at gaps; at least 6 hours of data. */
    val WEEK = Settings(Duration.ofHours(24), Duration.ofHours(24), Duration.ofHours(6))

    /** Reset times closer than this belong to the same window (the API's jitter). */
    private val SAME_WINDOW_TOLERANCE = Duration.ofMinutes(10)

    /** [points]: samples of one window type, any order, possibly of several windows - only
     * those of the window resetting at [currentResetsAt] count; null gives no forecast. */
    fun forecast(points: Iterable<UsagePoint>, currentResetsAt: Instant?, settings: Settings, now: Instant): UsageForecast {
        val resetsAt = currentResetsAt ?: return UsageForecast.NONE

        val window = points
            .filter { p -> p.resetsAt != null && abs(Duration.between(resetsAt, p.resetsAt).toMillis()) <= SAME_WINDOW_TOLERANCE.toMillis() }
            .sortedBy { it.time }
        val latest = window.lastOrNull() ?: return UsageForecast.NONE
        if (latest.percent >= 100 || Duration.between(latest.time, now) > settings.lookback) return UsageForecast.NONE

        val used = mutableListOf(latest)
        for (i in window.size - 2 downTo 0) {
            val point = window[i]
            if (Duration.between(point.time, latest.time) > settings.lookback ||
                Duration.between(point.time, used.last().time) > settings.maxGap
            ) break
            used += point
        }
        if (used.size < settings.minSamples || Duration.between(used.last().time, latest.time) < settings.minSpan) {
            return UsageForecast.NONE
        }

        val ratePerMinute = slope(used, latest.time)
        if (ratePerMinute <= 0) return UsageForecast.NONE

        // Compared in minutes before building a Duration: a near-flat fit can yield a tiny
        // positive slope whose minutes-to-limit overflow.
        val minutesToLimit = (100 - latest.percent) / ratePerMinute
        val minutesToReset = minutes(latest.time, resetsAt)
        if (minutesToLimit >= minutesToReset) {
            val projected = min(latest.percent + ratePerMinute * minutesToReset, 100.0)
            return UsageForecast(ForecastKind.ResetFirst, projectedPercent = projected)
        }

        val timeToLimit = Duration.between(now, latest.time.plusMillis((minutesToLimit * 60_000).roundToLong()))
        val atLeastAMinute = if (timeToLimit < Duration.ofMinutes(1)) Duration.ofMinutes(1) else timeToLimit
        return UsageForecast(ForecastKind.Limit, atLeastAMinute, projectedPercent = 100.0)
    }

    /** Least-squares slope of percent over time, in points per minute, relative to [origin]. */
    private fun slope(points: List<UsagePoint>, origin: Instant): Double {
        val n = points.size
        var sumX = 0.0
        var sumY = 0.0
        var sumXY = 0.0
        var sumXX = 0.0
        for (p in points) {
            val x = minutes(origin, p.time)
            sumX += x
            sumY += p.percent
            sumXY += x * p.percent
            sumXX += x * x
        }
        val denominator = n * sumXX - sumX * sumX
        return if (denominator == 0.0) 0.0 else (n * sumXY - sumX * sumY) / denominator
    }

    private fun minutes(from: Instant, to: Instant): Double = Duration.between(from, to).toMillis() / 60_000.0
}
