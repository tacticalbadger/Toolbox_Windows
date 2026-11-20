namespace Toolbox.Model;

internal enum AuthMethod { Credential, Stored, Folder, Agent, KeyFile, Password }

internal static class AuthMethods
{
    public static string Label(this AuthMethod method) => method switch
    {
        AuthMethod.Credential => "Use an existing credential",
        AuthMethod.Stored => "Specify username and password",
        AuthMethod.Folder => "Use the folder's credential",
        AuthMethod.Agent => "Use the SSH agent",
        AuthMethod.KeyFile => "Use a private key file",
        _ => "Ask for a password every time",
    };

    /// <summary>Keys and the agent only make sense over SSH.</summary>
    public static IReadOnlyList<AuthMethod> Available(ConnectionKind kind) => kind.UsesSsh()
        ? [AuthMethod.Credential, AuthMethod.Stored, AuthMethod.Folder, AuthMethod.Agent, AuthMethod.KeyFile, AuthMethod.Password]
        : [AuthMethod.Credential, AuthMethod.Stored, AuthMethod.Folder, AuthMethod.Password];

    /// <summary>Credential-based methods bring their own username; the rest need one.</summary>
    public static bool NeedsUsername(this AuthMethod method) =>
        method is not (AuthMethod.Credential or AuthMethod.Folder);
}

/// <summary>The Type control: File Transfer covers SFTP, FTP and FTPS.</summary>
internal enum ConnectionFamily { Ssh, Rdp, Vnc, Files }

internal static class ConnectionFamilies
{
    public static string Label(this ConnectionFamily family) => family switch
    {
        ConnectionFamily.Ssh => "SSH",
        ConnectionFamily.Rdp => "RDP",
        ConnectionFamily.Vnc => "VNC",
        _ => "File Transfer",
    };

    public static ConnectionFamily Of(ConnectionKind kind) => kind switch
    {
        ConnectionKind.Ssh => ConnectionFamily.Ssh,
        ConnectionKind.Rdp => ConnectionFamily.Rdp,
        ConnectionKind.Vnc => ConnectionFamily.Vnc,
        _ => ConnectionFamily.Files,
    };

    /// <summary>The kind chosen when switching to this family.</summary>
    public static ConnectionKind DefaultKind(this ConnectionFamily family) => family switch
    {
        ConnectionFamily.Ssh => ConnectionKind.Ssh,
        ConnectionFamily.Rdp => ConnectionKind.Rdp,
        ConnectionFamily.Vnc => ConnectionKind.Vnc,
        _ => ConnectionKind.Sftp,
    };
}

/// <summary>Editable copy of a <see cref="Connection"/> for the form.</summary>
internal sealed class ConnectionDraft : FormDraft
{
    /// <summary>The named pipe 1Password's SSH agent serves; the same one Windows OpenSSH uses.</summary>
    public const string OpenSshAgentPipe = @"\\.\pipe\openssh-ssh-agent";

    private string name = "";
    private ConnectionKind kind = ConnectionKind.Ssh;
    private string host = "";
    private string port = "22";
    private string username = Environment.UserName;
    private string folder = "";
    private AuthMethod authMethod = AuthMethod.Agent;
    private string? credentialId;
    private string password = "";
    private string keyPath = "";
    private string agentSocket = "";

    public string Id { get; private set; } = "";
    public IReadOnlyList<string> Tags { get; private set; } = [];
    public string? Source { get; private set; }

    public ConnectionDraft() { }

    public ConnectionDraft(Connection connection)
    {
        Id = connection.Id;
        name = connection.Name;
        kind = connection.Kind;
        host = connection.Host;
        port = connection.Port.ToString();
        username = connection.Username;
        folder = connection.Folder ?? "";
        Tags = connection.Tags;
        Source = connection.Source;
        switch (connection.Auth)
        {
            case AuthKind.Password:
                authMethod = AuthMethod.Password;
                break;
            case AuthKind.KeyFile key:
                authMethod = AuthMethod.KeyFile;
                keyPath = key.Path;
                break;
            case AuthKind.Agent agent:
                authMethod = AuthMethod.Agent;
                agentSocket = agent.SocketPath ?? "";
                break;
            case AuthKind.Credential credential:
                authMethod = AuthMethod.Credential;
                credentialId = credential.CredentialId;
                break;
            case AuthKind.StoredPassword:
                authMethod = AuthMethod.Stored;
                break;
            case AuthKind.FolderCredential:
                authMethod = AuthMethod.Folder;
                break;
        }
    }

    public bool IsNew => Id.Length == 0;
    public string Name { get => name; set => SetField(ref name, value); }
    public string Host { get => host; set => SetField(ref host, value); }
    public string Port { get => port; set => SetField(ref port, value); }
    public string Username { get => username; set => SetField(ref username, value); }
    public string Folder { get => folder; set => SetField(ref folder, value); }
    public string? CredentialId { get => credentialId; set => SetField(ref credentialId, value); }

    /// <summary>Password typed for "Specify username and password"; blank keeps the stored one.</summary>
    public string Password { get => password; set => SetField(ref password, value); }
    public string KeyPath { get => keyPath; set => SetField(ref keyPath, value); }
    public string AgentSocket { get => agentSocket; set => SetField(ref agentSocket, value); }

    public ConnectionKind Kind
    {
        get => kind;
        set
        {
            var old = kind;
            if (old == value) return;
            kind = value;
            // Follow the protocol's port unless the user picked another one.
            if (port.Trim() == old.DefaultPort().ToString()) port = value.DefaultPort().ToString();
            if (!AuthMethods.Available(value).Contains(authMethod)) authMethod = AuthMethod.Credential;
            ChangedAll();
        }
    }

    public ConnectionFamily Family
    {
        get => ConnectionFamilies.Of(kind);
        set
        {
            if (ConnectionFamilies.Of(kind) != value) Kind = value.DefaultKind();
        }
    }

    public AuthMethod AuthMethod { get => authMethod; set => SetField(ref authMethod, value); }

    // ----- What the form shows -----

    public bool IsFileTransfer => kind.IsFileTransfer();
    public bool IsRdp => kind == ConnectionKind.Rdp;
    public bool IsPlainFtp => kind == ConnectionKind.Ftp;
    public bool NeedsUsername => authMethod.NeedsUsername();
    public bool UsesCredential => authMethod == AuthMethod.Credential;
    public bool UsesStored => authMethod == AuthMethod.Stored;
    public bool UsesFolder => authMethod == AuthMethod.Folder;
    public bool UsesPrompt => authMethod == AuthMethod.Password;
    public bool UsesKeyFile => authMethod == AuthMethod.KeyFile;
    public bool UsesAgent => authMethod == AuthMethod.Agent;
    public bool IsGenerated => Source is not null;
    public string UsernameHint => kind == ConnectionKind.Rdp ? @"DOMAIN\user or user@domain" : "user";
    public string StoredPasswordNote => IsNew
        ? "Kept encrypted in the vault, just for this connection."
        : "Leave blank to keep the stored password.";

    // The same list instances every time, so a bound drop-down is not rebuilt on each keystroke.
    private static readonly IReadOnlyList<Choice<AuthMethod>> SshChoices =
        AuthMethods.Available(ConnectionKind.Ssh).Select(m => new Choice<AuthMethod>(m, m.Label())).ToList();
    private static readonly IReadOnlyList<Choice<AuthMethod>> PasswordChoices =
        AuthMethods.Available(ConnectionKind.Rdp).Select(m => new Choice<AuthMethod>(m, m.Label())).ToList();

    public IReadOnlyList<Choice<AuthMethod>> AuthMethodChoices => kind.UsesSsh() ? SshChoices : PasswordChoices;

    public Choice<AuthMethod>? SelectedAuthMethod
    {
        get => AuthMethodChoices.FirstOrDefault(c => c.Value == authMethod);
        set { if (value is not null) AuthMethod = value.Value; }
    }

    public int? PortNumber => int.TryParse(port.Trim(), out var value) ? value : null;

    public string? ValidationError
    {
        get
        {
            if (string.IsNullOrWhiteSpace(name)) return "Give the connection a name.";
            if (string.IsNullOrWhiteSpace(host)) return "Host is required.";
            if (PortNumber is not (>= 1 and <= 65535)) return "Port must be between 1 and 65535.";
            if (authMethod.NeedsUsername() && string.IsNullOrWhiteSpace(username)) return "Username is required.";
            return authMethod switch
            {
                AuthMethod.Credential when credentialId is null => "Choose a credential.",
                AuthMethod.Stored when IsNew && password.Length == 0 => "Enter a password.",
                AuthMethod.KeyFile when string.IsNullOrWhiteSpace(keyPath) => "Choose a private key file.",
                _ => null,
            };
        }
    }

    public Connection ToConnection()
    {
        AuthKind auth = authMethod switch
        {
            AuthMethod.Password => new AuthKind.Password(),
            AuthMethod.KeyFile => new AuthKind.KeyFile(keyPath.Trim()),
            AuthMethod.Agent => new AuthKind.Agent(agentSocket.BlankToNull()),
            AuthMethod.Credential => new AuthKind.Credential(credentialId ?? ""),
            AuthMethod.Stored => new AuthKind.StoredPassword(),
            _ => new AuthKind.FolderCredential(),
        };
        return new Connection(
            Id: Id,
            Name: name.Trim(),
            Kind: kind,
            Host: host.Trim(),
            Port: (ushort)Math.Clamp(PortNumber ?? kind.DefaultPort(), 1, 65535),
            // Credential-based methods take the credential's username.
            Username: authMethod.NeedsUsername() ? username.Trim() : "",
            Auth: auth,
            Folder: folder.BlankToNull(),
            Tags: Tags.ToArray(),
            Source: Source);
    }

    /// <summary>The password to store for "Specify username and password", if one was typed.</summary>
    public string? StoredPassword() => authMethod == AuthMethod.Stored ? password.NonEmpty() : null;
}
