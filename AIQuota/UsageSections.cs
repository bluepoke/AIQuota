using AIQuota.History;
using AIQuota.Localization;

namespace AIQuota;

/// <summary>A line of text under a usage bar; <paramref name="Emphasized"/> lines (a limit
/// forecast) use the primary text colour instead of the secondary one.</summary>
internal readonly record struct SectionLine(string Text, bool Emphasized = false);

/// <summary>One usage bar with everything shown around it - what the tray flyout and the
/// mini dashboard draw for the session, the week, each additional limit and the credits.</summary>
/// <param name="Key">Stable identifier: "session", "week", "credits" or "limit:&lt;limit key&gt;"
/// - what the mini dashboard stores as its bar selection.</param>
/// <param name="Sections">How many equal parts the bar's scale ticks divide it into.</param>
/// <param name="Details">Reset time, pace, allowance or amount, then the forecast - in
/// display order.</param>
internal sealed record UsageSection(
    string Key,
    string Label,
    int Percent,
    bool OutlineOnly,
    double? PaceFraction,
    double? ProjectedPercent,
    int Sections,
    IReadOnlyList<SectionLine> Details);

/// <summary>Builds the <see cref="UsageSection"/>s from a <see cref="FlyoutState"/>, in the
/// order they're shown: session, additional limits of the session window, week, all other
/// additional limits, credits. Empty while logged out or before the first fetch.</summary>
internal static class UsageSections
{
    public const string SessionKey = "session";
    public const string WeekKey = "week";
    public const string CreditsKey = "credits";

    public static string LimitKey(UsageLimitInfo limit) => "limit:" + limit.Key;

    public static IReadOnlyList<UsageSection> Build(FlyoutState state, DateTimeOffset now)
    {
        if (!state.LoggedIn || state.Snapshot is not { } snapshot)
            return [];

        var sections = new List<UsageSection>();
        var limits = (snapshot.AdditionalLimits ?? [])
            .OrderBy(Strings.LimitLabel, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        // Session - with a pace marker like the week's (the same setting turns both on/off),
        // here for an even spread over the 5 hours. None before a session starts: there's no
        // clock running yet.
        double? sessionPace = !state.SessionNotStarted && snapshot.SessionResetsAt is { } resetForPace
            ? UsageWindows.SessionPaceFraction(resetForPace, now)
            : null;
        var sessionDetails = new List<SectionLine>
        {
            new(state.SessionNotStarted || snapshot.SessionResetsAt is not { } sessionReset
                ? Strings.FlyoutSessionNotStarted
                : Strings.FlyoutResetLine(sessionReset, now)
                    + (state.ShowPaceMarker && sessionPace is { } sp ? Strings.PaceSuffix((int)Math.Round(sp * 100)) : "")),
        };
        AddForecast(sessionDetails, state.SessionForecast);
        sections.Add(new UsageSection(SessionKey, Strings.FlyoutSessionTitle, snapshot.SessionPercent, state.SessionNotStarted,
            state.ShowPaceMarker ? sessionPace : null, state.SessionForecast.ProjectedPercent, UsageWindows.SessionHours, sessionDetails));

        sections.AddRange(limits.Where(l => l.IsSessionGroup).Select(l => LimitSection(l, now)));

        // Week
        double? weekPace = snapshot.WeeklyResetsAt is { } weekReset ? UsageWindows.WeeklyPaceFraction(weekReset, now) : null;
        var weekDetails = new List<SectionLine>();
        if (snapshot.WeeklyResetsAt is { } weeklyReset)
        {
            weekDetails.Add(new(Strings.FlyoutResetLine(weeklyReset, now)
                + (state.ShowPaceMarker && weekPace is { } p ? Strings.PaceSuffix((int)Math.Round(p * 100)) : "")));
        }
        AddForecast(weekDetails, state.WeeklyForecast);
        sections.Add(new UsageSection(WeekKey, Strings.FlyoutWeekTitle, snapshot.WeeklyPercent, OutlineOnly: false,
            state.ShowPaceMarker ? weekPace : null, state.WeeklyForecast.ProjectedPercent, UsageWindows.WeekDays, weekDetails));

        sections.AddRange(limits.Where(l => !l.IsSessionGroup).Select(l => LimitSection(l, now)));

        // Credits
        if (state.CreditPercent is { } creditPercent &&
            snapshot is { CreditUsed: { } used, CreditLimit: { } limit, CreditCurrency: { } currency })
        {
            sections.Add(new UsageSection(CreditsKey, Strings.FlyoutCreditsTitle, creditPercent, OutlineOnly: false,
                PaceFraction: null, ProjectedPercent: null, Sections: 4, [new(Strings.CreditsAmount(used, limit, currency))]));
        }

        return sections;
    }

    /// <summary>An additional limit (e.g. a model's weekly limit, see <see cref="UsageLimitInfo"/>):
    /// no pace marker or projection - there's no history for it - but its reset time, and how
    /// much of the weekly allowance it may use when the API says so.</summary>
    private static UsageSection LimitSection(UsageLimitInfo limit, DateTimeOffset now)
    {
        var details = new List<SectionLine>();
        if (limit.ResetsAt is { } reset)
            details.Add(new(Strings.FlyoutResetLine(reset, now)));
        if (limit.AllowanceShareOfWeekly is { } share)
            details.Add(new(Strings.LimitAllowanceLine(share)));
        return new UsageSection(LimitKey(limit), Strings.LimitLabel(limit), limit.RoundedPercent, OutlineOnly: false,
            PaceFraction: null, ProjectedPercent: null, SectionsFor(limit.Group), details);
    }

    /// <summary>"Limit in ~X at current rate" (emphasized) or "On track to reset first".</summary>
    private static void AddForecast(List<SectionLine> lines, UsageForecast forecast)
    {
        switch (forecast.Kind)
        {
            case ForecastKind.Limit:
                lines.Add(new("↳ " + Strings.ForecastLimitInText(forecast.TimeToLimit), Emphasized: true));
                break;
            case ForecastKind.ResetFirst:
                lines.Add(new("↳ " + Strings.ForecastResetFirstText));
                break;
        }
    }

    /// <summary>Scale sections of a bar: one per hour of the session window, one per day of
    /// the weekly window (both from <see cref="UsageWindows"/>, so they follow if a window's
    /// length changes), quarters for everything else (credits, unknown windows).</summary>
    public static int SectionsFor(string group) => group.ToLowerInvariant() switch
    {
        "session" => UsageWindows.SessionHours,
        "weekly" => UsageWindows.WeekDays,
        _ => 4,
    };
}
