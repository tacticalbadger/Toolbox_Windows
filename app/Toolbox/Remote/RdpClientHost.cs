using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace Toolbox.Remote;

/// <summary>
/// Hosts Microsoft's Remote Desktop ActiveX control (mstscax.dll, the engine
/// behind mstsc.exe). The control is driven through late binding, so no
/// generated interop assembly is needed.
/// </summary>
internal sealed class RdpClientHost : WinForms.AxHost
{
    // MsRdpClient9NotSafeForScripting: on every Windows since 8.1, and the
    // first version whose session can be resized while connected.
    private const string ClassId = "8B918B82-7985-4C24-89DF-C33AD2BBFBCD";

    public RdpClientHost() : base(ClassId)
    {
    }

    /// <summary>The control's automation object, once the control has been created.</summary>
    public object? Control => GetOcx();
}

/// <summary>
/// The events of the Remote Desktop control (IMsTscAxEvents). Only the ones
/// the pane listens to are declared; the control's other notifications fall
/// through unanswered, which is allowed for a dispatch interface.
/// </summary>
[ComImport]
[Guid("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface IRdpClientEvents
{
    [DispId(1)]
    void OnConnecting();

    [DispId(2)]
    void OnConnected();

    [DispId(3)]
    void OnLoginComplete();

    [DispId(4)]
    void OnDisconnected(int reason);

    [DispId(10)]
    void OnFatalError(int errorCode);

    [DispId(11)]
    void OnWarning(int warningCode);

    [DispId(12)]
    void OnRemoteDesktopSizeChange(int width, int height);

    [DispId(22)]
    void OnLogonError(int error);
}

/// <summary>Receives the control's events and passes them on as delegates.</summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class RdpClientEventSink : IRdpClientEvents
{
    public Action? Connecting { get; set; }
    public Action? Connected { get; set; }
    public Action? LoginComplete { get; set; }
    public Action<int>? Disconnected { get; set; }
    public Action<int>? FatalError { get; set; }
    public Action<int>? LogonError { get; set; }
    public Action<int, int>? DesktopSizeChanged { get; set; }

    public void OnConnecting() => Connecting?.Invoke();
    public void OnConnected() => Connected?.Invoke();
    public void OnLoginComplete() => LoginComplete?.Invoke();
    public void OnDisconnected(int reason) => Disconnected?.Invoke(reason);
    public void OnFatalError(int errorCode) => FatalError?.Invoke(errorCode);
    public void OnWarning(int warningCode) { }
    public void OnRemoteDesktopSizeChange(int width, int height) => DesktopSizeChanged?.Invoke(width, height);
    public void OnLogonError(int error) => LogonError?.Invoke(error);
}

/// <summary>
/// Settings of the control that are not on its automation interface: display
/// scaling, zoom, hardware graphics, manual clipboard sync.
/// </summary>
[ComImport]
[Guid("302D8188-0052-4807-806A-362B628F9AC5")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IRdpExtendedSettings
{
    void SetProperty([MarshalAs(UnmanagedType.BStr)] string name, [In] ref object value);

    [return: MarshalAs(UnmanagedType.Struct)]
    object GetProperty([MarshalAs(UnmanagedType.BStr)] string name);
}

/// <summary>
/// The control's clipboard in manual mode (IMsRdpClipboard, reached through
/// IMsRdpClientNonScriptable7). With the "ManualClipboardSyncEnabled" setting
/// on, the control tells the remote desktop what this PC's clipboard holds
/// only when <see cref="OfferLocal"/> is called, and puts what was copied on
/// the remote side on this PC's clipboard only when <see cref="TakeRemote"/>
/// is. When this PC's clipboard changes after an offer, the control itself
/// withdraws the offer. Without the setting, the control tells every
/// connected session about every change, whichever window is in front.
/// <para>
/// The object does not answer QueryInterface for its own interface, so it is
/// called through its function table. The positions are those of the
/// control's type library (mstscax.dll), and a COM interface's table never
/// changes once published.
/// </para>
/// </summary>
internal sealed unsafe class RdpClipboard : IDisposable
{
    // IMsRdpClientNonScriptable7, and where its Clipboard property sits in its table.
    private static readonly Guid NonScriptable7 = new("71B4A60A-FE21-46D8-A39B-8E32BA0C5ECC");
    private const int ClipboardGetter = 69;

    // IMsRdpClipboard, after IUnknown's three entries.
    private const int CanSyncLocalToRemote = 3;
    private const int SyncLocalToRemote = 4;
    private const int CanSyncRemoteToLocal = 5;
    private const int SyncRemoteToLocal = 6;

    private nint self;

    private RdpClipboard(nint self) => this.self = self;

    /// <summary>Whether this version of the control has the manual clipboard at all.</summary>
    public static bool IsSupported(object control)
    {
        var unknown = Marshal.GetIUnknownForObject(control);
        try
        {
            var iid = NonScriptable7;
            if (Marshal.QueryInterface(unknown, in iid, out var found) != 0) return false;
            Marshal.Release(found);
            return true;
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    /// <summary>The clipboard of a control, or null when it has none to give.</summary>
    public static RdpClipboard? From(object control)
    {
        var unknown = Marshal.GetIUnknownForObject(control);
        try
        {
            var iid = NonScriptable7;
            if (Marshal.QueryInterface(unknown, in iid, out var settings) != 0) return null;
            try
            {
                var table = *(nint**)settings;
                var getter = (delegate* unmanaged[Stdcall]<nint, nint*, int>)table[ClipboardGetter];
                nint clipboard = 0;
                if (getter(settings, &clipboard) != 0 || clipboard == 0) return null;
                return new RdpClipboard(clipboard);
            }
            finally
            {
                Marshal.Release(settings);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    /// <summary>This PC's clipboard holds something the remote desktop has not been told about.</summary>
    public bool HasLocalChanges => Ask(CanSyncLocalToRemote);

    /// <summary>Tells the remote desktop what this PC's clipboard holds (kinds of data, no content).</summary>
    public void OfferLocal() => Do(SyncLocalToRemote);

    /// <summary>Something was copied on the remote side that is not on this PC's clipboard yet.</summary>
    public bool HasRemoteChanges => Ask(CanSyncRemoteToLocal);

    /// <summary>Puts what was copied on the remote side on this PC's clipboard.</summary>
    public void TakeRemote() => Do(SyncRemoteToLocal);

    private bool Ask(int slot)
    {
        if (self == 0) return false;
        // The answer is a VARIANT_BOOL; an int's worth of room is given so a
        // wider write cannot land outside it.
        var answer = 0;
        var function = (delegate* unmanaged[Stdcall]<nint, int*, int>)(*(nint**)self)[slot];
        return function(self, &answer) == 0 && (answer & 0xFFFF) != 0;
    }

    private void Do(int slot)
    {
        if (self == 0) return;
        var function = (delegate* unmanaged[Stdcall]<nint, int>)(*(nint**)self)[slot];
        Marshal.ThrowExceptionForHR(function(self));
    }

    public void Dispose()
    {
        var pointer = self;
        self = 0;
        if (pointer != 0) Marshal.Release(pointer);
    }
}
