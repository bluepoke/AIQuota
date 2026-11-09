using AIQuota.Auth;
using AIQuota.History;
using AIQuota.Localization;
using AIQuota.StreamDock;

namespace AIQuota;

public sealed class UsageTrayContext : ApplicationContext
{
    private enum IconKind { Unavailable, Warning, Usage, Refreshing }

    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan NewVersionCheckInterval = TimeSpan.FromHours(6);
    private const int WarnThresholdPercent = 90;

    /// <summary>Warn once when the session is forecast to hit its limit within this time...</summary>
    private static readonly TimeSpan ForecastWarnWithin = TimeSpan.FromMinutes(30);

    /// <summary>...and only warn again after the forecast has gone back above this (or away),
    /// so a forecast hovering around 30 minutes doesn't warn on every poll.</summary>
    private static readonly TimeSpan ForecastRearmAbove = TimeSpan.FromMinutes(45);

    /// <summary>Windows caps <see cref="NotifyIcon.Text"/> at 127 characters.</summary>
    private const int MaxTooltipLength = 127;

    private readonly OAuthClient _oauth = new();
    private readonly UsageApiClient _usageApi;

    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _newVersionCheckTimer;
    private readonly ToolStripMenuItem _userItem;
    private readonly ToolStripMenuItem _sessionItem;
    private readonly ToolStripMenuItem _sessionForecastItem;
    private readonly ToolStripMenuItem _weeklyItem;
    private readonly ToolStripMenuItem _weeklyForecastItem;
    private readonly ToolStripMenuItem _creditsItem;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _loginItem;
    private readonly ToolStripMenuItem _logoutItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly ToolStripMenuItem _showSessionRingItem;
    private readonly ToolStripMenuItem _showWeeklyPaceItem;
    private readonly ToolStripMenuItem _streamDockItem;
    private readonly ToolStripMenuItem _checkForNewVersionItem;
    private readonly ToolStripMenuItem _newVersionAvailableItem;
    private readonly ToolStripMenuItem _refreshItem;
    private readonly ToolStripMenuItem _exitItem;
    private readonly ToolStripMenuItem _languageMenu;
    private readonly ToolStripMenuItem _languageGermanItem;
    private readonly ToolStripMenuItem _languageEnglishItem;
    private readonly ToolStripMenuItem _versionItem;
    private readonly ToolStripMenuItem _githubItem;
    private readonly ToolStripMenuItem _settingsMenu;
    private readonly ToolStripMenuItem _aboutMenu;
    private readonly ToolStripMenuItem _historyMenu;
    private readonly ToolStripMenuItem _historyExportItem;
    private readonly ToolStripMenuItem _historyClearItem;

    private readonly IUsageHistory _history = new JsonlUsageHistory();
    private readonly SynchronizationContext _uiContext;
    private StreamDockBridge? _streamDockBridge;
    private UsageFlyout? _flyout;

    private bool _sessionWarningShown;
    private bool _weeklyWarningShown;
    private bool _sessionForecastWarningShown;
    private bool _refreshInProgress;
    private bool _newVersionCheckInProgress;
    private bool _updateInProgress;
    private string? _cachedAccountName;

    /// <summary>Stable ID of the signed-in account (see <see cref="AccountInfo.Id"/>), keying
    /// the usage history; null until the profile has been fetched successfully.</summary>
    private string? _accountId;

    private UsageForecast _sessionForecast = UsageForecast.None;
    private UsageForecast _weeklyForecast = UsageForecast.None;
    private bool _hasUsageSnapshot;
    private int _lastSessionPercent;
    private int _lastWeeklyPercent;
    private int? _lastCreditPercent;
    private DateTimeOffset? _lastSessionResetsAt;
    private DateTimeOffset? _lastWeeklyResetsAt;
    private UsageSnapshot? _lastUsageSnapshot;
    private NewVersionInfo? _availableUpdate;
    private IconKind _currentIconKind = IconKind.Unavailable;
    private string[] _tooltipCandidates = [""];

    private bool HasUpdate => _availableUpdate is not null;

    /// <summary>The API omits the session reset time whenever no 5-hour session is currently
    /// running (no message sent since the last one expired).</summary>
    private bool SessionNotStarted => _hasUsageSnapshot && _lastSessionResetsAt is null;

    public UsageTrayContext()
    {
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();
        _usageApi = new UsageApiClient(_oauth);

        _userItem = new ToolStripMenuItem { Enabled = false, Visible = false };
        _sessionItem = new ToolStripMenuItem { Enabled = false };
        _sessionForecastItem = new ToolStripMenuItem { Enabled = false, Visible = false };
        _weeklyItem = new ToolStripMenuItem { Enabled = false };
        _weeklyForecastItem = new ToolStripMenuItem { Enabled = false, Visible = false };
        _creditsItem = new ToolStripMenuItem { Enabled = false, Visible = false };
        _statusItem = new ToolStripMenuItem { Enabled = false };
        _loginItem = new ToolStripMenuItem();
        _loginItem.Click += OnLoginClicked;
        _logoutItem = new ToolStripMenuItem { Visible = false };
        _logoutItem.Click += OnLogoutClicked;
        _startupItem = new ToolStripMenuItem { CheckOnClick = false, Checked = StartupManager.IsEnabled() };
        _startupItem.Click += OnToggleStartup;
        _showSessionRingItem = new ToolStripMenuItem { CheckOnClick = false, Checked = SessionRingPreference.IsEnabled() };
        _showSessionRingItem.Click += OnToggleSessionRing;
        _showWeeklyPaceItem = new ToolStripMenuItem { CheckOnClick = false, Checked = WeeklyPacePreference.IsEnabled() };
        _showWeeklyPaceItem.Click += OnToggleWeeklyPace;
        _streamDockItem = new ToolStripMenuItem { CheckOnClick = false, Checked = StreamDockPreference.IsEnabled() };
        _streamDockItem.Click += OnToggleStreamDock;
        _checkForNewVersionItem = new ToolStripMenuItem { CheckOnClick = false, Checked = NewVersionPreference.IsEnabled() };
        _checkForNewVersionItem.Click += OnToggleNewVersionCheck;
        _newVersionAvailableItem = new ToolStripMenuItem { Visible = false };
        _newVersionAvailableItem.Click += (_, _) => OnNewVersionClicked();
        _refreshItem = new ToolStripMenuItem();
        _refreshItem.Click += async (_, _) => await RefreshAsync();
        _exitItem = new ToolStripMenuItem();
        _exitItem.Click += (_, _) => ExitThread();

        _languageGermanItem = new ToolStripMenuItem(Strings.MenuLanguageGerman, null, (_, _) => Strings.SetLanguage(AppLanguage.German));
        _languageEnglishItem = new ToolStripMenuItem(Strings.MenuLanguageEnglish, null, (_, _) => Strings.SetLanguage(AppLanguage.English));
        _languageMenu = new ToolStripMenuItem();
        _languageMenu.DropDownItems.Add(_languageGermanItem);
        _languageMenu.DropDownItems.Add(_languageEnglishItem);

        _versionItem = new ToolStripMenuItem { Enabled = false };
        _githubItem = new ToolStripMenuItem();
        _githubItem.Click += (_, _) =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppInfo.RepositoryUrl) { UseShellExecute = true });

        _settingsMenu = new ToolStripMenuItem();
        _settingsMenu.DropDownItems.Add(_startupItem);
        _settingsMenu.DropDownItems.Add(_showSessionRingItem);
        _settingsMenu.DropDownItems.Add(_showWeeklyPaceItem);
        _settingsMenu.DropDownItems.Add(_streamDockItem);
        _settingsMenu.DropDownItems.Add(_checkForNewVersionItem);
        _settingsMenu.DropDownItems.Add(_languageMenu);

        _aboutMenu = new ToolStripMenuItem();
        _aboutMenu.DropDownItems.Add(_githubItem);
        _aboutMenu.DropDownItems.Add(_versionItem);

        _historyExportItem = new ToolStripMenuItem();
        _historyExportItem.Click += OnExportHistoryClicked;
        _historyClearItem = new ToolStripMenuItem();
        _historyClearItem.Click += OnClearHistoryClicked;
        _historyMenu = new ToolStripMenuItem();
        _historyMenu.DropDownItems.Add(_historyExportItem);
        _historyMenu.DropDownItems.Add(_historyClearItem);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_userItem);
        menu.Items.Add(_sessionItem);
        menu.Items.Add(_sessionForecastItem);
        menu.Items.Add(_weeklyItem);
        menu.Items.Add(_weeklyForecastItem);
        menu.Items.Add(_creditsItem);
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_refreshItem);
        menu.Items.Add(_loginItem);
        menu.Items.Add(_logoutItem);
        menu.Items.Add(_historyMenu);
        menu.Items.Add(_settingsMenu);
        menu.Items.Add(_aboutMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_newVersionAvailableItem);
        menu.Items.Add(_exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = TrayIconFactory.CreateUnavailableIcon(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        // Left-click opens the flyout. A double-click no longer refreshes (the flyout's refresh
        // button and "Refresh now" do that): the flyout must open on the first click without
        // waiting to see whether a second one follows, and keeps open on the second.
        _notifyIcon.MouseClick += OnNotifyIconMouseClick;
        _notifyIcon.BalloonTipClicked += (_, _) => OnNewVersionClicked();

        _timer = new System.Windows.Forms.Timer { Interval = (int)PollInterval.TotalMilliseconds };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        _newVersionCheckTimer = new System.Windows.Forms.Timer { Interval = (int)NewVersionCheckInterval.TotalMilliseconds };
        _newVersionCheckTimer.Tick += async (_, _) => await CheckForNewVersionAsync();
        _newVersionCheckTimer.Start();

        Strings.LanguageChanged += async () =>
        {
            ApplyStaticMenuTexts();
            await RefreshAsync();
        };

        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;

        ApplyStaticMenuTexts();
        UpdateLoginMenuState();
        if (_streamDockItem.Checked)
            StartStreamDockBridge();
        _ = RefreshAsync();
        _ = CheckForNewVersionAsync();
    }

    private void ApplyStaticMenuTexts()
    {
        if (_cachedAccountName is not null)
            _userItem.Text = Strings.UserLabel(_cachedAccountName);
        _sessionItem.Text = Strings.MenuSessionEmpty;
        _weeklyItem.Text = Strings.MenuWeeklyEmpty;
        _statusItem.Text = Strings.MenuNotLoggedIn;
        _loginItem.Text = Strings.MenuLogin;
        _logoutItem.Text = Strings.MenuLogout;
        _startupItem.Text = Strings.MenuStartup;
        _showSessionRingItem.Text = Strings.MenuShowSessionRing;
        _showWeeklyPaceItem.Text = Strings.MenuShowWeeklyPace;
        _streamDockItem.Text = Strings.MenuEnableStreamDock;
        _checkForNewVersionItem.Text = Strings.MenuCheckForNewVersion;
        if (_availableUpdate is not null)
            _newVersionAvailableItem.Text = Strings.MenuNewVersionAvailable(_availableUpdate.Version);
        _refreshItem.Text = Strings.MenuRefresh;
        _exitItem.Text = Strings.MenuExit;
        _languageMenu.Text = Strings.MenuLanguage;
        _languageGermanItem.Checked = Strings.Current == AppLanguage.German;
        _languageEnglishItem.Checked = Strings.Current == AppLanguage.English;
        _versionItem.Text = Strings.VersionLabel(AppInfo.Version);
        _githubItem.Text = Strings.MenuGitHub;
        _settingsMenu.Text = Strings.MenuSettings;
        _aboutMenu.Text = Strings.MenuAbout;
        _historyMenu.Text = Strings.MenuHistory;
        _historyExportItem.Text = Strings.MenuHistoryExport;
        _historyClearItem.Text = Strings.MenuHistoryClear;
        ApplyForecastMenuTexts();
        SetNotifyIconText(Strings.TooltipNotLoggedIn);
    }

    private async void OnLoginClicked(object? sender, EventArgs e)
    {
        _loginItem.Enabled = false;
        try
        {
            SetNotifyIconText(Strings.TooltipLoggingIn);
            await _oauth.LoginAsync(CancellationToken.None);
            UpdateLoginMenuState();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(Strings.LoginFailed(ex.Message), Strings.AppTitle,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _loginItem.Enabled = true;
        }
    }

    private async void OnLogoutClicked(object? sender, EventArgs e)
    {
        _oauth.Logout();
        ForgetAccount();
        UpdateLoginMenuState();
        await RefreshAsync();
    }

    /// <summary>Drops everything tied to the signed-in account: its label, and the loaded
    /// usage history and forecast. The history files themselves stay on disk (see
    /// <see cref="OnClearHistoryClicked"/> to delete them).</summary>
    private void ForgetAccount()
    {
        _cachedAccountName = null;
        _accountId = null;
        _userItem.Visible = false;
        _history.SetAccount(null);
        ResetForecasts();
    }

    private void ResetForecasts()
    {
        _sessionForecast = UsageForecast.None;
        _weeklyForecast = UsageForecast.None;
        _sessionForecastWarningShown = false;
        ApplyForecastMenuTexts();
    }

    private void OnExportHistoryClicked(object? sender, EventArgs e)
    {
        var records = _history.Records;
        if (records.Count == 0)
        {
            MessageBox.Show(Strings.HistoryEmpty, Strings.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Title = Strings.HistoryExportDialogTitle,
            Filter = Strings.HistoryCsvFilter,
            DefaultExt = "csv",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"AIQuota-history-{DateTime.Now:yyyy-MM-dd}.csv",
        };
        if (dialog.ShowDialog() != DialogResult.OK)
            return;

        try
        {
            UsageHistoryCsv.Write(dialog.FileName, records);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(Strings.HistoryExportFailed(ex.Message), Strings.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnClearHistoryClicked(object? sender, EventArgs e)
    {
        var answer = MessageBox.Show(Strings.ConfirmClearHistory, Strings.AppTitle,
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
            return;

        _history.Clear();
        ResetForecasts();
        if (_currentIconKind == IconKind.Usage && _lastUsageSnapshot is { } snapshot)
            ApplyUsageTooltip(snapshot);
        UpdateFlyout();
    }

    private void OnToggleStartup(object? sender, EventArgs e)
    {
        var enable = !_startupItem.Checked;
        StartupManager.SetEnabled(enable);
        _startupItem.Checked = StartupManager.IsEnabled();
    }

    /// <summary>Redraws the tray icon right away when the Windows light/dark theme changes,
    /// so its colours (picked per-theme in <see cref="TrayIconFactory"/>) stay legible instead
    /// of waiting for the next usage poll.</summary>
    private void OnSystemPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category == Microsoft.Win32.UserPreferenceCategory.General ||
            e.Category == Microsoft.Win32.UserPreferenceCategory.Color)
        {
            RedrawIconForCurrentState();
            UpdateFlyout();
        }
    }

    private void OnNotifyIconMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;
        _flyout ??= new UsageFlyout(BuildFlyoutState, RefreshAsync);
        _flyout.Toggle(Cursor.Position);
    }

    /// <summary>Re-reads the flyout's state if it has been created - it only repaints while
    /// it's open.</summary>
    private void UpdateFlyout() => _flyout?.UpdateState();

    private FlyoutState BuildFlyoutState() => new(
        LoggedIn: _oauth.IsLoggedIn,
        AccountLabel: _cachedAccountName,
        StatusText: _statusItem.Text ?? "",
        IsRefreshing: _refreshInProgress,
        Snapshot: _lastUsageSnapshot,
        CreditPercent: _lastCreditPercent,
        SessionNotStarted: SessionNotStarted,
        ShowPaceMarker: _showWeeklyPaceItem.Checked,
        SessionForecast: _sessionForecast,
        WeeklyForecast: _weeklyForecast,
        History: _history.Records);

    private void OnToggleSessionRing(object? sender, EventArgs e)
    {
        var enable = !_showSessionRingItem.Checked;
        SessionRingPreference.SetEnabled(enable);
        _showSessionRingItem.Checked = enable;
        RedrawIconForCurrentState();
    }

    /// <summary>Shows or hides the weekly pace marker on the tray icon and deck keys, and the
    /// matching pace value in the tooltip and the week menu entry, right away.</summary>
    private void OnToggleWeeklyPace(object? sender, EventArgs e)
    {
        var enable = !_showWeeklyPaceItem.Checked;
        WeeklyPacePreference.SetEnabled(enable);
        _showWeeklyPaceItem.Checked = enable;
        RedrawIconForCurrentState();

        if (_lastUsageSnapshot is { } snapshot)
        {
            ApplyWeeklyMenuText(snapshot);
            // Leave an error tooltip alone - only the usage tooltip shows the pace.
            if (_currentIconKind == IconKind.Usage)
                ApplyUsageTooltip(snapshot);
        }
        UpdateFlyout();
    }

    private void OnToggleStreamDock(object? sender, EventArgs e)
    {
        var enable = !_streamDockItem.Checked;
        StreamDockPreference.SetEnabled(enable);
        _streamDockItem.Checked = enable;

        if (enable)
            StartStreamDockBridge();
        else
            StopStreamDockBridge();
    }

    private void StartStreamDockBridge()
    {
        if (_streamDockBridge is not null)
            return;
        _streamDockBridge = new StreamDockBridge(RefreshAsync, _uiContext);
        _streamDockBridge.Start();
        DeckStatusHolder.Current = BuildDeckStatusSnapshot();
    }

    private void StopStreamDockBridge()
    {
        _streamDockBridge?.Dispose();
        _streamDockBridge = null;
    }

    private void OnToggleNewVersionCheck(object? sender, EventArgs e)
    {
        var enable = !_checkForNewVersionItem.Checked;
        NewVersionPreference.SetEnabled(enable);
        _checkForNewVersionItem.Checked = enable;

        if (enable)
        {
            _ = CheckForNewVersionAsync();
        }
        else
        {
            _availableUpdate = null;
            _newVersionAvailableItem.Visible = false;
            RedrawIconForCurrentState();
            ApplyTooltipText();
        }
    }

    private async Task CheckForNewVersionAsync()
    {
        if (_newVersionCheckInProgress || !NewVersionPreference.IsEnabled())
            return;
        _newVersionCheckInProgress = true;
        try
        {
            var newVersion = await NewVersionChecker.CheckAsync(CancellationToken.None);
            if (newVersion is null)
                return;

            var isNewlyDetected = _availableUpdate?.Version != newVersion.Version;
            _availableUpdate = newVersion;
            _newVersionAvailableItem.Text = Strings.MenuNewVersionAvailable(newVersion.Version);
            _newVersionAvailableItem.Visible = true;
            RedrawIconForCurrentState();
            ApplyTooltipText();

            if (isNewlyDetected)
                _notifyIcon.ShowBalloonTip(8000, Strings.AppTitle, Strings.BalloonNewVersionAvailable(newVersion.Version), ToolTipIcon.Info);
        }
        finally
        {
            _newVersionCheckInProgress = false;
        }
    }

    private async void OnNewVersionClicked()
    {
        if (_availableUpdate is not { } update || _updateInProgress)
            return;

        if (!UpdateConfirmDialog.Confirm(update))
            return;

        _updateInProgress = true;
        _newVersionAvailableItem.Enabled = false;
        SetNotifyIconText(Strings.TooltipUpdating);
        try
        {
            await SelfUpdater.PrepareAsync(update, CancellationToken.None);
            ExitThread();
        }
        catch (Exception ex)
        {
            MessageBox.Show(Strings.UpdateFailed(ex.Message), Strings.AppTitle,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            _newVersionAvailableItem.Enabled = true;
            _updateInProgress = false;
            await RefreshAsync();
        }
    }

    private void UpdateLoginMenuState()
    {
        var loggedIn = _oauth.IsLoggedIn;
        _loginItem.Visible = !loggedIn;
        _logoutItem.Visible = loggedIn;
    }

    private async Task RefreshAsync()
    {
        if (_refreshInProgress)
            return;
        _refreshInProgress = true;
        try
        {
            if (_hasUsageSnapshot)
            {
                _currentIconKind = IconKind.Refreshing;
                SetIcon(TrayIconFactory.CreateRefreshingIcon(_lastSessionPercent, _lastWeeklyPercent, _lastCreditPercent, HasUpdate, SessionRingFraction, SessionNotStarted, WeeklyPaceFraction));
            }
            UpdateFlyout();

            var result = await _usageApi.FetchAsync(CancellationToken.None);

            // The account is resolved before the result is applied, so the very first fetch
            // already lands in the right account's history and feeds the forecast.
            if (result.Status == UsageFetchStatus.Ok && _accountId is null)
                await ResolveAccountAsync();

            if (result is { Status: UsageFetchStatus.Ok, Snapshot: { } snapshot })
                _history.Append(UsageHistoryRecord.FromSnapshot(snapshot));

            ApplyResult(result);
        }
        finally
        {
            _refreshInProgress = false;
            UpdateFlyout();
        }
    }

    /// <summary>Fetches the signed-in account's label (shown at the top of the menu) and ID
    /// (keys the usage history). On failure both stay unset and the next successful usage
    /// fetch tries again; until then, fetches aren't recorded in the history.</summary>
    private async Task ResolveAccountAsync()
    {
        if (await _usageApi.FetchAccountAsync(CancellationToken.None) is not { } account)
            return;

        if (account.DisplayLabel is { } label)
        {
            _cachedAccountName = label;
            _userItem.Text = Strings.UserLabel(label);
            _userItem.Visible = true;
        }

        _accountId = account.Id;
        _history.SetAccount(account.Id);
    }

    private void ApplyResult(UsageFetchResult result)
    {
        switch (result.Status)
        {
            case UsageFetchStatus.NotLoggedIn:
                _currentIconKind = IconKind.Unavailable;
                SetIcon(TrayIconFactory.CreateUnavailableIcon(HasUpdate));
                SetNotifyIconText(Strings.TooltipNotLoggedIn);
                ForgetAccount();
                _hasUsageSnapshot = false;
                _lastCreditPercent = null;
                _lastSessionResetsAt = null;
                _lastWeeklyResetsAt = null;
                _lastUsageSnapshot = null;
                _sessionItem.Text = Strings.MenuSessionEmpty;
                _weeklyItem.Text = Strings.MenuWeeklyEmpty;
                _creditsItem.Visible = false;
                _statusItem.Text = Strings.StatusPromptLogin;
                UpdateLoginMenuState();
                return;

            case UsageFetchStatus.AuthExpired:
                _oauth.Logout();
                _currentIconKind = IconKind.Unavailable;
                SetIcon(TrayIconFactory.CreateUnavailableIcon(HasUpdate));
                SetNotifyIconText(Strings.TooltipAuthExpired);
                ForgetAccount();
                _hasUsageSnapshot = false;
                _lastCreditPercent = null;
                _lastSessionResetsAt = null;
                _lastWeeklyResetsAt = null;
                _lastUsageSnapshot = null;
                _creditsItem.Visible = false;
                _statusItem.Text = Strings.StatusPleaseReauth;
                UpdateLoginMenuState();
                return;

            case UsageFetchStatus.NetworkError:
                _currentIconKind = IconKind.Warning;
                SetIcon(TrayIconFactory.CreateWarningIcon(HasUpdate));
                _statusItem.Text = Strings.FetchError(result.Error ?? "");
                SetNotifyIconText(Strings.FetchError(result.Error ?? ""));
                return;
        }

        var snapshot = result.Snapshot!;
        _hasUsageSnapshot = true;
        _lastSessionPercent = snapshot.SessionPercent;
        _lastWeeklyPercent = snapshot.WeeklyPercent;
        _lastSessionResetsAt = snapshot.SessionResetsAt;
        _lastWeeklyResetsAt = snapshot.WeeklyResetsAt;
        _lastUsageSnapshot = snapshot;

        int? creditPercent = snapshot is { CreditUsed: { } creditUsedForPercent, CreditLimit: > 0 and { } creditLimitForPercent }
            ? (int)Math.Round(Math.Clamp(creditUsedForPercent / creditLimitForPercent * 100m, 0, 100))
            : null;
        _lastCreditPercent = creditPercent;

        UpdateForecasts(snapshot);

        _currentIconKind = IconKind.Usage;
        SetIcon(TrayIconFactory.CreateUsageIcon(snapshot.SessionPercent, snapshot.WeeklyPercent, creditPercent, HasUpdate, SessionRingFraction, SessionNotStarted, WeeklyPaceFraction));

        ApplyUsageTooltip(snapshot);

        _sessionItem.Text = Strings.SessionLabel(snapshot.SessionPercent,
            SessionNotStarted ? Strings.SessionNotStartedSuffix : Strings.FormatReset(snapshot.SessionResetsAt));
        ApplyWeeklyMenuText(snapshot);

        if (snapshot is { CreditUsed: { } creditUsed, CreditLimit: { } creditLimit, CreditCurrency: { } creditCurrency })
        {
            _creditsItem.Text = Strings.CreditsLabel(creditUsed, creditLimit, creditCurrency);
            _creditsItem.Visible = true;
        }
        else
        {
            _creditsItem.Visible = false;
        }

        _statusItem.Text = Strings.StatusUpdated(snapshot.FetchedAt);

        MaybeWarn(snapshot);
    }

    /// <summary>Recomputes the session and week burn-rate forecasts from the history (which
    /// already includes <paramref name="snapshot"/>) and shows them in the menu. No session
    /// forecast while no session is running.</summary>
    private void UpdateForecasts(UsageSnapshot snapshot)
    {
        var now = DateTimeOffset.Now;
        var records = _history.Records;

        _sessionForecast = SessionNotStarted
            ? UsageForecast.None
            : BurnRateForecaster.Forecast(
                records.Select(r => new UsagePoint(r.Time, r.Session, r.SessionResetsAt)),
                snapshot.SessionResetsAt, BurnRateForecaster.Session, now);
        _weeklyForecast = BurnRateForecaster.Forecast(
            records.Select(r => new UsagePoint(r.Time, r.Week, r.WeekResetsAt)),
            snapshot.WeeklyResetsAt, BurnRateForecaster.Week, now);

        ApplyForecastMenuTexts();
    }

    /// <summary>Shows each forecast as an indented line under its session/week menu entry:
    /// "limit in ~X" or "on track to reset first", hidden when there's no forecast.</summary>
    private void ApplyForecastMenuTexts()
    {
        static void Apply(ToolStripMenuItem item, UsageForecast forecast)
        {
            item.Text = forecast.Kind switch
            {
                ForecastKind.Limit => Strings.ForecastLimitIn(forecast.TimeToLimit),
                ForecastKind.ResetFirst => Strings.ForecastResetFirst,
                _ => "",
            };
            item.Visible = forecast.Kind != ForecastKind.None;
        }

        Apply(_sessionForecastItem, _sessionForecast);
        Apply(_weeklyForecastItem, _weeklyForecast);
    }

    /// <summary>Sets the usage tooltip, with the weekly pace when the marker is on and the
    /// time until a forecast limit. Shorter variants are kept as fallbacks for when everything
    /// together (plus the "update available" line) would exceed <see cref="MaxTooltipLength"/>:
    /// first the pace is dropped, then the forecast - rather than cutting off the end.</summary>
    private void ApplyUsageTooltip(UsageSnapshot snapshot)
    {
        string Summary(int? pacePercent, bool withForecast) => Strings.TooltipSummary(
            snapshot.SessionPercent, snapshot.SessionResetsAt, snapshot.WeeklyPercent, snapshot.WeeklyResetsAt,
            snapshot.FetchedAt, _lastCreditPercent, pacePercent,
            withForecast ? _sessionForecast.LimitIn : null,
            withForecast ? _weeklyForecast.LimitIn : null);

        SetNotifyIconText(
            Summary(WeeklyPacePercent, withForecast: true),
            Summary(null, withForecast: true),
            Summary(null, withForecast: false));
    }

    private void ApplyWeeklyMenuText(UsageSnapshot snapshot) =>
        _weeklyItem.Text = Strings.WeeklyLabel(snapshot.WeeklyPercent, Strings.FormatReset(snapshot.WeeklyResetsAt))
            + Strings.PaceSuffix(WeeklyPacePercent);

    private void SetIcon(Icon icon)
    {
        var old = _notifyIcon.Icon;
        _notifyIcon.Icon = icon;
        old?.Dispose();
        DeckStatusHolder.Current = BuildDeckStatusSnapshot();
    }

    /// <summary>Projects the same state the tray icon was just redrawn from into a
    /// <see cref="DeckStatusSnapshot"/>, for <see cref="StreamDockBridge"/> to serve to the
    /// Stream Dock plugin - called from <see cref="SetIcon"/> so the two stay in sync.</summary>
    private DeckStatusSnapshot BuildDeckStatusSnapshot() => new(
        LoggedIn: _currentIconKind != IconKind.Unavailable,
        IsWarning: _currentIconKind == IconKind.Warning,
        IsRefreshing: _currentIconKind == IconKind.Refreshing,
        SessionPercent: _lastSessionPercent,
        WeeklyPercent: _lastWeeklyPercent,
        CreditPercent: _lastCreditPercent,
        SessionRemainingFraction: ComputeSessionRemainingFraction(),
        SessionNotStarted: SessionNotStarted,
        ShowSessionRing: _showSessionRingItem.Checked,
        WeeklyPaceFraction: WeeklyPaceFraction);

    /// <summary>Regenerates whichever icon is currently shown with the update badge added
    /// or removed, for when update availability changes independently of the next usage
    /// refresh (e.g. the periodic background version check).</summary>
    private void RedrawIconForCurrentState()
    {
        var icon = _currentIconKind switch
        {
            IconKind.Warning => TrayIconFactory.CreateWarningIcon(HasUpdate),
            IconKind.Usage => TrayIconFactory.CreateUsageIcon(_lastSessionPercent, _lastWeeklyPercent, _lastCreditPercent, HasUpdate, SessionRingFraction, SessionNotStarted, WeeklyPaceFraction),
            IconKind.Refreshing => TrayIconFactory.CreateRefreshingIcon(_lastSessionPercent, _lastWeeklyPercent, _lastCreditPercent, HasUpdate, SessionRingFraction, SessionNotStarted, WeeklyPaceFraction),
            _ => TrayIconFactory.CreateUnavailableIcon(HasUpdate),
        };
        SetIcon(icon);
    }

    /// <summary><see cref="ComputeSessionRemainingFraction"/> for the countdown ring drawn
    /// around the tray icon; null when the ring is turned off in the menu.</summary>
    private double? SessionRingFraction => _showSessionRingItem.Checked ? ComputeSessionRemainingFraction() : null;

    /// <summary>Fraction of the 5-hour session window still left before <see cref="_lastSessionResetsAt"/>,
    /// for the session countdown (tray ring, Stream Dock keys); null when there's no session data
    /// yet (not logged in, or no usage fetched yet). The API omits a reset time whenever no
    /// session is currently running (no message sent since the last one expired) - that means
    /// the full 5 hours are available the moment one starts, so the countdown shows completely
    /// full (drawn dimmed, see <see cref="SessionNotStarted"/>) rather than disappearing.</summary>
    private double? ComputeSessionRemainingFraction()
    {
        if (!_hasUsageSnapshot)
            return null;

        if (_lastSessionResetsAt is not { } resetsAt)
            return 1.0;

        var remaining = resetsAt - DateTimeOffset.Now;
        return Math.Clamp(remaining / UsageWindows.Session, 0.0, 1.0);
    }

    /// <summary>Elapsed fraction of the current 7-day window, i.e. where usage would be by now
    /// if the weekly quota were spread evenly - drawn as the weekly pace marker (tray icon,
    /// Stream Dock keys). Null when the marker is turned off in the menu, or there's no weekly
    /// reset time (not logged in, no usage fetched yet, or the API omitted it).</summary>
    private double? WeeklyPaceFraction
    {
        get
        {
            if (!_showWeeklyPaceItem.Checked || !_hasUsageSnapshot || _lastWeeklyResetsAt is not { } resetsAt)
                return null;

            return UsageWindows.WeeklyPaceFraction(resetsAt, DateTimeOffset.Now);
        }
    }

    /// <summary><see cref="WeeklyPaceFraction"/> as a whole percentage, for the tooltip and menu.</summary>
    private int? WeeklyPacePercent => WeeklyPaceFraction is { } fraction ? (int)Math.Round(fraction * 100) : null;

    /// <summary>Records the tooltip text for the current state and applies it, appending
    /// the "update available" line when applicable so it survives later re-application via
    /// <see cref="ApplyTooltipText"/> (e.g. when update availability changes).</summary>
    /// <param name="candidates">The text, most detailed first, optionally followed by
    /// shorter variants: the first one that fits in <see cref="MaxTooltipLength"/> together
    /// with the update line is shown, else the last one, truncated.</param>
    private void SetNotifyIconText(params string[] candidates)
    {
        _tooltipCandidates = candidates;
        ApplyTooltipText();
    }

    private void ApplyTooltipText()
    {
        var text = _tooltipCandidates.Select(WithUpdateLine).FirstOrDefault(t => t.Length <= MaxTooltipLength)
            ?? WithUpdateLine(_tooltipCandidates[^1]);
        _notifyIcon.Text = Truncate(text, MaxTooltipLength);
    }

    private string WithUpdateLine(string text) =>
        _availableUpdate is { } update
            ? $"{text}\n{Strings.TooltipUpdateAvailable(update.Version)}"
            : text;

    private void MaybeWarn(UsageSnapshot snapshot)
    {
        if (snapshot.SessionPercent >= WarnThresholdPercent && !_sessionWarningShown)
        {
            _notifyIcon.ShowBalloonTip(8000, Strings.AppTitle, Strings.BalloonSessionWarning(snapshot.SessionPercent), ToolTipIcon.Warning);
            _sessionWarningShown = true;
        }
        else if (snapshot.SessionPercent < WarnThresholdPercent)
        {
            _sessionWarningShown = false;
        }

        if (snapshot.WeeklyPercent >= WarnThresholdPercent && !_weeklyWarningShown)
        {
            _notifyIcon.ShowBalloonTip(8000, Strings.AppTitle, Strings.BalloonWeeklyWarning(snapshot.WeeklyPercent), ToolTipIcon.Warning);
            _weeklyWarningShown = true;
        }
        else if (snapshot.WeeklyPercent < WarnThresholdPercent)
        {
            _weeklyWarningShown = false;
        }

        if (_sessionForecast.LimitIn is { } limitIn && limitIn <= ForecastWarnWithin)
        {
            if (!_sessionForecastWarningShown)
            {
                _notifyIcon.ShowBalloonTip(8000, Strings.AppTitle, Strings.BalloonSessionForecast(limitIn), ToolTipIcon.Warning);
                _sessionForecastWarningShown = true;
            }
        }
        else if (_sessionForecast.LimitIn is not { } later || later > ForecastRearmAbove)
        {
            // No forecast any more (idle, reset first, new session) or comfortably far away.
            _sessionForecastWarningShown = false;
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
            _timer.Dispose();
            _newVersionCheckTimer.Dispose();
            _streamDockBridge?.Dispose();
            _flyout?.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Icon?.Dispose();
            _notifyIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}
