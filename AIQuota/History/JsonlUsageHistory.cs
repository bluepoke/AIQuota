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
/// Storage problems (disk full, no permission, a corrupt line) must never break the tray
/// app: write failures are traced and otherwise ignored, unreadable lines are skipped.
/// </summary>
public sealed class JsonlUsageHistory : IUsageHistory
{
    /// <summary>Records older than this are dropped: covers the previous weekly window plus
    /// some margin.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(35);

    private static readonly TimeSpan TrimInterval = TimeSpan.FromDays(1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly string _directory;
    private readonly Func<DateTimeOffset> _now;
    private string? _filePath;
    private List<UsageHistoryRecord> _records = [];
    private DateTimeOffset _lastTrim;

    /// <param name="directory">Where the history files live; defaults to
    /// <see cref="DefaultDirectory"/>.</param>
    /// <param name="now">Clock, injectable for testing.</param>
    public JsonlUsageHistory(string? directory = null, Func<DateTimeOffset>? now = null)
    {
        _directory = directory ?? DefaultDirectory;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIQuota", "history");

    public string? AccountId { get; private set; }

    public IReadOnlyList<UsageHistoryRecord> Records => _records;

    public void SetAccount(string? accountId)
    {
        if (accountId == AccountId)
            return;

        AccountId = accountId;
        _filePath = accountId is null ? null : Path.Combine(_directory, FileNameFor(accountId));
        Load();
    }

    public IReadOnlyList<UsageHistoryRecord> Query(DateTimeOffset from, DateTimeOffset to) =>
        _records.Where(r => r.Time >= from && r.Time <= to).ToList();

    public void Append(UsageHistoryRecord record)
    {
        if (_filePath is null)
            return;

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

        if (_now() - _lastTrim >= TrimInterval)
            Trim(rewriteAnyway: false);
    }

    public void Clear()
    {
        _records = [];
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
    }

    /// <summary>Replaces the file with the in-memory records - written to a temp file first
    /// and then moved over the original, so a crash mid-write can't lose the history.</summary>
    private void Rewrite()
    {
        if (_filePath is null)
            return;

        var tempPath = _filePath + ".tmp";
        try
        {
            Directory.CreateDirectory(_directory);
            var builder = new StringBuilder();
            foreach (var record in _records)
                builder.Append(Serialize(record)).Append('\n');
            File.WriteAllText(tempPath, builder.ToString(), Utf8NoBom);
            File.Move(tempPath, _filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"AIQuota: could not rewrite usage history {_filePath}: {ex.Message}");
        }
    }

    private static string Serialize(UsageHistoryRecord record) => JsonSerializer.Serialize(record, JsonOptions);

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
        return Convert.ToHexStringLower(hash.AsSpan(0, 8)) + ".jsonl";
    }
}
