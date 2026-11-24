package eu.schicht8.aiquota

import android.content.ActivityNotFoundException
import android.content.Intent
import android.os.Bundle
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.viewModels
import androidx.browser.customtabs.CustomTabsIntent
import androidx.core.net.toUri
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.lifecycleScope
import androidx.lifecycle.repeatOnLifecycle
import eu.schicht8.aiquota.ui.AiQuotaTheme
import eu.schicht8.aiquota.ui.OverviewScreen
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

class MainActivity : ComponentActivity() {
    private val viewModel: OverviewViewModel by viewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent {
            AiQuotaTheme {
                OverviewScreen(viewModel, openUrl = ::openInCustomTab)
            }
        }

        lifecycleScope.launch {
            // While in front: the accounts that are due, checked every 15 seconds - each
            // account itself is fetched every 5 minutes (or later after rate limiting).
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
    }

    /** The "Back to AI Quota" link of the login page (aiquota://login-complete) lands here;
     * singleTask has already closed the Custom Tab above this activity. */
    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
    }

    private fun openInCustomTab(url: String) {
        try {
            CustomTabsIntent.Builder().setShowTitle(true).build().launchUrl(this, url.toUri())
        } catch (_: ActivityNotFoundException) {
            Toast.makeText(this, url, Toast.LENGTH_LONG).show()
        }
    }

    private companion object {
        const val FETCH_TICK_MILLIS = 15_000L
    }
}
