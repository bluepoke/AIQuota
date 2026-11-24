package eu.schicht8.aiquota.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.selectableGroup
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LifecycleEventEffect
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import eu.schicht8.aiquota.core.AppSettings
import eu.schicht8.aiquota.core.LanguageSetting
import eu.schicht8.aiquota.core.ThemeSetting
import java.time.Duration

/** Language, theme, accessibility mode, pace markers, update intervals and notifications. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SettingsScreen(app: AppContext, canNotify: () -> Boolean, openNotificationSettings: () -> Unit) {
    val viewModel = app.viewModel
    val strings = app.strings
    val settings by viewModel.settings.collectAsStateWithLifecycle()
    // Re-checked on returning from the system settings.
    var notificationsAllowed by remember { mutableStateOf(canNotify()) }
    LifecycleEventEffect(Lifecycle.Event.ON_RESUME) { notificationsAllowed = canNotify() }
    var picker by rememberSaveable { mutableStateOf<Picker?>(null) }
    fun update(change: (AppSettings) -> AppSettings) = viewModel.updateSettings(change)
    val languages = listOf(LanguageSetting.System to strings.followSystem, LanguageSetting.German to strings.languageGerman, LanguageSetting.English to strings.languageEnglish)
    val themes = listOf(ThemeSetting.System to strings.followSystem, ThemeSetting.Light to strings.themeLight, ThemeSetting.Dark to strings.themeDark)
    val polls = AppSettings.POLL_INTERVALS.map { it to strings.everyInterval(it) }
    val backgrounds = listOf<Pair<Duration?, String>>(null to strings.backgroundOff) + AppSettings.BACKGROUND_INTERVALS.map { it to strings.everyInterval(it) }

    Scaffold(
        topBar = {
            TopAppBar(title = { Text(strings.settingsTitle) }, navigationIcon = { BackButton(strings) { viewModel.back() } })
        },
    ) { padding ->
        Column(
            Modifier.fillMaxSize().padding(padding).verticalScroll(rememberScrollState()),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Column(Modifier.widthIn(max = 640.dp).fillMaxWidth()) {
                Heading(strings.settingsDisplay)
                ChoiceRow(strings.settingsLanguage, languages.first { it.first == settings.language }.second) { picker = Picker.Language }
                ChoiceRow(strings.settingsTheme, themes.first { it.first == settings.theme }.second) { picker = Picker.Theme }
                SwitchRow(strings.accessibilityMode, null, settings.accessibilityMode) { on -> update { it.copy(accessibilityMode = on) } }
                SwitchRow(strings.showPaceMarkers, null, settings.showPaceMarker) { on -> update { it.copy(showPaceMarker = on) } }

                HorizontalDivider(Modifier.padding(vertical = 8.dp))
                Heading(strings.settingsUpdates)
                ChoiceRow(strings.pollInterval, strings.everyInterval(settings.pollInterval)) { picker = Picker.Poll }
                ChoiceRow(strings.backgroundUpdates, backgrounds.first { it.first == settings.backgroundInterval }.second, strings.backgroundNote) {
                    picker = Picker.Background
                }

                HorizontalDivider(Modifier.padding(vertical = 8.dp))
                Heading(strings.settingsNotifications)
                SwitchRow(strings.settingsNotifications, strings.notificationsEnabled, settings.notifications) { on -> update { it.copy(notifications = on) } }
                if (settings.notifications && !notificationsAllowed) {
                    Text(
                        strings.notificationsBlocked,
                        Modifier.padding(horizontal = 16.dp, vertical = 4.dp),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.error,
                    )
                }
                ChoiceRow(strings.notificationChannels, null, onClick = openNotificationSettings)

                HorizontalDivider(Modifier.padding(vertical = 8.dp))
                Text(
                    strings.versionLabel(appVersion()),
                    Modifier.padding(16.dp),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }

    val close = { picker = null }
    when (picker) {
        Picker.Language -> PickerDialog(strings.settingsLanguage, strings.dialogCancel, languages, settings.language, close) { choice ->
            update { it.copy(language = choice) }
        }
        Picker.Theme -> PickerDialog(strings.settingsTheme, strings.dialogCancel, themes, settings.theme, close) { choice ->
            update { it.copy(theme = choice) }
        }
        Picker.Poll -> PickerDialog(strings.pollInterval, strings.dialogCancel, polls, settings.pollInterval, close) { choice ->
            update { it.copy(pollInterval = choice) }
        }
        Picker.Background -> PickerDialog(strings.backgroundUpdates, strings.dialogCancel, backgrounds, settings.backgroundInterval, close) { choice ->
            update { it.copy(backgroundInterval = choice) }
        }
        null -> Unit
    }
}

/** Which setting's choices are open. */
private enum class Picker { Language, Theme, Poll, Background }

@Composable
private fun Heading(text: String) {
    Text(text, Modifier.padding(start = 16.dp, end = 16.dp, top = 16.dp, bottom = 4.dp), style = MaterialTheme.typography.titleSmall, color = MaterialTheme.colorScheme.primary)
}

/** A setting with its current value below; a tap opens its choices. */
@Composable
private fun ChoiceRow(title: String, value: String?, note: String? = null, onClick: () -> Unit) {
    Column(Modifier.fillMaxWidth().clickable(onClick = onClick).padding(horizontal = 16.dp, vertical = 12.dp)) {
        Text(title, style = MaterialTheme.typography.bodyLarge)
        value?.let { Text(it, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant) }
        note?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
    }
}

@Composable
private fun SwitchRow(title: String, description: String?, checked: Boolean, onChange: (Boolean) -> Unit) {
    Row(
        Modifier.fillMaxWidth().toggleable(checked, role = Role.Switch, onValueChange = onChange).padding(horizontal = 16.dp, vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(16.dp),
    ) {
        Column(Modifier.weight(1f)) {
            Text(title, style = MaterialTheme.typography.bodyLarge)
            description?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
        }
        Switch(checked = checked, onCheckedChange = null)
    }
}

@Composable
private fun <T> PickerDialog(title: String, cancel: String, options: List<Pair<T, String>>, current: T, onDismiss: () -> Unit, onPick: (T) -> Unit) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(title) },
        text = {
            Column(Modifier.selectableGroup()) {
                options.forEach { (value, label) ->
                    Row(
                        Modifier.fillMaxWidth()
                            .selectable(value == current, role = Role.RadioButton) {
                                onPick(value)
                                onDismiss()
                            }
                            .padding(vertical = 10.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        RadioButton(selected = value == current, onClick = null)
                        Text(label, Modifier.padding(start = 12.dp), style = MaterialTheme.typography.bodyLarge)
                    }
                }
            }
        },
        confirmButton = { TextButton(onClick = onDismiss) { Text(cancel) } },
    )
}

@Composable
private fun appVersion(): String {
    val context = LocalContext.current
    return remember { context.packageManager.getPackageInfo(context.packageName, 0).versionName ?: "?" }
}
