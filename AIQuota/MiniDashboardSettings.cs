using System.Drawing;
using Microsoft.Win32;

namespace AIQuota;

/// <summary>Everything the mini dashboard (<see cref="MiniDashboard"/>) remembers, saved
/// under <c>HKCU\Software\AIQuota\MiniDashboard</c>.</summary>
internal sealed class MiniDashboardSettings
{
    private const string KeyPath = @"Software\AIQuota\MiniDashboard";

    public const int MinLogicalWidth = 220;
    public const int MaxLogicalWidth = 640;
    public const int DefaultLogicalWidth = 300;

    /// <summary>The opacity steps offered in the dashboard's menu, in percent.</summary>
    public static readonly int[] OpacitySteps = [100, 90, 75, 60, 45, 30];

    /// <summary>Whether the window was open when the app last ran - it reopens on start.</summary>
    public bool IsOpen { get; set; }

    /// <summary>Top-left corner in screen pixels; null until the window was first placed.</summary>
    public Point? Location { get; set; }

    /// <summary>Width in 96-DPI logical pixels, so it keeps its size across monitors with
    /// different scaling. The height always follows the content.</summary>
    public int LogicalWidth { get; set; } = DefaultLogicalWidth;

    public bool Expanded { get; set; }

    /// <summary>Keys of the bars to show (see <see cref="UsageSection.Key"/>), e.g. "session",
    /// "week", "credits" or "limit:weekly_scoped:opus". Keys of bars that aren't currently
    /// reported stay selected, so they come back when the bar does. This is the default for
    /// an account without its own selection (see <see cref="BarsFor"/>) - the selection from
    /// before multiple accounts.</summary>
    public List<string> Bars { get; set; } = [UsageSections.SessionKey, UsageSections.WeekKey];

    /// <summary>Bar selection per saved account (by <see cref="Auth.SavedAccount.Key"/>), since
    /// accounts on different plans report different additional limits (#13). Saved under
    /// <c>MiniDashboard\Bars</c>, one value per account.</summary>
    private readonly Dictionary<string, List<string>> _accountBars = [];

    /// <summary>The bar selection of the account - <see cref="Bars"/> until it was changed for
    /// that account.</summary>
    public IReadOnlyList<string> BarsFor(string? accountKey) =>
        accountKey is not null && _accountBars.TryGetValue(accountKey, out var bars) ? bars : Bars;

    /// <summary>Sets the account's bar selection (without an account: the default). Call
    /// <see cref="Save"/> after.</summary>
    public void SetBars(string? accountKey, IEnumerable<string> bars)
    {
        if (accountKey is null)
            Bars = [.. bars];
        else
            _accountBars[accountKey] = [.. bars];
    }

    /// <summary>Forgets a removed account's bar selection.</summary>
    public void RemoveAccount(string accountKey)
    {
        _accountBars.Remove(accountKey);
        using var key = Registry.CurrentUser.OpenSubKey(BarsKeyPath, writable: true);
        key?.DeleteValue(accountKey, throwOnMissingValue: false);
    }

    private const string BarsKeyPath = KeyPath + @"\Bars";

    public int OpacityPercent { get; set; } = 100;

    public bool TopMost { get; set; } = true;

    /// <summary>Moving and resizing are disabled.</summary>
    public bool Locked { get; set; }

    public static MiniDashboardSettings Load()
    {
        var settings = new MiniDashboardSettings();
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        if (key is null)
            return settings;

        settings.IsOpen = key.GetValue("Open") is int open && open != 0;
        if (key.GetValue("X") is int x && key.GetValue("Y") is int y)
            settings.Location = new Point(x, y);
        if (key.GetValue("Width") is int width)
            settings.LogicalWidth = Math.Clamp(width, MinLogicalWidth, MaxLogicalWidth);
        settings.Expanded = key.GetValue("Expanded") is int expanded && expanded != 0;
        if (key.GetValue("Bars") is string[] bars && bars.Length > 0)
            settings.Bars = [.. bars.Where(b => !string.IsNullOrWhiteSpace(b)).Distinct()];
        if (key.GetValue("Opacity") is int opacity && OpacitySteps.Contains(opacity))
            settings.OpacityPercent = opacity;
        settings.TopMost = key.GetValue("TopMost") is not int topMost || topMost != 0;
        settings.Locked = key.GetValue("Locked") is int locked && locked != 0;

        using var barsKey = Registry.CurrentUser.OpenSubKey(BarsKeyPath, writable: false);
        foreach (var account in barsKey?.GetValueNames() ?? [])
        {
            if (barsKey!.GetValue(account) is string[] accountBars && accountBars.Length > 0)
                settings._accountBars[account] = [.. accountBars.Where(b => !string.IsNullOrWhiteSpace(b)).Distinct()];
        }
        return settings;
    }

    public void Save()
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        key.SetValue("Open", IsOpen ? 1 : 0, RegistryValueKind.DWord);
        if (Location is { } location)
        {
            key.SetValue("X", location.X, RegistryValueKind.DWord);
            key.SetValue("Y", location.Y, RegistryValueKind.DWord);
        }
        key.SetValue("Width", LogicalWidth, RegistryValueKind.DWord);
        key.SetValue("Expanded", Expanded ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("Bars", Bars.ToArray(), RegistryValueKind.MultiString);
        key.SetValue("Opacity", OpacityPercent, RegistryValueKind.DWord);
        key.SetValue("TopMost", TopMost ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("Locked", Locked ? 1 : 0, RegistryValueKind.DWord);

        using var barsKey = Registry.CurrentUser.CreateSubKey(BarsKeyPath, writable: true);
        foreach (var (account, bars) in _accountBars)
            barsKey.SetValue(account, bars.ToArray(), RegistryValueKind.MultiString);
    }
}
