package eu.schicht8.aiquota.core

import java.time.LocalDate
import java.time.temporal.ChronoUnit

/** One day's square: [column] is the week (0 = oldest), [row] the weekday (0 = Monday).
 * [day]: its values, null if there was no sample that day. */
data class HeatmapCell(val date: LocalDate, val column: Int, val row: Int, val day: DailyUsageRecord?) {
    /** 0-4, null without data (drawn as an outline only). */
    val level: Int? get() = day?.let { Heatmap.level(it.weekPoints) }
}

data class MonthLabel(val column: Int, val text: String)

/** What the yearly heatmap shows - laid out, with its texts. */
data class HeatmapModel(
    val days: List<DailyUsageRecord>,
    val today: LocalDate,
    val firstMonday: LocalDate,
    val cells: List<HeatmapCell>,
    val monthLabels: List<MonthLabel>,
    val summary: String,
    private val strings: Strings,
) {
    private val byDate = days.associateBy { it.date }

    /** A day's values, as the Windows heatmap's hover text. */
    fun dayText(date: LocalDate): String {
        val day = byDate[date]
        return strings.heatmapDayText(date, day?.weekPoints, day?.sessionPeak ?: 0.0, day?.activeHours ?: 0)
    }
}

/**
 * The yearly heatmap (#17) - the layout and texts of the Windows app's AIQuota/UsageHeatmap.cs:
 * one square per local day, in 53 week columns from Monday to Sunday with the current week
 * last, coloured by the share of the weekly quota used that day.
 */
object Heatmap {
    const val WEEKS = 53

    /** Upper bounds (weekly points per day) of levels 0-3; above the last is level 4. Fixed,
     * so years stay comparable - 100% spread evenly over 7 days is about 14 points a day. */
    private val LEVEL_BOUNDS = doubleArrayOf(0.5, 5.0, 10.0, 20.0)

    /** Strength of the green per level, from the card's background to full. */
    private val LEVEL_STRENGTH = floatArrayOf(0f, 0.32f, 0.55f, 0.78f, 1f)
    private val GREYS_LIGHT = intArrayOf(0xD8, 0xA8, 0x80, 0x58, 0x20)
    private val GREYS_DARK = intArrayOf(0x3C, 0x62, 0x8A, 0xB6, 0xF0)

    fun level(points: Double): Int = LEVEL_BOUNDS.indexOfFirst { points < it }.let { if (it < 0) LEVEL_BOUNDS.size else it }

    /** Monday of the leftmost week column: the current week is the last one. */
    fun firstMonday(today: LocalDate): LocalDate =
        today.minusDays((today.dayOfWeek.value - 1).toLong()).minusWeeks((WEEKS - 1).toLong())

    fun build(days: List<DailyUsageRecord>, today: LocalDate, strings: Strings): HeatmapModel {
        val firstMonday = firstMonday(today)
        val byDate = days.associateBy { it.date }
        val cells = generateSequence(firstMonday) { it.plusDays(1) }
            .takeWhile { it <= today }
            .map { date ->
                val offset = ChronoUnit.DAYS.between(firstMonday, date).toInt()
                HeatmapCell(date, offset / 7, offset % 7, byDate[date])
            }
            .toList()
        return HeatmapModel(days, today, firstMonday, cells, monthLabels(firstMonday, today, strings), summary(cells, firstMonday, strings), strings)
    }

    /** Month names above the column holding the month's first day, left out when they'd
     * crowd the previous one; the first column gets its month if the next label is far
     * enough away. */
    private fun monthLabels(firstMonday: LocalDate, today: LocalDate, strings: Strings): List<MonthLabel> {
        val endOfWeek = today.plusDays((7 - today.dayOfWeek.value).toLong())
        val labels = mutableListOf<Pair<Int, Int>>()
        for (column in 0 until WEEKS) {
            for (i in 0 until 7) {
                val date = firstMonday.plusDays(column * 7L + i)
                if (date.dayOfMonth == 1 && date <= endOfWeek) labels += column to date.monthValue
            }
        }
        if (labels.isEmpty() || labels[0].first >= 3) labels.add(0, 0 to firstMonday.monthValue)

        val shown = mutableListOf<MonthLabel>()
        var lastColumn = -WEEKS
        for ((column, month) in labels) {
            if (column - lastColumn < 3) continue
            shown += MonthLabel(column, strings.monthShort(month))
            lastColumn = column
        }
        return shown
    }

    /** "128 active days in the last year · busiest week: 98% (3–9 Mar)". */
    private fun summary(cells: List<HeatmapCell>, firstMonday: LocalDate, strings: Strings): String {
        val withData = cells.filter { it.day != null }
        val activeDays = withData.count { (it.level ?: 0) > 0 }
        if (activeDays == 0) return strings.heatmapNoUsageYet
        val busiest = withData.groupBy { it.column }
            .map { (column, week) -> column to week.sumOf { it.day!!.weekPoints } }
            .sortedWith(compareByDescending<Pair<Int, Double>> { it.second }.thenByDescending { it.first })
            .first()
        return strings.heatmapSummary(activeDays, busiest.second, firstMonday.plusWeeks(busiest.first.toLong()))
    }

    /** A level's colour: the app's green, from close to the card's background to full, on the
     * card's track for "no usage"; in accessibility mode greys that differ in brightness only. */
    fun levelColor(level: Int, usage: UsagePalette, card: CardPalette): Long {
        if (usage.isGreyscale) {
            val grey = (if (card.isLight) GREYS_LIGHT else GREYS_DARK)[level].toLong()
            return 0xFF000000 or (grey shl 16) or (grey shl 8) or grey
        }
        if (level == 0) return card.track
        return blend(card.background, usage.low, LEVEL_STRENGTH[level])
    }

    /** The outline of a day without data. */
    fun noDataOutline(usage: UsagePalette, card: CardPalette): Long = usage.trackOutline ?: card.track

    private fun blend(from: Long, to: Long, amount: Float): Long {
        fun channel(shift: Int): Long {
            val a = (from shr shift) and 0xFF
            val b = (to shr shift) and 0xFF
            return (a + (b - a) * amount.toDouble()).roundHalfEven().toLong()
        }
        return 0xFF000000 or (channel(16) shl 16) or (channel(8) shl 8) or channel(0)
    }
}
