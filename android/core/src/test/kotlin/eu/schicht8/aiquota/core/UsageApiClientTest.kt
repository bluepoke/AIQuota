package eu.schicht8.aiquota.core

import kotlinx.coroutines.test.runTest
import java.io.IOException
import java.math.BigDecimal
import java.net.ConnectException
import java.net.SocketTimeoutException
import java.net.UnknownHostException
import java.time.Duration
import java.time.Instant
import kotlin.test.Test
import kotlin.test.assertEquals

class UsageApiClientTest {
    private val now = Instant.parse("2026-11-24T10:00:00Z")

    private fun client(transport: FakeTransport, token: String? = "token") =
        UsageApiClient(transport, { token }, userAgent = { "claude-cli/2.1.328 (external, cli)" }, clock = { now })

    @Test
    fun `reads session and week from the usage response`() = runTest {
        val transport = FakeTransport(
            HttpResponse(
                200, emptyMap(),
                """
                {
                  "five_hour": { "utilization": 42.6, "resets_at": "2026-11-24T12:30:00+00:00" },
                  "seven_day": { "utilization": 17.0, "resets_at": "2026-11-30T09:00:00+00:00" }
                }
                """,
            ),
        )

        val result = client(transport).fetch("account-1")

        assertEquals(UsageFetchStatus.Ok, result.status)
        val snapshot = result.snapshot!!
        assertEquals(43, snapshot.sessionPercent)
        assertEquals(17, snapshot.weeklyPercent)
        assertEquals(42.6, snapshot.sessionUtilization)
        assertEquals(Instant.parse("2026-11-24T12:30:00Z"), snapshot.sessionResetsAt)
        assertEquals(Instant.parse("2026-11-30T09:00:00Z"), snapshot.weeklyResetsAt)
        assertEquals(now, snapshot.fetchedAt)

        val request = transport.requests.single()
        assertEquals("https://api.anthropic.com/api/oauth/usage?cedar_ember=1", request.url)
        assertEquals("Bearer token", request.headers["Authorization"])
        assertEquals("claude-cli/2.1.328 (external, cli)", request.headers["User-Agent"])
    }

    @Test
    fun `reads additional limits, skipping the unscoped session and week`() = runTest {
        val transport = FakeTransport(
            ok(
                """
                {
                  "five_hour": { "utilization": 10, "resets_at": null },
                  "seven_day": { "utilization": 20, "resets_at": "2026-11-30T09:00:00Z" },
                  "limits": [
                    { "kind": "session", "group": "session", "percent": 10 },
                    { "kind": "weekly_all", "group": "weekly", "percent": 20 },
                    { "kind": "weekly_scoped", "group": "weekly", "percent": 41.4, "resets_at": "2026-11-30T09:00:00Z",
                      "scope": { "model": { "id": null, "display_name": "Opus" }, "surface": null } },
                    { "kind": "weekly_scoped", "group": "weekly", "percent": 5, "scope": { "surface": "claude_code" } },
                    { "kind": "weekly_extra", "group": "weekly", "percent": 3 },
                    { "kind": "broken" },
                    "not an object"
                  ],
                  "weekly_scoped_shares": [ { "limit_index": 2, "allowance_percent_of_weekly": 50 } ]
                }
                """,
            ),
        )

        val limits = client(transport).fetch("account-1").snapshot!!.additionalLimits

        assertEquals(
            listOf(
                UsageLimitInfo("weekly_scoped:opus", "weekly", "Opus", 41.4, Instant.parse("2026-11-30T09:00:00Z"), 50.0),
                UsageLimitInfo("weekly_scoped:claude code", "weekly", "Claude Code", 5.0, null),
                UsageLimitInfo("weekly_extra:", "weekly", "Extra", 3.0, null),
            ),
            limits,
        )
    }

    @Test
    fun `reads enabled credits as amounts, and ignores disabled ones`() = runTest {
        val spend = """{ "used": { "amount_minor": 1234, "currency": "EUR", "exponent": 2 },
                         "limit": { "amount_minor": 5000, "currency": "EUR", "exponent": 2 }"""
        val enabled = client(FakeTransport(ok("""{ "spend": $spend, "enabled": true } }"""))).fetch("a").snapshot!!
        val disabled = client(FakeTransport(ok("""{ "spend": $spend, "enabled": false } }"""))).fetch("a").snapshot!!

        assertEquals(BigDecimal("12.34"), enabled.creditUsed)
        assertEquals(BigDecimal("50.00"), enabled.creditLimit)
        assertEquals("EUR", enabled.creditCurrency)
        assertEquals(25, enabled.creditPercent)
        assertEquals(null, disabled.creditUsed)
        assertEquals(null, disabled.creditPercent)
    }

    @Test
    fun `reads limit resets, deriving the kind from type, gives_back_weekly or clears`() = runTest {
        val body = """
            { "cedar_ember": { "eligible": true, "grants": [
                { "id": "g1", "resets_left": 1, "resets_total": 2, "type": "weekly",
                  "starts_at": "2026-11-20T00:00:00Z", "ends_at": "2026-11-29T23:59:00Z", "paused": false, "use_requires_limit": true },
                { "id": "g2", "resets_left": 1, "type": "weekly", "gives_back_weekly": true },
                { "id": "g3", "resets_left": 3, "clears": ["five_hour"] },
                { "id": "g4", "resets_left": 1, "clears": ["five_hour", "seven_day"], "paused": true },
                { "id": "g5", "resets_left": 1, "clears": ["seven_day"], "use_requires_limit": false },
                { "resets_left": 1 },
                { "id": "g6", "resets_left": -1 }
            ] } }
        """

        val status = client(FakeTransport(ok(body))).fetch("a").snapshot!!.limitResets!!

        assertEquals(true, status.eligible)
        assertEquals(
            listOf(
                LimitResetGrant("g1", LimitResetKind.Weekly, 1, 2, Instant.parse("2026-11-20T00:00:00Z"), Instant.parse("2026-11-29T23:59:00Z"), paused = false, useRequiresLimit = true),
                LimitResetGrant("g2", LimitResetKind.Model, 1, 1, null, null, paused = false, useRequiresLimit = true),
                LimitResetGrant("g3", LimitResetKind.Session, 3, 3, null, null, paused = false, useRequiresLimit = true),
                LimitResetGrant("g4", LimitResetKind.Full, 1, 1, null, null, paused = true, useRequiresLimit = true),
                LimitResetGrant("g5", LimitResetKind.Weekly, 1, 1, null, null, paused = false, useRequiresLimit = false),
            ),
            status.grants,
        )
        // g4 is paused, so no full reset is offered; the two weekly grants are summed.
        assertEquals(
            listOf(
                LimitResetOffer(LimitResetKind.Session, 3, null),
                LimitResetOffer(LimitResetKind.Weekly, 2, Instant.parse("2026-11-29T23:59:00Z")),
                LimitResetOffer(LimitResetKind.Model, 1, null),
            ),
            status.offers(now),
        )
    }

    @Test
    fun `has no limit reset status without a readable cedar_ember block`() = runTest {
        assertEquals(null, client(FakeTransport(ok("""{ "five_hour": null }"""))).fetch("a").snapshot!!.limitResets)
        assertEquals(null, client(FakeTransport(ok("""{ "cedar_ember": { "grants": [] } }"""))).fetch("a").snapshot!!.limitResets)
    }

    @Test
    fun `is not logged in without a token, and asks nothing`() = runTest {
        val transport = FakeTransport(ok("{}"))

        val result = client(transport, token = null).fetch("a")

        assertEquals(UsageFetchStatus.NotLoggedIn, result.status)
        assertEquals(emptyList(), transport.requests)
    }

    @Test
    fun `maps 401 to an expired login`() = runTest {
        val result = client(FakeTransport(HttpResponse(401, emptyMap(), ""))).fetch("a")

        assertEquals(UsageFetchStatus.AuthExpired, result.status)
    }

    @Test
    fun `maps 429 to rate limited, with Retry-After in seconds or as a date`() = runTest {
        val seconds = client(FakeTransport(HttpResponse(429, mapOf("retry-after" to "120"), ""))).fetch("a")
        val date = client(FakeTransport(HttpResponse(429, mapOf("retry-after" to "Tue, 24 Nov 2026 10:30:00 GMT"), ""))).fetch("a")
        val none = client(FakeTransport(HttpResponse(429, emptyMap(), ""))).fetch("a")

        assertEquals(UsageFetchResult(UsageFetchStatus.NetworkError, null, FetchError.Http(429), rateLimited = true, retryAfter = Duration.ofMinutes(2)), seconds)
        assertEquals(Duration.ofMinutes(30), date.retryAfter)
        assertEquals(true, none.rateLimited)
        assertEquals(null, none.retryAfter)
    }

    @Test
    fun `maps other failures to network errors with their cause`() = runTest {
        suspend fun errorOf(transport: FakeTransport) = client(transport).fetch("a").also {
            assertEquals(UsageFetchStatus.NetworkError, it.status)
        }.error

        assertEquals(FetchError.Http(503), errorOf(FakeTransport(HttpResponse(503, emptyMap(), "busy"))))
        assertEquals(FetchError.NoInternet, errorOf(FakeTransport(failure = UnknownHostException("api.anthropic.com"))))
        assertEquals(FetchError.NoInternet, errorOf(FakeTransport(failure = ConnectException("refused"))))
        assertEquals(FetchError.Timeout, errorOf(FakeTransport(failure = SocketTimeoutException("read timed out"))))
        assertEquals(FetchError.Other("TLS failed"), errorOf(FakeTransport(failure = IOException("TLS failed"))))
        assertEquals(FetchError.EmptyResponse, errorOf(FakeTransport(ok("null"))))
        assert(errorOf(FakeTransport(ok("<html>"))) is FetchError.UnexpectedFormat)
    }

    @Test
    fun `reads the profile as ID and label, preferring the UUID and the display name`() = runTest {
        suspend fun infoOf(account: String) =
            client(FakeTransport(ok("""{ "account": $account }"""))).fetchAccountWithToken("t")

        assertEquals(
            AccountInfo("u-1", "Ada (ada@example.com)"),
            infoOf("""{ "uuid": "u-1", "display_name": "Ada", "full_name": "Ada Lovelace", "email": "ada@example.com" }"""),
        )
        assertEquals(AccountInfo("ada@example.com", "Ada Lovelace (ada@example.com)"), infoOf("""{ "full_name": "Ada Lovelace", "email": "ada@example.com" }"""))
        assertEquals(AccountInfo("u-1", "Ada"), infoOf("""{ "uuid": "u-1", "display_name": "Ada", "email": "" }"""))
        assertEquals(AccountInfo(null, null), infoOf("{}"))
    }

    @Test
    fun `has no profile on any failure`() = runTest {
        assertEquals(null, client(FakeTransport(HttpResponse(500, emptyMap(), ""))).fetchAccountWithToken("t"))
        assertEquals(null, client(FakeTransport(ok("{}"))).fetchAccountWithToken("t"))
        assertEquals(null, client(FakeTransport(ok("nope"))).fetchAccountWithToken("t"))
        assertEquals(null, client(FakeTransport(failure = UnknownHostException())).fetchAccountWithToken("t"))
        assertEquals(null, client(FakeTransport(ok("{}")), token = null).fetchAccount("a"))
    }

    @Test
    fun `rounds halves to even, as the Windows app does`() = runTest {
        val spend = """{ "used": { "amount_minor": 125, "currency": "EUR", "exponent": 2 },
                         "limit": { "amount_minor": 1000, "currency": "EUR", "exponent": 2 }, "enabled": true }"""
        val snapshot = client(FakeTransport(ok("""{ "five_hour": { "utilization": 12.5 }, "seven_day": { "utilization": 13.5 }, "spend": $spend }""")))
            .fetch("a").snapshot!!

        assertEquals(12, snapshot.sessionPercent)
        assertEquals(14, snapshot.weeklyPercent)
        assertEquals(12, snapshot.creditPercent)
    }

    private fun ok(body: String) = HttpResponse(200, emptyMap(), body)
}

/** Answers every request with the next of [responses] (the last one repeats), or throws [failure]. */
class FakeTransport(vararg responses: HttpResponse, private val failure: Exception? = null) : HttpTransport {
    private val queue = ArrayDeque(responses.toList())
    val requests = mutableListOf<HttpRequest>()

    override suspend fun execute(request: HttpRequest): HttpResponse {
        requests += request
        failure?.let { throw it }
        return if (queue.size > 1) queue.removeFirst() else queue.first()
    }
}
