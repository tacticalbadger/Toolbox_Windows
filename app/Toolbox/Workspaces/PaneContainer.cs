using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Toolbox.Workspaces;

/// <summary>
/// Owns the pane tree of one workspace and lays its panes out. The panes are
/// direct children positioned from the tree, so rearranging the layout moves
/// rectangles around and never re-parents a running session.
/// </summary>
internal sealed class PaneContainer : Panel, IDragTarget
{
    private PaneTree tree;
    private readonly Dictionary<Guid, PaneView> panes = new();
    private readonly Dictionary<string, Thumb> thumbs = new();
    private readonly List<PaneSlot> paneSlots = [];
    private readonly List<DividerSlot> dividerSlots = [];
    private readonly DropHighlight highlight = new();
    private Guid focusedId;

    /// <summary>Raised when panes come, go, move or change focus.</summary>
    public event Action? LayoutChanged;

    /// <summary>A session came, went, or changed its title.</summary>
    public event Action? ContentChanged;

    /// <summary>The workspace's last pane was closed or moved away: nothing is left in it.</summary>
    public event Action? Emptied;

    /// <summary>Opens a connection in a pane chosen by a drop.</summary>
    public Action<Connection, PaneView>? OpenConnection { get; set; }

    /// <summary>Builds the header context menu for a pane.</summary>
    public Func<PaneView, ContextMenu?>? ContextMenuProvider { get; set; }

    public PaneContainer()
    {
        Background = Brushes.Transparent;
        UseLayoutRounding = true;
        var id = Guid.NewGuid();
        tree = new PaneTree.Leaf(id);
        focusedId = id;
        AddPane(id);
        Rebuild();

        Loaded += (_, _) =>
        {
            DragController.Register(this);
            ComponentDispatcher.ThreadFilterMessage += OnThreadMessage;
        };
        Unloaded += (_, _) =>
        {
            DragController.Unregister(this);
            ComponentDispatcher.ThreadFilterMessage -= OnThreadMessage;
            highlight.Close();
        };
    }

    public PaneTree Tree => tree;

    public int PaneCount => panes.Count;

    public IEnumerable<PaneView> Panes => tree.Leaves.Select(id => panes[id]);

    /// <summary>
    /// The focused pane. Focus is repaired if it points at a pane that is
    /// gone; the tree always has at least one leaf and every leaf has a view.
    /// </summary>
    public PaneView FocusedPane
    {
        get
        {
            if (panes.TryGetValue(focusedId, out var pane)) return pane;
            focusedId = tree.Leaves[0];
            return panes[focusedId];
        }
    }

    /// <summary>
    /// Sessions that closing the workspace would interrupt (an idle local
    /// shell is live, but nothing is lost by closing it).
    /// </summary>
    public int SessionsToWarnAbout => panes.Values.Count(p => p.Content?.CloseWarning is not null);

    /// <summary>The sessions, in the order of the layout.</summary>
    public IReadOnlyList<IPaneContent> Contents =>
        tree.Leaves.Select(id => panes[id].Content).OfType<IPaneContent>().ToList();

    /// <summary>A pane with nothing in it, the focused one first.</summary>
    public PaneView? EmptyPane =>
        FocusedPane.Content is null ? FocusedPane : Panes.FirstOrDefault(p => p.Content is null);

    /// <summary>
    /// The pane a session moved here from another workspace goes in: an
    /// empty one, otherwise a fresh pane split to the right of the focused
    /// one. (A new session never splits by itself: see the window's
    /// PlaceForNewSession.)
    /// </summary>
    public PaneView PaneForAdoptedSession() => EmptyPane ?? InsertPane(FocusedPane.Id, PaneSide.Right);

    public PaneView Split(SplitAxis axis) =>
        InsertPane(FocusedPane.Id, axis == SplitAxis.Horizontal ? PaneSide.Right : PaneSide.Bottom);

    /// <summary>Adds an empty pane on <paramref name="side"/> of <paramref name="target"/> and focuses it.</summary>
    public PaneView InsertPane(Guid target, PaneSide side)
    {
        var id = Guid.NewGuid();
        var pane = AddPane(id);
        tree = tree.Inserting(id, target, side);
        Rebuild();
        SetFocus(id, activate: true);
        return pane;
    }

    /// <summary>
    /// Closes a pane's session. The last remaining pane is emptied, not
    /// removed, and the workspace is told that nothing is left in it.
    /// </summary>
    public void ClosePane(Guid id)
    {
        if (!panes.TryGetValue(id, out var pane)) return;
        var hadSession = pane.Content is not null;
        pane.SetContent(null);
        if (!Remove(pane) && hadSession) Emptied?.Invoke();
    }

    /// <summary>Takes a pane out of the layout, unless it is the only one. Returns whether it was removed.</summary>
    private bool Remove(PaneView pane)
    {
        var id = pane.Id;
        var order = tree.Leaves;
        if (order.Count <= 1)
        {
            SetFocus(id, activate: true);
            return false;
        }
        var index = Math.Max(0, order.ToList().IndexOf(id));
        tree = tree.Removing(id) ?? new PaneTree.Leaf(id);
        var remaining = tree.Leaves;
        // Decide where focus goes before the pane disappears: everything that
        // listens to layout changes asks for the focused pane, which must
        // never be one that was just removed.
        var next = focusedId == id ? remaining[Math.Min(index, remaining.Count - 1)] : focusedId;
        var focusMoved = next != focusedId;
        focusedId = next;
        panes.Remove(id);
        Children.Remove(pane);
        Rebuild();
        SetFocus(next, activate: focusMoved);
        return true;
    }

    public void FocusNeighbor(int delta)
    {
        var order = tree.Leaves.ToList();
        var index = order.IndexOf(focusedId);
        if (index < 0 || order.Count < 2) return;
        SetFocus(order[(index + delta + order.Count) % order.Count], activate: true);
    }

    /// <summary>Makes a pane the active one; with <paramref name="activate"/> its session also takes the keyboard.</summary>
    public void SetFocus(Guid id, bool activate)
    {
        if (!panes.TryGetValue(id, out var pane)) return;
        focusedId = id;
        foreach (var (paneId, view) in panes) view.IsActivePane = paneId == id;
        if (activate) pane.FocusSession();
        LayoutChanged?.Invoke();
    }

    public void TerminateAll()
    {
        foreach (var pane in panes.Values) pane.SetContent(null);
    }

    /// <summary>Takes a pane's session out of this workspace, closing the pane.</summary>
    public IPaneContent? Lift(PaneView pane)
    {
        var content = pane.TakeContent();
        if (!Remove(pane) && content is not null) Emptied?.Invoke();
        return content;
    }

    /// <summary>Places a session arriving from another workspace.</summary>
    public void Adopt(IPaneContent content)
    {
        var pane = PaneForAdoptedSession();
        pane.SetContent(content);
        SetFocus(pane.Id, activate: true);
    }

    // ----- Building -----

    private PaneView AddPane(Guid id)
    {
        var pane = new PaneView(id);
        pane.FocusRequested += () => SetFocus(id, activate: false);
        pane.CloseRequested += () => ClosePane(id);
        pane.DragRequested += _ => DragController.Begin(pane, new DragPayload { PaneId = id });
        pane.ContextMenuProvider = () => ContextMenuProvider?.Invoke(pane);
        pane.ContentChanged += () => ContentChanged?.Invoke();
        panes[id] = pane;
        Children.Add(pane);
        return pane;
    }

    /// <summary>Brings the dividers in line with the tree and schedules a layout pass.</summary>
    private void Rebuild()
    {
        var wanted = new Dictionary<string, SplitAxis>();
        CollectDividers(tree, [], wanted);
        foreach (var key in thumbs.Keys.Where(k => !wanted.ContainsKey(k)).ToList())
        {
            Children.Remove(thumbs[key]);
            thumbs.Remove(key);
        }
        foreach (var (key, axis) in wanted)
        {
            if (thumbs.TryGetValue(key, out var existing))
            {
                existing.Cursor = axis == SplitAxis.Horizontal ? Cursors.SizeWE : Cursors.SizeNS;
                continue;
            }
            var thumb = MakeThumb(key, axis);
            thumbs[key] = thumb;
            Children.Add(thumb);
        }
        var multiple = panes.Count > 1;
        foreach (var pane in panes.Values) pane.ShowsFocusRing = multiple;
        InvalidateMeasure();
        LayoutChanged?.Invoke();
    }

    private static void CollectDividers(PaneTree node, int[] path, Dictionary<string, SplitAxis> into)
    {
        if (node is not PaneTree.Split split) return;
        for (var i = 0; i < split.Children.Count; i++)
        {
            if (i < split.Children.Count - 1) into[string.Join("/", path) + ":" + i] = split.Axis;
            CollectDividers(split.Children[i], [.. path, i], into);
        }
    }

    private Thumb MakeThumb(string key, SplitAxis axis)
    {
        var strip = new FrameworkElementFactory(typeof(Border));
        strip.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var thumb = new Thumb
        {
            Template = new ControlTemplate(typeof(Thumb)) { VisualTree = strip },
            Cursor = axis == SplitAxis.Horizontal ? Cursors.SizeWE : Cursors.SizeNS,
            Focusable = false,
        };
        // Remote desktops ask for their new size when the divider is let go.
        thumb.DragStarted += (_, _) => LiveResize.Begin();
        thumb.DragCompleted += (_, _) => LiveResize.End();
        thumb.DragDelta += (_, e) =>
        {
            var slot = dividerSlots.FirstOrDefault(d => d.Key == key);
            var split = slot is null ? null : tree.SplitAt(slot.Path);
            if (slot is null || split is null) return;
            var delta = slot.Axis == SplitAxis.Horizontal ? e.HorizontalChange : e.VerticalChange;
            tree = tree.WithRatios(PaneLayout.Dragged(split.Ratios, slot.Index, delta, slot.Usable), slot.Path);
            InvalidateMeasure();
        };
        return thumb;
    }

    private void ComputeSlots(Size size)
    {
        paneSlots.Clear();
        dividerSlots.Clear();
        PaneLayout.Compute(tree, new Rect(0, 0, size.Width, size.Height), paneSlots, dividerSlots);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
                            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
        ComputeSlots(size);
        foreach (var slot in paneSlots)
        {
            if (panes.TryGetValue(slot.Id, out var pane)) pane.Measure(slot.Bounds.Size);
        }
        foreach (var slot in dividerSlots)
        {
            if (thumbs.TryGetValue(slot.Key, out var thumb)) thumb.Measure(slot.Bounds.Size);
        }
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        ComputeSlots(finalSize);
        foreach (var slot in paneSlots)
        {
            if (panes.TryGetValue(slot.Id, out var pane)) pane.Arrange(slot.Bounds);
        }
        foreach (var slot in dividerSlots)
        {
            if (thumbs.TryGetValue(slot.Key, out var thumb)) thumb.Arrange(slot.Bounds);
        }
        return finalSize;
    }

    // ----- Clicks in sessions that are native windows -----

    private const int WmLeftButtonDown = 0x0201;
    private const int WmRightButtonDown = 0x0204;
    private const int WmMiddleButtonDown = 0x0207;

    /// <summary>
    /// Clicking anywhere in a pane makes it the active one. A remote desktop
    /// is a native child window that takes its own mouse input, so the click
    /// is noticed here, on its way through the message loop. (The terminal
    /// lives in another process and reports its own clicks.)
    /// </summary>
    private void OnThreadMessage(ref MSG message, ref bool handled)
    {
        if (message.message is not (WmLeftButtonDown or WmRightButtonDown or WmMiddleButtonDown)) return;
        if (!IsVisible || DragController.IsDragging) return;
        var point = new Point(message.pt_x, message.pt_y);
        foreach (var (id, pane) in panes)
        {
            if (id == focusedId || !pane.IsVisible) continue;
            if (ScreenRect(pane).Contains(point))
            {
                SetFocus(id, activate: false);
                return;
            }
        }
    }

    private static Rect ScreenRect(FrameworkElement element)
    {
        if (PresentationSource.FromVisual(element) is null) return Rect.Empty;
        return new Rect(element.PointToScreen(new Point(0, 0)),
                        element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight)));
    }

    // ----- Drops -----

    private (PaneView Pane, DropZone Zone, Rect Screen)? Hit(Point screen)
    {
        foreach (var pane in panes.Values)
        {
            var rect = ScreenRect(pane);
            if (rect.IsEmpty || !rect.Contains(screen)) continue;
            var zone = PaneLayout.ZoneFor(new Point(screen.X - rect.X, screen.Y - rect.Y), rect.Size);
            return (pane, zone, rect);
        }
        return null;
    }

    private bool Accepts(DragPayload payload, PaneView target)
    {
        if (payload.PaneId is { } dragged)
        {
            return dragged != target.Id && panes.ContainsKey(dragged);
        }
        return payload.Connection is not null;
    }

    bool IDragTarget.DragOver(Point screen, DragPayload payload)
    {
        if (!IsVisible) return false;
        var hit = Hit(screen);
        var owner = Window.GetWindow(this);
        if (hit is null || owner is null || !Accepts(payload, hit.Value.Pane))
        {
            highlight.Hide();
            return false;
        }
        highlight.Show(PaneLayout.ZoneRect(hit.Value.Zone, hit.Value.Screen), owner);
        return true;
    }

    void IDragTarget.DragLeave() => highlight.Hide();

    void IDragTarget.Drop(Point screen, DragPayload payload)
    {
        highlight.Hide();
        var hit = Hit(screen);
        if (hit is null || !Accepts(payload, hit.Value.Pane)) return;
        if (payload.PaneId is { } dragged)
        {
            DropPane(dragged, hit.Value.Pane.Id, hit.Value.Zone);
        }
        else if (payload.Connection is { } connection)
        {
            DropConnection(connection, hit.Value.Pane.Id, hit.Value.Zone);
        }
    }

    /// <summary>
    /// A pane dragged onto another: trade places on the centre, or move it to
    /// that edge. The pane keeps its view and session throughout.
    /// </summary>
    public void DropPane(Guid sourceId, Guid targetId, DropZone zone)
    {
        if (sourceId == targetId || !panes.ContainsKey(sourceId) || !panes.ContainsKey(targetId)) return;
        if (zone.Side() is not { } side)
        {
            tree = tree.Swapping(sourceId, targetId);
        }
        else
        {
            var without = tree.Removing(sourceId);
            if (without is null) return;
            tree = without.Inserting(sourceId, targetId, side);
        }
        Rebuild();
        SetFocus(sourceId, activate: true);
        Log.Info($"layout: pane moved ({zone}); now {tree.Describe()}");
    }

    /// <summary>
    /// A sidebar connection dropped on a pane: fill it if empty, otherwise
    /// open next to it on the chosen edge.
    /// </summary>
    public void DropConnection(Connection connection, Guid targetId, DropZone zone)
    {
        if (!panes.TryGetValue(targetId, out var target)) return;
        PaneView pane;
        if (zone.Side() is { } side) pane = InsertPane(targetId, side);
        else if (target.Content is null) pane = target;
        else pane = InsertPane(targetId, PaneSide.Right);
        Log.Info($"layout: {connection.Name} dropped ({zone}); now {tree.Describe()}");
        OpenConnection?.Invoke(connection, pane);
    }
}
