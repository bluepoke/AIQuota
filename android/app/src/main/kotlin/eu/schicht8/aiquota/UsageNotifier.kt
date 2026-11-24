package eu.schicht8.aiquota

import android.Manifest
import android.annotation.SuppressLint
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.app.NotificationChannelCompat
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import androidx.core.net.toUri
import eu.schicht8.aiquota.core.AlertSink
import eu.schicht8.aiquota.core.SavedAccount
import eu.schicht8.aiquota.core.Strings
import eu.schicht8.aiquota.core.UsageAlert
import eu.schicht8.aiquota.core.UsageAlertKind
import java.time.Duration
import java.time.Instant

/**
 * Shows the usage notifications (#10, #21) as Android notifications: one channel per
 * [UsageAlertKind], so each kind can be turned off in the system settings. Each account's
 * notification of a kind (and of an additional limit) replaces only its own previous one, and
 * is removed once the window it's about has reset. Tapping one opens that account. The text
 * names the account when several are saved (see [eu.schicht8.aiquota.core.UsageAlerts]).
 */
class UsageNotifier(
    private val context: Context,
    private val strings: () -> Strings,
    private val enabled: () -> Boolean,
) : AlertSink {
    private val manager = NotificationManagerCompat.from(context)

    /** Creates the channels, or renames them after a language change. */
    fun createChannels(strings: Strings) {
        manager.createNotificationChannelsCompat(
            UsageAlertKind.entries.map { kind ->
                NotificationChannelCompat.Builder(channelId(kind), NotificationManagerCompat.IMPORTANCE_DEFAULT)
                    .setName(strings.alertChannelName(kind))
                    .build()
            },
        )
    }

    /** Whether notifications can be shown at all (the permission, and not blocked for the app). */
    fun canNotify(): Boolean {
        val granted = Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU ||
            ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED
        return granted && manager.areNotificationsEnabled()
    }

    @SuppressLint("MissingPermission") // checked in canNotify()
    override fun show(account: SavedAccount, alerts: List<UsageAlert>) {
        if (!enabled() || !canNotify()) return
        val texts = strings()
        val now = Instant.now()
        for (alert in alerts) {
            val tag = tagFor(account.key, alert)
            val builder = NotificationCompat.Builder(context, channelId(alert.kind))
                .setSmallIcon(R.drawable.ic_notification)
                .setContentText(alert.text)
                .setStyle(NotificationCompat.BigTextStyle().bigText(alert.text))
                .setCategory(NotificationCompat.CATEGORY_STATUS)
                .setContentIntent(openAccount(account.key, tag))
                .setAutoCancel(true)
            alert.expiresAt?.let { Duration.between(now, it).toMillis() }?.takeIf { it > 0 }?.let(builder::setTimeoutAfter)
            builder.addAction(0, texts.openUsagePage, openUsagePage(tag))
            manager.notify(tag, NOTIFICATION_ID, builder.build())
        }
    }

    /** Removes an account's notifications - when it's removed from the list. */
    fun cancelAccount(accountKey: String) {
        val system = context.getSystemService(NotificationManager::class.java) ?: return
        system.activeNotifications
            .filter { it.tag?.startsWith("$accountKey|") == true }
            .forEach { manager.cancel(it.tag, it.id) }
    }

    private fun openAccount(accountKey: String, tag: String): PendingIntent {
        val intent = Intent(context, MainActivity::class.java)
            .putExtra(MainActivity.EXTRA_ACCOUNT_KEY, accountKey)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP)
        return PendingIntent.getActivity(context, tag.hashCode(), intent, PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
    }

    private fun openUsagePage(tag: String): PendingIntent {
        val intent = Intent(Intent.ACTION_VIEW, USAGE_PAGE_URL.toUri()).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        return PendingIntent.getActivity(context, tag.hashCode() + 1, intent, PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
    }

    companion object {
        /** Where "Open usage page" and a tap on an available limit reset lead - as on Windows. */
        const val USAGE_PAGE_URL = "https://claude.ai/settings/usage"

        /** Notifications are told apart by their tag; the ID is the same for all. */
        private const val NOTIFICATION_ID = 1

        fun channelId(kind: UsageAlertKind) = "usage-" + kind.name.lowercase()

        private fun tagFor(accountKey: String, alert: UsageAlert) = listOfNotNull(accountKey, alert.kind.name, alert.limitKey).joinToString("|")
    }
}
