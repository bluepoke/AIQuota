namespace AIQuota.StreamDock;

/// <summary>Holds the most recently computed <see cref="DeckStatusSnapshot"/> so
/// <see cref="StreamDockBridge"/> can serve it to the plugin on request without reaching
/// into <see cref="UsageTrayContext"/>'s private fields. Written from the UI thread after
/// every tray icon update; read from the bridge's HTTP listener thread - a single
/// reference assignment/read is already atomic in .NET, so no locking is needed.</summary>
internal static class DeckStatusHolder
{
    private static readonly DeckStatusSnapshot LoggedOut = new(
        LoggedIn: false, IsWarning: false, IsRefreshing: false,
        SessionPercent: 0, WeeklyPercent: 0, CreditPercent: null, SessionRemainingFraction: null);

    public static DeckStatusSnapshot Current { get; set; } = LoggedOut;
}
