package eu.schicht8.aiquota.ui

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.gestures.detectHorizontalDragGestures
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.PathEffect
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.drawscope.clipRect
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.drawText
import androidx.compose.ui.text.rememberTextMeasurer
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.Density
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import eu.schicht8.aiquota.core.DailyUsageAggregator
import eu.schicht8.aiquota.core.Strings
import eu.schicht8.aiquota.core.UsageHistoryRecord
import eu.schicht8.aiquota.core.UsageSnapshot
import eu.schicht8.aiquota.core.UsageWindows
import java.time.Duration
import java.time.Instant
import java.time.ZoneId
import kotlin.math.abs
import kotlin.math.roundToInt

/** Samples further apart than this are drawn as a gap (phone off, app not fetching). */
private val CHART_GAP: Duration = Duration.ofMinutes(20)
private val CHART_RANGE: Duration = Duration.ofDays(7)

/** The chart is drawn once there are this many samples in range, spanning [MIN_CHART_DATA]. */
private const val MIN_CHART_POINTS = 2
private val MIN_CHART_DATA: Duration = Duration.ofHours(1)

private val LabelInset = 36.dp
private val PlotEndInset = 4.dp
private val DayLabelHeight = 18.dp

/**
 * The past 7 days of weekly usage against the even pace - the Windows flyout's chart
 * (AIQuota/UsageFlyout.cs DrawChart): the usage line split at gaps and window changes, and
 * behind it the dashed even-pace line of each weekly window, which makes a sawtooth across a
 * reset. Touching the chart shows the time and value of the nearest sample.
 */
@Composable
fun WeekChart(history: List<UsageHistoryRecord>, snapshot: UsageSnapshot, now: Instant, strings: Strings, modifier: Modifier = Modifier) {
    val colors = LocalAiQuotaColors.current
    val card = colors.card
    val rangeStart = now.minus(CHART_RANGE)
    val points = history.filter { it.time >= rangeStart && it.time <= now }
    val enoughData = points.size >= MIN_CHART_POINTS && Duration.between(points.first().time, points.last().time) >= MIN_CHART_DATA
    var selected by remember(points) { mutableStateOf<UsageHistoryRecord?>(null) }
    val measurer = rememberTextMeasurer()
    val labelStyle = TextStyle(fontSize = 11.sp, color = Color(card.secondary))

    Column(modifier, verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Text(
            selected?.let { strings.chartPoint(it.time, it.week) } ?: " ",
            Modifier.semantics { liveRegion = LiveRegionMode.Polite },
            style = MaterialTheme.typography.bodySmall,
        )
        Box(Modifier.fillMaxWidth().height(160.dp)) {
            /** The sample nearest to [x] in a chart [width] pixels wide. */
            fun Density.nearest(x: Float, width: Float): UsageHistoryRecord? {
                if (!enoughData) return null
                val (plotLeft, plotWidth) = plotSpan(width)
                val fraction = ((x - plotLeft) / plotWidth).coerceIn(0f, 1f)
                val time = rangeStart.plusMillis((CHART_RANGE.toMillis() * fraction).toLong())
                return points.minByOrNull { abs(Duration.between(it.time, time).toMillis()) }
            }
            Canvas(
                Modifier.fillMaxWidth().height(160.dp)
                    .semantics { contentDescription = strings.historyTitle }
                    .pointerInput(points) {
                        detectTapGestures { selected = nearest(it.x, size.width.toFloat()) }
                    }
                    .pointerInput(points) {
                        detectHorizontalDragGestures { change, _ -> selected = nearest(change.position.x, size.width.toFloat()) }
                    },
            ) {
                val (plotLeft, plotWidth) = plotSpan(size.width)
                val plotTop = 6.dp.toPx()
                val plotBottom = size.height - DayLabelHeight.toPx()
                val plotHeight = plotBottom - plotTop
                fun x(t: Instant) = plotLeft + (Duration.between(rangeStart, t).toMillis().toFloat() / CHART_RANGE.toMillis()) * plotWidth
                fun y(percent: Double) = plotBottom - (percent.coerceIn(0.0, 100.0) / 100.0).toFloat() * plotHeight

                // Grid: 0 / 50 / 100% with labels, day boundaries with weekday labels.
                val gridColor = Color(card.grid)
                for (level in listOf(0, 50, 100)) {
                    val yy = y(level.toDouble())
                    drawLine(gridColor, Offset(plotLeft, yy), Offset(plotLeft + plotWidth, yy), 1.dp.toPx())
                    val label = measurer.measure("$level%", labelStyle)
                    drawText(label, topLeft = Offset(plotLeft - 5.dp.toPx() - label.size.width, yy - label.size.height / 2f))
                }
                val zone = ZoneId.systemDefault()
                var day = now.atZone(zone).toLocalDate().atStartOfDay(zone).toInstant()
                while (day > rangeStart.minus(Duration.ofDays(1))) {
                    if (day > rangeStart) drawLine(gridColor, Offset(x(day), plotTop), Offset(x(day), plotBottom), 1.dp.toPx())
                    val labelStart = maxOf(day, rangeStart)
                    val labelEnd = minOf(day.plus(Duration.ofDays(1)), now)
                    val width = x(labelEnd) - x(labelStart)
                    if (width >= 22.dp.toPx()) {
                        val label = measurer.measure(strings.weekdayShort(day.atZone(zone)), labelStyle.copy(textAlign = TextAlign.Center))
                        drawText(label, topLeft = Offset(x(labelStart) + (width - label.size.width) / 2f, plotBottom + 3.dp.toPx()))
                    }
                    day = day.minus(Duration.ofDays(1))
                }
                if (!enoughData) return@Canvas

                clipRect(plotLeft - 5.dp.toPx(), plotTop - 5.dp.toPx(), plotLeft + plotWidth + 5.dp.toPx(), plotBottom + 5.dp.toPx()) {
                    drawPace(snapshot, rangeStart, now, ::x, ::y, colors)
                    drawUsage(points, ::x, ::y, colors)

                    val current = snapshot.weeklyUtilization ?: snapshot.weeklyPercent.toDouble()
                    drawCircle(Color(card.background), 5.dp.toPx(), Offset(x(now), y(current)))
                    drawCircle(Color(colors.usage.forPercent(snapshot.weeklyPercent)), 3.5.dp.toPx(), Offset(x(now), y(current)))

                    selected?.let { record ->
                        val point = Offset(x(record.time), y(record.week))
                        drawLine(Color(card.secondary), Offset(point.x, plotTop), Offset(point.x, plotBottom), 1.dp.toPx())
                        drawCircle(Color(card.text), 4.dp.toPx(), point)
                    }
                }
            }
            if (!enoughData) {
                // The span is measured from the oldest sample in range, and new ones keep
                // coming - so it's reached at oldest + MIN_CHART_DATA.
                val remaining = points.firstOrNull()?.let { Duration.between(now, it.time.plus(MIN_CHART_DATA)) } ?: MIN_CHART_DATA
                Text(
                    strings.collectingData(MIN_CHART_POINTS, MIN_CHART_DATA, remaining),
                    Modifier.align(Alignment.Center).padding(start = LabelInset + 8.dp, end = 8.dp),
                    style = MaterialTheme.typography.bodySmall,
                    color = Color(card.secondary),
                    textAlign = TextAlign.Center,
                )
            }
        }
        Legend(strings, Color(colors.usage.forPercent(snapshot.weeklyPercent)), Color(card.pace), Color(card.secondary))
    }
}

/** Where the plot starts horizontally (after the % labels) and how wide it is, in a chart
 * [width] pixels wide. */
private fun Density.plotSpan(width: Float): Pair<Float, Float> {
    val left = LabelInset.toPx()
    return left to width - left - PlotEndInset.toPx()
}

/** The even-pace sawtooth and the divider at the reset, from the current window's reset time;
 * the current pace as a ring. */
private fun DrawScope.drawPace(
    snapshot: UsageSnapshot,
    rangeStart: Instant,
    now: Instant,
    x: (Instant) -> Float,
    y: (Double) -> Float,
    colors: AiQuotaColors,
) {
    val reset = snapshot.weeklyResetsAt ?: return
    val pace = Color(colors.card.pace)
    val dash = PathEffect.dashPathEffect(floatArrayOf(4.dp.toPx(), 3.dp.toPx()))
    val windowStart = reset.minus(UsageWindows.WEEK)
    fun paceAt(resetsAt: Instant, t: Instant) = UsageWindows.paceFraction(resetsAt, t, UsageWindows.WEEK) * 100
    if (windowStart > rangeStart) {
        drawLine(pace, Offset(x(rangeStart), y(paceAt(windowStart, rangeStart))), Offset(x(windowStart), y(100.0)), 1.5.dp.toPx(), pathEffect = dash)
        drawLine(
            Color(colors.card.secondary), Offset(x(windowStart), y(100.0)), Offset(x(windowStart), y(0.0)), 1.dp.toPx(),
            pathEffect = PathEffect.dashPathEffect(floatArrayOf(1.dp.toPx(), 2.dp.toPx())),
        )
    }
    val from = maxOf(windowStart, rangeStart)
    val paceNow = paceAt(reset, now)
    drawLine(pace, Offset(x(from), y(paceAt(reset, from))), Offset(x(now), y(paceNow)), 1.5.dp.toPx(), pathEffect = dash)
    drawCircle(pace, 3.5.dp.toPx(), Offset(x(now), y(paceNow)), style = Stroke(1.5.dp.toPx()))
}

/** The usage line, split at gaps and at window changes (where it drops back to ~0); isolated
 * samples as small dots, so they don't vanish. */
private fun DrawScope.drawUsage(points: List<UsageHistoryRecord>, x: (Instant) -> Float, y: (Double) -> Float, colors: AiQuotaColors) {
    fun connected(a: UsageHistoryRecord, b: UsageHistoryRecord) =
        Duration.between(a.time, b.time) <= CHART_GAP && DailyUsageAggregator.sameWindow(a.weekResetsAt, b.weekResetsAt)
    fun color(record: UsageHistoryRecord) = Color(colors.usage.forPercent(record.week.roundToInt()))

    for (i in 1 until points.size) {
        val a = points[i - 1]
        val b = points[i]
        if (!connected(a, b)) continue
        drawLine(color(b), Offset(x(a.time), y(a.week)), Offset(x(b.time), y(b.week)), 2.dp.toPx(), cap = StrokeCap.Round)
    }
    for (i in points.indices) {
        val before = i > 0 && connected(points[i - 1], points[i])
        val after = i < points.lastIndex && connected(points[i], points[i + 1])
        if (!before && !after) drawCircle(color(points[i]), 1.5.dp.toPx(), Offset(x(points[i].time), y(points[i].week)))
    }
}

@Composable
private fun Legend(strings: Strings, usage: Color, pace: Color, text: Color) {
    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End, verticalAlignment = Alignment.CenterVertically) {
        Canvas(Modifier.size(18.dp, 10.dp)) {
            drawLine(usage, Offset(0f, size.height / 2), Offset(size.width, size.height / 2), 2.dp.toPx(), cap = StrokeCap.Round)
        }
        Text(" ${strings.legendUsage}", style = MaterialTheme.typography.bodySmall, color = text)
        Canvas(Modifier.padding(start = 12.dp).size(18.dp, 10.dp)) {
            drawLine(
                pace, Offset(0f, size.height / 2), Offset(size.width, size.height / 2), 1.5.dp.toPx(),
                pathEffect = PathEffect.dashPathEffect(floatArrayOf(4.dp.toPx(), 3.dp.toPx())),
            )
        }
        Text(" ${strings.legendPace}", style = MaterialTheme.typography.bodySmall, color = text)
    }
}
