using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Toolbox.Workspaces;

namespace Toolbox.Terminal;

/// <summary>
/// A terminal on this PC: a window of the user's own Windows Terminal, shown
/// inside the pane.
///
/// The owner chose this over running a shell in the app's own terminal
/// (2026-11-25): it is their Terminal, with its profiles, colours, fonts and
/// key bindings, and none of the app's themes, text size or find bar apply
/// to it. What follows from that:
///  - While it has the keyboard, every key is Windows Terminal's, the app's
///    own shortcuts included.
///  - The pane knows the window's title and whether it still exists. What
///    runs in it is read from the system's process list, not from the
///    terminal.
///  - Closing asks the window to close and waits for it. A window that will
///    not close (Terminal asks about something) is handed back to the
///    desktop as a window of its own; it is never destroyed from here.
/// </summary>
internal sealed class LocalTerminalPane : Grid, IPaneContent
{
    private static readonly List<LocalTerminalPane> Closing = [];

    private readonly EmbeddedWindowHost host = new();
    private readonly TextBlock status = new();
    private readonly DispatcherTimer watch = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly string? profile;
    private readonly Stopwatch age = new();
    private string title;
    private int? shellProcess;
    private bool started;
    private bool isLive;
    private bool terminated;
    private bool hadKeyboard;

    // ----- IPaneContent -----

    public FrameworkElement View => this;
    public Connection? Connection => null;
    public string Title => title;
    public bool IsLive => isLive;
    public event Action? TitleChanged;
    public event Action? StateChanged;
    public event Action? FocusRequested;
    public event Action<bool>? SessionEnded;

    /// <summary>
    /// An idle shell closes without a question, as a Terminal window does; a
    /// program it started is named first. Where the shell could not be told
    /// apart from Terminal's other ones, closing asks.
    /// </summary>
    public string? CloseWarning
    {
        get
        {
            if (!isLive) return null;
            if (shellProcess is not { } shell) return "The terminal will be closed.";
            return WindowsTerminal.RunningIn(shell) is { } program ? $"“{program}” is still running in this terminal." : null;
        }
    }

    /// <param name="profile">A Windows Terminal profile by name; null for its default one.</param>
    public LocalTerminalPane(string? profile = null)
    {
        this.profile = profile;
        title = profile ?? "Terminal";
        Background = Brushes.Black;
        AutomationProperties.SetAutomationId(this, "LocalTerminalPane");

        // The window is another program's: WPF cannot draw over it, so it is
        // only shown once there is a terminal in it, and the status text is
        // shown the rest of the time.
        host.Visibility = Visibility.Hidden;
        Children.Add(host);

        status.TextAlignment = TextAlignment.Center;
        status.TextWrapping = TextWrapping.Wrap;
        status.FontSize = 13;
        status.Margin = new Thickness(24);
        status.HorizontalAlignment = HorizontalAlignment.Center;
        status.VerticalAlignment = VerticalAlignment.Center;
        status.Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0xB8, 0xB8));
        AutomationProperties.SetAutomationId(status, "LocalTerminalStatus");
        Children.Add(status);

        watch.Tick += (_, _) => Watch();
        SetStatus("Starting Windows Terminal…");
        // The pane's own window exists once it is part of the app's window.
        Loaded += (_, _) => Start();
    }

    private async void Start()
    {
        if (started || terminated) return;
        started = true;
        if (WindowsTerminal.Executable is null)
        {
            Fail("Windows Terminal isn't installed on this PC.\n\nLocal terminals are windows of Windows Terminal shown here. " +
                 "It comes with Windows 11 and is in the Microsoft Store for Windows 10.");
            return;
        }
        try
        {
            Log.Info($"local terminal: asking Windows Terminal for a window ({profile ?? "default profile"})");
            var opened = await WindowsTerminal.OpenWindowAsync(profile, CancellationToken.None);
            if (opened is not { } window)
            {
                Fail("Windows Terminal did not open a window.");
                return;
            }
            if (terminated)
            {
                // Closed while it was starting: the window goes too.
                CloseUnadopted(window.Handle);
                return;
            }
            if (!host.Adopt(window.Handle))
            {
                Fail("Windows Terminal's window could not be moved into this pane; it is open as a window of its own.");
                return;
            }
            shellProcess = window.ShellProcessId;
            // Written down, so that a later run can end it if this one is killed (see TerminalLedger).
            if (shellProcess is { } shell) TerminalLedger.Add(shell);
            age.Restart();
            host.Visibility = Visibility.Visible;
            SetStatus("");
            Log.Info($"local terminal: window in the pane (Terminal process {window.ProcessId}, shell {shellProcess?.ToString() ?? "not identified"})");
            watch.Start();
            SetLive(true);
            Watch();
        }
        catch (OperationCanceledException)
        {
            // Closed while it was starting.
        }
        catch (Exception e)
        {
            Log.Error("local terminal: could not start", e);
            Fail("The terminal could not start: " + e.Message);
        }
    }

    private static void CloseUnadopted(nint window)
    {
        const uint wmClose = 0x0010;
        PostMessage(window, wmClose, 0, 0);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    public void FocusSession()
    {
        if (isLive) host.FocusGuest();
        else Focus();
    }

    /// <summary>
    /// Asks the terminal to close and gives it a moment. One that is still
    /// there afterwards (Terminal is asking its own question) becomes a
    /// window of its own again.
    /// </summary>
    public void Terminate()
    {
        if (terminated) return;
        terminated = true;
        watch.Stop();
        var wasLive = isLive;
        SetLive(false);
        if (!wasLive || host.Guest == 0)
        {
            ForgetShell();
            DisposeHost();
            return;
        }
        Log.Info($"local terminal: closing ({title})");
        host.RequestClose();
        Closing.Add(this);
        var waited = Stopwatch.StartNew();
        var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        poll.Tick += (_, _) =>
        {
            if (host.Guest != 0 && waited.Elapsed < TimeSpan.FromSeconds(2.5)) return;
            poll.Stop();
            FinishClosing();
        };
        poll.Start();
    }

    private void FinishClosing()
    {
        if (!Closing.Remove(this)) return;
        if (host.Guest != 0)
        {
            Log.Warn($"local terminal: the window did not close; it is a window of its own again ({title})");
            host.Release();
        }
        // Closed, or the user's own window again: either way no longer this app's to end.
        ForgetShell();
        DisposeHost();
    }

    private void ForgetShell()
    {
        if (shellProcess is { } shell) TerminalLedger.Remove(shell);
    }

    private void DisposeHost()
    {
        try { host.Dispose(); }
        catch (Exception e) { Log.Warn($"local terminal: closing the pane's window failed: {e.Message}"); }
    }

    /// <summary>
    /// Before the app goes: gives terminals that were asked to close a
    /// moment to do it, so they are not cut off mid-way, and lets go of any
    /// that are still there. Messages are handled while waiting, because a
    /// closing child window tells its parent.
    /// </summary>
    public static void FinishAll(TimeSpan patience)
    {
        if (Closing.Count == 0) return;
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < patience && Closing.Any(pane => pane.host.Guest != 0))
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(15);
        }
        foreach (var pane in Closing.ToList()) pane.FinishClosing();
    }

    // ----- Watching the window -----

    /// <summary>Follows what cannot be asked for: the window's end, its title, and whether it has the keyboard.</summary>
    private void Watch()
    {
        if (terminated || !isLive) return;
        if (host.Guest == 0)
        {
            Ended();
            return;
        }
        var text = TerminalPane.CleanTitle(host.GuestTitle());
        if (text.Length == 0) text = profile ?? "Terminal";
        if (text != title)
        {
            title = text;
            TitleChanged?.Invoke();
        }
        var has = host.GuestHasFocus();
        if (has && !hadKeyboard) FocusRequested?.Invoke();
        hadKeyboard = has;
    }

    private void Ended()
    {
        watch.Stop();
        ForgetShell();
        SetLive(false);
        host.Visibility = Visibility.Hidden;
        // A window that was used and left is a clean end; one that went
        // right after it came is something to read about.
        if (age.Elapsed >= TimeSpan.FromSeconds(1.5))
        {
            Log.Info($"local terminal: ended ({title})");
            SetStatus("Session ended");
            SessionEnded?.Invoke(true);
        }
        else
        {
            Log.Warn($"local terminal: the window closed right after opening ({title})");
            SetStatus("Windows Terminal closed the window right after opening it." +
                      (profile is null ? "" : $"\n\nProfile: {profile}"));
            SessionEnded?.Invoke(false);
        }
    }

    private void Fail(string message)
    {
        Log.Warn("local terminal: " + message.ReplaceLineEndings(" "));
        host.Visibility = Visibility.Collapsed;
        SetStatus(message);
        SetLive(false);
    }

    private void SetStatus(string text)
    {
        status.Text = text;
        status.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetLive(bool value)
    {
        if (isLive == value) return;
        isLive = value;
        StateChanged?.Invoke();
    }

    /// <summary>
    /// To accessibility tools the pane is one element; Terminal's window is
    /// reachable through its own handle (see <see cref="NativePanePeer"/>
    /// for why it is not part of this tree).
    /// </summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new NativePanePeer(this, () => "Local terminal: " + title);
}
