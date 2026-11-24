package eu.schicht8.aiquota

import android.app.Application
import android.content.Context
import androidx.core.content.edit
import eu.schicht8.aiquota.core.AccountMonitor
import eu.schicht8.aiquota.core.AppLanguage
import eu.schicht8.aiquota.core.AppSettings
import eu.schicht8.aiquota.core.ClaudeCodeVersion
import eu.schicht8.aiquota.core.EncryptedFileTokenStore
import eu.schicht8.aiquota.core.JsonFileAccountStore
import eu.schicht8.aiquota.core.JsonFileMonitorStateStore
import eu.schicht8.aiquota.core.JsonFileSettingsStore
import eu.schicht8.aiquota.core.LanguageSetting
import eu.schicht8.aiquota.core.OAuthClient
import eu.schicht8.aiquota.core.Strings
import eu.schicht8.aiquota.core.UrlConnectionTransport
import eu.schicht8.aiquota.core.UsageApiClient
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.updateAndGet
import java.io.File
import java.util.Locale

class AiQuotaApplication : Application() {
    val graph: AppGraph by lazy { AppGraph(this) }

    override fun onCreate() {
        super.onCreate()
        // Channels exist from the start, so they can be set up in the system settings before
        // the first notification; the background job is (re)scheduled with the saved interval.
        graph.notifier.createChannels(graph.strings())
        BackgroundUpdates.schedule(this, graph.settings.value.backgroundInterval)
    }
}

/** The app's object graph: one of each, for the whole process - shared by the UI and the
 * background job. Tokens are in noBackupFilesDir (never in a cloud backup), encrypted with a
 * key in the Android Keystore. */
class AppGraph(private val context: Context) {
    private val prefs = context.getSharedPreferences("aiquota", Context.MODE_PRIVATE)
    private val transport = UrlConnectionTransport()
    private val tokens = EncryptedFileTokenStore(File(context.noBackupFilesDir, "tokens"), KeystoreTokenCipher())
    private val accounts = JsonFileAccountStore(File(context.filesDir, "accounts.json"))
    private val settingsStore = JsonFileSettingsStore(File(context.filesDir, "settings.json"))

    private val _settings = MutableStateFlow(settingsStore.load())
    val settings: StateFlow<AppSettings> = _settings.asStateFlow()

    private val claudeCodeVersion = ClaudeCodeVersion(
        transport,
        load = { prefs.getString(CLAUDE_CODE_VERSION, null) },
        save = { prefs.edit { putString(CLAUDE_CODE_VERSION, it) } },
    )

    val oauth = OAuthClient(transport, tokens)
    val usageApi = UsageApiClient(transport, oauth, userAgent = claudeCodeVersion::userAgent)
    val notifier = UsageNotifier(context, ::strings, enabled = { _settings.value.notifications })
    val monitor = AccountMonitor(
        accounts, tokens, usageApi, File(context.filesDir, "history"),
        pollInterval = { _settings.value.pollInterval },
        stateStore = JsonFileMonitorStateStore(File(context.filesDir, "monitor.json")),
        alerts = notifier,
        strings = ::strings,
    )

    /** The texts in the chosen language, at the time they're needed. */
    fun strings(): Strings = stringsFor(_settings.value.language, Locale.getDefault())

    /** Saves a change of the settings (blocking: call off the main thread) and applies the
     * parts that live outside the UI. */
    fun updateSettings(change: (AppSettings) -> AppSettings) {
        val before = _settings.value
        val after = _settings.updateAndGet(change)
        settingsStore.save(after)
        if (after.backgroundInterval != before.backgroundInterval) BackgroundUpdates.schedule(context, after.backgroundInterval)
        if (after.language != before.language) notifier.createChannels(strings())
        if (after.pollInterval != before.pollInterval) monitor.pollIntervalChanged()
    }

    private companion object {
        const val CLAUDE_CODE_VERSION = "claudeCodeVersion"
    }
}

/** The texts in the chosen language; "system" is German for de-*, English otherwise - as on
 * Windows. */
fun stringsFor(language: LanguageSetting, locale: Locale): Strings = Strings(
    when (language) {
        LanguageSetting.German -> AppLanguage.German
        LanguageSetting.English -> AppLanguage.English
        LanguageSetting.System -> if (locale.language == "de") AppLanguage.German else AppLanguage.English
    },
)
