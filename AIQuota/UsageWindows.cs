namespace AIQuota;

/// <summary>Lengths of the two usage windows, and the weekly pace derived from them - shared
/// by the tray icon, the Stream Dock keys and the tray flyout.</summary>
internal static class UsageWindows
{
    public static readonly TimeSpan Session = TimeSpan.FromHours(5);
    public static readonly TimeSpan Week = TimeSpan.FromDays(7);

    /// <summary>Elapsed fraction (0-1) of the 5-hour session that resets at <paramref
    /// name="sessionResetsAt"/>, at <paramref name="time"/> - i.e. where usage would be if the
    /// session's quota were spread evenly over the 5 hours.</summary>
    public static double SessionPaceFraction(DateTimeOffset sessionResetsAt, DateTimeOffset time) =>
        Math.Clamp(1.0 - (sessionResetsAt - time) / Session, 0.0, 1.0);

    /// <summary>Start of the weekly window that resets at <paramref name="weeklyResetsAt"/>.</summary>
    public static DateTimeOffset WeekStart(DateTimeOffset weeklyResetsAt) => weeklyResetsAt - Week;

    /// <summary>Elapsed fraction (0-1) of the weekly window that resets at <paramref
    /// name="weeklyResetsAt"/>, at <paramref name="time"/> - i.e. where usage would be if the
    /// weekly quota were spread evenly over the 7 days.</summary>
    public static double WeeklyPaceFraction(DateTimeOffset weeklyResetsAt, DateTimeOffset time) =>
        Math.Clamp(1.0 - (weeklyResetsAt - time) / Week, 0.0, 1.0);
}
