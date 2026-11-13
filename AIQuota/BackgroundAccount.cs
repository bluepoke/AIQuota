using AIQuota.Auth;
using AIQuota.History;

namespace AIQuota;

/// <summary>
/// A monitored account that isn't the active one (#18): polled on its own schedule by
/// <see cref="UsageTrayContext"/>, which also records its history and notifications. The
/// active account isn't one of these - it's fetched by the tray's own refresh.
/// </summary>
internal sealed class BackgroundAccount(SavedAccount account)
{
    /// <summary>The longest wait after repeated rate limiting (429).</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);

    public SavedAccount Account { get; } = account;

    /// <summary>The last successfully fetched usage (kept through later errors).</summary>
    public UsageSnapshot? Snapshot { get; set; }

    /// <summary>Result of the last fetch; null before the first one.</summary>
    public UsageFetchStatus? Status { get; set; }

    public string? Error { get; set; }

    public bool IsFetching { get; set; }

    public DateTimeOffset NextFetchAt { get; set; }

    /// <summary>Consecutive rate-limited fetches; each doubles the wait.</summary>
    public int RateLimitStrikes { get; set; }

    public UsageForecast SessionForecast { get; set; } = UsageForecast.None;

    public UsageForecast WeeklyForecast { get; set; } = UsageForecast.None;

    /// <summary>A server's Retry-After is honoured up to this.</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(6);

    /// <summary>When to fetch after a result: the poll interval, or after rate limiting
    /// (<paramref name="rateLimitStrikes"/> times in a row, this one included) the longer of
    /// <see cref="UsageFetchResult.RetryAfter"/> and an exponential backoff.</summary>
    public static TimeSpan DelayAfter(UsageFetchResult result, int rateLimitStrikes, TimeSpan pollInterval)
    {
        if (!result.RateLimited)
            return pollInterval;
        var backoff = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, pollInterval.Ticks << Math.Clamp(rateLimitStrikes, 1, 10)));
        if (result.RetryAfter is { } retryAfter && retryAfter > backoff)
            return retryAfter < MaxRetryAfter ? retryAfter : MaxRetryAfter;
        return backoff;
    }
}
