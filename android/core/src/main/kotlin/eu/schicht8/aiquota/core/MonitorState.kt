package eu.schicht8.aiquota.core

import kotlinx.serialization.SerializationException
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import kotlinx.serialization.json.putJsonArray
import java.io.File
import java.io.IOException
import java.time.Instant

/** What [AccountMonitor] keeps of one account across process restarts - the background fetch
 * often runs in a fresh process. [backoffUntil]/[rateLimitStrikes]: after rate limiting, the
 * account isn't asked again before then. [alerts]: what has been notified. */
data class PersistedAccountState(
    val backoffUntil: Instant? = null,
    val rateLimitStrikes: Int = 0,
    val alerts: AlertState = AlertState(),
)

interface MonitorStateStore {
    /** By account key. */
    fun loadAll(): Map<String, PersistedAccountState>
    fun save(accountKey: String, state: PersistedAccountState)
    fun remove(accountKey: String)
}

/** Keeps nothing beyond the process - for tests and previews. */
class InMemoryMonitorStateStore : MonitorStateStore {
    private val states = mutableMapOf<String, PersistedAccountState>()

    @Synchronized
    override fun loadAll(): Map<String, PersistedAccountState> = states.toMap()

    @Synchronized
    override fun save(accountKey: String, state: PersistedAccountState) {
        states[accountKey] = state
    }

    @Synchronized
    override fun remove(accountKey: String) {
        states.remove(accountKey)
    }
}

/** All accounts' state in one JSON object keyed by account key. An unreadable file counts as
 * empty: at worst a threshold is notified once more. */
class JsonFileMonitorStateStore(private val file: File) : MonitorStateStore {
    @Synchronized
    override fun loadAll(): Map<String, PersistedAccountState> = read()

    @Synchronized
    override fun save(accountKey: String, state: PersistedAccountState) = write(read() + (accountKey to state))

    @Synchronized
    override fun remove(accountKey: String) = write(read() - accountKey)

    private fun read(): Map<String, PersistedAccountState> {
        if (!file.exists()) return emptyMap()
        val json = try {
            Json.parseToJsonElement(file.readText()) as? JsonObject
        } catch (_: IOException) {
            null
        } catch (_: SerializationException) {
            null
        } ?: return emptyMap()
        return json.mapNotNull { (key, value) -> (value as? JsonObject)?.let { key to parse(it) } }.toMap()
    }

    private fun write(states: Map<String, PersistedAccountState>) {
        val json = buildJsonObject {
            states.forEach { (key, state) -> put(key, serialize(state)) }
        }
        try {
            writeAtomically(file, json.toString().toByteArray(Charsets.UTF_8))
        } catch (_: IOException) {
            // Kept in memory by the monitor; written with the next change.
        }
    }

    private fun parse(json: JsonObject): PersistedAccountState {
        val alerts = json.obj("alerts")
        fun set(name: String) = alerts?.array(name).orEmpty().mapNotNull { it.stringValue() }.toSet()
        return PersistedAccountState(
            backoffUntil = json.instant("backoffUntil"),
            rateLimitStrikes = json.long("rateLimitStrikes")?.toInt() ?: 0,
            alerts = AlertState(
                sessionWarningShown = alerts?.bool("session") ?: false,
                weeklyWarningShown = alerts?.bool("weekly") ?: false,
                sessionForecastWarningShown = alerts?.bool("sessionForecast") ?: false,
                limitWarningsShown = set("limits"),
                notifiedLimitResets = set("limitResets"),
            ),
        )
    }

    private fun serialize(state: PersistedAccountState) = buildJsonObject {
        state.backoffUntil?.let { put("backoffUntil", it.toString()) }
        put("rateLimitStrikes", state.rateLimitStrikes)
        put(
            "alerts",
            buildJsonObject {
                put("session", state.alerts.sessionWarningShown)
                put("weekly", state.alerts.weeklyWarningShown)
                put("sessionForecast", state.alerts.sessionForecastWarningShown)
                putJsonArray("limits") { state.alerts.limitWarningsShown.sorted().forEach { add(JsonPrimitive(it)) } }
                putJsonArray("limitResets") { state.alerts.notifiedLimitResets.sorted().forEach { add(JsonPrimitive(it)) } }
            },
        )
    }
}

/** Shows the notifications a fetch called for, for [account] - on Android as system
 * notifications. */
fun interface AlertSink {
    fun show(account: SavedAccount, alerts: List<UsageAlert>)
}
