namespace AIQuota.History;

public enum ForecastKind
{
    /// <summary>No forecast: not enough recent data in the current window, usage isn't rising
    /// (idle), the limit is already reached, or the session hasn't started.</summary>
    None,

    /// <summary>Usage is rising, but at the current rate the window resets before it reaches
    /// 100%.</summary>
    ResetFirst,

    /// <summary>At the current rate, usage reaches 100% before the window resets, in
    /// <see cref="UsageForecast.TimeToLimit"/>.</summary>
    Limit,
}

public readonly record struct UsageForecast(ForecastKind Kind, TimeSpan TimeToLimit = default)
{
    public static UsageForecast None => new(ForecastKind.None);

    /// <summary><see cref="TimeToLimit"/> when <see cref="Kind"/> is <see cref="ForecastKind.Limit"/>, else null.</summary>
    public TimeSpan? LimitIn => Kind == ForecastKind.Limit ? TimeToLimit : null;
}

/// <summary>One usage value of one window (session or week) at a point in time.</summary>
public readonly record struct UsagePoint(DateTimeOffset Time, double Percent, DateTimeOffset? ResetsAt);

/// <summary>
/// Projects when a usage window (5-hour session or 7-day week) will hit 100% if usage keeps
/// growing at its recent rate. The rate is the slope of a least-squares line through the
/// window's recent samples, in percentage points per minute; a linear fit rather than just
/// first/last keeps a single odd sample from swinging the result.
/// </summary>
public static class BurnRateForecaster
{
    /// <param name="Lookback">How far back samples are used.</param>
    /// <param name="MaxGap">Walking back from the newest sample, stop at the first gap between
    /// two samples longer than this (PC asleep, app not running).</param>
    /// <param name="MinSpan">Minimum time between the oldest and newest sample used, so a
    /// few quick manual refreshes can't produce a rate.</param>
    /// <param name="MinSamples">Minimum number of samples used.</param>
    public sealed record Settings(TimeSpan Lookback, TimeSpan MaxGap, TimeSpan MinSpan, int MinSamples = 3);

    /// <summary>Session: the last 30 minutes, split at gaps over 15 minutes - a session's rate
    /// changes quickly, so only the recent, unbroken stretch counts.</summary>
    public static readonly Settings Session = new(
        Lookback: TimeSpan.FromMinutes(30),
        MaxGap: TimeSpan.FromMinutes(15),
        MinSpan: TimeSpan.FromMinutes(10));

    /// <summary>Week: the last 24 hours. Not split at gaps - weekly utilization only ever
    /// accumulates within a window, so the average across a night the PC was asleep is still
    /// the true average rate (and counts usage from other devices meanwhile). At least 6 hours
    /// of data, so one heavy session isn't extrapolated over the rest of the week.</summary>
    public static readonly Settings Week = new(
        Lookback: TimeSpan.FromHours(24),
        MaxGap: TimeSpan.FromHours(24),
        MinSpan: TimeSpan.FromHours(6));

    /// <summary>Two reset times closer than this belong to the same window; guards against the
    /// API reporting a window's reset time with slight jitter between calls.</summary>
    private static readonly TimeSpan SameWindowTolerance = TimeSpan.FromMinutes(10);

    /// <param name="points">Samples of one window type, any order, possibly spanning several
    /// windows; only those of the current window (<paramref name="currentResetsAt"/>) count.</param>
    /// <param name="currentResetsAt">Reset time of the current window; null (e.g. session not
    /// started) gives no forecast.</param>
    public static UsageForecast Forecast(IEnumerable<UsagePoint> points, DateTimeOffset? currentResetsAt, Settings settings, DateTimeOffset now)
    {
        if (currentResetsAt is not { } resetsAt)
            return UsageForecast.None;

        var window = points
            .Where(p => p.ResetsAt is { } r && (r - resetsAt).Duration() <= SameWindowTolerance)
            .OrderBy(p => p.Time)
            .ToList();
        if (window.Count == 0)
            return UsageForecast.None;

        var latest = window[^1];
        if (latest.Percent >= 100 || now - latest.Time > settings.Lookback)
            return UsageForecast.None;

        var used = new List<UsagePoint> { latest };
        for (var i = window.Count - 2; i >= 0; i--)
        {
            var point = window[i];
            if (latest.Time - point.Time > settings.Lookback || used[^1].Time - point.Time > settings.MaxGap)
                break;
            used.Add(point);
        }

        if (used.Count < settings.MinSamples || latest.Time - used[^1].Time < settings.MinSpan)
            return UsageForecast.None;

        var ratePerMinute = Slope(used, latest.Time);
        if (ratePerMinute <= 0)
            return UsageForecast.None;

        var limitAt = latest.Time + TimeSpan.FromMinutes((100 - latest.Percent) / ratePerMinute);
        if (limitAt >= resetsAt)
            return new UsageForecast(ForecastKind.ResetFirst);

        var timeToLimit = limitAt - now;
        return new UsageForecast(ForecastKind.Limit, timeToLimit < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : timeToLimit);
    }

    /// <summary>Least-squares slope of percent over time, in percentage points per minute.
    /// Times are taken relative to <paramref name="origin"/> to keep the numbers small.</summary>
    private static double Slope(IReadOnlyCollection<UsagePoint> points, DateTimeOffset origin)
    {
        var n = points.Count;
        double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
        foreach (var p in points)
        {
            var x = (p.Time - origin).TotalMinutes;
            sumX += x;
            sumY += p.Percent;
            sumXY += x * p.Percent;
            sumXX += x * x;
        }

        var denominator = n * sumXX - sumX * sumX;
        return denominator == 0 ? 0 : (n * sumXY - sumX * sumY) / denominator;
    }
}
