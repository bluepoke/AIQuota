package eu.schicht8.aiquota.core

import kotlinx.coroutines.test.runTest
import java.io.File
import java.nio.file.Files
import java.time.Duration
import java.time.Instant
import java.time.ZoneOffset
import kotlin.test.Test
import kotlin.test.assertEquals

/** The background fetch runs [AccountMonitor.fetchDue] in what is often a fresh process: what
 * must not be forgotten between runs is kept on disk. */
class BackgroundMonitoringTest {
    private var now = Instant.parse("2026-11-24T10:00:00Z")
    private val dir: File = Files.createTempDirectory("aiquota-background").toFile()
    private val accounts = JsonFileAccountStore(File(dir, "accounts.json"))
    private val tokens = InMemoryTokenStore()
    private val usage = FakeUsageSource()
    private val shown = mutableListOf<Pair<String, String>>()

    private fun monitor() = AccountMonitor(
        accounts, tokens, usage, File(dir, "history"), clock = { now },
        stateStore = JsonFileMonitorStateStore(File(dir, "monitor.json")),
        alerts = AlertSink { account, alerts -> alerts.forEach { shown += account.key to it.text } },
        strings = { Strings(AppLanguage.English, ZoneOffset.UTC) },
    )

    private fun savedAccount(key: String, label: String, minutesAgo: Long) {
        accounts.save(SavedAccount(key, "id-$key", label, shortName = null, addedAt = now.minus(Duration.ofMinutes(minutesAgo))))
        tokens.save(key, TokenSet("access-$key", null, null, null))
    }

    private fun ok(session: Int) = UsageFetchResult(
        UsageFetchStatus.Ok,
        UsageSnapshot(session, 10, now.plus(Duration.ofHours(2)), now.plus(Duration.ofDays(3)), now, sessionUtilization = session.toDouble(), weeklyUtilization = 10.0),
    )

    @Test
    fun `a restart keeps an account's rate-limit backoff, so the background fetch doesn't ask it early`() = runTest {
        savedAccount("work", "Work", minutesAgo = 10)
        usage.results["work"] = UsageFetchResult(UsageFetchStatus.NetworkError, error = FetchError.Http(429), rateLimited = true, retryAfter = Duration.ofHours(2))
        monitor().fetchDue()
        val start = now

        now = start.plus(Duration.ofMinutes(15))
        usage.fetched.clear()
        val restarted = monitor()
        restarted.fetchDue()
        assertEquals(emptyList(), usage.fetched)
        assertEquals(1, restarted.states.value.single().rateLimitStrikes)

        now = start.plus(Duration.ofHours(2))
        usage.results["work"] = ok(20)
        restarted.fetchDue()
        assertEquals(listOf("work"), usage.fetched)

        assertEquals(now, monitor().states.value.single().nextFetchAt, "without a backoff an account is fetched as soon as the app starts")
    }

    @Test
    fun `notifies a threshold once per account, also across restarts, naming the account when there are several`() = runTest {
        savedAccount("work", "Work", minutesAgo = 20)
        savedAccount("home", "Home", minutesAgo = 10)
        usage.results["work"] = ok(92)
        usage.results["home"] = ok(50)

        monitor().fetchDue()
        assertEquals(listOf("work" to "Work: Session usage (5h) at 92% · resets 12:00"), shown)

        shown.clear()
        now = now.plus(Duration.ofMinutes(15))
        usage.results["work"] = ok(95)
        usage.results["home"] = ok(96)
        monitor().fetchDue()
        assertEquals(listOf("home" to "Home: Session usage (5h) at 96% · resets 12:15"), shown)
    }

    @Test
    fun `a single account's notifications don't name it`() = runTest {
        savedAccount("work", "Work", minutesAgo = 20)
        usage.results["work"] = ok(92)

        monitor().fetchDue()

        assertEquals(listOf("work" to "Session usage (5h) at 92% · resets 12:00"), shown)
    }

    @Test
    fun `a shorter poll interval applies at once, not only after the fetch already scheduled`() = runTest {
        savedAccount("work", "Work", minutesAgo = 10)
        usage.results["work"] = ok(20)
        var interval = Duration.ofMinutes(30)
        val monitor = AccountMonitor(accounts, tokens, usage, File(dir, "history"), clock = { now }, pollInterval = { interval })
        monitor.fetchDue()

        interval = Duration.ofMinutes(5)
        monitor.pollIntervalChanged()
        now = now.plus(Duration.ofMinutes(6))
        usage.fetched.clear()
        monitor.fetchDue()

        assertEquals(listOf("work"), usage.fetched)
    }

    @Test
    fun `accounts can be moved in the list, and keep that order`() = runTest {
        savedAccount("a", "A", minutesAgo = 30)
        savedAccount("b", "B", minutesAgo = 20)
        savedAccount("c", "C", minutesAgo = 10)
        val monitor = monitor()

        monitor.move("c", -2)
        monitor.move("a", 5)

        assertEquals(listOf("c", "b", "a"), monitor.states.value.map { it.account.key })
        assertEquals(listOf("c", "b", "a"), monitor().states.value.map { it.account.key })
        monitor.rename("b", "Bee")
        assertEquals(listOf("c", "b", "a"), accounts.loadAll().map { it.key }, "renaming keeps the place")
    }
}
