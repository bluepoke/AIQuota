package eu.schicht8.aiquota.ui

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.graphics.Color
import eu.schicht8.aiquota.core.CardPalette
import eu.schicht8.aiquota.core.UsagePalette

/** The palettes of the bars and cards - the Windows app's colours. */
data class AiQuotaColors(val card: CardPalette, val usage: UsagePalette, val isLight: Boolean)

val LocalAiQuotaColors = staticCompositionLocalOf { AiQuotaColors(CardPalette.of(true), UsagePalette.of(false, true), true) }

/** Light or dark (by default following the system, #19), and the greyscale palette in
 * accessibility mode (#12). */
@Composable
fun AiQuotaTheme(dark: Boolean, greyscale: Boolean, content: @Composable () -> Unit) {
    val light = !dark
    val card = CardPalette.of(light)
    val scheme = if (light) {
        lightColorScheme(
            background = Color(0xFFF3F3F3), surface = Color(0xFFF3F3F3),
            onBackground = Color(card.text), onSurface = Color(card.text),
            surfaceVariant = Color(card.background), onSurfaceVariant = Color(card.secondary),
        )
    } else {
        darkColorScheme(
            background = Color(0xFF202020), surface = Color(0xFF202020),
            onBackground = Color(card.text), onSurface = Color(card.text),
            surfaceVariant = Color(card.background), onSurfaceVariant = Color(card.secondary),
        )
    }
    CompositionLocalProvider(LocalAiQuotaColors provides AiQuotaColors(card, UsagePalette.of(greyscale, light), light)) {
        MaterialTheme(colorScheme = scheme, content = content)
    }
}
