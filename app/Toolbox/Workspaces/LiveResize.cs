namespace Toolbox.Workspaces;

/// <summary>
/// Whether something is being dragged that changes pane sizes step by step:
/// the window's edge, a divider between panes, the sidebar's edge. A remote
/// desktop scales its picture during the drag and asks the host for the new
/// size when the drag ends, instead of at every step.
/// </summary>
internal static class LiveResize
{
    private static int depth;

    public static bool IsActive => depth > 0;

    /// <summary>The last drag in progress has ended.</summary>
    public static event Action? Ended;

    public static void Begin() => depth++;

    public static void End()
    {
        if (depth == 0) return;
        depth--;
        if (depth == 0) Ended?.Invoke();
    }
}
