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
    bool Greyscale = false,
    bool HasError = false,
    // The active account (SavedAccount.Key), e.g. for the mini dashboard's bar selection per
    // account; null without one.
    string? AccountKey = null,
    // More than one account is saved: the flyout and mini dashboard offer switching, and the
    // mini dashboard names the account.
    bool CanSwitchAccount = false,
    // The other monitored accounts (#18), each as its own state (without history), in the
    // account list's order: summary rows in the flyout, groups in the mini dashboard.
    IReadOnlyList<FlyoutState>? OtherAccounts = null,
    // The account's position in the account list, so views showing several accounts keep a
    // fixed order regardless of which one is active.
    int AccountOrder = 0)
{
    public IReadOnlyList<FlyoutState> Others => OtherAccounts ?? [];
}

/// <summary>
/// Small borderless panel shown next to the tray icon on left-click, like the Windows volume
/// and network flyouts: the current usage at a readable size (session, week with its pace
/// marker, credits, burn-rate forecast) plus a chart of the past 7 days of weekly usage
/// against the "even pace" line. A small calendar button next to the chart's title opens the
/// yearly heatmap (<see cref="UsageHeatmap"/>).
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
    private readonly Action _openMiniDashboard;
    private readonly Action _openHeatmap;
    private readonly Button _refreshButton;
    private readonly Button _miniDashboardButton;
    private readonly Button _heatmapButton;
    private readonly Button _accountButton;
    private readonly ToolTip _toolTip = new();
    private readonly System.Windows.Forms.Timer _clockTimer = new() { Interval = 30_000 };
    private readonly System.Windows.Forms.Timer _reactivateCheck = new() { Interval = 150 };

    private FlyoutState _state;
    private FlyoutPalette _palette = FlyoutPalette.For(SystemTheme.IsLightTaskbar());
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
    /// <summary>For the account button's "▾", which Segoe MDL2 Assets doesn't have.</summary>
    private Font _caretFont = null!;

    // Chart geometry and points from the last paint, for hover hit-testing.
    private RectangleF _plot;
    /// <summary>The 7-day chart's title from the last layout, where the heatmap button goes
    /// next to; empty while there's no chart (logged out, nothing fetched yet).</summary>
    private RectangleF _historyTitle;
    /// <summary>The header's account name from the last layout, where the account button
    /// goes next to; empty while switching isn't offered.</summary>
    private RectangleF _accountTitle;
    private List<(PointF Point, UsageHistoryRecord Record)> _chartPoints = [];
    private int? _hoverIndex;

    /// <summary>Creates the menu items for switching accounts (#13), opened by the ▾ button
    /// next to the account name while more than one account is saved.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<IReadOnlyList<ToolStripItem>>? CreateAccountItems { get; init; }

    /// <summary>Also while the active account needs a new login, so another one can be picked.</summary>
    private bool ShowAccountButton => _state.CanSwitchAccount && CreateAccountItems is not null;

    /// <summary>Switches to the account with this key - a click on another monitored
    /// account's summary row (#18).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action<string>? SwitchAccount { get; init; }

    /// <summary>The other accounts' summary rows from the last layout, for hit-testing.</summary>
    private List<(RectangleF Bounds, string Key)> _otherAccountRows = [];
    private int? _hoverAccountRow;

    /// <param name="openMiniDashboard">Opens the mini dashboard (header button next to refresh).</param>
    /// <param name="openHeatmap">Opens the yearly heatmap (button next to the chart's title).</param>
    public UsageFlyout(Func<FlyoutState> getState, Func<Task> refresh, Action openMiniDashboard, Action openHeatmap)
    {
        _getState = getState;
        _refresh = refresh;
        _openMiniDashboard = openMiniDashboard;
        _openHeatmap = openHeatmap;
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

        _miniDashboardButton = new Button
        {
            Text = "", // Segoe MDL2 Assets "OpenInNewWindow"
            FlatStyle = FlatStyle.Flat,
            TabStop = true,
            UseVisualStyleBackColor = false,
        };
        _miniDashboardButton.FlatAppearance.BorderSize = 0;
        _miniDashboardButton.Click += (_, _) =>
        {
            HideFlyout();
            _openMiniDashboard();
        };
        Controls.Add(_miniDashboardButton);

        _heatmapButton = new Button
        {
            Text = "", // Segoe MDL2 Assets "Calendar"
            FlatStyle = FlatStyle.Flat,
            TabStop = true,
            UseVisualStyleBackColor = false,
        };
        _heatmapButton.FlatAppearance.BorderSize = 0;
        _heatmapButton.Click += (_, _) =>
        {
            HideFlyout();
            _openHeatmap();
        };
        Controls.Add(_heatmapButton);

        _accountButton = new Button
        {
            Text = "▾", // "▾" in Segoe UI Symbol (see UpdateFonts)
            FlatStyle = FlatStyle.Flat,
            TabStop = true,
            UseVisualStyleBackColor = false,
            Visible = false,
        };
        _accountButton.FlatAppearance.BorderSize = 0;
        _accountButton.Click += (_, _) => ShowAccountMenu();
        Controls.Add(_accountButton);

        _clockTimer.Tick += (_, _) => Invalidate();
        _reactivateCheck.Tick += (_, _) =>
        {
            _reactivateCheck.Stop();
            if (Visible && ActiveForm != this)
                HideFlyout();
        };

        ApplyLayout();
    }

    /// <summary>Opens the account list under the ▾ button. The menu doesn't take the
    /// activation, so the flyout stays open and shows the new account after a switch.</summary>
    private void ShowAccountMenu()
    {
        if (CreateAccountItems is not { } createItems)
            return;
        var menu = new ContextMenuStrip();
        menu.Items.AddRange([.. createItems()]);
        // Disposed only after an item's Click has run, which comes after Closed.
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(_accountButton, new Point(0, _accountButton.Height));
    }

    /// <summary>Click on a usage notification: opens the flyout at <paramref name="cursor"/>,
    /// or leaves it open if it already is.</summary>
    public void Open(Point cursor)
    {
        if (!Visible)
            ShowAt(cursor);
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
        _palette = FlyoutPalette.For(light);
        _usage = UsagePalette.For(_state.Greyscale, light);
    }

    private void HideFlyout()
    {
        Hide();
        _hiddenAt = Environment.TickCount64;
        _clockTimer.Stop();
        _hoverIndex = null;
        _hoverAccountRow = null;
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

        _miniDashboardButton.Size = _refreshButton.Size;
        _miniDashboardButton.Location = new Point(_refreshButton.Left - buttonSize, _refreshButton.Top);
        _miniDashboardButton.BackColor = _palette.Background;
        _miniDashboardButton.ForeColor = _palette.Text;
        _miniDashboardButton.FlatAppearance.MouseOverBackColor = _palette.Hover;
        _miniDashboardButton.FlatAppearance.MouseDownBackColor = _palette.Track;
        _miniDashboardButton.AccessibleName = Strings.FlyoutOpenMiniDashboard;
        _toolTip.SetToolTip(_miniDashboardButton, Strings.FlyoutOpenMiniDashboard);

        // Right after the chart's title text, centred on its line.
        var smallButton = (int)Math.Round(S(24f));
        var titleWidth = _historyTitle.IsEmpty ? 0 : TextRenderer.MeasureText(Strings.FlyoutHistoryTitle, _titleFont).Width;
        _heatmapButton.Visible = !_historyTitle.IsEmpty;
        _heatmapButton.Size = new Size(smallButton, smallButton);
        _heatmapButton.Location = new Point((int)Math.Round(_historyTitle.X + titleWidth + S(2f)),
            (int)Math.Round(_historyTitle.Y + (_historyTitle.Height - smallButton) / 2f));
        _heatmapButton.BackColor = _palette.Background;
        _heatmapButton.ForeColor = _palette.Text;
        _heatmapButton.FlatAppearance.MouseOverBackColor = _palette.Hover;
        _heatmapButton.FlatAppearance.MouseDownBackColor = _palette.Track;
        _heatmapButton.AccessibleName = Strings.FlyoutOpenHeatmap;
        _toolTip.SetToolTip(_heatmapButton, Strings.FlyoutOpenHeatmap);

        // Right after the account name, centred on its line.
        _accountButton.Visible = !_accountTitle.IsEmpty;
        _accountButton.Size = new Size(smallButton, smallButton);
        _accountButton.Location = new Point((int)Math.Round(_accountTitle.Right + S(2f)),
            (int)Math.Round(_accountTitle.Y + (_accountTitle.Height - smallButton) / 2f));
        _accountButton.BackColor = _palette.Background;
        _accountButton.ForeColor = _palette.Text;
        _accountButton.FlatAppearance.MouseOverBackColor = _palette.Hover;
        _accountButton.FlatAppearance.MouseDownBackColor = _palette.Track;
        _accountButton.AccessibleName = Strings.FlyoutSwitchAccount;
        _toolTip.SetToolTip(_accountButton, Strings.FlyoutSwitchAccount);
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

        var (oldTitle, oldBody, oldSmall, oldIcon, oldCaret) = (_titleFont, _bodyFont, _smallFont, _iconFont, _caretFont);
        _titleFont = new Font("Segoe UI Semibold", S(14f), FontStyle.Regular, GraphicsUnit.Pixel);
        _bodyFont = new Font("Segoe UI", S(12f), FontStyle.Regular, GraphicsUnit.Pixel);
        _smallFont = new Font("Segoe UI", S(11f), FontStyle.Regular, GraphicsUnit.Pixel);
        _iconFont = new Font("Segoe MDL2 Assets", S(14f), FontStyle.Regular, GraphicsUnit.Pixel);
        _caretFont = new Font("Segoe UI Symbol", S(14f), FontStyle.Regular, GraphicsUnit.Pixel);
        _refreshButton.Font = _iconFont;
        _miniDashboardButton.Font = _iconFont;
        _heatmapButton.Font = _iconFont;
        _accountButton.Font = _caretFont;
        _fontScale = _scale;

        oldTitle?.Dispose();
        oldBody?.Dispose();
        oldSmall?.Dispose();
        oldIcon?.Dispose();
        oldCaret?.Dispose();
    }

    private void DisposeFonts()
    {
        _titleFont?.Dispose();
        _bodyFont?.Dispose();
        _smallFont?.Dispose();
        _iconFont?.Dispose();
        _caretFont?.Dispose();
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
        var headerTextWidth = width - S(68f);

        // Header: account and last update / status, refresh button on the right.
        // The account's name - also while it needs a new login (the body says "Not logged in").
        var title = _state.AccountLabel ?? Strings.AppTitle;
        _accountTitle = RectangleF.Empty;
        if (ShowAccountButton)
        {
            // Leave room for the ▾ button right after the name (see ApplyLayout).
            var titleWidth = Math.Min(TextRenderer.MeasureText(title, _titleFont).Width + S(2f), headerTextWidth - S(26f));
            _accountTitle = new RectangleF(left, y, titleWidth, S(20f));
            DrawText(g, title, _titleFont, _palette.Text, _accountTitle);
        }
        else
        {
            DrawText(g, title, _titleFont, _palette.Text, new RectangleF(left, y, headerTextWidth, S(20f)));
        }
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

        _historyTitle = RectangleF.Empty;
        // The y below the content (the padding still to be added), and where the other accounts
        // would start - null if there's no content below the header (logged in, but nothing
        // fetched yet: the first refresh is running, or it failed - the header's status line
        // says which).
        float? bottom = null;
        float? othersTop = null;
        if (!_state.LoggedIn)
        {
            DrawText(g, Strings.MenuNotLoggedIn, _bodyFont, _palette.Text, new RectangleF(left, y, width, S(20f)));
            bottom = y + S(20f);
            othersTop = bottom + S(14f);
        }
        else if (_state.Snapshot is { } snapshot)
        {
            // Session, week, additional limits and credits - the same sections the mini
            // dashboard shows (see UsageSections / UsageBarRenderer).
            var bars = Bars;
            foreach (var section in UsageSections.Build(_state, now))
                y = bars.DrawSection(g, section, _bodyFont, _titleFont, _bodyFont, left, y, width) + S(14f);

            // 7-day chart
            _historyTitle = new RectangleF(left, y, width / 2f, S(20f));
            DrawText(g, Strings.FlyoutHistoryTitle, _titleFont, _palette.Text, _historyTitle);
            DrawLegend(g, snapshot, left + width, y + S(10f));
            y += S(20f) + S(8f);
            DrawChart(g, new RectangleF(left + S(ChartLeftInset), y, width - S(ChartLeftInset), S(ChartHeight)), snapshot, now);
            bottom = y + S(ChartHeight) + S(4f) + S(14f);
            othersTop = bottom;
        }

        _otherAccountRows = [];
        if (_state.Others.Count > 0)
            bottom = DrawOtherAccounts(g, left, width, othersTop ?? y, separator: othersTop is not null, now);

        // Without anything below the header, the flyout ends right below its separator.
        return bottom is { } contentBottom ? contentBottom + S(Pad) : y - S(12f) + S(Pad);
    }

    /// <summary>The other monitored accounts (#18), below everything else: per account its
    /// name and status, and its session and week as compact bars - or why there are none
    /// (login required, error, not fetched yet). A click on one switches to it. Returns the
    /// y below the last one.</summary>
    /// <param name="separator">Draw a separator first (there's content above).</param>
    private float DrawOtherAccounts(Graphics? g, float left, float width, float y, bool separator, DateTimeOffset now)
    {
        if (separator)
        {
            if (g is not null)
            {
                using var pen = new Pen(_palette.Border, Math.Max(1f, S(1f)));
                g.DrawLine(pen, 0, y, S(LogicalWidth), y);
            }
            y += S(1f) + S(12f);
        }
        DrawText(g, Strings.FlyoutOtherAccounts, _titleFont, _palette.Text, new RectangleF(left, y, width, S(20f)));
        y += S(20f) + S(8f);

        var bars = Bars;
        var rowHeight = S(20f);
        var labelWidth = Math.Max(TextRenderer.MeasureText(Strings.FlyoutSessionTitle, _smallFont).Width,
            TextRenderer.MeasureText(Strings.FlyoutWeekTitle, _smallFont).Width) + S(2f);
        var valueWidth = TextRenderer.MeasureText("100%", _smallFont).Width + S(2f);

        foreach (var other in _state.Others)
        {
            var sections = UsageSections.Build(other, now)
                .Where(s => s.Key is UsageSections.SessionKey or UsageSections.WeekKey)
                .ToList();
            var height = S(20f) + (sections.Count > 0 ? sections.Count * rowHeight + (sections.Count - 1) * S(2f) : S(18f));
            var block = new RectangleF(left - S(8f), y - S(4f), width + S(16f), height + S(8f));
            var index = _otherAccountRows.Count;
            _otherAccountRows.Add((block, other.AccountKey ?? ""));

            if (g is not null && _hoverAccountRow == index)
            {
                using var path = RoundedRect(block, S(6f));
                using var hover = new SolidBrush(_palette.Hover);
                g.FillPath(hover, path);
            }

            // Name on the left, status (or "refreshing") on the right.
            var status = other.IsRefreshing ? Strings.FlyoutRefreshing : other.StatusText;
            var statusWidth = Math.Min(TextRenderer.MeasureText(status, _smallFont).Width + S(2f), width * 0.55f);
            DrawText(g, other.AccountLabel ?? Strings.AccountUnnamed, _bodyFont, _palette.Text, new RectangleF(left, y, width - statusWidth - S(8f), S(20f)));
            DrawText(g, status, _smallFont, other.HasError ? _palette.Text : _palette.Secondary,
                new RectangleF(left + width - statusWidth, y + S(1f), statusWidth, S(18f)), TextFormatFlags.Right);
            var rowsTop = y + S(20f);

            if (sections.Count > 0)
            {
                for (var i = 0; i < sections.Count; i++)
                    bars.DrawCompactRow(g, sections[i], _smallFont, _smallFont, left, rowsTop + i * (rowHeight + S(2f)), width, labelWidth, valueWidth, rowHeight);
            }
            else
            {
                var reason = other.LoggedIn ? other.StatusText : Strings.MenuNotLoggedIn;
                DrawText(g, reason, _smallFont, _palette.Secondary, new RectangleF(left, rowsTop, width, S(18f)));
            }
            y += height + S(14f);
        }
        return y - S(14f) + S(4f);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left && AccountRowAt(e.Location) is { } row && SwitchAccount is { } switchAccount)
            switchAccount(_otherAccountRows[row].Key);
    }

    private int? AccountRowAt(Point location)
    {
        for (var i = 0; i < _otherAccountRows.Count; i++)
        {
            if (_otherAccountRows[i].Bounds.Contains(location))
                return i;
        }
        return null;
    }

    /// <summary>The shared bar renderer for the current palette and scale.</summary>
    private UsageBarRenderer Bars => new(_palette, _usage, _scale);

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

        // Another account's summary row: highlighted and clickable (#18).
        var accountRow = SwitchAccount is null ? null : AccountRowAt(e.Location);
        if (accountRow != _hoverAccountRow)
        {
            _hoverAccountRow = accountRow;
            Cursor = accountRow is null ? Cursors.Default : Cursors.Hand;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex is not null || _hoverAccountRow is not null)
        {
            _hoverIndex = null;
            _hoverAccountRow = null;
            Cursor = Cursors.Default;
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

    private static void DrawText(Graphics? g, string text, Font font, Color color, RectangleF rect, TextFormatFlags extra = 0) =>
        UsageBarRenderer.DrawText(g, text, font, color, rect, extra);

    private static GraphicsPath RoundedRect(RectangleF rect, float radius) => UsageBarRenderer.RoundedRect(rect, radius);

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
