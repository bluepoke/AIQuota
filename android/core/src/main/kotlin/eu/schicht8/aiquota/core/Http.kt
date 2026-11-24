package eu.schicht8.aiquota.core

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URI

data class HttpRequest(
    val method: String,
    val url: String,
    val headers: Map<String, String> = emptyMap(),
    val body: String? = null,
)

/** [headers] are keyed by lower-case name. */
data class HttpResponse(val status: Int, val headers: Map<String, String>, val body: String) {
    val isSuccess: Boolean get() = status in 200..299
}

/** Sends one HTTP request. Throws an [IOException] when no response arrives (no network,
 * timeout, TLS failure) - an HTTP error status is a normal [HttpResponse]. */
fun interface HttpTransport {
    suspend fun execute(request: HttpRequest): HttpResponse
}

/** [HttpTransport] on [HttpURLConnection] - on Android and the JVM alike, without extra
 * dependencies. */
class UrlConnectionTransport(
    private val connectTimeoutMillis: Int = 15_000,
    private val readTimeoutMillis: Int = 15_000,
) : HttpTransport {
    override suspend fun execute(request: HttpRequest): HttpResponse = withContext(Dispatchers.IO) {
        val connection = URI(request.url).toURL().openConnection() as HttpURLConnection
        try {
            connection.requestMethod = request.method
            connection.connectTimeout = connectTimeoutMillis
            connection.readTimeout = readTimeoutMillis
            connection.instanceFollowRedirects = false
            request.headers.forEach { (name, value) -> connection.setRequestProperty(name, value) }
            if (request.body != null) {
                connection.doOutput = true
                connection.outputStream.use { it.write(request.body.toByteArray(Charsets.UTF_8)) }
            }
            val status = connection.responseCode
            val stream = if (status >= 400) connection.errorStream else connection.inputStream
            val body = stream?.use { it.readBytes().toString(Charsets.UTF_8) } ?: ""
            val headers = connection.headerFields
                .filterKeys { it != null }
                .mapKeys { it.key.lowercase() }
                .mapValues { it.value.joinToString(",") }
            HttpResponse(status, headers, body)
        } finally {
            connection.disconnect()
        }
    }
}
