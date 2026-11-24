package eu.schicht8.aiquota.ui

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import eu.schicht8.aiquota.core.DailyUsageRecord
import eu.schicht8.aiquota.core.Heatmap
import eu.schicht8.aiquota.core.HeatmapModel
import eu.schicht8.aiquota.core.Strings
import java.time.DayOfWeek
import java.time.LocalDate
import java.time.ZoneId
import kotlin.math.floor

private val Cell = 14.dp
private val Gap = 3.dp
private val Pitch = Cell + Gap

/**
 * The yearly heatmap (#17) of one account: the days recorded on this device, one square per
 * day in week columns. On a phone it scrolls horizontally and starts at the current week;
 * tapping a day shows its values, as the hover text does on Windows.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun HeatmapScreen(app: AppContext, accountKey: String) {
    val viewModel = app.viewModel
    val strings = app.strings
    val accounts by viewModel.accounts.collectAsStateWithLifecycle()
    val state = accounts.firstOrNull { it.account.key == accountKey }
    if (state == null) {
        LaunchedEffect(Unit) { viewModel.back() }
        return
    }
    val days by produceState<List<DailyUsageRecord>?>(null, accountKey, state.snapshot?.fetchedAt) {
        value = viewModel.dailyUsage(accountKey)
    }
    val today = app.now.atZone(ZoneId.systemDefault()).toLocalDate()
    val model = Heatmap.build(days.orEmpty(), today, strings)
    var selectedDay by rememberSaveable { mutableStateOf<String?>(null) }
    val selected = selectedDay?.let(LocalDate::parse)?.takeIf { it >= model.firstMonday && it <= today }
    val card = LocalAiQuotaColors.current.card

    Scaffold(
        topBar = {
            TopAppBar(
                title = {
                    Column {
                        Text(strings.heatmapTitle, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        Text(
                            strings.accountName(state.account),
                            style = MaterialTheme.typography.bodySmall, color = Color(card.secondary),
                            maxLines = 1, overflow = TextOverflow.Ellipsis,
                        )
                    }
                },
                navigationIcon = { BackButton(strings) { viewModel.back() } },
            )
        },
    ) { padding ->
        Column(
            Modifier.fillMaxSize().padding(padding).verticalScroll(rememberScrollState()).padding(16.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            UsageCard(Modifier.widthIn(max = 1000.dp)) {
                Column(Modifier.padding(top = 8.dp, end = 12.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                    Text(if (days == null) " " else model.summary, style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.SemiBold)
                    Grid(model, strings, selected, onSelect = { selectedDay = it.toString() })
                    Text(
                        selected?.let(model::dayText) ?: strings.heatmapTapHint,
                        Modifier.semantics { liveRegion = LiveRegionMode.Polite },
                        style = MaterialTheme.typography.bodySmall,
                        color = Color(if (selected != null) card.text else card.secondary),
                    )
                    Legend(app)
                    Text(strings.heatmapExplanation, style = MaterialTheme.typography.bodySmall, color = Color(card.secondary))
                }
            }
        }
    }
}

/** Weekday labels on the left (fixed), and the month row and the squares, scrolled to the
 * current week at first. */
@Composable
private fun Grid(model: HeatmapModel, strings: Strings, selected: LocalDate?, onSelect: (LocalDate) -> Unit) {
    val colors = LocalAiQuotaColors.current
    val scroll = rememberScrollState()
    LaunchedEffect(scroll.maxValue) { scroll.scrollTo(scroll.maxValue) }
    val gridWidth = Pitch * Heatmap.WEEKS - Gap
    val gridHeight = Pitch * 7 - Gap
    // Sized by the label font, so the labels don't clip with large system fonts.
    val labelStyle = MaterialTheme.typography.labelSmall
    val monthRowHeight = with(LocalDensity.current) { labelStyle.lineHeight.toDp() } + 4.dp

    Row {
        Box(Modifier.padding(top = monthRowHeight, end = 6.dp).widthIn(min = 24.dp).height(gridHeight)) {
            for ((row, day) in listOf(0 to DayOfWeek.MONDAY, 2 to DayOfWeek.WEDNESDAY, 4 to DayOfWeek.FRIDAY)) {
                Text(
                    strings.weekdayShort(day),
                    Modifier.offset(y = Pitch * row + (Cell - with(LocalDensity.current) { labelStyle.lineHeight.toDp() }) / 2),
                    style = labelStyle,
                    color = Color(colors.card.secondary),
                )
            }
        }
        Column(Modifier.horizontalScroll(scroll)) {
            Box(Modifier.width(gridWidth).height(monthRowHeight)) {
                for (label in model.monthLabels) {
                    Text(
                        label.text,
                        Modifier.offset(x = Pitch * label.column),
                        style = labelStyle,
                        softWrap = false,
                        color = Color(colors.card.secondary),
                    )
                }
            }
            Canvas(
                Modifier.size(gridWidth, gridHeight)
                    .semantics { contentDescription = model.summary }
                    .pointerInput(model.firstMonday, model.today) {
                        detectTapGestures { offset ->
                            val column = floor((offset.x + Gap.toPx() / 2) / Pitch.toPx()).toInt()
                            val row = floor((offset.y + Gap.toPx() / 2) / Pitch.toPx()).toInt()
                            if (column in 0 until Heatmap.WEEKS && row in 0..6) {
                                val date = model.firstMonday.plusDays(column * 7L + row)
                                if (date <= model.today) onSelect(date)
                            }
                        }
                    },
            ) {
                for (cell in model.cells) {
                    drawDay(Offset((Pitch * cell.column).toPx(), (Pitch * cell.row).toPx()), Cell.toPx(), cell.level, colors)
                }
                model.cells.firstOrNull { it.date == selected }?.let { cell ->
                    val inset = 1.5.dp.toPx()
                    drawRoundRect(
                        Color(colors.card.text),
                        Offset((Pitch * cell.column).toPx() - inset, (Pitch * cell.row).toPx() - inset),
                        Size(Cell.toPx() + 2 * inset, Cell.toPx() + 2 * inset),
                        CornerRadius(3.dp.toPx()),
                        style = Stroke(1.5.dp.toPx()),
                    )
                }
            }
        }
    }
}

/** "No data □   Less ■■■■■ More". */
@Composable
private fun Legend(app: AppContext) {
    val colors = LocalAiQuotaColors.current
    val strings = app.strings
    val secondary = Color(colors.card.secondary)
    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(4.dp)) {
        LegendSquare(null, colors)
        Text(strings.heatmapNoData, style = MaterialTheme.typography.labelSmall, color = secondary)
        Box(Modifier.width(12.dp))
        Text(strings.heatmapLess, style = MaterialTheme.typography.labelSmall, color = secondary)
        for (level in 0..4) LegendSquare(level, colors)
        Text(strings.heatmapMore, style = MaterialTheme.typography.labelSmall, color = secondary)
    }
}

@Composable
private fun LegendSquare(level: Int?, colors: AiQuotaColors) {
    Canvas(Modifier.size(Cell)) { drawDay(Offset.Zero, size.width, level, colors) }
}

/** A day's square in its level's colour, or - without samples that day - as an outline. */
private fun DrawScope.drawDay(topLeft: Offset, size: Float, level: Int?, colors: AiQuotaColors) {
    val radius = CornerRadius(2.dp.toPx())
    if (level != null) {
        drawRoundRect(Color(Heatmap.levelColor(level, colors.usage, colors.card)), topLeft, Size(size, size), radius)
    } else {
        val width = 1.dp.toPx()
        drawRoundRect(
            Color(Heatmap.noDataOutline(colors.usage, colors.card)),
            topLeft + Offset(width / 2, width / 2), Size(size - width, size - width), radius, style = Stroke(width),
        )
    }
}
