using Microsoft.Win32;

namespace AIQuota.StreamDock;

/// <summary>Persists whether the local Stream Dock bridge (<see cref="StreamDockBridge"/>)
/// should run. Off by default since, unlike the other tray preferences, this one opens a
/// (loopback-only) HTTP port.</summary>
internal static class StreamDockPreference
{
    private const string KeyPath = @"Software\AIQuota";
    private const string ValueName = "EnableStreamDockBridge";

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
}
