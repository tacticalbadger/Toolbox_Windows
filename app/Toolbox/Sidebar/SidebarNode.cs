using System.Collections.ObjectModel;
using Toolbox.Model;

namespace Toolbox.Sidebar;

internal enum SidebarNodeKind { Group, Folder, Provider, Connection, Credential }

/// <summary>What the sidebar can ask the window to do. Folder arguments are paths like <c>ACME/Jump Boxes</c>; null means the top level.</summary>
internal interface ISidebarHost
{
    void OpenConnection(Connection connection);
    void BrowseFiles(Connection connection);
    void EditConnection(Connection? connection, string? folder);
    void DeleteConnection(Connection connection);
    void EditCredential(Credential? credential, string? folder);
    void DeleteCredential(Credential credential);
    void EditProvider(Provider? provider, string? folder);
    void DeleteProvider(Provider provider);
    void SyncProvider(Provider provider);
    void NewFolder(string? parent);
    void RenameFolder(string path);
    void DeleteFolder(string path);
    void SetFolderCredential(string path);
}

/// <summary>
/// One row of the tree. Nodes are kept by id across rebuilds, so expansion
/// and selection stay with the row they belong to.
/// </summary>
internal sealed class SidebarNode : Observable
{
    private readonly Action<SidebarNode, bool> expansionChanged;
    private bool isExpanded;
    private bool isSelected;
    private bool isDropTarget;
    private bool isSyncing;
    private string? badgeTip;

    public SidebarNode(string id, Action<SidebarNode, bool> expansionChanged)
    {
        Id = id;
        this.expansionChanged = expansionChanged;
    }

    public string Id { get; }
    public SidebarNodeKind Kind { get; private set; }
    public string GroupTitle { get; private set; } = "";
    public string Path { get; private set; } = "";
    public Provider? Provider { get; private set; }
    public Connection? Connection { get; private set; }
    public Credential? Credential { get; private set; }

    public ObservableCollection<SidebarNode> Children { get; } = [];
    public SidebarNode? Parent { get; set; }

    public void SetGroup(string title) => Assign(SidebarNodeKind.Group, title: title);
    public void SetFolder(string path) => Assign(SidebarNodeKind.Folder, path: path);
    public void SetProvider(Provider provider) => Assign(SidebarNodeKind.Provider, provider: provider);
    public void SetConnection(Connection connection) => Assign(SidebarNodeKind.Connection, connection: connection);
    public void SetCredential(Credential credential) => Assign(SidebarNodeKind.Credential, credential: credential);

    private void Assign(SidebarNodeKind kind, string title = "", string path = "", Provider? provider = null,
                        Connection? connection = null, Credential? credential = null)
    {
        var same = Kind == kind && GroupTitle == title && Path == path && Equals(Provider, provider)
                   && Equals(Connection, connection) && Equals(Credential, credential);
        if (same) return;
        Kind = kind;
        GroupTitle = title;
        Path = path;
        Provider = provider;
        Connection = connection;
        Credential = credential;
        ChangedAll();
    }

    // ----- What the row shows -----

    public string Title => Kind switch
    {
        SidebarNodeKind.Group => GroupTitle,
        SidebarNodeKind.Folder => FolderPath.Leaf(Path),
        SidebarNodeKind.Provider => Provider!.Name,
        SidebarNodeKind.Connection => Connection!.Name,
        _ => Credential!.Name,
    };

    public string Detail
    {
        get
        {
            switch (Kind)
            {
                case SidebarNodeKind.Provider:
                    return Provider!.LastStatus ?? "Not synced yet";
                case SidebarNodeKind.Connection:
                    var c = Connection!;
                    var user = c.Username.Length == 0 ? "" : c.Username + "@";
                    var port = c.Port == c.Kind.DefaultPort() ? "" : ":" + c.Port;
                    return $"{user}{c.Host}{port}";
                case SidebarNodeKind.Credential:
                    return Credential!.Username.Length == 0 ? "Credential" : Credential.Username;
                default:
                    return "";
            }
        }
    }

    public bool HasDetail => Detail.Length > 0;

    public string Glyph => Kind switch
    {
        SidebarNodeKind.Folder or SidebarNodeKind.Provider => Icons.FolderFilled,
        SidebarNodeKind.Connection => Icons.For(Connection!.Kind),
        SidebarNodeKind.Credential => Icons.For(Credential!.Kind),
        _ => "",
    };

    public bool ShowsIcon => Kind != SidebarNodeKind.Group;

    /// <summary>Folders and providers carry the accent colour.</summary>
    public bool IsTinted => Kind is SidebarNodeKind.Folder or SidebarNodeKind.Provider;
    public bool IsGroup => Kind == SidebarNodeKind.Group;
    public bool IsProvider => Kind == SidebarNodeKind.Provider;
    public bool IsContainer => Kind is SidebarNodeKind.Group or SidebarNodeKind.Folder or SidebarNodeKind.Provider;

    public bool IsGenerated => Kind switch
    {
        SidebarNodeKind.Connection => Connection!.Source is not null,
        SidebarNodeKind.Credential => Credential!.Source is not null,
        _ => false,
    };

    public bool IsDraggable => Kind switch
    {
        SidebarNodeKind.Group => false,
        SidebarNodeKind.Folder or SidebarNodeKind.Provider => true,
        _ => !IsGenerated,
    };

    /// <summary>The folder this node is (folders, providers).</summary>
    public string? OwnFolder => Kind switch
    {
        SidebarNodeKind.Folder => Path,
        SidebarNodeKind.Provider => Provider!.Folder,
        _ => null,
    };

    /// <summary>What kind of thing the row is, for screen readers and UI automation.</summary>
    public string KindLabel => Kind switch
    {
        SidebarNodeKind.Group => "group",
        SidebarNodeKind.Folder => "folder",
        SidebarNodeKind.Provider => "provider",
        SidebarNodeKind.Connection => Connection!.Kind.Label() + " connection",
        _ => "credential",
    };

    /// <summary>Name of the credential this folder hands down; shown as a key badge.</summary>
    public string? BadgeTip
    {
        get => badgeTip;
        set
        {
            if (Set(ref badgeTip, value)) Changed(nameof(HasBadge));
        }
    }

    public bool HasBadge => badgeTip is not null;

    public bool IsSyncing { get => isSyncing; set => Set(ref isSyncing, value); }
    public bool IsSelected { get => isSelected; set => Set(ref isSelected, value); }
    public bool IsDropTarget { get => isDropTarget; set => Set(ref isDropTarget, value); }

    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            if (Set(ref isExpanded, value)) expansionChanged(this, value);
        }
    }

    /// <summary>Sets expansion without telling the owner (restoring saved state, expanding for a filter).</summary>
    public void SetExpandedQuietly(bool value)
    {
        if (isExpanded == value) return;
        isExpanded = value;
        Changed(nameof(IsExpanded));
    }
}
