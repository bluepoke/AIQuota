using AIQuota.Localization;

namespace AIQuota.Notifications;

/// <summary>Legacy tray balloon tips - the fallback when toasts can't be registered or shown
/// (see <see cref="ToastNotifier"/>), so warnings are never lost. Balloons have no buttons:
/// clicking the new-version balloon opens the update confirmation, clicking a usage warning
/// opens the flyout.</summary>
internal sealed class BalloonNotifier : INotifier
{
    private const int TimeoutMs = 8000;

    private readonly NotifyIcon _notifyIcon;
    private readonly Action<NotificationAction> _onAction;

    /// <summary><see cref="NotifyIcon"/> has a single <see cref="NotifyIcon.BalloonTipClicked"/>
    /// event for all balloons; this decides what a click does. Windows can also report a
    /// click on an older balloon from the notification center - it's then attributed to the
    /// most recent one, so a usage warning shown after the update notice disarms the update
    /// click (the menu entry still offers the update).</summary>
    private bool _lastWasNewVersion;

    public BalloonNotifier(NotifyIcon notifyIcon, Action<NotificationAction> onAction)
    {
        _notifyIcon = notifyIcon;
        _onAction = onAction;
        _notifyIcon.BalloonTipClicked += (_, _) =>
            _onAction(_lastWasNewVersion ? NotificationAction.Install : NotificationAction.OpenFlyout);
    }

    public void ShowUsageAlert(UsageAlert alert, string text, DateTimeOffset? expiresAt)
    {
        _lastWasNewVersion = false;
        _notifyIcon.ShowBalloonTip(TimeoutMs, Strings.AppTitle, text, ToolTipIcon.Warning);
    }

    public void ShowNewVersion(string version)
    {
        _lastWasNewVersion = true;
        _notifyIcon.ShowBalloonTip(TimeoutMs, Strings.AppTitle, Strings.NotificationNewVersionBalloon(version), ToolTipIcon.Info);
    }
}
