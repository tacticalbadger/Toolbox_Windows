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

/// <summary>Settings of the control that are not on its automation interface (display scaling).</summary>
[ComImport]
[Guid("302D8188-0052-4807-806A-362B628F9AC5")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IRdpExtendedSettings
{
    void SetProperty([MarshalAs(UnmanagedType.BStr)] string name, [In] ref object value);

    [return: MarshalAs(UnmanagedType.Struct)]
    object GetProperty([MarshalAs(UnmanagedType.BStr)] string name);
}
