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
import androidx.compose.foundation.lazy.itemsIndexed
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
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import eu.schicht8.aiquota.R
import eu.schicht8.aiquota.Screen
import eu.schicht8.aiquota.UsageNotifier
import eu.schicht8.aiquota.core.AccountState
import eu.schicht8.aiquota.core.Strings

/** What a dialog on the overview is about. */
private sealed interface OverviewDialog {
    data class Rename(val account: AccountState) : OverviewDialog
    data class Remove(val account: AccountState) : OverviewDialog
}

/**
 * The start screen (#19): one card per saved account, in the account list's order, each with
 * its own values, errors and "login required" state. Pull to refresh fetches all of them; a
 * tap on a card opens the account's details.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun OverviewScreen(app: AppContext) {
    val viewModel = app.viewModel
    val strings = app.strings
    val accounts by viewModel.accounts.collectAsStateWithLifecycle()
    val isRefreshing by viewModel.isRefreshing.collectAsStateWithLifecycle()
    val login by viewModel.login.collectAsStateWithLifecycle()
    val settings by viewModel.settings.collectAsStateWithLifecycle()
    val snackbar = remember { SnackbarHostState() }
    LaunchedEffect(viewModel) { viewModel.messages.collect { snackbar.showSnackbar(it) } }

    var dialog by remember { mutableStateOf<OverviewDialog?>(null) }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(strings.appTitle) },
                actions = {
                    IconButton(onClick = { app.startLogin(null) }, enabled = login == null) {
                        Icon(painterResource(R.drawable.ic_add), contentDescription = strings.addAccount)
                    }
                    IconButton(onClick = { viewModel.open(Screen.Settings) }) {
                        Icon(painterResource(R.drawable.ic_settings), contentDescription = strings.settingsTitle)
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
                    item(key = "empty") { EmptyState(strings) { app.startLogin(null) } }
                }
                itemsIndexed(accounts, key = { _, state -> state.account.key }) { index, state ->
                    AccountCard(
                        state = state,
                        app = app,
                        showPaceMarker = settings.showPaceMarker,
                        loginEnabled = login == null,
                        actions = AccountCardActions(
                            open = { viewModel.open(Screen.Details(state.account.key)) },
                            rename = { dialog = OverviewDialog.Rename(state) },
                            moveUp = if (index > 0) ({ viewModel.move(state.account.key, -1) }) else null,
                            moveDown = if (index < accounts.lastIndex) ({ viewModel.move(state.account.key, 1) }) else null,
                            remove = { dialog = OverviewDialog.Remove(state) },
                        ),
                        modifier = Modifier.widthIn(max = 640.dp).animateItem(),
                    )
                }
            }
        }
    }

    when (val current = dialog) {
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
            text = { Text(strings.confirmRemoveAccount(strings.accountName(current.account.account))) },
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

/** Opens the usage page - from an available limit reset. */
fun AppContext.openUsagePage() = openUrl(UsageNotifier.USAGE_PAGE_URL)
