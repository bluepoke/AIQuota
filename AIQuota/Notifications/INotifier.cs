namespace AIQuota.Notifications;

/// <summary>A usage notification. Each kind replaces its own previous notification instead
/// of stacking up.</summary>
internal enum UsageAlert
{
    /// <summary>The 5-hour session crossed the warning threshold.</summary>
    Session,

    /// <summary>The weekly usage crossed the warning threshold.</summary>
    Weekly,

    /// <summary>At the current rate, the session is about to hit its limit.</summary>
    SessionForecast,
}

/// <summary>What the user chose on a notification, handled by <see cref="UsageTrayContext"/>.</summary>
internal enum NotificationAction
{
    /// <summary>Clicked a usage notification itself (not a button): open the tray flyout.</summary>
    OpenFlyout,

    /// <summary>"Open usage page": claude.ai's usage settings in the browser.</summary>
    OpenUsagePage,

    /// <summary>"Install…" on the new-version notification: the update confirmation.</summary>
    Install,
}

/// <summary>Shows the app's notifications - as Windows toasts (<see cref="ToastNotifier"/>),
/// or as tray balloon tips where toasts aren't available (<see cref="BalloonNotifier"/>).
/// What the user then clicks comes back as a <see cref="NotificationAction"/> on the UI
/// thread.</summary>
internal interface INotifier
{
    /// <param name="expiresAt">When the notification is out of date (the window resets) and
    /// may be removed from the notification center; null keeps it.</param>
    void ShowUsageAlert(UsageAlert alert, string text, DateTimeOffset? expiresAt);

    void ShowNewVersion(string version);
}
