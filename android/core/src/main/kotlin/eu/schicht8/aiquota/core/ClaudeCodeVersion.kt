package eu.schicht8.aiquota.core

import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.io.IOException
import java.time.Duration
import java.time.Instant

/**
 * The current Claude Code version, for the User-Agent of the usage request: the endpoint
 * reports limit resets (#21) only to Claude Code, and may turn away an outdated version. A
 * port of AIQuota/ClaudeCodeVersion.cs, reading only the release channel Claude Code's own
 * installer uses (the Windows app falls back to npm; on Android no other host is asked).
 * Checked at most every 6 hours; the last known version is kept via [load]/[save].
 */
class ClaudeCodeVersion(
    private val transport: HttpTransport,
    private val load: () -> String?,
    private val save: (String) -> Unit,
    private val clock: () -> Instant = Instant::now,
) {
    private val gate = Mutex()
    @Volatile private var version: String? = null
    @Volatile private var nextCheck: Instant = Instant.MIN

    /** "claude-cli/2.1.328 (external, cli)", after reading the version online if it's due.
     * Never throws: on failure the last known version stays. */
    suspend fun userAgent(): String {
        ensureFresh()
        return "claude-cli/${current()} (external, cli)"
    }

    private fun current(): String = version ?: load()?.takeIf(::isVersion)?.also { version = it } ?: FALLBACK

    private suspend fun ensureFresh() {
        if (clock() < nextCheck) return
        gate.withLock {
            if (clock() < nextCheck) return
            val fetched = try {
                transport.execute(HttpRequest("GET", RELEASE_CHANNEL_URL))
                    .takeIf { it.isSuccess }?.body?.trim()?.takeIf(::isVersion)
            } catch (_: IOException) {
                null
            }
            if (fetched != null && fetched != current()) save(fetched)
            if (fetched != null) version = fetched
            nextCheck = clock().plus(if (fetched == null) RETRY_INTERVAL else CHECK_INTERVAL)
        }
    }

    private companion object {
        /** Used until a version has been read online once. */
        const val FALLBACK = "2.1.328"
        const val RELEASE_CHANNEL_URL = "https://downloads.claude.ai/claude-code-releases/latest"
        val CHECK_INTERVAL: Duration = Duration.ofHours(6)
        val RETRY_INTERVAL: Duration = Duration.ofMinutes(30)
        val VERSION = Regex("""^\d{1,4}\.\d{1,4}\.\d{1,6}$""")

        fun isVersion(text: String) = VERSION.matches(text)
    }
}
