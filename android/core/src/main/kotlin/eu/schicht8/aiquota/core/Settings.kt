package eu.schicht8.aiquota.core

import kotlinx.serialization.SerializationException
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import java.io.File
import java.io.IOException
import java.time.Duration

enum class LanguageSetting { System, German, English }

enum class ThemeSetting { System, Light, Dark }

/**
 * The app's settings (#19). [language] and [theme] follow the system by default.
 * [accessibilityMode]: the high-contrast greyscale palette (#12). [pollInterval]: how often
 * each account is fetched while the app is open. [backgroundInterval]: how often all accounts
 * are fetched while it's closed; null turns that off.
 */
data class AppSettings(
    val language: LanguageSetting = LanguageSetting.System,
    val theme: ThemeSetting = ThemeSetting.System,
    val accessibilityMode: Boolean = false,
    val showPaceMarker: Boolean = true,
    val notifications: Boolean = true,
    val pollInterval: Duration = POLL_INTERVALS[0],
    val backgroundInterval: Duration? = BACKGROUND_INTERVALS[0],
) {
    companion object {
        /** The choices offered; the first is the default - every 5 minutes, as on Windows. */
        val POLL_INTERVALS: List<Duration> = listOf(5L, 10L, 15L, 30L).map(Duration::ofMinutes)

        /** Android runs periodic work at most every 15 minutes, which is the default. */
        val BACKGROUND_INTERVALS: List<Duration> = listOf(15L, 30L, 60L, 120L).map(Duration::ofMinutes)
    }
}

interface SettingsStore {
    fun load(): AppSettings
    fun save(settings: AppSettings)
}

/** The settings as one JSON file. Anything missing, unknown or out of range falls back to its
 * default, so an old or damaged file never keeps the app from starting. */
class JsonFileSettingsStore(private val file: File) : SettingsStore {
    @Synchronized
    override fun load(): AppSettings {
        val json = try {
            if (file.exists()) Json.parseToJsonElement(file.readText()) as? JsonObject else null
        } catch (_: IOException) {
            null
        } catch (_: SerializationException) {
            null
        } ?: return AppSettings()

        val defaults = AppSettings()
        val background = json.long("backgroundMinutes")
        return AppSettings(
            language = json.enumValue<LanguageSetting>("language") ?: defaults.language,
            theme = json.enumValue<ThemeSetting>("theme") ?: defaults.theme,
            accessibilityMode = json.bool("accessibilityMode") ?: defaults.accessibilityMode,
            showPaceMarker = json.bool("showPaceMarker") ?: defaults.showPaceMarker,
            notifications = json.bool("notifications") ?: defaults.notifications,
            pollInterval = json.long("pollMinutes")?.let(Duration::ofMinutes)?.takeIf { it in AppSettings.POLL_INTERVALS }
                ?: defaults.pollInterval,
            backgroundInterval = when {
                background == 0L -> null
                else -> background?.let(Duration::ofMinutes)?.takeIf { it in AppSettings.BACKGROUND_INTERVALS } ?: defaults.backgroundInterval
            },
        )
    }

    @Synchronized
    override fun save(settings: AppSettings) {
        val json = buildJsonObject {
            put("language", settings.language.name)
            put("theme", settings.theme.name)
            put("accessibilityMode", settings.accessibilityMode)
            put("showPaceMarker", settings.showPaceMarker)
            put("notifications", settings.notifications)
            put("pollMinutes", settings.pollInterval.toMinutes())
            put("backgroundMinutes", settings.backgroundInterval?.toMinutes() ?: 0L)
        }
        writeAtomically(file, json.toString().toByteArray(Charsets.UTF_8))
    }

    private inline fun <reified E : Enum<E>> JsonObject.enumValue(name: String): E? =
        string(name)?.let { value -> enumValues<E>().firstOrNull { it.name == value } }
}
