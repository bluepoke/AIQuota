namespace AIQuota.StreamDock;

/// <summary>What a Stream Dock key shows - picked per key in the plugin's settings panel
/// and passed to <see cref="StreamDockBridge"/> as <c>GET /status?style=...</c>.</summary>
public enum DeckKeyStyle
{
    /// <summary>Copy of the tray icon: all bars plus the countdown ring.</summary>
    Combined,

    /// <summary>The 5-hour session as one large bar, with its countdown underneath.</summary>
    Session,

    /// <summary>The 7-day (weekly) usage as one large bar.</summary>
    Week,

    /// <summary>The usage credit balance as one large bar.</summary>
    Credit,
}

public static class DeckKeyStyles
{
    /// <summary>Parses the plugin's style value (case-insensitive); anything missing or
    /// unknown falls back to <see cref="DeckKeyStyle.Combined"/>, so keys placed before
    /// styles existed - which send none - keep their old look.</summary>
    public static DeckKeyStyle Parse(string? value) =>
        Enum.TryParse<DeckKeyStyle>(value, ignoreCase: true, out var style) && Enum.IsDefined(style)
            ? style
            : DeckKeyStyle.Combined;
}
