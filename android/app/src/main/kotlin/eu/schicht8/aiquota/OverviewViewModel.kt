package eu.schicht8.aiquota

import android.app.Application
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import eu.schicht8.aiquota.core.AccountState
import eu.schicht8.aiquota.core.LoopbackListener
import eu.schicht8.aiquota.core.OAuthConfig
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineExceptionHandler
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.receiveAsFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import java.util.Locale
import kotlin.time.Duration.Companion.minutes

/** A login waiting for the browser. [linkCopied]: the link went to the clipboard instead.
 * [id] tells two logins for the same account apart. */
data class LoginInProgress(val targetKey: String?, val linkCopied: Boolean, val id: Long)

class OverviewViewModel(application: Application) : AndroidViewModel(application) {
    private val graph = (application as AiQuotaApplication).graph
    private val monitor = graph.monitor

    val accounts: StateFlow<List<AccountState>> = monitor.states

    private val _isRefreshing = MutableStateFlow(false)
    val isRefreshing: StateFlow<Boolean> = _isRefreshing.asStateFlow()

    private val _login = MutableStateFlow<LoginInProgress?>(null)
    val login: StateFlow<LoginInProgress?> = _login.asStateFlow()
    private var loginJob: Job? = null
    private var loginCount = 0L

    private val _messages = Channel<String>(Channel.BUFFERED)
    /** One-off messages for a snackbar: where a login went, why it failed. */
    val messages: Flow<String> = _messages.receiveAsFlow()

    private val _openUrls = Channel<String>(Channel.BUFFERED)
    /** URLs for the activity to open in a Custom Tab. */
    val openUrls: Flow<String> = _openUrls.receiveAsFlow()

    private fun strings() = stringsFor(Locale.getDefault())

    /** A storage failure (disk full, Keystore unavailable) in a background step is shown,
     * not a crash. */
    private val showFailure = CoroutineExceptionHandler { _, error ->
        _messages.trySend(error.message ?: error.javaClass.simpleName)
    }

    /** Called periodically while the app is in front: fetches the accounts that are due. */
    fun fetchDue() {
        viewModelScope.launch(Dispatchers.IO + showFailure) { monitor.fetchDue() }
    }

    /** Pull to refresh. */
    fun refresh() {
        viewModelScope.launch(Dispatchers.IO + showFailure) {
            _isRefreshing.value = true
            try {
                monitor.refreshAll()
            } finally {
                _isRefreshing.value = false
            }
        }
    }

    /**
     * Logs in - a new account ([targetKey] null) or again for a saved one: opens claude.ai's
     * consent page in a Custom Tab (or copies its link, for a private tab), waits up to 5
     * minutes for the redirect to the loopback listener, and stores the tokens with the
     * account they belong to.
     */
    fun startLogin(targetKey: String?, copyLink: Boolean) {
        if (_login.value != null) return
        val login = LoginInProgress(targetKey, copyLink, ++loginCount)
        _login.value = login
        loginJob = viewModelScope.launch {
            val strings = strings()
            try {
                withContext(Dispatchers.IO) { LoopbackListener.bindFirstAvailable() }.use { listener ->
                    val request = graph.oauth.beginLogin(OAuthConfig.redirectUri(listener.port))
                    if (copyLink) copyToClipboard(request.authorizeUrl) else _openUrls.send(request.authorizeUrl)

                    val redirect = withTimeout(5.minutes) { listener.waitForRedirect(strings, RETURN_URL) }
                    val tokens = withContext(Dispatchers.IO) { graph.oauth.completeLogin(request, redirect) }
                    val info = withContext(Dispatchers.IO) { graph.usageApi.fetchAccountWithToken(tokens.accessToken) }
                    val message = withContext(Dispatchers.IO) { monitor.addLogin(targetKey, tokens, info) }
                    message?.let { _messages.send(strings.loginMessage(it)) }
                }
                endLogin(login)
                withContext(Dispatchers.IO) { monitor.fetchDue() }
            } catch (e: TimeoutCancellationException) {
                _messages.send(strings.loginError(e))
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                _messages.send(strings.loginError(e))
            } finally {
                endLogin(login)
            }
        }
    }

    /** Ends [login] only - not a newer one started after it was cancelled. */
    private fun endLogin(login: LoginInProgress) {
        _login.compareAndSet(login, null)
    }

    fun cancelLogin() {
        loginJob?.cancel()
        _login.value = null
    }

    fun rename(key: String, shortName: String) {
        viewModelScope.launch(Dispatchers.IO + showFailure) { monitor.rename(key, shortName) }
    }

    fun remove(key: String) {
        viewModelScope.launch(Dispatchers.IO + showFailure) { monitor.remove(key) }
    }

    private fun copyToClipboard(text: String) {
        val clipboard = getApplication<Application>().getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        clipboard.setPrimaryClip(ClipData.newPlainText("AI Quota", text))
    }

    companion object {
        /** The "Back to AI Quota" link on the login result page; MainActivity handles it. */
        const val RETURN_URL = "aiquota://login-complete"
    }
}
