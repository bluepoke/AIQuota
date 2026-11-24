package eu.schicht8.aiquota.core

import java.time.Duration
import java.time.Instant
import kotlin.test.Test
import kotlin.test.assertEquals

class BurnRateForecasterTest {
    private val start = Instant.parse("2026-11-24T10:00:00Z")
    private fun at(minutes: Long): Instant = start.plus(Duration.ofMinutes(minutes))

    @Test
    fun `forecasts the limit when the current rate reaches 100 percent before the reset`() {
        // 1 point per minute: 30% at minute 20 reaches 100% at minute 90, long before the reset at 200.
        val reset = at(200)
        val points = listOf(UsagePoint(at(0), 10.0, reset), UsagePoint(at(10), 20.0, reset), UsagePoint(at(20), 30.0, reset))

        val forecast = BurnRateForecaster.forecast(points, reset, BurnRateForecaster.SESSION, now = at(25))

        assertEquals(UsageForecast(ForecastKind.Limit, Duration.ofMinutes(65), projectedPercent = 100.0), forecast)
    }

    @Test
    fun `projects the level at the reset when that comes first`() {
        // 0.1 points per minute: 12% at minute 20 grows to 18% by the reset at minute 80.
        val reset = at(80)
        val points = listOf(UsagePoint(at(0), 10.0, reset), UsagePoint(at(10), 11.0, reset), UsagePoint(at(20), 12.0, reset))

        val forecast = BurnRateForecaster.forecast(points, reset, BurnRateForecaster.SESSION, now = at(20))

        assertEquals(ForecastKind.ResetFirst, forecast.kind)
        assertEquals(18.0, forecast.projectedPercent!!, 1e-9)
    }

    @Test
    fun `has no forecast without enough recent, rising data of the current window`() {
        val reset = at(200)
        fun forecast(vararg points: UsagePoint, now: Instant = at(20), resetsAt: Instant? = reset) =
            BurnRateForecaster.forecast(points.toList(), resetsAt, BurnRateForecaster.SESSION, now).kind

        val rising = arrayOf(UsagePoint(at(0), 10.0, reset), UsagePoint(at(10), 20.0, reset), UsagePoint(at(20), 30.0, reset))
        assertEquals(ForecastKind.Limit, forecast(*rising))
        assertEquals(ForecastKind.None, forecast(*rising, resetsAt = null), "session not started")
        assertEquals(ForecastKind.None, forecast(*rising, now = at(51)), "newest sample beyond the lookback")
        assertEquals(ForecastKind.None, forecast(*rising.take(2).toTypedArray()), "too few samples")
        assertEquals(ForecastKind.None, forecast(UsagePoint(at(14), 10.0, reset), UsagePoint(at(17), 20.0, reset), UsagePoint(at(20), 30.0, reset)), "too short a span")
        assertEquals(ForecastKind.None, forecast(UsagePoint(at(0), 30.0, reset), UsagePoint(at(10), 30.0, reset), UsagePoint(at(20), 30.0, reset)), "idle")
        assertEquals(ForecastKind.None, forecast(UsagePoint(at(0), 90.0, reset), UsagePoint(at(10), 95.0, reset), UsagePoint(at(20), 100.0, reset)), "limit reached")
        assertEquals(ForecastKind.None, forecast(*rising.map { it.copy(resetsAt = at(500)) }.toTypedArray()), "another window")
    }

    @Test
    fun `uses only the unbroken stretch after a gap in the session`() {
        // The samples before the 16-minute gap would make it rise; after it, usage is flat.
        val reset = at(200)
        val points = listOf(
            UsagePoint(at(0), 0.0, reset), UsagePoint(at(2), 20.0, reset),
            UsagePoint(at(18), 40.0, reset), UsagePoint(at(24), 40.0, reset), UsagePoint(at(30), 40.0, reset),
        )

        assertEquals(ForecastKind.None, BurnRateForecaster.forecast(points, reset, BurnRateForecaster.SESSION, at(30)).kind)
    }

    @Test
    fun `treats reset times a few minutes apart as the same window`() {
        val reset = at(200)
        val points = listOf(
            UsagePoint(at(0), 10.0, reset.minusSeconds(240)),
            UsagePoint(at(10), 20.0, reset.plusSeconds(300)),
            UsagePoint(at(20), 30.0, reset),
        )

        assertEquals(ForecastKind.Limit, BurnRateForecaster.forecast(points, reset, BurnRateForecaster.SESSION, at(20)).kind)
    }
}
