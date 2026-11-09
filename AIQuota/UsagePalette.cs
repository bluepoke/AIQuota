using System.Drawing;

namespace AIQuota;

/// <summary>The usage colours shared by every renderer - tray icon (<see
/// cref="TrayIconFactory"/>), Stream Dock keys and the tray flyout - so they always agree,
/// and an alternative palette (e.g. an accessibility mode) only has to be added here.</summary>
internal static class UsagePalette
{
    /// <summary>Green up to 50%, yellow up to 80%, red above.</summary>
    public static Color ColorForPercent(int percent) => percent switch
    {
        > 80 => Color.FromArgb(235, 70, 70),
        > 50 => Color.FromArgb(240, 170, 60),
        _ => Color.FromArgb(90, 190, 110),
    };
}
