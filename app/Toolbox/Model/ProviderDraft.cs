namespace Toolbox.Model;

internal enum ProviderKindChoice { OnePassword, ActiveDirectory }

internal static class ProviderKindChoices
{
    public static string Label(this ProviderKindChoice choice) =>
        choice == ProviderKindChoice.OnePassword ? "1Password" : "Active Directory";

    public static readonly IReadOnlyList<Choice<ProviderKindChoice>> All =
    [
        new(ProviderKindChoice.OnePassword, ProviderKindChoice.OnePassword.Label()),
        new(ProviderKindChoice.ActiveDirectory, ProviderKindChoice.ActiveDirectory.Label()),
    ];
}

/// <summary>One of the Active Directory provider's accounts.</summary>
internal enum ProviderRole { Bind, Admin, User }

internal static class ProviderRoles
{
    public static readonly IReadOnlyList<ProviderRole> All = [ProviderRole.Bind, ProviderRole.Admin, ProviderRole.User];

    public static string Title(this ProviderRole role) => role switch
    {
        ProviderRole.Bind => "Bind as",
        ProviderRole.Admin => "Domain controllers use",
        _ => "Other computers use",
    };

    public static string AccountLabel(this ProviderRole role) => role switch
    {
        ProviderRole.Bind => "bind account",
        ProviderRole.Admin => "domain controller account",
        _ => "computer account",
    };

    private static string Suffix(this ProviderRole role) => role switch
    {
        ProviderRole.Bind => "bind",
        ProviderRole.Admin => "admin",
        _ => "user",
    };

    /// <summary>
    /// Id of the vault credential the provider owns for this role when the
    /// username and password are typed into the provider itself.
    /// </summary>
    public static string ManagedId(this ProviderRole role, string providerId) => $"{providerId}#{role.Suffix()}";
}

internal enum RoleMode { None, Existing, Specify }

/// <summary>
/// How a role gets its credential: none, an existing credential, or a
/// username and password entered here (kept in the vault, owned by the
/// provider).
/// </summary>
internal sealed class RoleCredentialDraft : FormDraft
{
    private RoleMode mode;
    private string? credentialId;
    private string username = "";
    private string password = "";

    public RoleCredentialDraft(ProviderRole role, RoleMode mode = RoleMode.None)
    {
        Role = role;
        this.mode = mode;
    }

    public ProviderRole Role { get; }
    public string Title => Role.Title();

    public RoleMode Mode { get => mode; set => SetField(ref mode, value); }
    public string? CredentialId { get => credentialId; set => SetField(ref credentialId, value); }
    public string Username { get => username; set => SetField(ref username, value); }

    /// <summary>Blank keeps the stored password when editing.</summary>
    public string Password { get => password; set => SetField(ref password, value); }

    /// <summary>A provider-owned credential already exists for this role.</summary>
    public bool HasStored { get; init; }

    private static readonly IReadOnlyList<Choice<RoleMode>> BindChoices =
    [
        new(RoleMode.Existing, "Use an existing credential"),
        new(RoleMode.Specify, "Specify username and password"),
    ];

    private static readonly IReadOnlyList<Choice<RoleMode>> OptionalChoices =
    [
        new(RoleMode.None, "None (ask when connecting)"),
        new(RoleMode.Existing, "Use an existing credential"),
        new(RoleMode.Specify, "Specify username and password"),
    ];

    /// <summary>Binding needs an account; the other roles may go without one.</summary>
    public IReadOnlyList<Choice<RoleMode>> ModeChoices => Role == ProviderRole.Bind ? BindChoices : OptionalChoices;

    public Choice<RoleMode>? SelectedMode
    {
        get => ModeChoices.FirstOrDefault(c => c.Value == mode);
        set { if (value is not null) Mode = value.Value; }
    }

    public bool IsExisting => mode == RoleMode.Existing;
    public bool IsSpecify => mode == RoleMode.Specify;
    public string PasswordNote => HasStored
        ? "Leave the password blank to keep the stored one. Kept encrypted in the vault as a credential inside this provider's folder."
        : "Kept encrypted in the vault as a credential inside this provider's folder.";
}

internal sealed class ProviderDraft : FormDraft
{
    private string name = "";
    private string parent = "";
    private ProviderKindChoice kind = ProviderKindChoice.OnePassword;
    private int autoRefreshMinutes = 60;
    private string opPath = "";
    private string account = "";
    private string vaults = "";
    private string server = "";
    private string searchBase = "";
    private bool allowPlainLdap = true;
    private bool acceptInvalidCerts;

    public string Id { get; set; } = "";

    /// <summary>Name of the provider's own folder node; follows <see cref="Name"/> for new ones.</summary>
    public string Leaf { get; private set; } = "";
    public ulong? LastSyncEpoch { get; private set; }
    public string? LastStatus { get; private set; }

    public RoleCredentialDraft Bind { get; private set; } = new(ProviderRole.Bind, RoleMode.Existing);
    public RoleCredentialDraft Admin { get; private set; } = new(ProviderRole.Admin);
    public RoleCredentialDraft User { get; private set; } = new(ProviderRole.User);

    public ProviderDraft() { }

    public ProviderDraft(Provider provider, AppModel model)
    {
        Id = provider.Id;
        name = provider.Name;
        parent = FolderPath.Parent(provider.Folder) ?? "";
        Leaf = FolderPath.Leaf(provider.Folder);
        autoRefreshMinutes = (int)provider.AutoRefreshMinutes;
        LastSyncEpoch = provider.LastSyncEpoch;
        LastStatus = provider.LastStatus;
        switch (provider.Kind)
        {
            case ProviderKind.OnePassword op:
                kind = ProviderKindChoice.OnePassword;
                opPath = op.OpPath ?? "";
                account = op.Account ?? "";
                vaults = string.Join(", ", op.Vaults);
                break;
            case ProviderKind.ActiveDirectory ad:
                kind = ProviderKindChoice.ActiveDirectory;
                server = ad.Server;
                searchBase = ad.SearchBase;
                Bind = RoleFrom(ProviderRole.Bind, ad.BindCredentialId, provider.Id, model);
                Admin = RoleFrom(ProviderRole.Admin, ad.AdminCredentialId, provider.Id, model);
                User = RoleFrom(ProviderRole.User, ad.UserCredentialId, provider.Id, model);
                allowPlainLdap = ad.AllowPlainLdap;
                acceptInvalidCerts = ad.AcceptInvalidCerts;
                break;
        }
    }

    private static RoleCredentialDraft RoleFrom(ProviderRole role, string? id, string providerId, AppModel model)
    {
        if (id is null) return new RoleCredentialDraft(role, role == ProviderRole.Bind ? RoleMode.Existing : RoleMode.None);
        if (id == role.ManagedId(providerId))
        {
            var stored = model.Credential(id);
            return new RoleCredentialDraft(role, RoleMode.Specify)
            {
                Username = stored?.Username ?? "",
                HasStored = stored is not null,
            };
        }
        return new RoleCredentialDraft(role, RoleMode.Existing) { CredentialId = id };
    }

    public bool IsNew => Id.Length == 0;
    public string Name { get => name; set => SetField(ref name, value); }

    /// <summary>Folder the provider sits in; its own node is named after it.</summary>
    public string Parent { get => parent; set => SetField(ref parent, value); }
    public ProviderKindChoice Kind { get => kind; set => SetField(ref kind, value); }
    public int AutoRefreshMinutes { get => autoRefreshMinutes; set => SetField(ref autoRefreshMinutes, Math.Clamp(value, 0, 1440)); }
    public string OpPath { get => opPath; set => SetField(ref opPath, value); }
    public string Account { get => account; set => SetField(ref account, value); }
    public string Vaults { get => vaults; set => SetField(ref vaults, value); }
    public string Server { get => server; set => SetField(ref server, value); }
    public string SearchBase { get => searchBase; set => SetField(ref searchBase, value); }
    public bool AllowPlainLdap { get => allowPlainLdap; set => SetField(ref allowPlainLdap, value); }
    public bool AcceptInvalidCerts { get => acceptInvalidCerts; set => SetField(ref acceptInvalidCerts, value); }

    public Choice<ProviderKindChoice>? SelectedKind
    {
        get => ProviderKindChoices.All.FirstOrDefault(c => c.Value == kind);
        set { if (value is not null) Kind = value.Value; }
    }

    public bool IsOnePassword => kind == ProviderKindChoice.OnePassword;
    public bool IsActiveDirectory => kind == ProviderKindChoice.ActiveDirectory;
    public string NameHint => kind.Label();
    public string RefreshLabel => autoRefreshMinutes == 0 ? "Refresh manually" : $"Refresh every {autoRefreshMinutes} min";
    public IReadOnlyList<RoleCredentialDraft> Roles => [Bind, Admin, User];

    public RoleCredentialDraft Role(ProviderRole role) => role switch
    {
        ProviderRole.Bind => Bind,
        ProviderRole.Admin => Admin,
        _ => User,
    };

    public string? ValidationError
    {
        get
        {
            if (string.IsNullOrWhiteSpace(name)) return "Give the provider a name.";
            if (kind != ProviderKindChoice.ActiveDirectory) return null;
            if (string.IsNullOrWhiteSpace(server)) return "Enter the domain controller to query.";
            if (string.IsNullOrWhiteSpace(searchBase)) return "Enter the search base, e.g. DC=corp,DC=example.";
            if (Bind.Mode == RoleMode.None) return "Choose how to bind (your regular domain account).";
            foreach (var role in ProviderRoles.All)
            {
                var r = Role(role);
                switch (r.Mode)
                {
                    case RoleMode.Existing when r.CredentialId is null:
                        return $"{role.Title()}: choose a credential.";
                    case RoleMode.Specify when string.IsNullOrWhiteSpace(r.Username):
                        return $"{role.Title()}: enter a username.";
                    case RoleMode.Specify when r.Password.Length == 0 && !r.HasStored:
                        return $"{role.Title()}: enter a password.";
                }
            }
            return null;
        }
    }

    /// <summary>The credential id a role resolves to once saved.</summary>
    public string? CredentialId(ProviderRole role)
    {
        var r = Role(role);
        return r.Mode switch
        {
            RoleMode.None => null,
            RoleMode.Existing => r.CredentialId,
            _ => role.ManagedId(Id),
        };
    }

    /// <summary>Provider-owned accounts to write to the vault before the provider.</summary>
    public IReadOnlyList<(Credential Credential, string? Password)> ManagedAccounts(string folder)
    {
        if (kind != ProviderKindChoice.ActiveDirectory) return [];
        var label = name.Trim();
        var accounts = new List<(Credential, string?)>();
        foreach (var role in ProviderRoles.All)
        {
            var r = Role(role);
            if (r.Mode != RoleMode.Specify) continue;
            var record = new Credential(
                Id: role.ManagedId(Id),
                Name: $"{label} {role.AccountLabel()}",
                Username: r.Username.Trim(),
                Kind: new CredentialKind.Vault(),
                Folder: folder,
                HasPrivateKey: false,
                Tags: ["provider-account"],
                Source: null);
            accounts.Add((record, r.Password.Length == 0 ? null : r.Password));
        }
        return accounts;
    }

    public Provider ToProvider()
    {
        ProviderKind stored;
        if (kind == ProviderKindChoice.OnePassword)
        {
            var list = vaults.Split(',').Select(v => v.Trim()).Where(v => v.Length > 0).ToArray();
            stored = new ProviderKind.OnePassword(opPath.BlankToNull(), account.BlankToNull(), list);
        }
        else
        {
            stored = new ProviderKind.ActiveDirectory(
                Server: server.Trim(),
                SearchBase: searchBase.Trim(),
                BindCredentialId: CredentialId(ProviderRole.Bind),
                AdminCredentialId: CredentialId(ProviderRole.Admin),
                UserCredentialId: CredentialId(ProviderRole.User),
                AllowPlainLdap: allowPlainLdap,
                AcceptInvalidCerts: acceptInvalidCerts);
        }
        var trimmedName = name.Trim();
        var node = string.IsNullOrWhiteSpace(Leaf) ? trimmedName : Leaf.Trim();
        var location = parent.Trim();
        return new Provider(
            Id: Id,
            Name: trimmedName,
            Kind: stored,
            Folder: location.Length == 0 ? node : $"{location}/{node}",
            AutoRefreshMinutes: (uint)Math.Max(autoRefreshMinutes, 0),
            LastSyncEpoch: LastSyncEpoch,
            LastStatus: LastStatus);
    }
}
