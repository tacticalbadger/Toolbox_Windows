namespace Toolbox.Model;

internal enum CredentialKindChoice { Vault, OnePassword, Prompt }

internal static class CredentialKindChoices
{
    public static string Label(this CredentialKindChoice choice) => choice switch
    {
        CredentialKindChoice.Vault => "Stored in the vault",
        CredentialKindChoice.OnePassword => "1Password item",
        _ => "Ask each time",
    };

    public static readonly IReadOnlyList<Choice<CredentialKindChoice>> All =
    [
        new(CredentialKindChoice.Vault, CredentialKindChoice.Vault.Label()),
        new(CredentialKindChoice.OnePassword, CredentialKindChoice.OnePassword.Label()),
        new(CredentialKindChoice.Prompt, CredentialKindChoice.Prompt.Label()),
    ];
}

/// <summary>Editable copy of a <see cref="Credential"/> for the form.</summary>
internal sealed class CredentialDraft : FormDraft
{
    private string name = "";
    private string username = "";
    private string folder = "";
    private CredentialKindChoice kind = CredentialKindChoice.Vault;
    private string password = "";
    private string privateKey = "";
    private string passphrase = "";
    private string itemId = "";
    private string account = "";
    private string opPath = "";

    public string Id { get; private set; } = "";
    public bool HasPrivateKey { get; private set; }
    public IReadOnlyList<string> Tags { get; private set; } = [];
    public string? Source { get; private set; }

    public CredentialDraft() { }

    public CredentialDraft(Credential credential)
    {
        Id = credential.Id;
        name = credential.Name;
        username = credential.Username;
        folder = credential.Folder ?? "";
        HasPrivateKey = credential.HasPrivateKey;
        Tags = credential.Tags;
        Source = credential.Source;
        switch (credential.Kind)
        {
            case CredentialKind.Vault:
                kind = CredentialKindChoice.Vault;
                break;
            case CredentialKind.OnePassword item:
                kind = CredentialKindChoice.OnePassword;
                itemId = item.ItemId;
                account = item.Account ?? "";
                opPath = item.OpPath ?? "";
                break;
            case CredentialKind.Prompt:
                kind = CredentialKindChoice.Prompt;
                break;
        }
    }

    public bool IsNew => Id.Length == 0;
    public string Name { get => name; set => SetField(ref name, value); }
    public string Username { get => username; set => SetField(ref username, value); }
    public string Folder { get => folder; set => SetField(ref folder, value); }
    public CredentialKindChoice Kind { get => kind; set => SetField(ref kind, value); }
    public string Password { get => password; set => SetField(ref password, value); }
    public string PrivateKey { get => privateKey; set => SetField(ref privateKey, value); }
    public string Passphrase { get => passphrase; set => SetField(ref passphrase, value); }
    public string ItemId { get => itemId; set => SetField(ref itemId, value); }
    public string Account { get => account; set => SetField(ref account, value); }
    public string OpPath { get => opPath; set => SetField(ref opPath, value); }

    public Choice<CredentialKindChoice>? SelectedKind
    {
        get => CredentialKindChoices.All.FirstOrDefault(c => c.Value == kind);
        set { if (value is not null) Kind = value.Value; }
    }

    public bool IsVault => kind == CredentialKindChoice.Vault;
    public bool IsOnePassword => kind == CredentialKindChoice.OnePassword;
    public bool IsPrompt => kind == CredentialKindChoice.Prompt;
    public bool IsGenerated => Source is not null;
    public string VaultNote => IsNew
        ? "Encrypted in the local vault. Opening the vault asks Windows to confirm it's you."
        : "Leave a field blank to keep what is stored.";

    public string? ValidationError
    {
        get
        {
            if (string.IsNullOrWhiteSpace(name)) return "Give the credential a name.";
            return kind switch
            {
                CredentialKindChoice.OnePassword when string.IsNullOrWhiteSpace(itemId) =>
                    "Enter the 1Password item id (op item list shows it).",
                CredentialKindChoice.Vault when IsNew && password.Length == 0 && string.IsNullOrWhiteSpace(privateKey) =>
                    "Enter a password or a private key.",
                _ => null,
            };
        }
    }

    public Credential ToCredential()
    {
        CredentialKind stored = kind switch
        {
            CredentialKindChoice.Vault => new CredentialKind.Vault(),
            CredentialKindChoice.OnePassword =>
                new CredentialKind.OnePassword(itemId.Trim(), account.BlankToNull(), opPath.BlankToNull()),
            _ => new CredentialKind.Prompt(),
        };
        return new Credential(
            Id: Id,
            Name: name.Trim(),
            Username: username.Trim(),
            Kind: stored,
            Folder: folder.BlankToNull(),
            HasPrivateKey: HasPrivateKey,
            Tags: Tags.ToArray(),
            Source: Source);
    }

    /// <summary>Secret fields entered this time. Only meaningful for vault credentials.</summary>
    public Secret? ToSecret() => kind == CredentialKindChoice.Vault
        ? new Secret(password.NonEmpty(), privateKey.BlankToNull(), passphrase.NonEmpty())
        : null;
}
