using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
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
/// connection settings, keeps the remote desktop the size of the pane,
/// decides when the control may share the clipboard, and reports how the
/// session ended.
/// </summary>
internal sealed class RdpPane : Grid, IPaneContent
{
    public Connection Connection { get; }

    // Holds the control. It fills the pane until there is a desktop, and is
    // then kept at exactly the pixels the picture covers (LayoutDesktop);
    // around it the pane's own black shows.
    private readonly WindowsFormsHost host = new();
    private readonly TextBlock status = new();
    private readonly DispatcherTimer resizeTimer = new();
    private readonly DispatcherTimer clipboardTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private RdpClientHost? client;
    private dynamic? control;
    private RdpClientEventSink? events;
    private WinForms.AxHost.ConnectionPointCookie? cookie;
    private DesktopRequest asked;
    private string title;
    private bool isLive;
    private bool terminated;
    private bool closing;
    private string? lastError;

    // What this session was started with (View > Remote Desktop).
    private bool fastGraphics = true;
    private bool sharesClipboard;
    private bool manualClipboard;

    // Clipboard sharing in manual mode: see CheckClipboard.
    private RdpClipboard? clipboard;
    private bool hadKeyboard;

    // Whether the control is shrinking the picture to fit (see LayoutDesktop).
    private bool shrinking;

    /// <summary>A desktop size in pixels and the display scale the host is told, as a percentage.</summary>
    private readonly record struct DesktopRequest(int Width, int Height, uint Scale);

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
        clipboardTimer.Tick += (_, _) => CheckClipboard();
        SizeChanged += (_, _) =>
        {
            LayoutDesktop();
            ScheduleResize();
        };
        LiveResize.Ended += OnLiveResizeEnded;
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
            client.Enter += (_, _) =>
            {
                FocusRequested?.Invoke();
                // What is on the clipboard should be there to paste right away.
                Dispatcher.BeginInvoke(CheckClipboard, DispatcherPriority.Input);
            };
            host.Visibility = Visibility.Visible;
            client.CreateControl();
            control = client.Control ?? throw new InvalidOperationException("the control did not start");

            events = new RdpClientEventSink
            {
                Connecting = () => { },
                Connected = OnConnected,
                LoginComplete = OnLoginComplete,
                Disconnected = OnDisconnected,
                FatalError = code => lastError = $"The Remote Desktop client reported fatal error {code}.",
                LogonError = code => Log.Info($"rdp: logon notice {code} ({Connection.Name})"),
                DesktopSizeChanged = OnDesktopSizeChanged,
            };
            cookie = new WinForms.AxHost.ConnectionPointCookie(client.Control!, events, typeof(IRdpClientEvents));

            var wanted = Wanted();
            asked = wanted;
            control.Server = Connection.Host;
            control.UserName = username;
            control.DesktopWidth = wanted.Width;
            control.DesktopHeight = wanted.Height;
            control.ColorDepth = 32;
            control.ConnectingText = $"Connecting to {Connection.Host}…";

            var advanced = control.AdvancedSettings9;
            advanced.RDPPort = (int)Connection.Port;
            advanced.EnableCredSspSupport = true;
            // 2 = show the standard warning when the server cannot be verified and let the user decide.
            advanced.AuthenticationLevel = 2;
            advanced.ClearTextPassword = password;
            advanced.SmartSizing = false;
            // Nothing of this PC but the clipboard (below) is offered to the
            // remote desktop: no drives, printers, ports, smart cards or other
            // devices. These are the control's defaults, said out loud so a
            // changed default cannot open one.
            advanced.RedirectDrives = false;
            advanced.RedirectPrinters = false;
            advanced.RedirectPorts = false;
            advanced.RedirectSmartCards = false;
            advanced.RedirectDevices = false;
            advanced.RedirectPOSDevices = false;
            advanced.GrabFocusOnConnect = false;
            advanced.EnableAutoReconnect = true;
            advanced.DisplayConnectionBar = false;
            ApplyScale(wanted.Scale);

            // The switches under View > Remote Desktop, as they are now.
            fastGraphics = AppSettings.Shared.RdpFastGraphics;
            sharesClipboard = AppSettings.Shared.RdpShareClipboard;
            advanced.RedirectClipboard = sharesClipboard;
            // Manual mode: the control tells the remote desktop about the
            // clipboard only when this pane says so. Left to itself it tells
            // every connected session about every change.
            manualClipboard = sharesClipboard && RdpClipboard.IsSupported(client.Control!) &&
                              SetExtended("ManualClipboardSyncEnabled", true);
            if (sharesClipboard && !manualClipboard)
            {
                Log.Warn($"rdp: this Windows has no manual clipboard sync; the Remote Desktop control shares the " +
                         $"clipboard with this session whenever it changes ({Connection.Name})");
            }
            // Off: no Direct3D device; the control decodes and draws in software.
            if (!fastGraphics) SetExtended("EnableHardwareMode", false);

            Log.Info($"rdp: connecting to {Connection.Host}:{Connection.Port} ({Connection.Name}); " +
                     $"desktop {wanted.Width}x{wanted.Height} at {wanted.Scale}%, " +
                     $"graphics {(fastGraphics ? "hardware" : "software")}, " +
                     $"clipboard {(!sharesClipboard ? "off" : manualClipboard ? "with the pane in use" : "always")}");

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
        clipboardTimer.Stop();
        LiveResize.Ended -= OnLiveResizeEnded;
        SetLive(false);
        clipboard?.Dispose();
        clipboard = null;
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

    /// <summary>Sets one of the control's extended settings; false (and a log line) when it does not take it.</summary>
    private bool SetExtended(string name, object value)
    {
        try
        {
            if (client?.Control is not IRdpExtendedSettings extended) return false;
            extended.SetProperty(name, ref value);
            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"rdp: the control did not take {name} ({Connection.Name}): {e.Message}");
            return false;
        }
    }

    // ----- Size -----

    /// <summary>The Windows display scale of this pane as a percentage (100, 150, 200…).</summary>
    private uint DisplayPercent() =>
        (uint)Math.Clamp(Math.Round(VisualTreeHelper.GetDpi(this).DpiScaleX * 100), 100, 500);

    /// <summary>
    /// The desktop to ask for: this pane's size in device pixels, as the
    /// core's rule adjusts it (within RDP's limits, width a multiple of 4), so
    /// the size compared against is the size actually asked for. The host is
    /// told the display's scale so it does not draw tiny on a dense screen.
    /// </summary>
    private DesktopRequest Wanted()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = (uint)Math.Clamp(Math.Round(ActualWidth * dpi.DpiScaleX), 0, 16384);
        var height = (uint)Math.Clamp(Math.Round(ActualHeight * dpi.DpiScaleY), 0, 16384);
        var size = CoreApi.RdpDesktopSize(width, height);
        return new DesktopRequest(size.Width, size.Height, DisplayPercent());
    }

    /// <summary>Tells the server the display scale, so the remote desktop is not drawn tiny on a dense screen.</summary>
    private void ApplyScale(uint percent)
    {
        SetExtended("DesktopScaleFactor", percent);
        SetExtended("DeviceScaleFactor", 100u);
    }

    /// <summary>
    /// Asks for a desktop of this pane's size once the size has settled; the
    /// picture is kept, shrunk to fit where need be, in the meantime. While
    /// something is being dragged the wait is longer, so the host is not asked
    /// for a new desktop at every step; the end of the drag asks at once.
    /// </summary>
    private void ScheduleResize()
    {
        resizeTimer.Stop();
        if (!isLive || terminated) return;
        if (Wanted() == asked) return;
        resizeTimer.Interval = TimeSpan.FromMilliseconds(LiveResize.IsActive ? 250 : 80);
        resizeTimer.Start();
    }

    private void OnLiveResizeEnded() => ResizeSession();

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        // The same pane is a different number of pixels on the other display.
        LayoutDesktop();
        ScheduleResize();
    }

    /// <summary>
    /// Brings the remote desktop to the pane's size without reconnecting
    /// (Display Control), falling back to a reconnect at the new size for
    /// servers that do not support it.
    /// </summary>
    private void ResizeSession()
    {
        resizeTimer.Stop();
        if (!isLive || terminated || control is null) return;
        var wanted = Wanted();
        if (wanted == asked) return;
        try
        {
            control.UpdateSessionDisplaySettings((uint)wanted.Width, (uint)wanted.Height, (uint)wanted.Width, (uint)wanted.Height,
                                                 0u, wanted.Scale, 100u);
            asked = wanted;
            resizeAttempts = 0;
            Log.Info($"rdp: desktop resized to {wanted.Width}x{wanted.Height} at {wanted.Scale}% ({Connection.Name})");
        }
        catch (Exception first)
        {
            // Right after connecting the display channel may not be open yet:
            // ask again a few times before concluding the server lacks it.
            if (++resizeAttempts <= MaxResizeAttempts)
            {
                resizeTimer.Interval = TimeSpan.FromMilliseconds(300);
                resizeTimer.Start();
                return;
            }
            resizeAttempts = 0;
            asked = wanted;
            try
            {
                ApplyScale(wanted.Scale);
                control.Reconnect((uint)wanted.Width, (uint)wanted.Height);
                Log.Info($"rdp: reconnected at {wanted.Width}x{wanted.Height} ({Connection.Name})");
            }
            catch (Exception second)
            {
                Log.Warn($"rdp: resize failed ({Connection.Name}): {first.Message}; {second.Message}");
            }
        }
    }

    private const int MaxResizeAttempts = 8;
    private int resizeAttempts;

    /// <summary>
    /// The pixels a desktop is shown in: its own size where the pane has the
    /// room, otherwise the largest size of the same shape that fits.
    /// </summary>
    internal static (int Width, int Height) FitFor(int desktopWidth, int desktopHeight, int paneWidth, int paneHeight)
    {
        if (desktopWidth <= 0 || desktopHeight <= 0 || paneWidth <= 0 || paneHeight <= 0) return (0, 0);
        if (paneWidth >= desktopWidth && paneHeight >= desktopHeight) return (desktopWidth, desktopHeight);
        // Whichever edge runs out first is used in full.
        if ((long)paneWidth * desktopHeight <= (long)paneHeight * desktopWidth)
        {
            return (paneWidth, Math.Max(1, (int)((long)desktopHeight * paneWidth / desktopWidth)));
        }
        return (Math.Max(1, (int)((long)desktopWidth * paneHeight / desktopHeight)), paneHeight);
    }

    /// <summary>
    /// Places the control in the pane, from the top left corner. A desktop
    /// made for this pane is shown pixel for pixel; the core rounds its size
    /// down a little, and the control gets exactly those pixels (given more
    /// it would paint a grey margin). A desktop larger than the pane, because
    /// the pane is being dragged smaller or the host keeps its own size, is
    /// shrunk to fit by the control and keeps its shape. A smaller one stays
    /// as it is until the host has delivered the new size: the control cannot
    /// magnify a picture dependably.
    /// </summary>
    private void LayoutDesktop()
    {
        if (terminated) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var paneWidth = (int)Math.Round(ActualWidth * dpi.DpiScaleX);
        var paneHeight = (int)Math.Round(ActualHeight * dpi.DpiScaleY);
        if (client is null || !isLive || paneWidth <= 0 || paneHeight <= 0 ||
            remoteSize is not { Width: > 0, Height: > 0 } desktop)
        {
            FillPane();
            return;
        }
        var (width, height) = FitFor(desktop.Width, desktop.Height, paneWidth, paneHeight);
        SetShrinking(width < desktop.Width || height < desktop.Height);
        // Whole device pixels: layout rounding is on for everything in a pane.
        host.HorizontalAlignment = HorizontalAlignment.Left;
        host.VerticalAlignment = VerticalAlignment.Top;
        host.Width = width / dpi.DpiScaleX;
        host.Height = height / dpi.DpiScaleY;
    }

    private void FillPane()
    {
        host.HorizontalAlignment = HorizontalAlignment.Stretch;
        host.VerticalAlignment = VerticalAlignment.Stretch;
        host.Width = double.NaN;
        host.Height = double.NaN;
    }

    /// <summary>
    /// Has the control scale the picture down into its window (its "smart
    /// sizing"). Off whenever the picture fits, so that it is drawn exactly as
    /// it arrives.
    /// </summary>
    private void SetShrinking(bool value)
    {
        if (shrinking == value || control is null) return;
        try
        {
            control.AdvancedSettings9.SmartSizing = value;
            shrinking = value;
        }
        catch (Exception e)
        {
            Log.Warn($"rdp: could not switch scaling ({Connection.Name}): {e.Message}");
        }
    }

    // ----- Clipboard -----

    private static readonly bool AssumesKeyboard = App.HasFlag("--assume-keyboard");

    [DllImport("user32.dll")]
    private static extern nint GetFocus();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(nint parent, nint window);

    /// <summary>
    /// Whether this pane is where typing goes: the app's window is the active
    /// one and the keyboard focus is inside this session's control.
    /// </summary>
    private bool HasKeyboard()
    {
        if (client is null || !client.IsHandleCreated || host.Visibility != Visibility.Visible) return false;
        // Testing aid for runs where the app cannot be in front.
        if (AssumesKeyboard) return true;
        if (Window.GetWindow(this) is not { IsActive: true }) return false;
        var focus = GetFocus();
        return focus != 0 && (focus == client.Handle || IsChild(client.Handle, focus));
    }

    /// <summary>
    /// Shares the clipboard with this session, in manual mode. The remote
    /// desktop is told what this PC's clipboard holds only while this pane
    /// has the keyboard, so a session in the background learns nothing; the
    /// control takes an offer back by itself as soon as the clipboard holds
    /// something else. What was copied on the remote side reaches this PC's
    /// clipboard while the pane has the keyboard, or just as it gives it up.
    /// This pane never sees clipboard contents, only whether there is
    /// something new on either side.
    /// </summary>
    private void CheckClipboard()
    {
        if (!isLive || terminated || clipboard is null) return;
        var has = HasKeyboard();
        var had = hadKeyboard;
        hadKeyboard = has;
        // Switched off while connected: nothing new goes either way.
        if (!AppSettings.Shared.RdpShareClipboard) return;
        try
        {
            if (has)
            {
                if (clipboard.HasLocalChanges)
                {
                    clipboard.OfferLocal();
                    Log.Info($"rdp: clipboard offered ({Connection.Name})");
                }
                else if (clipboard.HasRemoteChanges)
                {
                    TakeRemoteClipboard();
                }
            }
            else if (had && !clipboard.HasLocalChanges && clipboard.HasRemoteChanges)
            {
                // Copied in the session a moment before leaving it.
                TakeRemoteClipboard();
            }
        }
        catch (Exception e)
        {
            // Never a reason to end a session.
            Log.Warn($"rdp: clipboard sharing failed ({Connection.Name}): {e.Message}");
        }
    }

    private void TakeRemoteClipboard()
    {
        clipboard?.TakeRemote();
        Log.Info($"rdp: clipboard taken from the remote desktop ({Connection.Name})");
    }

    // ----- Events from the control (UI thread) -----

    private void OnConnected()
    {
        Log.Info($"rdp: connected ({Connection.Name})");
        lastError = null;
        if (terminated) return;
        host.Visibility = Visibility.Visible;
        SetStatus("");
        SetLive(true);
        if (manualClipboard && client?.Control is { } ocx)
        {
            clipboard?.Dispose();
            clipboard = RdpClipboard.From(ocx);
            hadKeyboard = false;
            if (clipboard is null) Log.Warn($"rdp: the control gave no clipboard; nothing is shared with this session ({Connection.Name})");
            else clipboardTimer.Start();
        }
        LayoutDesktop();
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
        var wanted = Wanted();
        if (remoteSize != (wanted.Width, wanted.Height)) asked = default;
        ScheduleResize();
    }

    /// <summary>The desktop size the server last announced, if it has.</summary>
    private (int Width, int Height)? remoteSize;

    private void OnDesktopSizeChanged(int width, int height)
    {
        remoteSize = (width, height);
        LayoutDesktop();
        Log.Info($"rdp: remote desktop is {width}x{height} ({Connection.Name})");
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
        clipboardTimer.Stop();
        clipboard?.Dispose();
        clipboard = null;
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

    // ----- Accessibility -----

    private static readonly bool ExposesControl = App.HasFlag("--rdp-expose-control");

    /// <summary>
    /// To accessibility tools the pane is one element, without the Remote
    /// Desktop control's own windows: with those in the tree, typing
    /// elsewhere in the window loses keys (see <see cref="NativePanePeer"/>).
    /// Nothing is lost by leaving the control out: what it shows is a picture
    /// of another computer's screen. <c>--rdp-expose-control</c> puts it
    /// back, to see the difference.
    /// </summary>
    protected override AutomationPeer OnCreateAutomationPeer() =>
        ExposesControl ? base.OnCreateAutomationPeer() : new NativePanePeer(this, () => "Remote desktop: " + title);
}
