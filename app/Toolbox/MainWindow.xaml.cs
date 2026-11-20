using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Toolbox.Model;
using Toolbox.Terminal;
using Toolbox.Workspaces;

namespace Toolbox;

/// <summary>
/// The app's window: the connection sidebar on the left, workspaces as tabs
/// on the right, each holding a pane layout.
/// </summary>
internal partial class MainWindow : Window, Sidebar.ISidebarHost
{
    private readonly List<Workspace> workspaces = [];
    private readonly AppModel model = AppModel.Shared;
    private Workspace current = null!;
    private int workspaceCounter;
    private bool closingConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        RestorePlacement();
        BuildThemeMenu();
        BindShortcuts();
        Sidebar.ExpandAllOnLoad = App.HasFlag("--expand-all");
        Sidebar.Attach(this);
        SetSidebarVisible(AppSettings.Shared.SidebarVisible, remember: false);
        Select(AddWorkspace());
        Closing += OnClosing;
    }

    private PaneContainer Panes => current.Panes;

    // ----- Workspaces -----

    internal IReadOnlyList<Workspace> Workspaces => workspaces;

    internal Workspace AddWorkspace()
    {
        workspaceCounter++;
        var workspace = new Workspace($"Workspace {workspaceCounter}");
        workspace.Panes.Visibility = Visibility.Collapsed;
        workspace.Panes.LayoutChanged += () =>
        {
            if (ReferenceEquals(workspace, current)) RefreshCommands();
        };
        workspace.Panes.OpenConnection = (connection, pane) => Open(connection, pane);
        workspace.Panes.ContextMenuProvider = pane => PaneMenu(workspace, pane);
        workspace.Selected += () => Select(workspace);
        workspace.CloseRequested += () => CloseWorkspace(workspace);
        workspace.RenameRequested += () => RenameWorkspace(workspace);
        workspaces.Add(workspace);
        WorkspaceHost.Children.Add(workspace.Panes);
        TabStrip.Children.Add(workspace.Tab);
        return workspace;
    }

    internal void Select(Workspace workspace)
    {
        current = workspace;
        foreach (var other in workspaces)
        {
            var selected = ReferenceEquals(other, workspace);
            other.Panes.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            other.IsSelected = selected;
        }
        Title = $"{workspace.Name} - Toolbox";
        workspace.Tab.BringIntoView();
        RefreshCommands();
        // Once the panes are laid out again, hand the keyboard back to the session.
        Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(current, workspace)) workspace.Panes.FocusedPane.FocusSession();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void SelectNeighbor(int delta)
    {
        if (workspaces.Count < 2) return;
        var index = workspaces.IndexOf(current);
        Select(workspaces[(index + delta + workspaces.Count) % workspaces.Count]);
    }

    private void CloseWorkspace(Workspace workspace)
    {
        var live = workspace.Panes.LiveSessionCount;
        if (live > 0 && !Prompts.Confirm($"Close “{workspace.Name}”?",
                                         $"{live} active session{(live == 1 ? "" : "s")} will be disconnected.", "Close"))
        {
            return;
        }
        workspace.Panes.TerminateAll();
        var index = workspaces.IndexOf(workspace);
        workspaces.Remove(workspace);
        WorkspaceHost.Children.Remove(workspace.Panes);
        TabStrip.Children.Remove(workspace.Tab);
        // The window always shows a workspace: closing the last one leaves a fresh, empty one.
        if (workspaces.Count == 0) AddWorkspace();
        if (ReferenceEquals(current, workspace)) Select(workspaces[Math.Min(index, workspaces.Count - 1)]);
        else RefreshCommands();
    }

    /// <summary>Also reached by double-clicking the workspace's tab.</summary>
    private void RenameWorkspace(Workspace workspace)
    {
        var name = Prompts.AskText("Rename Workspace", "Double-click a tab to rename it any time.", workspace.Name, "Rename");
        if (string.IsNullOrWhiteSpace(name)) return;
        workspace.Name = name.Trim();
        if (ReferenceEquals(workspace, current)) Title = $"{workspace.Name} - Toolbox";
    }

    /// <summary>Moves a pane's session into another workspace, keeping it running.</summary>
    internal void MovePane(Workspace from, PaneView pane, Workspace target)
    {
        if (ReferenceEquals(from, target)) return;
        var content = from.Panes.Lift(pane);
        if (content is null) return;
        Select(target);
        target.Panes.Adopt(content);
    }

    internal void MovePaneToNewWorkspace()
    {
        var from = current;
        var pane = from.Panes.FocusedPane;
        if (pane.Content is null) return;
        MovePane(from, pane, AddWorkspace());
    }

    /// <summary>Header context menu: where to send the pane, and close.</summary>
    private ContextMenu? PaneMenu(Workspace workspace, PaneView pane)
    {
        if (pane.Content is null) return null;
        var menu = new ContextMenu();
        menu.Items.Add(MenuEntry("Move to New Workspace", () => MovePane(workspace, pane, AddWorkspace())));
        var others = workspaces.Where(w => !ReferenceEquals(w, workspace)).ToList();
        if (others.Count > 0)
        {
            var submenu = new MenuItem { Header = "Move to Workspace" };
            foreach (var other in others) submenu.Items.Add(MenuEntry(other.Name, () => MovePane(workspace, pane, other)));
            menu.Items.Add(submenu);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Close Pane", () => ClosePane(workspace, pane)));
        return menu;
    }

    private static MenuItem MenuEntry(string title, Action handler)
    {
        var item = new MenuItem { Header = title };
        item.Click += (_, _) => handler();
        return item;
    }

    private void ClosePane(Workspace workspace, PaneView pane)
    {
        if (pane.Content?.IsLive == true &&
            !Prompts.Confirm("Close pane?", "The session will be disconnected.", "Close"))
        {
            return;
        }
        workspace.Panes.ClosePane(pane.Id);
    }

    // ----- What is possible right now -----

    private bool CanClosePane => Panes.FocusedPane.Content is not null || Panes.PaneCount > 1;

    /// <summary>The connection behind the focused pane, if any.</summary>
    private Connection? FocusedConnection => Panes.FocusedPane.Content?.Connection;

    private bool CanBrowseFiles =>
        FocusedConnection is { } connection && (connection.Kind.UsesSsh() || connection.Kind.IsFileTransfer());

    private void RefreshCommands()
    {
        if (current is null) return;
        ClosePaneButton.IsEnabled = CanClosePane;
        BrowseFilesButton.IsEnabled = CanBrowseFiles;
    }

    private void OnMenuOpened(object sender, RoutedEventArgs e)
    {
        ClosePaneItem.IsEnabled = CanClosePane;
        BrowseFilesItem.IsEnabled = CanBrowseFiles;
        FocusNextItem.IsEnabled = Panes.PaneCount > 1;
        FocusPreviousItem.IsEnabled = Panes.PaneCount > 1;
        var hasSession = Panes.FocusedPane.Content is not null;
        MoveToNewItem.IsEnabled = hasSession;

        // Fills the "Move Pane to Workspace" submenu with the other workspaces.
        MoveToMenu.Items.Clear();
        var from = current;
        foreach (var other in workspaces.Where(w => !ReferenceEquals(w, from)))
        {
            MoveToMenu.Items.Add(MenuEntry(other.Name, () => MovePane(from, from.Panes.FocusedPane, other)));
        }
        if (MoveToMenu.Items.Count == 0)
        {
            MoveToMenu.Items.Add(new MenuItem { Header = "No Other Workspaces", IsEnabled = false });
        }
        MoveToMenu.IsEnabled = hasSession;

        foreach (var item in ThemeMenu.Items.OfType<MenuItem>())
        {
            item.IsChecked = (item.Tag as string) == ThemeStore.Shared.Current.Id;
        }
        LockVaultItem.IsEnabled = VaultManager.Shared.Vault.IsUnlocked();
        RequireAuthItem.IsChecked = VaultManager.Shared.RequiresAuthentication;
    }

    // ----- Menu and toolbar -----

    private void OnNewWorkspace(object sender, RoutedEventArgs e) => Select(AddWorkspace());
    private void OnNewConnection(object sender, RoutedEventArgs e) => EditConnection(null, null);
    private void OnNewCredential(object sender, RoutedEventArgs e) => EditCredential(null, null);
    private void OnNewProvider(object sender, RoutedEventArgs e) => EditProvider(null, null);
    private void OnBrowseFiles(object sender, RoutedEventArgs e) => BrowseFocused();
    private void OnSyncProviders(object sender, RoutedEventArgs e) => SyncAllProviders();
    private void OnClosePane(object sender, RoutedEventArgs e) => CloseFocusedPane();
    private void OnCloseWorkspace(object sender, RoutedEventArgs e) => CloseWorkspace(current);
    private void OnExit(object sender, RoutedEventArgs e) => Close();
    private void OnToggleSidebar(object sender, RoutedEventArgs e) => SetSidebarVisible(!AppSettings.Shared.SidebarVisible);
    private void OnTextBigger(object sender, RoutedEventArgs e) => ThemeStore.Shared.FontSize += 1;
    private void OnTextSmaller(object sender, RoutedEventArgs e) => ThemeStore.Shared.FontSize -= 1;
    private void OnTextReset(object sender, RoutedEventArgs e) => ThemeStore.Shared.FontSize = AppSettings.DefaultFontSize;
    private void OnSplitRight(object sender, RoutedEventArgs e) => Panes.Split(SplitAxis.Horizontal);
    private void OnSplitDown(object sender, RoutedEventArgs e) => Panes.Split(SplitAxis.Vertical);
    private void OnFocusNext(object sender, RoutedEventArgs e) => Panes.FocusNeighbor(1);
    private void OnFocusPrevious(object sender, RoutedEventArgs e) => Panes.FocusNeighbor(-1);
    private void OnMovePaneToNewWorkspace(object sender, RoutedEventArgs e) => MovePaneToNewWorkspace();
    private void OnRenameWorkspace(object sender, RoutedEventArgs e) => RenameWorkspace(current);
    private void OnNextWorkspace(object sender, RoutedEventArgs e) => SelectNeighbor(1);
    private void OnPreviousWorkspace(object sender, RoutedEventArgs e) => SelectNeighbor(-1);
    private void OnLockVault(object sender, RoutedEventArgs e) => VaultManager.Shared.Lock();

    private void OnAbout(object sender, RoutedEventArgs e) =>
        Prompts.Info($"Toolbox {AppInfo.Display}",
                     $"SSH, RDP, VNC and file transfer sessions in one window.\n\ncore {CoreApi.CoreVersion()}");

    private async void OnToggleVaultAuth(object sender, RoutedEventArgs e)
    {
        try
        {
            await VaultManager.Shared.SetRequiresAuthenticationAsync(!VaultManager.Shared.RequiresAuthentication);
        }
        catch (VaultException error) when (error.IsCancellation)
        {
            // Left as it was.
        }
        catch (Exception error)
        {
            Prompts.Error("Couldn't change the vault setting", Errors.Describe(error));
        }
    }

    private void CloseFocusedPane()
    {
        if (CanClosePane) ClosePane(current, Panes.FocusedPane);
    }

    private void BrowseFocused()
    {
        if (CanBrowseFiles && FocusedConnection is { } connection) OpenFiles(connection);
    }

    private async void SyncAllProviders() => await ProviderSync.Shared.SyncAllAsync(automatic: false);

    private void BuildThemeMenu()
    {
        foreach (var theme in TerminalTheme.All)
        {
            var item = new MenuItem { Header = theme.Name, Tag = theme.Id, IsCheckable = false };
            item.Click += (_, _) => ThemeStore.Shared.Current = theme;
            ThemeMenu.Items.Add(item);
        }
    }

    /// <summary>
    /// App shortcuts. They use Ctrl+Shift and Alt+Shift so that plain Ctrl+letter
    /// keys keep going to the terminal, where they are control characters.
    /// </summary>
    private void BindShortcuts()
    {
        const ModifierKeys ctrlShift = ModifierKeys.Control | ModifierKeys.Shift;
        const ModifierKeys altShift = ModifierKeys.Alt | ModifierKeys.Shift;
        const ModifierKeys ctrlAlt = ModifierKeys.Control | ModifierKeys.Alt;
        Shortcut(Key.T, ctrlShift, () => Select(AddWorkspace()));
        Shortcut(Key.N, ctrlShift, () => EditConnection(null, null));
        Shortcut(Key.F, ctrlShift, BrowseFocused);
        Shortcut(Key.R, ctrlShift, SyncAllProviders);
        Shortcut(Key.W, ctrlShift, CloseFocusedPane);
        Shortcut(Key.W, ctrlAlt, () => CloseWorkspace(current));
        Shortcut(Key.B, ctrlShift, () => SetSidebarVisible(!AppSettings.Shared.SidebarVisible));
        Shortcut(Key.M, ctrlShift, MovePaneToNewWorkspace);
        Shortcut(Key.R, ctrlAlt, () => RenameWorkspace(current));
        Shortcut(Key.OemPlus, altShift, () => Panes.Split(SplitAxis.Horizontal));
        Shortcut(Key.OemMinus, altShift, () => Panes.Split(SplitAxis.Vertical));
        Shortcut(Key.OemCloseBrackets, ctrlShift, () => Panes.FocusNeighbor(1));
        Shortcut(Key.OemOpenBrackets, ctrlShift, () => Panes.FocusNeighbor(-1));
        Shortcut(Key.Tab, ModifierKeys.Control, () => SelectNeighbor(1));
        Shortcut(Key.Tab, ctrlShift, () => SelectNeighbor(-1));
        Shortcut(Key.OemPlus, ModifierKeys.Control, () => ThemeStore.Shared.FontSize += 1);
        Shortcut(Key.Add, ModifierKeys.Control, () => ThemeStore.Shared.FontSize += 1);
        Shortcut(Key.OemMinus, ModifierKeys.Control, () => ThemeStore.Shared.FontSize -= 1);
        Shortcut(Key.Subtract, ModifierKeys.Control, () => ThemeStore.Shared.FontSize -= 1);
        Shortcut(Key.D0, ModifierKeys.Control, () => ThemeStore.Shared.FontSize = AppSettings.DefaultFontSize);
        Shortcut(Key.NumPad0, ModifierKeys.Control, () => ThemeStore.Shared.FontSize = AppSettings.DefaultFontSize);
    }

    private void Shortcut(Key key, ModifierKeys modifiers, Action action) =>
        InputBindings.Add(new KeyBinding(new RelayCommand(action), key, modifiers));

    // ----- Sidebar and window placement -----

    private void SetSidebarVisible(bool visible, bool remember = true)
    {
        if (visible)
        {
            SidebarColumn.MinWidth = 220;
            SidebarColumn.Width = new GridLength(Math.Clamp(AppSettings.Shared.SidebarWidth, 220, 520));
            Sidebar.Visibility = Visibility.Visible;
            SidebarSplitter.Visibility = Visibility.Visible;
        }
        else
        {
            SidebarColumn.MinWidth = 0;
            SidebarColumn.Width = new GridLength(0);
            Sidebar.Visibility = Visibility.Collapsed;
            SidebarSplitter.Visibility = Visibility.Collapsed;
        }
        if (!remember) return;
        AppSettings.Shared.SidebarVisible = visible;
        AppSettings.Shared.SaveSoon();
    }

    private void OnSidebarResized(object sender, DragCompletedEventArgs e)
    {
        AppSettings.Shared.SidebarWidth = SidebarColumn.ActualWidth;
        AppSettings.Shared.SaveSoon();
    }

    private void RestorePlacement()
    {
        if (AppSettings.Shared.Window is not { } saved || saved.Width < 400 || saved.Height < 300) return;
        // Only if it still lands on a screen that exists.
        var visible = saved.Left + saved.Width > SystemParameters.VirtualScreenLeft + 80
                      && saved.Left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 80
                      && saved.Top >= SystemParameters.VirtualScreenTop - 10
                      && saved.Top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 80;
        if (!visible) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = saved.Left;
        Top = saved.Top;
        Width = saved.Width;
        Height = saved.Height;
        if (saved.Maximized) WindowState = WindowState.Maximized;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!closingConfirmed)
        {
            var live = workspaces.Sum(w => w.Panes.LiveSessionCount);
            if (live > 0 && !Prompts.Confirm("Quit Toolbox?",
                                             $"{live} active session{(live == 1 ? "" : "s")} will be disconnected.", "Quit"))
            {
                e.Cancel = true;
                return;
            }
            closingConfirmed = true;
        }
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        AppSettings.Shared.Window = new WindowPlacement
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = WindowState == WindowState.Maximized,
        };
        foreach (var workspace in workspaces) workspace.Panes.TerminateAll();
    }
}
