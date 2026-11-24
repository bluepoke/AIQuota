package eu.schicht8.aiquota.core

import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject

/**
 * Reads the limit reset status (#21) from the usage response's `cedar_ember` block - a port
 * of LimitResetParser in AIQuota/LimitResets.cs. A grant's kind: "model" if it gives back to
 * the weekly limit, else its `type` (session / weekly / full), else derived from `clears`
 * (only `five_hour` → session, only others → weekly, both → full). A missing or malformed
 * block is "no status" (null); a malformed grant is skipped.
 */
internal object LimitResetParser {
    fun parse(block: JsonElement?): LimitResetStatus? {
        val status = block as? JsonObject ?: return null
        val eligible = status.bool("eligible") ?: return null
        val grants = (status["grants"] as? JsonArray).orEmpty().mapNotNull { parseGrant(it) }
        return LimitResetStatus(eligible, grants)
    }

    private fun parseGrant(element: JsonElement): LimitResetGrant? {
        val entry = element as? JsonObject ?: return null
        val id = entry.string("id")?.takeIf { it.isNotEmpty() } ?: return null
        val left = entry.count("resets_left") ?: return null
        return LimitResetGrant(
            id = id,
            kind = kindOf(entry),
            resetsLeft = left,
            resetsTotal = entry.count("resets_total") ?: left,
            startsAt = entry.instant("starts_at"),
            endsAt = entry.instant("ends_at"),
            paused = entry.bool("paused") ?: false,
            useRequiresLimit = entry.bool("use_requires_limit") ?: true,
        )
    }

    private fun kindOf(entry: JsonObject): LimitResetKind {
        if (entry.bool("gives_back_weekly") == true) return LimitResetKind.Model
        when (entry.string("type")?.lowercase()) {
            "session" -> return LimitResetKind.Session
            "weekly" -> return LimitResetKind.Weekly
            "full" -> return LimitResetKind.Full
        }
        val clears = (entry["clears"] as? JsonArray).orEmpty().mapNotNull { it.stringValue() }
        val session = "five_hour" in clears
        val other = clears.any { it != "five_hour" }
        return when {
            session && !other -> LimitResetKind.Session
            other && !session -> LimitResetKind.Weekly
            else -> LimitResetKind.Full
        }
    }

    /** A non-negative whole number that fits an Int; null otherwise. */
    private fun JsonObject.count(name: String): Int? =
        long(name)?.takeIf { it in 0..Int.MAX_VALUE }?.toInt()
}
