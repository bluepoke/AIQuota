using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIQuota.History;

/// <summary>
/// <see cref="IUsageHistory"/> stored as JSON Lines - one <see cref="UsageHistoryRecord"/>
/// per line - in <c>%LOCALAPPDATA%\AIQuota\history\</c>, one file per account. The file name
/// is a hash of the account ID, so switching accounts never mixes data and the account's
/// email doesn't show up in a file name. A record is appended per fetch (a few hundred bytes
/// every 5 minutes), and the whole file is read into memory when an account is loaded -
/// small enough at <see cref="Retention"/> days that a database would be overkill.
///
/// Next to it, <c>&lt;hash&gt;.daily.jsonl</c> keeps one <see cref="DailyUsageRecord"/> per
/// local calendar day for the yearly heatmap, for <see cref="DailyRetention"/> days. Every
/// append adds the updated current day as a new line (the last line of a date wins), so the
/// file is never rewritten on a fetch; it's compacted to one line per day when it's loaded
/// and once a day. Without a daily file - the first start after the update - it's
/// backfilled from the raw history.
///
/// Storage problems (disk full, no permission, a corrupt line) must never break the tray
/// app: write failures are traced and otherwise ignored, unreadable lines are skipped.
/// </summary>
public sealed class JsonlUsageHistory : IUsageHistory
{
    /// <summary>Records older than this are dropped: covers the previous weekly window plus
    /// some margin.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(35);

    /// <summary>Daily values older than this are dropped: a full year in the heatmap, plus
    /// some margin.</summary>
    public static readonly TimeSpan DailyRetention = TimeSpan.FromDays(400);

    private static readonly TimeSpan TrimInterval = TimeSpan.FromDays(1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly string _directory;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeZoneInfo _zone;
    private string? _filePath;
    private string? _dailyFilePath;
    private List<UsageHistoryRecord> _records = [];
    private List<DailyUsageRecord> _daily = [];
    /// <summary>Lines were appended to the daily file since it was last compacted.</summary>
    private bool _dailyHasExtraLines;
    private DateTimeOffset _lastTrim;

    /// <param name="directory">Where the history files live; defaults to
    /// <see cref="DefaultDirectory"/>.</param>
    /// <param name="now">Clock, injectable for testing.</param>
    /// <param name="zone">Time zone that defines a "day" for the daily values; defaults to
    /// the local one.</param>
    public JsonlUsageHistory(string? directory = null, Func<DateTimeOffset>? now = null, TimeZoneInfo? zone = null)
    {
        _directory = directory ?? DefaultDirectory;
        _now = now ?? (() => DateTimeOffset.Now);
        _zone = zone ?? TimeZoneInfo.Local;
    }

    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIQuota", "history");

    public string? AccountId { get; private set; }

    public IReadOnlyList<UsageHistoryRecord> Records => _records;

    public IReadOnlyList<DailyUsageRecord> DailyUsage => _daily;

    public void SetAccount(string? accountId)
    {
        if (accountId == AccountId)
            return;

        AccountId = accountId;
        _filePath = accountId is null ? null : Path.Combine(_directory, FileNameFor(accountId) + ".jsonl");
        _dailyFilePath = accountId is null ? null : Path.Combine(_directory, FileNameFor(accountId) + ".daily.jsonl");
        // Emptied first: Load() trims, which must not write the previous account's days.
        _daily = [];
        _dailyHasExtraLines = false;
        Load();
        LoadDaily();
    }

    public IReadOnlyList<UsageHistoryRecord> Query(DateTimeOffset from, DateTimeOffset to) =>
        _records.Where(r => r.Time >= from && r.Time <= to).ToList();

    public void Append(UsageHistoryRecord record)
    {
        if (_filePath is null)
            return;

        var previous = _records.Count > 0 ? _records[^1] : null;
        _records.Add(record);
        try
        {
            Directory.CreateDirectory(_directory);
            File.AppendAllText(_filePath, Serialize(record) + "\n", Utf8NoBom);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"AIQuota: could not append to usage history {_filePath}: {ex.Message}");
        }

        AppendDaily(previous, record);

        if (_now() - _lastTrim >= TrimInterval)
            Trim(rewriteAnyway: false);
    }

    public void Clear()
    {
        _records = [];
        _daily = [];
        _dailyHasExtraLines = false;
        try
        {
            if (Directory.Exists(_directory))
            {
                foreach (var file in Directory.EnumerateFiles(_directory, "*.jsonl"))
                    File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"AIQuota: could not clear usage history in {_directory}: {ex.Message}");
        }
    }

    /// <summary>Reads the current account's file, skipping lines that can't be parsed (e.g.
    /// one cut off by a crash or power loss mid-write), then trims old records. If anything
    /// was skipped - or the file doesn't end with a newline, which would glue the next
    /// appended record onto a broken line - the file is rewritten clean.</summary>
    private void Load()
    {
        _records = [];
        if (_filePath is null || !File.Exists(_filePath))
        {
            _lastTrim = _now();
            return;
        }

        var needsRewrite = false;
        try
        {
            var content = File.ReadAllText(_filePath, Utf8NoBom);
            needsRewrite = content.Length > 0 && !content.EndsWith('\n');
            foreach (var line in content.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (TryParse(line) is { } record)
                    _records.Add(record);
                else
                    needsRewrite = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"AIQuota: could not read usage history {_filePath}: {ex.Message}");
            return;
        }

        _records.Sort((a, b) => a.Time.CompareTo(b.Time));
        Trim(needsRewrite);
    }

    private void Trim(bool rewriteAnyway)
    {
        _lastTrim = _now();
        var cutoff = _lastTrim - Retention;
        var removed = _records.RemoveAll(r => r.Time < cutoff);
        if (removed > 0 || rewriteAnyway)
            Rewrite();
        TrimDaily(rewriteAnyway: false);
    }

    // ---------------------------------------------------------------- daily values

    /// <summary>Adds <paramref name="record"/> to its day and appends that day's new state to
    /// the daily file. A sample older than the newest one so far is left out there (the raw
    /// history keeps it).</summary>
    private void AppendDaily(UsageHistoryRecord? previous, UsageHistoryRecord record)
    {
        if (_dailyFilePath is null || (previous is not null && record.Time < previous.Time))
            return;

        var date = DailyUsageAggregator.DayOf(record.Time, _zone);
        var index = _daily.FindLastIndex(d => d.Date == date);
        var day = DailyUsageAggregator.Add(index >= 0 ? _daily[index] : null, previous, record, _zone);
        if (index >= 0)
        {
            _daily[index] = day;
        }
        else
        {
            _daily.Add(day);
            _daily.Sort((a, b) => a.Date.CompareTo(b.Date));
        }

        try
        {
            Directory.CreateDirectory(_directory);
            File.AppendAllText(_dailyFilePath, SerializeDaily(day) + "\n", Utf8NoBom);
            _dailyHasExtraLines |= index >= 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"AIQuota: could not append to daily usage {_dailyFilePath}: {ex.Message}");
        }
    }

    /// <summary>Reads the current account's daily file - the last line of each date wins -
    /// or, if there is none yet, backfills it from the raw history.</summary>
    private void LoadDaily()
    {
        _daily = [];
        _dailyHasExtraLines = false;
        if (_dailyFilePath is null)
            return;

        if (!File.Exists(_dailyFilePath))
        {
            if (_records.Count > 0)
            {
                _daily = DailyUsageAggregator.Build(_records, _zone);
                TrimDaily(rewriteAnyway: true);
            }
            return;
        }

        var needsRewrite = false;
        try
        {
            var content = File.ReadAllText(_dailyFilePath, Utf8NoBom);
            needsRewrite = content.Length > 0 && !content.EndsWith('\n');
            var byDate = new Dictionary<DateOnly, DailyUsageRecord>();
            foreach (var line in content.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (TryParseDaily(line) is { } day)
                {
                    needsRewrite |= byDate.ContainsKey(day.Date);
                    byDate[day.Date] = day;
                }
                else
                {
                    needsRewrite = true;
                }
            }
            _daily = [.. byDate.Values.OrderBy(d => d.Date)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"AIQuota: could not read daily usage {_dailyFilePath}: {ex.Message}");
            return;
        }

        TrimDaily(needsRewrite);
    }

    /// <summary>Drops days beyond <see cref="DailyRetention"/> and compacts the daily file to
    /// one line per day if anything changed.</summary>
    private void TrimDaily(bool rewriteAnyway)
    {
        var cutoff = DateOnly.FromDateTime((_now() - DailyRetention).Date);
        var removed = _daily.RemoveAll(d => d.Date < cutoff);
        if (removed > 0 || rewriteAnyway || _dailyHasExtraLines)
            RewriteFile(_dailyFilePath, _daily.Select(SerializeDaily));
        _dailyHasExtraLines = false;
    }

    /// <summary>Replaces the file with the in-memory records - written to a temp file first
    /// and then moved over the original, so a crash mid-write can't lose the history.</summary>
    private void Rewrite() => RewriteFile(_filePath, _records.Select(Serialize));

    private void RewriteFile(string? path, IEnumerable<string> lines)
    {
        if (path is null)
            return;

        var tempPath = path + ".tmp";
        try
        {
            Directory.CreateDirectory(_directory);
            var builder = new StringBuilder();
            foreach (var line in lines)
                builder.Append(line).Append('\n');
            File.WriteAllText(tempPath, builder.ToString(), Utf8NoBom);
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"AIQuota: could not rewrite {path}: {ex.Message}");
        }
    }

    private static string Serialize(UsageHistoryRecord record) => JsonSerializer.Serialize(record, JsonOptions);

    private static string SerializeDaily(DailyUsageRecord day) => JsonSerializer.Serialize(day, JsonOptions);

    private static DailyUsageRecord? TryParseDaily(string line)
    {
        try
        {
            var day = JsonSerializer.Deserialize<DailyUsageRecord>(line, JsonOptions);
            return day is not null && day.Date != default ? day : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static UsageHistoryRecord? TryParse(string line)
    {
        try
        {
            var record = JsonSerializer.Deserialize<UsageHistoryRecord>(line, JsonOptions);
            // A line without a timestamp isn't usable for anything.
            return record is not null && record.Time != default ? record : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string FileNameFor(string accountId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(accountId));
        return Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }
}
