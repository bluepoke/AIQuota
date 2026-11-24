package eu.schicht8.aiquota.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.unit.dp
import androidx.core.os.ConfigurationCompat
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import eu.schicht8.aiquota.OverviewViewModel
import eu.schicht8.aiquota.R
import eu.schicht8.aiquota.core.AccountState
import eu.schicht8.aiquota.core.Strings
import eu.schicht8.aiquota.stringsFor
import kotlinx.coroutines.delay
import java.time.Instant

/** What a dialog on the overview is about. */
private sealed interface OverviewDialog {
    /** How to open the sign-in page, while other accounts are saved. */
    data class ChooseLogin(val targetKey: String?, val heading: String) : OverviewDialog
    data class Rename(val account: AccountState) : OverviewDialog
    data class Remove(val account: AccountState) : OverviewDialog
}

/**
 * The start screen (#19): one card per saved account, in the account list's order, each with
 * its own values, errors and "login required" state. Pull to refresh fetches all of them.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun OverviewScreen(viewModel: OverviewViewModel, openUrl: (String) -> Unit) {
    val accounts by viewModel.accounts.collectAsStateWithLifecycle()
    val isRefreshing by viewModel.isRefreshing.collectAsStateWithLifecycle()
    val login by viewModel.login.collectAsStateWithLifecycle()
    val configuration = LocalConfiguration.current
    val strings = remember(configuration) { stringsFor(ConfigurationCompat.getLocales(configuration)[0] ?: java.util.Locale.getDefault()) }
    // Countdowns ("in 2.5h") and the pace marker move on between fetches.
    val now by produceState(Instant.now()) {
        while (true) {
            delay(30_000)
            value = Instant.now()
        }
    }
    val snackbar = remember { SnackbarHostState() }
    LaunchedEffect(viewModel) { viewModel.messages.collect { snackbar.showSnackbar(it) } }

    var dialog by remember { mutableStateOf<OverviewDialog?>(null) }
    fun startLogin(target: AccountState?) {
        if (accounts.none { it.account.key != target?.account?.key }) {
            viewModel.startLogin(target?.account?.key, copyLink = false)
        } else {
            val heading = target?.let { strings.loginAgainHeading(it.account.displayName ?: strings.accountUnnamed) } ?: strings.addAccountHeading
            dialog = OverviewDialog.ChooseLogin(target?.account?.key, heading)
        }
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(strings.appTitle) },
                actions = {
                    IconButton(onClick = { startLogin(null) }, enabled = login == null) {
                        Icon(painterResource(R.drawable.ic_add), contentDescription = strings.addAccount)
                    }
                },
            )
        },
        snackbarHost = { SnackbarHost(snackbar) },
    ) { padding ->
        PullToRefreshBox(
            isRefreshing = isRefreshing,
            onRefresh = viewModel::refresh,
            modifier = Modifier.fillMaxSize().padding(padding),
        ) {
            LazyColumn(
                modifier = Modifier.fillMaxSize(),
                contentPadding = PaddingValues(16.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                login?.let { inProgress ->
                    item(key = "login") {
                        LoginBanner(if (inProgress.linkCopied) strings.loginLinkCopied else strings.loginInProgress, strings, viewModel::cancelLogin)
                    }
                }
                if (accounts.isEmpty() && login == null) {
                    item(key = "empty") { EmptyState(strings) { startLogin(null) } }
                }
                items(accounts, key = { it.account.key }) { state ->
                    AccountCard(
                        state = state,
                        now = now,
                        strings = strings,
                        loginEnabled = login == null,
                        onLogin = { startLogin(state) },
                        onRename = { dialog = OverviewDialog.Rename(state) },
                        onRemove = { dialog = OverviewDialog.Remove(state) },
                        onOpenUsagePage = { openUrl(USAGE_PAGE_URL) },
                        modifier = Modifier.widthIn(max = 640.dp),
                    )
                }
            }
        }
    }

    when (val current = dialog) {
        is OverviewDialog.ChooseLogin -> ChooseLoginDialog(
            current.heading, strings,
            onChoose = { copyLink ->
                dialog = null
                viewModel.startLogin(current.targetKey, copyLink)
            },
            onDismiss = { dialog = null },
        )
        is OverviewDialog.Rename -> RenameDialog(
            current.account, strings,
            onConfirm = {
                dialog = null
                viewModel.rename(current.account.account.key, it)
            },
            onDismiss = { dialog = null },
        )
        is OverviewDialog.Remove -> AlertDialog(
            onDismissRequest = { dialog = null },
            text = { Text(strings.confirmRemoveAccount(current.account.account.displayName ?: strings.accountUnnamed)) },
            confirmButton = {
                TextButton(onClick = {
                    dialog = null
                    viewModel.remove(current.account.account.key)
                }) { Text(strings.remove) }
            },
            dismissButton = { TextButton(onClick = { dialog = null }) { Text(strings.dialogCancel) } },
        )
        null -> Unit
    }
}

@Composable
private fun LoginBanner(text: String, strings: Strings, onCancel: () -> Unit) {
    Card(
        modifier = Modifier.fillMaxWidth().widthIn(max = 640.dp),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant),
    ) {
        Row(Modifier.padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
            Text(text, Modifier.weight(1f), style = MaterialTheme.typography.bodyMedium)
            TextButton(onClick = onCancel) { Text(strings.dialogCancel) }
        }
    }
}

@Composable
private fun EmptyState(strings: Strings, onLogin: () -> Unit) {
    Column(
        Modifier.fillMaxWidth().padding(top = 48.dp, start = 16.dp, end = 16.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(16.dp),
    ) {
        Text(strings.noAccounts, style = MaterialTheme.typography.bodyLarge)
        Button(onClick = onLogin) { Text(strings.logIn) }
    }
}

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

@Composable
private fun RenameDialog(state: AccountState, strings: Strings, onConfirm: (String) -> Unit, onDismiss: () -> Unit) {
    var name by rememberSaveable { mutableStateOf(state.account.shortName ?: "") }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(strings.renameAccountTitle) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                Text(strings.renameAccountPrompt(state.account.label ?: strings.accountUnnamed))
                Box { OutlinedTextField(value = name, onValueChange = { name = it }, singleLine = true) }
            }
        },
        confirmButton = { TextButton(onClick = { onConfirm(name) }) { Text(strings.dialogOk) } },
        dismissButton = { TextButton(onClick = onDismiss) { Text(strings.dialogCancel) } },
    )
}

/** Where a tap on an available limit reset leads - as on Windows. */
private const val USAGE_PAGE_URL = "https://claude.ai/settings/usage"
