package eu.schicht8.aiquota.core

import java.time.Instant

/** A usage limit beyond the 5-hour session and the overall week - e.g. a weekly limit for
 * one model - from the usage response's `limits` list (#15).
 * [key]: stable identifier (kind plus scope), never shown. [group]: the window it belongs to,
 * e.g. "session" or "weekly". [name]: what it's scoped to, e.g. "Opus"; null if nothing.
 * [allowanceShareOfWeekly]: the share of the weekly allowance it may use, in percent. */
data class UsageLimitInfo(
    val key: String,
    val group: String,
    val name: String?,
    val percent: Double,
    val resetsAt: Instant?,
    val allowanceShareOfWeekly: Double? = null,
) {
    val roundedPercent: Int get() = percent.coerceIn(0.0, 100.0).roundHalfEven()
    val isSessionGroup: Boolean get() = group.equals("session", ignoreCase = true)
}

/** What a limit reset refills (#21), named as Claude Code names it; declared in display order. */
enum class LimitResetKind { Session, Weekly, Model, Full }

/** One grant of limit resets the account was offered. */
data class LimitResetGrant(
    val id: String,
    val kind: LimitResetKind,
    val resetsLeft: Int,
    val resetsTotal: Int,
    val startsAt: Instant?,
    val endsAt: Instant?,
    val paused: Boolean,
    val useRequiresLimit: Boolean,
) {
    /** Has resets left that can be used now or once a limit is reached. */
    fun isAvailable(now: Instant): Boolean =
        resetsLeft > 0 && !paused &&
            (startsAt == null || startsAt <= now) &&
            (endsAt == null || endsAt > now)
}

/** The available resets of one kind, summed over its grants. */
data class LimitResetOffer(val kind: LimitResetKind, val count: Int, val firstEndsAt: Instant?) {
    val isSessionKind: Boolean get() = kind == LimitResetKind.Session
}

/** The account's limit reset status (the response's `cedar_ember` block). */
data class LimitResetStatus(val eligible: Boolean, val grants: List<LimitResetGrant>) {
    /** The available resets per kind, in the order session, weekly, model, full. */
    fun offers(now: Instant): List<LimitResetOffer> =
        if (!eligible) emptyList()
        else grants.filter { it.isAvailable(now) }
            .groupBy { it.kind }
            .toSortedMap()
            .map { (kind, list) -> LimitResetOffer(kind, list.sumOf { it.resetsLeft }, list.mapNotNull { it.endsAt }.minOrNull()) }
}
