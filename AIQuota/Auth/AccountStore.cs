using Microsoft.Win32;

namespace AIQuota.Auth;

/// <summary>One Claude account the app has been signed in with (#13).</summary>
public sealed class SavedAccount
{
    /// <summary>Local, random identifier: names the account's token file and registry key,
    /// so neither contains the email. Unlike <see cref="AccountId"/> it's known from the
    /// start, before the profile could be fetched.</summary>
    public required string Key { get; init; }

    /// <summary>Stable ID from the profile endpoint (see <see cref="AccountInfo.Id"/>), which
    /// keys the usage history and tells whether two logins are the same account; null until
    /// the profile has been fetched once.</summary>
    public string? AccountId { get; set; }

    /// <summary>"Name (email)", just the name, or just the email, from the profile.</summary>
    public string? Label { get; set; }

    /// <summary>Optional name the user gave the account, e.g. "Work".</summary>
    public string? ShortName { get; set; }

    /// <summary>When the account was added - the order of the account list.</summary>
    public DateTimeOffset AddedAt { get; init; }

    /// <summary>Also polled while it isn't the active account, and shown next to it (#18).
    /// The active account is always polled.</summary>
    public bool MonitorInBackground { get; set; }

    /// <summary>The short name, else the profile's label; null if neither is known yet.</summary>
    public string? DisplayName => ShortName ?? Label;

    /// <summary>Whether a token is stored. Without one (refresh token rejected, or the usage
    /// API answered 401) the account stays in the list and needs a new login.</summary>
    public bool HasToken => TokenStore.Exists(Key);
}

/// <summary>
/// The saved accounts and which one is active, under <c>HKCU\Software\AIQuota\Accounts</c>:
/// a subkey per account (named by <see cref="SavedAccount.Key"/>) with its ID, label and
/// short name, and the active account's key in the value <c>Active</c>. The tokens
/// themselves are in <see cref="TokenStore"/>.
/// </summary>
public static class AccountStore
{
    private const string KeyPath = @"Software\AIQuota\Accounts";

    /// <summary>All saved accounts, oldest first.</summary>
    public static List<SavedAccount> LoadAll()
    {
        var accounts = new List<SavedAccount>();
        using var root = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        if (root is null)
            return accounts;

        // The account keys are the subkeys; values of the Accounts key itself are settings.
        foreach (var name in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(name, writable: false);
            if (key is null)
                continue;
            accounts.Add(new SavedAccount
            {
                Key = name,
                AccountId = key.GetValue("AccountId") as string,
                Label = key.GetValue("Label") as string,
                ShortName = key.GetValue("ShortName") as string,
                AddedAt = key.GetValue("AddedAt") is long ticks && ticks > 0
                    ? new DateTimeOffset(ticks, TimeSpan.Zero)
                    : DateTimeOffset.MinValue,
                MonitorInBackground = key.GetValue("MonitorInBackground") is int monitor && monitor != 0,
            });
        }
        accounts.Sort((a, b) => a.AddedAt.CompareTo(b.AddedAt));
        return accounts;
    }

    public static string? LoadActiveKey()
    {
        using var root = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        return root?.GetValue("Active") as string;
    }

    public static void SaveActiveKey(string? accountKey)
    {
        using var root = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        if (accountKey is null)
            root.DeleteValue("Active", throwOnMissingValue: false);
        else
            root.SetValue("Active", accountKey, RegistryValueKind.String);
    }

    /// <summary>A new, saved account without token, ID or label yet.</summary>
    public static SavedAccount Create()
    {
        var account = new SavedAccount { Key = Guid.NewGuid().ToString("N"), AddedAt = DateTimeOffset.UtcNow };
        Save(account);
        return account;
    }

    public static void Save(SavedAccount account)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"{KeyPath}\{account.Key}", writable: true);
        SetOrDelete(key, "AccountId", account.AccountId);
        SetOrDelete(key, "Label", account.Label);
        SetOrDelete(key, "ShortName", account.ShortName);
        key.SetValue("AddedAt", account.AddedAt.UtcTicks, RegistryValueKind.QWord);
        key.SetValue("MonitorInBackground", account.MonitorInBackground ? 1 : 0, RegistryValueKind.DWord);
    }

    /// <summary>One tray icon per monitored account instead of a single one (#18); off by
    /// default.</summary>
    public static bool LoadIconPerAccount()
    {
        using var root = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        return root?.GetValue("IconPerAccount") is int value && value != 0;
    }

    public static void SaveIconPerAccount(bool enabled)
    {
        using var root = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        root.SetValue("IconPerAccount", enabled ? 1 : 0, RegistryValueKind.DWord);
    }

    /// <summary>Removes the account and its token. Its usage history stays on disk.</summary>
    public static void Remove(string accountKey)
    {
        TokenStore.Clear(accountKey);
        using var root = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        root?.DeleteSubKeyTree(accountKey, throwOnMissingSubKey: false);
    }

    /// <summary>Versions before #13 kept one token in <c>oauth-token.dat</c>. It becomes the
    /// first, active account (its ID and label are filled in by the next refresh), so an
    /// existing installation stays signed in.</summary>
    public static void MigrateLegacyToken()
    {
        if (TokenStore.LoadLegacy() is not { } tokens)
        {
            // Nothing there, or unreadable (e.g. another Windows user's file): drop it.
            TokenStore.DeleteLegacy();
            return;
        }

        var account = Create();
        TokenStore.Save(account.Key, tokens);
        SaveActiveKey(account.Key);
        TokenStore.DeleteLegacy();
    }

    private static void SetOrDelete(RegistryKey key, string name, string? value)
    {
        if (value is null)
            key.DeleteValue(name, throwOnMissingValue: false);
        else
            key.SetValue(name, value, RegistryValueKind.String);
    }
}
