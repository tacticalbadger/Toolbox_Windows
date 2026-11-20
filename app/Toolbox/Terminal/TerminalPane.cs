using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Toolbox.Workspaces;

namespace Toolbox.Terminal;

/// <summary>One WebView2 environment (browser process and profile folder) for every terminal.</summary>
internal static class TerminalEnvironment
{
    private static Task<CoreWebView2Environment>? shared;

    public static Task<CoreWebView2Environment> GetAsync() =>
        shared ??= CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewDataDirectory);
}

/// <summary>
/// A terminal pane bound to one SSH session from the Rust core. The terminal
/// itself is xterm.js in a WebView2 that only ever loads the page shipped
/// with the app.
/// </summary>
internal sealed class TerminalPane : Grid, IPaneContent
{
    // A name under the reserved .example domain: it exists only as this
    // WebView2's mapping onto the app's own folder.
    private const string Host = "toolbox-terminal.example";
    private const string Origin = "https://" + Host + "/";

    public Connection Connection { get; }

    private readonly WebView2 web = new();
    private readonly TextBlock fallback = new();
    private readonly List<string> queued = [];
    private readonly DispatcherTimer resizeTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private SshSession? session;
    private SessionBridge? bridge;
    private SshAuth? auth;
    private string username;
    private string title;
    private int cols = 80;
    private int rows = 24;
    private bool isLive;
    private bool pageReady;
    private bool startPending;
    private bool terminated;
    private bool reconnectPending;
    private bool askedForPassphrase;

    // ----- IPaneContent -----

    public FrameworkElement View => this;
    public string Title => title;
    public bool IsLive => isLive;
    public event Action? TitleChanged;
    public event Action? StateChanged;
    public event Action? FocusRequested;
    public event Action<bool>? SessionEnded;

    /// <summary>
    /// Creates the pane in a "resolving" state; call <see cref="Start"/> once
    /// the credentials are known.
    /// </summary>
    public TerminalPane(Connection connection)
    {
        Connection = connection;
        username = connection.Username;
        title = connection.Name;
        AutomationProperties.SetAutomationId(this, "TerminalPane");

        var theme = ThemeStore.Shared.Current;
        web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(
            255, (int)(theme.Background >> 16) & 0xFF, (int)(theme.Background >> 8) & 0xFF, (int)theme.Background & 0xFF);
        Children.Add(web);

        fallback.Visibility = Visibility.Collapsed;
        fallback.TextWrapping = TextWrapping.Wrap;
        fallback.Margin = new Thickness(20);
        fallback.VerticalAlignment = VerticalAlignment.Center;
        fallback.HorizontalAlignment = HorizontalAlignment.Center;
        Children.Add(fallback);

        resizeTimer.Tick += (_, _) =>
        {
            resizeTimer.Stop();
            session?.Resize((ushort)cols, (ushort)rows);
        };
        ThemeStore.Shared.Changed += OnThemeChanged;
        Feed($"\u001b[2mResolving credentials for {connection.Name}…\u001b[0m\r\n");
        _ = InitializeAsync();
    }

    /// <summary>
    /// Begins the SSH session with resolved credentials. Ignored once the
    /// pane has been closed, so a slow prompt can't leave a zombie session.
    /// </summary>
    public void Start(string user, SshAuth authentication)
    {
        if (terminated) return;
        username = user;
        auth = authentication;
        // The page reports the real size when it is ready; connect then.
        if (pageReady) Connect();
        else startPending = true;
    }

    /// <summary>The user declined a prompt while resolving credentials.</summary>
    public void Cancelled()
    {
        Feed("\u001b[2m[Cancelled]\u001b[0m\r\n");
        SetLive(false);
    }

    /// <summary>Credentials could not be resolved.</summary>
    public void Failed(string message)
    {
        Feed($"\u001b[31m{message}\u001b[0m\r\n");
        SetLive(false);
    }

    public void FocusSession()
    {
        if (terminated) return;
        web.Focus();
        Post("{\"t\":\"focus\"}");
    }

    public void Terminate()
    {
        if (terminated) return;
        terminated = true;
        resizeTimer.Stop();
        ThemeStore.Shared.Changed -= OnThemeChanged;
        bridge?.Detach();
        bridge = null;
        session?.Close();
        session = null;
        SetLive(false);
        try { web.Dispose(); }
        catch (Exception e) { Log.Warn($"terminal: closing the web view failed: {e.Message}"); }
    }

    // ----- The page -----

    private async Task InitializeAsync()
    {
        try
        {
            var environment = await TerminalEnvironment.GetAsync();
            await web.EnsureCoreWebView2Async(environment);
            if (terminated) return;
            var core = web.CoreWebView2;
            var settings = core.Settings;
            settings.AreDevToolsEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.AreHostObjectsAllowed = false;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = false;
            settings.IsPinchZoomEnabled = false;
            settings.IsSwipeNavigationEnabled = false;
            settings.IsBuiltInErrorPageEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.IsReputationCheckingRequired = false;

            // The page and xterm.js are files next to the executable, served under
            // a name that resolves nowhere else. Nothing else may be loaded.
            core.SetVirtualHostNameToFolderMapping(Host, AppPaths.TerminalAssets, CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += (_, e) =>
            {
                if (!e.Uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
            };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.WebMessageReceived += OnWebMessage;
            core.ProcessFailed += (_, e) =>
            {
                Log.Error($"terminal: the web view process failed ({e.ProcessFailedKind})");
                ShowFallback("The terminal's renderer stopped unexpectedly. Close this pane and connect again.");
            };
            core.Navigate(Origin + "terminal.html");
        }
        catch (Exception e)
        {
            Log.Error("terminal: WebView2 could not start", e);
            ShowFallback("The terminal could not start: " + e.Message +
                         "\n\nToolbox needs the Microsoft Edge WebView2 Runtime, which is part of Windows 11 " +
                         "and available from Microsoft for Windows 10.");
        }
    }

    private void ShowFallback(string message)
    {
        if (terminated) return;
        fallback.Text = message;
        fallback.Visibility = Visibility.Visible;
        web.Visibility = Visibility.Collapsed;
    }

    /// <summary>Sends a message to the page, or keeps it until the page is up.</summary>
    private void Post(string json)
    {
        if (terminated) return;
        if (!pageReady)
        {
            queued.Add(json);
            return;
        }
        try { web.CoreWebView2.PostWebMessageAsJson(json); }
        catch (Exception e) { Log.Warn($"terminal: message to the page failed: {e.Message}"); }
    }

    private void Feed(string text) => Feed(Encoding.UTF8.GetBytes(text));

    private void Feed(byte[] bytes) =>
        Post("{\"t\":\"o\",\"d\":\"" + Convert.ToBase64String(bytes) + "\"}");

    private void OnThemeChanged() => Post(ThemeStore.Shared.ToMessage());

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (terminated || !e.Source.StartsWith(Origin, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var message = document.RootElement;
            switch (message.GetProperty("t").GetString())
            {
                case "i":
                    session?.Write(Encoding.UTF8.GetBytes(message.GetProperty("d").GetString() ?? ""));
                    break;
                case "b":
                    // Raw bytes carried one per character (mouse reports).
                    session?.Write(Encoding.Latin1.GetBytes(message.GetProperty("d").GetString() ?? ""));
                    break;
                case "size":
                    OnSize(message.GetProperty("cols").GetInt32(), message.GetProperty("rows").GetInt32());
                    break;
                case "ready":
                    OnPageReady(message.GetProperty("cols").GetInt32(), message.GetProperty("rows").GetInt32());
                    break;
                case "title":
                    var text = CleanTitle(message.GetProperty("d").GetString());
                    SetTitle(text.Length == 0 ? Connection.Name : $"{Connection.Name} · {text}");
                    break;
                case "focus":
                    FocusRequested?.Invoke();
                    break;
                case "bell":
                    System.Media.SystemSounds.Beep.Play();
                    break;
                case "copy":
                    Copy(message.GetProperty("d").GetString());
                    break;
                case "paste":
                    Paste();
                    break;
                case "link":
                    OpenLink(message.GetProperty("d").GetString());
                    break;
            }
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Warn($"terminal: unreadable message from the page: {error.Message}");
        }
    }

    private void OnPageReady(int newCols, int newRows)
    {
        cols = Math.Max(newCols, 2);
        rows = Math.Max(newRows, 1);
        pageReady = true;
        Post(ThemeStore.Shared.ToMessage());
        foreach (var json in queued) Post(json);
        queued.Clear();
        if (startPending)
        {
            startPending = false;
            Connect();
        }
    }

    private void OnSize(int newCols, int newRows)
    {
        cols = Math.Max(newCols, 2);
        rows = Math.Max(newRows, 1);
        // Layout passes fire this many times in a row; send the settled size.
        resizeTimer.Stop();
        resizeTimer.Start();
    }

    private static void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); }
        catch (Exception e) { Log.Warn($"terminal: copy failed: {e.Message}"); }
    }

    private void Paste()
    {
        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
        catch (Exception e)
        {
            Log.Warn($"terminal: paste failed: {e.Message}");
            return;
        }
        if (text.Length == 0) return;
        Post(JsonSerializer.Serialize(new { t = "paste", d = text }));
    }

    /// <summary>Opens a link from terminal output in the default browser: web and mail links only.</summary>
    private static void OpenLink(string? link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme is not ("http" or "https" or "mailto")) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception e) { Log.Warn($"terminal: could not open the link: {e.Message}"); }
    }

    private const int MaxTitleLength = 200;

    /// <summary>
    /// A title the remote program set, made fit for a pane header: it is the
    /// remote side's to choose, so it is kept to one short line. (A terminal
    /// accepts titles of megabytes, which would stall the window.)
    /// </summary>
    internal static string CleanTitle(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var builder = new StringBuilder(Math.Min(text.Length, MaxTitleLength));
        foreach (var c in text)
        {
            if (builder.Length >= MaxTitleLength) break;
            builder.Append(char.IsControl(c) ? ' ' : c);
        }
        return builder.ToString().Trim();
    }

    private void SetTitle(string value)
    {
        if (title == value) return;
        title = value;
        TitleChanged?.Invoke();
    }

    private void SetLive(bool value)
    {
        if (isLive == value) return;
        isLive = value;
        StateChanged?.Invoke();
    }

    // ----- Session lifecycle -----

    private void Connect()
    {
        if (auth is null || terminated) return;
        SetLive(false);
        Feed($"\u001b[2mConnecting to {username}@{Connection.Host}:{Connection.Port}…\u001b[0m\r\n");
        var listener = new SessionBridge(this);
        bridge = listener;
        var config = new SshConfig(
            Host: Connection.Host,
            Port: Connection.Port,
            Username: username,
            Auth: auth,
            Term: "xterm-256color",
            Cols: (ushort)cols,
            Rows: (ushort)rows,
            ConnectTimeoutSecs: 15,
            // Normally %USERPROFILE%\.ssh\known_hosts, shared with OpenSSH. Overridable for tests.
            KnownHostsPath: AppPaths.KnownHostsPath);
        Log.Info($"ssh: connecting to {username}@{Connection.Host}:{Connection.Port} ({Connection.Name})");
        session = SshSession.Connect(config, listener);
    }

    private void HandleConnected()
    {
        Log.Info($"ssh: connected ({Connection.Name})");
        SetLive(true);
    }

    private void HandleError(string message)
    {
        Log.Warn($"ssh: {Connection.Name}: {message}");
        if (!askedForPassphrase && auth is SshAuth.KeyFile { Passphrase: null } key && message.Contains("cannot load key"))
        {
            askedForPassphrase = true;
            var secret = Prompts.AskSecret($"Passphrase for {key.Path}", "The private key is encrypted.");
            if (secret is not null)
            {
                auth = new SshAuth.KeyFile(key.Path, secret);
                reconnectPending = true;
                return;
            }
        }
        Feed($"\r\n\u001b[31m{message}\u001b[0m\r\n");
    }

    private void HandleClosed(bool clean)
    {
        SetLive(false);
        session = null;
        if (terminated) return;
        if (reconnectPending)
        {
            reconnectPending = false;
            Connect();
        }
        else if (clean)
        {
            Log.Info($"ssh: session ended ({Connection.Name})");
            Feed("\r\n\u001b[2m[Session ended]\u001b[0m\r\n");
            SessionEnded?.Invoke(true);
        }
        else
        {
            Log.Info($"ssh: disconnected ({Connection.Name})");
            Feed("\r\n\u001b[2m[Disconnected]\u001b[0m\r\n");
            SessionEnded?.Invoke(false);
        }
    }

    /// <summary>
    /// Receives core callbacks on the core's threads, coalesces output, and
    /// hands everything to the pane on the UI thread in order.
    /// </summary>
    private sealed class SessionBridge(TerminalPane pane) : SshListener
    {
        private readonly object gate = new();
        private readonly Dispatcher dispatcher = pane.Dispatcher;
        private TerminalPane? owner = pane;
        private MemoryStream pending = new();
        private bool flushScheduled;

        public void Detach()
        {
            lock (gate) owner = null;
        }

        private void OnUi(Action<TerminalPane> work) => dispatcher.BeginInvoke(() =>
        {
            TerminalPane? target;
            lock (gate) target = owner;
            if (target is not null) work(target);
        });

        public void OnConnected() => OnUi(p => p.HandleConnected());

        public void OnData(byte[] data)
        {
            bool schedule;
            lock (gate)
            {
                pending.Write(data, 0, data.Length);
                schedule = !flushScheduled;
                flushScheduled = true;
            }
            if (schedule) dispatcher.BeginInvoke(Flush);
        }

        private void Flush()
        {
            byte[] chunk;
            TerminalPane? target;
            lock (gate)
            {
                chunk = pending.ToArray();
                pending = new MemoryStream();
                flushScheduled = false;
                target = owner;
            }
            if (chunk.Length > 0) target?.Feed(chunk);
        }

        public void OnError(string message) => OnUi(p => p.HandleError(message));

        public void OnClosed(bool clean) => OnUi(p => p.HandleClosed(clean));

        public bool ConfirmHostKey(string host, ushort port, string keyType, string fingerprint)
        {
            lock (gate)
            {
                if (owner is null) return false;
            }
            // The core waits on this thread for the answer; ask on the UI thread.
            return dispatcher.Invoke(() => Prompts.ConfirmHostKey(host, port, keyType, fingerprint));
        }
    }
}
