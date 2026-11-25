using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Interop;

namespace Toolbox.Workspaces;

/// <summary>
/// How accessibility tools see a pane that shows another program's window (a
/// remote desktop, an embedded Windows Terminal): one element with a name,
/// and whatever WPF text the pane shows around it, but not the hosted
/// window itself.
///
/// This is about typing, not about screen readers. With a hosted window in
/// the tree and any accessibility client listening (a password manager is
/// one, and so is Windows' own text input), every change WPF reports to UI
/// Automation makes the UI thread wait on a COM call for a few milliseconds,
/// and such a wait takes waiting key messages out of the queue and drops
/// them. Of "web" typed quickly into the sidebar's filter with a remote
/// desktop on screen, only the "w" arrived (measured 2026-11-25; a COM
/// message filter does not change it, COM asks it and drops the keys all
/// the same). The hosted window stays reachable for such tools through its
/// own window handle.
/// </summary>
internal sealed class NativePanePeer(FrameworkElement owner, Func<string> name) : FrameworkElementAutomationPeer(owner)
{
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;

    protected override string GetClassNameCore() => Owner.GetType().Name;

    protected override string GetNameCore() => name();

    protected override List<AutomationPeer>? GetChildrenCore()
    {
        var children = base.GetChildrenCore();
        children?.RemoveAll(peer => peer is FrameworkElementAutomationPeer { Owner: HwndHost });
        return children;
    }
}
