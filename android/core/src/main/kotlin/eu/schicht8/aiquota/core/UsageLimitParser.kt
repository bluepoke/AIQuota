package eu.schicht8.aiquota.core

import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject

/**
 * Reads the additional limits from the usage response's `limits` list - a port of
 * UsageLimitParser in AIQuota/UsageLimits.cs. Everything except the unscoped session and
 * overall weekly limit (shown by their own bars) is returned. Tolerant: entries or fields of
 * an unexpected shape are skipped.
 */
internal object UsageLimitParser {
    /** Limits already shown as the session and week bars (when unscoped). */
    private val primaryKinds = setOf("session", "weekly_all")

    fun parseAdditional(limits: JsonElement?, weeklyScopedShares: JsonElement?): List<UsageLimitInfo> {
        val list = limits as? JsonArray ?: return emptyList()
        val shares = parseShares(weeklyScopedShares)
        return list.mapIndexedNotNull { index, element ->
            val entry = element as? JsonObject ?: return@mapIndexedNotNull null
            val kind = entry.string("kind")?.takeIf { it.isNotBlank() } ?: return@mapIndexedNotNull null
            val percent = entry.number("percent") ?: return@mapIndexedNotNull null

            val scopeName = scopeName(entry)
            if (scopeName == null && kind.lowercase() in primaryKinds) return@mapIndexedNotNull null

            val group = entry.string("group")?.takeIf { it.isNotEmpty() } ?: kind
            UsageLimitInfo(
                key = "$kind:${scopeName ?: ""}".lowercase(),
                group = group,
                name = scopeName ?: nameFromKind(kind, group),
                percent = percent,
                resetsAt = entry.instant("resets_at"),
                allowanceShareOfWeekly = shares[index],
            )
        }
    }

    /** The display name of what a limit is scoped to: a model, else a surface (an object
     * with a display name, or a plain string). */
    private fun scopeName(entry: JsonObject): String? {
        val scope = entry.obj("scope") ?: return null
        for (part in listOf("model", "surface")) {
            when (val value = scope[part]) {
                is JsonObject -> {
                    val name = value.string("display_name") ?: value.string("name") ?: value.string("id")
                    if (!name.isNullOrBlank()) return name.trim()
                }
                else -> value?.stringValue()?.takeIf { it.isNotEmpty() }?.let { return titleCase(it) }
            }
        }
        return null
    }

    /** "weekly_extra" in group "weekly" → "Extra"; null when the kind is just the group. */
    private fun nameFromKind(kind: String, group: String): String? {
        val rest = if (kind.startsWith(group + "_", ignoreCase = true)) kind.substring(group.length + 1) else kind
        return if (rest.isEmpty() || rest.equals(group, ignoreCase = true)) null else titleCase(rest)
    }

    /** `weekly_scoped_shares`: per limit (by index in `limits`), its share of the weekly allowance. */
    private fun parseShares(shares: JsonElement?): Map<Int, Double> {
        val list = shares as? JsonArray ?: return emptyMap()
        return list.mapNotNull { element ->
            val entry = element as? JsonObject ?: return@mapNotNull null
            val index = entry.number("limit_index") ?: return@mapNotNull null
            val allowance = entry.number("allowance_percent_of_weekly")?.takeIf { it > 0 } ?: return@mapNotNull null
            index.toInt() to allowance
        }.toMap()
    }

    /** Like .NET's TextInfo.ToTitleCase on lower-cased text: "claude_code" → "Claude Code". */
    fun titleCase(text: String): String =
        text.replace('_', ' ').replace('-', ' ').trim().lowercase()
            .split(' ')
            .joinToString(" ") { word -> word.replaceFirstChar { it.uppercaseChar() } }
}
