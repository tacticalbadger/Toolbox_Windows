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

    /// <summary>
    /// A folder was renamed or moved with everything in it (from, to). Raised
    /// once the lists above are up to date.
    /// </summary>
    public event Action<string, string>? FolderMoved;

    /// <summary>False when <c>TOOLBOX_STORE_PATH</c> names another store (a test run).</summary>
    public bool UsesDefaultStore { get; } = AppPaths.UsesDefaultStore;

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

    /// <summary>Renames or moves a folder; returns the path it has now.</summary>
    public string RenameFolder(string path, string newPath)
    {
        var from = FolderPath.Normalize(path) ?? path;
        var renamed = Store.RenameFolder(path, newPath);
        Reload();
        if (renamed != from) FolderMoved?.Invoke(from, renamed);
        ProviderSync.Shared.ScheduleAutoRefresh();
        return renamed;
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
        var before = Store.GetProvider(provider.Id)?.Folder;
        Store.MoveProvider(provider.Id, parent);
        Reload();
        AnnounceFolder(provider.Id, before);
        ProviderSync.Shared.ScheduleAutoRefresh();
    }

    /// <summary>Raises <see cref="FolderMoved"/> when a provider's folder is no longer <paramref name="before"/>.</summary>
    private void AnnounceFolder(string providerId, string? before)
    {
        if (before is null || Provider(providerId)?.Folder is not { } now || now == before) return;
        FolderMoved?.Invoke(before, now);
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

    /// <summary>
    /// Saves the record. The provider's folder is named after it, so a new
    /// name or location moves the folder and what is inside.
    /// </summary>
    public Provider SaveProvider(Provider provider)
    {
        var before = provider.Id.Length == 0 ? null : Store.GetProvider(provider.Id)?.Folder;
        var stored = Store.UpsertProvider(provider);
        Reload();
        AnnounceFolder(stored.Id, before);
        ProviderSync.Shared.ScheduleAutoRefresh();
        return stored;
    }

    /// <summary>
    /// Saves a provider from its editor: accounts typed into the provider are
    /// written to the vault first, then the provider, then accounts it no
    /// longer uses are removed. A provider the store would refuse (its folder
    /// belongs to another one, say) is refused before any account is written.
    /// </summary>
    public async Task<Provider> SaveProviderAsync(ProviderDraft draft)
    {
        var record = Store.ValidateProvider(draft.ToProvider());
        // The accounts go where the provider is now; a new name or location
        // then moves them along with the rest of its folder.
        var folder = Store.GetProvider(record.Id)?.Folder ?? record.Folder;
        foreach (var (credential, password) in draft.ManagedAccounts(folder))
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
