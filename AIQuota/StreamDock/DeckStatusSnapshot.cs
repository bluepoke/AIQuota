namespace AIQuota.StreamDock;

/// <summary>Latest usage state needed to render the Stream Dock key image and JSON status -
/// a small, render-only projection of what <see cref="UsageTrayContext"/> already tracks
/// for the tray icon, so <see cref="StreamDockBridge"/> doesn't need access to its private
/// fields.</summary>
public sealed record DeckStatusSnapshot(
    bool LoggedIn,
    bool IsWarning,
    bool IsRefreshing,
    int SessionPercent,
    int WeeklyPercent,
    int? CreditPercent,
    double? SessionRemainingFraction);
