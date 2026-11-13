using System.Security.Cryptography;
using System.Text.Json;

namespace AIQuota.Auth;

/// <summary>
/// Persists the OAuth tokens this app obtained through its own login flow, one file per saved
/// account (see <see cref="AccountStore"/>), encrypted with DPAPI for the current Windows
/// user. This is our own app's data directory - distinct from, and never reading from,
/// Claude Desktop's or Claude Code's storage.
/// </summary>
public static class TokenStore
{
    private static readonly string AppDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIQuota");

    /// <summary><c>%APPDATA%\AIQuota\accounts\&lt;account key&gt;.dat</c>.</summary>
    private static readonly string AccountsDirectory = Path.Combine(AppDirectory, "accounts");

    /// <summary>The single token file of versions before multiple accounts (#13); migrated
    /// by <see cref="AccountStore.MigrateLegacyToken"/>.</summary>
    private static readonly string LegacyFilePath = Path.Combine(AppDirectory, "oauth-token.dat");

    public static bool Exists(string accountKey) => File.Exists(PathFor(accountKey));

    public static TokenSet? Load(string accountKey) => LoadFile(PathFor(accountKey));

    public static void Save(string accountKey, TokenSet tokens)
    {
        Directory.CreateDirectory(AccountsDirectory);
        var plain = JsonSerializer.SerializeToUtf8Bytes(tokens);
        var encrypted = ProtectedData.Protect(plain, optionalEntropy: null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(PathFor(accountKey), encrypted);
    }

    public static void Clear(string accountKey)
    {
        var path = PathFor(accountKey);
        if (File.Exists(path))
            File.Delete(path);
    }

    internal static TokenSet? LoadLegacy() => LoadFile(LegacyFilePath);

    internal static void DeleteLegacy()
    {
        if (File.Exists(LegacyFilePath))
            File.Delete(LegacyFilePath);
    }

    private static string PathFor(string accountKey) => Path.Combine(AccountsDirectory, accountKey + ".dat");

    private static TokenSet? LoadFile(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var encrypted = File.ReadAllBytes(path);
            var plain = ProtectedData.Unprotect(encrypted, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<TokenSet>(plain);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
