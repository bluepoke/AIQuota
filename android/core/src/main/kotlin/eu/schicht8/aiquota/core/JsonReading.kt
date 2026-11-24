package eu.schicht8.aiquota.core

import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.doubleOrNull
import kotlinx.serialization.json.longOrNull
import java.time.Instant
import java.time.OffsetDateTime
import java.time.format.DateTimeParseException

// Tolerant readers for API responses, like the Windows app's JsonElement helpers: a field
// of an unexpected shape reads as null instead of failing the whole response.

internal fun JsonObject.obj(name: String): JsonObject? = this[name] as? JsonObject

internal fun JsonObject.array(name: String): JsonArray? = this[name] as? JsonArray

internal fun JsonObject.string(name: String): String? =
    (this[name] as? JsonPrimitive)?.takeIf { it.isString }?.content

internal fun JsonObject.number(name: String): Double? =
    (this[name] as? JsonPrimitive)?.takeIf { !it.isString }?.doubleOrNull

internal fun JsonObject.long(name: String): Long? =
    (this[name] as? JsonPrimitive)?.takeIf { !it.isString }?.longOrNull

internal fun JsonObject.bool(name: String): Boolean? =
    (this[name] as? JsonPrimitive)?.takeIf { !it.isString }?.booleanOrNull

internal fun JsonObject.instant(name: String): Instant? = string(name)?.let(::parseInstant)

internal fun JsonElement.stringValue(): String? = (this as? JsonPrimitive)?.takeIf { it.isString }?.content

/** ISO-8601 with an offset ("2026-11-24T12:30:00+00:00", "…Z", with fractions); null if unreadable. */
internal fun parseInstant(text: String): Instant? = try {
    OffsetDateTime.parse(text).toInstant()
} catch (_: DateTimeParseException) {
    try {
        Instant.parse(text)
    } catch (_: DateTimeParseException) {
        null
    }
}
