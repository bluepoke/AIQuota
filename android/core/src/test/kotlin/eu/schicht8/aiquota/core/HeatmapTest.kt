package eu.schicht8.aiquota.core

import java.time.LocalDate
import java.time.ZoneId
import kotlin.test.Test
import kotlin.test.assertEquals

class HeatmapTest {
    private val english = Strings(AppLanguage.English, ZoneId.of("Europe/Berlin"))
    private val german = Strings(AppLanguage.German, ZoneId.of("Europe/Berlin"))

    /** A Tuesday. */
    private val today = LocalDate.of(2026, 11, 24)

    private fun day(date: String, points: Double, peak: Double = 40.0, hours: Int = 0b11) =
        DailyUsageRecord(LocalDate.parse(date), points, peak, hours, null)

    @Test
    fun `53 week columns from Monday to Sunday, the current week last, days up to today`() {
        val model = Heatmap.build(emptyList(), today, english)

        assertEquals(LocalDate.of(2025, 11, 24), model.firstMonday)
        assertEquals(52 * 7 + 2, model.cells.size)
        val last = model.cells.last()
        assertEquals(today, last.date)
        assertEquals(52 to 1, last.column to last.row)
        assertEquals(null, last.level, "no samples that day")
    }

    @Test
    fun `fixed levels by the share of the weekly quota used that day`() {
        assertEquals(listOf(0, 1, 1, 2, 3, 4), listOf(0.4, 0.5, 4.99, 5.0, 10.0, 20.0).map(Heatmap::level))
    }

    @Test
    fun `the summary counts active days and names the busiest calendar week - all its days summed`() {
        val days = listOf(
            day("2026-11-17", 8.0),
            day("2026-11-18", 5.2),
            day("2026-11-20", 0.3),
            day("2026-11-24", 3.0),
        )

        assertEquals("3 active days in the last year · busiest week: 14% (16–22 Nov)", Heatmap.build(days, today, english).summary)
        assertEquals("3 aktive Tage im letzten Jahr · stärkste Woche: 14% (16.–22. Nov)", Heatmap.build(days, today, german).summary)
        assertEquals("No usage recorded in the last year yet", Heatmap.build(listOf(day("2026-11-20", 0.3)), today, english).summary)
    }

    @Test
    fun `tapping a day shows its values, or that the app wasn't running`() {
        val model = Heatmap.build(listOf(day("2026-11-17", 12.5, peak = 64.5, hours = 0b111)), today, english)

        assertEquals("Tue, 17 Nov 2026 · 12% of the weekly quota · session peak 64% · 3 h active", model.dayText(LocalDate.of(2026, 11, 17)))
        assertEquals("Mon, 16 Nov 2026 · no data (app not running)", model.dayText(LocalDate.of(2026, 11, 16)))
        assertEquals(
            "Di, 17. Nov 2026 · 12% des Wochenkontingents · Sitzungsspitze 64% · 3 h aktiv",
            Heatmap.build(model.days, today, german).dayText(LocalDate.of(2026, 11, 17)),
        )
    }

    @Test
    fun `month names above the column with the month's first day`() {
        val labels = Heatmap.build(emptyList(), today, english).monthLabels

        assertEquals(1 to "Dec", labels.first().column to labels.first().text)
        assertEquals(48 to "Nov", labels.last().column to labels.last().text)
        assertEquals(12, labels.size)
    }

    @Test
    fun `level colours - the app's green on the card, or greys in accessibility mode`() {
        val card = CardPalette.of(light = true)
        val standard = UsagePalette.of(greyscale = false, light = true)

        assertEquals(card.track, Heatmap.levelColor(0, standard, card))
        assertEquals(standard.low, Heatmap.levelColor(4, standard, card))
        assertEquals(0xFF808080, Heatmap.levelColor(2, UsagePalette.of(greyscale = true, light = true), card))
    }
}
