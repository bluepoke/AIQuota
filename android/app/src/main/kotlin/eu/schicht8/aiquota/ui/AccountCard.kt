package eu.schicht8.aiquota.ui

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import eu.schicht8.aiquota.R
import eu.schicht8.aiquota.core.AccountState
import eu.schicht8.aiquota.core.Strings
import eu.schicht8.aiquota.core.UsageFetchStatus
import eu.schicht8.aiquota.core.UsageSection
import eu.schicht8.aiquota.core.UsageSections
import java.time.Instant

/**
 * One account on the overview: its name and status, then - like the Windows flyout - the
 * session, week, credit and additional limit bars with percentages, reset times, pace
 * marker, forecast and available limit resets. Without a token it shows "login required"
 * with a button to log in again, and no values.
 */
@Composable
fun AccountCard(
    state: AccountState,
    now: Instant,
    strings: Strings,
    loginEnabled: Boolean,
    onLogin: () -> Unit,
    onRename: () -> Unit,
    onRemove: () -> Unit,
    onOpenUsagePage: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val colors = LocalAiQuotaColors.current
    val card = colors.card
    Card(
        modifier = modifier.fillMaxWidth(),
        shape = RoundedCornerShape(8.dp),
        border = BorderStroke(1.dp, Color(card.border)),
        colors = CardDefaults.cardColors(containerColor = Color(card.background), contentColor = Color(card.text)),
    ) {
        Column(Modifier.padding(start = 16.dp, end = 4.dp, top = 8.dp, bottom = 16.dp)) {
            Header(state, strings, loginEnabled, onLogin, onRename, onRemove)
            Column(Modifier.padding(end = 12.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
                if (!state.hasToken) {
                    Button(onClick = onLogin, enabled = loginEnabled) { Text(strings.logInAgain) }
                } else {
                    val snapshot = state.snapshot
                    if (snapshot != null) {
                        UsageSections.build(snapshot, state.sessionForecast, state.weeklyForecast, showPaceMarker = true, now, strings)
                            .forEach { Section(it, onOpenUsagePage) }
                    }
                }
            }
        }
    }
}

@Composable
private fun Header(
    state: AccountState,
    strings: Strings,
    loginEnabled: Boolean,
    onLogin: () -> Unit,
    onRename: () -> Unit,
    onRemove: () -> Unit,
) {
    val card = LocalAiQuotaColors.current.card
    Row(verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f).padding(vertical = 4.dp)) {
            Text(
                state.account.displayName ?: strings.accountUnnamed,
                style = MaterialTheme.typography.titleMedium,
                fontWeight = FontWeight.SemiBold,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            Text(statusText(state, strings), style = MaterialTheme.typography.bodySmall, color = Color(card.secondary))
        }
        var menuOpen by remember { mutableStateOf(false) }
        Box {
            IconButton(onClick = { menuOpen = true }) {
                Icon(painterResource(R.drawable.ic_more_vert), contentDescription = strings.moreOptions, tint = Color(card.secondary))
            }
            DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) {
                DropdownMenuItem(text = { Text(strings.logInAgain) }, enabled = loginEnabled, onClick = {
                    menuOpen = false
                    onLogin()
                })
                DropdownMenuItem(text = { Text(strings.rename) }, onClick = {
                    menuOpen = false
                    onRename()
                })
                DropdownMenuItem(text = { Text(strings.remove) }, onClick = {
                    menuOpen = false
                    onRemove()
                })
            }
        }
    }
}

/** "Login required", "Refreshing…", the fetch error, or when the values are from. */
private fun statusText(state: AccountState, strings: Strings): String {
    val snapshot = state.snapshot
    val error = state.error
    return when {
        !state.hasToken -> strings.loginRequired
        state.isFetching -> strings.refreshing
        state.status == UsageFetchStatus.NetworkError && error != null -> strings.fetchFailed(error)
        snapshot != null -> strings.statusUpdated(snapshot.fetchedAt)
        else -> strings.refreshing
    }
}

@Composable
private fun Section(section: UsageSection, onOpenUsagePage: () -> Unit) {
    val card = LocalAiQuotaColors.current.card
    Column {
        Row(
            Modifier.fillMaxWidth().semantics(mergeDescendants = true) { contentDescription = "${section.label}: ${section.percent}%" },
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(section.label, Modifier.weight(1f), style = MaterialTheme.typography.bodyMedium, fontWeight = FontWeight.Medium)
            Text("${section.percent}%", style = MaterialTheme.typography.bodyMedium, fontWeight = FontWeight.Medium)
        }
        UsageBar(section)
        section.details.forEach { line ->
            Text(
                line.text,
                modifier = if (line.opensUsagePage) Modifier.clickable(onClick = onOpenUsagePage).padding(vertical = 2.dp) else Modifier,
                style = MaterialTheme.typography.bodySmall,
                color = Color(if (line.emphasized) card.text else card.secondary),
                textDecoration = if (line.opensUsagePage) TextDecoration.Underline else null,
            )
        }
    }
}
