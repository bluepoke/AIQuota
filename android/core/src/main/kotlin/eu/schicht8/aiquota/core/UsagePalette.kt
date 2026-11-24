package eu.schicht8.aiquota.core

/**
 * The colours of the usage bars, as ARGB values - the same as the Windows app's
 * AIQuota/UsagePalette.cs and the flyout's FlyoutPalette (AIQuota/UsageBarRenderer.cs), so
 * both apps agree. [standard]: green (≤ 50%), yellow (≤ 80%), red (above). [greyscale]
 * (accessibility mode, #12): no hue - the level is shown by brightness, plus a notch at the
 * fill's end above 80%.
 */
data class UsagePalette(
    val isGreyscale: Boolean,
    val low: Long,
    val medium: Long,
    val high: Long,
    /** The empty part of a bar; null uses [CardPalette.track]. */
    val track: Long?,
    /** 1px outline around the track; null draws none. */
    val trackOutline: Long?,
    /** The session bar's outline while no session has started (greyscale only). */
    val notStarted: Long?,
    val highNotch: Long,
) {
    /** Levels above [HIGH_THRESHOLD] get an extra shape cue in the greyscale palette. */
    val marksHighLevel: Boolean get() = isGreyscale

    fun forPercent(percent: Int): Long = when {
        percent > HIGH_THRESHOLD -> high
        percent > MEDIUM_THRESHOLD -> medium
        else -> low
    }

    companion object {
        const val HIGH_THRESHOLD = 80
        const val MEDIUM_THRESHOLD = 50

        fun isHigh(percent: Int) = percent > HIGH_THRESHOLD

        fun of(greyscale: Boolean, light: Boolean): UsagePalette = when {
            !greyscale -> STANDARD
            light -> GREYSCALE_LIGHT
            else -> GREYSCALE_DARK
        }

        private val STANDARD = UsagePalette(
            isGreyscale = false,
            low = 0xFF5ABE6E, medium = 0xFFF0AA3C, high = 0xFFEB4646,
            track = null, trackOutline = null, notStarted = null, highNotch = 0xFF000000,
        )

        private val GREYSCALE_DARK = UsagePalette(
            isGreyscale = true,
            low = 0xFF9A9A9A, medium = 0xFFD0D0D0, high = 0xFFFFFFFF,
            track = 0xFF3C3C3C, trackOutline = 0xFF808080, notStarted = 0xFF808080, highNotch = 0xFF000000,
        )

        private val GREYSCALE_LIGHT = UsagePalette(
            isGreyscale = true,
            low = 0xFF6A6A6A, medium = 0xFF3A3A3A, high = 0xFF000000,
            track = 0xFFD8D8D8, trackOutline = 0xFF707070, notStarted = 0xFF707070, highNotch = 0xFFFFFFFF,
        )
    }
}

/** The card colours for the light and dark theme - the Windows flyout's. [grid] and [pace]:
 * the 7-day chart's grid lines and even-pace line. */
data class CardPalette(
    val isLight: Boolean,
    val background: Long,
    val text: Long,
    val secondary: Long,
    val track: Long,
    val border: Long,
    val grid: Long,
    val pace: Long,
) {
    companion object {
        fun of(light: Boolean) = if (light) LIGHT else DARK

        private val LIGHT = CardPalette(true, 0xFFF9F9F9, 0xFF1A1A1A, 0xFF606060, 0xFFD6D6D6, 0xFFD6D6D6, 0xFFE5E5E5, 0xFF464646)
        private val DARK = CardPalette(false, 0xFF2B2B2B, 0xFFFFFFFF, 0xFFB2B2B2, 0xFF505050, 0xFF464646, 0xFF3E3E3E, 0xFFCDCDCD)
    }
}
