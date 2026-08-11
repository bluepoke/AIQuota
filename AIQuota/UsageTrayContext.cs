using AIQuota.Auth;
using AIQuota.Localization;

namespace AIQuota;

public sealed class UsageTrayContext : ApplicationContext
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan NewVersionCheckInterval = TimeSpan.FromHours(6);
    private const int WarnThresholdPercent = 90;

    private readonly OAuthClient _oauth = new();
    private readonly UsageApiClient _usageApi;

    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _newVersionCheckTimer;
    private readonly ToolStripMenuItem _userItem;
    private readonly ToolStripMenuItem _sessionItem;
    private readonly ToolStripMenuItem _weeklyItem;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _loginItem;
    private readonly ToolStripMenuItem _logoutItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly ToolStripMenuItem _checkForNewVersionItem;
    private readonly ToolStripMenuItem _newVersionAvailableItem;
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
    private bool _newVersionCheckInProgress;
    private bool _updateInProgress;
    private string? _cachedAccountName;
    private bool _hasUsageSnapshot;
    private int _lastSessionPercent;
    private int _lastWeeklyPercent;
    private NewVersionInfo? _availableUpdate;

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
        menu.Items.Add(_checkForNewVersionItem);
        menu.Items.Add(_languageMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_githubItem);
        menu.Items.Add(_versionItem);
        menu.Items.Add(_newVersionAvailableItem);
        menu.Items.Add(_exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = TrayIconFactory.CreateUnavailableIcon(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += async (_, _) => await RefreshAsync();
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

        ApplyStaticMenuTexts();
        UpdateLoginMenuState();
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
        _notifyIcon.Text = Strings.TooltipNotLoggedIn;
    }

    private async void OnLoginClicked(object? sender, EventArgs e)
    {
        _loginItem.Enabled = false;
        try
        {
            _notifyIcon.Text = Strings.TooltipLoggingIn;
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
        _notifyIcon.Text = Strings.TooltipUpdating;
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
                SetIcon(TrayIconFactory.CreateRefreshingIcon(_lastSessionPercent, _lastWeeklyPercent));

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
                SetIcon(TrayIconFactory.CreateUnavailableIcon());
                _notifyIcon.Text = Strings.TooltipNotLoggedIn;
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
                SetIcon(TrayIconFactory.CreateUnavailableIcon());
                _notifyIcon.Text = Strings.TooltipAuthExpired;
                _userItem.Visible = false;
                _cachedAccountName = null;
                _hasUsageSnapshot = false;
                _statusItem.Text = Strings.StatusPleaseReauth;
                UpdateLoginMenuState();
                return;

            case UsageFetchStatus.NetworkError:
                SetIcon(TrayIconFactory.CreateWarningIcon());
                _statusItem.Text = Strings.FetchError(result.Error ?? "");
                _notifyIcon.Text = Truncate(Strings.FetchError(result.Error ?? ""), 127);
                return;
        }

        var snapshot = result.Snapshot!;
        _hasUsageSnapshot = true;
        _lastSessionPercent = snapshot.SessionPercent;
        _lastWeeklyPercent = snapshot.WeeklyPercent;
        SetIcon(TrayIconFactory.CreateUsageIcon(snapshot.SessionPercent, snapshot.WeeklyPercent));

        _notifyIcon.Text = Truncate(
            Strings.TooltipSummary(snapshot.SessionPercent, snapshot.SessionResetsAt, snapshot.WeeklyPercent, snapshot.WeeklyResetsAt, snapshot.FetchedAt),
            127);

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
            _newVersionCheckTimer.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Icon?.Dispose();
            _notifyIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}
