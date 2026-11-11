namespace AIQuota.History;

/// <summary>
/// Turns raw usage samples into per-day values (<see cref="DailyUsageRecord"/>) for the
/// yearly heatmap. The API only reports the utilization of rolling windows, so a day's usage
/// is derived: the sum of all increases of the weekly utilization between consecutive
/// samples, attributed to the local calendar day of the later sample.
///
/// <list type="bullet">
/// <item>Within one weekly window, utilization only grows, so the increases add up to the
/// true total even across gaps (PC off, app not running) - an increase across a gap simply
/// lands on the day of the sample after it.</item>
/// <item>A weekly reset between two samples is never counted as negative usage: the new
/// window started at 0, so its whole current value is that day's usage.</item>
/// <item>A reset time that moves while the old window hasn't ended yet (API jitter beyond
/// <see cref="SameWindowTolerance"/>) is treated as the same window, so it can't inflate a
/// day with the whole week's value.</item>
/// </list>
/// </summary>
public static class DailyUsageAggregator
{
    /// <summary>Two reset times closer than this belong to the same window - as in <see
    /// cref="BurnRateForecaster"/> and the flyout's chart.</summary>
    public static readonly TimeSpan SameWindowTolerance = TimeSpan.FromMinutes(10);

    /// <summary>The local calendar day of <paramref name="time"/> in <paramref name="zone"/>.</summary>
    public static DateOnly DayOf(DateTimeOffset time, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time, zone).DateTime);

    /// <summary>Percentage points of the weekly quota used between <paramref name="previous"/>
    /// and <paramref name="current"/>; 0 without a previous sample (nothing to compare with).</summary>
    public static double WeekIncrease(UsageHistoryRecord? previous, UsageHistoryRecord current)
    {
        if (previous is null || current.Time <= previous.Time)
            return 0;

        var increase = current.Week - previous.Week;
        if (!SameWindow(previous.WeekResetsAt, current.WeekResetsAt))
        {
            // The previous window has ended (or there was none): a new one started at 0 since
            // then, so everything it shows now was used in between. Anything else is a reset
            // time that moved within a running window - compare the values as usual.
            var previousEnded = previous.WeekResetsAt is not { } previousReset ||
                                previousReset <= current.Time + SameWindowTolerance;
            if (previousEnded)
                increase = current.Week;
        }
        return Math.Clamp(increase, 0, 100);
    }

    /// <summary>The day of <paramref name="current"/> with that sample added: <paramref
    /// name="day"/> (that same day's record so far, or null) plus the weekly increase since
    /// <paramref name="previous"/>, the session peak and the sample's hour.</summary>
    public static DailyUsageRecord Add(DailyUsageRecord? day, UsageHistoryRecord? previous, UsageHistoryRecord current, TimeZoneInfo zone)
    {
        var date = DayOf(current.Time, zone);
        if (day is null || day.Date != date)
            day = new DailyUsageRecord(date, 0, 0, 0, null);

        var hour = TimeZoneInfo.ConvertTime(current.Time, zone).Hour;
        return day with
        {
            WeekPoints = Math.Round(day.WeekPoints + WeekIncrease(previous, current), 3),
            SessionPeak = Math.Max(day.SessionPeak, Math.Round(current.Session, 3)),
            HourMask = day.HourMask | (1 << hour),
            WeekResetsAt = current.WeekResetsAt ?? day.WeekResetsAt,
        };
    }

    /// <summary>All days of <paramref name="records"/> (any order), oldest first - used to
    /// backfill the daily values from an existing raw history.</summary>
    public static List<DailyUsageRecord> Build(IEnumerable<UsageHistoryRecord> records, TimeZoneInfo zone)
    {
        var days = new List<DailyUsageRecord>();
        UsageHistoryRecord? previous = null;
        foreach (var record in records.OrderBy(r => r.Time))
        {
            var last = days.Count > 0 ? days[^1] : null;
            var updated = Add(last, previous, record, zone);
            if (last is not null && last.Date == updated.Date)
                days[^1] = updated;
            else
                days.Add(updated);
            previous = record;
        }
        return days;
    }

    private static bool SameWindow(DateTimeOffset? a, DateTimeOffset? b) =>
        a is { } x && b is { } y ? (x - y).Duration() <= SameWindowTolerance : a is null && b is null;
}
