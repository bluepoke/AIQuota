package eu.schicht8.aiquota

import android.content.Context
import androidx.work.Constraints
import androidx.work.CoroutineWorker
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.NetworkType
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import java.time.Duration
import java.util.concurrent.TimeUnit

/**
 * Background updates (#19): a periodic WorkManager job that fetches all accounts while the app
 * is closed - recorded in each account's history and notified about as in the foreground.
 * Android runs it at most every 15 minutes, and Doze may delay it further.
 */
object BackgroundUpdates {
    private const val WORK_NAME = "usage-updates"

    /** Runs the job every [interval], or stops it with null. Keeps the schedule when the
     * interval is unchanged, so this can be called on every start. */
    fun schedule(context: Context, interval: Duration?) {
        val work = WorkManager.getInstance(context)
        if (interval == null) {
            work.cancelUniqueWork(WORK_NAME)
            return
        }
        val request = PeriodicWorkRequestBuilder<UsageUpdateWorker>(interval.toMinutes(), TimeUnit.MINUTES)
            .setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build())
            .build()
        work.enqueueUniquePeriodicWork(WORK_NAME, ExistingPeriodicWorkPolicy.UPDATE, request)
    }
}

/** Fetches every account that is due: in a fresh process all of them (except one still
 * backing off after rate limiting, which the monitor remembers across restarts); in a warm one
 * not those the app has just fetched. */
class UsageUpdateWorker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {
    override suspend fun doWork(): Result {
        (applicationContext as AiQuotaApplication).graph.monitor.fetchDue()
        return Result.success()
    }
}
