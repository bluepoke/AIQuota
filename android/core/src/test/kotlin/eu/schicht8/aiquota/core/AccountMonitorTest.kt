package eu.schicht8.aiquota.core

import kotlinx.coroutines.test.runTest
import java.nio.file.Files
import java.nio.file.Path
import java.time.Duration
import java.time.Instant
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class AccountMonitorTest {
    private var now = Instant.parse("2026-11-24T10:00:00Z")
    private val historyDir: Path = Files.createTempDirectory("aiquota-history")
    private val accounts = InMemoryAccountStore()
    private val tokens = InMemoryTokenStore()
    private val usage = FakeUsageSource()

    private fun monitor() = AccountMonitor(accounts, tokens, usage, historyDirectory = historyDir.toFile(), clock = { now })

    private fun savedAccount(key: String, id: String?, label: String?, minutesAgo: Long = 0): SavedAccount {
        val account = SavedAccount(key, id, label, shortName = null, addedAt = now.minus(Duration.ofMinutes(100 - minutesAgo)))
        accounts.save(account)
        tokens.save(key, TokenSet("access-$key", "refresh-$key", null, null))
        return account
    }

    private fun snapshot(session: Int, week: Int) = UsageSnapshot(
        session, week, now.plus(Duration.ofHours(2)), now.plus(Duration.ofDays(3)), now,
        sessionUtilization = session.toDouble(), weeklyUtilization = week.toDouble(),
    )

    @Test
    fun `fetches every account on its own - a failing one keeps its error and doesn't affect the others`() = runTest {
        savedAccount("work", "id-work", "Work (w@example.com)", minutesAgo = 0)
        savedAccount("home", "id-home", "Home (h@example.com)", minutesAgo = 10)
        usage.results["work"] = UsageFetchResult(UsageFetchStatus.Ok, snapshot(40, 20))
        usage.results["home"] = UsageFetchResult(UsageFetchStatus.NetworkError, error = FetchError.NoInternet)
        val monitor = monitor()

        monitor.fetchDue()

        val (work, home) = monitor.states.value
        assertEquals("work", work.account.key, "in the order the accounts were added")
        assertEquals(UsageFetchStatus.Ok, work.status)
        assertEquals(40, work.snapshot?.sessionPercent)
        assertEquals(UsageFetchStatus.NetworkError, home.status)
        assertEquals(FetchError.NoInternet, home.error)
        assertEquals(null, home.snapshot)
        assertEquals(true, home.hasToken)
        assertEquals(now.plus(Duration.ofMinutes(5)), home.nextFetchAt)
    }

    @Test
    fun `an unexpected failure of one account's fetch leaves the others and itself working`() = runTest {
        savedAccount("work", "id-work", "Work", minutesAgo = 0)
        savedAccount("home", "id-home", "Home", minutesAgo = 10)
        usage.onFetch = { key -> if (key == "work") throw IllegalStateException("keystore unavailable") }
        usage.results["home"] = UsageFetchResult(UsageFetchStatus.Ok, snapshot(40, 20))
        val monitor = monitor()

        monitor.fetchDue()

        val (work, home) = monitor.states.value
        assertEquals(false, work.isFetching)
        assertEquals(FetchError.Other("keystore unavailable"), work.error)
        assertEquals(now.plus(Duration.ofMinutes(5)), work.nextFetchAt)
        assertEquals(40, home.snapshot?.sessionPercent)
    }

    @Test
    fun `an expired login needs a new login on that account only, which is not fetched until then`() = runTest {
        savedAccount("work", "id-work", "Work", minutesAgo = 0)
        savedAccount("home", "id-home", "Home", minutesAgo = 10)
        savedAccount("old", "id-old", "Old", minutesAgo = 20)
        usage.results["work"] = UsageFetchResult(UsageFetchStatus.AuthExpired)
        usage.results["home"] = UsageFetchResult(UsageFetchStatus.Ok, snapshot(40, 20))
        usage.results["old"] = UsageFetchResult(UsageFetchStatus.NotLoggedIn)
        val monitor = monitor()
        // Its refresh token gets rejected: the token provider drops it, and answers "not logged in".
        tokens.clear("old")

        monitor.fetchDue()
        now = now.plus(Duration.ofMinutes(10))
        usage.fetched.clear()
        monitor.fetchDue()

        val (work, home, old) = monitor.states.value
        assertEquals(false, work.hasToken)
        assertEquals(null, tokens.load("work"))
        assertEquals(false, old.hasToken)
        assertEquals(true, home.hasToken)
        assertEquals(listOf("home"), usage.fetched)
    }

    @Test
    fun `backs off a rate-limited account, doubling the wait and honouring Retry-After, even on a manual refresh`() = runTest {
        savedAccount("work", "id-work", "Work", minutesAgo = 0)
        savedAccount("home", "id-home", "Home", minutesAgo = 10)
        usage.results["work"] = UsageFetchResult(UsageFetchStatus.NetworkError, error = FetchError.Http(429), rateLimited = true)
        usage.results["home"] = UsageFetchResult(UsageFetchStatus.Ok, snapshot(40, 20))
        val monitor = monitor()
        val start = now

        monitor.fetchDue()
        assertEquals(start.plus(Duration.ofMinutes(10)), monitor.states.value[0].nextFetchAt)

        now = start.plus(Duration.ofMinutes(6))
        usage.fetched.clear()
        monitor.refreshAll()
        assertEquals(listOf("home"), usage.fetched, "a manual refresh doesn't ask a rate-limited account early")

        now = start.plus(Duration.ofMinutes(10))
        monitor.fetchDue()
        assertEquals(now.plus(Duration.ofMinutes(20)), monitor.states.value[0].nextFetchAt, "second strike")

        usage.results["work"] = UsageFetchResult(UsageFetchStatus.NetworkError, error = FetchError.Http(429), rateLimited = true, retryAfter = Duration.ofHours(2))
        now = now.plus(Duration.ofMinutes(20))
        monitor.fetchDue()
        assertEquals(now.plus(Duration.ofHours(2)), monitor.states.value[0].nextFetchAt, "Retry-After beyond the backoff")

        usage.results["work"] = UsageFetchResult(UsageFetchStatus.Ok, snapshot(10, 5))
        now = now.plus(Duration.ofHours(2))
        monitor.fetchDue()
        assertEquals(0, monitor.states.value[0].rateLimitStrikes)
        assertEquals(now.plus(Duration.ofMinutes(5)), monitor.states.value[0].nextFetchAt)
    }

    @Test
    fun `records each account's history, which drives its forecast and is shown again after a restart`() = runTest {
        savedAccount("work", "id-work", "Work")
        val sessionReset = now.plus(Duration.ofHours(3))
        val weekReset = now.plus(Duration.ofDays(3))
        val monitor = monitor()
        for (step in 0..2) {
            val percent = 10.0 + 10 * step
            usage.results["work"] = UsageFetchResult(
                UsageFetchStatus.Ok,
                UsageSnapshot(percent.toInt(), 30, sessionReset, weekReset, now, sessionUtilization = percent, weeklyUtilization = 30.0),
            )
            monitor.fetchDue()
            now = now.plus(Duration.ofMinutes(10))
        }

        // 1 point per minute from 30% at the last fetch: the limit in 70 minutes, as of that fetch.
        assertEquals(UsageForecast(ForecastKind.Limit, Duration.ofMinutes(70), 100.0), monitor.states.value.single().sessionForecast)

        val restarted = monitor().states.value.single()
        assertEquals(30, restarted.snapshot?.sessionPercent)
        assertEquals(sessionReset, restarted.snapshot?.sessionResetsAt)
        assertEquals(ForecastKind.Limit, restarted.sessionForecast.kind)

        now = sessionReset.plusSeconds(60)
        val afterReset = monitor().states.value.single().snapshot!!
        assertEquals(0, afterReset.sessionPercent, "a session whose reset has passed counts as empty")
        assertEquals(null, afterReset.sessionResetsAt)
        assertEquals(30, afterReset.weeklyPercent)
    }

    @Test
    fun `a login adds a new account, or renews the one it turns out to be already`() = runTest {
        savedAccount("work", "id-work", "Work (w@example.com)")
        val monitor = monitor()
        val homeTokens = TokenSet("access-home", "refresh-home", null, null)

        val added = monitor.addLogin(targetKey = null, homeTokens, AccountInfo("id-home", "Home (h@example.com)"))

        assertEquals(null, added)
        val home = monitor.states.value.last()
        assertEquals("id-home", home.account.accountId)
        assertEquals("Home (h@example.com)", home.account.label)
        assertEquals(true, home.hasToken)
        assertEquals(homeTokens, tokens.load(home.account.key))
        assertEquals(now, home.nextFetchAt, "fetched right away")
        assertEquals(home.account, accounts.loadAll().last())

        val renewed = TokenSet("access-work-2", "refresh-work-2", null, null)
        val again = monitor.addLogin(targetKey = null, renewed, AccountInfo("id-work", "Work (w@example.com)"))

        assertEquals(LoginMessage.AlreadyAdded("Work (w@example.com)"), again)
        assertEquals(2, monitor.states.value.size)
        assertEquals(renewed, tokens.load("work"))
    }

    @Test
    fun `a new login for an expired account fixes it, and says so when it signed in another account`() = runTest {
        savedAccount("work", "id-work", "Work", minutesAgo = 0)
        savedAccount("home", "id-home", "Home", minutesAgo = 10)
        savedAccount("unknown", null, null, minutesAgo = 20)
        usage.results["work"] = UsageFetchResult(UsageFetchStatus.AuthExpired)
        usage.results["unknown"] = UsageFetchResult(UsageFetchStatus.AuthExpired)
        usage.results["home"] = UsageFetchResult(UsageFetchStatus.Ok, snapshot(1, 1))
        val monitor = monitor()
        monitor.fetchDue()
        fun state(key: String) = monitor.states.value.single { it.account.key == key }
        val fresh = TokenSet("new", "new-refresh", null, null)

        assertEquals(LoginMessage.DifferentExistingAccount("Home", "Work"), monitor.addLogin("work", fresh, AccountInfo("id-home", "Home")))
        assertEquals(false, state("work").hasToken, "still needs its own login")
        assertEquals(fresh, tokens.load("home"))

        assertEquals(LoginMessage.DifferentAccount("Other", "Work"), monitor.addLogin("work", fresh, AccountInfo("id-other", "Other")))
        assertEquals(4, monitor.states.value.size)
        assertEquals(false, state("work").hasToken)

        assertEquals(null, monitor.addLogin("work", fresh, AccountInfo("id-work", "Work")))
        assertEquals(true, state("work").hasToken)

        assertEquals(null, monitor.addLogin("unknown", fresh, AccountInfo("id-unknown", "Unknown")), "an account whose ID was never known")
        assertEquals(true, state("unknown").hasToken)
        assertEquals("id-unknown", state("unknown").account.accountId)

        usage.fetched.clear()
        monitor.fetchDue()
        assertTrue(usage.fetched.containsAll(listOf("work", "unknown")), "fetched again right after the login")
    }

    @Test
    fun `resolves an account's profile after its first fetch, merging it into a saved twin`() = runTest {
        savedAccount("work", "id-work", "Work", minutesAgo = 0)
        savedAccount("nameless", null, null, minutesAgo = 10)
        savedAccount("twin", null, null, minutesAgo = 20)
        accounts.save(accounts.loadAll().single { it.key == "twin" }.copy(shortName = "Büro"))
        usage.results["work"] = UsageFetchResult(UsageFetchStatus.Ok, snapshot(1, 1))
        usage.results["nameless"] = UsageFetchResult(UsageFetchStatus.Ok, snapshot(2, 2))
        usage.results["twin"] = UsageFetchResult(UsageFetchStatus.Ok, snapshot(3, 3))
        usage.profiles["work"] = AccountInfo("id-work", "Work (w@example.com)")
        usage.profiles["nameless"] = AccountInfo("id-home", "Home (h@example.com)")
        usage.profiles["twin"] = AccountInfo("id-work", "Work (w@example.com)")
        val monitor = monitor()

        monitor.fetchDue()

        val states = monitor.states.value
        assertEquals(listOf("work", "nameless"), states.map { it.account.key }, "the twin was merged into work")
        assertEquals(SavedAccount("work", "id-work", "Work (w@example.com)", "Büro", states[0].account.addedAt), states[0].account)
        assertEquals(TokenSet("access-twin", "refresh-twin", null, null), tokens.load("work"), "keeps the twin's newer token")
        assertEquals("Home (h@example.com)", states[1].account.label)
        assertEquals(listOf("work", "nameless"), accounts.loadAll().map { it.key })

        usage.fetched.clear()
        usage.profiles.clear()
        now = now.plus(Duration.ofMinutes(5))
        monitor.fetchDue()
        assertEquals("Home (h@example.com)", monitor.states.value[1].account.label, "the profile is asked once per run")
    }

    @Test
    fun `renames an account, and an empty name goes back to the profile's`() = runTest {
        savedAccount("work", "id-work", "Work (w@example.com)")
        val monitor = monitor()

        monitor.rename("work", "  Job ")
        assertEquals("Job", monitor.states.value.single().account.displayName)
        assertEquals("Job", accounts.loadAll().single().shortName)

        monitor.rename("work", " ")
        assertEquals("Work (w@example.com)", monitor.states.value.single().account.displayName)
    }

    @Test
    fun `removes an account with its token, and drops a fetch that was running for it`() = runTest {
        savedAccount("work", "id-work", "Work")
        val monitor = monitor()
        usage.onFetch = { key -> if (key == "work") monitor.remove("work") }
        usage.results["work"] = UsageFetchResult(UsageFetchStatus.Ok, snapshot(1, 1))

        monitor.fetchDue()

        assertEquals(emptyList(), monitor.states.value)
        assertEquals(emptyList(), accounts.loadAll())
        assertEquals(null, tokens.load("work"))
    }

    @Test
    fun `waits at most an hour of backoff and six hours of Retry-After`() {
        val limited = UsageFetchResult(UsageFetchStatus.NetworkError, rateLimited = true)
        val poll = Duration.ofMinutes(5)

        assertEquals(Duration.ofHours(1), AccountMonitor.delayAfter(limited, 9, poll))
        assertEquals(Duration.ofHours(6), AccountMonitor.delayAfter(limited.copy(retryAfter = Duration.ofDays(1)), 1, poll))
        assertEquals(poll, AccountMonitor.delayAfter(UsageFetchResult(UsageFetchStatus.Ok), 3, poll))
    }
}

class InMemoryAccountStore : AccountStore {
    private val accounts = linkedMapOf<String, SavedAccount>()
    override fun loadAll() = accounts.values.toList()
    override fun save(account: SavedAccount) {
        accounts[account.key] = account
    }
    override fun remove(key: String) {
        accounts.remove(key)
    }
    override fun reorder(keys: List<String>) {
        val ordered = keys.mapNotNull { accounts[it] } + accounts.values.filter { it.key !in keys }
        accounts.clear()
        ordered.forEach { accounts[it.key] = it }
    }
}

class InMemoryTokenStore : TokenStore {
    val tokens = mutableMapOf<String, TokenSet>()
    override fun load(accountKey: String) = tokens[accountKey]
    override fun save(accountKey: String, tokens: TokenSet) {
        this.tokens[accountKey] = tokens
    }
    override fun clear(accountKey: String) {
        tokens.remove(accountKey)
    }
}

/** Answers each account with its entry in [results]; counts the calls. */
class FakeUsageSource : UsageSource {
    val results = mutableMapOf<String, UsageFetchResult>()
    val profiles = mutableMapOf<String, AccountInfo>()
    val fetched = mutableListOf<String>()
    /** Runs while a fetch is "in flight" - e.g. to change the accounts meanwhile. */
    var onFetch: (String) -> Unit = {}

    override suspend fun fetch(accountKey: String): UsageFetchResult {
        fetched += accountKey
        onFetch(accountKey)
        return results[accountKey] ?: UsageFetchResult(UsageFetchStatus.NetworkError, error = FetchError.Timeout)
    }

    override suspend fun fetchAccount(accountKey: String) = profiles[accountKey]
}
