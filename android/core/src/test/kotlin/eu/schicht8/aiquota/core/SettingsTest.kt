package eu.schicht8.aiquota.core

import java.io.File
import java.nio.file.Files
import java.time.Duration
import kotlin.test.Test
import kotlin.test.assertEquals

class SettingsTest {
    private val file = File(Files.createTempDirectory("aiquota-settings").toFile(), "settings.json")
    private val store = JsonFileSettingsStore(file)

    @Test
    fun `defaults - system language and theme, pace markers and notifications on, every 5 minutes, background every 15`() {
        val settings = store.load()

        assertEquals(
            AppSettings(
                language = LanguageSetting.System,
                theme = ThemeSetting.System,
                accessibilityMode = false,
                showPaceMarker = true,
                notifications = true,
                pollInterval = Duration.ofMinutes(5),
                backgroundInterval = Duration.ofMinutes(15),
            ),
            settings,
        )
    }

    @Test
    fun `saved settings are loaded again`() {
        val changed = AppSettings(
            language = LanguageSetting.German,
            theme = ThemeSetting.Dark,
            accessibilityMode = true,
            showPaceMarker = false,
            notifications = false,
            pollInterval = Duration.ofMinutes(15),
            backgroundInterval = null,
        )

        store.save(changed)

        assertEquals(changed, JsonFileSettingsStore(file).load())
    }

    @Test
    fun `unknown or out-of-range values fall back to their default, the others are kept`() {
        file.writeText("""{"language":"Klingon","theme":"Dark","pollMinutes":3,"backgroundMinutes":7,"accessibilityMode":"yes"}""")

        val settings = store.load()

        assertEquals(LanguageSetting.System, settings.language)
        assertEquals(ThemeSetting.Dark, settings.theme)
        assertEquals(false, settings.accessibilityMode)
        assertEquals(Duration.ofMinutes(5), settings.pollInterval)
        assertEquals(Duration.ofMinutes(15), settings.backgroundInterval, "Android runs periodic work at most every 15 minutes")
    }

    @Test
    fun `a broken file gives the defaults`() {
        file.writeText("{not json")

        assertEquals(AppSettings(), store.load())
    }
}
