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

/** What a card's menu can do; a null move isn't possible (first or last in the list). */
class AccountCardActions(
    val open: () -> Unit,
    val rename: () -> Unit,
    val moveUp: (() -> Unit)?,
    val moveDown: (() -> Unit)?,
    val remove: () -> Unit,
)

/**
 * One account on the overview: its name and status, then - like the Windows flyout - the
 * session, week, credit and additional limit bars with percentages, reset times, pace
 * marker, forecast and available limit resets. Without a token it shows "login required"
 * with a button to log in again, and no values. A tap opens the account's details.
 */
@Composable
fun AccountCard(
    state: AccountState,
    app: AppContext,
    showPaceMarker: Boolean,
    loginEnabled: Boolean,
    actions: AccountCardActions,
    modifier: Modifier = Modifier,
) {
    UsageCard(modifier, onClick = actions.open) {
        Header(state, app, loginEnabled, actions)
        AccountUsage(state, app, showPaceMarker, loginEnabled)
    }
}

/** A card in the flyout's colours. */
@Composable
fun UsageCard(modifier: Modifier = Modifier, onClick: (() -> Unit)? = null, content: @Composable () -> Unit) {
    val card = LocalAiQuotaColors.current.card
    val shape = RoundedCornerShape(8.dp)
    val border = BorderStroke(1.dp, Color(card.border))
    val colors = CardDefaults.cardColors(containerColor = Color(card.background), contentColor = Color(card.text))
    val inner: @Composable () -> Unit = {
        Column(Modifier.padding(start = 16.dp, end = 4.dp, top = 8.dp, bottom = 16.dp)) { content() }
    }
    if (onClick != null) {
        Card(onClick = onClick, modifier = modifier.fillMaxWidth(), shape = shape, border = border, colors = colors) { inner() }
    } else {
        Card(modifier = modifier.fillMaxWidth(), shape = shape, border = border, colors = colors) { inner() }
    }
}

/** The account's bars - or, without a token, the button to log in again. */
@Composable
fun AccountUsage(state: AccountState, app: AppContext, showPaceMarker: Boolean, loginEnabled: Boolean) {
    Column(Modifier.padding(end = 12.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
        if (!state.hasToken) {
            Button(onClick = { app.startLogin(state) }, enabled = loginEnabled) { Text(app.strings.logInAgain) }
        } else {
            val snapshot = state.snapshot
            if (snapshot != null) {
                UsageSections.build(snapshot, state.sessionForecast, state.weeklyForecast, showPaceMarker, app.now, app.strings)
                    .forEach { Section(it, app::openUsagePage) }
            }
        }
    }
}

@Composable
private fun Header(state: AccountState, app: AppContext, loginEnabled: Boolean, actions: AccountCardActions) {
    val strings = app.strings
    val card = LocalAiQuotaColors.current.card
    Row(verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f).padding(vertical = 4.dp)) {
            Text(
                strings.accountName(state.account),
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
                MenuItem(strings.logInAgain, enabled = loginEnabled) {
                    menuOpen = false
                    app.startLogin(state)
                }
                MenuItem(strings.rename) {
                    menuOpen = false
                    actions.rename()
                }
                actions.moveUp?.let { moveUp ->
                    MenuItem(strings.moveUp) {
                        menuOpen = false
                        moveUp()
                    }
                }
                actions.moveDown?.let { moveDown ->
                    MenuItem(strings.moveDown) {
                        menuOpen = false
                        moveDown()
                    }
                }
                MenuItem(strings.remove) {
                    menuOpen = false
                    actions.remove()
                }
            }
        }
    }
}

@Composable
private fun MenuItem(text: String, enabled: Boolean = true, onClick: () -> Unit) {
    DropdownMenuItem(text = { Text(text) }, enabled = enabled, onClick = onClick)
}

/** "Login required", "Refreshing…", the fetch error, or when the values are from. */
fun statusText(state: AccountState, strings: Strings): String {
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
