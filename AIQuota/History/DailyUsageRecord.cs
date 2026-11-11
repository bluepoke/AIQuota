using System.Numerics;
using System.Text.Json.Serialization;

namespace AIQuota.History;

/// <summary>
/// One local calendar day of usage, as kept for the yearly heatmap (issue #17) - one JSON
/// object per line in <c>&lt;account-hash&gt;.daily.jsonl</c> next to the raw history (see
/// <see cref="JsonlUsageHistory"/>). Built from the raw samples by <see
/// cref="DailyUsageAggregator"/>, and kept for <see cref="JsonlUsageHistory.DailyRetention"/>,
/// far longer than the raw samples themselves.
/// </summary>
/// <param name="Date">The local calendar day.</param>
/// <param name="WeekPoints">Share of the weekly quota used that day, in percentage points:
/// the sum of all increases of the weekly utilization between consecutive samples (see <see
/// cref="DailyUsageAggregator.WeekIncrease"/>).</param>
/// <param name="SessionPeak">Highest session utilization seen that day, 0-100.</param>
/// <param name="HourMask">Bit <c>h</c> is set if there was a sample in local hour <c>h</c>
/// (0-23) - "how long was the app running" for the hover text.</param>
/// <param name="WeekResetsAt">Reset time of the weekly window at the day's last sample, for
/// context.</param>
public sealed record DailyUsageRecord(
    [property: JsonPropertyName("date")] DateOnly Date,
    [property: JsonPropertyName("weekPoints")] double WeekPoints,
    [property: JsonPropertyName("sessionPeak")] double SessionPeak,
    [property: JsonPropertyName("hours")] int HourMask,
    [property: JsonPropertyName("weekResetsAt")] DateTimeOffset? WeekResetsAt)
{
    /// <summary>Number of local hours with at least one sample.</summary>
    [JsonIgnore]
    public int ActiveHours => BitOperations.PopCount((uint)HourMask);
}
