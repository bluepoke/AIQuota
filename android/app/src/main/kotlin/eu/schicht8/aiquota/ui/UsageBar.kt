package eu.schicht8.aiquota.ui

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.RoundRect
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.drawscope.clipPath
import androidx.compose.ui.unit.dp
import eu.schicht8.aiquota.core.UsagePalette
import eu.schicht8.aiquota.core.UsageSection
import kotlin.math.ceil
import kotlin.math.max
import kotlin.math.min
import kotlin.math.roundToInt

private val BarHeight = 8.dp
/** Room above and below the bar for the pace marker, which sticks out by 3dp plus its outline. */
private val MarkerMargin = 4.5.dp

/**
 * A usage bar, drawn as the Windows flyout draws it (AIQuota/UsageBarRenderer.cs DrawBar):
 * track, the forecast's projection as a dimmed, hatched extension, the fill in the level's
 * colour (with a notch above 80% in the greyscale palette), scale ticks, and the pace marker.
 * An outline-only track while no session has started.
 */
@Composable
fun UsageBar(section: UsageSection, modifier: Modifier = Modifier) {
    val colors = LocalAiQuotaColors.current
    Canvas(modifier.fillMaxWidth().height(BarHeight + MarkerMargin * 2)) {
        drawBar(section, colors)
    }
}

private fun DrawScope.drawBar(section: UsageSection, colors: AiQuotaColors) {
    val card = colors.card
    val usage = colors.usage
    val height = BarHeight.toPx()
    val top = MarkerMargin.toPx()
    val width = size.width
    val radius = height / 2f
    val clamped = section.percent.coerceIn(0, 100)
    val trackColor = Color(usage.track ?: card.track)
    val outlineColor = Color(usage.notStarted ?: card.track)
    val background = Color(card.background)

    // Track
    if (section.outlineOnly) {
        val inset = 0.75.dp.toPx()
        drawRoundRect(
            outlineColor, Offset(inset, top + inset), Size(width - 2 * inset, height - 2 * inset),
            CornerRadius(radius - inset), style = Stroke(1.5.dp.toPx()),
        )
    } else {
        drawRoundRect(trackColor, Offset(0f, top), Size(width, height), CornerRadius(radius))
        usage.trackOutline?.let { outline ->
            val inset = 0.5.dp.toPx()
            drawRoundRect(
                Color(outline), Offset(inset, top + inset), Size(width - 2 * inset, height - 2 * inset),
                CornerRadius(radius - inset), style = Stroke(1.dp.toPx()),
            )
        }
    }

    // Projection: from the start to the projected level, dimmed and striped.
    val projected = section.projectedPercent
    if (projected != null && projected > clamped) {
        val level = projected.coerceIn(0.0, 100.0)
        val projectionWidth = min(max(width * (level / 100).toFloat(), height), width)
        val color = Color(usage.forPercent(ceil(level).toInt()))
        val path = roundRectPath(0f, top, projectionWidth, height, radius)
        clipPath(path) {
            drawRect(color.copy(alpha = 70 / 255f), Offset(0f, top), Size(projectionWidth, height))
            val spacing = 9.dp.toPx()
            var x = -height
            while (x < projectionWidth) {
                drawLine(color.copy(alpha = 170 / 255f), Offset(x, top + height), Offset(x + height, top), 1.5.dp.toPx())
                x += spacing
            }
        }
    }

    // Fill
    if (clamped > 0) {
        val fillWidth = min(max(width * clamped / 100f, height), width)
        drawRoundRect(Color(usage.forPercent(clamped)), Offset(0f, top), Size(fillWidth, height), CornerRadius(radius))
        if (usage.marksHighLevel && UsagePalette.isHigh(clamped)) {
            clipPath(roundRectPath(0f, top, fillWidth, height, radius)) {
                val notch = 2.dp.toPx()
                drawRect(Color(usage.highNotch), Offset(fillWidth - 4.dp.toPx() - notch, top), Size(notch, height))
            }
        }
    }

    // Scale ticks: thin gaps in the background colour (on an outline there's nothing to cut
    // a gap into, so they take the outline's colour).
    if (section.sections >= 2) {
        val tickColor = if (section.outlineOnly) outlineColor else background.copy(alpha = 190 / 255f)
        val tickWidth = max(1f, 1.dp.toPx().roundToInt().toFloat())
        for (i in 1 until section.sections) {
            val x = (width * i / section.sections - tickWidth / 2f).roundToInt().toFloat()
            drawRect(tickColor, Offset(x, top), Size(tickWidth, height))
        }
    }

    // Pace marker: where even usage over the window would be by now.
    section.paceFraction?.let { pace ->
        val x = width * pace.coerceIn(0.0, 1.0).toFloat()
        val core = 2.dp.toPx()
        val outline = 1.5.dp.toPx()
        val markerTop = top - 3.dp.toPx()
        val markerHeight = height + 6.dp.toPx()
        drawRect(background, Offset(x - core / 2 - outline, markerTop - outline), Size(core + 2 * outline, markerHeight + 2 * outline))
        drawRect(Color(card.text), Offset(x - core / 2, markerTop), Size(core, markerHeight))
    }
}

private fun roundRectPath(left: Float, top: Float, width: Float, height: Float, radius: Float) = Path().apply {
    addRoundRect(RoundRect(left, top, left + width, top + height, CornerRadius(radius)))
}
