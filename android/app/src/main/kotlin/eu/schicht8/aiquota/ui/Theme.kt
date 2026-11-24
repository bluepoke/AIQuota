package eu.schicht8.aiquota.ui

import androidx.compose.foundation.isSystemInDarkTheme
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

/** Light or dark, following the system (#19). Accessibility mode (#12) follows with settings. */
@Composable
fun AiQuotaTheme(content: @Composable () -> Unit) {
    val light = !isSystemInDarkTheme()
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
    CompositionLocalProvider(LocalAiQuotaColors provides AiQuotaColors(card, UsagePalette.of(greyscale = false, light = light), light)) {
        MaterialTheme(colorScheme = scheme, content = content)
    }
}
