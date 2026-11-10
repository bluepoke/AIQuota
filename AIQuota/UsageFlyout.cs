using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using AIQuota.History;
using AIQuota.Localization;

namespace AIQuota;

/// <summary>Everything the flyout shows, pulled from <see cref="UsageTrayContext"/> whenever
/// it opens or the usage state changes - the flyout itself never calls the API.</summary>
internal sealed record FlyoutState(
    bool LoggedIn,
    string? AccountLabel,
    string StatusText,
    bool IsRefreshing,
    UsageSnapshot? Snapshot,
    int? CreditPercent,
    bool SessionNotStarted,
    bool ShowPaceMarker,
    UsageForecast SessionForecast,
    UsageForecast WeeklyForecast,
    IReadOnlyList<UsageHistoryRecord> History,
    bool Greyscale = false);

/// <summary>
/// Small borderless panel shown next to the tray icon on left-click, like the Windows volume
/// and network flyouts: the current usage at a readable size (session, week with its pace
/// marker, credits, burn-rate forecast) plus a chart of the past 7 days of weekly usage
/// against the "even pace" line.
///
/// It's one custom-painted form - laid out in 96-DPI logical pixels and scaled to the DPI of
/// the monitor it opens on - with a single real <see cref="Button"/> for refresh, so
/// keyboard (Tab) and screen-reader users reach it. It's created once and only hidden
/// between uses, so opening is instant; it closes on Esc, on a click anywhere else
/// (deactivation) and on another click on the tray icon.
/// </summary>
internal sealed class UsageFlyout : Form
{
    private const float LogicalWidth = 340f;
    private const float Pad = 16f;
    private const float ChartHeight = 84f;
    private const float ChartLeftInset = 36f;

    /// <summary>History samples further apart than this are drawn as a gap (PC off, app not
    /// running) - a few missed 5-minute polls still count as one line.</summary>
    private static readonly TimeSpan ChartGap = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan ChartRange = TimeSpan.FromDays(7);
    /// <summary>The chart is drawn once there are at least <see cref="MinChartPoints"/> samples
    /// in the past 7 days, the first and last at least <see cref="MinChartData"/> apart.</summary>
    private const int MinChartPoints = 2;
    private static readonly TimeSpan MinChartData = TimeSpan.FromHours(1);
    private static readonly TimeSpan SameWindowTolerance = TimeSpan.FromMinutes(10);

    private readonly Func<FlyoutState> _getState;
    private readonly Func<Task> _refresh;
    private readonly Button _refreshButton;
    private readonly ToolTip _toolTip = new();
    private readonly System.Windows.Forms.Timer _clockTimer = new() { Interval = 30_000 };
    private readonly System.Windows.Forms.Timer _reactivateCheck = new() { Interval = 150 };

    private FlyoutState _state;
    private Palette _palette = Palette.For(SystemTheme.IsLightTaskbar());
    private UsagePalette _usage = UsagePalette.Standard(SystemTheme.IsLightTaskbar());
    private float _scale = 1f;
    private float _fontScale;
    private Point _anchor;
    private long _shownAt;
    private long _hiddenAt;

    private Font _titleFont = null!;
    private Font _bodyFont = null!;
    private Font _smallFont = null!;
    private Font _iconFont = null!;

    // Chart geometry and points from the last paint, for hover hit-testing.
    private RectangleF _plot;
    private List<(PointF Point, UsageHistoryRecord Record)> _chartPoints = [];
    private int? _hoverIndex;

    public UsageFlyout(Func<FlyoutState> getState, Func<Task> refresh)
    {
        _getState = getState;
        _refresh = refresh;
        _state = getState();

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.None;
        Text = Strings.AppTitle;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        _refreshButton = new Button
        {
            Text = "", // Segoe MDL2 Assets "Refresh"
            FlatStyle = FlatStyle.Flat,
            TabStop = true,
            UseVisualStyleBackColor = false,
        };
        _refreshButton.FlatAppearance.BorderSize = 0;
        _refreshButton.Click += async (_, _) => await _refresh();
        Controls.Add(_refreshButton);

        _clockTimer.Tick += (_, _) => Invalidate();
        _reactivateCheck.Tick += (_, _) =>
        {
            _reactivateCheck.Stop();
            if (Visible && ActiveForm != this)
                HideFlyout();
        };

        ApplyLayout();
    }

    /// <summary>Left-click on the tray icon: opens the flyout at <paramref name="cursor"/>,
    /// or closes it if it's open.</summary>
    public void Toggle(Point cursor)
    {
        if (Visible)
        {
            HideFlyout();
            return;
        }

        // Clicking the tray icon while the flyout is open first deactivates it (the click goes
        // to the taskbar), which already hid it - this click shouldn't reopen it.
        if (Environment.TickCount64 - _hiddenAt < 400)
            return;

        ShowAt(cursor);
    }

    /// <summary>Re-reads the state from the tray context and redraws - after a refresh, a
    /// setting change, or the theme changing.</summary>
    public void UpdateState()
    {
        _state = _getState();
        if (!Visible)
            return;
        ApplyTheme();
        ApplyLayout();
        Location = ComputeLocation(_anchor, Size);
        Invalidate();
    }

    private void ShowAt(Point anchor)
    {
        _anchor = anchor;
        _state = _getState();
        ApplyTheme();
        _scale = DpiAt(anchor) / 96f;
        _hoverIndex = null;
        ApplyLayout();
        Location = ComputeLocation(anchor, Size);

        Show();
        Activate();
        _refreshButton.Focus();
        _shownAt = Environment.TickCount64;
        _clockTimer.Start();
    }

    /// <summary>Picks the flyout and usage colours for the current Windows theme and the
    /// accessibility mode.</summary>
    private void ApplyTheme()
    {
        var light = SystemTheme.IsLightTaskbar();
        _palette = Palette.For(light);
        _usage = UsagePalette.For(_state.Greyscale, light);
    }

    private void HideFlyout()
    {
        Hide();
        _hiddenAt = Environment.TickCount64;
        _clockTimer.Stop();
        _hoverIndex = null;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int CS_DROPSHADOW = 0x20000;
            const int WS_EX_TOOLWINDOW = 0x80; // keeps it out of Alt+Tab
            var cp = base.CreateParams;
            cp.ClassStyle |= CS_DROPSHADOW;
            cp.ExStyle |= WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Windows 11: round the corners like the system flyouts (Windows 10 ignores this and
        // gets a plain 1px border from OnPaint instead).
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        var round = 2; // DWMWCP_ROUND
        _ = NativeMethods.DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            HideFlyout();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Closes on any click outside the flyout - except the second click of a
    /// double-click on the tray icon: it lands on the taskbar right after the first click
    /// opened the flyout, and would otherwise close it straight away. In that case the
    /// flyout stays open and takes the activation back; should Windows not allow that, it
    /// closes after all (an open flyout that isn't active would never see the next outside
    /// click).</summary>
    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!Visible)
            return;

        if (IsSecondClickOfDoubleClick())
        {
            _shownAt = 0;
            BeginInvoke(() =>
            {
                Activate();
                _reactivateCheck.Start();
            });
            return;
        }

        HideFlyout();
    }

    private bool IsSecondClickOfDoubleClick()
    {
        var cursor = Cursor.Position;
        var size = SystemInformation.DoubleClickSize;
        return Environment.TickCount64 - _shownAt <= SystemInformation.DoubleClickTime
            && Math.Abs(cursor.X - _anchor.X) <= size.Width
            && Math.Abs(cursor.Y - _anchor.Y) <= size.Height;
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        // Lay out for the new DPI ourselves instead of taking Windows' suggested bounds.
        e.Cancel = true;
        base.OnDpiChanged(e);
        _scale = e.DeviceDpiNew / 96f;
        ApplyLayout();
        Location = ComputeLocation(_anchor, Size);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _clockTimer.Dispose();
            _reactivateCheck.Dispose();
            _toolTip.Dispose();
            DisposeFonts();
        }
        base.Dispose(disposing);
    }

    // ---------------------------------------------------------------- layout & positioning

    private float S(float logical) => logical * _scale;

    /// <summary>Recreates the fonts for the current scale, sizes the window to its content
    /// (which varies: credits, forecast lines, logged out) and places the refresh button.</summary>
    private void ApplyLayout()
    {
        UpdateFonts();

        var height = (int)Math.Ceiling(DrawContent(null, DateTimeOffset.Now));
        ClientSize = new Size((int)Math.Ceiling(S(LogicalWidth)), height);

        var buttonSize = (int)Math.Round(S(32f));
        _refreshButton.Size = new Size(buttonSize, buttonSize);
        _refreshButton.Location = new Point(ClientSize.Width - (int)Math.Round(S(Pad - 4f)) - buttonSize, (int)Math.Round(S(Pad - 6f)));
        _refreshButton.Enabled = _state is { LoggedIn: true, IsRefreshing: false };
        _refreshButton.BackColor = _palette.Background;
        _refreshButton.ForeColor = _palette.Text;
        _refreshButton.FlatAppearance.MouseOverBackColor = _palette.Hover;
        _refreshButton.FlatAppearance.MouseDownBackColor = _palette.Track;
        _refreshButton.AccessibleName = Strings.MenuRefresh;
        _toolTip.SetToolTip(_refreshButton, Strings.MenuRefresh);
        BackColor = _palette.Background;
    }

    /// <summary>Creates the fonts for the current scale - only when the scale changed. The
    /// button gets its new font before the old one is disposed: WinForms ignores assigning a
    /// font that is equal (same name and size) to the current one and would keep painting
    /// with the disposed font ("Parameter is not valid").</summary>
    private void UpdateFonts()
    {
        if (_fontScale == _scale && _titleFont is not null)
            return;

        var (oldTitle, oldBody, oldSmall, oldIcon) = (_titleFont, _bodyFont, _smallFont, _iconFont);
        _titleFont = new Font("Segoe UI Semibold", S(14f), FontStyle.Regular, GraphicsUnit.Pixel);
        _bodyFont = new Font("Segoe UI", S(12f), FontStyle.Regular, GraphicsUnit.Pixel);
        _smallFont = new Font("Segoe UI", S(11f), FontStyle.Regular, GraphicsUnit.Pixel);
        _iconFont = new Font("Segoe MDL2 Assets", S(14f), FontStyle.Regular, GraphicsUnit.Pixel);
        _refreshButton.Font = _iconFont;
        _fontScale = _scale;

        oldTitle?.Dispose();
        oldBody?.Dispose();
        oldSmall?.Dispose();
        oldIcon?.Dispose();
    }

    private void DisposeFonts()
    {
        _titleFont?.Dispose();
        _bodyFont?.Dispose();
        _smallFont?.Dispose();
        _iconFont?.Dispose();
    }

    /// <summary>Places the flyout against the taskbar, next to where the tray icon was
    /// clicked: the taskbar's edge is the side where the screen's working area stops short
    /// of its full bounds. With an auto-hiding taskbar (no difference) it opens just above
    /// the cursor. Always kept fully inside the working area.</summary>
    private Point ComputeLocation(Point anchor, Size size)
    {
        var screen = Screen.FromPoint(anchor);
        var work = screen.WorkingArea;
        var bounds = screen.Bounds;
        var margin = (int)Math.Round(S(12f));

        int x, y;
        if (work.Top > bounds.Top)
        {
            x = anchor.X - size.Width / 2;
            y = work.Top + margin;
        }
        else if (work.Left > bounds.Left)
        {
            x = work.Left + margin;
            y = anchor.Y - size.Height / 2;
        }
        else if (work.Right < bounds.Right)
        {
            x = work.Right - size.Width - margin;
            y = anchor.Y - size.Height / 2;
        }
        else
        {
            x = anchor.X - size.Width / 2;
            y = work.Bottom < bounds.Bottom ? work.Bottom - size.Height - margin : anchor.Y - size.Height - margin;
        }

        x = Math.Clamp(x, work.Left + margin, Math.Max(work.Left + margin, work.Right - size.Width - margin));
        y = Math.Clamp(y, work.Top + margin, Math.Max(work.Top + margin, work.Bottom - size.Height - margin));
        return new Point(x, y);
    }

    private int DpiAt(Point point)
    {
        try
        {
            var monitor = NativeMethods.MonitorFromPoint(new NativeMethods.POINT(point.X, point.Y), 2 /* MONITOR_DEFAULTTONEAREST */);
            if (NativeMethods.GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out var dpiX, out _) == 0 && dpiX > 0)
                return (int)dpiX;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Fall through to the DPI the window currently has.
        }
        return DeviceDpi;
    }

    // ---------------------------------------------------------------- painting

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(_palette.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // In accessibility mode, greyscale text smoothing instead of ClearType, whose coloured
        // subpixel fringes would be the only hue left in the flyout. TextRenderer picks the
        // GDI font quality from this hint.
        if (_usage.IsGreyscale)
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        DrawContent(g, DateTimeOffset.Now);

        if (Environment.OSVersion.Version.Build < 22000)
        {
            using var border = new Pen(_palette.Border);
            g.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }
    }

    /// <summary>Draws the whole flyout top to bottom and returns the height it needs. With
    /// <paramref name="g"/> null it only measures, which is how <see cref="ApplyLayout"/>
    /// sizes the window - so layout and painting can't drift apart.</summary>
    private float DrawContent(Graphics? g, DateTimeOffset now)
    {
        var left = S(Pad);
        var width = S(LogicalWidth - 2 * Pad);
        var y = S(Pad);
        var headerTextWidth = width - S(36f);

        // Header: account and last update / status, refresh button on the right.
        var title = _state.LoggedIn ? _state.AccountLabel ?? Strings.AppTitle : Strings.AppTitle;
        DrawText(g, title, _titleFont, _palette.Text, new RectangleF(left, y, headerTextWidth, S(20f)));
        y += S(20f);
        var status = _state.IsRefreshing ? Strings.FlyoutRefreshing : _state.StatusText;
        DrawText(g, status, _smallFont, _palette.Secondary, new RectangleF(left, y, headerTextWidth, S(18f)));
        y += S(18f) + S(10f);

        if (g is not null)
        {
            using var separator = new Pen(_palette.Border, Math.Max(1f, S(1f)));
            g.DrawLine(separator, 0, y, S(LogicalWidth), y);
        }
        y += S(1f) + S(12f);

        if (!_state.LoggedIn)
        {
            DrawText(g, Strings.MenuNotLoggedIn, _bodyFont, _palette.Text, new RectangleF(left, y, width, S(20f)));
            return y + S(20f) + S(Pad);
        }

        // Logged in, but nothing fetched yet (first refresh running, or it failed - the
        // header's status line says which).
        if (_state.Snapshot is not { } snapshot)
            return y - S(12f) + S(Pad);

        // Session - with a pace marker like the week's (the same setting turns both on/off),
        // here for an even spread over the 5 hours. None before a session starts: there's no
        // clock running yet.
        double? sessionPace = !_state.SessionNotStarted && snapshot.SessionResetsAt is { } resetForPace
            ? UsageWindows.SessionPaceFraction(resetForPace, now)
            : null;
        y = DrawTitleRow(g, Strings.FlyoutSessionTitle, $"{snapshot.SessionPercent}%", left, y, width);
        DrawBar(g, new RectangleF(left, y, width, S(8f)), snapshot.SessionPercent, _state.SessionNotStarted,
            paceFraction: _state.ShowPaceMarker ? sessionPace : null);
        y += S(8f) + S(6f);
        var sessionDetail = _state.SessionNotStarted || snapshot.SessionResetsAt is not { } sessionReset
            ? Strings.FlyoutSessionNotStarted
            : Strings.FlyoutResetLine(sessionReset, now)
                + (_state.ShowPaceMarker && sessionPace is { } sp ? Strings.PaceSuffix((int)Math.Round(sp * 100)) : "");
        DrawText(g, sessionDetail, _bodyFont, _palette.Secondary, new RectangleF(left, y, width, S(18f)));
        y += S(18f);
        y = DrawForecast(g, _state.SessionForecast, left, y, width);
        y += S(14f);

        // Week
        double? pace = snapshot.WeeklyResetsAt is { } weekReset ? UsageWindows.WeeklyPaceFraction(weekReset, now) : null;
        y = DrawTitleRow(g, Strings.FlyoutWeekTitle, $"{snapshot.WeeklyPercent}%", left, y, width);
        DrawBar(g, new RectangleF(left, y, width, S(8f)), snapshot.WeeklyPercent, outlineOnly: false,
            paceFraction: _state.ShowPaceMarker ? pace : null);
        y += S(8f) + S(6f);
        if (snapshot.WeeklyResetsAt is { } weeklyReset)
        {
            var weekDetail = Strings.FlyoutResetLine(weeklyReset, now)
                + (_state.ShowPaceMarker && pace is { } p ? Strings.PaceSuffix((int)Math.Round(p * 100)) : "");
            DrawText(g, weekDetail, _bodyFont, _palette.Secondary, new RectangleF(left, y, width, S(18f)));
            y += S(18f);
        }
        y = DrawForecast(g, _state.WeeklyForecast, left, y, width);
        y += S(14f);

        // Credits
        if (_state.CreditPercent is { } creditPercent &&
            snapshot is { CreditUsed: { } used, CreditLimit: { } limit, CreditCurrency: { } currency })
        {
            y = DrawTitleRow(g, Strings.FlyoutCreditsTitle, $"{creditPercent}%", left, y, width);
            DrawBar(g, new RectangleF(left, y, width, S(8f)), creditPercent, outlineOnly: false, paceFraction: null);
            y += S(8f) + S(6f);
            DrawText(g, Strings.CreditsAmount(used, limit, currency), _bodyFont, _palette.Secondary, new RectangleF(left, y, width, S(18f)));
            y += S(18f) + S(14f);
        }

        // 7-day chart
        DrawText(g, Strings.FlyoutHistoryTitle, _titleFont, _palette.Text, new RectangleF(left, y, width / 2f, S(20f)));
        DrawLegend(g, snapshot, left + width, y + S(10f));
        y += S(20f) + S(8f);
        DrawChart(g, new RectangleF(left + S(ChartLeftInset), y, width - S(ChartLeftInset), S(ChartHeight)), snapshot, now);
        y += S(ChartHeight) + S(4f) + S(14f);

        return y + S(Pad);
    }

    private float DrawTitleRow(Graphics? g, string title, string value, float left, float y, float width)
    {
        DrawText(g, title, _bodyFont, _palette.Text, new RectangleF(left, y, width - S(60f), S(20f)));
        DrawText(g, value, _titleFont, _palette.Text, new RectangleF(left + width - S(60f), y, S(60f), S(20f)), TextFormatFlags.Right);
        return y + S(20f) + S(6f);
    }

    private float DrawForecast(Graphics? g, UsageForecast forecast, float left, float y, float width)
    {
        var (text, color) = forecast.Kind switch
        {
            ForecastKind.Limit => (Strings.ForecastLimitInText(forecast.TimeToLimit), _palette.Text),
            ForecastKind.ResetFirst => (Strings.ForecastResetFirstText, _palette.Secondary),
            _ => ((string?)null, _palette.Secondary),
        };
        if (text is null)
            return y;

        DrawText(g, "↳ " + text, _bodyFont, color, new RectangleF(left, y, width, S(18f)));
        return y + S(18f);
    }

    /// <summary>A large usage bar: the same track/fill/colour rules as the tray icon's (an
    /// outline-only track before a session starts; in the greyscale palette an outlined track
    /// and a notch above 80%), plus - for the session and the week - the pace marker as a
    /// tick that sticks out a little above and below the bar.</summary>
    private void DrawBar(Graphics? g, RectangleF rect, int percent, bool outlineOnly, double? paceFraction)
    {
        if (g is null)
            return;

        var clamped = Math.Clamp(percent, 0, 100);
        var radius = rect.Height / 2f;
        using (var trackPath = RoundedRect(rect, radius))
        {
            // The greyscale palette brings its own track (with an outline for contrast);
            // the standard one uses the flyout's neutral grey.
            var greyscaleTrack = _usage.TrackOutline;
            if (outlineOnly)
            {
                using var outline = new Pen(greyscaleTrack is null ? _palette.Track : _usage.NotStarted, S(1.5f));
                g.DrawPath(outline, trackPath);
            }
            else
            {
                using var track = new SolidBrush(greyscaleTrack is null ? _palette.Track : _usage.Track);
                g.FillPath(track, trackPath);
                if (greyscaleTrack is { } outlineColor)
                {
                    using var outline = new Pen(outlineColor, S(1f));
                    using var insetPath = RoundedRect(RectangleF.Inflate(rect, -S(0.5f), -S(0.5f)), radius - S(0.5f));
                    g.DrawPath(outline, insetPath);
                }
            }
        }

        if (clamped > 0)
        {
            var fillWidth = Math.Max(rect.Width * clamped / 100f, rect.Height);
            var fillRect = new RectangleF(rect.X, rect.Y, Math.Min(fillWidth, rect.Width), rect.Height);
            using var fillPath = RoundedRect(fillRect, radius);
            using var fill = new SolidBrush(_usage.ForPercent(clamped));
            g.FillPath(fill, fillPath);

            if (_usage.MarksHighLevel && UsagePalette.IsHigh(clamped))
            {
                // Same notch as the tray icon: a contrasting gap just inside the fill's end.
                var previousClip = g.Clip;
                g.SetClip(fillPath, CombineMode.Intersect);
                using var notch = new SolidBrush(_usage.HighNotch);
                g.FillRectangle(notch, fillRect.Right - S(4f) - S(2f), fillRect.Y, S(2f), fillRect.Height);
                g.Clip = previousClip;
            }
        }

        if (paceFraction is { } pace)
        {
            var x = rect.X + rect.Width * (float)Math.Clamp(pace, 0.0, 1.0);
            var core = S(2f);
            var outline = S(1.5f);
            var top = rect.Y - S(3f);
            var height = rect.Height + S(6f);
            using var outlineBrush = new SolidBrush(_palette.Background);
            using var coreBrush = new SolidBrush(_palette.Text);
            g.FillRectangle(outlineBrush, x - core / 2f - outline, top - outline, core + 2 * outline, height + 2 * outline);
            g.FillRectangle(coreBrush, x - core / 2f, top, core, height);
        }
    }

    private void DrawLegend(Graphics? g, UsageSnapshot snapshot, float right, float centerY)
    {
        if (g is null)
            return;

        var sample = S(14f);
        var gap = S(6f);
        var paceText = Strings.FlyoutLegendPace;
        var usageText = Strings.FlyoutLegendUsage;
        var paceWidth = TextRenderer.MeasureText(paceText, _smallFont, Size.Empty, TextFormatFlags.NoPadding).Width;
        var usageWidth = TextRenderer.MeasureText(usageText, _smallFont, Size.Empty, TextFormatFlags.NoPadding).Width;

        var x = right - paceWidth;
        DrawText(g, paceText, _smallFont, _palette.Secondary, new RectangleF(x, centerY - S(8f), paceWidth + S(2f), S(16f)), TextFormatFlags.NoPadding);
        x -= gap + sample;
        using (var pacePen = PacePen())
            g.DrawLine(pacePen, x, centerY, x + sample, centerY);

        x -= S(12f) + usageWidth;
        DrawText(g, usageText, _smallFont, _palette.Secondary, new RectangleF(x, centerY - S(8f), usageWidth + S(2f), S(16f)), TextFormatFlags.NoPadding);
        x -= gap + sample;
        using var usagePen = new Pen(_usage.ForPercent(snapshot.WeeklyPercent), S(2f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(usagePen, x, centerY, x + sample, centerY);
    }

    private Pen PacePen() => new(_palette.Pace, S(1.5f)) { DashStyle = DashStyle.Dash };

    /// <summary>
    /// The past 7 days of weekly usage, from the local history: one line per stretch of
    /// unbroken data, coloured by level like the bars, with gaps where the app wasn't
    /// running. Behind it, the dashed "even pace" line of each weekly window - 0% at the
    /// window's start to 100% at its reset - which, since 7 days normally span a reset,
    /// makes a sawtooth, with a dotted divider at the reset. The current value is a dot at
    /// the right edge, the current pace a ring on the pace line. Hovering shows the time and
    /// value of the nearest sample.
    /// </summary>
    private void DrawChart(Graphics? g, RectangleF plot, UsageSnapshot snapshot, DateTimeOffset now)
    {
        _plot = plot;
        if (g is null)
            return;

        var rangeStart = now - ChartRange;
        float X(DateTimeOffset t) => plot.Left + (float)((t - rangeStart) / ChartRange) * plot.Width;
        float Y(double percent) => plot.Bottom - (float)(Math.Clamp(percent, 0, 100) / 100.0) * plot.Height;

        // Grid: 0 / 50 / 100% lines with labels, day boundaries with weekday labels.
        using (var grid = new Pen(_palette.Grid, Math.Max(1f, S(1f))))
        {
            foreach (var level in new[] { 0, 50, 100 })
            {
                var y = Y(level);
                g.DrawLine(grid, plot.Left, y, plot.Right, y);
                DrawText(g, $"{level}%", _smallFont, _palette.Secondary,
                    new RectangleF(plot.Left - S(ChartLeftInset), y - S(8f), S(ChartLeftInset - 5f), S(16f)), TextFormatFlags.Right | TextFormatFlags.NoPadding);
            }

            var localNow = now.ToLocalTime();
            var midnight = new DateTimeOffset(localNow.Date, localNow.Offset);
            for (var day = midnight; day > rangeStart - TimeSpan.FromDays(1); day -= TimeSpan.FromDays(1))
            {
                if (day > rangeStart)
                    g.DrawLine(grid, X(day), plot.Top, X(day), plot.Bottom);

                var labelStart = day < rangeStart ? rangeStart : day;
                var labelEnd = day + TimeSpan.FromDays(1) > now ? now : day + TimeSpan.FromDays(1);
                var labelWidth = X(labelEnd) - X(labelStart);
                if (labelWidth >= S(22f))
                {
                    DrawText(g, Strings.WeekdayShort(day), _smallFont, _palette.Secondary,
                        new RectangleF(X(labelStart), plot.Bottom + S(3f), labelWidth, S(14f)), TextFormatFlags.HorizontalCenter);
                }
            }
        }

        var points = _state.History.Where(r => r.Time >= rangeStart && r.Time <= now).ToList();
        _chartPoints = points.Select(r => (new PointF(X(r.Time), Y(r.Week)), r)).ToList();

        if (points.Count < MinChartPoints || points[^1].Time - points[0].Time < MinChartData)
        {
            _chartPoints = [];
            var hint = RectangleF.Inflate(plot, -S(8f), -S(10f));
            using (var path = RoundedRect(hint, S(4f)))
            using (var fill = new SolidBrush(_palette.Background))
                g.FillPath(fill, path);
            // The span is measured from the oldest sample in range, and new ones keep coming
            // every few minutes while the app runs - so it's reached at oldest + MinChartData.
            // With no sample yet, the first refresh starts the clock.
            var remaining = points.Count > 0 ? points[0].Time + MinChartData - now : MinChartData;
            DrawText(g, Strings.FlyoutCollectingData(MinChartPoints, MinChartData, remaining), _smallFont, _palette.Secondary, hint,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
            return;
        }

        var previousClip = g.Clip;
        g.SetClip(RectangleF.Inflate(plot, S(5f), S(5f)));

        // Even-pace sawtooth and reset divider, from the current window's reset time.
        double? paceNow = null;
        if (snapshot.WeeklyResetsAt is { } reset)
        {
            var windowStart = UsageWindows.WeekStart(reset);
            paceNow = UsageWindows.WeeklyPaceFraction(reset, now) * 100;
            using var pacePen = PacePen();
            if (windowStart > rangeStart)
            {
                // The previous window reset at windowStart.
                var paceAtRangeStart = UsageWindows.WeeklyPaceFraction(windowStart, rangeStart) * 100;
                g.DrawLine(pacePen, X(rangeStart), Y(paceAtRangeStart), X(windowStart), Y(100));

                using var divider = new Pen(_palette.Secondary, S(1f)) { DashStyle = DashStyle.Dot };
                g.DrawLine(divider, X(windowStart), plot.Top, X(windowStart), plot.Bottom);
            }
            var from = windowStart > rangeStart ? windowStart : rangeStart;
            g.DrawLine(pacePen, X(from), Y(UsageWindows.WeeklyPaceFraction(reset, from) * 100), X(now), Y(paceNow.Value));
        }

        // Usage line, split at gaps and at window changes (where it drops back to ~0).
        for (var i = 1; i < points.Count; i++)
        {
            var a = points[i - 1];
            var b = points[i];
            if (b.Time - a.Time > ChartGap || !SameWindow(a.WeekResetsAt, b.WeekResetsAt))
                continue;
            using var pen = new Pen(_usage.ForPercent((int)Math.Round(b.Week)), S(2f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(pen, _chartPoints[i - 1].Point, _chartPoints[i].Point);
        }

        // Isolated samples (no neighbour within the gap) as small dots, so they don't vanish.
        for (var i = 0; i < points.Count; i++)
        {
            var connectedBefore = i > 0 && points[i].Time - points[i - 1].Time <= ChartGap && SameWindow(points[i - 1].WeekResetsAt, points[i].WeekResetsAt);
            var connectedAfter = i < points.Count - 1 && points[i + 1].Time - points[i].Time <= ChartGap && SameWindow(points[i].WeekResetsAt, points[i + 1].WeekResetsAt);
            if (!connectedBefore && !connectedAfter)
                FillDot(g, _chartPoints[i].Point, S(1.5f), _usage.ForPercent((int)Math.Round(points[i].Week)));
        }

        // Current pace (ring on the pace line) and current value (dot) at the right edge.
        if (paceNow is { } paceValue)
        {
            using var ring = new Pen(_palette.Pace, S(1.5f));
            var r = S(3.5f);
            g.DrawEllipse(ring, X(now) - r, Y(paceValue) - r, 2 * r, 2 * r);
        }
        var current = snapshot.WeeklyUtilization ?? snapshot.WeeklyPercent;
        FillDot(g, new PointF(X(now), Y(current)), S(5f), _palette.Background);
        FillDot(g, new PointF(X(now), Y(current)), S(3.5f), _usage.ForPercent(snapshot.WeeklyPercent));

        g.Clip = previousClip;

        if (_hoverIndex is { } hover && hover < _chartPoints.Count)
            DrawHover(g, plot, _chartPoints[hover].Point, _chartPoints[hover].Record);
    }

    private void DrawHover(Graphics g, RectangleF plot, PointF point, UsageHistoryRecord record)
    {
        using (var line = new Pen(_palette.Secondary, S(1f)))
            g.DrawLine(line, point.X, plot.Top, point.X, plot.Bottom);
        FillDot(g, point, S(4f), _palette.Text);

        var text = Strings.FlyoutChartPoint(record.Time, record.Week);
        var textSize = TextRenderer.MeasureText(text, _smallFont, Size.Empty, TextFormatFlags.NoPadding);
        var box = new RectangleF(0, plot.Top + S(2f), textSize.Width + S(10f), textSize.Height + S(4f));
        box.X = point.X + S(6f) + box.Width <= plot.Right ? point.X + S(6f) : point.X - S(6f) - box.Width;

        using (var path = RoundedRect(box, S(4f)))
        using (var fill = new SolidBrush(_palette.Background))
        using (var border = new Pen(_palette.Border, S(1f)))
        {
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }
        DrawText(g, text, _smallFont, _palette.Text, box, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int? hover = null;
        if (_chartPoints.Count > 0 && RectangleF.Inflate(_plot, S(4f), S(4f)).Contains(e.Location))
        {
            var best = double.MaxValue;
            for (var i = 0; i < _chartPoints.Count; i++)
            {
                var distance = Math.Abs(_chartPoints[i].Point.X - e.X);
                if (distance < best)
                {
                    best = distance;
                    hover = i;
                }
            }
            if (best > S(12f))
                hover = null;
        }

        if (hover != _hoverIndex)
        {
            _hoverIndex = hover;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex is not null)
        {
            _hoverIndex = null;
            Invalidate();
        }
    }

    private static bool SameWindow(DateTimeOffset? a, DateTimeOffset? b) =>
        a is { } x && b is { } y ? (x - y).Duration() <= SameWindowTolerance : a is null && b is null;

    private static void FillDot(Graphics g, PointF center, float radius, Color color)
    {
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, center.X - radius, center.Y - radius, 2 * radius, 2 * radius);
    }

    private static void DrawText(Graphics? g, string text, Font font, Color color, RectangleF rect, TextFormatFlags extra = 0)
    {
        if (g is null)
            return;
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | extra;
        if ((extra & TextFormatFlags.WordBreak) == 0)
            flags |= TextFormatFlags.SingleLine;
        TextRenderer.DrawText(g, text, font, Rectangle.Round(rect), color, flags);
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2f, Math.Min(rect.Width, rect.Height));
        if (d <= 0)
        {
            path.AddRectangle(rect);
            return path;
        }
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Flyout colours for the light and dark system theme, close to the Windows 11
    /// flyouts. Usage colours themselves come from <see cref="UsagePalette"/>.</summary>
    private sealed record Palette(Color Background, Color Text, Color Secondary, Color Track, Color Border, Color Grid, Color Pace, Color Hover)
    {
        public static Palette For(bool light) => light
            ? new Palette(
                Background: Color.FromArgb(249, 249, 249),
                Text: Color.FromArgb(26, 26, 26),
                Secondary: Color.FromArgb(96, 96, 96),
                Track: Color.FromArgb(214, 214, 214),
                Border: Color.FromArgb(214, 214, 214),
                Grid: Color.FromArgb(229, 229, 229),
                Pace: Color.FromArgb(70, 70, 70),
                Hover: Color.FromArgb(234, 234, 234))
            : new Palette(
                Background: Color.FromArgb(43, 43, 43),
                Text: Color.FromArgb(255, 255, 255),
                Secondary: Color.FromArgb(178, 178, 178),
                Track: Color.FromArgb(80, 80, 80),
                Border: Color.FromArgb(70, 70, 70),
                Grid: Color.FromArgb(62, 62, 62),
                Pace: Color.FromArgb(205, 205, 205),
                Hover: Color.FromArgb(60, 60, 60));
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        public readonly struct POINT(int x, int y)
        {
            public readonly int X = x;
            public readonly int Y = y;
        }

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

        [DllImport("shcore.dll")]
        public static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}
