using System.Text.Json.Serialization;

namespace AIQuota.History;

/// <summary>One successful usage fetch, as stored in the local history (one JSON object per
/// line, see <see cref="JsonlUsageHistory"/>). Percentages are the API's raw 0-100
/// utilization values rather than the rounded ones shown in the UI, so burn rates computed
/// from them aren't skewed by rounding.</summary>
public sealed record UsageHistoryRecord(
    [property: JsonPropertyName("time")] DateTimeOffset Time,
    [property: JsonPropertyName("session")] double Session,
    [property: JsonPropertyName("sessionResetsAt")] DateTimeOffset? SessionResetsAt,
    [property: JsonPropertyName("week")] double Week,
    [property: JsonPropertyName("weekResetsAt")] DateTimeOffset? WeekResetsAt,
    [property: JsonPropertyName("creditUsed")] decimal? CreditUsed = null,
    [property: JsonPropertyName("creditLimit")] decimal? CreditLimit = null,
    [property: JsonPropertyName("creditCurrency")] string? CreditCurrency = null)
{
    public static UsageHistoryRecord FromSnapshot(UsageSnapshot snapshot) => new(
        snapshot.FetchedAt,
        snapshot.SessionUtilization ?? snapshot.SessionPercent,
        snapshot.SessionResetsAt,
        snapshot.WeeklyUtilization ?? snapshot.WeeklyPercent,
        snapshot.WeeklyResetsAt,
        snapshot.CreditUsed,
        snapshot.CreditLimit,
        snapshot.CreditCurrency);
}
