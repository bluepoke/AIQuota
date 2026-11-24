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
import java.time.LocalDate
import java.time.ZoneId

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
 * than [RETENTION] are dropped. Next to it, `<hash>.daily.jsonl` keeps one [DailyUsageRecord]
 * per local day in [zone] for the yearly heatmap, for [DAILY_RETENTION]: every append adds the
 * updated day as a new line (the last line of a date wins), and the file is compacted to one
 * line per day when it's loaded and once a day. Without a daily file it's backfilled from the
 * raw history.
 *
 * Storage problems never break the app: write failures are ignored, unreadable lines skipped
 * (and the file rewritten clean).
 */
class JsonlUsageHistory(
    directory: File,
    accountId: String,
    private val clock: () -> Instant = Instant::now,
    private val zone: ZoneId = ZoneId.systemDefault(),
) {
    private val file = File(directory, fileNameFor(accountId) + ".jsonl")
    private val dailyFile = File(directory, fileNameFor(accountId) + ".daily.jsonl")
    private val records = mutableListOf<UsageHistoryRecord>()
    private val days = mutableListOf<DailyUsageRecord>()

    /** Lines were appended to the daily file since it was last compacted. */
    private var dailyHasExtraLines = false
    private var lastTrim: Instant = Instant.MIN

    init {
        load()
        loadDaily()
    }

    /** All records, oldest first. */
    val all: List<UsageHistoryRecord> @Synchronized get() = records.toList()

    /** The per-day values for the heatmap, oldest first. */
    val daily: List<DailyUsageRecord> @Synchronized get() = days.toList()

    @Synchronized
    fun append(record: UsageHistoryRecord) {
        val previous = records.lastOrNull()
        records += record
        try {
            file.parentFile?.mkdirs()
            file.appendText(serialize(record) + "\n")
        } catch (_: IOException) {
            // History is a nice-to-have; the next append tries again.
        }
        appendDaily(previous, record)
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
        if (removed || rewriteAnyway) rewrite(file, records.map(::serialize))
        trimDaily(rewriteAnyway = false)
    }

    // ------------------------------------------------------------------ daily values

    /** Adds [record] to its day and appends that day's new state to the daily file. A sample
     * older than the newest one so far is left out there (the raw history keeps it). */
    private fun appendDaily(previous: UsageHistoryRecord?, record: UsageHistoryRecord) {
        if (previous != null && record.time < previous.time) return
        val date = DailyUsageAggregator.dayOf(record.time, zone)
        val index = days.indexOfLast { it.date == date }
        val day = DailyUsageAggregator.add(days.getOrNull(index), previous, record, zone)
        if (index >= 0) {
            days[index] = day
        } else {
            days += day
            days.sortBy { it.date }
        }
        try {
            dailyFile.parentFile?.mkdirs()
            dailyFile.appendText(serializeDaily(day) + "\n")
            dailyHasExtraLines = dailyHasExtraLines || index >= 0
        } catch (_: IOException) {
            // Kept in memory; written with the next compaction.
        }
    }

    /** Reads the daily file - the last line of each date wins - or, if there is none yet,
     * backfills it from the raw history. */
    private fun loadDaily() {
        if (!dailyFile.exists()) {
            if (records.isNotEmpty()) {
                days += DailyUsageAggregator.build(records, zone)
                trimDaily(rewriteAnyway = true)
            }
            return
        }
        var needsRewrite: Boolean
        try {
            val content = dailyFile.readText()
            needsRewrite = content.isNotEmpty() && !content.endsWith("\n")
            val byDate = mutableMapOf<LocalDate, DailyUsageRecord>()
            for (line in content.split('\n')) {
                if (line.isBlank()) continue
                val day = parseDaily(line)
                if (day != null) {
                    needsRewrite = needsRewrite || day.date in byDate
                    byDate[day.date] = day
                } else {
                    needsRewrite = true
                }
            }
            days += byDate.values.sortedBy { it.date }
        } catch (_: IOException) {
            return
        }
        trimDaily(needsRewrite)
    }

    /** Drops days beyond [DAILY_RETENTION] and compacts the file to one line per day if
     * anything changed. */
    private fun trimDaily(rewriteAnyway: Boolean) {
        val cutoff = clock().minus(DAILY_RETENTION).atZone(zone).toLocalDate()
        val removed = days.removeAll { it.date < cutoff }
        if (removed || rewriteAnyway || dailyHasExtraLines) rewrite(dailyFile, days.map(::serializeDaily))
        dailyHasExtraLines = false
    }

    /** Replaces [target] with [lines] (see [writeAtomically]). */
    private fun rewrite(target: File, lines: List<String>) {
        try {
            writeAtomically(target, lines.joinToString("") { it + "\n" }.toByteArray(Charsets.UTF_8))
        } catch (_: IOException) {
            // Kept in memory; the file is rewritten on the next trim.
        }
    }

    companion object {
        /** Covers the previous weekly window plus some margin. */
        val RETENTION: Duration = Duration.ofDays(35)

        /** A full year in the heatmap, plus some margin. */
        val DAILY_RETENTION: Duration = Duration.ofDays(400)
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

        private fun serializeDaily(day: DailyUsageRecord): String = buildJsonObject {
            put("date", day.date.toString())
            put("weekPoints", day.weekPoints)
            put("sessionPeak", day.sessionPeak)
            put("hours", day.hourMask)
            day.weekResetsAt?.let { put("weekResetsAt", it.toString()) }
        }.toString()

        private fun parseDaily(line: String): DailyUsageRecord? = try {
            val json = Json.parseToJsonElement(line) as? JsonObject
            val date = json?.string("date")?.let { runCatching { LocalDate.parse(it) }.getOrNull() }
            if (json == null || date == null) null
            else DailyUsageRecord(
                date,
                json.number("weekPoints") ?: 0.0,
                json.number("sessionPeak") ?: 0.0,
                json.long("hours")?.toInt() ?: 0,
                json.instant("weekResetsAt"),
            )
        } catch (_: SerializationException) {
            null
        }

        private fun JsonObject.decimal(name: String): BigDecimal? =
            (this[name] as? JsonPrimitive)?.takeIf { !it.isString }?.content?.toBigDecimalOrNull()
    }
}
