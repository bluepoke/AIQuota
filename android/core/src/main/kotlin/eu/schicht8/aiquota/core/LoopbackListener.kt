package eu.schicht8.aiquota.core

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.BufferedReader
import java.io.Closeable
import java.io.IOException
import java.io.InputStreamReader
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.SocketTimeoutException
import java.net.URLDecoder

data class AuthCodeResult(val code: String?, val state: String?, val error: String?)

/**
 * Minimal HTTP listener on 127.0.0.1 for the OAuth loopback redirect - a port of
 * AIQuota/Auth/LoopbackListener.cs. Requests other than `/callback` (a browser's favicon
 * request, a preconnect that never sends anything) are answered or dropped, and waiting
 * continues.
 *
 * [returnUrl]: a link on the result page that brings the user back to the app (on Android
 * the page is shown in a Custom Tab in front of it); null shows none.
 */
class LoopbackListener private constructor(private val server: ServerSocket, val port: Int) : Closeable {
    /** Waits for the redirect; cancelling closes the socket. */
    suspend fun waitForRedirect(strings: Strings, returnUrl: String?): AuthCodeResult = coroutineScope {
        val closer = launch {
            try {
                awaitCancellation()
            } finally {
                server.close()
            }
        }
        try {
            withContext(Dispatchers.IO) {
                var result: AuthCodeResult? = null
                while (result == null) {
                    result = server.accept().use { handle(it, strings, returnUrl) }
                }
                result
            }
        } finally {
            closer.cancel()
        }
    }

    private fun handle(socket: Socket, strings: Strings, returnUrl: String?): AuthCodeResult? {
        socket.soTimeout = 5_000
        val requestLine = try {
            val reader = BufferedReader(InputStreamReader(socket.getInputStream(), Charsets.US_ASCII))
            val line = reader.readLine() ?: return null
            // Drain the headers so the browser doesn't see a connection reset.
            var header = reader.readLine()
            while (!header.isNullOrEmpty()) header = reader.readLine()
            line
        } catch (_: SocketTimeoutException) {
            return null
        } catch (_: IOException) {
            return null
        }

        // "GET /callback?code=...&state=... HTTP/1.1"
        val target = requestLine.split(' ').getOrNull(1).orEmpty()
        val path = target.substringBefore('?')
        if (path != "/callback") {
            respond(socket, 404, "")
            return null
        }
        val query = parseQuery(target.substringAfter('?', ""))
        val result = AuthCodeResult(query["code"], query["state"], query["error"])
        respond(socket, 200, page(result, strings, returnUrl))
        return result
    }

    private fun page(result: AuthCodeResult, strings: Strings, returnUrl: String?): String {
        val title = if (result.error == null) strings.loopbackSuccessTitle else strings.loopbackFailureTitle
        val body = result.error ?: strings.loopbackSuccessBody
        val link = returnUrl?.let {
            "<p><a href='${html(it)}' style='display:inline-block;padding:12px 20px;border-radius:20px;background:#1a73e8;color:#fff;text-decoration:none'>${html(strings.loopbackReturnToApp)}</a></p>"
        } ?: ""
        return "<html><head><meta name='viewport' content='width=device-width,initial-scale=1'></head>" +
            "<body style='font-family:sans-serif;margin:24px'><h2>${html(title)}</h2><p>${html(body)}</p>$link</body></html>"
    }

    private fun respond(socket: Socket, status: Int, body: String) {
        try {
            val bytes = body.toByteArray(Charsets.UTF_8)
            val reason = if (status == 200) "OK" else "Not Found"
            val head = "HTTP/1.1 $status $reason\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: ${bytes.size}\r\nConnection: close\r\n\r\n"
            socket.getOutputStream().apply {
                write(head.toByteArray(Charsets.US_ASCII))
                write(bytes)
                flush()
            }
        } catch (_: IOException) {
            // The browser went away; the result counts anyway.
        }
    }

    override fun close() = server.close()

    companion object {
        /** Binds the first free port of [ports]; throws [LoginError.NoPort] if none is. */
        fun bindFirstAvailable(ports: List<Int> = OAuthConfig.LOOPBACK_PORTS): LoopbackListener {
            for (port in ports) {
                try {
                    return LoopbackListener(ServerSocket(port, 1, InetAddress.getLoopbackAddress()), port)
                } catch (_: IOException) {
                    // In use - try the next one.
                }
            }
            throw LoginError.NoPort
        }

        private fun parseQuery(query: String): Map<String, String> =
            query.split('&').filter { it.isNotEmpty() }.associate { pair ->
                val key = pair.substringBefore('=')
                val value = pair.substringAfter('=', "")
                // The String overload: the Charset one exists on Android only from API 33.
                URLDecoder.decode(key, "UTF-8") to URLDecoder.decode(value, "UTF-8")
            }

        private fun html(text: String) = text
            .replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace("'", "&#39;").replace("\"", "&quot;")
    }
}
