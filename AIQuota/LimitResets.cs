using System.Globalization;
using System.Text.Json;

namespace AIQuota;

/// <summary>What a limit reset refills (#21), named as Claude Code names it.</summary>
public enum LimitResetKind
{
    /// <summary>The 5-hour session limit ("5-hour reset").</summary>
    Session,

    /// <summary>The weekly limit ("weekly reset").</summary>
    Weekly,

    /// <summary>Gives back to the weekly limit what one model used this week - Claude Code
    /// calls it "Fable reset" (<c>gives_back_weekly</c>).</summary>
    Model,

    /// <summary>The session and the weekly limit ("full reset").</summary>
    Full,
}

/// <summary>One grant of limit resets the account was offered: how many of how many are
/// left, when they can be used, and what they refill.</summary>
/// <param name="Id">The grant's ID - keys which grants have been notified about.</param>
/// <param name="UseRequiresLimit">The reset can only be used once a limit is reached.</param>
public sealed record LimitResetGrant(
    string Id,
    LimitResetKind Kind,
    int ResetsLeft,
    int ResetsTotal,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    bool Paused,
    bool UseRequiresLimit)
{
    /// <summary>Has resets left that can be used now or once a limit is reached: not paused,
    /// already started and not yet ended.</summary>
    public bool IsAvailable(DateTimeOffset now) =>
        ResetsLeft > 0 && !Paused
        && (StartsAt is not { } start || start <= now)
        && (EndsAt is not { } end || end > now);

    /// <summary>The session section shows 5-hour resets, the week section all others.</summary>
    public bool IsSessionKind => Kind == LimitResetKind.Session;
}

/// <summary>The available resets of one kind, summed over its grants: how many are left and
/// when the first of them ends (null if none of them ends).</summary>
public sealed record LimitResetOffer(LimitResetKind Kind, int Count, DateTimeOffset? FirstEndsAt)
{
    public bool IsSessionKind => Kind == LimitResetKind.Session;
}

/// <summary>The account's limit reset status (the usage response's <c>cedar_ember</c>
/// block): whether it can get resets at all, and its grants.</summary>
public sealed record LimitResetStatus(bool Eligible, IReadOnlyList<LimitResetGrant> Grants)
{
    /// <summary>The grants with resets that can be used, if the account is eligible.</summary>
    public IEnumerable<LimitResetGrant> AvailableGrants(DateTimeOffset now) =>
        Eligible ? Grants.Where(g => g.IsAvailable(now)) : [];

    /// <summary>The available resets per kind, in the order session, weekly, model, full.</summary>
    public IReadOnlyList<LimitResetOffer> Offers(DateTimeOffset now) =>
        AvailableGrants(now)
            .GroupBy(g => g.Kind)
            .OrderBy(group => group.Key)
            .Select(group => new LimitResetOffer(group.Key, group.Sum(g => g.ResetsLeft),
                group.Where(g => g.EndsAt is not null).Min(g => g.EndsAt)))
            .ToList();
}

/// <summary>
/// Reads the limit reset status from the usage response's <c>cedar_ember</c> block, which the
/// endpoint only adds when asked with <c>?cedar_ember=1</c> - the same request and the same
/// rules Claude Code uses for its <c>/limit-reset</c> command (as of v2.1.325):
/// <code>"cedar_ember": { "eligible": true, "grants": [ { "id": "…", "label": "…",
///   "resets_total": 2, "resets_left": 1, "starts_at": "…", "ends_at": "…",
///   "clears": ["seven_day"], "paused": false, "usable_now": false,
///   "use_requires_limit": true, "type": "weekly", "gives_back_weekly": false } ],
///   "next_grant_id": "…", "weekly_resets_at": "…", "cooldown_until": null }</code>
/// A grant's kind: "model" if it gives back to the weekly limit, else its <c>type</c>
/// (session / weekly / full), else derived from <c>clears</c> (only <c>five_hour</c> →
/// session, only others → weekly, both → full).
///
/// Tolerant by design: a missing or malformed block means "no status" (null), and a
/// malformed grant is skipped - neither can fail the whole fetch.
/// </summary>
internal static class LimitResetParser
{
    public static LimitResetStatus? Parse(JsonElement? block)
    {
        if (block is not { ValueKind: JsonValueKind.Object } status)
            return null;
        if (!status.TryGetProperty("eligible", out var eligible) || eligible.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return null;

        var grants = new List<LimitResetGrant>();
        if (status.TryGetProperty("grants", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in list.EnumerateArray())
            {
                if (ParseGrant(entry) is { } grant)
                    grants.Add(grant);
            }
        }
        return new LimitResetStatus(eligible.GetBoolean(), grants);
    }

    private static LimitResetGrant? ParseGrant(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object)
            return null;
        if (GetString(entry, "id") is not { Length: > 0 } id || GetInt(entry, "resets_left") is not { } left)
            return null;

        return new LimitResetGrant(
            id,
            KindOf(entry),
            left,
            GetInt(entry, "resets_total") ?? left,
            GetDate(entry, "starts_at"),
            GetDate(entry, "ends_at"),
            GetBool(entry, "paused") ?? false,
            GetBool(entry, "use_requires_limit") ?? true);
    }

    private static LimitResetKind KindOf(JsonElement entry)
    {
        if (GetBool(entry, "gives_back_weekly") == true)
            return LimitResetKind.Model;

        switch (GetString(entry, "type")?.ToLowerInvariant())
        {
            case "session":
                return LimitResetKind.Session;
            case "weekly":
                return LimitResetKind.Weekly;
            case "full":
                return LimitResetKind.Full;
        }

        var clears = entry.TryGetProperty("clears", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()!).ToList()
            : [];
        var session = clears.Contains("five_hour");
        var other = clears.Any(c => c != "five_hour");
        return session && !other ? LimitResetKind.Session
            : other && !session ? LimitResetKind.Weekly
            : LimitResetKind.Full;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number) && number >= 0
            ? number
            : null;

    private static bool? GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static DateTimeOffset? GetDate(JsonElement element, string name) =>
        GetString(element, name) is { } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date)
            ? date
            : null;
}
