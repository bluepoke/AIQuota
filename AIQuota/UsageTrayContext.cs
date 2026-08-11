using AIQuota.Auth;
using AIQuota.Localization;

namespace AIQuota;

public sealed class UsageTrayContext : ApplicationContext
{
    private enum IconKind { Unavailable, Warning, Usage, Refreshing }

    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
#if !STORE_EDITION
    private static readonly TimeSpan NewVersionCheckInterval = TimeSpan.FromHours(6);
#endif
    private const int WarnThresholdPercent = 90;

    private readonly OAuthClient _oauth = new();
    private readonly UsageApiClient _usageApi;

    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _timer;
#if !STORE_EDITION
    private readonly System.Windows.Forms.Timer _newVersionCheckTimer;
#endif
    private readonly ToolStripMenuItem _userItem;
    private readonly ToolStripMenuItem _sessionItem;
    private readonly ToolStripMenuItem _weeklyItem;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _loginItem;
    private readonly ToolStripMenuItem _logoutItem;
    private readonly ToolStripMenuItem _startupItem;
#if !STORE_EDITION
    private readonly ToolStripMenuItem _checkForNewVersionItem;
    private readonly ToolStripMenuItem _newVersionAvailableItem;
#endif
    private readonly ToolStripMenuItem _refreshItem;
    private readonly ToolStripMenuItem _exitItem;
    private readonly ToolStripMenuItem _languageMenu;
    private readonly ToolStripMenuItem _languageGermanItem;
    private readonly ToolStripMenuItem _languageEnglishItem;
    private readonly ToolStripMenuItem _versionItem;
    private readonly ToolStripMenuItem _githubItem;

    private bool _sessionWarningShown;
    private bool _weeklyWarningShown;
    private bool _refreshInProgress;
#if !STORE_EDITION
    private bool _newVersionCheckInProgress;
    private bool _updateInProgress;
#endif
    private string? _cachedAccountName;
    private bool _hasUsageSnapshot;
    private int _lastSessionPercent;
    private int _lastWeeklyPercent;
#if !STORE_EDITION
    private NewVersionInfo? _availableUpdate;
#endif
    private IconKind _currentIconKind = IconKind.Unavailable;
    private string _baseTooltipText = "";

#if STORE_EDITION
    // The Microsoft Store handles updates; this edition never has one to report.
    private const bool HasUpdate = false;
#else
    private bool HasUpdate => _availableUpdate is not null;
#endif

    public UsageTrayContext()
    {
        _usageApi = new UsageApiClient(_oauth);

        _userItem = new ToolStripMenuItem { Enabled = false, Visible = false };
        _sessionItem = new ToolStripMenuItem { Enabled = false };
        _weeklyItem = new ToolStripMenuItem { Enabled = false };
        _statusItem = new ToolStripMenuItem { Enabled = false };
        _loginItem = new ToolStripMenuItem();
        _loginItem.Click += OnLoginClicked;
        _logoutItem = new ToolStripMenuItem { Visible = false };
        _logoutItem.Click += OnLogoutClicked;
        _startupItem = new ToolStripMenuItem { CheckOnClick = false, Checked = StartupManager.IsEnabled() };
        _startupItem.Click += OnToggleStartup;
#if !STORE_EDITION
        _checkForNewVersionItem = new ToolStripMenuItem { CheckOnClick = false, Checked = NewVersionPreference.IsEnabled() };
        _checkForNewVersionItem.Click += OnToggleNewVersionCheck;
        _newVersionAvailableItem = new ToolStripMenuItem { Visible = false };
        _newVersionAvailableItem.Click += (_, _) => OnNewVersionClicked();
#endif
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

        var menu = new ContextMenuStrip();
        menu.Items.Add(_userItem);
        menu.Items.Add(_sessionItem);
        menu.Items.Add(_weeklyItem);
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_refreshItem);
        menu.Items.Add(_loginItem);
        menu.Items.Add(_logoutItem);
        menu.Items.Add(_startupItem);
#if !STORE_EDITION
        menu.Items.Add(_checkForNewVersionItem);
#endif
        menu.Items.Add(_languageMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_githubItem);
        menu.Items.Add(_versionItem);
#if !STORE_EDITION
        menu.Items.Add(_newVersionAvailableItem);
#endif
        menu.Items.Add(_exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = TrayIconFactory.CreateUnavailableIcon(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += async (_, _) => await RefreshAsync();
#if !STORE_EDITION
        _notifyIcon.BalloonTipClicked += (_, _) => OnNewVersionClicked();
#endif

        _timer = new System.Windows.Forms.Timer { Interval = (int)PollInterval.TotalMilliseconds };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

#if !STORE_EDITION
        _newVersionCheckTimer = new System.Windows.Forms.Timer { Interval = (int)NewVersionCheckInterval.TotalMilliseconds };
        _newVersionCheckTimer.Tick += async (_, _) => await CheckForNewVersionAsync();
        _newVersionCheckTimer.Start();
#endif

        Strings.LanguageChanged += async () =>
        {
            ApplyStaticMenuTexts();
            await RefreshAsync();
        };

        ApplyStaticMenuTexts();
        UpdateLoginMenuState();
        _ = RefreshAsync();
#if !STORE_EDITION
        _ = CheckForNewVersionAsync();
#endif
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
#if !STORE_EDITION
        _checkForNewVersionItem.Text = Strings.MenuCheckForNewVersion;
        if (_availableUpdate is not null)
            _newVersionAvailableItem.Text = Strings.MenuNewVersionAvailable(_availableUpdate.Version);
#endif
        _refreshItem.Text = Strings.MenuRefresh;
        _exitItem.Text = Strings.MenuExit;
        _languageMenu.Text = Strings.MenuLanguage;
        _languageGermanItem.Checked = Strings.Current == AppLanguage.German;
        _languageEnglishItem.Checked = Strings.Current == AppLanguage.English;
        _versionItem.Text = Strings.VersionLabel(AppInfo.Version);
        _githubItem.Text = Strings.MenuGitHub;
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
        _cachedAccountName = null;
        _userItem.Visible = false;
        UpdateLoginMenuState();
        await RefreshAsync();
    }

    private void OnToggleStartup(object? sender, EventArgs e)
    {
        var enable = !_startupItem.Checked;
        StartupManager.SetEnabled(enable);
        _startupItem.Checked = StartupManager.IsEnabled();
    }

#if !STORE_EDITION
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

        var confirmed = MessageBox.Show(
            Strings.ConfirmUpdatePrompt(update.Version),
            Strings.AppTitle,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question) == DialogResult.Yes;
        if (!confirmed)
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
#endif

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
                SetIcon(TrayIconFactory.CreateRefreshingIcon(_lastSessionPercent, _lastWeeklyPercent, HasUpdate));
            }

            var result = await _usageApi.FetchAsync(CancellationToken.None);
            ApplyResult(result);

            if (result.Status == UsageFetchStatus.Ok && _cachedAccountName is null)
            {
                _cachedAccountName = await _usageApi.FetchAccountNameAsync(CancellationToken.None);
                if (_cachedAccountName is not null)
                {
                    _userItem.Text = Strings.UserLabel(_cachedAccountName);
                    _userItem.Visible = true;
                }
            }
        }
        finally
        {
            _refreshInProgress = false;
        }
    }

    private void ApplyResult(UsageFetchResult result)
    {
        switch (result.Status)
        {
            case UsageFetchStatus.NotLoggedIn:
                _currentIconKind = IconKind.Unavailable;
                SetIcon(TrayIconFactory.CreateUnavailableIcon(HasUpdate));
                SetNotifyIconText(Strings.TooltipNotLoggedIn);
                _userItem.Visible = false;
                _cachedAccountName = null;
                _hasUsageSnapshot = false;
                _sessionItem.Text = Strings.MenuSessionEmpty;
                _weeklyItem.Text = Strings.MenuWeeklyEmpty;
                _statusItem.Text = Strings.StatusPromptLogin;
                UpdateLoginMenuState();
                return;

            case UsageFetchStatus.AuthExpired:
                _oauth.Logout();
                _currentIconKind = IconKind.Unavailable;
                SetIcon(TrayIconFactory.CreateUnavailableIcon(HasUpdate));
                SetNotifyIconText(Strings.TooltipAuthExpired);
                _userItem.Visible = false;
                _cachedAccountName = null;
                _hasUsageSnapshot = false;
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
        _currentIconKind = IconKind.Usage;
        SetIcon(TrayIconFactory.CreateUsageIcon(snapshot.SessionPercent, snapshot.WeeklyPercent, HasUpdate));

        SetNotifyIconText(
            Strings.TooltipSummary(snapshot.SessionPercent, snapshot.SessionResetsAt, snapshot.WeeklyPercent, snapshot.WeeklyResetsAt, snapshot.FetchedAt));

        _sessionItem.Text = Strings.SessionLabel(snapshot.SessionPercent, Strings.FormatReset(snapshot.SessionResetsAt));
        _weeklyItem.Text = Strings.WeeklyLabel(snapshot.WeeklyPercent, Strings.FormatReset(snapshot.WeeklyResetsAt));
        _statusItem.Text = Strings.StatusUpdated(snapshot.FetchedAt);

        MaybeWarn(snapshot);
    }

    private void SetIcon(Icon icon)
    {
        var old = _notifyIcon.Icon;
        _notifyIcon.Icon = icon;
        old?.Dispose();
    }

    /// <summary>Regenerates whichever icon is currently shown with the update badge added
    /// or removed, for when update availability changes independently of the next usage
    /// refresh (e.g. the periodic background version check).</summary>
    private void RedrawIconForCurrentState()
    {
        var icon = _currentIconKind switch
        {
            IconKind.Warning => TrayIconFactory.CreateWarningIcon(HasUpdate),
            IconKind.Usage => TrayIconFactory.CreateUsageIcon(_lastSessionPercent, _lastWeeklyPercent, HasUpdate),
            IconKind.Refreshing => TrayIconFactory.CreateRefreshingIcon(_lastSessionPercent, _lastWeeklyPercent, HasUpdate),
            _ => TrayIconFactory.CreateUnavailableIcon(HasUpdate),
        };
        SetIcon(icon);
    }

    /// <summary>Records the tooltip text for the current state and applies it, appending
    /// the "update available" line when applicable so it survives later re-application via
    /// <see cref="ApplyTooltipText"/> (e.g. when update availability changes).</summary>
    private void SetNotifyIconText(string baseText)
    {
        _baseTooltipText = baseText;
        ApplyTooltipText();
    }

    private void ApplyTooltipText()
    {
#if STORE_EDITION
        _notifyIcon.Text = Truncate(_baseTooltipText, 127);
#else
        var text = _availableUpdate is { } update
            ? $"{_baseTooltipText}\n{Strings.TooltipUpdateAvailable(update.Version)}"
            : _baseTooltipText;
        _notifyIcon.Text = Truncate(text, 127);
#endif
    }

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
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
#if !STORE_EDITION
            _newVersionCheckTimer.Dispose();
#endif
            _notifyIcon.Visible = false;
            _notifyIcon.Icon?.Dispose();
            _notifyIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}
