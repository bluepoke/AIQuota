package eu.schicht8.aiquota.ui

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.Column
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.res.painterResource
import androidx.core.os.ConfigurationCompat
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import eu.schicht8.aiquota.AppViewModel
import eu.schicht8.aiquota.R
import eu.schicht8.aiquota.Screen
import eu.schicht8.aiquota.core.AccountState
import eu.schicht8.aiquota.core.Strings
import eu.schicht8.aiquota.stringsFor
import kotlinx.coroutines.delay
import java.time.Instant
import java.util.Locale

/** What every screen gets: the texts, the clock, and the app-wide actions. */
class AppContext(
    val viewModel: AppViewModel,
    val strings: Strings,
    /** Countdowns ("in 2.5h") and pace markers move on between fetches. */
    val now: Instant,
    val openUrl: (String) -> Unit,
    /** Starts a login for that account, or for a new one with null - asking how to open the
     * sign-in page while other accounts are saved. */
    val startLogin: (AccountState?) -> Unit,
)

/** The app: the current screen of [AppViewModel.screens], with system back going one step back. */
@Composable
fun AiQuotaApp(viewModel: AppViewModel, openUrl: (String) -> Unit, canNotify: () -> Boolean, openNotificationSettings: () -> Unit) {
    val screens by viewModel.screens.collectAsStateWithLifecycle()
    val settings by viewModel.settings.collectAsStateWithLifecycle()
    val accounts by viewModel.accounts.collectAsStateWithLifecycle()
    val configuration = LocalConfiguration.current
    val strings = remember(configuration, settings.language) {
        stringsFor(settings.language, ConfigurationCompat.getLocales(configuration)[0] ?: Locale.getDefault())
    }
    val now by produceState(Instant.now()) {
        while (true) {
            delay(30_000)
            value = Instant.now()
        }
    }

    var loginChoice by remember { mutableStateOf<LoginChoice?>(null) }
    fun startLogin(target: AccountState?) {
        if (accounts.none { it.account.key != target?.account?.key }) {
            viewModel.startLogin(target?.account?.key, copyLink = false)
        } else {
            loginChoice = LoginChoice(target)
        }
    }
    val app = AppContext(viewModel, strings, now, openUrl, ::startLogin)

    BackHandler(enabled = screens.size > 1) { viewModel.back() }
    when (val screen = screens.last()) {
        Screen.Overview -> OverviewScreen(app)
        is Screen.Details -> DetailsScreen(app, screen.accountKey)
        is Screen.Heatmap -> HeatmapScreen(app, screen.accountKey)
        Screen.Settings -> SettingsScreen(app, canNotify, openNotificationSettings)
    }

    loginChoice?.let { choice ->
        val heading = choice.target?.let { strings.loginAgainHeading(strings.accountName(it.account)) } ?: strings.addAccountHeading
        ChooseLoginDialog(
            heading, strings,
            onChoose = { copyLink ->
                loginChoice = null
                viewModel.startLogin(choice.target?.account?.key, copyLink)
            },
            onDismiss = { loginChoice = null },
        )
    }
}

/** How to open the sign-in page is being asked - for [target], or a new account with null. */
private data class LoginChoice(val target: AccountState?)

@Composable
private fun ChooseLoginDialog(heading: String, strings: Strings, onChoose: (copyLink: Boolean) -> Unit, onDismiss: () -> Unit) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(heading) },
        text = { Text(strings.addAccountText) },
        confirmButton = {
            Column(horizontalAlignment = Alignment.End) {
                TextButton(onClick = { onChoose(false) }) { Text(strings.addAccountOpenBrowser) }
                TextButton(onClick = { onChoose(true) }) { Text(strings.addAccountCopyLink) }
                TextButton(onClick = onDismiss) { Text(strings.dialogCancel) }
            }
        },
    )
}

/** The top bar's back arrow. */
@Composable
fun BackButton(strings: Strings, onBack: () -> Unit) {
    IconButton(onClick = onBack) {
        Icon(painterResource(R.drawable.ic_arrow_back), contentDescription = strings.back)
    }
}
