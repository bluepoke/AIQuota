package eu.schicht8.aiquota.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.produceState
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import eu.schicht8.aiquota.Screen
import eu.schicht8.aiquota.core.UsageHistoryRecord

/**
 * One account in full (#19): the flyout content of its card plus the 7-day chart against the
 * even pace, and the way to its yearly heatmap.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun DetailsScreen(app: AppContext, accountKey: String) {
    val viewModel = app.viewModel
    val strings = app.strings
    val accounts by viewModel.accounts.collectAsStateWithLifecycle()
    val settings by viewModel.settings.collectAsStateWithLifecycle()
    val login by viewModel.login.collectAsStateWithLifecycle()
    val isRefreshing by viewModel.isRefreshing.collectAsStateWithLifecycle()
    val state = accounts.firstOrNull { it.account.key == accountKey }
    if (state == null) {
        // Removed (or merged into another) meanwhile.
        LaunchedEffect(Unit) { viewModel.back() }
        return
    }
    val history by produceState(emptyList<UsageHistoryRecord>(), accountKey, state.snapshot?.fetchedAt) {
        value = viewModel.history(accountKey)
    }
    val card = LocalAiQuotaColors.current.card

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(strings.accountName(state.account), maxLines = 1, overflow = TextOverflow.Ellipsis) },
                navigationIcon = { BackButton(strings) { viewModel.back() } },
            )
        },
    ) { padding ->
        PullToRefreshBox(isRefreshing = isRefreshing, onRefresh = viewModel::refresh, modifier = Modifier.fillMaxSize().padding(padding)) {
            Column(
                Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                val width = Modifier.fillMaxWidth().widthIn(max = 640.dp)
                Text(statusText(state, strings), width, style = MaterialTheme.typography.bodySmall, color = Color(card.secondary))
                UsageCard(width) {
                    Column(Modifier.padding(top = 8.dp)) {
                        AccountUsage(state, app, settings.showPaceMarker, loginEnabled = login == null)
                    }
                }
                val snapshot = state.snapshot
                if (state.hasToken && snapshot != null) {
                    UsageCard(width) {
                        Column(Modifier.padding(top = 4.dp, end = 12.dp)) {
                            Text(strings.historyTitle, style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.SemiBold)
                            WeekChart(history, snapshot, app.now, strings, Modifier.padding(top = 4.dp))
                        }
                    }
                }
                OutlinedButton(onClick = { viewModel.open(Screen.Heatmap(accountKey)) }) { Text(strings.openHeatmap) }
            }
        }
    }
}
