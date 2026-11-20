using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using Toolbox.Model;

namespace Toolbox.Files;

/// <summary>
/// What the file browser needs from a session. The core's SFTP and FTP
/// sessions expose identical methods; these two adapters let the browser
/// drive either.
/// </summary>
internal interface IFileTransferSession
{
    Task<string> Home();
    Task<string> Canonicalize(string path);
    Task<SftpEntry[]> List(string path);
    Task<ulong> Download(string remote, string local, string transferId);
    Task<ulong> Upload(string local, string remote, string transferId);
    Task MakeDir(string path);
    Task Rename(string from, string to);
    Task Remove(string path, bool directory);
    Task Close();
}

internal sealed class SftpTransfer(SftpSession session) : IFileTransferSession
{
    public Task<string> Home() => session.Home();
    public Task<string> Canonicalize(string path) => session.Canonicalize(path);
    public Task<SftpEntry[]> List(string path) => session.List(path);
    public Task<ulong> Download(string remote, string local, string transferId) => session.Download(remote, local, transferId);
    public Task<ulong> Upload(string local, string remote, string transferId) => session.Upload(local, remote, transferId);
    public Task MakeDir(string path) => session.MakeDir(path);
    public Task Rename(string from, string to) => session.Rename(from, to);
    public Task Remove(string path, bool directory) => session.Remove(path, directory);
    public Task Close() => session.Close();
}

internal sealed class FtpTransfer(FtpSession session) : IFileTransferSession
{
    public Task<string> Home() => session.Home();
    public Task<string> Canonicalize(string path) => session.Canonicalize(path);
    public Task<SftpEntry[]> List(string path) => session.List(path);
    public Task<ulong> Download(string remote, string local, string transferId) => session.Download(remote, local, transferId);
    public Task<ulong> Upload(string local, string remote, string transferId) => session.Upload(local, remote, transferId);
    public Task MakeDir(string path) => session.MakeDir(path);
    public Task Rename(string from, string to) => session.Rename(from, to);
    public Task Remove(string path, bool directory) => session.Remove(path, directory);
    public Task Close() => session.Close();
}

/// <summary>A row of the remote directory listing.</summary>
internal sealed class FileEntry(SftpEntry entry)
{
    public SftpEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public bool IsDir => Entry.IsDir;
    public string Glyph => Entry.IsDir ? Icons.FolderFilled : Entry.IsSymlink ? Icons.Link : Icons.Document;
    public string Size => Entry.IsDir ? "—" : FileSizes.Format(Entry.Size);
    public string Modified => Entry.ModifiedEpoch is { } epoch
        ? DateTimeOffset.FromUnixTimeSeconds((long)epoch).LocalDateTime.ToString("g")
        : "";
    public string KindLabel => Entry.IsDir ? "folder" : Entry.IsSymlink ? "link" : "file";

    /// <summary>What a screen reader (and UI automation) calls the row.</summary>
    public override string ToString() => Entry.Name;
}

internal static class FileSizes
{
    public static string Format(ulong bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} bytes" : $"{value:0.#} {units[unit]}";
    }
}

/// <summary>One upload or download, shown under the listing while it runs.</summary>
internal sealed class TransferItem(string id, string name, bool upload, ulong total) : Observable
{
    private ulong done;
    private ulong total = total;
    private bool finished;
    private string? error;

    public string Id { get; } = id;
    public string Name { get; } = name;
    public bool Upload { get; } = upload;
    public string Glyph => Upload ? Icons.Upload : Icons.Download;

    public ulong Done { get => done; set { if (Set(ref done, value)) Refresh(); } }
    public ulong Total { get => total; set { if (Set(ref total, value)) Refresh(); } }
    public bool Finished { get => finished; set { if (Set(ref finished, value)) Refresh(); } }
    public string? Error { get => error; set { if (Set(ref error, value)) Refresh(); } }

    public bool IsRunning => !finished;
    public bool HasError => error is not null;
    public double Percent => total == 0 ? 0 : Math.Min(100, done * 100.0 / total);
    public string StatusText => error ?? (finished ? "Done" : FileSizes.Format(done));

    private void Refresh()
    {
        Changed(nameof(IsRunning));
        Changed(nameof(HasError));
        Changed(nameof(Percent));
        Changed(nameof(StatusText));
    }
}

/// <summary>
/// State for one file browser pane (SFTP, FTP or FTPS): the remote directory
/// being shown and the transfers in flight.
/// </summary>
internal sealed class FileBrowserModel : Observable
{
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private IFileTransferSession? session;
    private Bridge? bridge;
    private bool closed;
    private string path = "/";
    private bool busy;
    private bool isConnected;
    private string? status;

    public FileBrowserModel(Connection connection)
    {
        Connection = connection;
        Transfers.CollectionChanged += (_, _) =>
        {
            Changed(nameof(HasTransfers));
            Changed(nameof(HasFinished));
        };
    }

    public Connection Connection { get; }
    public ObservableCollection<FileEntry> Entries { get; } = [];
    public ObservableCollection<TransferItem> Transfers { get; } = [];

    public string Path { get => path; private set { if (Set(ref path, value)) Changed(nameof(CanGoUp)); } }
    public bool Busy { get => busy; private set => Set(ref busy, value); }

    public bool IsConnected
    {
        get => isConnected;
        private set
        {
            if (!Set(ref isConnected, value)) return;
            Changed(nameof(IsDisconnected));
            Changed(nameof(CanGoUp));
            Changed(nameof(ShowsError));
            Changed(nameof(Placeholder));
            ConnectedChanged?.Invoke();
        }
    }

    public string? Status
    {
        get => status;
        set
        {
            if (!Set(ref status, value)) return;
            Changed(nameof(ShowsError));
            Changed(nameof(Placeholder));
        }
    }

    public event Action? ConnectedChanged;

    public bool IsDisconnected => !isConnected;
    public bool CanGoUp => isConnected && path != "/";
    public bool HasTransfers => Transfers.Count > 0;
    public bool HasFinished => Transfers.Any(t => t.Finished);

    /// <summary>A problem to show under the listing while connected.</summary>
    public bool ShowsError => isConnected && status is not null;

    /// <summary>What the empty pane says while there is no listing.</summary>
    public string Placeholder => status ?? "Not connected";

    // ----- Session -----

    public async Task ConnectAsync(string username, SshAuth auth)
    {
        Status = $"Connecting to {username}@{Connection.Host}…";
        Busy = true;
        try
        {
            bridge = new Bridge(this);
            var config = new SshConfig(
                Host: Connection.Host,
                Port: Connection.Port,
                Username: username,
                Auth: auth,
                Term: "xterm-256color",
                Cols: 80,
                Rows: 24,
                ConnectTimeoutSecs: 15,
                KnownHostsPath: AppPaths.KnownHostsPath);
            Log.Info($"sftp: connecting to {username}@{Connection.Host}:{Connection.Port} ({Connection.Name})");
            var connected = await SftpSession.Connect(config, bridge);
            await AttachAsync(new SftpTransfer(connected));
        }
        catch (Exception e)
        {
            Log.Warn($"sftp: {Connection.Name}: {Errors.Describe(e)}");
            Status = Errors.Describe(e);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>
    /// FTP, or explicit FTPS when <paramref name="secure"/>. Plain FTP sends the
    /// password unencrypted, which the status line says while connecting.
    /// </summary>
    public async Task ConnectFtpAsync(string username, string password, bool secure)
    {
        Status = secure
            ? $"Connecting to {username}@{Connection.Host} over FTPS…"
            : $"Connecting to {username}@{Connection.Host} over plain FTP (not encrypted)…";
        Busy = true;
        try
        {
            bridge = new Bridge(this);
            var config = new FtpConfig(
                Host: Connection.Host,
                Port: Connection.Port,
                Username: username,
                Password: password,
                Secure: secure,
                AcceptInvalidCerts: false,
                ConnectTimeoutSecs: 15);
            Log.Info($"{(secure ? "ftps" : "ftp")}: connecting to {username}@{Connection.Host}:{Connection.Port} ({Connection.Name})");
            var connected = await FtpSession.Connect(config, bridge);
            await AttachAsync(new FtpTransfer(connected));
        }
        catch (Exception e)
        {
            Log.Warn($"ftp: {Connection.Name}: {Errors.Describe(e)}");
            Status = Errors.Describe(e);
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task AttachAsync(IFileTransferSession connected)
    {
        if (closed)
        {
            await connected.Close();
            return;
        }
        session = connected;
        IsConnected = true;
        Status = null;
        Log.Info($"files: connected ({Connection.Name})");
        try
        {
            await LoadAsync(await connected.Home());
        }
        catch (Exception e)
        {
            Status = Errors.Describe(e);
        }
    }

    public void Disconnect()
    {
        closed = true;
        bridge?.Detach();
        IsConnected = false;
        var ending = session;
        session = null;
        if (ending is not null) _ = CloseQuietly(ending);
    }

    private static async Task CloseQuietly(IFileTransferSession ending)
    {
        try { await ending.Close(); }
        catch (Exception e) { Log.Warn($"files: closing the session failed: {e.Message}"); }
    }

    // ----- Navigation -----

    public async Task LoadAsync(string newPath)
    {
        if (session is not { } current) return;
        Busy = true;
        try
        {
            var resolved = await current.Canonicalize(newPath);
            var listing = await current.List(resolved);
            Path = resolved;
            Entries.Clear();
            foreach (var entry in listing) Entries.Add(new FileEntry(entry));
            Status = null;
        }
        catch (Exception e)
        {
            Status = Errors.Describe(e);
        }
        finally
        {
            Busy = false;
        }
    }

    public Task RefreshAsync() => LoadAsync(path);

    public Task GoUpAsync() => path == "/" ? Task.CompletedTask : LoadAsync(RemotePath.Parent(path));

    public async Task GoHomeAsync()
    {
        if (session is not { } current) return;
        try { await LoadAsync(await current.Home()); }
        catch (Exception e) { Status = Errors.Describe(e); }
    }

    public void Open(FileEntry entry)
    {
        if (entry.IsDir) _ = LoadAsync(entry.Entry.Path);
        else Download([entry]);
    }

    // ----- Transfers -----

    /// <summary>Downloads files into a folder the user picks.</summary>
    public void Download(IReadOnlyList<FileEntry> selected)
    {
        var files = selected.Where(e => !e.IsDir).ToList();
        if (files.Count == 0 || session is not { } current) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = files.Count == 1 ? $"Save {files[0].Name} to" : $"Save {files.Count} files to",
        };
        if (dialog.ShowDialog(Prompts.Owner()) != true) return;
        var folder = dialog.FolderName;
        foreach (var file in files)
        {
            // Only ever write a plain file name inside the chosen folder. The
            // name comes from the server and is not trusted.
            if (!LocalNames.IsSafe(file.Name))
            {
                Status = $"“{file.Name}” can't be saved under that name on Windows.";
                continue;
            }
            var local = System.IO.Path.Combine(folder, file.Name);
            if (File.Exists(local) &&
                !Prompts.Confirm($"Replace {file.Name}?",
                                 $"A file with that name already exists in {System.IO.Path.GetFileName(folder)}.", "Replace"))
            {
                continue;
            }
            var transfer = new TransferItem(CoreApi.NewId(), file.Name, upload: false, file.Entry.Size);
            Transfers.Add(transfer);
            _ = RunAsync(transfer, () => current.Download(file.Entry.Path, local, transfer.Id), refresh: false);
        }
    }

    /// <summary>Uploads files the user picks into the current directory.</summary>
    public void Upload()
    {
        if (session is null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Upload to {path}",
            Multiselect = true,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(Prompts.Owner()) != true) return;
        UploadFiles(dialog.FileNames);
    }

    /// <summary>Uploads local files into the current directory, asking before replacing.</summary>
    public void UploadFiles(IReadOnlyList<string> localPaths)
    {
        if (session is not { } current) return;
        var destination = path;
        foreach (var local in localPaths)
        {
            var name = System.IO.Path.GetFileName(local);
            if (Entries.Any(e => e.Name == name) &&
                !Prompts.Confirm($"Replace {name}?", $"A file with that name already exists in {destination}.", "Replace"))
            {
                continue;
            }
            ulong size = 0;
            try { size = (ulong)new FileInfo(local).Length; } catch (Exception) { /* reported by the transfer */ }
            var transfer = new TransferItem(CoreApi.NewId(), name, upload: true, size);
            Transfers.Add(transfer);
            _ = RunAsync(transfer, () => current.Upload(local, RemotePath.Join(destination, name), transfer.Id), refresh: true);
        }
    }

    private async Task RunAsync(TransferItem transfer, Func<Task<ulong>> work, bool refresh)
    {
        try
        {
            await work();
            transfer.Done = Math.Max(transfer.Done, transfer.Total);
            transfer.Finished = true;
            Log.Info($"files: {(transfer.Upload ? "uploaded" : "downloaded")} {transfer.Name} ({Connection.Name})");
            Changed(nameof(HasFinished));
            if (refresh) await RefreshAsync();
            // Keep a finished transfer visible briefly, then tidy the list.
            await Task.Delay(TimeSpan.FromSeconds(4));
            if (transfer.Error is null) Transfers.Remove(transfer);
        }
        catch (Exception e)
        {
            transfer.Error = Errors.Describe(e);
            transfer.Finished = true;
            Log.Warn($"files: transfer of {transfer.Name} failed: {transfer.Error}");
            Changed(nameof(HasFinished));
        }
    }

    private void UpdateProgress(string id, ulong done, ulong total)
    {
        var transfer = Transfers.FirstOrDefault(t => t.Id == id);
        if (transfer is null || transfer.Finished) return;
        transfer.Done = done;
        if (total > 0) transfer.Total = total;
    }

    public void ClearFinishedTransfers()
    {
        foreach (var finished in Transfers.Where(t => t.Finished).ToList()) Transfers.Remove(finished);
    }

    // ----- Management -----

    public async Task NewFolderAsync()
    {
        if (session is not { } current) return;
        var name = Prompts.AskText("New Folder", $"Create in {path}", "untitled folder", "Create");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            await current.MakeDir(RemotePath.Join(path, name.Trim()));
            await RefreshAsync();
        }
        catch (Exception e)
        {
            Status = Errors.Describe(e);
        }
    }

    public async Task RenameAsync(FileEntry entry)
    {
        if (session is not { } current) return;
        var name = Prompts.AskText("Rename", entry.Entry.Path, entry.Name, "Rename");
        if (string.IsNullOrWhiteSpace(name) || name == entry.Name) return;
        try
        {
            await current.Rename(entry.Entry.Path, RemotePath.Join(RemotePath.Parent(entry.Entry.Path), name.Trim()));
            await RefreshAsync();
        }
        catch (Exception e)
        {
            Status = Errors.Describe(e);
        }
    }

    public async Task DeleteAsync(IReadOnlyList<FileEntry> selected)
    {
        if (session is not { } current || selected.Count == 0) return;
        var what = selected.Count == 1 ? selected[0].Name : $"{selected.Count} items";
        if (!Prompts.Confirm($"Delete {what}?", "This removes them from the server. Folders must be empty.", "Delete"))
        {
            return;
        }
        string? problem = null;
        foreach (var entry in selected)
        {
            try { await current.Remove(entry.Entry.Path, entry.IsDir); }
            catch (Exception e) { problem = Errors.Describe(e); }
        }
        await RefreshAsync();
        if (problem is not null) Status = problem;
    }

    /// <summary>Core callbacks arrive on the core's threads.</summary>
    private sealed class Bridge(FileBrowserModel model) : SftpListener, FtpListener
    {
        private readonly object gate = new();
        private FileBrowserModel? owner = model;
        private readonly Dispatcher dispatcher = model.dispatcher;

        public void Detach()
        {
            lock (gate) owner = null;
        }

        private bool IsAttached
        {
            get { lock (gate) return owner is not null; }
        }

        public bool ConfirmHostKey(string host, ushort port, string keyType, string fingerprint) =>
            IsAttached && dispatcher.Invoke(() => Prompts.ConfirmHostKey(host, port, keyType, fingerprint));

        public bool ConfirmUntrustedCertificate(string host, string reason) =>
            IsAttached && dispatcher.Invoke(() => Prompts.Confirm(
                $"The certificate for {host} is not trusted",
                $"{reason}\n\nThe connection would be encrypted, but Toolbox cannot confirm it is talking to {host}. " +
                "Continue only if you expect this server to use a self-signed or private certificate.",
                "Connect Anyway", cancelIsDefault: true));

        public void OnProgress(string transferId, ulong done, ulong total) => dispatcher.BeginInvoke(() =>
        {
            FileBrowserModel? target;
            lock (gate) target = owner;
            target?.UpdateProgress(transferId, done, total);
        });
    }
}

/// <summary>
/// Whether a file name a server reported can be used, exactly as it is, for
/// a file in a folder on this PC. Anything Windows would read as more than a
/// plain name is refused instead of being quietly changed into something
/// else: path separators and drive or stream colons, the names Windows
/// reserves for devices (a download called COM1 would be written to the
/// serial port), and names ending in a dot or a space, which Windows trims
/// and so could land on another existing file.
/// </summary>
internal static class LocalNames
{
    private static readonly HashSet<string> Devices = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    public static bool IsSafe(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Length > 255) return false;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        if (name[^1] is '.' or ' ') return false;
        // "NUL", "nul.txt" and "COM1.tar.gz" all name the device.
        var stem = name.Split('.')[0].TrimEnd(' ');
        return !Devices.Contains(stem);
    }
}

/// <summary>Remote paths are POSIX-style whatever the server runs.</summary>
internal static class RemotePath
{
    public static string Parent(string path)
    {
        var trimmed = path.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index <= 0 ? "/" : trimmed[..index];
    }

    public static string Join(string directory, string name) =>
        directory.EndsWith('/') ? directory + name : directory + "/" + name;
}
