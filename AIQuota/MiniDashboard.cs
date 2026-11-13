using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using AIQuota.Localization;

namespace AIQuota;

/// <summary>
/// Small, freely positionable window that shows selected usage bars permanently, in addition
/// to the tray flyout (issue #16). It draws the same <see cref="UsageSection"/>s as the flyout
/// with the shared <see cref="UsageBarRenderer"/>, from the same <see cref="FlyoutState"/> -
/// it never calls the API itself.
///
/// <list type="bullet">
/// <item><b>Compact</b> layout: one row per bar (label, bar with pace marker and projection,
/// percentage). <b>Expanded</b>: each bar as in the flyout, with reset time, pace marker,
/// projection and forecast, under a header with the "updated" time and a refresh button.</item>
/// <item>A small close button in the top-right corner (in the compact layout next to the first
/// row's percentage), besides "Close" in the right-click menu.</item>
/// <item>Drag anywhere to move it (unless locked); it snaps to the edges of every monitor's
/// working area within <see cref="SnapDistance"/>, except while Shift is held. Its width can
/// be changed from the left and right edges; the height always follows the content.</item>
/// <item>Always on top, opacity, lock, layout and bar selection come from its right-click
/// menu (also reachable with Shift+F10 or the context-menu key) and are saved in
/// <see cref="MiniDashboardSettings"/>.</item>
/// <item>A borderless tool window: no taskbar button, not in Alt+Tab, and it doesn't take the
/// focus when it appears.</item>
/// </list>
/// </summary>
internal sealed class MiniDashboard : Form
{
    private const float Pad = 12f;
    private const float RowHeight = 22f;
    private const float RowGap = 4f;
    private const float SectionGap = 10f;
    private const float HeaderHeight = 24f;
    /// <summary>Size of the compact layout's close button; the rows leave room for it.</summary>
    private const float CompactCloseSize = 20f;
    private const float ResizeBorder = 6f;
    private const int SnapDistance = 12;

    private readonly Func<FlyoutState> _getState;
    private readonly Func<Task> _refresh;
    private readonly Action _openFlyout;
    private readonly Action _close;
    private readonly MiniDashboardSettings _settings;
    private readonly Action<MiniDashboardSettings> _save;
    private readonly Button _refreshButton;
    private readonly Button _closeButton;
    private readonly ToolTip _toolTip = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly System.Windows.Forms.Timer _clockTimer = new() { Interval = 30_000 };

    private FlyoutState _state;
    private FlyoutPalette _palette = FlyoutPalette.For(SystemTheme.IsLightTaskbar());
    private UsagePalette _usage = UsagePalette.Standard(SystemTheme.IsLightTaskbar());
    private float _scale = 1f;
    private float _fontScale;
    private bool _inLayout;

    /// <summary>Cursor position and window location when the current move began (or since the
    /// last DPI change). Windows offsets each WM_MOVING rectangle from the previously
    /// adjusted one, so snapping against that would accumulate and keep the window stuck to an
    /// edge; instead the unsnapped position is always derived from the cursor.</summary>
    private (Point Cursor, Point Location)? _moveAnchor;

    private Font _labelFont = null!;
    private Font _valueFont = null!;
    private Font _titleFont = null!;
    private Font _smallFont = null!;
    private Font _iconFont = null!;
    private Font _closeFont = null!;

    /// <param name="close">Called for the close button and "Close" in the window's menu; the
    /// owner hides the window and remembers that it's closed.</param>
    /// <param name="save">Persists <paramref name="settings"/> after every change.</param>
    public MiniDashboard(Func<FlyoutState> getState, Func<Task> refresh, Action openFlyout, Action close,
        MiniDashboardSettings settings, Action<MiniDashboardSettings> save)
    {
        _getState = getState;
        _refresh = refresh;
        _openFlyout = openFlyout;
        _close = close;
        _settings = settings;
        _save = save;
        _state = getState();

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        MaximizeBox = false;
        MinimizeBox = false;
        ControlBox = false;
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        Text = Strings.MiniDashboardTitle;
        AccessibleName = Strings.MiniDashboardTitle;
        // Not the TopMost property: setting it activates the window (WinForms calls
        // SetWindowPos without SWP_NOACTIVATE), and the dashboard must not take the focus when
        // it appears. "On top" is a creation style instead (CreateParams) and switched later
        // with ApplyTopMost.
        Opacity = settings.OpacityPercent / 100.0;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        _refreshButton = new IconButton
        {
            Text = "", // Segoe MDL2 Assets "Refresh"
            FlatStyle = FlatStyle.Flat,
            TabStop = true,
            UseVisualStyleBackColor = false,
        };
        _refreshButton.FlatAppearance.BorderSize = 0;
        _refreshButton.Click += async (_, _) => await _refresh();
        Controls.Add(_refreshButton);

        _closeButton = new IconButton
        {
            Text = "", // Segoe MDL2 Assets "ChromeClose"
            FlatStyle = FlatStyle.Flat,
            TabStop = true,
            UseVisualStyleBackColor = false,
        };
        _closeButton.FlatAppearance.BorderSize = 0;
        _closeButton.Click += (_, _) => _close();
        Controls.Add(_closeButton);

        ContextMenuStrip = _menu;
        _menu.Opening += (_, _) => BuildMenu();
        _clockTimer.Tick += (_, _) => UpdateState();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int CS_DROPSHADOW = 0x20000;
            const int WS_EX_TOOLWINDOW = 0x80; // no taskbar button, not in Alt+Tab
            const int WS_EX_TOPMOST = 0x8;
            var cp = base.CreateParams;
            cp.ClassStyle |= CS_DROPSHADOW;
            cp.ExStyle |= WS_EX_TOOLWINDOW;
            // Also read by the base constructor, before _settings is set; the handle itself is
            // only created later, when the settings are there.
            if (_settings?.TopMost == true)
                cp.ExStyle |= WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Windows 11: rounded corners like the flyout (Windows 10 gets a 1px border instead).
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        var round = 2; // DWMWCP_ROUND
        _ = NativeMethods.DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
    }

    // ---------------------------------------------------------------- showing & state

    /// <summary>Shows the window at its saved position (or, the first time, near the bottom
    /// right of the primary screen), moved fully onto a screen if that position isn't on one
    /// any more.</summary>
    public void ShowDashboard()
    {
        _state = _getState();
        ApplyTheme();

        var location = _settings.Location ?? DefaultLocation();
        _scale = NativeMethods.DpiAt(location, DeviceDpi) / 96f;
        ApplyLayout();
        Location = _settings.Location ?? DefaultLocation();
        EnsureOnScreen();

        Show();
        _clockTimer.Start();
    }

    public void HideDashboard()
    {
        _clockTimer.Stop();
        Hide();
    }

    /// <summary>Re-reads the state - after a refresh, a setting or theme change, and every 30
    /// seconds for the countdowns.</summary>
    public void UpdateState()
    {
        _state = _getState();
        if (!Visible)
            return;
        ApplyTheme();
        ApplyLayout();
        EnsureOnScreen();
        Invalidate();
    }

    private void ApplyTheme()
    {
        var light = SystemTheme.IsLightTaskbar();
        _palette = FlyoutPalette.For(light);
        _usage = UsagePalette.For(_state.Greyscale, light);
    }

    private Point DefaultLocation()
    {
        var work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1024, 768);
        var margin = (int)Math.Round(24 * _scale);
        return new Point(work.Right - Width - margin, work.Bottom - Height - margin);
    }

    /// <summary>Moves the window fully into the nearest working area unless it already lies
    /// fully within one - e.g. after a monitor was unplugged, or the content grew taller.</summary>
    private void EnsureOnScreen()
    {
        var bounds = Bounds;
        if (Screen.AllScreens.Any(s => s.WorkingArea.Contains(bounds)))
            return;

        var work = Screen.FromRectangle(bounds).WorkingArea;
        var x = Math.Clamp(bounds.X, work.Left, Math.Max(work.Left, work.Right - bounds.Width));
        var y = Math.Clamp(bounds.Y, work.Top, Math.Max(work.Top, work.Bottom - bounds.Height));
        Location = new Point(x, y);
        if (Visible)
            SaveSettings();
    }

    private void SaveSettings()
    {
        if (Visible)
            _settings.Location = Location;
        _save(_settings);
    }

    // ---------------------------------------------------------------- layout

    private float S(float logical) => logical * _scale;

    /// <summary>Sizes the window: the saved logical width, and the height its content needs.</summary>
    private void ApplyLayout()
    {
        _inLayout = true;
        try
        {
            UpdateFonts();
            var width = (int)Math.Round(S(Math.Clamp(_settings.LogicalWidth, MiniDashboardSettings.MinLogicalWidth, MiniDashboardSettings.MaxLogicalWidth)));
            var height = (int)Math.Ceiling(DrawContent(null, width, DateTimeOffset.Now));
            ClientSize = new Size(width, height);

            // Top-right corner, 4 px into the padding: in the expanded layout the close button
            // and, left of it, the refresh button fill the header; in the compact layout the
            // close button sits next to the first row's percentage (DrawContent leaves room).
            var edge = (int)Math.Round(S(Pad - 4f));
            if (_settings.Expanded)
            {
                var buttonSize = (int)Math.Round(S(HeaderHeight));
                _closeButton.Bounds = new Rectangle(ClientSize.Width - edge - buttonSize, edge, buttonSize, buttonSize);
                _refreshButton.Bounds = new Rectangle(_closeButton.Left - buttonSize, edge, buttonSize, buttonSize);
            }
            else
            {
                var closeSize = (int)Math.Round(S(CompactCloseSize));
                _closeButton.Bounds = new Rectangle(ClientSize.Width - edge - closeSize,
                    (int)Math.Round(S(Pad) + (S(RowHeight) - closeSize) / 2f), closeSize, closeSize);
            }
            _refreshButton.Visible = _settings.Expanded;
            _refreshButton.Enabled = _state is { LoggedIn: true, IsRefreshing: false };
            _refreshButton.AccessibleName = Strings.MenuRefresh;
            _toolTip.SetToolTip(_refreshButton, Strings.MenuRefresh);
            _closeButton.AccessibleName = Strings.MiniDashboardClose;
            _toolTip.SetToolTip(_closeButton, Strings.MiniDashboardClose);
            foreach (var button in new[] { _refreshButton, _closeButton })
            {
                button.BackColor = _palette.Background;
                button.ForeColor = _palette.Text;
                button.FlatAppearance.MouseOverBackColor = _palette.Hover;
                button.FlatAppearance.MouseDownBackColor = _palette.Track;
                // Any border WinForms still draws (BorderSize 0 doesn't cover every state) blends in.
                button.FlatAppearance.BorderColor = _palette.Background;
            }
            BackColor = _palette.Background;
            AccessibleDescription = DescribeForScreenReaders();
        }
        finally
        {
            _inLayout = false;
        }
    }

    private void UpdateFonts()
    {
        if (_fontScale == _scale && _labelFont is not null)
            return;

        var old = new Font?[] { _labelFont, _valueFont, _titleFont, _smallFont, _iconFont, _closeFont };
        _labelFont = new Font("Segoe UI", S(12f), FontStyle.Regular, GraphicsUnit.Pixel);
        _valueFont = new Font("Segoe UI Semibold", S(12f), FontStyle.Regular, GraphicsUnit.Pixel);
        _titleFont = new Font("Segoe UI Semibold", S(14f), FontStyle.Regular, GraphicsUnit.Pixel);
        _smallFont = new Font("Segoe UI", S(11f), FontStyle.Regular, GraphicsUnit.Pixel);
        _iconFont = new Font("Segoe MDL2 Assets", S(12f), FontStyle.Regular, GraphicsUnit.Pixel);
        _closeFont = new Font("Segoe MDL2 Assets", S(9f), FontStyle.Regular, GraphicsUnit.Pixel);
        // The buttons get their new fonts before the old ones are disposed (see UsageFlyout.UpdateFonts).
        _refreshButton.Font = _iconFont;
        _closeButton.Font = _closeFont;
        _fontScale = _scale;
        foreach (var font in old)
            font?.Dispose();
    }

    /// <summary>The selected sections that currently exist, in the flyout's order. If none of
    /// the selected bars is reported at the moment (e.g. only "Credits" is selected and the
    /// plan has none), the session and the week are shown instead of an empty window.</summary>
    private List<UsageSection> SelectedSections(DateTimeOffset now)
    {
        var all = UsageSections.Build(_state, now);
        var selected = all.Where(s => _settings.Bars.Contains(s.Key)).ToList();
        return selected.Count > 0
            ? selected
            : all.Where(s => s.Key is UsageSections.SessionKey or UsageSections.WeekKey).ToList();
    }

    private string StatusText => _state.IsRefreshing ? Strings.FlyoutRefreshing : _state.StatusText;

    // ---------------------------------------------------------------- painting

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(_palette.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (_usage.IsGreyscale)
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        DrawContent(g, ClientSize.Width, DateTimeOffset.Now);

        if (Environment.OSVersion.Version.Build < 22000)
        {
            using var border = new Pen(_palette.Border);
            g.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }
    }

    /// <summary>Draws the content for a window <paramref name="clientWidth"/> wide and returns
    /// the height it needs; with <paramref name="g"/> null it only measures (as in the flyout).</summary>
    private float DrawContent(Graphics? g, float clientWidth, DateTimeOffset now)
    {
        var bars = new UsageBarRenderer(_palette, _usage, _scale);
        var left = S(Pad);
        // The compact layout leaves room on the right for the close button (see ApplyLayout).
        var width = clientWidth - 2 * S(Pad) - (_settings.Expanded ? 0 : S(CompactCloseSize));
        var y = S(Pad);

        if (_settings.Expanded)
        {
            // Left of the refresh and close buttons.
            UsageBarRenderer.DrawText(g, StatusText, _smallFont, _palette.Secondary,
                new RectangleF(left, y, width - S(2 * HeaderHeight), S(HeaderHeight)));
            y += S(HeaderHeight) + S(6f);
        }

        if (!_state.LoggedIn)
        {
            UsageBarRenderer.DrawText(g, Strings.MenuNotLoggedIn, _labelFont, _palette.Text, new RectangleF(left, y, width, S(RowHeight)));
            return y + S(RowHeight) + S(Pad);
        }

        var sections = SelectedSections(now);
        if (sections.Count == 0)
        {
            // Nothing fetched yet (first refresh running, or it failed).
            if (!_settings.Expanded)
            {
                UsageBarRenderer.DrawText(g, StatusText, _smallFont, _palette.Secondary, new RectangleF(left, y, width, S(RowHeight)));
                y += S(RowHeight);
            }
            return Math.Max(y, S(Pad) + S(RowHeight)) + S(Pad);
        }

        if (_settings.Expanded)
        {
            foreach (var section in sections)
                y = bars.DrawSection(g, section, _labelFont, _titleFont, _labelFont, left, y, width) + S(SectionGap);
            return y - S(SectionGap) + S(Pad);
        }

        // Compact: one row per bar; the label column is as wide as the longest label, but at
        // most 45% of the width, so the bars line up.
        // Measured with the same (default) padding DrawText uses, so nothing gets cut off.
        var valueWidth = TextRenderer.MeasureText("100%", _valueFont).Width + S(2f);
        var labelWidth = Math.Min(
            sections.Max(s => TextRenderer.MeasureText(s.Label, _labelFont).Width) + S(2f),
            width * 0.45f);
        foreach (var section in sections)
        {
            bars.DrawCompactRow(g, section, _labelFont, _valueFont, left, y, width, labelWidth, valueWidth, S(RowHeight));
            y += S(RowHeight) + S(RowGap);
        }
        y -= S(RowGap);

        // A failed refresh keeps the last values; say so instead of silently showing old data.
        if (_state.HasError)
        {
            UsageBarRenderer.DrawText(g, StatusText, _smallFont, _palette.Secondary, new RectangleF(left, y + S(2f), width, S(18f)));
            y += S(20f);
        }
        return y + S(Pad);
    }

    /// <summary>The values as text for screen readers, e.g. "Session (5h) 37%, Resets 14:30 ·
    /// in 1.6h; Week 62%, …".</summary>
    private string DescribeForScreenReaders()
    {
        if (!_state.LoggedIn)
            return Strings.MenuNotLoggedIn;
        var sections = SelectedSections(DateTimeOffset.Now);
        if (sections.Count == 0)
            return StatusText;
        return string.Join("; ", sections.Select(s =>
            $"{s.Label} {s.Percent}%" + (s.Details.Count > 0 ? ", " + s.Details[0].Text : "")));
    }

    // ---------------------------------------------------------------- menu

    private void BuildMenu()
    {
        foreach (ToolStripItem item in _menu.Items.Cast<ToolStripItem>().ToList())
            item.Dispose();
        _menu.Items.Clear();

        var bars = new ToolStripMenuItem(Strings.MiniDashboardBars);
        var available = UsageSections.Build(_state, DateTimeOffset.Now);
        foreach (var section in available)
        {
            var key = section.Key;
            var item = new ToolStripMenuItem(section.Label) { Checked = _settings.Bars.Contains(key) };
            item.Click += (_, _) => ToggleBar(key, available);
            bars.DropDownItems.Add(item);
        }
        bars.Enabled = available.Count > 0;
        _menu.Items.Add(bars);

        var layout = new ToolStripMenuItem(Strings.MiniDashboardLayout);
        layout.DropDownItems.Add(new ToolStripMenuItem(Strings.MiniDashboardCompact, null, (_, _) => SetExpanded(false)) { Checked = !_settings.Expanded });
        layout.DropDownItems.Add(new ToolStripMenuItem(Strings.MiniDashboardExpanded, null, (_, _) => SetExpanded(true)) { Checked = _settings.Expanded });
        _menu.Items.Add(layout);
        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add(new ToolStripMenuItem(Strings.MiniDashboardAlwaysOnTop, null, (_, _) => SetTopMost(!_settings.TopMost)) { Checked = _settings.TopMost });
        var opacity = new ToolStripMenuItem(Strings.MiniDashboardOpacity);
        foreach (var step in MiniDashboardSettings.OpacitySteps)
        {
            var percent = step;
            opacity.DropDownItems.Add(new ToolStripMenuItem($"{percent}%", null, (_, _) => SetOpacity(percent)) { Checked = _settings.OpacityPercent == percent });
        }
        _menu.Items.Add(opacity);
        _menu.Items.Add(new ToolStripMenuItem(Strings.MiniDashboardLock, null, (_, _) => SetLocked(!_settings.Locked)) { Checked = _settings.Locked });
        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add(new ToolStripMenuItem(Strings.MenuRefresh, null, async (_, _) => await _refresh()) { Enabled = _state is { LoggedIn: true, IsRefreshing: false } });
        _menu.Items.Add(new ToolStripMenuItem(Strings.MiniDashboardOpenFlyout, null, (_, _) => _openFlyout()));
        _menu.Items.Add(new ToolStripMenuItem(Strings.MiniDashboardClose, null, (_, _) => _close()));
    }

    /// <summary>Turns a bar on or off - but never the last one that's currently shown.</summary>
    private void ToggleBar(string key, IReadOnlyList<UsageSection> available)
    {
        if (_settings.Bars.Contains(key))
        {
            var shownAfter = available.Count(s => s.Key != key && _settings.Bars.Contains(s.Key));
            if (shownAfter == 0)
                return;
            _settings.Bars.Remove(key);
        }
        else
        {
            _settings.Bars.Add(key);
        }
        ApplySettingChange();
    }

    private void SetExpanded(bool expanded)
    {
        _settings.Expanded = expanded;
        ApplySettingChange();
    }

    private void SetTopMost(bool topMost)
    {
        _settings.TopMost = topMost;
        ApplyTopMost();
        SaveSettings();
    }

    /// <summary>Puts the window in or out of the always-on-top band without activating it.</summary>
    private void ApplyTopMost()
    {
        if (!IsHandleCreated)
            return;
        const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
        NativeMethods.SetWindowPos(Handle, _settings.TopMost ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST,
            0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
    }

    private void SetOpacity(int percent)
    {
        _settings.OpacityPercent = percent;
        Opacity = percent / 100.0;
        SaveSettings();
    }

    private void SetLocked(bool locked)
    {
        _settings.Locked = locked;
        SaveSettings();
    }

    private void ApplySettingChange()
    {
        ApplyLayout();
        EnsureOnScreen();
        Invalidate();
        SaveSettings();
    }

    // ---------------------------------------------------------------- moving, resizing, DPI

    /// <summary>Dragging anywhere (except the button) moves the window via the system's own
    /// move loop, which also sends WM_MOVING for snapping.</summary>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || _settings.Locked)
            return;
        NativeMethods.ReleaseCapture();
        NativeMethods.SendMessage(Handle, NativeMethods.WM_NCLBUTTONDOWN, NativeMethods.HTCAPTION, IntPtr.Zero);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // A width change by the user (dragging an edge): remember it in logical pixels.
        if (!_inLayout && _scale > 0 && ClientSize.Width > 0)
            _settings.LogicalWidth = Math.Clamp((int)Math.Round(ClientSize.Width / _scale), MiniDashboardSettings.MinLogicalWidth, MiniDashboardSettings.MaxLogicalWidth);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        // Lay out for the new DPI ourselves (same logical width), at Windows' suggested position.
        e.Cancel = true;
        base.OnDpiChanged(e);
        _scale = e.DeviceDpiNew / 96f;
        Location = e.SuggestedRectangle.Location;
        ApplyLayout();
        Invalidate();
        if (_moveAnchor is not null)
            _moveAnchor = (Cursor.Position, Location);
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case NativeMethods.WM_NCHITTEST:
                base.WndProc(ref m);
                if ((int)m.Result == NativeMethods.HTCLIENT && !_settings.Locked)
                {
                    var point = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
                    var border = (int)Math.Round(S(ResizeBorder));
                    if (point.X < border)
                        m.Result = NativeMethods.HTLEFT;
                    else if (point.X >= ClientSize.Width - border)
                        m.Result = NativeMethods.HTRIGHT;
                }
                return;

            case NativeMethods.WM_SIZING:
                ConstrainSizing(m.WParam.ToInt32(), m.LParam);
                m.Result = 1;
                return;

            case NativeMethods.WM_ENTERSIZEMOVE:
                _moveAnchor = (Cursor.Position, Location);
                break;

            case NativeMethods.WM_MOVING:
                SnapMoving(m.LParam, snap: (ModifierKeys & Keys.Shift) == 0);
                m.Result = 1;
                return;

            case NativeMethods.WM_EXITSIZEMOVE:
                _moveAnchor = null;
                base.WndProc(ref m);
                ApplyLayout();
                Invalidate();
                SaveSettings();
                return;
        }
        base.WndProc(ref m);
    }

    /// <summary>While resizing: keep the height (it follows the content) and keep the width
    /// between the minimum and maximum, on the edge that's being dragged.</summary>
    private void ConstrainSizing(int edge, IntPtr rectPointer)
    {
        var rect = Marshal.PtrToStructure<NativeMethods.RECT>(rectPointer);
        var min = (int)Math.Round(S(MiniDashboardSettings.MinLogicalWidth));
        var max = (int)Math.Round(S(MiniDashboardSettings.MaxLogicalWidth));
        var width = Math.Clamp(rect.Right - rect.Left, min, max);
        if (edge is NativeMethods.WMSZ_LEFT or NativeMethods.WMSZ_TOPLEFT or NativeMethods.WMSZ_BOTTOMLEFT)
            rect.Left = rect.Right - width;
        else
            rect.Right = rect.Left + width;
        rect.Top = Top;
        rect.Bottom = Top + Height;
        Marshal.StructureToPtr(rect, rectPointer, false);
    }

    /// <summary>While moving: places the window where the cursor has dragged it since the move
    /// began and, if <paramref name="snap"/>, snaps each edge to the nearest working-area edge
    /// of any monitor within <see cref="SnapDistance"/> pixels.</summary>
    private void SnapMoving(IntPtr rectPointer, bool snap)
    {
        var rect = Marshal.PtrToStructure<NativeMethods.RECT>(rectPointer);
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var left = rect.Left;
        var top = rect.Top;
        if (_moveAnchor is { } anchor)
        {
            var cursor = Cursor.Position;
            left = anchor.Location.X + cursor.X - anchor.Cursor.X;
            top = anchor.Location.Y + cursor.Y - anchor.Cursor.Y;
        }

        if (snap)
        {
            var distance = (int)Math.Round(SnapDistance * _scale);
            var edgesX = Screen.AllScreens.SelectMany(s => new[] { s.WorkingArea.Left, s.WorkingArea.Right }).ToList();
            var edgesY = Screen.AllScreens.SelectMany(s => new[] { s.WorkingArea.Top, s.WorkingArea.Bottom }).ToList();
            left = Snap(left, left + width, edgesX, distance);
            top = Snap(top, top + height, edgesY, distance);
        }
        rect.Left = left;
        rect.Right = left + width;
        rect.Top = top;
        rect.Bottom = top + height;
        Marshal.StructureToPtr(rect, rectPointer, false);
    }

    /// <summary>The new start coordinate after snapping the start or the end (whichever is
    /// closer to an edge) of a span to one of <paramref name="edges"/>.</summary>
    private static int Snap(int start, int end, List<int> edges, int distance)
    {
        var best = start;
        var bestDelta = distance + 1;
        foreach (var edge in edges)
        {
            var toStart = edge - start;
            if (Math.Abs(toStart) < bestDelta)
            {
                bestDelta = Math.Abs(toStart);
                best = start + toStart;
            }
            var toEnd = edge - end;
            if (Math.Abs(toEnd) < bestDelta)
            {
                bestDelta = Math.Abs(toEnd);
                best = start + toEnd;
            }
        }
        return best;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _clockTimer.Dispose();
            _toolTip.Dispose();
            _menu.Dispose();
            _labelFont?.Dispose();
            _valueFont?.Dispose();
            _titleFont?.Dispose();
            _smallFont?.Dispose();
            _iconFont?.Dispose();
            _closeFont?.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>Flat icon button that never becomes the window's default button: a flat button
    /// that is the default draws a rectangle in its text colour despite BorderSize 0, which
    /// showed up around the close button once the window lost focus.</summary>
    private sealed class IconButton : Button
    {
        public override void NotifyDefault(bool value) => base.NotifyDefault(false);
    }

    private static class NativeMethods
    {
        public const int WM_NCHITTEST = 0x0084;
        public const int WM_NCLBUTTONDOWN = 0x00A1;
        public const int WM_SIZING = 0x0214;
        public const int WM_MOVING = 0x0216;
        public const int WM_ENTERSIZEMOVE = 0x0231;
        public const int WM_EXITSIZEMOVE = 0x0232;
        public const int HTCLIENT = 1;
        public const int HTCAPTION = 2;
        public const int HTLEFT = 10;
        public const int HTRIGHT = 11;
        public const int WMSZ_LEFT = 1;
        public const int WMSZ_TOPLEFT = 4;
        public const int WMSZ_BOTTOMLEFT = 7;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct POINT(int x, int y)
        {
            public readonly int X = x;
            public readonly int Y = y;
        }

        public static readonly IntPtr HWND_TOPMOST = new(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new(-2);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, IntPtr lParam);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

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
