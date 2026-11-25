using Toolbox.Model;
using Toolbox.Sidebar;
using Toolbox.Support;
using Toolbox.Workspaces;

namespace Toolbox.Tests;

/// <summary>What a workspace's tab is called when the user has not named it.</summary>
public class TabTitleTests
{
    [Fact]
    public void AnEmptyWorkspaceHasNoTitleOfItsOwn() => Assert.Null(Workspace.TitleFor([]));

    [Fact]
    public void OneSessionNamesTheTab() => Assert.Equal("web01", Workspace.TitleFor(["web01"]));

    [Fact]
    public void TwoSessionsAreBothNamed() => Assert.Equal("web01, db02", Workspace.TitleFor(["web01", "db02"]));

    [Fact]
    public void FurtherSessionsAreCounted()
    {
        Assert.Equal("web01, db02 + 1", Workspace.TitleFor(["web01", "db02", "app03"]));
        Assert.Equal("web01, db02 + 3", Workspace.TitleFor(["web01", "db02", "app03", "app04", "app05"]));
    }
}

/// <summary>Folder paths, and what follows a folder when it is renamed or moved.</summary>
public class FolderMoveTests
{
    [Theory]
    [InlineData(" ACME / Jump Boxes /", "ACME/Jump Boxes")]
    [InlineData("ACME", "ACME")]
    [InlineData("//ACME//AD", "ACME/AD")]
    public void PathsAreNormalizedTheWayTheStoreDoes(string path, string expected) =>
        Assert.Equal(expected, FolderPath.Normalize(path));

    [Theory]
    [InlineData("")]
    [InlineData(" / ")]
    public void APathOfNothingIsNoPath(string path) => Assert.Null(FolderPath.Normalize(path));

    [Fact]
    public void AFolderAndWhatIsInsideItMove()
    {
        Assert.Equal("Work/AD", FolderPath.Moved("ACME/AD", "ACME/AD", "Work/AD"));
        Assert.Equal("Work/AD/Servers/Web", FolderPath.Moved("ACME/AD/Servers/Web", "ACME/AD", "Work/AD"));
    }

    [Fact]
    public void ANeighbourWithALongerNameStaysWhereItIs()
    {
        Assert.Null(FolderPath.Moved("ACME/AD 2", "ACME/AD", "Work/AD"));
        Assert.Null(FolderPath.Moved("ACME", "ACME/AD", "Work/AD"));
    }

    [Fact]
    public void OnlyFolderRowsUnderTheMovedFolderGetNewIds()
    {
        string[] ids =
        [
            "group:library", "folder:ACME", "folder:ACME/AD", "folder:ACME/AD/Servers", "folder:ACME/AD 2",
            "prov:ad-fake", "conn:ACME/AD", "folder:ACME/AD/Servers",
        ];
        var moved = SidebarView.MovedIds(ids, "ACME/AD", "Work/Directory").Select(m => $"{m.Old} -> {m.New}").ToArray();
        string[] expected =
        [
            "folder:ACME/AD -> folder:Work/Directory",
            "folder:ACME/AD/Servers -> folder:Work/Directory/Servers",
        ];
        Assert.Equal(expected, moved);
    }
}

/// <summary>A provider's folder is its location plus its name.</summary>
public class ProviderDraftTests
{
    [Fact]
    public void TheFolderCarriesTheProvidersName()
    {
        var draft = new ProviderDraft { Name = " Logins ", Parent = "Work" };
        Assert.Null(draft.ValidationError);
        var provider = draft.ToProvider();
        Assert.Equal("Logins", provider.Name);
        Assert.Equal("Work/Logins", provider.Folder);
    }

    [Fact]
    public void AtTheTopLevelTheFolderIsTheName() =>
        Assert.Equal("Logins", new ProviderDraft { Name = "Logins" }.ToProvider().Folder);

    [Fact]
    public void ANameCannotHoldASlash()
    {
        var draft = new ProviderDraft { Name = "Work/Logins" };
        Assert.Contains("Location", draft.ValidationError);
    }
}
