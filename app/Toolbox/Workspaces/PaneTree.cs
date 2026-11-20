using System.Windows;

namespace Toolbox.Workspaces;

internal enum SplitAxis
{
    /// <summary>Panes side by side.</summary>
    Horizontal,

    /// <summary>Panes stacked.</summary>
    Vertical,
}

internal enum PaneSide { Left, Right, Top, Bottom }

/// <summary>Where a dragged session lands relative to a pane.</summary>
internal enum DropZone { Center, Left, Right, Top, Bottom }

internal static class PaneSides
{
    public static SplitAxis Axis(this PaneSide side) =>
        side is PaneSide.Left or PaneSide.Right ? SplitAxis.Horizontal : SplitAxis.Vertical;

    /// <summary>Whether the new pane goes before the target in the split's order.</summary>
    public static bool Before(this PaneSide side) => side is PaneSide.Left or PaneSide.Top;

    public static PaneSide? Side(this DropZone zone) => zone switch
    {
        DropZone.Left => PaneSide.Left,
        DropZone.Right => PaneSide.Right,
        DropZone.Top => PaneSide.Top,
        DropZone.Bottom => PaneSide.Bottom,
        _ => null,
    };
}

/// <summary>
/// The layout of one workspace: a tree of splits whose leaves are panes.
/// Each split keeps the fraction of space every child takes, so the
/// arrangement the user dragged out survives adding and closing panes.
/// Trees are immutable; every change returns a new tree.
/// </summary>
internal abstract class PaneTree
{
    public sealed class Leaf(Guid id) : PaneTree
    {
        public Guid Id { get; } = id;
    }

    public sealed class Split(SplitAxis axis, IReadOnlyList<PaneTree> children, IReadOnlyList<double> ratios) : PaneTree
    {
        public SplitAxis Axis { get; } = axis;
        public IReadOnlyList<PaneTree> Children { get; } = children;
        public IReadOnlyList<double> Ratios { get; } = ratios;
    }

    /// <summary>Pane ids in reading order.</summary>
    public IReadOnlyList<Guid> Leaves
    {
        get
        {
            var list = new List<Guid>();
            Collect(this, list);
            return list;
        }
    }

    private static void Collect(PaneTree node, List<Guid> into)
    {
        switch (node)
        {
            case Leaf leaf:
                into.Add(leaf.Id);
                break;
            case Split split:
                foreach (var child in split.Children) Collect(child, into);
                break;
        }
    }

    /// <summary>
    /// A tree with <paramref name="newLeaf"/> placed on <paramref name="side"/> of
    /// <paramref name="target"/>. When the target already sits in a split along
    /// that axis the new leaf becomes a sibling and takes half of the target's
    /// space; otherwise the target is replaced by a two-way split.
    /// </summary>
    public PaneTree Inserting(Guid newLeaf, Guid target, PaneSide side)
    {
        switch (this)
        {
            case Leaf leaf:
                if (leaf.Id != target) return this;
                PaneTree[] pair = side.Before()
                    ? [new Leaf(newLeaf), leaf]
                    : [leaf, new Leaf(newLeaf)];
                return new Split(side.Axis(), pair, [0.5, 0.5]);

            case Split split:
                if (split.Axis == side.Axis())
                {
                    var index = IndexOfLeaf(split.Children, target);
                    if (index >= 0)
                    {
                        var kids = split.Children.ToList();
                        var fractions = split.Ratios.ToList();
                        var share = fractions[index] / 2;
                        fractions[index] = share;
                        var at = side.Before() ? index : index + 1;
                        kids.Insert(at, new Leaf(newLeaf));
                        fractions.Insert(at, share);
                        return new Split(split.Axis, kids, fractions);
                    }
                }
                return new Split(split.Axis,
                                 split.Children.Select(c => c.Inserting(newLeaf, target, side)).ToList(),
                                 split.Ratios);

            default:
                return this;
        }
    }

    private static int IndexOfLeaf(IReadOnlyList<PaneTree> children, Guid id)
    {
        for (var i = 0; i < children.Count; i++)
        {
            if (children[i] is Leaf leaf && leaf.Id == id) return i;
        }
        return -1;
    }

    /// <summary>
    /// The tree without <paramref name="target"/>, giving its space to its
    /// siblings and collapsing splits left with one child. Null means the
    /// tree became empty.
    /// </summary>
    public PaneTree? Removing(Guid target)
    {
        switch (this)
        {
            case Leaf leaf:
                return leaf.Id == target ? null : this;

            case Split split:
                var kept = new List<PaneTree>();
                var fractions = new List<double>();
                for (var i = 0; i < split.Children.Count; i++)
                {
                    var remaining = split.Children[i].Removing(target);
                    if (remaining is null) continue;
                    var ratio = split.Ratios[i];
                    // Splice same-axis splits produced by a collapse into this level.
                    if (remaining is Split inner && inner.Axis == split.Axis)
                    {
                        kept.AddRange(inner.Children);
                        fractions.AddRange(inner.Ratios.Select(r => r * ratio));
                    }
                    else
                    {
                        kept.Add(remaining);
                        fractions.Add(ratio);
                    }
                }
                if (kept.Count == 0) return null;
                if (kept.Count == 1) return kept[0];
                var total = fractions.Sum();
                var normalized = total > 0
                    ? fractions.Select(f => f / total).ToList()
                    : fractions.Select(_ => 1.0 / kept.Count).ToList();
                return new Split(split.Axis, kept, normalized);

            default:
                return this;
        }
    }

    /// <summary>The tree with the split at <paramref name="path"/> (indices from the root) using <paramref name="ratios"/>.</summary>
    public PaneTree WithRatios(IReadOnlyList<double> ratios, IReadOnlyList<int> path, int depth = 0)
    {
        if (this is not Split split) return this;
        if (depth == path.Count)
        {
            return ratios.Count == split.Children.Count ? new Split(split.Axis, split.Children, ratios) : this;
        }
        var index = path[depth];
        if (index < 0 || index >= split.Children.Count) return this;
        var kids = split.Children.ToList();
        kids[index] = kids[index].WithRatios(ratios, path, depth + 1);
        return new Split(split.Axis, kids, split.Ratios);
    }

    /// <summary>The split at <paramref name="path"/>, or null when the path does not lead to one.</summary>
    public Split? SplitAt(IReadOnlyList<int> path)
    {
        var node = this;
        foreach (var index in path)
        {
            if (node is not Split split || index < 0 || index >= split.Children.Count) return null;
            node = split.Children[index];
        }
        return node as Split;
    }

    /// <summary>The tree with two panes trading places; sizes stay with the positions.</summary>
    public PaneTree Swapping(Guid a, Guid b)
    {
        switch (this)
        {
            case Leaf leaf:
                if (leaf.Id == a) return new Leaf(b);
                if (leaf.Id == b) return new Leaf(a);
                return this;
            case Split split:
                return new Split(split.Axis, split.Children.Select(c => c.Swapping(a, b)).ToList(), split.Ratios);
            default:
                return this;
        }
    }

    /// <summary>A compact description, e.g. <c>H[0.50 a | 0.50 V[0.50 b | 0.50 c]]</c>. For logs and tests.</summary>
    public string Describe(Func<Guid, string>? name = null)
    {
        name ??= id => id.ToString("N")[..4];
        return this switch
        {
            Leaf leaf => name(leaf.Id),
            Split split => (split.Axis == SplitAxis.Horizontal ? "H[" : "V[") +
                           string.Join(" | ", split.Children.Select((c, i) =>
                               $"{split.Ratios[i].ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} {c.Describe(name)}")) + "]",
            _ => "?",
        };
    }
}

/// <summary>A pane's rectangle within the workspace.</summary>
internal readonly record struct PaneSlot(Guid Id, Rect Bounds);

/// <summary>A draggable divider between two children of a split.</summary>
internal sealed record DividerSlot(IReadOnlyList<int> Path, int Index, SplitAxis Axis, Rect Bounds, double Usable)
{
    /// <summary>Identifies the divider across layout passes.</summary>
    public string Key => string.Join("/", Path) + ":" + Index;
}

/// <summary>Turns a pane tree into rectangles.</summary>
internal static class PaneLayout
{
    /// <summary>Width of the strip between panes, in device-independent pixels.</summary>
    public const double Divider = 5;

    /// <summary>A divider cannot be dragged to leave a pane smaller than this.</summary>
    public const double MinPane = 80;

    public static void Compute(PaneTree tree, Rect bounds, List<PaneSlot> panes, List<DividerSlot> dividers)
    {
        Place(tree, bounds, [], panes, dividers);
    }

    private static void Place(PaneTree node, Rect bounds, int[] path, List<PaneSlot> panes, List<DividerSlot> dividers)
    {
        switch (node)
        {
            case PaneTree.Leaf leaf:
                panes.Add(new PaneSlot(leaf.Id, bounds));
                break;

            case PaneTree.Split split:
                var count = split.Children.Count;
                var horizontal = split.Axis == SplitAxis.Horizontal;
                var extent = horizontal ? bounds.Width : bounds.Height;
                var usable = Math.Max(0, extent - Divider * (count - 1));
                var total = split.Ratios.Sum();
                var offset = horizontal ? bounds.X : bounds.Y;
                for (var i = 0; i < count; i++)
                {
                    var share = total > 0 ? split.Ratios[i] / total : 1.0 / count;
                    // The last child takes what is left, so rounding never leaves a gap.
                    var size = i == count - 1
                        ? Math.Max(0, (horizontal ? bounds.Right : bounds.Bottom) - offset)
                        : Math.Round(usable * share);
                    var child = horizontal
                        ? new Rect(offset, bounds.Y, size, bounds.Height)
                        : new Rect(bounds.X, offset, bounds.Width, size);
                    Place(split.Children[i], child, [.. path, i], panes, dividers);
                    offset += size;
                    if (i < count - 1)
                    {
                        var strip = horizontal
                            ? new Rect(offset, bounds.Y, Math.Min(Divider, Math.Max(0, bounds.Right - offset)), bounds.Height)
                            : new Rect(bounds.X, offset, bounds.Width, Math.Min(Divider, Math.Max(0, bounds.Bottom - offset)));
                        dividers.Add(new DividerSlot(path, i, split.Axis, strip, usable));
                        offset += Divider;
                    }
                }
                break;
        }
    }

    /// <summary>
    /// New ratios after the divider behind child <paramref name="index"/> moved by
    /// <paramref name="delta"/> pixels. Only the two neighbours change, and neither
    /// goes below the minimum pane size (unless it already was).
    /// </summary>
    public static IReadOnlyList<double> Dragged(IReadOnlyList<double> ratios, int index, double delta, double usable)
    {
        if (usable <= 0 || index < 0 || index + 1 >= ratios.Count) return ratios;
        var total = ratios.Sum();
        if (total <= 0) return ratios;
        var sizes = ratios.Select(r => r / total * usable).ToList();
        var pair = sizes[index] + sizes[index + 1];
        var min = Math.Min(MinPane, pair / 2);
        var first = Math.Clamp(sizes[index] + delta, min, pair - min);
        sizes[index] = first;
        sizes[index + 1] = pair - first;
        return sizes.Select(s => s / usable).ToList();
    }

    /// <summary>The drop zone for a pointer at <paramref name="point"/> within a pane of <paramref name="size"/>.</summary>
    public static DropZone ZoneFor(Point point, Size size)
    {
        if (size.Width <= 0 || size.Height <= 0) return DropZone.Center;
        var fx = point.X / size.Width;
        var fy = point.Y / size.Height;
        const double margin = 0.25;
        // Pick the edge the pointer is closest to when inside a margin band.
        (DropZone Zone, double Distance)[] distances =
        [
            (DropZone.Left, fx), (DropZone.Right, 1 - fx), (DropZone.Top, fy), (DropZone.Bottom, 1 - fy),
        ];
        var nearest = distances.MinBy(d => d.Distance);
        return nearest.Distance < margin ? nearest.Zone : DropZone.Center;
    }

    /// <summary>The part of a pane a drop in <paramref name="zone"/> would take.</summary>
    public static Rect ZoneRect(DropZone zone, Rect pane) => zone switch
    {
        DropZone.Left => new Rect(pane.X, pane.Y, pane.Width / 2, pane.Height),
        DropZone.Right => new Rect(pane.X + pane.Width / 2, pane.Y, pane.Width / 2, pane.Height),
        DropZone.Top => new Rect(pane.X, pane.Y, pane.Width, pane.Height / 2),
        DropZone.Bottom => new Rect(pane.X, pane.Y + pane.Height / 2, pane.Width, pane.Height / 2),
        _ => new Rect(pane.X + pane.Width * 0.12, pane.Y + pane.Height * 0.12, pane.Width * 0.76, pane.Height * 0.76),
    };
}
