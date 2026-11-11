namespace AIQuota.History;

/// <summary>Local, per-account history of usage fetches - the data source for the burn-rate
/// forecast (<see cref="BurnRateForecaster"/>), the CSV export, and later the tray flyout's
/// sparkline. Nothing in it ever leaves the machine.</summary>
public interface IUsageHistory
{
    /// <summary>The account whose history is currently loaded; null while signed out, in
    /// which case <see cref="Append"/> does nothing and <see cref="Records"/> is empty.</summary>
    string? AccountId { get; }

    /// <summary>Switches to (and loads) the history of the given account.</summary>
    void SetAccount(string? accountId);

    /// <summary>All records of the current account, oldest first.</summary>
    IReadOnlyList<UsageHistoryRecord> Records { get; }

    /// <summary>Records of the current account with <paramref name="from"/> &lt;= time &lt;=
    /// <paramref name="to"/>, oldest first.</summary>
    IReadOnlyList<UsageHistoryRecord> Query(DateTimeOffset from, DateTimeOffset to);

    /// <summary>Per-day values of the current account for the yearly heatmap, oldest first -
    /// kept much longer than <see cref="Records"/>, and updated with every <see
    /// cref="Append"/>.</summary>
    IReadOnlyList<DailyUsageRecord> DailyUsage { get; }

    void Append(UsageHistoryRecord record);

    /// <summary>Deletes the stored history of every account, not just the current one -
    /// the raw samples and the daily values.</summary>
    void Clear();
}
