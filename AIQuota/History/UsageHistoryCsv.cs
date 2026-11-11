using System.Globalization;
using System.Text;
using Microsoft.Win32;

namespace AIQuota.History;

/// <summary>
/// Exports the usage history as CSV that opens as proper columns in Excel on both German and
/// English Windows. Excel splits a double-clicked CSV at the Windows list separator (";" on
/// German systems, "," on English ones) and parses numbers with the Windows decimal separator,
/// so a fixed "," + "." file would land in a single column in German Excel. Both separators
/// are therefore read from the user's regional settings in the registry - the app runs with
/// <c>InvariantGlobalization</c>, so <see cref="CultureInfo.CurrentCulture"/> can't tell.
/// Timestamps are ISO 8601 with offset, plus a plain local "yyyy-MM-dd HH:mm:ss" column that
/// Excel recognises as a date/time in every locale.
/// </summary>
public static class UsageHistoryCsv
{
    private const string InternationalKeyPath = @"Control Panel\International";

    public static void Write(string path, IEnumerable<UsageHistoryRecord> records)
    {
        var (listSeparator, decimalSeparator) = ReadRegionalSeparators();
        // With BOM, so Excel reads it as UTF-8 (currency symbols etc.).
        File.WriteAllText(path, Build(records, listSeparator, decimalSeparator), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    public static string Build(IEnumerable<UsageHistoryRecord> records, string listSeparator, string decimalSeparator)
    {
        var numberFormat = new NumberFormatInfo { NumberDecimalSeparator = decimalSeparator, NumberGroupSeparator = "" };

        string Field(string? value) =>
            value is null ? "" :
            value.Contains(listSeparator) || value.Contains('"') || value.Contains('\n')
                ? $"\"{value.Replace("\"", "\"\"")}\""
                : value;
        string Number(double value) => Field(value.ToString("0.###", numberFormat));
        string Money(decimal? value) => value is { } v ? Field(v.ToString("0.##", numberFormat)) : "";
        static string Iso(DateTimeOffset? value) => value?.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture) ?? "";

        var builder = new StringBuilder();
        builder.AppendJoin(listSeparator,
            "time", "local_time", "session_percent", "session_resets_at",
            "week_percent", "week_resets_at", "credit_used", "credit_limit", "credit_currency");
        builder.Append("\r\n");

        foreach (var r in records)
        {
            builder.AppendJoin(listSeparator,
                Iso(r.Time),
                r.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Number(r.Session),
                Iso(r.SessionResetsAt),
                Number(r.Week),
                Iso(r.WeekResetsAt),
                Money(r.CreditUsed),
                Money(r.CreditLimit),
                Field(r.CreditCurrency));
            builder.Append("\r\n");
        }

        return builder.ToString();
    }

    /// <summary>Exports the daily values of the yearly heatmap (<see cref="DailyUsageRecord"/>),
    /// one row per day, with the same separators as <see cref="Write"/>.</summary>
    public static void WriteDaily(string path, IEnumerable<DailyUsageRecord> days)
    {
        var (listSeparator, decimalSeparator) = ReadRegionalSeparators();
        File.WriteAllText(path, BuildDaily(days, listSeparator, decimalSeparator), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    public static string BuildDaily(IEnumerable<DailyUsageRecord> days, string listSeparator, string decimalSeparator)
    {
        var numberFormat = new NumberFormatInfo { NumberDecimalSeparator = decimalSeparator, NumberGroupSeparator = "" };
        string Number(double value)
        {
            var text = value.ToString("0.###", numberFormat);
            return text.Contains(listSeparator) ? $"\"{text}\"" : text;
        }

        var builder = new StringBuilder();
        builder.AppendJoin(listSeparator,
            "date", "week_percent_used", "session_peak_percent", "active_hours", "week_resets_at");
        builder.Append("\r\n");

        foreach (var day in days)
        {
            builder.AppendJoin(listSeparator,
                // yyyy-MM-dd: Excel recognises it as a date in every locale.
                day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Number(day.WeekPoints),
                Number(day.SessionPeak),
                day.ActiveHours.ToString(CultureInfo.InvariantCulture),
                day.WeekResetsAt?.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture) ?? "");
            builder.Append("\r\n");
        }

        return builder.ToString();
    }

    /// <summary>The Windows list and decimal separators (Settings → Time & language → Region),
    /// falling back to "," and "." - and to ";" as list separator should both be the same
    /// character, which would make the file unparseable.</summary>
    public static (string ListSeparator, string DecimalSeparator) ReadRegionalSeparators()
    {
        string? list = null, decimalSep = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternationalKeyPath, writable: false);
            list = key?.GetValue("sList") as string;
            decimalSep = key?.GetValue("sDecimal") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Fall back to the defaults below.
        }

        list = string.IsNullOrEmpty(list) ? "," : list;
        decimalSep = string.IsNullOrEmpty(decimalSep) ? "." : decimalSep;
        if (list == decimalSep)
            list = ";";
        return (list, decimalSep);
    }
}
