package eu.schicht8.aiquota

import android.app.Application
import android.content.Context
import androidx.core.content.edit
import eu.schicht8.aiquota.core.AccountMonitor
import eu.schicht8.aiquota.core.AppLanguage
import eu.schicht8.aiquota.core.ClaudeCodeVersion
import eu.schicht8.aiquota.core.EncryptedFileTokenStore
import eu.schicht8.aiquota.core.JsonFileAccountStore
import eu.schicht8.aiquota.core.OAuthClient
import eu.schicht8.aiquota.core.Strings
import eu.schicht8.aiquota.core.UrlConnectionTransport
import eu.schicht8.aiquota.core.UsageApiClient
import java.io.File
import java.util.Locale

class AiQuotaApplication : Application() {
    val graph: AppGraph by lazy { AppGraph(this) }
}

/** The app's object graph: one of each, for the whole process. Tokens are in
 * noBackupFilesDir (never in a cloud backup), encrypted with a key in the Android Keystore. */
class AppGraph(context: Context) {
    private val prefs = context.getSharedPreferences("aiquota", Context.MODE_PRIVATE)
    private val transport = UrlConnectionTransport()
    private val tokens = EncryptedFileTokenStore(File(context.noBackupFilesDir, "tokens"), KeystoreTokenCipher())
    private val accounts = JsonFileAccountStore(File(context.filesDir, "accounts.json"))

    private val claudeCodeVersion = ClaudeCodeVersion(
        transport,
        load = { prefs.getString(CLAUDE_CODE_VERSION, null) },
        save = { prefs.edit { putString(CLAUDE_CODE_VERSION, it) } },
    )

    val oauth = OAuthClient(transport, tokens)
    val usageApi = UsageApiClient(transport, oauth, userAgent = claudeCodeVersion::userAgent)
    val monitor = AccountMonitor(accounts, tokens, usageApi, File(context.filesDir, "history"))

    private companion object {
        const val CLAUDE_CODE_VERSION = "claudeCodeVersion"
    }
}

/** The texts in the device's language: German for de-*, English otherwise - as on Windows.
 * (A language setting follows with the settings screen.) */
fun stringsFor(locale: Locale): Strings =
    Strings(if (locale.language == "de") AppLanguage.German else AppLanguage.English)
