using System.IO;

namespace Toolbox.Model;

/// <summary>
/// Connections, credentials and providers, backed by the Rust store on disk.
/// Used from the UI thread only; <see cref="Changed"/> fires after every reload.
/// </summary>
internal sealed class AppModel
{
    public static AppModel Shared { get; } = new();

    public IReadOnlyList<Connection> Connections { get; private set; } = [];
    public IReadOnlyList<Credential> Credentials { get; private set; } = [];
    public IReadOnlyList<Provider> Providers { get; private set; } = [];

    /// <summary>Folder paths (<c>ACME/Jump Boxes</c>), including empty ones.</summary>
    public IReadOnlyList<string> Folders { get; private set; } = [];

    /// <summary>Credential id assigned to a folder, by path.</summary>
    public IReadOnlyDictionary<string, string> FolderCredentials { get; private set; } = new Dictionary<string, string>();

    public ConnectionStore Store { get; }
    public string StorePath { get; }

    /// <summary>Raised after the lists above have been re-read from the store.</summary>
    public event Action? Changed;

    private AppModel()
    {
        StorePath = AppPaths.StorePath;
        Store = OpenStore(StorePath);
        Reload();
    }

    private static ConnectionStore OpenStore(string path)
    {
        try
        {
            return ConnectionStore.Open(path);
        }
        catch (Exception error)
        {
            // Keep the unreadable file for inspection and start with an empty one.
            var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var broken = Path.ChangeExtension(path, $".broken-{stamp}.json");
            try { File.Move(path, broken); } catch (Exception) { /* the retry below reports it */ }
            Log.Error($"connections.json could not be read; moved to {broken}", error);
            try
            {
                return ConnectionStore.Open(path);
            }
            catch (Exception)
            {
                // Last resort: a store in the temp folder so the app still opens.
                var temp = Path.Combine(Path.GetTempPath(), "toolbox-connections.json");
                Prompts.Error("Connections can't be saved",
                              $"The store at {path} is unusable; working from a temporary copy until it's fixed.");
                return ConnectionStore.Open(temp);
            }
        }
    }

    public void Reload()
    {
        Connections = Store.List();
        Credentials = Store.ListCredentials();
        Providers = Store.ListProviders();
        Folders = Store.ListFolders();
        FolderCredentials = Store.ListFolderCredentials().ToDictionary(f => f.Folder, f => f.CredentialId);
        Changed?.Invoke();
    }

    // ----- Folders -----

    public string AddFolder(string path)
    {
        var created = Store.AddFolder(path);
        Reload();
        return created;
    }

    public void RenameFolder(string path, string newPath)
    {
        Store.RenameFolder(path, newPath);
        Reload();
        ProviderSync.Shared.ScheduleAutoRefresh();
    }

    public void DeleteFolder(string path)
    {
        Store.DeleteFolder(path);
        Reload();
    }

    /// <summary>The credential assigned directly to <paramref name="path"/>, if any.</summary>
    public Credential? FolderCredential(string path) =>
        FolderCredentials.TryGetValue(path, out var id) ? Credential(id) : null;

    /// <summary>What a connection in <paramref name="folder"/> inherits, and the folder it comes from.</summary>
    public (string Folder, Credential Credential)? InheritedCredential(string? folder)
    {
        var found = Store.InheritedCredential(folder);
        if (found is null) return null;
        var credential = Credential(found.CredentialId);
        return credential is null ? null : (found.Folder, credential);
    }

    public void SetFolderCredential(string path, string? credentialId)
    {
        Store.SetFolderCredential(path, credentialId);
        Reload();
    }

    public void MoveConnection(Connection connection, string? folder)
    {
        Store.MoveConnection(connection.Id, folder);
        Reload();
    }

    public void MoveCredential(Credential credential, string? folder)
    {
        Store.MoveCredential(credential.Id, folder);
        Reload();
    }

    public void MoveProvider(Provider provider, string? parent)
    {
        Store.MoveProvider(provider.Id, parent);
        Reload();
        ProviderSync.Shared.ScheduleAutoRefresh();
    }

    // ----- Connections -----

    public Connection? Connection(string id) => Connections.FirstOrDefault(c => c.Id == id);

    /// <summary>A connection by id or, failing that, by name (launch arguments accept either).</summary>
    public Connection? FindConnection(string idOrName) =>
        Connections.FirstOrDefault(c => c.Id == idOrName) ?? Connections.FirstOrDefault(c => c.Name == idOrName);

    /// <summary>
    /// Saves the record. A connection with its own stored password keeps it in
    /// the vault under the connection's id; a blank password keeps the one
    /// already stored.
    /// </summary>
    public async Task<Connection> SaveAsync(Connection connection, string? storedPassword = null)
    {
        var record = connection;
        var previous = record.Id.Length == 0 ? null : Connection(record.Id);
        var wroteSecret = false;
        if (record.Auth is AuthKind.StoredPassword)
        {
            var vault = await VaultManager.Shared.UnlockedVaultAsync();
            if (record.Id.Length == 0) record = record with { Id = CoreApi.NewId() };
            var existing = TryGetSecret(vault, record.Id)?.Password;
            var password = storedPassword.NonEmpty() ?? existing.NonEmpty()
                ?? throw ResolveException.MissingPassword(record.Name);
            vault.SetSecret(record.Id, new Secret(password, null, null));
            wroteSecret = true;
        }
        Connection stored;
        try
        {
            stored = Store.Upsert(record);
        }
        catch (Exception)
        {
            if (wroteSecret && previous is null) TryDeleteSecret(record.Id);
            throw;
        }
        // Switching away from a stored password leaves nothing behind.
        if (previous?.Auth is AuthKind.StoredPassword && !wroteSecret && VaultManager.Shared.Vault.IsUnlocked())
        {
            TryDeleteSecret(stored.Id);
        }
        Reload();
        return stored;
    }

    public void Delete(Connection connection)
    {
        Store.Delete(connection.Id);
        if (connection.Auth is AuthKind.StoredPassword && VaultManager.Shared.Vault.IsUnlocked())
        {
            TryDeleteSecret(connection.Id);
        }
        Reload();
    }

    // ----- Credentials -----

    public Credential? Credential(string id) => Credentials.FirstOrDefault(c => c.Id == id);

    /// <summary>
    /// Saves the record and, for vault credentials, the secret alongside it.
    /// Blank secret fields keep whatever was stored before.
    /// </summary>
    public async Task<Credential> SaveCredentialAsync(Credential credential, Secret? secret)
    {
        var record = credential;
        Vault? vault = null;
        Secret? merged = null;
        if (credential.Kind is CredentialKind.Vault)
        {
            var unlocked = await VaultManager.Shared.UnlockedVaultAsync();
            var none = new Secret(null, null, null);
            var existing = record.Id.Length == 0 ? none : TryGetSecret(unlocked, record.Id) ?? none;
            var incoming = secret ?? none;
            var result = new Secret(
                incoming.Password.NonEmpty() ?? existing.Password,
                incoming.PrivateKey.NonEmpty() ?? existing.PrivateKey,
                incoming.Passphrase.NonEmpty() ?? existing.Passphrase);
            if (result.Password.NonEmpty() is null && result.PrivateKey.NonEmpty() is null)
            {
                throw ResolveException.EmptyCredential(record.Name);
            }
            record = record with { HasPrivateKey = result.PrivateKey.NonEmpty() is not null };
            vault = unlocked;
            merged = result;
        }
        var stored = Store.UpsertCredential(record);
        if (vault is not null && merged is not null)
        {
            vault.SetSecret(stored.Id, merged);
        }
        Reload();
        return stored;
    }

    public void DeleteCredential(Credential credential)
    {
        Store.DeleteCredential(credential.Id);
        if (credential.Kind is CredentialKind.Vault && VaultManager.Shared.Vault.IsUnlocked())
        {
            TryDeleteSecret(credential.Id);
        }
        Reload();
    }

    // ----- Providers -----

    public Provider? Provider(string id) => Providers.FirstOrDefault(p => p.Id == id);

    public Provider SaveProvider(Provider provider)
    {
        var stored = Store.UpsertProvider(provider);
        Reload();
        ProviderSync.Shared.ScheduleAutoRefresh();
        return stored;
    }

    /// <summary>
    /// Saves a provider from its editor: accounts typed into the provider are
    /// written to the vault first, then the provider, then accounts it no
    /// longer uses are removed.
    /// </summary>
    public async Task<Provider> SaveProviderAsync(ProviderDraft draft)
    {
        var record = draft.ToProvider();
        foreach (var (credential, password) in draft.ManagedAccounts(record.Folder))
        {
            await SaveCredentialAsync(credential, new Secret(password, null, null));
        }
        var stored = SaveProvider(record);
        var inUse = ProviderRoles.All.Select(draft.CredentialId).Where(id => id is not null).ToHashSet();
        foreach (var credential in ManagedCredentials(stored.Id).Where(c => !inUse.Contains(c.Id)).ToList())
        {
            DeleteCredential(credential);
        }
        return stored;
    }

    /// <summary>Vault credentials a provider owns (typed into its editor).</summary>
    public IReadOnlyList<Credential> ManagedCredentials(string providerId)
    {
        var prefix = providerId + "#";
        return Credentials.Where(c => c.Id.StartsWith(prefix, StringComparison.Ordinal) && c.Source is null).ToList();
    }

    public void DeleteProvider(Provider provider)
    {
        var owned = ManagedCredentials(provider.Id);
        Store.DeleteProvider(provider.Id);
        foreach (var credential in owned)
        {
            try { DeleteCredential(credential); }
            catch (Exception e) { Log.Warn($"could not remove provider account {credential.Id}: {e.Message}"); }
        }
        Reload();
        ProviderSync.Shared.ScheduleAutoRefresh();
    }

    // ----- Vault helpers -----

    private static Secret? TryGetSecret(Vault vault, string id)
    {
        try { return vault.GetSecret(id); }
        catch (CoreException) { return null; }
    }

    private static void TryDeleteSecret(string id)
    {
        try { VaultManager.Shared.Vault.DeleteSecret(id); }
        catch (CoreException e) { Log.Warn($"could not remove the vault entry for {id}: {e.Message}"); }
    }
}
