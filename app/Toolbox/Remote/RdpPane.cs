using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Media;
using System.Windows.Threading;
using Toolbox.Workspaces;
using WinForms = System.Windows.Forms;

namespace Toolbox.Remote;

/// <summary>
/// A remote desktop pane on Microsoft's own RDP client control. The control
/// does the protocol, the drawing and the input; this wrapper owns the
/// connection settings, keeps the remote desktop the size of the pane, and
/// reports how the session ended.
/// </summary>
internal sealed class RdpPane : Grid, IPaneContent
{
    public Connection Connection { get; }

    private readonly WindowsFormsHost host = new();
    private readonly TextBlock status = new();
    private readonly DispatcherTimer resizeTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private RdpClientHost? client;
    private dynamic? control;
    private RdpClientEventSink? events;
    private WinForms.AxHost.ConnectionPointCookie? cookie;
    private (int Width, int Height) asked;
    private string title;
    private bool isLive;
    private bool terminated;
    private bool closing;
    private string? lastError;

    // ----- IPaneContent -----

    public FrameworkElement View => this;
    public string Title => title;
    public bool IsLive => isLive;
    public event Action? TitleChanged { add { } remove { } }
    public event Action? StateChanged;
    public event Action? FocusRequested;
    public event Action<bool>? SessionEnded;

    public RdpPane(Connection connection)
    {
        Connection = connection;
        title = connection.Name;
        Background = Brushes.Black;
        AutomationProperties.SetAutomationId(this, "RdpPane");

        // The control is a native window: nothing of WPF can be drawn over it,
        // so it is only shown while a session is up and the status text is
        // shown the rest of the time.
        host.Visibility = Visibility.Collapsed;
        host.Background = Brushes.Black;
        Children.Add(host);

        status.TextAlignment = TextAlignment.Center;
        status.TextWrapping = TextWrapping.Wrap;
        status.FontSize = 13;
        status.Margin = new Thickness(24);
        status.HorizontalAlignment = HorizontalAlignment.Center;
        status.VerticalAlignment = VerticalAlignment.Center;
        status.Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0xB8, 0xB8));
        AutomationProperties.SetAutomationId(status, "RdpStatus");
        Children.Add(status);

        resizeTimer.Tick += (_, _) =>
        {
            resizeTimer.Stop();
            ResizeSession();
        };
        SizeChanged += (_, _) => ScheduleResize();
        SetStatus($"Resolving credentials for {connection.Name}…");
    }

    /// <summary>
    /// Connects with resolved credentials. <paramref name="username"/> is passed
    /// exactly as written (<c>DOMAIN\user</c> or <c>user@domain</c>); the control
    /// sorts out the domain the way mstsc does.
    /// </summary>
    public void Start(string username, string password)
    {
        if (terminated) return;
        try
        {
            client = new RdpClientHost
            {
                // The control paints its "Connecting…" text with these.
                BackColor = System.Drawing.Color.Black,
                ForeColor = System.Drawing.Color.FromArgb(0xB8, 0xB8, 0xB8),
            };
            ((ISupportInitialize)client).BeginInit();
            host.Child = client;
            ((ISupportInitialize)client).EndInit();
            client.Enter += (_, _) => FocusRequested?.Invoke();
            host.Visibility = Visibility.Visible;
            client.CreateControl();
            control = client.Control ?? throw new InvalidOperationException("the control did not start");

            events = new RdpClientEventSink
            {
                Connecting = () => Log.Info($"rdp: connecting to {Connection.Host}:{Connection.Port} ({Connection.Name})"),
                Connected = OnConnected,
                LoginComplete = OnLoginComplete,
                Disconnected = OnDisconnected,
                FatalError = code => lastError = $"The Remote Desktop client reported fatal error {code}.",
                LogonError = code => Log.Info($"rdp: logon notice {code} ({Connection.Name})"),
                DesktopSizeChanged = OnDesktopSizeChanged,
            };
            cookie = new WinForms.AxHost.ConnectionPointCookie(client.Control!, events, typeof(IRdpClientEvents));

            var size = PixelSize();
            asked = size;
            control.Server = Connection.Host;
            control.UserName = username;
            control.DesktopWidth = size.Width;
            control.DesktopHeight = size.Height;
            control.ColorDepth = 32;
            control.ConnectingText = $"Connecting to {Connection.Host}…";

            var advanced = control.AdvancedSettings9;
            advanced.RDPPort = (int)Connection.Port;
            advanced.EnableCredSspSupport = true;
            // 2 = show the standard warning when the server cannot be verified and let the user decide.
            advanced.AuthenticationLevel = 2;
            advanced.ClearTextPassword = password;
            advanced.SmartSizing = false;
            advanced.RedirectClipboard = true;
            // Local drives and printers stay private unless a connection asks for them (not offered yet).
            advanced.RedirectDrives = false;
            advanced.RedirectPrinters = false;
            advanced.GrabFocusOnConnect = false;
            advanced.EnableAutoReconnect = true;
            advanced.DisplayConnectionBar = false;
            ApplyScale();

            // The control paints its waiting screen white whatever colours it
            // is given, so it stays out of sight (but keeps its place and
            // size) until there is a desktop to show.
            host.Visibility = Visibility.Hidden;
            SetStatus($"Connecting to {Connection.Host}…");
            control.Connect();
        }
        catch (Exception e)
        {
            Log.Error($"rdp: could not start ({Connection.Name})", e);
            Failed("The Remote Desktop control could not start: " + e.Message);
        }
    }

    /// <summary>The user declined a prompt while resolving credentials.</summary>
    public void Cancelled() => SetStatus("Cancelled");

    /// <summary>Credentials could not be resolved, or the control could not start.</summary>
    public void Failed(string message)
    {
        host.Visibility = Visibility.Collapsed;
        SetStatus(message);
    }

    public void FocusSession()
    {
        if (client is not null && host.Visibility == Visibility.Visible)
        {
            host.Focus();
            client.Focus();
        }
        else
        {
            Focus();
        }
    }

    public void Terminate()
    {
        if (terminated) return;
        terminated = true;
        resizeTimer.Stop();
        SetLive(false);
        try
        {
            if (control is not null && (short)control.Connected != 0)
            {
                closing = true;
                control.Disconnect();
            }
        }
        catch (Exception e)
        {
            Log.Warn($"rdp: disconnect failed ({Connection.Name}): {e.Message}");
        }
        // Let the control finish its disconnect before it is destroyed.
        var doomedCookie = cookie;
        var doomedClient = client;
        var doomedHost = host;
        cookie = null;
        client = null;
        control = null;
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                doomedCookie?.Disconnect();
                doomedHost.Child = null;
                doomedClient?.Dispose();
                doomedHost.Dispose();
            }
            catch (Exception e)
            {
                Log.Warn($"rdp: closing the control failed: {e.Message}");
            }
        }, DispatcherPriority.Background);
    }

    // ----- Size -----

    /// <summary>
    /// The desktop size to ask for: this pane's size in device pixels, as the
    /// core's rule adjusts it (within RDP's limits, width a multiple of 4), so
    /// the size compared against is the size actually asked for.
    /// </summary>
    private (int Width, int Height) PixelSize()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = (uint)Math.Clamp(ActualWidth * dpi.DpiScaleX, 0, 16384);
        var height = (uint)Math.Clamp(ActualHeight * dpi.DpiScaleY, 0, 16384);
        var size = CoreApi.RdpDesktopSize(width, height);
        return (size.Width, size.Height);
    }

    /// <summary>The Windows display scale of this pane as a percentage (100, 150, 200…).</summary>
    private uint ScalePercent() =>
        (uint)Math.Clamp(Math.Round(VisualTreeHelper.GetDpi(this).DpiScaleX * 100), 100, 500);

    /// <summary>Tells the server the display scale, so the remote desktop is not drawn tiny on a dense screen.</summary>
    private void ApplyScale()
    {
        try
        {
            if (client?.Control is IRdpExtendedSettings extended)
            {
                object desktop = ScalePercent();
                extended.SetProperty("DesktopScaleFactor", ref desktop);
                object device = 100u;
                extended.SetProperty("DeviceScaleFactor", ref device);
            }
        }
        catch (Exception e)
        {
            Log.Warn($"rdp: display scale not applied ({Connection.Name}): {e.Message}");
        }
    }

    private void ScheduleResize()
    {
        if (!isLive || terminated) return;
        if (PixelSize() == asked) return;
        resizeTimer.Stop();
        resizeTimer.Start();
    }

    /// <summary>
    /// Brings the remote desktop to the pane's size without reconnecting
    /// (Display Control), falling back to a reconnect at the new size for
    /// servers that do not support it.
    /// </summary>
    private void ResizeSession()
    {
        if (!isLive || terminated || control is null) return;
        var size = PixelSize();
        if (size == asked) return;
        try
        {
            control.UpdateSessionDisplaySettings((uint)size.Width, (uint)size.Height, (uint)size.Width, (uint)size.Height,
                                                 0u, ScalePercent(), 100u);
            asked = size;
            resizeAttempts = 0;
            Log.Info($"rdp: desktop resized to {size.Width}x{size.Height} ({Connection.Name})");
        }
        catch (Exception first)
        {
            // Right after connecting the display channel may not be open yet:
            // ask again a few times before concluding the server lacks it.
            if (++resizeAttempts <= MaxResizeAttempts)
            {
                resizeTimer.Stop();
                resizeTimer.Start();
                return;
            }
            resizeAttempts = 0;
            asked = size;
            try
            {
                control.Reconnect((uint)size.Width, (uint)size.Height);
                Log.Info($"rdp: reconnected at {size.Width}x{size.Height} ({Connection.Name})");
            }
            catch (Exception second)
            {
                Log.Warn($"rdp: resize failed ({Connection.Name}): {first.Message}; {second.Message}");
            }
        }
    }

    private const int MaxResizeAttempts = 8;
    private int resizeAttempts;

    // ----- Events from the control (UI thread) -----

    private void OnConnected()
    {
        Log.Info($"rdp: connected ({Connection.Name})");
        lastError = null;
        if (terminated) return;
        host.Visibility = Visibility.Visible;
        SetStatus("");
        SetLive(true);
        SyncSize();
    }

    private void OnLoginComplete()
    {
        Log.Info($"rdp: signed in ({Connection.Name})");
        SyncSize();
    }

    /// <summary>
    /// Some servers ignore the size asked for at connect, and the pane may
    /// have changed size while connecting: once the session is up, bring the
    /// desktop to the pane's pixel size.
    /// </summary>
    private void SyncSize()
    {
        if (terminated || control is null) return;
        // Unless the server has confirmed the size the pane wants, ask (once
        // more) now. "asked" only changes when a request is sent, so a server
        // that answers with a different size is not asked over and over.
        if (remoteSize != PixelSize()) asked = (0, 0);
        ScheduleResize();
    }

    /// <summary>The desktop size the server last announced, if it has.</summary>
    private (int Width, int Height)? remoteSize;

    private void OnDesktopSizeChanged(int width, int height)
    {
        Log.Info($"rdp: remote desktop is {width}x{height} ({Connection.Name})");
        remoteSize = (width, height);
    }

    // What the control reports when it disconnects (MSDN: IMsTscAxEvents::OnDisconnected).
    private const int ReasonLocal = 1;
    private const int ReasonRemoteByUser = 2;
    private const int ReasonByServer = 3;

    // ExtendedDisconnectReasonCode values that are an ordinary end of a session.
    private const int ExtendedApiDisconnect = 1;
    private const int ExtendedApiLogoff = 2;
    private const int ExtendedRpcDisconnectByUser = 11;
    private const int ExtendedLogoffByUser = 12;

    private void OnDisconnected(int reason)
    {
        var extended = 0;
        var description = "";
        try
        {
            if (control is not null)
            {
                extended = (int)control.ExtendedDisconnectReason;
                description = (string)control.GetErrorDescription((uint)reason, (uint)extended) ?? "";
            }
        }
        catch (Exception e)
        {
            Log.Warn($"rdp: could not read the disconnect reason: {e.Message}");
        }
        Log.Info($"rdp: disconnected, reason {reason}, extended {extended} ({Connection.Name}): {description}");

        var wasLive = isLive;
        SetLive(false);
        host.Visibility = Visibility.Collapsed;
        if (terminated || closing) return;

        // Sign-out, a disconnect chosen in the session, or closed from here:
        // the pane goes with it. Anything else stays on screen to be read.
        var clean = wasLive && (reason is ReasonLocal or ReasonRemoteByUser ||
                                (reason == ReasonByServer && extended is ExtendedApiDisconnect or ExtendedApiLogoff
                                     or ExtendedRpcDisconnectByUser or ExtendedLogoffByUser));
        if (clean)
        {
            SetStatus("Session ended");
            SessionEnded?.Invoke(true);
            return;
        }
        var detail = !string.IsNullOrWhiteSpace(description) ? description.Trim()
                   : lastError ?? $"The connection ended (code {reason}).";
        SetStatus("Disconnected: " + detail);
        SessionEnded?.Invoke(false);
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
}
