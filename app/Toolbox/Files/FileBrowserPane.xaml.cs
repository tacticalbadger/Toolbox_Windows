using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Toolbox.Workspaces;

namespace Toolbox.Files;

/// <summary>A file browser (SFTP, FTP or FTPS) living in a workspace pane.</summary>
internal partial class FileBrowserPane : UserControl, IPaneContent
{
    private readonly FileBrowserModel model;

    public FileBrowserPane(Connection connection)
    {
        Connection = connection;
        model = new FileBrowserModel(connection);
        InitializeComponent();
        DataContext = model;
        model.ConnectedChanged += () => StateChanged?.Invoke();
        model.PropertyChanged += OnModelChanged;
        PathBox.Text = model.Path;
        PreviewMouseDown += (_, _) => FocusRequested?.Invoke();
    }

    internal FileBrowserModel Model => model;

    // ----- IPaneContent -----

    public FrameworkElement View => this;
    public Connection Connection { get; }
    public string Title => $"Files · {Connection.Name}";
    public bool IsLive => model.IsConnected;
    public event Action? TitleChanged { add { } remove { } }
    public event Action? StateChanged;
    public event Action? FocusRequested;
    public event Action<bool>? SessionEnded { add { } remove { } }

    public void FocusSession()
    {
        if (model.IsConnected) List.Focus();
        else Focus();
    }

    public void Terminate() => model.Disconnect();

    public async void Start(string username, SshAuth auth) => await model.ConnectAsync(username, auth);

    public async void StartFtp(string username, string password, bool secure) =>
        await model.ConnectFtpAsync(username, password, secure);

    /// <summary>The user declined a prompt while resolving credentials.</summary>
    public void Cancelled() => model.Status = "Cancelled";

    /// <summary>Credentials could not be resolved.</summary>
    public void Failed(string message) => model.Status = message;

    // ----- Events -----

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileBrowserModel.Path)) PathBox.Text = model.Path;
    }

    private List<FileEntry> Selected => List.SelectedItems.OfType<FileEntry>().ToList();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = Selected;
        DownloadButton.IsEnabled = selected.Any(s => !s.IsDir);
        DeleteButton.IsEnabled = selected.Count > 0;
    }

    private async void OnUp(object sender, RoutedEventArgs e) => await model.GoUpAsync();
    private async void OnHome(object sender, RoutedEventArgs e) => await model.GoHomeAsync();
    private async void OnRefresh(object sender, RoutedEventArgs e) => await model.RefreshAsync();
    private void OnUpload(object sender, RoutedEventArgs e) => model.Upload();
    private void OnDownload(object sender, RoutedEventArgs e) => model.Download(Selected);
    private async void OnNewFolder(object sender, RoutedEventArgs e) => await model.NewFolderAsync();
    private async void OnDelete(object sender, RoutedEventArgs e) => await model.DeleteAsync(Selected);
    private void OnClearFinished(object sender, RoutedEventArgs e) => model.ClearFinishedTransfers();

    private async void OnPathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await model.LoadAsync(PathBox.Text.Trim());
        PathBox.Text = model.Path;
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is FileEntry entry) model.Open(entry);
    }

    private async void OnListKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when List.SelectedItem is FileEntry entry:
                e.Handled = true;
                model.Open(entry);
                break;
            case Key.Delete:
                e.Handled = true;
                await model.DeleteAsync(Selected);
                break;
            case Key.Back:
                e.Handled = true;
                await model.GoUpAsync();
                break;
            case Key.F5:
                e.Handled = true;
                await model.RefreshAsync();
                break;
            case Key.F2 when List.SelectedItem is FileEntry renamed:
                e.Handled = true;
                await model.RenameAsync(renamed);
                break;
        }
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var picked = Selected;
        ListMenu.Items.Clear();
        if (picked.Count == 0)
        {
            ListMenu.Items.Add(Item("Upload…", model.Upload));
            ListMenu.Items.Add(Item("New Folder…", async () => await model.NewFolderAsync()));
            ListMenu.Items.Add(Item("Refresh", async () => await model.RefreshAsync()));
            return;
        }
        if (picked.Count == 1)
        {
            var entry = picked[0];
            ListMenu.Items.Add(entry.IsDir
                ? Item("Open", () => model.Open(entry))
                : Item("Download…", () => model.Download([entry])));
            ListMenu.Items.Add(Item("Rename…", async () => await model.RenameAsync(entry)));
            ListMenu.Items.Add(new Separator());
        }
        else if (picked.Any(p => !p.IsDir))
        {
            ListMenu.Items.Add(Item("Download…", () => model.Download(picked)));
            ListMenu.Items.Add(new Separator());
        }
        ListMenu.Items.Add(Item("Delete", async () => await model.DeleteAsync(picked)));
    }

    private static MenuItem Item(string title, Action handler)
    {
        var item = new MenuItem { Header = title };
        item.Click += (_, _) => handler();
        return item;
    }
}
