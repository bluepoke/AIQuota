package eu.schicht8.aiquota.core

import kotlinx.serialization.SerializationException
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import java.io.File
import java.io.IOException
import java.nio.file.AtomicMoveNotSupportedException
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.time.Instant

/** The saved accounts as one JSON array, in the list's order (no tokens in it, see
 * [TokenStore]). Written to a temp file and moved over the original, so a crash can't lose
 * the list. */
class JsonFileAccountStore(private val file: File) : AccountStore {
    @Synchronized
    override fun loadAll(): List<SavedAccount> = read()

    @Synchronized
    override fun save(account: SavedAccount) {
        val accounts = read()
        write(if (accounts.any { it.key == account.key }) accounts.map { if (it.key == account.key) account else it } else accounts + account)
    }

    @Synchronized
    override fun remove(key: String) = write(read().filter { it.key != key })

    @Synchronized
    override fun reorder(keys: List<String>) {
        val accounts = read()
        write(keys.mapNotNull { key -> accounts.firstOrNull { it.key == key } } + accounts.filter { it.key !in keys })
    }

    private fun read(): List<SavedAccount> {
        if (!file.exists()) return emptyList()
        return try {
            (Json.parseToJsonElement(file.readText()) as? JsonArray).orEmpty().mapNotNull { element ->
                val json = element as? JsonObject ?: return@mapNotNull null
                val key = json.string("key") ?: return@mapNotNull null
                SavedAccount(
                    key,
                    json.string("accountId"),
                    json.string("label"),
                    json.string("shortName"),
                    json.instant("addedAt") ?: Instant.EPOCH,
                )
            }
        } catch (_: IOException) {
            emptyList()
        } catch (_: SerializationException) {
            emptyList()
        }
    }

    private fun write(accounts: List<SavedAccount>) {
        val json = buildJsonArray {
            accounts.forEach { account ->
                add(
                    buildJsonObject {
                        put("key", account.key)
                        account.accountId?.let { put("accountId", it) }
                        account.label?.let { put("label", it) }
                        account.shortName?.let { put("shortName", it) }
                        put("addedAt", account.addedAt.toString())
                    },
                )
            }
        }
        writeAtomically(file, json.toString().toByteArray(Charsets.UTF_8))
    }
}

/** Encrypts and decrypts stored tokens - on Android with a key in the Android Keystore. */
interface TokenCipher {
    fun encrypt(plain: ByteArray): ByteArray
    fun decrypt(encrypted: ByteArray): ByteArray
}

/** One encrypted file per account in [directory], named by the account's random key. A file
 * that can't be decrypted (e.g. its key is gone) counts as no token. */
class EncryptedFileTokenStore(private val directory: File, private val cipher: TokenCipher) : TokenStore {
    private fun fileFor(key: String) = File(directory, "$key.token")

    @Synchronized
    override fun load(accountKey: String): TokenSet? {
        val file = fileFor(accountKey)
        if (!file.exists()) return null
        return try {
            val json = Json.parseToJsonElement(cipher.decrypt(file.readBytes()).toString(Charsets.UTF_8)) as? JsonObject
            val access = json?.string("access_token") ?: return null
            TokenSet(access, json.string("refresh_token"), json.instant("expires_at"), json.string("scope"))
        } catch (_: Exception) {
            // Unreadable (corrupt, or the Keystore key is gone): needs a new login.
            null
        }
    }

    @Synchronized
    override fun save(accountKey: String, tokens: TokenSet) {
        val json = buildJsonObject {
            put("access_token", tokens.accessToken)
            tokens.refreshToken?.let { put("refresh_token", it) }
            tokens.expiresAt?.let { put("expires_at", it.toString()) }
            tokens.scope?.let { put("scope", it) }
        }
        writeAtomically(fileFor(accountKey), cipher.encrypt(json.toString().toByteArray(Charsets.UTF_8)))
    }

    @Synchronized
    override fun clear(accountKey: String) {
        fileFor(accountKey).delete()
    }
}

/** Writes to a temp file first and then moves it over [file], so a crash or a full disk
 * mid-write never leaves a half-written or missing file behind. Throws an [IOException]. */
internal fun writeAtomically(file: File, bytes: ByteArray) {
    file.parentFile?.mkdirs()
    val temp = File(file.path + ".tmp")
    temp.writeBytes(bytes)
    try {
        Files.move(temp.toPath(), file.toPath(), StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.ATOMIC_MOVE)
    } catch (_: AtomicMoveNotSupportedException) {
        Files.move(temp.toPath(), file.toPath(), StandardCopyOption.REPLACE_EXISTING)
    }
}
