package eu.schicht8.aiquota.core

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import java.io.File
import java.time.Duration
import java.time.Instant
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap

/** What the overview shows of one saved account. [status]: result of the last fetch, null
 * before the first one. [snapshot]: the last known usage - kept through later errors. */
data class AccountState(
    val account: SavedAccount,
    val hasToken: Boolean,
    val snapshot: UsageSnapshot? = null,
    val status: UsageFetchStatus? = null,
    val error: FetchError? = null,
    val isFetching: Boolean = false,
    val sessionForecast: UsageForecast = UsageForecast.NONE,
    val weeklyForecast: UsageForecast = UsageForecast.NONE,
    val nextFetchAt: Instant,
    /** Consecutive rate-limited fetches; each doubles the wait. */
    val rateLimitStrikes: Int = 0,
)

/** What a login has to tell the user about where it went (see [AccountMonitor.addLogin]);
 * names are null when not known. */
sealed interface LoginMessage {
    /** Logged in to an account that is already in the list: its sign-in was renewed. */
    data class AlreadyAdded(val name: String?) : LoginMessage

    /** Logged in again for [expected], but as a new, different account - added separately. */
    data class DifferentAccount(val signedIn: String?, val expected: String?) : LoginMessage

    /** Logged in again for [expected], but as another saved account, which was renewed. */
    data class DifferentExistingAccount(val signedIn: String?, val expected: String?) : LoginMessage
}

/**
 * Monitors every saved account at once (#19): each is fetched on its own schedule, recorded
 * in its own history, and has its own values, errors and "login required" state - a failing
 * account never affects the others. Rate limiting (429) backs off per account, as on Windows
 * (#18). The app calls [fetchDue] periodically while it's in the foreground.
 */
class AccountMonitor(
    private val accounts: AccountStore,
    private val tokens: TokenStore,
    private val usage: UsageSource,
    private val historyDirectory: File,
    private val clock: () -> Instant = Instant::now,
    private val pollInterval: Duration = DEFAULT_POLL_INTERVAL,
    private val newKey: () -> String = { UUID.randomUUID().toString().replace("-", "") },
) {
    private val histories = mutableMapOf<String, JsonlUsageHistory>()

    /** Held while the account list, the stores or an account's state change - logins, merges,
     * renames, removals and recording a fetch run concurrently (fetches on IO threads). */
    private val lock = Any()

    /** Accounts whose profile was fetched this run - it's asked once, after the first
     * successful fetch (or with the login). */
    private val resolvedAccounts: MutableSet<String> = ConcurrentHashMap.newKeySet()
    private val _states = MutableStateFlow(accounts.loadAll().map { initialState(it) })

    /** One entry per saved account, in the account list's order. */
    val states: StateFlow<List<AccountState>> = _states.asStateFlow()

    /** Fetches every account with a token whose next fetch is due, all at once. */
    suspend fun fetchDue() {
        val now = clock()
        fetchAll { now >= it.nextFetchAt }
    }

    /** Pull to refresh: fetches every account with a token now - except one that's backing off
     * after rate limiting, which would only make that longer. */
    suspend fun refreshAll() {
        val now = clock()
        fetchAll { it.rateLimitStrikes == 0 || now >= it.nextFetchAt }
    }

    private suspend fun fetchAll(select: (AccountState) -> Boolean) {
        val due = startFetching { it.hasToken && !it.isFetching && select(it) }
        coroutineScope { due.forEach { launch { fetch(it) } } }
    }

    /**
     * Stores the tokens of a completed login with the saved account they belong to: [targetKey]
     * when logging in again for that account, else a new one - unless [info] (the profile
     * fetched with the new token) shows it's another saved account, which is then renewed
     * instead. The account is fetched with the next [fetchDue]. Returns what the user should
     * be told when that isn't what they presumably meant.
     */
    fun addLogin(targetKey: String?, newTokens: TokenSet, info: AccountInfo?): LoginMessage? = synchronized(lock) {
        val target = targetKey?.let { state(it)?.account }
        val signedInName = info?.displayLabel
        val existing = info?.id?.let { id -> _states.value.firstOrNull { it.account.accountId == id }?.account }
        val (account, message) = when {
            existing != null -> existing to when {
                target == null -> LoginMessage.AlreadyAdded(existing.displayName ?: signedInName)
                existing != target -> LoginMessage.DifferentExistingAccount(existing.displayName ?: signedInName, target.displayName)
                else -> null
            }
            // A new login for an expired account - unless it's provably a different account.
            target != null && (target.accountId == null || info?.id == null) -> target to null
            else -> addAccount() to target?.let { LoginMessage.DifferentAccount(signedInName, it.displayName) }
        }

        tokens.save(account.key, newTokens)
        val resolved = if (info != null) applyAccountInfo(account, info).also { resolvedAccounts += it.key } else account
        updateState(resolved.key) { it.copy(hasToken = true, status = null, error = null, rateLimitStrikes = 0, nextFetchAt = clock()) }
        message
    }

    private fun addAccount(): SavedAccount {
        val account = SavedAccount(newKey(), accountId = null, label = null, shortName = null, addedAt = clock())
        accounts.save(account)
        _states.update { it + AccountState(account, hasToken = false, nextFetchAt = clock()) }
        return account
    }

    /** Stores a fetched profile with the account. If another saved account turns out to be the
     * same Claude account, the two are merged: the other one keeps this one's newer token.
     * Returns the account that remains. */
    private fun applyAccountInfo(account: SavedAccount, info: AccountInfo): SavedAccount = synchronized(lock) {
        val twin = info.id?.let { id -> _states.value.firstOrNull { it.account.key != account.key && it.account.accountId == id }?.account }
        var remaining = account
        if (twin != null) {
            tokens.load(account.key)?.let { tokens.save(twin.key, it) }
            remove(account.key)
            remaining = twin.copy(shortName = twin.shortName ?: account.shortName)
        }
        remaining = remaining.copy(accountId = info.id ?: remaining.accountId, label = info.displayLabel ?: remaining.label)
        accounts.save(remaining)
        val saved = remaining
        updateState(saved.key) { it.copy(account = saved, hasToken = tokens.exists(saved.key)) }
        saved
    }

    /** Sets the account's short name; a blank one goes back to the profile's name. */
    fun rename(key: String, shortName: String?): Unit = synchronized(lock) {
        val account = state(key)?.account ?: return
        val renamed = account.copy(shortName = shortName?.trim()?.takeIf { it.isNotEmpty() })
        accounts.save(renamed)
        updateState(key) { it.copy(account = renamed) }
    }

    /** Removes the account and its token. Its usage history stays on the device. */
    fun remove(key: String): Unit = synchronized(lock) {
        tokens.clear(key)
        accounts.remove(key)
        _states.update { list -> list.filter { it.account.key != key } }
    }

    /** Marks the accounts matching [select] as fetching and returns their keys - in one
     * step, so two callers never fetch the same account at once. */
    private fun startFetching(select: (AccountState) -> Boolean): List<String> {
        var keys = emptyList<String>()
        _states.update { list ->
            keys = list.filter(select).map { it.account.key }
            list.map { if (it.account.key in keys) it.copy(isFetching = true) else it }
        }
        return keys
    }

    /** Fetches one account, records it in its history and resolves its profile once. Any
     * failure only affects this account; a result for an account that was removed (or merged
     * into another) meanwhile is dropped. */
    private suspend fun fetch(key: String) {
        try {
            val result = usage.fetch(key)
            var account = state(key)?.account ?: return
            if (result.status == UsageFetchStatus.Ok && key !in resolvedAccounts) {
                val info = usage.fetchAccount(key)
                account = state(key)?.account ?: return
                if (info != null) {
                    resolvedAccounts += key
                    account = applyAccountInfo(account, info)
                    // Merged into a saved twin, which is fetched on its own.
                    if (account.key != key) return
                }
            }
            record(account, result)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            // E.g. the Keystore or the storage failing: shown like a network error, and tried
            // again on the next interval.
            val account = state(key)?.account ?: return
            record(account, UsageFetchResult(UsageFetchStatus.NetworkError, error = FetchError.Other(e.message ?: e.javaClass.simpleName)))
        } finally {
            updateState(key) { it.copy(isFetching = false) }
        }
    }

    private fun record(account: SavedAccount, result: UsageFetchResult): Unit = synchronized(lock) {
        val key = account.key
        if (state(key) == null) return
        val now = clock()
        val history = account.accountId?.let(::historyFor)
        if (result.status == UsageFetchStatus.Ok && result.snapshot != null) {
            history?.append(UsageHistoryRecord.fromSnapshot(result.snapshot))
        }
        // The usage API rejected the token: only this account needs a new login, and it's
        // skipped until it has one.
        if (result.status == UsageFetchStatus.AuthExpired) tokens.clear(key)
        val hasToken = tokens.exists(key)
        updateState(key) { state ->
            val strikes = if (result.rateLimited) state.rateLimitStrikes + 1 else 0
            val base = state.copy(
                hasToken = hasToken,
                status = result.status,
                error = result.error,
                rateLimitStrikes = strikes,
                nextFetchAt = now.plus(delayAfter(result, strikes, pollInterval)),
            )
            when (result.status) {
                UsageFetchStatus.Ok -> {
                    val snapshot = result.snapshot ?: state.snapshot
                    val (session, week) = forecasts(history?.all.orEmpty(), snapshot, now)
                    base.copy(snapshot = snapshot, sessionForecast = session, weeklyForecast = week)
                }
                else -> base
            }
        }
    }

    private fun state(key: String) = _states.value.firstOrNull { it.account.key == key }

    private fun updateState(key: String, change: (AccountState) -> AccountState) {
        _states.update { list -> list.map { if (it.account.key == key) change(it) else it } }
    }

    /** An account's last known values from its history, shown until its first fetch. */
    private fun initialState(account: SavedAccount): AccountState {
        val now = clock()
        val records = account.accountId?.let { historyFor(it).all }.orEmpty()
        val snapshot = records.lastOrNull()?.toSnapshot(now)
        val (session, week) = forecasts(records, snapshot, now)
        return AccountState(
            account, hasToken = tokens.exists(account.key), snapshot = snapshot,
            sessionForecast = session, weeklyForecast = week, nextFetchAt = now,
        )
    }

    @Synchronized
    private fun historyFor(accountId: String): JsonlUsageHistory =
        histories.getOrPut(accountId) { JsonlUsageHistory(historyDirectory, accountId, clock) }

    companion object {
        /** As on Windows: every 5 minutes while the app is in front. */
        val DEFAULT_POLL_INTERVAL: Duration = Duration.ofMinutes(5)

        /** The longest wait after repeated rate limiting. */
        val MAX_BACKOFF: Duration = Duration.ofHours(1)

        /** A server's Retry-After is honoured up to this. */
        val MAX_RETRY_AFTER: Duration = Duration.ofHours(6)

        /** When to fetch after a result: the poll interval, or after rate limiting ([strikes]
         * times in a row, this one included) the longer of Retry-After and an exponential
         * backoff - a port of BackgroundAccount.DelayAfter. */
        fun delayAfter(result: UsageFetchResult, strikes: Int, pollInterval: Duration): Duration {
            if (!result.rateLimited) return pollInterval
            val backoff = minOf(MAX_BACKOFF, pollInterval.multipliedBy(1L shl strikes.coerceIn(1, 10)))
            val retryAfter = result.retryAfter
            if (retryAfter != null && retryAfter > backoff) return minOf(retryAfter, MAX_RETRY_AFTER)
            return backoff
        }

        /** The session and week forecasts of an account from its history (which already
         * includes [snapshot]). No session forecast while no session is running. */
        fun forecasts(records: List<UsageHistoryRecord>, snapshot: UsageSnapshot?, now: Instant): Pair<UsageForecast, UsageForecast> {
            if (snapshot == null) return UsageForecast.NONE to UsageForecast.NONE
            val session = if (snapshot.sessionResetsAt == null) UsageForecast.NONE
            else BurnRateForecaster.forecast(
                records.map { UsagePoint(it.time, it.session, it.sessionResetsAt) },
                snapshot.sessionResetsAt, BurnRateForecaster.SESSION, now,
            )
            val week = BurnRateForecaster.forecast(
                records.map { UsagePoint(it.time, it.week, it.weekResetsAt) },
                snapshot.weeklyResetsAt, BurnRateForecaster.WEEK, now,
            )
            return session to week
        }
    }
}
