using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Toolbox.Workspaces;

/// <summary>What is being dragged: a pane (by its header) or sidebar nodes.</summary>
internal sealed class DragPayload
{
    public Guid? PaneId { get; init; }

    /// <summary>Sidebar node ids such as <c>conn:…</c>, <c>cred:…</c>, <c>folder:…</c>, <c>prov:…</c>.</summary>
    public IReadOnlyList<string> NodeIds { get; init; } = [];

    /// <summary>The first connection among the dragged sidebar nodes, if any.</summary>
    public Connection? Connection
    {
        get
        {
            foreach (var id in NodeIds)
            {
                foreach (var prefix in new[] { "conn:", "all-conn:" })
                {
                    if (!id.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var found = Model.AppModel.Shared.Connection(id[prefix.Length..]);
                    if (found is not null) return found;
                }
            }
            return null;
        }
    }
}

/// <summary>A place a drag can end. Points are screen positions in device pixels.</summary>
internal interface IDragTarget
{
    /// <summary>True when the pointer is over this target and a drop here would do something; shows its feedback.</summary>
    bool DragOver(Point screen, DragPayload payload);

    void DragLeave();

    void Drop(Point screen, DragPayload payload);
}

/// <summary>
/// Runs drags inside the app with mouse capture instead of OLE drag and drop.
/// The terminal and the remote desktop are separate native windows (one of
/// them in another process); captured mouse input keeps arriving here
/// whatever is under the pointer, so every pane is a valid place to drop.
/// </summary>
internal static class DragController
{
    private static readonly List<IDragTarget> Targets = [];
    private static UIElement? source;
    private static DragPayload? payload;
    private static IDragTarget? current;
    private static Window? window;

    public static bool IsDragging => source is not null;

    public static void Register(IDragTarget target)
    {
        if (!Targets.Contains(target)) Targets.Add(target);
    }

    public static void Unregister(IDragTarget target) => Targets.Remove(target);

    /// <summary>Starts a drag from <paramref name="element"/>, which must be under a pressed mouse button.</summary>
    public static void Begin(UIElement element, DragPayload what)
    {
        if (IsDragging) return;
        if (!element.CaptureMouse())
        {
            Log.Warn("drag: the mouse could not be captured");
            return;
        }
        Log.Info(what.PaneId is null ? "drag: sidebar item picked up" : "drag: pane picked up");
        source = element;
        payload = what;
        element.MouseMove += OnMove;
        element.MouseLeftButtonUp += OnUp;
        element.LostMouseCapture += OnLostCapture;
        window = Window.GetWindow(element);
        if (window is not null) window.PreviewKeyDown += OnKey;
        Mouse.OverrideCursor = Cursors.No;
    }

    private static void OnMove(object sender, MouseEventArgs e)
    {
        if (source is null || payload is null) return;
        var screen = source.PointToScreen(e.GetPosition(source));
        IDragTarget? over = null;
        foreach (var target in Targets)
        {
            if (target.DragOver(screen, payload))
            {
                over = target;
                break;
            }
        }
        if (!ReferenceEquals(over, current))
        {
            current?.DragLeave();
            current = over;
        }
        Mouse.OverrideCursor = over is null ? Cursors.No : Cursors.Hand;
    }

    private static void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (source is null || payload is null) return;
        var screen = source.PointToScreen(e.GetPosition(source));
        var target = current;
        var what = payload;
        Finish();
        // Ask again at the final position: the last move may have been a while ago.
        if (target is not null && target.DragOver(screen, what))
        {
            target.DragLeave();
            target.Drop(screen, what);
        }
        else
        {
            target?.DragLeave();
        }
        e.Handled = true;
    }

    private static void OnLostCapture(object sender, MouseEventArgs e)
    {
        if (source is not null) Log.Info("drag: ended without a drop (capture lost)");
        Finish();
    }

    private static void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Finish();
            e.Handled = true;
        }
    }

    private static void Finish()
    {
        var element = source;
        if (element is null) return;
        source = null;
        payload = null;
        element.MouseMove -= OnMove;
        element.MouseLeftButtonUp -= OnUp;
        element.LostMouseCapture -= OnLostCapture;
        if (window is not null) window.PreviewKeyDown -= OnKey;
        window = null;
        current?.DragLeave();
        current = null;
        Mouse.OverrideCursor = null;
        if (element.IsMouseCaptured) element.ReleaseMouseCapture();
    }
}

/// <summary>
/// The translucent rectangle showing where a drop would land. It is a small
/// click-through window of its own, because nothing drawn inside the main
/// window can appear on top of the terminal or the remote desktop.
/// </summary>
internal sealed class DropHighlight
{
    private Window? window;
    private IntPtr handle;

    /// <summary>Shows the highlight over <paramref name="screen"/> (device pixels).</summary>
    public void Show(Rect screen, Window owner)
    {
        if (window is null)
        {
            var accent = (owner.TryFindResource("AccentFillColorDefaultBrush") as SolidColorBrush)?.Color
                         ?? Color.FromRgb(0x4C, 0xC2, 0xFF);
            window = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                ShowActivated = false,
                Focusable = false,
                IsHitTestVisible = false,
                ResizeMode = ResizeMode.NoResize,
                Owner = owner,
                Width = 8,
                Height = 8,
                Content = new Border
                {
                    Margin = new Thickness(4),
                    CornerRadius = new CornerRadius(8),
                    BorderThickness = new Thickness(2),
                    Background = new SolidColorBrush(Color.FromArgb(0x38, accent.R, accent.G, accent.B)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0xE6, accent.R, accent.G, accent.B)),
                },
            };
            window.SourceInitialized += (_, _) =>
            {
                handle = new WindowInteropHelper(window).Handle;
                // Never takes the mouse or the keyboard, and stays out of Alt+Tab.
                var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
                SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style | WsExTransparent | WsExNoActivate | WsExToolWindow));
            };
        }
        if (!window.IsVisible) window.Show();
        SetWindowPos(handle, IntPtr.Zero, (int)Math.Round(screen.X), (int)Math.Round(screen.Y),
                     Math.Max(1, (int)Math.Round(screen.Width)), Math.Max(1, (int)Math.Round(screen.Height)),
                     SwpNoActivate | SwpNoZOrder);
    }

    public void Hide()
    {
        if (window is { IsVisible: true }) window.Hide();
    }

    public void Close()
    {
        window?.Close();
        window = null;
    }

    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x20;
    private const long WsExToolWindow = 0x80;
    private const long WsExNoActivate = 0x08000000;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
