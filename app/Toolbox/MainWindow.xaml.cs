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

    /// <summary>
    /// A new, empty workspace. Its tab goes right after <paramref name="after"/>'s,
    /// or at the end of the row when none is named.
    /// </summary>
    internal Workspace AddWorkspace(Workspace? after = null)
    {
        workspaceCounter++;
        var workspace = new Workspace($"Workspace {workspaceCounter}");
        workspace.Panes.Visibility = Visibility.Collapsed;
        workspace.Panes.LayoutChanged += () =>
        {
            if (ReferenceEquals(workspace, current)) RefreshCommands();
        };
        workspace.Panes.Emptied += () => WorkspaceEmptied(workspace);
        workspace.Panes.OpenConnection = (connection, pane) => Open(connection, workspace, pane);
        workspace.Panes.ContextMenuProvider = pane => PaneMenu(workspace, pane);
        workspace.Selected += () => Select(workspace);
        workspace.CloseRequested += () => CloseWorkspace(workspace);
        workspace.RenameRequested += () => RenameWorkspace(workspace);
        workspace.TitleChanged += () =>
        {
            if (ReferenceEquals(workspace, current)) Title = $"{workspace.Title} - Toolbox";
            NoteTabs();
        };
        var index = after is null ? -1 : workspaces.IndexOf(after);
        index = index < 0 ? workspaces.Count : index + 1;
        workspaces.Insert(index, workspace);
        WorkspaceHost.Children.Add(workspace.Panes);
        TabStrip.Children.Insert(index, workspace.Tab);
        NoteTabs();
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
        Title = $"{workspace.Title} - Toolbox";
        workspace.Tab.BringIntoView();
        RefreshCommands();
        NoteTabs();
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
        var busy = workspace.Panes.SessionsToWarnAbout;
        if (busy > 0 && !Prompts.Confirm($"Close “{workspace.Title}”?",
                                         $"{busy} active session{(busy == 1 ? "" : "s")} will be disconnected.", "Close"))
        {
            return;
        }
        RemoveWorkspace(workspace);
    }

    private void RemoveWorkspace(Workspace workspace)
    {
        var index = workspaces.IndexOf(workspace);
        if (index < 0) return;
        workspace.Panes.TerminateAll();
        workspaces.Remove(workspace);
        WorkspaceHost.Children.Remove(workspace.Panes);
        TabStrip.Children.Remove(workspace.Tab);
        // The window always shows a workspace: closing the last one leaves a fresh, empty one.
        if (workspaces.Count == 0) AddWorkspace();
        if (ReferenceEquals(current, workspace)) Select(workspaces[Math.Min(index, workspaces.Count - 1)]);
        else RefreshCommands();
        NoteTabs();
    }

    /// <summary>
    /// The last pane of a workspace was closed or moved away. A tab with
    /// nothing in it has no reason to stay next to others; the only tab does,
    /// as the place the next session goes.
    /// </summary>
    private void WorkspaceEmptied(Workspace workspace)
    {
        // Not from inside the click or the move that emptied it, and only if
        // it is still empty by then.
        Dispatcher.BeginInvoke(() =>
        {
            if (workspaces.Count < 2 || !workspaces.Contains(workspace)) return;
            if (workspace.Panes.Contents.Count > 0 || workspace.Panes.PaneCount != 1) return;
            RemoveWorkspace(workspace);
        });
    }

    /// <summary>
    /// Also reached by double-clicking the workspace's tab. An empty name
    /// goes back to naming the tab after its sessions.
    /// </summary>
    private void RenameWorkspace(Workspace workspace)
    {
        var entered = Prompts.AskText(
            "Rename Workspace",
            "Double-click a tab to rename it any time. Leave the name empty to have the tab named after its sessions.",
            workspace.Title, "Rename");
        if (entered is null) return;
        var name = entered.Trim();
        // Confirming the automatic name unchanged does not pin it.
        if (workspace.CustomName is null && name == workspace.Title) return;
        workspace.CustomName = name.Length == 0 ? null : name;
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
        if (!CanMoveToNewWorkspace) return;
        MovePane(from, from.Panes.FocusedPane, AddWorkspace(after: from));
    }

    /// <summary>A session that already has its workspace to itself gains nothing from a new one.</summary>
    private bool CanMoveToNewWorkspace => Panes.FocusedPane.Content is not null && Panes.PaneCount > 1;

    /// <summary>Header context menu: where to send the pane, and close.</summary>
    private ContextMenu? PaneMenu(Workspace workspace, PaneView pane)
    {
        if (pane.Content is null) return null;
        var menu = new ContextMenu();
        if (workspace.Panes.PaneCount > 1)
        {
            menu.Items.Add(MenuEntry("Move to New Workspace", () => MovePane(workspace, pane, AddWorkspace(after: workspace))));
        }
        var others = workspaces.Where(w => !ReferenceEquals(w, workspace)).ToList();
        if (others.Count > 0)
        {
            var submenu = new MenuItem { Header = "Move to Workspace" };
            foreach (var other in others) submenu.Items.Add(MenuEntry(other.Title, () => MovePane(workspace, pane, other)));
            menu.Items.Add(submenu);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Close Pane", () => ClosePane(workspace, pane)));
        return menu;
    }

    private bool tabsNotePending;

    /// <summary>
    /// One log line for a burst of changes to the tab row: every tab's name
    /// and number of sessions, the one in front marked with a star. The
    /// end-to-end checks read it.
    /// </summary>
    private void NoteTabs()
    {
        if (tabsNotePending) return;
        tabsNotePending = true;
        Dispatcher.BeginInvoke(() =>
        {
            tabsNotePending = false;
            Log.Info("tabs: " + string.Join(" | ", workspaces.Select(
                w => $"{(ReferenceEquals(w, current) ? "*" : "")}{w.Title} ({w.Panes.Contents.Count})")));
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private static MenuItem MenuEntry(string title, Action handler)
    {
        var item = new MenuItem { Header = title };
        item.Click += (_, _) => handler();
        return item;
    }

    private void ClosePane(Workspace workspace, PaneView pane)
    {
        if (pane.Content?.CloseWarning is { } warning && !Prompts.Confirm("Close pane?", warning, "Close"))
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

    /// <summary>The focused pane's session if its text can be searched (a terminal).</summary>
    private ISearchablePane? FocusedSearchable => Panes.FocusedPane.Content as ISearchablePane;

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
        var searchable = FocusedSearchable is not null;
        FindItem.IsEnabled = searchable;
        FindNextItem.IsEnabled = searchable;
        FindPreviousItem.IsEnabled = searchable;
        FocusNextItem.IsEnabled = Panes.PaneCount > 1;
        FocusPreviousItem.IsEnabled = Panes.PaneCount > 1;
        var hasSession = Panes.FocusedPane.Content is not null;
        MoveToNewItem.IsEnabled = CanMoveToNewWorkspace;

        // Fills the "Move Pane to Workspace" submenu with the other workspaces.
        MoveToMenu.Items.Clear();
        var from = current;
        foreach (var other in workspaces.Where(w => !ReferenceEquals(w, from)))
        {
            MoveToMenu.Items.Add(MenuEntry(other.Title, () => MovePane(from, from.Panes.FocusedPane, other)));
        }
        if (MoveToMenu.Items.Count == 0)
        {
            MoveToMenu.Items.Add(new MenuItem { Header = "No Other Workspaces", IsEnabled = false });
        }
        MoveToMenu.IsEnabled = hasSession;

        // The profiles of the user's Windows Terminal, as its settings name them now.
        TerminalProfilesMenu.Items.Clear();
        foreach (var profile in WindowsTerminal.Profiles())
        {
            // An underscore in a name is not an access key.
            TerminalProfilesMenu.Items.Add(MenuEntry(profile.Name.Replace("_", "__"), () => OpenLocalTerminal(profile.Name)));
        }
        TerminalProfilesMenu.IsEnabled = TerminalProfilesMenu.Items.Count > 0;
        if (TerminalProfilesMenu.Items.Count == 0)
        {
            TerminalProfilesMenu.Items.Add(new MenuItem { Header = "No Profiles Found", IsEnabled = false });
        }

        foreach (var item in ThemeMenu.Items.OfType<MenuItem>())
        {
            item.IsChecked = (item.Tag as string) == ThemeStore.Shared.Current.Id;
        }
        LockVaultItem.IsEnabled = VaultManager.Shared.Vault.IsUnlocked();
        RequireAuthItem.IsChecked = VaultManager.Shared.RequiresAuthentication;
        RdpGraphicsItem.IsChecked = AppSettings.Shared.RdpFastGraphics;
        RdpClipboardItem.IsChecked = AppSettings.Shared.RdpShareClipboard;
        // A build that does not look for updates (one made for trying things) says so by greying both out.
        CheckUpdatesItem.IsEnabled = Updates.Shared.IsActive;
        AutoUpdatesItem.IsEnabled = Updates.Shared.IsActive;
        AutoUpdatesItem.IsChecked = Updates.Shared.IsActive && Updates.Shared.ChecksAutomatically;
    }

    // ----- Help > updates -----

    private async void OnCheckForUpdates(object sender, RoutedEventArgs e) => await Updates.Shared.CheckNowAsync();

    private void OnToggleAutomaticUpdates(object sender, RoutedEventArgs e) =>
        Updates.Shared.ChecksAutomatically = !Updates.Shared.ChecksAutomatically;

    /// <summary>
    /// Installing an update restarts the app. With sessions open that is
    /// asked about first, the way quitting is; false when the user backs out.
    /// </summary>
    internal bool ConfirmRestartForUpdate()
    {
        var busy = workspaces.Sum(w => w.Panes.SessionsToWarnAbout);
        return busy == 0 || Prompts.Confirm("Install the update now?",
            $"{busy} active session{(busy == 1 ? "" : "s")} will be disconnected. Toolbox starts again when the update is in.", "Install");
    }

    /// <summary>Closes the window for an update that is about to install; the question has been asked.</summary>
    internal void CloseForUpdate()
    {
        closingConfirmed = true;
        Close();
    }

    // ----- View > Remote Desktop -----

    private void OnToggleRdpGraphics(object sender, RoutedEventArgs e)
    {
        AppSettings.Shared.RdpFastGraphics = !AppSettings.Shared.RdpFastGraphics;
        RemoteDesktopSwitched("Fast Graphics", AppSettings.Shared.RdpFastGraphics);
    }

    private void OnToggleRdpClipboard(object sender, RoutedEventArgs e)
    {
        AppSettings.Shared.RdpShareClipboard = !AppSettings.Shared.RdpShareClipboard;
        RemoteDesktopSwitched("Share Clipboard", AppSettings.Shared.RdpShareClipboard);
    }

    private static void RemoteDesktopSwitched(string name, bool on)
    {
        Log.Info($"remote desktop: {name} {(on ? "on" : "off")}");
        AppSettings.Shared.SaveSoon();
    }

    // ----- Menu and toolbar -----

    private void OnNewWorkspace(object sender, RoutedEventArgs e) => Select(AddWorkspace());
    private void OnNewLocalTerminal(object sender, RoutedEventArgs e) => OpenLocalTerminal();
    private void OnNewConnection(object sender, RoutedEventArgs e) => EditConnection(null, null);
    private void OnNewCredential(object sender, RoutedEventArgs e) => EditCredential(null, null);
    private void OnNewProvider(object sender, RoutedEventArgs e) => EditProvider(null, null);
    private void OnBrowseFiles(object sender, RoutedEventArgs e) => BrowseFocused();
    private void OnFind(object sender, RoutedEventArgs e) => FocusedSearchable?.Find();
    private void OnFindNext(object sender, RoutedEventArgs e) => FocusedSearchable?.FindNext();
    private void OnFindPrevious(object sender, RoutedEventArgs e) => FocusedSearchable?.FindPrevious();
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
            // Checkable so the current theme shows its mark; the state is set each time the menu opens.
            var item = new MenuItem { Header = theme.Name, Tag = theme.Id, IsCheckable = true };
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
        Shortcut(Key.T, ctrlAlt, () => OpenLocalTerminal());
        Shortcut(Key.N, ctrlShift, () => EditConnection(null, null));
        Shortcut(Key.F, ctrlAlt, BrowseFocused);
        Shortcut(Key.F, ctrlShift, () => FocusedSearchable?.Find());
        Shortcut(Key.G, ctrlShift, () => FocusedSearchable?.FindNext());
        Shortcut(Key.G, ctrlAlt, () => FocusedSearchable?.FindPrevious());
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

    private void OnSidebarResizing(object sender, DragStartedEventArgs e) => LiveResize.Begin();

    private void OnSidebarResized(object sender, DragCompletedEventArgs e)
    {
        LiveResize.End();
        AppSettings.Shared.SidebarWidth = SidebarColumn.ActualWidth;
        AppSettings.Shared.SaveSoon();
    }

    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;

    /// <summary>
    /// Dragging the window's edge resizes every pane step by step. Remote
    /// desktops wait for the end of the drag before asking for a new size.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource source) source.AddHook(WindowHook);
    }

    private static IntPtr WindowHook(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmEnterSizeMove) LiveResize.Begin();
        else if (message == WmExitSizeMove) LiveResize.End();
        return IntPtr.Zero;
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
            var busy = workspaces.Sum(w => w.Panes.SessionsToWarnAbout);
            if (busy > 0 && !Prompts.Confirm("Quit Toolbox?",
                                             $"{busy} active session{(busy == 1 ? "" : "s")} will be disconnected.", "Quit"))
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
        // Local terminals are another program's windows: they get a moment to close.
        LocalTerminalPane.FinishAll(TimeSpan.FromSeconds(1.5));
    }
}
