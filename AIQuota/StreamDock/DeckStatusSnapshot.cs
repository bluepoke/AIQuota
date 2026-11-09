namespace AIQuota.StreamDock;

/// <summary>Latest usage state needed to render the Stream Dock key image and JSON status -
/// a small, render-only projection of what <see cref="UsageTrayContext"/> already tracks
/// for the tray icon, so <see cref="StreamDockBridge"/> doesn't need access to its private
/// fields.</summary>
/// <param name="SessionRemainingFraction">Session time left, regardless of <paramref
/// name="ShowSessionRing"/> - the Session key always shows its countdown.</param>
/// <param name="ShowSessionRing">Whether the tray icon shows its countdown ring, which the
/// combined key mirrors.</param>
/// <param name="WeeklyPaceFraction">Elapsed fraction of the current 7-day window, where the
/// weekly pace marker is drawn on the Combined and Week keys; null when the marker is turned
/// off or there's no weekly reset time.</param>
public sealed record DeckStatusSnapshot(
    bool LoggedIn,
    bool IsWarning,
    bool IsRefreshing,
    int SessionPercent,
    int WeeklyPercent,
    int? CreditPercent,
    double? SessionRemainingFraction,
    bool SessionNotStarted,
    bool ShowSessionRing,
    double? WeeklyPaceFraction = null);
