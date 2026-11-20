using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Toolbox.Model;

internal sealed class VaultException : Exception
{
    /// <summary>The user backed out of a prompt; nothing went wrong.</summary>
    public bool IsCancellation { get; }

    private VaultException(string message, bool cancelled = false) : base(message)
    {
        IsCancellation = cancelled;
    }

    public static VaultException Cancelled() => new("Vault unlock was cancelled.", cancelled: true);

    public static VaultException Unavailable(string reason) =>
        new($"The vault can't be unlocked on this PC: {reason}");

    public static VaultException KeyStore(string message) => new(message);
}

/// <summary>
/// Owns the vault's key. The key is 32 random bytes kept in vault.key next to
/// the vault, encrypted with DPAPI for the signed-in Windows account, and is
/// handed to the core once per launch after Windows has confirmed it is you
/// (Windows Hello, or the account password where Hello is not set up).
///
/// DPAPI ties the key file to the Windows account, not to this program: any
/// process running as the same user can ask DPAPI to decrypt it. The owner
/// check stops someone at an unlocked desk, not code already running as you.
/// </summary>
internal sealed class VaultManager
{
    public static VaultManager Shared { get; } = new();

    public Vault Vault { get; }

    /// <summary>Mixed into the DPAPI protection so the blob is only good for this purpose. Not a secret.</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Toolbox vault key v1");

    private const int KeyLength = 32;
    private Task? unlocking;

    private VaultManager()
    {
        Vault = OpenVault(AppPaths.VaultPath);
    }

    private static Vault OpenVault(string path)
    {
        try
        {
            return Vault.Open(path);
        }
        catch (Exception error)
        {
            var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var broken = Path.ChangeExtension(path, $".broken-{stamp}.json");
            try { File.Move(path, broken); } catch (Exception) { /* the retry below reports it */ }
            Log.Error($"vault.json could not be read; moved to {broken}", error);
            try
            {
                return Vault.Open(path);
            }
            catch (Exception)
            {
                return Vault.Open(Path.Combine(Path.GetTempPath(), "toolbox-vault.json"));
            }
        }
    }

    public bool RequiresAuthentication => AppSettings.Shared.VaultRequiresAuth;

    /// <summary>
    /// Turns the owner check on or off. Turning it off asks Windows to confirm
    /// it is you first, so the switch cannot be flipped by whoever happens to
    /// be at the keyboard.
    /// </summary>
    public async Task SetRequiresAuthenticationAsync(bool value)
    {
        if (value == RequiresAuthentication) return;
        if (!value)
        {
            await OwnerCheck.VerifyAsync("Confirm it's you to stop asking before the Toolbox vault unlocks.");
        }
        AppSettings.Shared.VaultRequiresAuth = value;
        AppSettings.Shared.Save();
        Log.Info($"vault: owner check turned {(value ? "on" : "off")}");
    }

    /// <summary>The vault, unlocked. Prompts at most once even if several callers ask at the same time.</summary>
    public async Task<Vault> UnlockedVaultAsync()
    {
        if (Vault.IsUnlocked()) return Vault;
        unlocking ??= UnlockAsync();
        var pending = unlocking;
        try
        {
            await pending;
        }
        finally
        {
            if (ReferenceEquals(unlocking, pending)) unlocking = null;
        }
        return Vault;
    }

    public void Lock()
    {
        Vault.Lock();
        Log.Info("vault: locked");
    }

    private async Task UnlockAsync()
    {
        if (RequiresAuthentication)
        {
            await OwnerCheck.VerifyAsync("Confirm it's you to unlock the Toolbox vault.");
        }
        var key = LoadOrCreateKey(out var created);
        try
        {
            Vault.Unlock(key);
            Log.Info(created ? "vault: created a key and unlocked" : "vault: unlocked");
        }
        catch (CoreException) when (created)
        {
            // The vault already holds secrets under a key that is gone; the
            // fresh key cannot open it and must not be left behind as if it could.
            TryDelete(AppPaths.VaultKeyPath);
            throw VaultException.KeyStore(
                "The vault's key file (vault.key) is missing, so the secrets in vault.json can't be decrypted. " +
                "Restore vault.key next to it, or remove vault.json to start a new vault.");
        }
        catch (CoreException.Auth)
        {
            throw VaultException.KeyStore(
                "vault.key does not open this vault: the two files don't belong together. " +
                "Restore the matching pair, or remove both to start a new vault.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    // ----- Key file -----

    private sealed record KeyFile(int Version, string Protection, string Key);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static byte[] LoadOrCreateKey(out bool created)
    {
        var path = AppPaths.VaultKeyPath;
        if (File.Exists(path))
        {
            created = false;
            return ReadKey(path);
        }
        var key = CoreApi.GenerateVaultKey();
        WriteKey(path, key);
        created = true;
        return key;
    }

    private static byte[] ReadKey(string path)
    {
        KeyFile? file;
        byte[] blob;
        try
        {
            file = JsonSerializer.Deserialize<KeyFile>(File.ReadAllText(path), JsonOptions);
            blob = Convert.FromBase64String(file?.Key ?? "");
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            throw VaultException.KeyStore($"The vault's key file ({path}) is damaged and can't be read.");
        }
        byte[] key;
        try
        {
            key = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            throw VaultException.KeyStore(
                "The vault's key file (vault.key) can't be decrypted by this Windows account. It was protected by a " +
                "different account or on a different PC; copy your connections over and re-enter the secrets, or " +
                "remove vault.json and vault.key to start a new vault.");
        }
        if (key.Length != KeyLength)
        {
            CryptographicOperations.ZeroMemory(key);
            throw VaultException.KeyStore("The vault's key file (vault.key) holds a key of an unexpected size.");
        }
        return key;
    }

    private static void WriteKey(string path, byte[] key)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var blob = ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser);
        var file = new KeyFile(1, "dpapi-current-user", Convert.ToBase64String(blob));
        // Sibling temp file then rename; never replaces an existing key.
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(file, JsonOptions));
        File.Move(temp, path, overwrite: false);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) { Log.Warn($"could not remove {path}: {e.Message}"); }
    }
}
