using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using AIQuota.History;
using AIQuota.Localization;

namespace AIQuota;

/// <summary>What the yearly heatmap shows: the current account's daily values (see <see
/// cref="IUsageHistory.DailyUsage"/>) and how to draw them.</summary>
internal sealed record HeatmapState(
    IReadOnlyList<DailyUsageRecord> Days,
    bool LoggedIn,
    string? AccountLabel,
    bool Greyscale);

/// <summary>
/// Calendar heatmap of the past year (issue #17): one square per local calendar day, in 53
/// week columns from Monday to Sunday, coloured by the share of the weekly quota used that
/// day (<see cref="DailyUsageRecord.WeekPoints"/>). Days without any sample are drawn as an
/// outline only, days in the future not at all. Hovering a day - or moving with the arrow
/// keys - shows its values.
///
/// A normal window (title bar, taskbar button), custom-painted like the flyout: laid out in
/// 96-DPI logical pixels scaled to the monitor's DPI, in the flyout's light/dark palette, and
/// in greyscale in accessibility mode. It only repaints when it's opened, after a refresh,
/// on a theme change and on hover - no timer.
/// </summary>
internal sealed class UsageHeatmap : Form
{
    private const float Pad = 20f;
    private const float Cell = 11f;
    private const float Gap = 3f;
    private const float Pitch = Cell + Gap;
    private const int Weeks = 53;
    private const float DayLabelWidth = 34f;
    private const float SummaryHeight = 22f;
    private const float MonthRowHeight = 18f;
    private const float LegendHeight = 18f;

    /// <summary>Upper bounds (weekly percentage points per day) of levels 0-3; above the last
    /// is level 4. Fixed, so years stay comparable - an even spread of 100% over 7 days is
    /// about 14 points a day. Below 0.5 shows as 0% and counts as "no usage".</summary>
    private static readonly double[] LevelBounds = [0.5, 5, 10, 20];

    private readonly Func<HeatmapState> _getState;
    private HeatmapState _state;
    private FlyoutPalette _palette = FlyoutPalette.For(SystemTheme.IsLightTaskbar());
    private UsagePalette _usage = UsagePalette.Standard(SystemTheme.IsLightTaskbar());
    private float _scale = 1f;
    private float _fontScale;
    private Icon? _icon;

    private Font _titleFont = null!;
    private Font _labelFont = null!;
    private Font _smallFont = null!;

    /// <summary>The highlighted day (hover or keyboard), shown with its values.</summary>
    private DateOnly? _selected;

    public UsageHeatmap(Func<HeatmapState> getState)
    {
        _getState = getState;
        _state = getState();

        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        Text = Strings.HeatmapTitle;
        AccessibleName = Strings.HeatmapTitle;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);

        try
        {
            if (Environment.ProcessPath is { } exe)
                Icon = _icon = Icon.ExtractAssociatedIcon(exe);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            // Keep the default icon.
        }

        ApplyTheme();
        ApplyLayout();
    }

    /// <summary>Shows the window centred on the monitor under the cursor, or brings it to the
    /// front if it's already open.</summary>
    public void ShowHeatmap()
    {
        if (Visible)
        {
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            Activate();
            return;
        }

        var work = Screen.FromPoint(Cursor.Position).WorkingArea;
        _scale = NativeMethods.DpiAt(Cursor.Position, DeviceDpi) / 96f;
        UpdateState();
        Location = new Point(work.Left + Math.Max(0, (work.Width - Width) / 2), work.Top + Math.Max(0, (work.Height - Height) / 2));
        Show();
        Activate();
    }

    /// <summary>Re-reads the state - after a refresh, an account switch, or a theme or palette
    /// change.</summary>
    public void UpdateState()
    {
        _state = _getState();
        Text = Strings.HeatmapTitle;
        AccessibleName = Strings.HeatmapTitle;
        ApplyTheme();
        ApplyLayout();
        UpdateAccessibleDescription();
        Invalidate();
    }

    private void ApplyTheme()
    {
        var light = SystemTheme.IsLightTaskbar();
        _palette = FlyoutPalette.For(light);
        _usage = UsagePalette.For(_state.Greyscale, light);
        BackColor = _palette.Background;
        if (IsHandleCreated)
            ApplyTitleBarTheme();
    }

    /// <summary>Dark title bar in the dark theme (Windows 10 2004+ / 11).</summary>
    private void ApplyTitleBarTheme()
    {
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        var dark = _usage.IsLight ? 0 : 1;
        _ = NativeMethods.DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyTitleBarTheme();
        // The handle now knows the monitor it's on.
        _scale = DeviceDpi / 96f;
        ApplyLayout();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        // Lay out for the new DPI ourselves, at Windows' suggested position.
        e.Cancel = true;
        base.OnDpiChanged(e);
        _scale = e.DeviceDpiNew / 96f;
        Location = e.SuggestedRectangle.Location;
        ApplyLayout();
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _titleFont?.Dispose();
            _labelFont?.Dispose();
            _smallFont?.Dispose();
            _icon?.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---------------------------------------------------------------- layout

    private float S(float logical) => logical * _scale;

    private float GridLeft => S(Pad + DayLabelWidth);
    private float GridTop => S(Pad + SummaryHeight + 10f + MonthRowHeight);

    private void ApplyLayout()
    {
        UpdateFonts();
        var width = Pad + DayLabelWidth + Weeks * Pitch - Gap + Pad;
        var height = Pad + SummaryHeight + 10f + MonthRowHeight + 7 * Pitch - Gap + 14f + LegendHeight + Pad;
        ClientSize = new Size((int)Math.Ceiling(S(width)), (int)Math.Ceiling(S(height)));
    }

    private void UpdateFonts()
    {
        if (_fontScale == _scale && _titleFont is not null)
            return;

        var old = new Font?[] { _titleFont, _labelFont, _smallFont };
        _titleFont = new Font("Segoe UI Semibold", S(14f), FontStyle.Regular, GraphicsUnit.Pixel);
        _labelFont = new Font("Segoe UI", S(11f), FontStyle.Regular, GraphicsUnit.Pixel);
        _smallFont = new Font("Segoe UI", S(11f), FontStyle.Regular, GraphicsUnit.Pixel);
        _fontScale = _scale;
        foreach (var font in old)
            font?.Dispose();
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>Monday of the first (leftmost) week column: the current week is the last one.</summary>
    private static DateOnly FirstMonday(DateOnly today) =>
        today.AddDays(-(((int)today.DayOfWeek + 6) % 7) - 7 * (Weeks - 1));

    private RectangleF CellRect(int column, int row)
    {
        var x = MathF.Round(GridLeft + S(column * Pitch));
        var y = MathF.Round(GridTop + S(row * Pitch));
        var size = MathF.Round(S(Cell));
        return new RectangleF(x, y, size, size);
    }

    private static (int Column, int Row) Position(DateOnly date, DateOnly firstMonday)
    {
        var offset = date.DayNumber - firstMonday.DayNumber;
        return (offset / 7, offset % 7);
    }

    /// <summary>The level (0-4) for a day's weekly points.</summary>
    private static int Level(double points)
    {
        for (var i = 0; i < LevelBounds.Length; i++)
        {
            if (points < LevelBounds[i])
                return i;
        }
        return LevelBounds.Length;
    }

    /// <summary>Level colours: the app's green, from close to the background to full, on the
    /// flyout's neutral track for "no usage". In accessibility mode greys that differ in
    /// brightness only - the highest level has well over 3:1 contrast against level 0.</summary>
    private Color LevelColor(int level)
    {
        if (_usage.IsGreyscale)
        {
            int[] greys = _usage.IsLight ? [0xD8, 0xA8, 0x80, 0x58, 0x20] : [0x3C, 0x62, 0x8A, 0xB6, 0xF0];
            return Color.FromArgb(greys[level], greys[level], greys[level]);
        }
        if (level == 0)
            return _palette.Track;

        float[] strength = [0f, 0.32f, 0.55f, 0.78f, 1f];
        return Blend(_palette.Background, _usage.Low, strength[level]);
    }

    private Color NoDataOutline => _usage.TrackOutline ?? _palette.Track;

    private static Color Blend(Color from, Color to, float amount) => Color.FromArgb(
        (int)Math.Round(from.R + (to.R - from.R) * amount),
        (int)Math.Round(from.G + (to.G - from.G) * amount),
        (int)Math.Round(from.B + (to.B - from.B) * amount));

    // ---------------------------------------------------------------- painting

    protected override void OnPaint(PaintEventArgs e) => DrawAll(e.Graphics);

    /// <summary>Draws the whole window - also used by the screenshot tool.</summary>
    private void DrawAll(Graphics g)
    {
        g.Clear(_palette.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (_usage.IsGreyscale)
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        var today = Today;
        var firstMonday = FirstMonday(today);
        var days = _state.Days.ToDictionary(d => d.Date);
        var left = S(Pad);
        var right = ClientSize.Width - S(Pad);

        // Summary line, with the account on the right.
        DrawText(g, SummaryText(days, firstMonday, today), _titleFont, _palette.Text,
            new RectangleF(left, S(Pad), (right - left) * 0.7f, S(SummaryHeight)));
        if (_state is { LoggedIn: true, AccountLabel: { } account })
            DrawText(g, account, _smallFont, _palette.Secondary,
                new RectangleF(left + (right - left) * 0.7f, S(Pad), (right - left) * 0.3f, S(SummaryHeight)), TextFormatFlags.Right);

        DrawMonthLabels(g, firstMonday, today);

        // Weekday labels: Mon, Wed, Fri.
        foreach (var (row, day) in new[] { (0, DayOfWeek.Monday), (2, DayOfWeek.Wednesday), (4, DayOfWeek.Friday) })
        {
            var cell = CellRect(0, row);
            DrawText(g, Strings.WeekdayShort(day), _labelFont, _palette.Secondary,
                new RectangleF(left, cell.Y - S(3f), S(DayLabelWidth - 6f), cell.Height + S(6f)), TextFormatFlags.NoPadding);
        }

        // The days, up to today.
        for (var date = firstMonday; date <= today; date = date.AddDays(1))
        {
            var (column, row) = Position(date, firstMonday);
            DrawDay(g, CellRect(column, row), days.TryGetValue(date, out var day) ? Level(day.WeekPoints) : null);
        }

        DrawLegend(g, left, right, GridTop + S(7 * Pitch - Gap + 14f));

        if (_selected is { } selected && selected >= firstMonday && selected <= today)
        {
            var (column, row) = Position(selected, firstMonday);
            var cell = CellRect(column, row);
            using (var ring = new Pen(_palette.Text, Math.Max(1f, S(1.5f))))
            using (var path = UsageBarRenderer.RoundedRect(RectangleF.Inflate(cell, S(1.5f), S(1.5f)), S(3f)))
                g.DrawPath(ring, path);
            DrawHover(g, cell, DayText(selected, days));
        }
    }

    /// <summary>A day's square in its level's colour, or - with <paramref name="level"/> null
    /// (no samples that day) - as an outline only.</summary>
    private void DrawDay(Graphics g, RectangleF rect, int? level)
    {
        var radius = S(2f);
        if (level is { } l)
        {
            using var path = UsageBarRenderer.RoundedRect(rect, radius);
            using var brush = new SolidBrush(LevelColor(l));
            g.FillPath(brush, path);
        }
        else
        {
            var width = Math.Max(1f, S(1f));
            using var path = UsageBarRenderer.RoundedRect(RectangleF.Inflate(rect, -width / 2f, -width / 2f), radius);
            using var pen = new Pen(NoDataOutline, width);
            g.DrawPath(pen, path);
        }
    }

    /// <summary>Month names above the column that holds the month's first day, left out when
    /// they would crowd the previous one. The first column gets its month too if the next
    /// label is far enough away.</summary>
    private void DrawMonthLabels(Graphics g, DateOnly firstMonday, DateOnly today)
    {
        var labels = new List<(int Column, int Month)>();
        for (var column = 0; column < Weeks; column++)
        {
            var monday = firstMonday.AddDays(column * 7);
            for (var i = 0; i < 7; i++)
            {
                var date = monday.AddDays(i);
                if (date.Day == 1 && date <= today.AddDays(6 - ((int)today.DayOfWeek + 6) % 7))
                    labels.Add((column, date.Month));
            }
        }
        if (labels.Count == 0 || labels[0].Column >= 3)
            labels.Insert(0, (0, firstMonday.Month));

        var y = GridTop - S(MonthRowHeight);
        var lastColumn = -Weeks;
        foreach (var (column, month) in labels)
        {
            if (column - lastColumn < 3)
                continue;
            var x = CellRect(column, 0).X;
            DrawText(g, Strings.MonthShort(month), _labelFont, _palette.Secondary,
                new RectangleF(x, y, S(3 * Pitch + 10f), S(MonthRowHeight - 3f)), TextFormatFlags.NoPadding);
            lastColumn = column;
        }
    }

    /// <summary>Explanation on the left; "No data □   Less ■■■■■ More" on the right.</summary>
    private void DrawLegend(Graphics g, float left, float right, float y)
    {
        var size = MathF.Round(S(Cell));
        var top = MathF.Round(y + (S(LegendHeight) - size) / 2f);
        const TextFormatFlags flags = TextFormatFlags.NoPadding;

        float TextWidth(string text) => TextRenderer.MeasureText(text, _smallFont, Size.Empty, flags).Width;

        var x = right - TextWidth(Strings.HeatmapMore);
        DrawText(g, Strings.HeatmapMore, _smallFont, _palette.Secondary, new RectangleF(x, y, TextWidth(Strings.HeatmapMore) + S(2f), S(LegendHeight)), flags);
        x -= S(6f);
        for (var level = 4; level >= 0; level--)
        {
            x -= size;
            DrawDay(g, new RectangleF(MathF.Round(x), top, size, size), level);
            x -= S(Gap);
        }
        x -= S(3f) + TextWidth(Strings.HeatmapLess);
        DrawText(g, Strings.HeatmapLess, _smallFont, _palette.Secondary, new RectangleF(x, y, TextWidth(Strings.HeatmapLess) + S(2f), S(LegendHeight)), flags);

        x -= S(18f) + size;
        DrawDay(g, new RectangleF(MathF.Round(x), top, size, size), null);
        x -= S(6f) + TextWidth(Strings.HeatmapNoData);
        DrawText(g, Strings.HeatmapNoData, _smallFont, _palette.Secondary, new RectangleF(x, y, TextWidth(Strings.HeatmapNoData) + S(2f), S(LegendHeight)), flags);

        DrawText(g, Strings.HeatmapExplanation, _smallFont, _palette.Secondary,
            new RectangleF(left, y, Math.Max(0, x - S(16f) - left), S(LegendHeight)), flags);
    }

    /// <summary>The day's values in a small box above the square (below it if there's no
    /// room), kept inside the window.</summary>
    private void DrawHover(Graphics g, RectangleF cell, string text)
    {
        var textSize = TextRenderer.MeasureText(text, _smallFont, Size.Empty, TextFormatFlags.NoPadding);
        var box = new RectangleF(0, 0, textSize.Width + S(12f), textSize.Height + S(6f));
        box.X = Math.Clamp(cell.X + cell.Width / 2f - box.Width / 2f, S(4f), Math.Max(S(4f), ClientSize.Width - S(4f) - box.Width));
        box.Y = cell.Y - S(6f) - box.Height;
        if (box.Y < S(2f))
            box.Y = cell.Bottom + S(6f);

        using (var path = UsageBarRenderer.RoundedRect(box, S(4f)))
        using (var fill = new SolidBrush(_palette.Background))
        using (var border = new Pen(_palette.Border, Math.Max(1f, S(1f))))
        {
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }
        DrawText(g, text, _smallFont, _palette.Text, box, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
    }

    /// <summary>"128 active days in the last year · busiest week: 98% (3–9 Mar)" - over the
    /// days shown. The busiest week is a calendar week (Monday to Sunday).</summary>
    private string SummaryText(Dictionary<DateOnly, DailyUsageRecord> days, DateOnly firstMonday, DateOnly today)
    {
        if (!_state.LoggedIn)
            return Strings.MenuNotLoggedIn;

        var shown = days.Values.Where(d => d.Date >= firstMonday && d.Date <= today).ToList();
        var activeDays = shown.Count(d => Level(d.WeekPoints) > 0);
        if (activeDays == 0)
            return Strings.HeatmapNoUsageYet;

        var busiest = shown
            .GroupBy(d => Position(d.Date, firstMonday).Column)
            .Select(week => (Column: week.Key, Points: week.Sum(d => d.WeekPoints)))
            .OrderByDescending(week => week.Points)
            .ThenByDescending(week => week.Column)
            .First();
        return Strings.HeatmapSummary(activeDays, busiest.Points, firstMonday.AddDays(busiest.Column * 7));
    }

    private static string DayText(DateOnly date, Dictionary<DateOnly, DailyUsageRecord> days) =>
        days.TryGetValue(date, out var day)
            ? Strings.HeatmapDayText(date, day.WeekPoints, day.SessionPeak, day.ActiveHours)
            : Strings.HeatmapDayText(date, null, 0, 0);

    private static void DrawText(Graphics g, string text, Font font, Color color, RectangleF rect, TextFormatFlags extra = 0) =>
        UsageBarRenderer.DrawText(g, text, font, color, rect, extra);

    // ---------------------------------------------------------------- hover & keyboard

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Highlight(DayAt(e.Location));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        Highlight(null);
    }

    /// <summary>The day whose square (including half the gap around it) is at <paramref
    /// name="point"/>, if it's shown.</summary>
    private DateOnly? DayAt(Point point)
    {
        var column = (int)Math.Floor((point.X - GridLeft + S(Gap / 2f)) / S(Pitch));
        var row = (int)Math.Floor((point.Y - GridTop + S(Gap / 2f)) / S(Pitch));
        if (column is < 0 or >= Weeks || row is < 0 or > 6)
            return null;
        var today = Today;
        var date = FirstMonday(today).AddDays(column * 7 + row);
        return date <= today ? date : null;
    }

    /// <summary>Arrow keys move the highlighted day (up/down within a week, left/right by a
    /// week), starting at today; Esc closes the window.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var step = keyData switch
        {
            Keys.Up => -1,
            Keys.Down => 1,
            Keys.Left => -7,
            Keys.Right => 7,
            _ => 0,
        };
        if (step != 0)
        {
            var today = Today;
            var next = _selected is { } current ? current.AddDays(step) : today;
            if (next >= FirstMonday(today) && next <= today)
                Highlight(next);
            return true;
        }
        if (keyData == Keys.Escape)
        {
            Close();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void Highlight(DateOnly? date)
    {
        if (date == _selected)
            return;
        _selected = date;
        UpdateAccessibleDescription();
        Invalidate();
    }

    /// <summary>For screen readers: the summary, plus the highlighted day's values.</summary>
    private void UpdateAccessibleDescription()
    {
        var today = Today;
        var days = _state.Days.ToDictionary(d => d.Date);
        var text = SummaryText(days, FirstMonday(today), today);
        if (_selected is { } selected)
            text = DayText(selected, days) + ". " + text;
        AccessibleDescription = text;
        AccessibilityNotifyClients(AccessibleEvents.DescriptionChange, -1);
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        private readonly struct POINT(int x, int y)
        {
            public readonly int X = x;
            public readonly int Y = y;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>Effective DPI of the monitor at <paramref name="point"/>, else <paramref name="fallback"/>.</summary>
        public static int DpiAt(Point point, int fallback)
        {
            try
            {
                var monitor = MonitorFromPoint(new POINT(point.X, point.Y), 2 /* MONITOR_DEFAULTTONEAREST */);
                if (GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out var dpiX, out _) == 0 && dpiX > 0)
                    return (int)dpiX;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                // Fall through.
            }
            return fallback;
        }
    }
}
