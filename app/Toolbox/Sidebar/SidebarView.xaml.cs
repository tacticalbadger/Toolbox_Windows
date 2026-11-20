using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Toolbox.Model;
using Toolbox.Workspaces;

namespace Toolbox.Sidebar;

/// <summary>
/// The sidebar: one tree of folders, providers, connections and credentials,
/// with drag and drop between folders and onto panes. The filter field
/// searches the whole tree.
/// </summary>
internal partial class SidebarView : UserControl, IDragTarget
{
    private const string LibraryId = "group:library";

    private readonly AppModel model = AppModel.Shared;
    private readonly ProviderSync sync = ProviderSync.Shared;
    private readonly ObservableCollection<SidebarNode> roots = [];
    private readonly Dictionary<string, SidebarNode> cache = new();
    private readonly HashSet<string> expanded;
    private ISidebarHost? host;
    private string filter = "";
    private bool didInitialExpand;
    private SidebarNode? dropTarget;
    private SidebarNode? pressedNode;
    private Point pressedAt;

    /// <summary>Expands every node on the first load (testing aid: <c>--expand-all</c>).</summary>
    public bool ExpandAllOnLoad { get; set; }

    public SidebarView()
    {
        InitializeComponent();
        expanded = new HashSet<string>(AppSettings.Shared.ExpandedNodes ?? [LibraryId]);
        Tree.ItemsSource = roots;
        model.Changed += Rebuild;
        sync.Changed += Rebuild;
        Loaded += (_, _) => DragController.Register(this);
        Unloaded += (_, _) => DragController.Unregister(this);
    }

    public void Attach(ISidebarHost owner)
    {
        host = owner;
        Rebuild();
    }

    public void FocusFilter() => Filter.Focus();

    // ----- Tree -----

    private SidebarNode Node(string id, Action<SidebarNode> assign, HashSet<string> used)
    {
        if (!cache.TryGetValue(id, out var node))
        {
            node = new SidebarNode(id, OnExpansionChanged);
            node.SetExpandedQuietly(expanded.Contains(id));
            cache[id] = node;
        }
        assign(node);
        used.Add(id);
        return node;
    }

    private bool Matches(params string?[] fields)
    {
        var query = filter.Trim();
        return query.Length == 0 ||
               fields.Any(f => f is not null && f.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }

    private void Rebuild()
    {
        var selectedId = (Tree.SelectedItem as SidebarNode)?.Id;
        var used = new HashSet<string>();
        var children = new Dictionary<SidebarNode, List<SidebarNode>>();
        List<SidebarNode> ChildrenOf(SidebarNode parent) =>
            children.TryGetValue(parent, out var list) ? list : children[parent] = [];
        void AttachTo(SidebarNode child, SidebarNode parent)
        {
            ChildrenOf(parent).Add(child);
            child.Parent = parent;
        }

        var library = Node(LibraryId, n => n.SetGroup("Library"), used);
        var folderNodes = new Dictionary<string, SidebarNode>();
        SidebarNode FolderNode(string path)
        {
            if (folderNodes.TryGetValue(path, out var existing)) return existing;
            var provider = model.Providers.FirstOrDefault(p => p.Folder == path);
            var made = provider is not null
                ? Node("prov:" + provider.Id, n => n.SetProvider(provider), used)
                : Node("folder:" + path, n => n.SetFolder(path), used);
            folderNodes[path] = made;
            var parentPath = FolderPath.Parent(path);
            AttachTo(made, parentPath is null ? library : FolderNode(parentPath));
            return made;
        }

        foreach (var path in model.Folders) FolderNode(path);
        foreach (var provider in model.Providers) FolderNode(provider.Folder);
        foreach (var c in model.Connections)
        {
            if (!Matches(c.Name, c.Host, c.Username, c.Folder)) continue;
            AttachTo(Node("conn:" + c.Id, n => n.SetConnection(c), used), c.Folder is null ? library : FolderNode(c.Folder));
        }
        foreach (var c in model.Credentials)
        {
            if (!Matches(c.Name, c.Username, c.Folder)) continue;
            AttachTo(Node("cred:" + c.Id, n => n.SetCredential(c), used), c.Folder is null ? library : FolderNode(c.Folder));
        }
        var filtering = filter.Trim().Length > 0;
        if (filtering) Prune(library, children);

        // Nodes whose record is gone leave the cache; pruned nodes stay out of the tree.
        foreach (var stale in cache.Keys.Where(id => !used.Contains(id)).ToList()) cache.Remove(stale);

        foreach (var node in cache.Values)
        {
            var wanted = children.TryGetValue(node, out var list) ? list : [];
            Sort(wanted);
            Replace(node.Children, wanted);
            node.IsSyncing = node.Provider is { } p && sync.IsSyncing(p.Id);
            node.BadgeTip = node.OwnFolder is { } folder && model.FolderCredential(folder) is { } credential
                ? "Credential: " + credential.Name
                : null;
        }
        Replace(roots, [library]);

        if (filtering)
        {
            foreach (var node in cache.Values.Where(n => n.IsContainer)) node.SetExpandedQuietly(true);
        }
        else
        {
            foreach (var node in cache.Values.Where(n => n.IsContainer)) node.SetExpandedQuietly(expanded.Contains(node.Id));
            if (!didInitialExpand && host is not null)
            {
                didInitialExpand = true;
                if (ExpandAllOnLoad)
                {
                    foreach (var node in cache.Values.Where(n => n.IsContainer)) node.IsExpanded = true;
                }
            }
        }

        if (selectedId is not null && cache.TryGetValue(selectedId, out var selected)) selected.IsSelected = true;
    }

    private static void Sort(List<SidebarNode> nodes)
    {
        nodes.Sort((a, b) =>
        {
            var byRank = Rank(a).CompareTo(Rank(b));
            return byRank != 0 ? byRank : string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase);
        });
    }

    private static int Rank(SidebarNode node) => node.Kind switch
    {
        SidebarNodeKind.Group => 0,
        SidebarNodeKind.Folder or SidebarNodeKind.Provider => 1,
        SidebarNodeKind.Connection => 2,
        _ => 3,
    };

    /// <summary>Drops containers with nothing matching inside while filtering.</summary>
    private static bool Prune(SidebarNode node, Dictionary<SidebarNode, List<SidebarNode>> children)
    {
        if (children.TryGetValue(node, out var list))
        {
            list.RemoveAll(child => child.IsContainer && !Prune(child, children));
            return list.Count > 0 || !node.IsContainer;
        }
        return !node.IsContainer;
    }

    /// <summary>Makes the collection hold exactly <paramref name="wanted"/>, leaving it alone when it already does.</summary>
    private static void Replace(ObservableCollection<SidebarNode> target, List<SidebarNode> wanted)
    {
        if (target.SequenceEqual(wanted)) return;
        target.Clear();
        foreach (var node in wanted) target.Add(node);
    }

    private void OnExpansionChanged(SidebarNode node, bool isExpanded)
    {
        // What a filter expands is temporary and not remembered.
        if (filter.Trim().Length > 0) return;
        if (isExpanded) expanded.Add(node.Id);
        else expanded.Remove(node.Id);
        AppSettings.Shared.ExpandedNodes = expanded.Order().ToList();
        AppSettings.Shared.SaveSoon();
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        filter = Filter.Text;
        FilterHint.Visibility = filter.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Rebuild();
    }

    private void OnSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        // The heading is not a thing to select.
        if (e.NewValue is SidebarNode { IsGroup: true } group) group.IsSelected = false;
    }

    // ----- Actions -----

    private static SidebarNode? NodeAt(object? source)
    {
        var element = source as DependencyObject;
        while (element is not null and not TreeViewItem)
        {
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }
        return (element as TreeViewItem)?.DataContext as SidebarNode;
    }

    private static bool IsOnExpander(object? source)
    {
        var element = source as DependencyObject;
        while (element is not null and not TreeViewItem)
        {
            if (element is ToggleButton) return true;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return false;
    }

    private void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || IsOnExpander(e.OriginalSource)) return;
        var node = NodeAt(e.OriginalSource);
        if (node is null) return;
        // Folders and headings just open and close, which the tree does itself.
        if (node.Kind is SidebarNodeKind.Folder or SidebarNodeKind.Group) return;
        e.Handled = true;
        Activate(node);
    }

    private void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (Tree.SelectedItem is not SidebarNode node) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (node.Kind is SidebarNodeKind.Folder or SidebarNodeKind.Group) node.IsExpanded = !node.IsExpanded;
            else Activate(node);
        }
        else if (e.Key == Key.Delete)
        {
            e.Handled = true;
            Delete(node);
        }
    }

    private void Activate(SidebarNode node)
    {
        if (host is null) return;
        switch (node.Kind)
        {
            case SidebarNodeKind.Connection: host.OpenConnection(node.Connection!); break;
            case SidebarNodeKind.Credential: host.EditCredential(node.Credential, null); break;
            case SidebarNodeKind.Provider: host.SyncProvider(node.Provider!); break;
        }
    }

    private void Delete(SidebarNode node)
    {
        if (host is null) return;
        switch (node.Kind)
        {
            case SidebarNodeKind.Connection: host.DeleteConnection(node.Connection!); break;
            case SidebarNodeKind.Credential: host.DeleteCredential(node.Credential!); break;
            case SidebarNodeKind.Provider: host.DeleteProvider(node.Provider!); break;
            case SidebarNodeKind.Folder: host.DeleteFolder(node.Path); break;
        }
    }

    // ----- Menus -----

    private void OnAddClicked(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = AddButton, Placement = PlacementMode.Top };
        AddItems(menu, null);
        menu.IsOpen = true;
    }

    private void OnTreeRightDown(object sender, MouseButtonEventArgs e)
    {
        // Select what was right-clicked, so the menu and the highlight agree.
        var node = NodeAt(e.OriginalSource);
        if (node is { IsGroup: false }) node.IsSelected = true;
        else if (Tree.SelectedItem is SidebarNode selected) selected.IsSelected = false;
    }

    private void OnTreeRightUp(object sender, MouseButtonEventArgs e)
    {
        var menu = MenuFor(NodeAt(e.OriginalSource));
        menu.PlacementTarget = Tree;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void AddItems(ContextMenu menu, string? folder)
    {
        if (host is null) return;
        var suffix = folder is null ? "" : $" in {FolderPath.Leaf(folder)}";
        menu.Items.Add(Item($"New Connection{suffix}…", () => host.EditConnection(null, folder)));
        menu.Items.Add(Item($"New Credential{suffix}…", () => host.EditCredential(null, folder)));
        menu.Items.Add(Item($"New Folder{suffix}", () => host.NewFolder(folder)));
        menu.Items.Add(Item($"New Provider{suffix}…", () => host.EditProvider(null, folder)));
    }

    /// <summary>The context menu for a node; null is the empty area below the tree.</summary>
    internal ContextMenu MenuFor(SidebarNode? node)
    {
        var menu = new ContextMenu();
        if (host is null) return menu;
        switch (node?.Kind)
        {
            case null:
            case SidebarNodeKind.Group:
                AddItems(menu, null);
                break;
            case SidebarNodeKind.Folder:
            {
                var path = node.Path;
                AddItems(menu, path);
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Rename…", () => host.RenameFolder(path)));
                menu.Items.Add(Item(model.FolderCredentials.ContainsKey(path) ? "Change Credential…" : "Set Credential…",
                                    () => host.SetFolderCredential(path)));
                menu.Items.Add(Item("Delete Folder", () => host.DeleteFolder(path)));
                break;
            }
            case SidebarNodeKind.Provider:
            {
                var provider = node.Provider!;
                menu.Items.Add(Item("Sync Now", () => host.SyncProvider(provider)));
                menu.Items.Add(Item("Edit…", () => host.EditProvider(provider, null)));
                menu.Items.Add(Item(model.FolderCredentials.ContainsKey(provider.Folder) ? "Change Credential…" : "Set Credential…",
                                    () => host.SetFolderCredential(provider.Folder)));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Delete", () => host.DeleteProvider(provider)));
                break;
            }
            case SidebarNodeKind.Connection:
            {
                var connection = node.Connection!;
                menu.Items.Add(Item(connection.Kind.IsFileTransfer() ? "Open" : "Connect", () => host.OpenConnection(connection)));
                if (connection.Kind == ConnectionKind.Ssh)
                {
                    menu.Items.Add(Item("Browse Files (SFTP)", () => host.BrowseFiles(connection)));
                }
                menu.Items.Add(Item("Edit…", () => host.EditConnection(connection, null)));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Delete", () => host.DeleteConnection(connection)));
                break;
            }
            case SidebarNodeKind.Credential:
            {
                var credential = node.Credential!;
                menu.Items.Add(Item("Edit…", () => host.EditCredential(credential, null)));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Delete", () => host.DeleteCredential(credential)));
                break;
            }
        }
        return menu;
    }

    private static MenuItem Item(string title, Action handler)
    {
        var item = new MenuItem { Header = title };
        item.Click += (_, _) => handler();
        return item;
    }

    // ----- Dragging out of the tree -----

    private void OnTreeMouseDown(object sender, MouseButtonEventArgs e)
    {
        pressedNode = IsOnExpander(e.OriginalSource) ? null : NodeAt(e.OriginalSource);
        pressedAt = e.GetPosition(Tree);
    }

    private void OnTreeMouseUp(object sender, MouseButtonEventArgs e) => pressedNode = null;

    private void OnTreeMouseMove(object sender, MouseEventArgs e)
    {
        if (pressedNode is null || e.LeftButton != MouseButtonState.Pressed || DragController.IsDragging) return;
        var now = e.GetPosition(Tree);
        if (Math.Abs(now.X - pressedAt.X) < 5 && Math.Abs(now.Y - pressedAt.Y) < 5) return;
        var node = pressedNode;
        pressedNode = null;
        if (!node.IsDraggable) return;
        DragController.Begin(Tree, new DragPayload { NodeIds = [node.Id] });
    }

    // ----- Accepting drops -----

    /// <summary>
    /// The node a drop should land on and the folder path it means (null path
    /// = top level). Items retarget to their containing folder; nothing can be
    /// dropped into a provider.
    /// </summary>
    private (SidebarNode Node, string? Folder)? DropTargetFor(SidebarNode? node)
    {
        if (node is null) return cache.TryGetValue(LibraryId, out var library) ? (library, null) : null;
        switch (node.Kind)
        {
            case SidebarNodeKind.Group:
                return node.Id == LibraryId ? (node, null) : null;
            case SidebarNodeKind.Folder:
                return (node, node.Path);
            case SidebarNodeKind.Provider:
                return null;
            default:
                return node.Parent is null || node.IsGenerated ? null : DropTargetFor(node.Parent);
        }
    }

    private bool CanDrop(DragPayload payload, string? folder)
    {
        foreach (var id in payload.NodeIds)
        {
            if (!cache.TryGetValue(id, out var node) || !node.IsDraggable) return false;
            // A folder cannot move into itself or its own subfolders.
            if (node.OwnFolder is { } own && folder is not null &&
                (folder == own || folder.StartsWith(own + "/", StringComparison.Ordinal)))
            {
                return false;
            }
        }
        return payload.NodeIds.Count > 0;
    }

    private (SidebarNode Node, string? Folder)? TargetAt(Point screen, DragPayload payload)
    {
        if (payload.PaneId is not null || !IsVisible || PresentationSource.FromVisual(Tree) is null) return null;
        var point = Tree.PointFromScreen(screen);
        if (point.X < 0 || point.Y < 0 || point.X > Tree.ActualWidth || point.Y > Tree.ActualHeight) return null;
        var target = DropTargetFor(NodeAt(Tree.InputHitTest(point)));
        return target is not null && CanDrop(payload, target.Value.Folder) ? target : null;
    }

    private void SetDropTarget(SidebarNode? node)
    {
        if (ReferenceEquals(dropTarget, node)) return;
        if (dropTarget is not null) dropTarget.IsDropTarget = false;
        dropTarget = node;
        if (dropTarget is not null) dropTarget.IsDropTarget = true;
    }

    bool IDragTarget.DragOver(Point screen, DragPayload payload)
    {
        var target = TargetAt(screen, payload);
        SetDropTarget(target?.Node);
        return target is not null;
    }

    void IDragTarget.DragLeave() => SetDropTarget(null);

    void IDragTarget.Drop(Point screen, DragPayload payload)
    {
        var target = TargetAt(screen, payload);
        SetDropTarget(null);
        if (target is null) return;
        MoveNodes(payload.NodeIds, target.Value.Folder);
    }

    /// <summary>Moves nodes into a folder (null = top level), the way a drop does.</summary>
    internal void MoveNodes(IReadOnlyList<string> ids, string? folder)
    {
        foreach (var id in ids)
        {
            if (!cache.TryGetValue(id, out var node)) continue;
            try
            {
                switch (node.Kind)
                {
                    case SidebarNodeKind.Connection: model.MoveConnection(node.Connection!, folder); break;
                    case SidebarNodeKind.Credential: model.MoveCredential(node.Credential!, folder); break;
                    case SidebarNodeKind.Provider: model.MoveProvider(node.Provider!, folder); break;
                    case SidebarNodeKind.Folder:
                        model.RenameFolder(node.Path, FolderPath.Join(folder, FolderPath.Leaf(node.Path)));
                        break;
                }
            }
            catch (Exception error)
            {
                Prompts.Error($"Couldn't move {node.Title}", Errors.Describe(error));
            }
        }
    }
}
