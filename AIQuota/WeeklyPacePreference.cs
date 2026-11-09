using Microsoft.Win32;

namespace AIQuota;

/// <summary>Persists the on/off choice for the weekly pace marker drawn on the week bar (and
/// the matching pace value in the tooltip and context menu). Defaults to enabled.</summary>
internal static class WeeklyPacePreference
{
    private const string KeyPath = @"Software\AIQuota";
    private const string ValueName = "ShowWeeklyPaceMarker";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        return key?.GetValue(ValueName) is not int value || value != 0;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        key.SetValue(ValueName, enabled ? 1 : 0, RegistryValueKind.DWord);
    }
}
