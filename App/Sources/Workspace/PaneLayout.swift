import Foundation

enum SplitAxis: Equatable {
    /// Panes side by side.
    case horizontal
    /// Panes stacked.
    case vertical
}

/// Where a dragged session lands relative to a pane.
enum DropZone: Equatable {
    case center, left, right, top, bottom

    var side: PaneTree.Side? {
        switch self {
        case .center: return nil
        case .left: return .left
        case .right: return .right
        case .top: return .top
        case .bottom: return .bottom
        }
    }
}

/// The layout of one workspace: a tree of splits whose leaves are panes.
/// Each split keeps the fraction of space every child takes, so the
/// arrangement the user dragged out survives adding and closing panes.
indirect enum PaneTree: Equatable {
    case leaf(UUID)
    case split(SplitAxis, [PaneTree], [CGFloat])

    enum Side {
        case left, right, top, bottom

        var axis: SplitAxis {
            switch self {
            case .left, .right: return .horizontal
            case .top, .bottom: return .vertical
            }
        }

        /// Whether the new pane goes before the target in the split's order.
        var before: Bool {
            self == .left || self == .top
        }
    }

    var leaves: [UUID] {
        switch self {
        case .leaf(let id): return [id]
        case .split(_, let children, _): return children.flatMap(\.leaves)
        }
    }

    /// Returns a tree with `newLeaf` placed on `side` of `target`. When the
    /// target already sits in a split along that axis the new leaf becomes a
    /// sibling and takes half of the target's space; otherwise the target is
    /// replaced by a two-way split.
    func inserting(_ newLeaf: UUID, relativeTo target: UUID, side: Side) -> PaneTree {
        switch self {
        case .leaf(let id):
            guard id == target else { return self }
            let pair: [PaneTree] = side.before ? [.leaf(newLeaf), .leaf(id)] : [.leaf(id), .leaf(newLeaf)]
            return .split(side.axis, pair, [0.5, 0.5])
        case .split(let axis, let children, let ratios):
            if axis == side.axis,
               let index = children.firstIndex(where: { if case .leaf(let id) = $0 { return id == target }; return false }) {
                var kids = children
                var fractions = ratios
                let share = fractions[index] / 2
                fractions[index] = share
                let at = side.before ? index : index + 1
                kids.insert(.leaf(newLeaf), at: at)
                fractions.insert(share, at: at)
                return .split(axis, kids, fractions)
            }
            return .split(axis, children.map { $0.inserting(newLeaf, relativeTo: target, side: side) }, ratios)
        }
    }

    /// Returns the tree without `target`, giving its space to its siblings
    /// and collapsing splits left with one child. `nil` means the tree became empty.
    func removing(_ target: UUID) -> PaneTree? {
        switch self {
        case .leaf(let id):
            return id == target ? nil : self
        case .split(let axis, let children, let ratios):
            var kept: [PaneTree] = []
            var fractions: [CGFloat] = []
            for (child, ratio) in zip(children, ratios) {
                guard let remaining = child.removing(target) else { continue }
                // Splice same-axis splits produced by a collapse into this level.
                if case .split(let childAxis, let grandchildren, let childRatios) = remaining, childAxis == axis {
                    kept.append(contentsOf: grandchildren)
                    fractions.append(contentsOf: childRatios.map { $0 * ratio })
                } else {
                    kept.append(remaining)
                    fractions.append(ratio)
                }
            }
            if kept.isEmpty { return nil }
            if kept.count == 1 { return kept[0] }
            let total = fractions.reduce(0, +)
            let normalized = total > 0 ? fractions.map { $0 / total } : fractions.map { _ in 1 / CGFloat(kept.count) }
            return .split(axis, kept, normalized)
        }
    }

    /// Returns the tree with the split at `path` (indices from the root)
    /// using `ratios`.
    func withRatios(_ ratios: [CGFloat], atPath path: [Int]) -> PaneTree {
        guard case .split(let axis, let children, let current) = self else { return self }
        if path.isEmpty {
            return ratios.count == children.count ? .split(axis, children, ratios) : self
        }
        var kids = children
        let index = path[0]
        guard kids.indices.contains(index) else { return self }
        kids[index] = kids[index].withRatios(ratios, atPath: Array(path.dropFirst()))
        return .split(axis, kids, current)
    }
}
