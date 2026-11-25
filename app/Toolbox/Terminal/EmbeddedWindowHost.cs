using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace Toolbox.Terminal;

/// <summary>
/// A place in the WPF window that another program's top-level window is
/// moved into: the "guest" becomes a child window that fills this element,
/// and is a top-level window again if it has to be let go.
///
/// The guest belongs to another process. Nothing here can make it draw,
/// answer or close; it can only be placed, shown, asked to close, and
/// watched.
/// </summary>
internal sealed class EmbeddedWindowHost : HwndHost
{
    private nint container;
    private nint guest;
    private nint guestStyle;

    /// <summary>The guest window, or 0 before one was taken in or after it went.</summary>
    public nint Guest => guest != 0 && IsWindow(guest) ? guest : 0;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        // A plain black rectangle: what shows before the guest has drawn.
        container = CreateWindowEx(0, "Static", "", WsChild | WsVisible | WsClipChildren | SsBlackRect,
                                   0, 0, 16, 16, hwndParent.Handle, 0, 0, 0);
        if (container == 0) throw new InvalidOperationException("the pane's window could not be created");
        return new HandleRef(this, container);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        // Whatever is still inside goes back to the desktop first: a window
        // destroyed here would be destroyed under its own program's feet.
        Release();
        DestroyWindow(hwnd.Handle);
        container = 0;
    }

    /// <summary>
    /// Moves <paramref name="window"/> into this element. It loses its frame
    /// and becomes a child; its size follows the element from then on.
    /// </summary>
    public bool Adopt(nint window)
    {
        if (container == 0 || !IsWindow(window)) return false;
        guestStyle = GetWindowLongPtr(window, GwlStyle);
        var style = (guestStyle & ~(WsPopup | WsCaption | WsThickFrame | WsSysMenu | WsMinimizeBox | WsMaximizeBox)) | WsChild;
        // Child style first, then the new parent, as SetParent asks for.
        SetWindowLongPtr(window, GwlStyle, style);
        if (SetParent(window, container) == 0 && Marshal.GetLastWin32Error() != 0 && GetParent(window) != container)
        {
            SetWindowLongPtr(window, GwlStyle, guestStyle);
            return false;
        }
        guest = window;
        Fit(show: true);
        return true;
    }

    /// <summary>
    /// Lets the guest go: it is a window of its own again, on the desktop,
    /// with the frame it had. For a guest that would not close.
    /// </summary>
    public void Release()
    {
        var window = Guest;
        guest = 0;
        if (window == 0) return;
        SetParent(window, 0);
        SetWindowLongPtr(window, GwlStyle, guestStyle);
        SetWindowPos(window, 0, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged | SwpShowWindow | SwpNoActivate);
    }

    /// <summary>Asks the guest to close, the way its own close button would.</summary>
    public void RequestClose()
    {
        if (Guest is var window and not 0) PostMessage(window, WmClose, 0, 0);
    }

    /// <summary>Gives the guest the keyboard.</summary>
    public void FocusGuest()
    {
        if (Guest is var window and not 0) SetFocus(window);
    }

    /// <summary>Whether the keyboard focus is in the guest (or one of its own child windows).</summary>
    public bool GuestHasFocus()
    {
        var window = Guest;
        if (window == 0) return false;
        // The guest's thread shares this one's input state from the moment
        // it became a child, so its focus window is visible from here.
        var focus = GetFocus();
        return focus != 0 && (focus == window || IsChild(window, focus));
    }

    /// <summary>The guest's title, as its program sets it.</summary>
    public string GuestTitle()
    {
        var window = Guest;
        if (window == 0) return "";
        var text = new StringBuilder(512);
        return GetWindowText(window, text, text.Capacity) > 0 ? text.ToString() : "";
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        Fit(show: false);
    }

    private void Fit(bool show)
    {
        var window = Guest;
        if (window == 0 || container == 0 || !GetClientRect(container, out var area)) return;
        var flags = SwpNoZOrder | SwpNoActivate | (show ? SwpFrameChanged | SwpShowWindow : 0);
        SetWindowPos(window, 0, 0, 0, Math.Max(area.Right - area.Left, 1), Math.Max(area.Bottom - area.Top, 1), flags);
    }

    // ----- Win32 -----

    private const int GwlStyle = -16;
    private const nint WsChild = 0x40000000;
    private const nint WsVisible = 0x10000000;
    private const nint WsClipChildren = 0x02000000;
    private static readonly nint WsPopup = unchecked((nint)0x80000000L);
    private const nint WsCaption = 0x00C00000;
    private const nint WsThickFrame = 0x00040000;
    private const nint WsSysMenu = 0x00080000;
    private const nint WsMinimizeBox = 0x00020000;
    private const nint WsMaximizeBox = 0x00010000;
    private const nint SsBlackRect = 0x00000004;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;
    private const uint WmClose = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string windowName, nint style,
                                              int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(nint parent, nint window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetParent(nint child, nint parent);

    [DllImport("user32.dll")]
    private static extern nint GetParent(nint window);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out RECT area);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetFocus();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, StringBuilder text, int capacity);
}
