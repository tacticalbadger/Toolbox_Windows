namespace Toolbox.Model;

internal sealed class ResolveException : Exception
{
    private ResolveException(string message) : base(message) { }

    public static ResolveException MissingCredential(string id) =>
        new($"The credential this connection uses ({id}) no longer exists.");

    public static ResolveException EmptyCredential(string name) =>
        new($"Credential “{name}” has neither a password nor a private key.");

    public static ResolveException MissingPassword(string name) =>
        new($"“{name}” has no stored password. Edit the connection and enter one.");

    public static ResolveException NoFolderCredential(string name) =>
        new($"“{name}” uses its folder's credential, but no folder above it has one. " +
            "Right-click a folder and choose Set Credential….");
}

/// <summary>What a credential resolves to at the moment it is used.</summary>
internal sealed record SecretBundle(string? Username, string? Password, string? PrivateKey, string? Passphrase);

internal sealed record ResolvedSsh(string Username, SshAuth Auth);

/// <summary>
/// A username and password. <see cref="Username"/> and <see cref="Domain"/> are
/// the two halves of a <c>DOMAIN\user</c> or <c>user@domain</c> name;
/// <see cref="FullUsername"/> is the name exactly as it was written.
/// </summary>
internal sealed record ResolvedPassword(string Username, string Password, string? Domain, string FullUsername);

/// <summary>
/// Turns stored references into usable secrets at the moment they're needed:
/// vault entries after unlocking, 1Password items through <c>op</c>, prompts
/// otherwise. Nothing resolved here is written back anywhere. A null result
/// means the user cancelled a prompt.
/// </summary>
internal static class CredentialResolver
{
    /// <summary>SSH credentials for a connection.</summary>
    public static async Task<ResolvedSsh?> SshAuthAsync(Connection connection)
    {
        switch (connection.Auth)
        {
            case AuthKind.Password:
            {
                var password = AskPassword(connection);
                return password is null ? null : new ResolvedSsh(connection.Username, new SshAuth.Password(password));
            }
            case AuthKind.KeyFile key:
                return new ResolvedSsh(connection.Username, new SshAuth.KeyFile(key.Path, null));
            case AuthKind.Agent agent:
                return new ResolvedSsh(connection.Username, new SshAuth.Agent(agent.SocketPath));
            case AuthKind.Credential reference:
            {
                var credential = AppModel.Shared.Credential(reference.CredentialId)
                    ?? throw ResolveException.MissingCredential(reference.CredentialId);
                return await SshWithAsync(credential, connection);
            }
            case AuthKind.FolderCredential:
                return await SshWithAsync(FolderCredential(connection), connection);
            case AuthKind.StoredPassword:
            {
                var password = await StoredPasswordAsync(connection);
                return new ResolvedSsh(connection.Username, new SshAuth.Password(password));
            }
            default:
                throw new InvalidOperationException($"unknown authentication method on {connection.Name}");
        }
    }

    private static async Task<ResolvedSsh?> SshWithAsync(Credential credential, Connection connection)
    {
        var secret = await SecretAsync(credential, $"connect to {connection.Name}");
        if (secret is null) return null;
        var username = connection.Username.Length == 0 ? secret.Username ?? credential.Username : connection.Username;
        if (!string.IsNullOrWhiteSpace(secret.PrivateKey))
        {
            return new ResolvedSsh(username, new SshAuth.KeyData(secret.PrivateKey, secret.Passphrase));
        }
        if (secret.Password is not null)
        {
            return new ResolvedSsh(username, new SshAuth.Password(secret.Password));
        }
        throw ResolveException.EmptyCredential(credential.Name);
    }

    /// <summary>The credential the nearest folder above the connection assigns.</summary>
    private static Credential FolderCredential(Connection connection) =>
        AppModel.Shared.InheritedCredential(connection.Folder)?.Credential
        ?? throw ResolveException.NoFolderCredential(connection.Name);

    /// <summary>The password kept in the vault for a "Specify username and password" connection.</summary>
    private static async Task<string> StoredPasswordAsync(Connection connection)
    {
        var vault = await VaultManager.Shared.UnlockedVaultAsync();
        string? password = null;
        try { password = vault.GetSecret(connection.Id).Password; }
        catch (CoreException) { /* reported as missing below */ }
        return password.NonEmpty() ?? throw ResolveException.MissingPassword(connection.Name);
    }

    /// <summary>Username and password for RDP and VNC.</summary>
    public static async Task<ResolvedPassword?> PasswordAuthAsync(Connection connection)
    {
        var raw = await UsernameAndPasswordAsync(connection);
        return raw is null ? null : Split(raw.Value.Username, raw.Value.Password);
    }

    /// <summary>
    /// Username and password for FTP: the username is used exactly as written,
    /// without splitting off a Windows domain.
    /// </summary>
    public static Task<(string Username, string Password)?> FtpAuthAsync(Connection connection) =>
        UsernameAndPasswordAsync(connection);

    /// <summary>The username (as written) and password a connection resolves to.</summary>
    private static async Task<(string Username, string Password)?> UsernameAndPasswordAsync(Connection connection)
    {
        switch (connection.Auth)
        {
            case AuthKind.Password:
            {
                var password = AskPassword(connection);
                return password is null ? null : (connection.Username, password);
            }
            case AuthKind.Credential reference:
            {
                var credential = AppModel.Shared.Credential(reference.CredentialId)
                    ?? throw ResolveException.MissingCredential(reference.CredentialId);
                return await PasswordWithAsync(credential, connection);
            }
            case AuthKind.FolderCredential:
                return await PasswordWithAsync(FolderCredential(connection), connection);
            case AuthKind.StoredPassword:
                return (connection.Username, await StoredPasswordAsync(connection));
            default:
                throw ResolveException.EmptyCredential(
                    $"{connection.Name} (SSH keys don't apply to {connection.Kind.Label()})");
        }
    }

    private static async Task<(string Username, string Password)?> PasswordWithAsync(Credential credential, Connection connection)
    {
        var secret = await SecretAsync(credential, $"connect to {connection.Name}");
        if (secret is null) return null;
        if (secret.Password is null) throw ResolveException.EmptyCredential(credential.Name);
        var username = connection.Username.Length == 0 ? secret.Username ?? credential.Username : connection.Username;
        return (username, secret.Password);
    }

    /// <summary>Splits a Windows domain off a <c>DOMAIN\user</c> or <c>user@domain</c> username.</summary>
    internal static ResolvedPassword Split(string username, string password)
    {
        var backslash = username.IndexOf('\\');
        if (backslash >= 0)
        {
            return new ResolvedPassword(username[(backslash + 1)..], password, username[..backslash], username);
        }
        var at = username.IndexOf('@');
        if (at >= 0)
        {
            return new ResolvedPassword(username[..at], password, username[(at + 1)..], username);
        }
        return new ResolvedPassword(username, password, null, username);
    }

    /// <summary>Username and password for binding to a directory.</summary>
    public static async Task<BindCredentials?> BindAsync(Credential credential)
    {
        var secret = await SecretAsync(credential, "query Active Directory");
        if (secret is null) return null;
        if (secret.Password is null) throw ResolveException.EmptyCredential(credential.Name);
        return new BindCredentials(secret.Username ?? credential.Username, secret.Password);
    }

    public static async Task<SecretBundle?> SecretAsync(Credential credential, string purpose)
    {
        switch (credential.Kind)
        {
            case CredentialKind.Vault:
            {
                var vault = await VaultManager.Shared.UnlockedVaultAsync();
                var stored = vault.GetSecret(credential.Id);
                return new SecretBundle(credential.Username, stored.Password, stored.PrivateKey, stored.Passphrase);
            }
            case CredentialKind.OnePassword item:
            {
                var resolved = await CoreApi.ResolveOnePasswordItem(item.OpPath, item.Account, item.ItemId);
                return new SecretBundle(resolved.Username ?? credential.Username, resolved.Password, resolved.PrivateKey, null);
            }
            default:
            {
                var who = credential.Username.Length == 0 ? credential.Name : credential.Username;
                var password = Prompts.AskSecret($"Password for {who}", $"Needed to {purpose}.");
                return password is null ? null : new SecretBundle(credential.Username, password, null, null);
            }
        }
    }

    private static string? AskPassword(Connection connection)
    {
        var who = connection.Username.Length == 0 ? connection.Host : $"{connection.Username}@{connection.Host}";
        return Prompts.AskSecret($"Password for {who}", "This connection asks every time.");
    }
}
