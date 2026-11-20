using Toolbox.Sidebar;

namespace Toolbox.Tests;

/// <summary>What the credential picker's search field shows.</summary>
public class CredentialPickerTests
{
    private static Credential Make(string id, string name, string user, CredentialKind kind, string? folder = null) =>
        new(id, name, user, kind, folder, HasPrivateKey: false, Tags: [], Source: null);

    private static readonly Credential[] All =
    [
        Make("v1", "Domain admin", @"CORP\jdoe", new CredentialKind.Vault(), "ACME"),
        Make("o1", "db-prod-atl admin", "svc_db_admin", new CredentialKind.OnePassword("i1", "work", null), "ACME/1Password/work"),
        Make("o2", "web-prod-sea deploy", "svc_web_deploy", new CredentialKind.OnePassword("i2", "work", null), "ACME/1Password/work"),
        Make("o3", "app-stage-atl admin", "svc_app_admin", new CredentialKind.OnePassword("i3", "work", null), "ACME/1Password/lab"),
        Make("p1", "Folder user", "folderuser", new CredentialKind.Prompt(), "Jump Boxes"),
    ];

    private static string[] Ids(string query) => CredentialPicker.Matches(All, query).Select(c => c.Id).ToArray();

    [Fact]
    public void AnEmptySearchShowsEverything()
    {
        Assert.Equal(5, Ids("").Length);
        Assert.Equal(5, Ids("   ").Length);
    }

    [Fact]
    public void ASearchMatchesNameUsernameOrFolder()
    {
        Assert.Equal(["o1"], Ids("db-prod"));
        Assert.Equal(["o2"], Ids("svc_web"));
        Assert.Equal(["p1"], Ids("jump"));
    }

    [Fact]
    public void EveryWordHasToMatchSomewhere()
    {
        Assert.Equal(["o1", "o3"], Ids("atl admin"));
        Assert.Equal(["o3"], Ids("admin lab"));
        Assert.Empty(Ids("atl deploy"));
    }

    [Fact]
    public void CaseDoesNotMatter()
    {
        Assert.Equal(["v1"], Ids("CORP\\JDOE"));
        Assert.Equal(["v1"], Ids("domain ADMIN acme"));
    }

    [Fact]
    public void GroupsFollowWhereTheSecretLivesAndSkipEmptyOnes()
    {
        var groups = CredentialPicker.Groups(All);
        Assert.Equal(["Vault", "1Password", "Ask each time"], groups.Select(g => g.Title).ToArray());
        Assert.Equal(["o3", "o1", "o2"], groups[1].Items.Select(c => c.Id).ToArray());

        var searched = CredentialPicker.Groups(CredentialPicker.Matches(All, "prod"));
        Assert.Equal(["1Password"], searched.Select(g => g.Title).ToArray());
    }

    [Fact]
    public void LabelsReadAsSomethingWhateverIsFilledIn()
    {
        Assert.Equal(@"Domain admin (CORP\jdoe)", CredentialPicker.Label(All[0]));
        Assert.Equal("root", CredentialPicker.Label(Make("x", "root", "root", new CredentialKind.Vault())));
        Assert.Equal("Only a name", CredentialPicker.Label(Make("x", " Only a name ", "", new CredentialKind.Vault())));
        Assert.Equal("only-a-user", CredentialPicker.Label(Make("x", "", "only-a-user", new CredentialKind.Vault())));
        Assert.Equal("Untitled credential", CredentialPicker.Label(Make("x", " ", "", new CredentialKind.Vault())));
    }
}
