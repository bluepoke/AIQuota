using AIQuota.Auth;
using AIQuota.History;
using AIQuota.Localization;
using AIQuota.Notifications;
using AIQuota.StreamDock;

namespace AIQuota;

public sealed class UsageTrayContext : ApplicationContext
{
    private enum IconKind { Unavailable, Warning, Usage, Refreshing }

    /// <summary>Where "Open usage page" on a usage notification leads.</summary>
    private const string UsagePageUrl = "https://claude.ai/settings/usage";

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
    /// <summary>"Logged in as …" with the account list, add, rename and remove (#13).</summary>
    private readonly ToolStripMenuItem _accountMenu;
    private readonly ToolStripMenuItem _sessionItem;
    private readonly ToolStripMenuItem _sessionForecastItem;
    private readonly ToolStripMenuItem _weeklyItem;
    private readonly ToolStripMenuItem _weeklyForecastItem;
    private readonly ToolStripMenuItem _creditsItem;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _loginItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly ToolStripMenuItem _showSessionRingItem;
    private readonly ToolStripMenuItem _showWeeklyPaceItem;
    private readonly ToolStripMenuItem _accessibilityModeItem;
    private readonly ToolStripMenuItem _streamDockItem;
    private readonly ToolStripMenuItem _checkForNewVersionItem;
    private readonly ToolStripMenuItem _removeNotificationRegistrationItem;
    private readonly ToolStripMenuItem _newVersionAvailableItem;
    private readonly ToolStripMenuItem _refreshItem;
    private readonly ToolStripMenuItem _miniDashboardItem;
    private readonly ToolStripMenuItem _exitItem;
    private readonly ToolStripMenuItem _languageMenu;
    private readonly ToolStripMenuItem _languageGermanItem;
    private readonly ToolStripMenuItem _languageEnglishItem;
    private readonly ToolStripMenuItem _versionItem;
    private readonly ToolStripMenuItem _githubItem;
    private readonly ToolStripMenuItem _settingsMenu;
    private readonly ToolStripMenuItem _aboutMenu;
    private readonly ToolStripMenuItem _historyMenu;
    private readonly ToolStripMenuItem _historyHeatmapItem;
    private readonly ToolStripMenuItem _historyExportItem;
    private readonly ToolStripMenuItem _historyExportDailyItem;
    private readonly ToolStripMenuItem _historyClearItem;

    private readonly IUsageHistory _history = new JsonlUsageHistory();
    private readonly SynchronizationContext _uiContext;
    private StreamDockBridge? _streamDockBridge;
    private UsageFlyout? _flyout;
    private MiniDashboard? _miniDashboard;
    private UsageHeatmap? _heatmap;
    private readonly MiniDashboardSettings _miniDashboardSettings = MiniDashboardSettings.Load();

    /// <summary>Which usage notifications have been shown for one account, so a threshold
    /// already notified isn't notified again - and doesn't carry over to another account.</summary>
    private sealed class AlertState
    {
        public bool SessionWarningShown;
        public bool WeeklyWarningShown;
        public bool SessionForecastWarningShown;

        /// <summary>Keys of the additional limits (<see cref="UsageLimitInfo"/>) whose warning
        /// has been shown, until they drop below the threshold again.</summary>
        public readonly HashSet<string> LimitWarningsShown = [];
    }

    /// <summary>The saved accounts (#13), oldest first, and the one that's shown and polled.
    /// Only the active account is ever fetched.</summary>
    private readonly List<SavedAccount> _accounts;
    private SavedAccount? _active;

    /// <summary>Incremented on every account switch: a fetch that started for the previous
    /// account is discarded when it completes (see <see cref="RefreshAsync"/>).</summary>
    private int _generation;

    private readonly Dictionary<string, AlertState> _alertStates = [];
    /// <summary>The active account's notification state.</summary>
    private AlertState _alerts = new();

    /// <summary>The last fetched usage per account (this run), shown at once on switching
    /// back to it until the refresh completes.</summary>
    private readonly Dictionary<string, UsageSnapshot> _lastSnapshots = [];

    /// <summary>Accounts whose profile (label, ID) has been fetched this run.</summary>
    private readonly HashSet<string> _resolvedAccounts = [];

    /// <summary>Context menu lines of the additional limits, rebuilt on every refresh.</summary>
    private readonly List<ToolStripMenuItem> _limitItems = [];
    private bool _refreshInProgress;
    private bool _loginInProgress;
    private bool _newVersionCheckInProgress;
    private bool _updateInProgress;

    /// <summary>Stable ID of the active account (see <see cref="AccountInfo.Id"/>), keying
    /// the usage history; null until its profile has been fetched successfully.</summary>
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
    private readonly INotifier _notifier;
    private string[] _tooltipCandidates = [""];

    /// <summary>The saved accessibility-mode choice; the mode is also on while Windows High
    /// Contrast is (see <see cref="GreyscaleActive"/>).</summary>
    private bool _accessibilityModeEnabled = AccessibilityModePreference.IsEnabled();

    private bool HasUpdate => _availableUpdate is not null;

    /// <summary>The active account has a token, i.e. usage can be fetched.</summary>
    private bool IsLoggedIn => _active is { HasToken: true };

    /// <summary>With more than one account, the account's name is shown in the tooltip,
    /// notifications and the mini dashboard, and the flyout offers switching.</summary>
    private bool MultipleAccounts => _accounts.Count > 1;

    /// <summary>The active account's short name or label.</summary>
    private string ActiveName => _active?.DisplayName ?? Strings.AccountUnnamed;

    /// <summary>Whether the tray icon, flyout and deck keys use the greyscale palette.</summary>
    private bool GreyscaleActive => AccessibilityModePreference.IsActive(_accessibilityModeEnabled);

    /// <summary>The palette the tray icon is drawn with: standard or greyscale, for the
    /// current taskbar theme.</summary>
    private UsagePalette TrayPalette => UsagePalette.For(GreyscaleActive, SystemTheme.IsLightTaskbar());

    /// <summary>The API omits the session reset time whenever no 5-hour session is currently
    /// running (no message sent since the last one expired).</summary>
    private bool SessionNotStarted => _hasUsageSnapshot && _lastSessionResetsAt is null;

    public UsageTrayContext()
    {
        // WinForms only installs its context when the first control is created, which hasn't
        // happened yet here - a plain SynchronizationContext would run posted work (Stream
        // Dock refreshes, toast clicks) on the thread pool instead of the UI thread.
        if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        _uiContext = SynchronizationContext.Current!;
        _usageApi = new UsageApiClient(_oauth);

        AccountStore.MigrateLegacyToken();
        _accounts = AccountStore.LoadAll();
        var activeKey = AccountStore.LoadActiveKey();
        _active = _accounts.FirstOrDefault(a => a.Key == activeKey)
            ?? _accounts.FirstOrDefault(a => a.HasToken)
            ?? _accounts.FirstOrDefault();
        if (_active?.Key != activeKey)
            AccountStore.SaveActiveKey(_active?.Key);

        _accountMenu = new ToolStripMenuItem { Visible = false };
        // Rebuilt whenever it opens rather than on every change: a change can come from one of
        // its own items' Click, and that item must not be disposed while its handler runs.
        _accountMenu.DropDownOpening += (_, _) => RebuildAccountMenuItems();
        _sessionItem = new ToolStripMenuItem { Enabled = false };
        _sessionForecastItem = new ToolStripMenuItem { Enabled = false, Visible = false };
        _weeklyItem = new ToolStripMenuItem { Enabled = false };
        _weeklyForecastItem = new ToolStripMenuItem { Enabled = false, Visible = false };
        _creditsItem = new ToolStripMenuItem { Enabled = false, Visible = false };
        _statusItem = new ToolStripMenuItem { Enabled = false };
        _loginItem = new ToolStripMenuItem();
        // The first login, or a new one for the active account if its sign-in has expired.
        _loginItem.Click += async (_, _) => await LoginAsync(_active);
        _startupItem = new ToolStripMenuItem { CheckOnClick = false, Checked = StartupManager.IsEnabled() };
        _startupItem.Click += OnToggleStartup;
        _showSessionRingItem = new ToolStripMenuItem { CheckOnClick = false, Checked = SessionRingPreference.IsEnabled() };
        _showSessionRingItem.Click += OnToggleSessionRing;
        _showWeeklyPaceItem = new ToolStripMenuItem { CheckOnClick = false, Checked = WeeklyPacePreference.IsEnabled() };
        _showWeeklyPaceItem.Click += OnToggleWeeklyPace;
        _accessibilityModeItem = new ToolStripMenuItem { CheckOnClick = false };
        _accessibilityModeItem.Click += OnToggleAccessibilityMode;
        _streamDockItem = new ToolStripMenuItem { CheckOnClick = false, Checked = StreamDockPreference.IsEnabled() };
        _streamDockItem.Click += OnToggleStreamDock;
        _checkForNewVersionItem = new ToolStripMenuItem { CheckOnClick = false, Checked = NewVersionPreference.IsEnabled() };
        _checkForNewVersionItem.Click += OnToggleNewVersionCheck;
        // Only the zip build registers itself for notifications; the MSIX package's
        // registration goes away with the package.
        _removeNotificationRegistrationItem = new ToolStripMenuItem { Visible = !ToastNotifier.IsPackaged };
        _removeNotificationRegistrationItem.Click += OnRemoveNotificationRegistrationClicked;
        _newVersionAvailableItem = new ToolStripMenuItem { Visible = false };
        _newVersionAvailableItem.Click += (_, _) => OnNewVersionClicked();
        _refreshItem = new ToolStripMenuItem();
        _miniDashboardItem = new ToolStripMenuItem { CheckOnClick = false };
        _miniDashboardItem.Click += (_, _) =>
        {
            if (_miniDashboardItem.Checked)
                CloseMiniDashboard();
            else
                ShowMiniDashboard();
        };
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
        _settingsMenu.DropDownItems.Add(_accessibilityModeItem);
        _settingsMenu.DropDownItems.Add(_streamDockItem);
        _settingsMenu.DropDownItems.Add(_checkForNewVersionItem);
        _settingsMenu.DropDownItems.Add(_languageMenu);
        _settingsMenu.DropDownItems.Add(_removeNotificationRegistrationItem);

        _aboutMenu = new ToolStripMenuItem();
        _aboutMenu.DropDownItems.Add(_githubItem);
        _aboutMenu.DropDownItems.Add(_versionItem);

        _historyHeatmapItem = new ToolStripMenuItem();
        _historyHeatmapItem.Click += (_, _) => ShowHeatmap();
        _historyExportItem = new ToolStripMenuItem();
        _historyExportItem.Click += OnExportHistoryClicked;
        _historyExportDailyItem = new ToolStripMenuItem();
        _historyExportDailyItem.Click += OnExportDailyClicked;
        _historyClearItem = new ToolStripMenuItem();
        _historyClearItem.Click += OnClearHistoryClicked;
        _historyMenu = new ToolStripMenuItem();
        _historyMenu.DropDownItems.Add(_historyHeatmapItem);
        _historyMenu.DropDownItems.Add(new ToolStripSeparator());
        _historyMenu.DropDownItems.Add(_historyExportItem);
        _historyMenu.DropDownItems.Add(_historyExportDailyItem);
        _historyMenu.DropDownItems.Add(_historyClearItem);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_accountMenu);
        menu.Items.Add(_sessionItem);
        menu.Items.Add(_sessionForecastItem);
        menu.Items.Add(_weeklyItem);
        menu.Items.Add(_weeklyForecastItem);
        menu.Items.Add(_creditsItem);
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_refreshItem);
        menu.Items.Add(_miniDashboardItem);
        menu.Items.Add(_loginItem);
        menu.Items.Add(_historyMenu);
        menu.Items.Add(_settingsMenu);
        menu.Items.Add(_aboutMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_newVersionAvailableItem);
        menu.Items.Add(_exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = TrayIconFactory.CreateUnavailableIcon(TrayPalette),
            ContextMenuStrip = menu,
            Visible = true,
        };
        // Left-click opens the flyout. A double-click no longer refreshes (the flyout's refresh
        // button and "Refresh now" do that): the flyout must open on the first click without
        // waiting to see whether a second one follows, and keeps open on the second.
        _notifyIcon.MouseClick += OnNotifyIconMouseClick;
        _notifier = ToastNotifier.Create(_notifyIcon, OnNotificationAction, _uiContext);

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
        LoadActiveAccount();
        if (_streamDockItem.Checked)
            StartStreamDockBridge();
        if (_miniDashboardSettings.IsOpen)
            ShowMiniDashboard();
        _ = RefreshAsync();
        _ = CheckForNewVersionAsync();
    }

    private void ApplyStaticMenuTexts()
    {
        UpdateAccountMenu();
        _sessionItem.Text = Strings.MenuSessionEmpty;
        _weeklyItem.Text = Strings.MenuWeeklyEmpty;
        _statusItem.Text = Strings.MenuNotLoggedIn;
        UpdateLoginMenuState();
        _startupItem.Text = Strings.MenuStartup;
        _showSessionRingItem.Text = Strings.MenuShowSessionRing;
        _showWeeklyPaceItem.Text = Strings.MenuShowWeeklyPace;
        ApplyAccessibilityMenuState();
        _streamDockItem.Text = Strings.MenuEnableStreamDock;
        _checkForNewVersionItem.Text = Strings.MenuCheckForNewVersion;
        _removeNotificationRegistrationItem.Text = Strings.MenuRemoveNotificationRegistration;
        if (_availableUpdate is not null)
            _newVersionAvailableItem.Text = Strings.MenuNewVersionAvailable(_availableUpdate.Version);
        _refreshItem.Text = Strings.MenuRefresh;
        _miniDashboardItem.Text = Strings.MenuMiniDashboard;
        _exitItem.Text = Strings.MenuExit;
        _languageMenu.Text = Strings.MenuLanguage;
        _languageGermanItem.Checked = Strings.Current == AppLanguage.German;
        _languageEnglishItem.Checked = Strings.Current == AppLanguage.English;
        _versionItem.Text = Strings.VersionLabel(AppInfo.Version);
        _githubItem.Text = Strings.MenuGitHub;
        _settingsMenu.Text = Strings.MenuSettings;
        _aboutMenu.Text = Strings.MenuAbout;
        _historyMenu.Text = Strings.MenuHistory;
        _historyHeatmapItem.Text = Strings.MenuHistoryHeatmap;
        _historyExportItem.Text = Strings.MenuHistoryExport;
        _historyExportDailyItem.Text = Strings.MenuHistoryExportDaily;
        _historyClearItem.Text = Strings.MenuHistoryClear;
        ApplyForecastMenuTexts();
        SetNotifyIconText(Strings.TooltipNotLoggedIn);
    }

    // ---------------------------------------------------------------- accounts (#13)

    private enum LoginLaunch { Browser, CopyLink, Cancel }

    /// <summary>
    /// Signs in through the browser and stores the token with the matching saved account:
    /// the one with the same account ID if it's already in the list (then just its token is
    /// renewed), else <paramref name="target"/> (a new login for an account whose sign-in
    /// expired), else a new account. That account then becomes the active one.
    /// </summary>
    /// <param name="target">The account to log in again; null for the first login or
    /// "Add account…".</param>
    private async Task LoginAsync(SavedAccount? target)
    {
        if (_loginInProgress)
            return;

        // With another account saved, the browser may still be signed in to claude.ai with
        // that one - and its session decides which account signs in - so say how to pick.
        var otherAccounts = _accounts.Count(a => a != target);
        var launch = otherAccounts == 0 ? LoginLaunch.Browser
            : AskHowToOpenLogin(target is null ? Strings.AddAccountHeading : Strings.LoginAgainHeading(target.DisplayName ?? Strings.AccountUnnamed));
        if (launch == LoginLaunch.Cancel)
            return;

        _loginInProgress = true;
        UpdateLoginMenuState();
        var previousTooltip = _tooltipCandidates;
        SetNotifyIconText(launch == LoginLaunch.CopyLink ? Strings.TooltipWaitingForLoginLink : Strings.TooltipLoggingIn);
        var succeeded = false;
        try
        {
            Action<string>? open = launch == LoginLaunch.CopyLink ? url => Clipboard.SetText(url) : null;
            var tokens = await _oauth.LoginAsync(open, CancellationToken.None);
            var info = await _usageApi.FetchAccountWithTokenAsync(tokens.AccessToken, CancellationToken.None);

            var account = PickAccountForLogin(target, info, out var message);
            TokenStore.Save(account.Key, tokens);
            if (info is not null)
            {
                _resolvedAccounts.Add(account.Key);
                account = ApplyAccountInfo(account, info);
            }

            if (account != _active)
                ActivateAccount(account);
            else
                LoadActiveAccount();
            UpdateFlyout();
            succeeded = true;

            if (message is not null)
                MessageBox.Show(message, Strings.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            SetNotifyIconText(previousTooltip);
            MessageBox.Show(Strings.LoginFailed(ex.Message), Strings.AppTitle,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _loginInProgress = false;
            UpdateLoginMenuState();
        }

        if (succeeded)
            await RefreshAsync();
    }

    /// <summary>Asks how to open the sign-in page while another account is saved: in the
    /// browser (signs in whichever account is signed in on claude.ai there), or copy the link
    /// for a private window.</summary>
    private static LoginLaunch AskHowToOpenLogin(string heading)
    {
        var openButton = new TaskDialogButton(Strings.AddAccountOpenBrowser);
        var copyButton = new TaskDialogButton(Strings.AddAccountCopyLink);
        var page = new TaskDialogPage
        {
            Caption = Strings.AppTitle,
            Heading = heading,
            Text = Strings.AddAccountText,
            Icon = TaskDialogIcon.Information,
            Buttons = { openButton, copyButton, TaskDialogButton.Cancel },
            DefaultButton = openButton,
        };
        var result = TaskDialog.ShowDialog(page, TaskDialogStartupLocation.CenterScreen);
        return result == openButton ? LoginLaunch.Browser
            : result == copyButton ? LoginLaunch.CopyLink
            : LoginLaunch.Cancel;
    }

    /// <summary>Which saved account a fresh login belongs to (see <see cref="LoginAsync"/>),
    /// creating a new one if needed; <paramref name="message"/> tells the user when it isn't
    /// the one they presumably meant.</summary>
    private SavedAccount PickAccountForLogin(SavedAccount? target, AccountInfo? info, out string? message)
    {
        message = null;
        var signedInName = info?.DisplayLabel ?? Strings.AccountUnnamed;
        if (info?.Id is { } id && _accounts.FirstOrDefault(a => a.AccountId == id) is { } existing)
        {
            if (target is null)
                message = Strings.LoginAccountAlreadyAdded(existing.DisplayName ?? signedInName);
            else if (existing != target)
                message = Strings.LoginDifferentExistingAccount(existing.DisplayName ?? signedInName, target.DisplayName ?? Strings.AccountUnnamed);
            return existing;
        }

        // A new login for an expired account - unless it's provably a different account.
        if (target is not null && (target.AccountId is null || info?.Id is null))
            return target;

        if (target is not null)
            message = Strings.LoginDifferentAccount(signedInName, target.DisplayName ?? Strings.AccountUnnamed);
        var created = AccountStore.Create();
        _accounts.Add(created);
        return created;
    }

    /// <summary>Stores a fetched profile with the account. If another saved account turns out
    /// to be the same Claude account (e.g. the migrated token, added again), the two are
    /// merged: the other one keeps this one's newer token. Returns the account that remains.</summary>
    private SavedAccount ApplyAccountInfo(SavedAccount account, AccountInfo info)
    {
        if (info.Id is { } id && _accounts.FirstOrDefault(a => a != account && a.AccountId == id) is { } twin)
        {
            if (TokenStore.Load(account.Key) is { } tokens)
                TokenStore.Save(twin.Key, tokens);
            twin.ShortName ??= account.ShortName;
            var wasActive = account == _active;
            RemoveAccountEntry(account);
            _resolvedAccounts.Add(twin.Key);
            if (wasActive)
            {
                // Still the same Claude account, so no generation change: a running fetch
                // for it may complete.
                _active = twin;
                AccountStore.SaveActiveKey(twin.Key);
                _alerts = AlertStateFor(twin);
            }
            account = twin;
        }

        account.AccountId = info.Id ?? account.AccountId;
        account.Label = info.DisplayLabel ?? account.Label;
        AccountStore.Save(account);

        if (account == _active)
        {
            _accountId = account.AccountId;
            _history.SetAccount(_accountId);
        }
        UpdateAccountMenu();
        return account;
    }

    /// <summary>Makes <paramref name="account"/> the active one; a fetch still running for
    /// the previous one is discarded.</summary>
    private void ActivateAccount(SavedAccount? account)
    {
        _active = account;
        AccountStore.SaveActiveKey(account?.Key);
        _generation++;
        LoadActiveAccount();
    }

    /// <summary>Shows the active account: its history, forecasts and notification state, and
    /// its last known usage until the next refresh - nothing of the previous account stays
    /// visible.</summary>
    private void LoadActiveAccount()
    {
        _alerts = _active is null ? new AlertState() : AlertStateFor(_active);
        _accountId = _active?.AccountId;
        _history.SetAccount(_accountId);
        ShowLastKnownUsage();
        UpdateAccountMenu();
        UpdateLoginMenuState();
    }

    private AlertState AlertStateFor(SavedAccount account)
    {
        if (!_alertStates.TryGetValue(account.Key, out var state))
            _alertStates[account.Key] = state = new AlertState();
        return state;
    }

    /// <summary>The usage fetched last for the active account this run, else its newest
    /// history record (a window whose reset has passed counts as empty again), marked by
    /// its time in the status line; the refresh that follows replaces it.</summary>
    private void ShowLastKnownUsage()
    {
        if (!IsLoggedIn)
        {
            ApplyResult(new UsageFetchResult(UsageFetchStatus.NotLoggedIn, null));
            return;
        }

        if ((_lastSnapshots.GetValueOrDefault(_active!.Key) ?? SnapshotFromHistory()) is { } snapshot)
        {
            ApplySnapshot(snapshot, live: false);
            return;
        }

        ClearUsageDisplay();
        _currentIconKind = IconKind.Unavailable;
        SetIcon(TrayIconFactory.CreateUnavailableIcon(TrayPalette, HasUpdate));
        _statusItem.Text = Strings.FlyoutRefreshing;
        SetNotifyIconText(Strings.TooltipLoadingAccount(ActiveName));
    }

    private UsageSnapshot? SnapshotFromHistory()
    {
        if (_history.Records is not [.., var last])
            return null;

        var now = DateTimeOffset.Now;
        var sessionOver = last.SessionResetsAt is { } sessionReset && sessionReset <= now;
        var weekOver = last.WeekResetsAt is { } weekReset && weekReset <= now;
        var session = sessionOver ? 0 : Math.Clamp(last.Session, 0, 100);
        var week = weekOver ? 0 : Math.Clamp(last.Week, 0, 100);
        return new UsageSnapshot(
            (int)Math.Round(session), (int)Math.Round(week),
            sessionOver ? null : last.SessionResetsAt, weekOver ? null : last.WeekResetsAt,
            last.Time, last.CreditUsed, last.CreditLimit, last.CreditCurrency, session, week);
    }

    private async Task SwitchAccountAsync(SavedAccount account)
    {
        if (account == _active && account.HasToken)
            return;
        if (account != _active)
        {
            ActivateAccount(account);
            UpdateFlyout();
        }

        if (account.HasToken)
            await RefreshAsync();
        else
            await LoginAsync(account);
    }

    private void OnRenameAccountClicked()
    {
        if (_active is not { } account)
            return;
        if (!RenameAccountDialog.Ask(account.Label ?? Strings.AccountUnnamed, account.ShortName, out var shortName))
            return;

        account.ShortName = shortName;
        AccountStore.Save(account);
        UpdateAccountMenu();
        if (_currentIconKind == IconKind.Usage && _lastUsageSnapshot is { } snapshot)
            ApplyUsageTooltip(snapshot);
        UpdateFlyout();
    }

    /// <summary>Removes the active account (its token; the history stays) and switches to
    /// the next one that's signed in, else the next one, else "not logged in".</summary>
    private async Task RemoveActiveAccountAsync()
    {
        if (_active is not { } account)
            return;
        var answer = MessageBox.Show(Strings.ConfirmRemoveAccount(ActiveName), Strings.AppTitle,
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
            return;

        RemoveAccountEntry(account);
        ActivateAccount(_accounts.FirstOrDefault(a => a.HasToken) ?? _accounts.FirstOrDefault());
        UpdateFlyout();
        await RefreshAsync();
    }

    private void RemoveAccountEntry(SavedAccount account)
    {
        AccountStore.Remove(account.Key);
        _accounts.Remove(account);
        _alertStates.Remove(account.Key);
        _lastSnapshots.Remove(account.Key);
        _resolvedAccounts.Remove(account.Key);
        _miniDashboardSettings.RemoveAccount(account.Key);
    }

    /// <summary>The "Logged in as …" entry; its submenu is rebuilt when it opens.</summary>
    private void UpdateAccountMenu()
    {
        _accountMenu.Visible = _active is not null;
        if (_active is not null)
            _accountMenu.Text = _active.HasToken ? Strings.UserLabel(ActiveName) : ActiveName + Strings.AccountLoginRequiredSuffix;
        if (!_accountMenu.HasDropDownItems)
            RebuildAccountMenuItems();
    }

    private void RebuildAccountMenuItems()
    {
        foreach (var item in _accountMenu.DropDownItems.Cast<ToolStripItem>().ToList())
            item.Dispose();
        _accountMenu.DropDownItems.Clear();
        _accountMenu.DropDownItems.AddRange([.. CreateAccountItems(withManagement: true)]);
    }

    /// <summary>The account list (active one checked, a click switches) and "Add account…" -
    /// for the tray menu, the flyout's ▾ button and the mini dashboard's menu. <paramref
    /// name="withManagement"/> adds "Rename…" and "Remove active account…".</summary>
    private List<ToolStripItem> CreateAccountItems(bool withManagement)
    {
        var items = new List<ToolStripItem>();
        foreach (var account in _accounts)
        {
            var name = account is { ShortName: { } shortName, Label: { } label }
                ? $"{shortName} – {label}"
                : account.DisplayName ?? Strings.AccountUnnamed;
            if (!account.HasToken)
                name += Strings.AccountLoginRequiredSuffix;
            var item = new ToolStripMenuItem(name) { Checked = account == _active, Enabled = !_loginInProgress };
            item.Click += async (_, _) => await SwitchAccountAsync(account);
            items.Add(item);
        }

        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem(Strings.MenuAddAccount, null, async (_, _) => await LoginAsync(null)) { Enabled = !_loginInProgress });
        if (withManagement)
        {
            items.Add(new ToolStripMenuItem(Strings.MenuRenameAccount, null, (_, _) => OnRenameAccountClicked()) { Enabled = _active is not null });
            items.Add(new ToolStripMenuItem(Strings.MenuRemoveAccount, null, async (_, _) => await RemoveActiveAccountAsync()) { Enabled = _active is not null && !_loginInProgress });
        }
        return items;
    }

    // ---------------------------------------------------------------- history, menus

    private void ResetForecasts()
    {
        _sessionForecast = UsageForecast.None;
        _weeklyForecast = UsageForecast.None;
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

    private void OnExportDailyClicked(object? sender, EventArgs e)
    {
        var days = _history.DailyUsage;
        if (days.Count == 0)
        {
            MessageBox.Show(Strings.HistoryEmpty, Strings.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Title = Strings.HistoryExportDailyDialogTitle,
            Filter = Strings.HistoryCsvFilter,
            DefaultExt = "csv",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"AIQuota-daily-{DateTime.Now:yyyy-MM-dd}.csv",
        };
        if (dialog.ShowDialog() != DialogResult.OK)
            return;

        try
        {
            UsageHistoryCsv.WriteDaily(dialog.FileName, days);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(Strings.HistoryExportFailed(ex.Message), Strings.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Opens the yearly heatmap (see <see cref="UsageHeatmap"/>), or brings it to
    /// the front. It's disposed when closed and created anew the next time.</summary>
    private void ShowHeatmap()
    {
        if (_heatmap is null || _heatmap.IsDisposed)
        {
            _heatmap = new UsageHeatmap(BuildHeatmapState);
            // A closed modeless form disposes itself.
            _heatmap.FormClosed += (_, _) => _heatmap = null;
        }
        _heatmap.ShowHeatmap();
    }

    private HeatmapState BuildHeatmapState() => new(
        Days: _history.DailyUsage,
        LoggedIn: IsLoggedIn,
        AccountLabel: _active?.DisplayName,
        Greyscale: GreyscaleActive);

    private void OnClearHistoryClicked(object? sender, EventArgs e)
    {
        var answer = MessageBox.Show(Strings.ConfirmClearHistory, Strings.AppTitle,
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
            return;

        _history.Clear();
        ResetForecasts();
        _alerts.SessionForecastWarningShown = false;
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

    /// <summary>Redraws the tray icon right away when the Windows light/dark theme or High
    /// Contrast changes, so its colours (picked per-theme in <see cref="UsagePalette"/>) stay
    /// legible instead of waiting for the next usage poll.</summary>
    private void OnSystemPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category == Microsoft.Win32.UserPreferenceCategory.General ||
            e.Category == Microsoft.Win32.UserPreferenceCategory.Color ||
            e.Category == Microsoft.Win32.UserPreferenceCategory.Accessibility)
        {
            ApplyAccessibilityMenuState();
            RedrawIconForCurrentState();
            UpdateFlyout();
        }
    }

    private void OnNotifyIconMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;
        Flyout.Toggle(Cursor.Position);
    }

    private UsageFlyout Flyout => _flyout ??= new UsageFlyout(BuildFlyoutState, RefreshAsync, ShowMiniDashboard, ShowHeatmap)
    {
        CreateAccountItems = () => CreateAccountItems(withManagement: false),
    };

    /// <summary>Shows the mini dashboard (see <see cref="MiniDashboard"/>) and remembers that
    /// it's open, so it reopens on the next start.</summary>
    private void ShowMiniDashboard()
    {
        _miniDashboard ??= new MiniDashboard(BuildFlyoutState, RefreshAsync,
            openFlyout: () => Flyout.Open(Cursor.Position),
            close: CloseMiniDashboard,
            _miniDashboardSettings, settings => settings.Save())
        {
            CreateAccountItems = () => CreateAccountItems(withManagement: false),
        };
        if (!_miniDashboard.Visible)
            _miniDashboard.ShowDashboard();
        _miniDashboardSettings.IsOpen = true;
        _miniDashboardSettings.Save();
        _miniDashboardItem.Checked = true;
    }

    private void CloseMiniDashboard()
    {
        _miniDashboard?.HideDashboard();
        _miniDashboardSettings.IsOpen = false;
        _miniDashboardSettings.Save();
        _miniDashboardItem.Checked = false;
    }

    /// <summary>A button or the body of a notification was clicked (see <see
    /// cref="INotifier"/>). Only the new-version notification's "Install…" leads to the
    /// update flow - and only if an update is still pending, e.g. not after the app was
    /// restarted by a click on an old notification.</summary>
    private void OnNotificationAction(NotificationAction action)
    {
        switch (action)
        {
            case NotificationAction.OpenUsagePage:
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(UsagePageUrl) { UseShellExecute = true });
                break;
            case NotificationAction.Install:
                OnNewVersionClicked();
                break;
            default:
                Flyout.Open(Cursor.Position);
                break;
        }
    }

    /// <summary>Zip build only: removes the notification registration (see <see
    /// cref="ToastNotifier.RemoveRegistration"/>) and exits, as a clean-up before deleting
    /// the app.</summary>
    private void OnRemoveNotificationRegistrationClicked(object? sender, EventArgs e)
    {
        var answer = MessageBox.Show(Strings.ConfirmRemoveNotificationRegistration, Strings.AppTitle,
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
            return;

        try
        {
            ToastNotifier.RemoveRegistration();
        }
        catch (Exception)
        {
            // Nothing was registered (toasts were unavailable), or it's already gone.
        }
        ExitThread();
    }

    /// <summary>Re-reads the state of the flyout and the mini dashboard, if they have been
    /// created - each only repaints while it's open.</summary>
    private void UpdateFlyout()
    {
        _flyout?.UpdateState();
        _miniDashboard?.UpdateState();
        _heatmap?.UpdateState();
    }

    private FlyoutState BuildFlyoutState() => new(
        LoggedIn: IsLoggedIn,
        AccountLabel: _active?.DisplayName,
        StatusText: _statusItem.Text ?? "",
        IsRefreshing: _refreshInProgress,
        Snapshot: _lastUsageSnapshot,
        CreditPercent: _lastCreditPercent,
        SessionNotStarted: SessionNotStarted,
        ShowPaceMarker: _showWeeklyPaceItem.Checked,
        SessionForecast: _sessionForecast,
        WeeklyForecast: _weeklyForecast,
        History: _history.Records,
        Greyscale: GreyscaleActive,
        HasError: _currentIconKind == IconKind.Warning,
        AccountKey: _active?.Key,
        CanSwitchAccount: MultipleAccounts);

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

    /// <summary>Switches the accessibility mode (greyscale palette) and redraws the tray icon,
    /// the flyout and - via the deck snapshot - the Stream Dock keys right away.</summary>
    private void OnToggleAccessibilityMode(object? sender, EventArgs e)
    {
        _accessibilityModeEnabled = !_accessibilityModeEnabled;
        AccessibilityModePreference.SetEnabled(_accessibilityModeEnabled);
        ApplyAccessibilityMenuState();
        RedrawIconForCurrentState();
        UpdateFlyout();
    }

    /// <summary>The menu entry shows the saved choice, or - while Windows High Contrast forces
    /// the mode on - checked and disabled, with a note saying why.</summary>
    private void ApplyAccessibilityMenuState()
    {
        var forced = AccessibilityModePreference.ForcedBySystem;
        _accessibilityModeItem.Checked = forced || _accessibilityModeEnabled;
        _accessibilityModeItem.Enabled = !forced;
        _accessibilityModeItem.Text = forced ? Strings.MenuAccessibilityModeForced : Strings.MenuAccessibilityMode;
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
                _notifier.ShowNewVersion(newVersion.Version);
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

    /// <summary>"Log in…" while there's no account, "Log in again as …" while the active
    /// account's sign-in has expired; hidden otherwise.</summary>
    private void UpdateLoginMenuState()
    {
        _loginItem.Visible = !IsLoggedIn;
        _loginItem.Text = _active is null ? Strings.MenuLogin : Strings.MenuLoginAgain(ActiveName);
        _loginItem.Enabled = !_loginInProgress;
    }

    /// <summary>Fetches the active account's usage. If the account is switched while a fetch
    /// runs, that fetch's result is discarded and the new account is fetched right after.</summary>
    private async Task RefreshAsync()
    {
        if (_refreshInProgress)
            return;
        _refreshInProgress = true;
        try
        {
            int generation;
            do
            {
                generation = _generation;
                await RefreshOnceAsync(generation);
            }
            while (generation != _generation);
        }
        finally
        {
            _refreshInProgress = false;
            UpdateFlyout();
        }
    }

    private async Task RefreshOnceAsync(int generation)
    {
        if (_hasUsageSnapshot)
        {
            _currentIconKind = IconKind.Refreshing;
            SetIcon(TrayIconFactory.CreateRefreshingIcon(TrayPalette, _lastSessionPercent, _lastWeeklyPercent, _lastCreditPercent, HasUpdate, SessionRingFraction, SessionNotStarted, WeeklyPaceFraction));
        }
        UpdateFlyout();

        if (_active is not { } account)
        {
            ApplyResult(new UsageFetchResult(UsageFetchStatus.NotLoggedIn, null));
            return;
        }

        var result = await _usageApi.FetchAsync(account.Key, CancellationToken.None);
        if (generation != _generation)
            return;

        // The account is resolved before the result is applied, so the very first fetch
        // already lands in the right account's history and feeds the forecast.
        if (result.Status == UsageFetchStatus.Ok)
        {
            await ResolveAccountAsync(account, generation);
            if (generation != _generation)
                return;
        }

        if (result is { Status: UsageFetchStatus.Ok, Snapshot: { } snapshot })
            _history.Append(UsageHistoryRecord.FromSnapshot(snapshot));

        ApplyResult(result);
    }

    /// <summary>Fetches the account's label (shown in the menu and flyout) and ID (keys the
    /// usage history) once per run. On failure both stay as they were and the next successful
    /// usage fetch tries again; without an ID, fetches aren't recorded in the history.</summary>
    private async Task ResolveAccountAsync(SavedAccount account, int generation)
    {
        if (_resolvedAccounts.Contains(account.Key))
            return;
        if (await _usageApi.FetchAccountAsync(account.Key, CancellationToken.None) is not { } info)
            return;
        if (generation != _generation || !_accounts.Contains(account))
            return;

        _resolvedAccounts.Add(account.Key);
        ApplyAccountInfo(account, info);
        UpdateLoginMenuState();
    }

    /// <summary>Empties the usage shown in the menu, icon state and forecasts.</summary>
    private void ClearUsageDisplay()
    {
        _hasUsageSnapshot = false;
        _lastCreditPercent = null;
        _lastSessionResetsAt = null;
        _lastWeeklyResetsAt = null;
        _lastUsageSnapshot = null;
        _sessionItem.Text = Strings.MenuSessionEmpty;
        _weeklyItem.Text = Strings.MenuWeeklyEmpty;
        _creditsItem.Visible = false;
        ApplyLimitMenuItems([]);
        ResetForecasts();
    }

    private void ApplyResult(UsageFetchResult result)
    {
        switch (result.Status)
        {
            case UsageFetchStatus.NotLoggedIn:
            case UsageFetchStatus.AuthExpired:
                // No account at all, or the active one has no (valid) token any more: it stays
                // in the account list as "login required", the other accounts aren't affected.
                if (result.Status == UsageFetchStatus.AuthExpired && _active is not null)
                    TokenStore.Clear(_active.Key);
                var needsLogin = _active is not null;
                _currentIconKind = IconKind.Unavailable;
                SetIcon(TrayIconFactory.CreateUnavailableIcon(TrayPalette, HasUpdate));
                SetNotifyIconText(needsLogin ? Strings.TooltipAuthExpired : Strings.TooltipNotLoggedIn);
                ClearUsageDisplay();
                _statusItem.Text = needsLogin ? Strings.StatusPleaseReauth : Strings.StatusPromptLogin;
                UpdateLoginMenuState();
                UpdateAccountMenu();
                return;

            case UsageFetchStatus.NetworkError:
                _currentIconKind = IconKind.Warning;
                SetIcon(TrayIconFactory.CreateWarningIcon(TrayPalette, HasUpdate));
                _statusItem.Text = Strings.FetchError(result.Error ?? "");
                SetNotifyIconText(Strings.FetchError(result.Error ?? ""));
                return;
        }

        ApplySnapshot(result.Snapshot!, live: true);
    }

    /// <summary>Shows a usage snapshot in the icon, tooltip, menu and forecasts. <paramref
    /// name="live"/>: just fetched (remembered per account, may notify); otherwise the last
    /// known values shown right after switching accounts.</summary>
    private void ApplySnapshot(UsageSnapshot snapshot, bool live)
    {
        if (live && _active is not null)
            _lastSnapshots[_active.Key] = snapshot;
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
        SetIcon(TrayIconFactory.CreateUsageIcon(TrayPalette, snapshot.SessionPercent, snapshot.WeeklyPercent, creditPercent, HasUpdate, SessionRingFraction, SessionNotStarted, WeeklyPaceFraction));

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

        ApplyLimitMenuItems(snapshot.AdditionalLimits ?? []);

        _statusItem.Text = Strings.StatusUpdated(snapshot.FetchedAt);

        if (live)
            MaybeWarn(snapshot);
    }

    /// <summary>One disabled menu line per additional limit (e.g. a model's weekly limit):
    /// limits of the session window right under the session entries, all others under the
    /// week entries, each group sorted by name. Replaces the lines of the previous refresh,
    /// so a limit that's no longer reported disappears.</summary>
    private void ApplyLimitMenuItems(IReadOnlyList<UsageLimitInfo> limits)
    {
        var menu = _notifyIcon.ContextMenuStrip!;
        foreach (var item in _limitItems)
        {
            menu.Items.Remove(item);
            item.Dispose();
        }
        _limitItems.Clear();

        void InsertAfter(ToolStripItem anchor, IEnumerable<UsageLimitInfo> group)
        {
            var index = menu.Items.IndexOf(anchor);
            foreach (var limit in group.OrderBy(Strings.LimitLabel, StringComparer.CurrentCultureIgnoreCase))
            {
                var item = new ToolStripMenuItem(Strings.LimitMenuLabel(limit)) { Enabled = false };
                menu.Items.Insert(++index, item);
                _limitItems.Add(item);
            }
        }

        InsertAfter(_sessionForecastItem, limits.Where(l => l.IsSessionGroup));
        InsertAfter(_weeklyForecastItem, limits.Where(l => !l.IsSessionGroup));
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
            withForecast ? _weeklyForecast.LimitIn : null,
            MultipleAccounts ? ActiveName : null);

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
        WeeklyPaceFraction: WeeklyPaceFraction,
        Greyscale: GreyscaleActive);

    /// <summary>Regenerates whichever icon is currently shown, for when something it depends
    /// on changes independently of the next usage refresh: update availability (e.g. the
    /// periodic background version check), a display setting, the theme or the palette.</summary>
    private void RedrawIconForCurrentState()
    {
        var palette = TrayPalette;
        var icon = _currentIconKind switch
        {
            IconKind.Warning => TrayIconFactory.CreateWarningIcon(palette, HasUpdate),
            IconKind.Usage => TrayIconFactory.CreateUsageIcon(palette, _lastSessionPercent, _lastWeeklyPercent, _lastCreditPercent, HasUpdate, SessionRingFraction, SessionNotStarted, WeeklyPaceFraction),
            IconKind.Refreshing => TrayIconFactory.CreateRefreshingIcon(palette, _lastSessionPercent, _lastWeeklyPercent, _lastCreditPercent, HasUpdate, SessionRingFraction, SessionNotStarted, WeeklyPaceFraction),
            _ => TrayIconFactory.CreateUnavailableIcon(palette, HasUpdate),
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

    /// <summary>Notifies about the active account's usage; the state of what has been
    /// notified is kept per account (<see cref="AlertState"/>).</summary>
    private void MaybeWarn(UsageSnapshot snapshot)
    {
        var alerts = _alerts;
        if (snapshot.SessionPercent >= WarnThresholdPercent && !alerts.SessionWarningShown)
        {
            _notifier.ShowUsageAlert(UsageAlert.Session,
                AlertText(Strings.NotificationSessionWarning(snapshot.SessionPercent, snapshot.SessionResetsAt)), snapshot.SessionResetsAt);
            alerts.SessionWarningShown = true;
        }
        else if (snapshot.SessionPercent < WarnThresholdPercent)
        {
            alerts.SessionWarningShown = false;
        }

        if (snapshot.WeeklyPercent >= WarnThresholdPercent && !alerts.WeeklyWarningShown)
        {
            _notifier.ShowUsageAlert(UsageAlert.Weekly,
                AlertText(Strings.NotificationWeeklyWarning(snapshot.WeeklyPercent, snapshot.WeeklyResetsAt)), snapshot.WeeklyResetsAt);
            alerts.WeeklyWarningShown = true;
        }
        else if (snapshot.WeeklyPercent < WarnThresholdPercent)
        {
            alerts.WeeklyWarningShown = false;
        }

        // Additional limits: once per limit, until it drops below the threshold again (or
        // is no longer reported).
        var limits = snapshot.AdditionalLimits ?? [];
        alerts.LimitWarningsShown.IntersectWith(limits.Select(l => l.Key));
        foreach (var limit in limits)
        {
            if (limit.RoundedPercent >= WarnThresholdPercent)
            {
                if (alerts.LimitWarningsShown.Add(limit.Key))
                    _notifier.ShowUsageAlert(UsageAlert.AdditionalLimit, AlertText(Strings.NotificationLimitWarning(limit)), limit.ResetsAt, limit.Key);
            }
            else
            {
                alerts.LimitWarningsShown.Remove(limit.Key);
            }
        }

        if (_sessionForecast.LimitIn is { } limitIn && limitIn <= ForecastWarnWithin)
        {
            if (!alerts.SessionForecastWarningShown)
            {
                _notifier.ShowUsageAlert(UsageAlert.SessionForecast,
                    AlertText(Strings.NotificationSessionForecast(limitIn)), snapshot.SessionResetsAt);
                alerts.SessionForecastWarningShown = true;
            }
        }
        else if (_sessionForecast.LimitIn is not { } later || later > ForecastRearmAbove)
        {
            // No forecast any more (idle, reset first, new session) or comfortably far away.
            alerts.SessionForecastWarningShown = false;
        }
    }

    /// <summary>With more than one account, a notification names the account it's about.</summary>
    private string AlertText(string text) => MultipleAccounts ? $"{ActiveName}: {text}" : text;


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
            _miniDashboard?.Dispose();
            _heatmap?.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Icon?.Dispose();
            _notifyIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}
