using Microsoft.Win32;

namespace AIQuota;

/// <summary>Persists the on/off choice for the accessibility mode (high-contrast greyscale
/// palette, see <see cref="UsagePalette.Greyscale"/>). Defaults to off. The mode is also on
/// while Windows High Contrast is active, whatever is saved here (see <see
/// cref="IsActive"/>).</summary>
internal static class AccessibilityModePreference
{
    private const string KeyPath = @"Software\AIQuota";
    private const string ValueName = "AccessibilityMode";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        return key?.GetValue(ValueName) is int value && value != 0;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        key.SetValue(ValueName, enabled ? 1 : 0, RegistryValueKind.DWord);
    }

    /// <summary>Windows High Contrast is on, which turns the mode on regardless of the
    /// saved choice.</summary>
    public static bool ForcedBySystem => SystemInformation.HighContrast;

    /// <summary>Whether the greyscale palette is used: the saved choice, or Windows High
    /// Contrast.</summary>
    public static bool IsActive(bool enabled) => enabled || ForcedBySystem;
}
