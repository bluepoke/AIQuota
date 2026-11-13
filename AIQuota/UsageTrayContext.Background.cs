using AIQuota.Auth;
using AIQuota.History;
using AIQuota.Localization;

namespace AIQuota;

/// <summary>
/// Monitoring further accounts in the background (#18): every account with "Monitor in
/// background" that isn't the active one is a <see cref="BackgroundAccount"/>, polled on its
/// own schedule (spread out, with a backoff after rate limiting), recorded in its own history,
/// notified about with its name, and shown next to the active account - in the flyout, the
/// mini dashboard and, optionally, with a tray icon of its own.
/// </summary>
public sealed partial class UsageTrayContext
{
    /// <summary>How often due background fetches are looked for; each account itself is
    /// fetched every <see cref="PollInterval"/>.</summary>
    private static readonly TimeSpan BackgroundTick = TimeSpan.FromSeconds(15);

    /// <summary>First fetches of newly monitored accounts are this far apart, so they don't
    /// all go out at once - and stay spread out, since each continues on its own interval.</summary>
    private static readonly TimeSpan BackgroundStagger = TimeSpan.FromSeconds(30);

    /// <summary>One history per account ID, shared by the active account and background
    /// monitoring; accounts without a known ID use <see cref="_noHistory"/>, which records
    /// nothing.</summary>
    private readonly Dictionary<string, JsonlUsageHistory> _histories = [];
    private readonly JsonlUsageHistory _noHistory = new();

    /// <summary>The monitored accounts that aren't active, by <see cref="SavedAccount.Key"/>.</summary>
    private readonly Dictionary<string, BackgroundAccount> _backgroundAccounts = [];
    private readonly System.Windows.Forms.Timer _backgroundTimer = new() { Interval = (int)BackgroundTick.TotalMilliseconds };

    private readonly ToolStripMenuItem _iconPerAccountItem = new() { CheckOnClick = false };
    private bool _iconPerAccount = AccountStore.LoadIconPerAccount();

    /// <summary>With one icon per account: which icon belongs to which account. <see
    /// cref="_notifyIcon"/> is bound to one of them (it's never disposed - the balloon
    /// fallback for notifications uses it); further icons are created as needed. An icon
    /// stays with its account when switching, so the icons don't swap places.</summary>
    private readonly Dictionary<string, NotifyIcon> _accountIcons = [];

    /// <summary>A left click on another account's icon switched to it in MouseDown.</summary>
    private bool _switchedByIconClick;

    /// <summary>The icon that shows the active account.</summary>
    private NotifyIcon ActiveNotifyIcon =>
        _active is not null && _accountIcons.TryGetValue(_active.Key, out var icon) ? icon : _notifyIcon;

    private IUsageHistory HistoryFor(string? accountId)
    {
        if (accountId is null)
            return _noHistory;
        if (!_histories.TryGetValue(accountId, out var history))
        {
            history = new JsonlUsageHistory();
            history.SetAccount(accountId);
            _histories[accountId] = history;
        }
        return history;
    }

    private void StartBackgroundMonitoring()
    {
        _iconPerAccountItem.Checked = _iconPerAccount;
        _iconPerAccountItem.Click += (_, _) => OnToggleIconPerAccount();
        _backgroundTimer.Tick += (_, _) => FetchDueBackgroundAccounts();
        _backgroundTimer.Start();
        SyncBackgroundAccounts();
    }

    private void DisposeBackgroundMonitoring()
    {
        _backgroundTimer.Dispose();
        foreach (var icon in _accountIcons.Values.Where(i => i != _notifyIcon))
        {
            icon.Visible = false;
            icon.Icon?.Dispose();
            icon.Dispose();
        }
        _accountIcons.Clear();
    }

    /// <summary>Brings the background accounts in line with the account list, the monitor
    /// settings and the active account - after every change to any of them. A newly monitored
    /// account starts with its last known values (and continues one interval after them), or
    /// is fetched within moments, a little after the previous new one.</summary>
    private void SyncBackgroundAccounts()
    {
        var wanted = _accounts.Where(a => a.MonitorInBackground && a != _active).ToList();
        foreach (var key in _backgroundAccounts.Keys.Where(key => wanted.All(a => a.Key != key)).ToList())
            _backgroundAccounts.Remove(key);

        var now = DateTimeOffset.Now;
        var nextFirstFetch = now + TimeSpan.FromSeconds(5);
        foreach (var account in wanted.Where(a => !_backgroundAccounts.ContainsKey(a.Key)))
        {
            var background = new BackgroundAccount(account);
            if (_lastSnapshots.TryGetValue(account.Key, out var snapshot))
            {
                background.Snapshot = snapshot;
                background.Status = UsageFetchStatus.Ok;
                background.NextFetchAt = snapshot.FetchedAt + PollInterval;
            }
            else
            {
                // Shown until the first fetch, which is due shortly.
                background.Snapshot = SnapshotFromHistory(HistoryFor(account.AccountId).Records);
                background.NextFetchAt = nextFirstFetch;
                nextFirstFetch += BackgroundStagger;
            }
            if (background.Snapshot is { } shown)
                (background.SessionForecast, background.WeeklyForecast) = ComputeForecasts(HistoryFor(account.AccountId).Records, shown, now);
            _backgroundAccounts[account.Key] = background;
        }

        SyncAccountIcons();
        UpdateFlyout();
    }

    private void FetchDueBackgroundAccounts()
    {
        var now = DateTimeOffset.Now;
        foreach (var background in _backgroundAccounts.Values.ToList())
        {
            if (!background.IsFetching && background.Account.HasToken && now >= background.NextFetchAt)
                _ = FetchBackgroundAsync(background);
        }
    }

    /// <summary>Still monitored in the background (not removed, unmonitored or made active
    /// while its fetch ran).</summary>
    private bool IsCurrent(BackgroundAccount background) =>
        _backgroundAccounts.TryGetValue(background.Account.Key, out var current) && current == background;

    /// <summary>Fetches one background account, records it in its history and notifies about
    /// it. A failure only affects this account. Discarded if the account became active (the
    /// tray's own refresh takes over) or stopped being monitored meanwhile.</summary>
    private async Task FetchBackgroundAsync(BackgroundAccount background)
    {
        var account = background.Account;
        background.IsFetching = true;
        RenderBackgroundIcon(background);
        UpdateFlyout();
        try
        {
            var result = await _usageApi.FetchAsync(account.Key, CancellationToken.None);
            if (!IsCurrent(background))
                return;

            if (result.Status == UsageFetchStatus.Ok && !_resolvedAccounts.Contains(account.Key)
                && await _usageApi.FetchAccountAsync(account.Key, CancellationToken.None) is { } info)
            {
                if (!IsCurrent(background))
                    return;
                _resolvedAccounts.Add(account.Key);
                // May merge it into another saved entry of the same account, which ends this one.
                ApplyAccountInfo(account, info);
                if (!IsCurrent(background))
                    return;
            }

            var now = DateTimeOffset.Now;
            background.Status = result.Status;
            background.Error = result.Error;
            background.RateLimitStrikes = result.RateLimited ? background.RateLimitStrikes + 1 : 0;
            background.NextFetchAt = now + BackgroundAccount.DelayAfter(result, background.RateLimitStrikes, PollInterval);

            switch (result.Status)
            {
                case UsageFetchStatus.Ok when result.Snapshot is { } snapshot:
                    var history = HistoryFor(account.AccountId);
                    history.Append(UsageHistoryRecord.FromSnapshot(snapshot));
                    background.Snapshot = snapshot;
                    _lastSnapshots[account.Key] = snapshot;
                    (background.SessionForecast, background.WeeklyForecast) = ComputeForecasts(history.Records, snapshot, now);
                    MaybeWarn(snapshot, account, AlertStateFor(account), background.SessionForecast);
                    break;

                case UsageFetchStatus.AuthExpired:
                    // Only this account needs a new login; it's skipped until it has a token.
                    TokenStore.Clear(account.Key);
                    UpdateAccountMenu();
                    break;
            }
        }
        finally
        {
            background.IsFetching = false;
            RenderBackgroundIcon(background);
            UpdateFlyout();
        }
    }

    /// <summary>The newest history record as a snapshot (a window whose reset has passed counts
    /// as empty again) - an account's last known values before it's fetched this run.</summary>
    private static UsageSnapshot? SnapshotFromHistory(IReadOnlyList<UsageHistoryRecord> records)
    {
        if (records is not [.., var last])
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

    private static int? CreditPercentOf(UsageSnapshot snapshot) =>
        snapshot is { CreditUsed: { } used, CreditLimit: > 0 and { } limit }
            ? (int)Math.Round(Math.Clamp(used / limit * 100m, 0, 100))
            : null;

    /// <summary>Fraction of the session window still left (see <see
    /// cref="ComputeSessionRemainingFraction"/>), for a background account's icon.</summary>
    private static double SessionRemainingFraction(UsageSnapshot snapshot) =>
        snapshot.SessionResetsAt is { } resetsAt
            ? Math.Clamp((resetsAt - DateTimeOffset.Now) / UsageWindows.Session, 0.0, 1.0)
            : 1.0;

    /// <summary>The other monitored accounts for the flyout and mini dashboard, in the account
    /// list's order.</summary>
    private List<FlyoutState> BuildOtherAccountStates()
    {
        var states = new List<FlyoutState>();
        foreach (var account in _accounts)
        {
            if (!_backgroundAccounts.TryGetValue(account.Key, out var background))
                continue;

            var snapshot = background.Snapshot;
            var status = !account.HasToken ? Strings.StatusPleaseReauth
                : background.Status == UsageFetchStatus.NetworkError ? Strings.FetchError(background.Error ?? "")
                : background.Status is null || snapshot is null ? Strings.FlyoutRefreshing
                : Strings.StatusUpdated(snapshot.FetchedAt);
            states.Add(new FlyoutState(
                LoggedIn: account.HasToken,
                AccountLabel: account.DisplayName ?? Strings.AccountUnnamed,
                StatusText: status,
                IsRefreshing: background.IsFetching,
                Snapshot: snapshot,
                CreditPercent: snapshot is null ? null : CreditPercentOf(snapshot),
                SessionNotStarted: snapshot is { SessionResetsAt: null },
                ShowPaceMarker: _showWeeklyPaceItem.Checked,
                SessionForecast: background.SessionForecast,
                WeeklyForecast: background.WeeklyForecast,
                History: [],
                Greyscale: GreyscaleActive,
                HasError: background.Status == UsageFetchStatus.NetworkError,
                AccountKey: account.Key,
                AccountOrder: _accounts.IndexOf(account)));
        }
        return states;
    }

    private async Task SwitchAccountByKeyAsync(string accountKey)
    {
        if (_accounts.FirstOrDefault(a => a.Key == accountKey) is { } account)
            await SwitchAccountAsync(account);
    }

    /// <summary>"Monitor in background ›": one checkable entry per account.</summary>
    private ToolStripMenuItem CreateMonitorMenu()
    {
        var menu = new ToolStripMenuItem(Strings.MenuMonitorInBackground) { Enabled = MultipleAccounts };
        foreach (var account in _accounts)
        {
            var item = new ToolStripMenuItem(AccountMenuName(account)) { Checked = account.MonitorInBackground };
            item.Click += (_, _) =>
            {
                account.MonitorInBackground = !account.MonitorInBackground;
                AccountStore.Save(account);
                SyncBackgroundAccounts();
            };
            menu.DropDownItems.Add(item);
        }
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(new ToolStripMenuItem(Strings.MonitorActiveNote) { Enabled = false });
        return menu;
    }

    // ---------------------------------------------------------------- one icon per account

    private void OnToggleIconPerAccount()
    {
        _iconPerAccount = !_iconPerAccount;
        AccountStore.SaveIconPerAccount(_iconPerAccount);
        _iconPerAccountItem.Checked = _iconPerAccount;
        SyncAccountIcons();
    }

    /// <summary>With one icon per account, gives the active account and every background
    /// account an icon and removes the others; without, only <see cref="_notifyIcon"/> is
    /// shown, for the active account. Then redraws all icons.</summary>
    private void SyncAccountIcons()
    {
        var wanted = _iconPerAccount
            ? _accounts.Where(a => a == _active || _backgroundAccounts.ContainsKey(a.Key)).ToList()
            : [];

        foreach (var (key, icon) in _accountIcons.Where(p => wanted.All(a => a.Key != p.Key)).ToList())
        {
            _accountIcons.Remove(key);
            if (icon != _notifyIcon)
            {
                icon.Visible = false;
                icon.Icon?.Dispose();
                icon.Dispose();
            }
        }

        foreach (var account in wanted.Where(a => !_accountIcons.ContainsKey(a.Key)))
        {
            NotifyIcon icon;
            if (!_accountIcons.ContainsValue(_notifyIcon))
            {
                icon = _notifyIcon;
            }
            else
            {
                icon = new NotifyIcon
                {
                    Icon = TrayIconFactory.CreateUnavailableIcon(TrayPalette),
                    ContextMenuStrip = _notifyIcon.ContextMenuStrip,
                    Visible = true,
                };
                icon.MouseDown += OnNotifyIconMouseDown;
                icon.MouseClick += OnNotifyIconMouseClick;
            }
            _accountIcons[account.Key] = icon;
        }

        // Hidden only while all accounts that need an icon have one of their own.
        _notifyIcon.Visible = _accountIcons.Count == 0 || _accountIcons.ContainsValue(_notifyIcon);

        RedrawIconForCurrentState();
        ApplyTooltipText();
    }

    /// <summary>A left click on another account's icon switches to it first, so the flyout
    /// that opens shows that account. A right click doesn't switch: the menu always acts on
    /// the active account - otherwise an account picked in the menu would be switched back
    /// the next time the menu is opened from the same icon.</summary>
    private void OnNotifyIconMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || sender is not NotifyIcon icon || _accountIcons.Count == 0)
            return;
        var key = _accountIcons.FirstOrDefault(p => p.Value == icon).Key;
        if (key is null || key == _active?.Key || _accounts.FirstOrDefault(a => a.Key == key) is not { } account)
            return;

        _switchedByIconClick = true;
        // Switches synchronously before its first await.
        _ = SwitchAccountAsync(account);
    }

    private void RedrawBackgroundIcons()
    {
        foreach (var background in _backgroundAccounts.Values)
            RenderBackgroundIcon(background);
    }

    /// <summary>Draws a background account's own icon and tooltip, if it has one: its bars,
    /// a warning after a failed fetch, or "?" while it needs a login or has no data yet.</summary>
    private void RenderBackgroundIcon(BackgroundAccount background)
    {
        var account = background.Account;
        if (!_accountIcons.TryGetValue(account.Key, out var notifyIcon) || notifyIcon == ActiveNotifyIcon)
            return;

        var palette = TrayPalette;
        var name = account.DisplayName ?? Strings.AccountUnnamed;
        Icon icon;
        string[] tooltip;
        if (!account.HasToken)
        {
            icon = TrayIconFactory.CreateUnavailableIcon(palette, HasUpdate);
            tooltip = [name + Strings.AccountLoginRequiredSuffix];
        }
        else if (background.Status == UsageFetchStatus.NetworkError && !background.IsFetching)
        {
            icon = TrayIconFactory.CreateWarningIcon(palette, HasUpdate);
            tooltip = [$"{name} – {Strings.FetchError(background.Error ?? "")}"];
        }
        else if (background.Snapshot is not { } snapshot)
        {
            icon = TrayIconFactory.CreateUnavailableIcon(palette, HasUpdate);
            tooltip = [Strings.TooltipLoadingAccount(name)];
        }
        else
        {
            var now = DateTimeOffset.Now;
            var sessionNotStarted = snapshot.SessionResetsAt is null;
            double? ring = _showSessionRingItem.Checked ? SessionRemainingFraction(snapshot) : null;
            double? pace = _showWeeklyPaceItem.Checked && snapshot.WeeklyResetsAt is { } weekReset
                ? UsageWindows.WeeklyPaceFraction(weekReset, now)
                : null;
            var credit = CreditPercentOf(snapshot);
            icon = background.IsFetching
                ? TrayIconFactory.CreateRefreshingIcon(palette, snapshot.SessionPercent, snapshot.WeeklyPercent, credit, HasUpdate, ring, sessionNotStarted, pace)
                : TrayIconFactory.CreateUsageIcon(palette, snapshot.SessionPercent, snapshot.WeeklyPercent, credit, HasUpdate, ring, sessionNotStarted, pace);

            string Summary(bool withForecast) => Strings.TooltipSummary(
                snapshot.SessionPercent, snapshot.SessionResetsAt, snapshot.WeeklyPercent, snapshot.WeeklyResetsAt,
                snapshot.FetchedAt, credit, weeklyPacePercent: null,
                withForecast ? background.SessionForecast.LimitIn : null,
                withForecast ? background.WeeklyForecast.LimitIn : null,
                title: name);
            tooltip = [Summary(withForecast: true), Summary(withForecast: false)];
        }

        var old = notifyIcon.Icon;
        notifyIcon.Icon = icon;
        old?.Dispose();
        notifyIcon.Text = FitTooltip(tooltip);
    }
}
