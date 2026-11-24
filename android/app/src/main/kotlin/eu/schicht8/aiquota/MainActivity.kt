package eu.schicht8.aiquota

import android.Manifest
import android.content.ActivityNotFoundException
import android.content.Intent
import android.graphics.Color
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.browser.customtabs.CustomTabsIntent
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.core.net.toUri
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.lifecycleScope
import androidx.lifecycle.repeatOnLifecycle
import eu.schicht8.aiquota.core.ThemeSetting
import eu.schicht8.aiquota.ui.AiQuotaApp
import eu.schicht8.aiquota.ui.AiQuotaTheme
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

class MainActivity : ComponentActivity() {
    private val viewModel: AppViewModel by viewModels()
    private val notifier get() = (application as AiQuotaApplication).graph.notifier
    private val requestNotifications = registerForActivityResult(ActivityResultContracts.RequestPermission()) { }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        if (savedInstanceState == null) openAccountFrom(intent)
        // The channel names follow a change of the system language (which recreates this
        // activity), not only of the app's own setting.
        notifier.createChannels((application as AiQuotaApplication).graph.strings())
        setContent {
            val settings by viewModel.settings.collectAsStateWithLifecycle()
            val dark = when (settings.theme) {
                ThemeSetting.System -> isSystemInDarkTheme()
                ThemeSetting.Light -> false
                ThemeSetting.Dark -> true
            }
            // Status and navigation bar icons that match the chosen theme, not the system's.
            LaunchedEffect(dark) {
                val style = SystemBarStyle.auto(Color.TRANSPARENT, Color.TRANSPARENT) { dark }
                enableEdgeToEdge(statusBarStyle = style, navigationBarStyle = style)
            }
            AiQuotaTheme(dark, settings.accessibilityMode) {
                AiQuotaApp(
                    viewModel,
                    openUrl = ::openInCustomTab,
                    canNotify = notifier::canNotify,
                    openNotificationSettings = ::openNotificationSettings,
                )
            }
        }

        lifecycleScope.launch {
            // While in front: the accounts that are due, checked every 15 seconds - each
            // account itself is fetched every poll interval (or later after rate limiting).
            repeatOnLifecycle(Lifecycle.State.STARTED) {
                while (isActive) {
                    viewModel.fetchDue()
                    delay(FETCH_TICK_MILLIS)
                }
            }
        }
        lifecycleScope.launch {
            repeatOnLifecycle(Lifecycle.State.STARTED) {
                viewModel.openUrls.collect(::openInCustomTab)
            }
        }
        lifecycleScope.launch {
            // Android 13+ asks for the permission - once there is an account to notify about.
            repeatOnLifecycle(Lifecycle.State.STARTED) {
                combine(viewModel.accounts, viewModel.settings) { accounts, settings -> accounts.isNotEmpty() && settings.notifications }
                    .collect { wanted -> if (wanted) askForNotificationPermission() }
            }
        }
    }

    /** A notification's tap lands here (singleTask); so does the "Back to AI Quota" link of the
     * login page (aiquota://login-complete), which has already closed the Custom Tab. */
    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        openAccountFrom(intent)
    }

    private fun openAccountFrom(intent: Intent) {
        intent.getStringExtra(EXTRA_ACCOUNT_KEY)?.let(viewModel::openAccount)
        intent.removeExtra(EXTRA_ACCOUNT_KEY)
    }

    private fun askForNotificationPermission() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU || viewModel.notificationPermissionAsked || notifier.canNotify()) return
        viewModel.notificationPermissionAsked = true
        requestNotifications.launch(Manifest.permission.POST_NOTIFICATIONS)
    }

    private fun openNotificationSettings() {
        startActivity(Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS).putExtra(Settings.EXTRA_APP_PACKAGE, packageName))
    }

    private fun openInCustomTab(url: String) {
        try {
            CustomTabsIntent.Builder().setShowTitle(true).build().launchUrl(this, url.toUri())
        } catch (_: ActivityNotFoundException) {
            Toast.makeText(this, url, Toast.LENGTH_LONG).show()
        }
    }

    companion object {
        /** On a notification's intent: the account to open. */
        const val EXTRA_ACCOUNT_KEY = "eu.schicht8.aiquota.ACCOUNT_KEY"
        private const val FETCH_TICK_MILLIS = 15_000L
    }
}
