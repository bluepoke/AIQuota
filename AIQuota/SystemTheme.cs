using Microsoft.Win32;

namespace AIQuota;

/// <summary>Reads whether Windows currently uses a light taskbar/system theme, so the tray
/// icon can pick colours with enough contrast against it (a translucent-white track that
/// reads clearly on a dark taskbar all but disappears on a light one, and vice versa).</summary>
internal static class SystemTheme
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>True for a light taskbar/system UI, false for dark. Falls back to dark (the
    /// app's original, still-supported look) if the registry value can't be read.</summary>
    public static bool IsLightTaskbar()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        if (key?.GetValue("SystemUsesLightTheme") is int systemValue)
            return systemValue != 0;
        if (key?.GetValue("AppsUseLightTheme") is int appsValue)
            return appsValue != 0;
        return false;
    }
}
