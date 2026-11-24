package eu.schicht8.aiquota.core

import kotlinx.serialization.SerializationException
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import java.io.File
import java.io.IOException
import java.math.BigDecimal
import java.security.MessageDigest
import java.time.Duration
import java.time.Instant

/** One successful usage fetch, as stored in the local history. Percentages are the API's raw
 * 0-100 values, so burn rates computed from them aren't skewed by rounding. */
data class UsageHistoryRecord(
    val time: Instant,
    val session: Double,
    val sessionResetsAt: Instant?,
    val week: Double,
    val weekResetsAt: Instant?,
    val creditUsed: BigDecimal? = null,
    val creditLimit: BigDecimal? = null,
    val creditCurrency: String? = null,
) {
    /** This record as a snapshot - an account's last known values before it's fetched. A
     * window whose reset has passed by [now] counts as empty again. */
    fun toSnapshot(now: Instant): UsageSnapshot {
        val sessionOver = sessionResetsAt != null && sessionResetsAt <= now
        val weekOver = weekResetsAt != null && weekResetsAt <= now
        val session = if (sessionOver) 0.0 else this.session.coerceIn(0.0, 100.0)
        val week = if (weekOver) 0.0 else this.week.coerceIn(0.0, 100.0)
        return UsageSnapshot(
            session.roundHalfEven(), week.roundHalfEven(),
            if (sessionOver) null else sessionResetsAt, if (weekOver) null else weekResetsAt,
            time, creditUsed, creditLimit, creditCurrency, session, week,
        )
    }

    companion object {
        fun fromSnapshot(snapshot: UsageSnapshot) = UsageHistoryRecord(
            snapshot.fetchedAt,
            snapshot.sessionUtilization ?: snapshot.sessionPercent.toDouble(),
            snapshot.sessionResetsAt,
            snapshot.weeklyUtilization ?: snapshot.weeklyPercent.toDouble(),
            snapshot.weeklyResetsAt,
            snapshot.creditUsed,
            snapshot.creditLimit,
            snapshot.creditCurrency,
        )
    }
}

/**
 * The usage history of one account, stored as JSON Lines - one [UsageHistoryRecord] per line,
 * in the Windows app's format (AIQuota/History/JsonlUsageHistory.cs), named by a hash of the
 * account ID so the email never shows up in a file name. Nothing in it leaves the device.
 *
 * A record is appended per fetch; the whole file is read when it's opened and records older
 * than [RETENTION] are dropped. Storage problems never break the app: write failures are
 * ignored, unreadable lines skipped (and the file rewritten clean).
 *
 * Only the raw history so far - the daily values for the yearly heatmap follow with the
 * heatmap, and are backfilled from this file then, as on Windows.
 */
class JsonlUsageHistory(directory: File, accountId: String, private val clock: () -> Instant = Instant::now) {
    private val file = File(directory, fileNameFor(accountId) + ".jsonl")
    private val records = mutableListOf<UsageHistoryRecord>()
    private var lastTrim: Instant = Instant.MIN

    init {
        load()
    }

    /** All records, oldest first. */
    val all: List<UsageHistoryRecord> @Synchronized get() = records.toList()

    @Synchronized
    fun append(record: UsageHistoryRecord) {
        records += record
        try {
            file.parentFile?.mkdirs()
            file.appendText(serialize(record) + "\n")
        } catch (_: IOException) {
            // History is a nice-to-have; the next append tries again.
        }
        if (Duration.between(lastTrim, clock()) >= TRIM_INTERVAL) trim(rewriteAnyway = false)
    }

    private fun load() {
        if (!file.exists()) {
            lastTrim = clock()
            return
        }
        var needsRewrite: Boolean
        try {
            val content = file.readText()
            needsRewrite = content.isNotEmpty() && !content.endsWith("\n")
            for (line in content.split('\n')) {
                if (line.isBlank()) continue
                val record = parse(line)
                if (record != null) records += record else needsRewrite = true
            }
        } catch (_: IOException) {
            return
        }
        records.sortBy { it.time }
        trim(needsRewrite)
    }

    private fun trim(rewriteAnyway: Boolean) {
        lastTrim = clock()
        val cutoff = lastTrim.minus(RETENTION)
        val removed = records.removeAll { it.time < cutoff }
        if (removed || rewriteAnyway) rewrite()
    }

    /** Replaces the file with the in-memory records (see [writeAtomically]). */
    private fun rewrite() {
        try {
            writeAtomically(file, records.joinToString("") { serialize(it) + "\n" }.toByteArray(Charsets.UTF_8))
        } catch (_: IOException) {
            // Kept in memory; the file is rewritten on the next trim.
        }
    }

    companion object {
        /** Covers the previous weekly window plus some margin. */
        val RETENTION: Duration = Duration.ofDays(35)
        private val TRIM_INTERVAL: Duration = Duration.ofDays(1)

        fun fileNameFor(accountId: String): String =
            MessageDigest.getInstance("SHA-256").digest(accountId.toByteArray(Charsets.UTF_8))
                .take(8).joinToString("") { "%02x".format(it) }

        private fun serialize(record: UsageHistoryRecord): String = buildJsonObject {
            put("time", record.time.toString())
            put("session", record.session)
            record.sessionResetsAt?.let { put("sessionResetsAt", it.toString()) }
            put("week", record.week)
            record.weekResetsAt?.let { put("weekResetsAt", it.toString()) }
            record.creditUsed?.let { put("creditUsed", it) }
            record.creditLimit?.let { put("creditLimit", it) }
            record.creditCurrency?.let { put("creditCurrency", it) }
        }.toString()

        /** A line without a readable timestamp isn't usable for anything. */
        private fun parse(line: String): UsageHistoryRecord? = try {
            val json = Json.parseToJsonElement(line) as? JsonObject
            val time = json?.instant("time")
            if (json == null || time == null) null
            else UsageHistoryRecord(
                time,
                json.number("session") ?: 0.0,
                json.instant("sessionResetsAt"),
                json.number("week") ?: 0.0,
                json.instant("weekResetsAt"),
                json.decimal("creditUsed"),
                json.decimal("creditLimit"),
                json.string("creditCurrency"),
            )
        } catch (_: SerializationException) {
            null
        }

        private fun JsonObject.decimal(name: String): BigDecimal? =
            (this[name] as? JsonPrimitive)?.takeIf { !it.isString }?.content?.toBigDecimalOrNull()
    }
}
