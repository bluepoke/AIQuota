using System.Globalization;
using System.Text.Json;

namespace AIQuota;

/// <summary>A usage limit beyond the 5-hour session and the overall week - e.g. a weekly
/// limit for one model - as reported in the usage response's <c>limits</c> list.</summary>
/// <param name="Key">Stable identifier (kind plus scope), for menu items and notification
/// tags; never shown.</param>
/// <param name="Group">The window it belongs to, e.g. "session" or "weekly" - picks the
/// label prefix and where it's shown.</param>
/// <param name="Name">What it's scoped to, e.g. a model's display name ("Opus"); null for a
/// limit that has no scope and no recognisable name.</param>
/// <param name="AllowanceShareOfWeekly">How much of the overall weekly allowance this limit
/// may use at most, in percent (e.g. 50), when the response says so.</param>
public sealed record UsageLimitInfo(
    string Key,
    string Group,
    string? Name,
    double Percent,
    DateTimeOffset? ResetsAt,
    double? AllowanceShareOfWeekly = null)
{
    public int RoundedPercent => (int)Math.Round(Math.Clamp(Percent, 0, 100));

    public bool IsSessionGroup => string.Equals(Group, "session", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Reads the additional limits from the usage response's <c>limits</c> list, which describes
/// every limit of the account in one structured form, e.g.
/// <code>{ "kind": "weekly_scoped", "group": "weekly", "percent": 41, "resets_at": "…",
///   "scope": { "model": { "id": null, "display_name": "Opus" }, "surface": null } }</code>
/// Which limits exist isn't hard-coded: everything except the unscoped session and overall
/// weekly limit (shown by the existing bars) is returned. The top-level per-model fields of
/// the response (<c>seven_day_opus</c> and many codenamed ones) are deliberately ignored -
/// they carry no display names.
///
/// Tolerant by design: entries or fields of an unexpected shape are skipped, so a change in
/// the response never fails the whole fetch.
/// </summary>
internal static class UsageLimitParser
{
    /// <summary>Limits already shown as the session and week bars (when unscoped).</summary>
    private static readonly HashSet<string> PrimaryKinds = new(StringComparer.OrdinalIgnoreCase) { "session", "weekly_all" };

    public static IReadOnlyList<UsageLimitInfo> ParseAdditional(JsonElement? limits, JsonElement? weeklyScopedShares)
    {
        if (limits is not { ValueKind: JsonValueKind.Array } list)
            return [];

        var shares = ParseShares(weeklyScopedShares);
        var result = new List<UsageLimitInfo>();
        var index = 0;
        foreach (var entry in list.EnumerateArray())
        {
            var entryIndex = index++;
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            var kind = GetString(entry, "kind");
            if (string.IsNullOrWhiteSpace(kind) || GetNumber(entry, "percent") is not { } percent)
                continue;

            var scopeName = ScopeName(entry);
            if (scopeName is null && PrimaryKinds.Contains(kind))
                continue;

            var group = GetString(entry, "group") is { Length: > 0 } g ? g : kind;
            var name = scopeName ?? NameFromKind(kind, group);
            var key = $"{kind}:{scopeName ?? ""}".ToLowerInvariant();
            result.Add(new UsageLimitInfo(key, group, name, percent, GetDate(entry, "resets_at"),
                shares.TryGetValue(entryIndex, out var share) ? share : null));
        }
        return result;
    }

    /// <summary>The display name of what a limit is scoped to: a model, else a surface
    /// (given as an object with a display name, or as a plain string).</summary>
    private static string? ScopeName(JsonElement entry)
    {
        if (!entry.TryGetProperty("scope", out var scope) || scope.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var part in new[] { "model", "surface" })
        {
            if (!scope.TryGetProperty(part, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.Object)
            {
                var name = GetString(value, "display_name") ?? GetString(value, "name") ?? GetString(value, "id");
                if (!string.IsNullOrWhiteSpace(name))
                    return name.Trim();
            }
            else if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
            {
                return TitleCase(text);
            }
        }
        return null;
    }

    /// <summary>A readable name from an unscoped kind, e.g. "weekly_extra" in group "weekly"
    /// → "Extra"; null when the kind is just the group itself.</summary>
    private static string? NameFromKind(string kind, string group)
    {
        var rest = kind.StartsWith(group + "_", StringComparison.OrdinalIgnoreCase) ? kind[(group.Length + 1)..] : kind;
        return string.Equals(rest, group, StringComparison.OrdinalIgnoreCase) || rest.Length == 0 ? null : TitleCase(rest);
    }

    /// <summary><c>weekly_scoped_shares</c>: per limit (by its index in <c>limits</c>), the
    /// share of the weekly allowance it may use.</summary>
    private static Dictionary<int, double> ParseShares(JsonElement? shares)
    {
        var result = new Dictionary<int, double>();
        if (shares is not { ValueKind: JsonValueKind.Array } list)
            return result;

        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object
                && GetNumber(entry, "limit_index") is { } index
                && GetNumber(entry, "allowance_percent_of_weekly") is { } allowance and > 0)
            {
                result[(int)index] = allowance;
            }
        }
        return result;
    }

    public static string TitleCase(string text) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.Replace('_', ' ').Replace('-', ' ').Trim().ToLowerInvariant());

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? GetNumber(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : null;

    private static DateTimeOffset? GetDate(JsonElement element, string name) =>
        GetString(element, name) is { } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date)
            ? date
            : null;
}
