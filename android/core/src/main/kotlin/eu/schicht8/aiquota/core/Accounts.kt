package eu.schicht8.aiquota.core

import java.time.Instant

/** One Claude account the app has been signed in with.
 * [key]: local, random identifier - names the account's token, so nothing on disk contains
 * the email; known from the start, before the profile could be fetched.
 * [accountId]: stable ID from the profile (see [AccountInfo.id]), which keys the usage
 * history and tells whether two logins are the same account; null until fetched once.
 * [label]: "Name (email)", just the name, or just the email, from the profile.
 * [shortName]: optional name the user gave the account, e.g. "Work".
 * [addedAt]: when it was added - the order of the account list. */
data class SavedAccount(
    val key: String,
    val accountId: String?,
    val label: String?,
    val shortName: String?,
    val addedAt: Instant,
) {
    /** The short name, else the profile's label; null if neither is known yet. */
    val displayName: String? get() = shortName ?: label
}

/** Where the saved accounts are kept (not their tokens: see [TokenStore]). */
interface AccountStore {
    /** All saved accounts, oldest first. */
    fun loadAll(): List<SavedAccount>
    fun save(account: SavedAccount)
    fun remove(key: String)
}

/** The OAuth tokens per account - stored encrypted, on the device only. */
interface TokenStore {
    fun load(accountKey: String): TokenSet?
    fun save(accountKey: String, tokens: TokenSet)
    fun clear(accountKey: String)
    fun exists(accountKey: String): Boolean = load(accountKey) != null
}

data class TokenSet(
    val accessToken: String,
    val refreshToken: String?,
    val expiresAt: Instant?,
    val scope: String?,
) {
    fun isExpiredOrExpiringSoon(now: Instant): Boolean = expiresAt != null && expiresAt <= now.plusSeconds(60)
}

/** Usage and profile of a saved account - implemented by [UsageApiClient]. */
interface UsageSource {
    suspend fun fetch(accountKey: String): UsageFetchResult
    suspend fun fetchAccount(accountKey: String): AccountInfo?
}
