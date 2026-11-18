import AppKit

/// An NSSplitView that lays its children out by stored fractions and
/// reports the fractions back when the user drags a divider.
final class PaneSplitView: NSSplitView {
    /// Indices from the root of the layout tree to this split.
    var path: [Int] = []
    var ratios: [CGFloat] = []
    var onRatiosChanged: (([Int], [CGFloat]) -> Void)?
    private var applied = false
    private var applying = false
    // A split view must not be its own delegate: AppKit's sidebar support
    // forwards respondsToSelector: to the delegate and recurses forever.
    private let resizeObserver = ResizeObserver()

    private final class ResizeObserver: NSObject, NSSplitViewDelegate {
        weak var owner: PaneSplitView?
        func splitViewDidResizeSubviews(_ notification: Notification) {
            owner?.handleResize()
        }
    }

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        resizeObserver.owner = self
        delegate = resizeObserver
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError() }

    override func layout() {
        super.layout()
        if !applied { apply() }
    }

    private func apply() {
        let count = arrangedSubviews.count
        let total = isVertical ? bounds.width : bounds.height
        guard total > 0, count > 1, ratios.count == count else { return }
        applied = true
        applying = true
        let usable = total - dividerThickness * CGFloat(count - 1)
        var position: CGFloat = 0
        for index in 0 ..< count - 1 {
            position += usable * ratios[index]
            setPosition(position + dividerThickness * CGFloat(index), ofDividerAt: index)
        }
        applying = false
    }

    private func handleResize() {
        guard applied, !applying, arrangedSubviews.count > 1 else { return }
        let sizes = arrangedSubviews.map { isVertical ? $0.frame.width : $0.frame.height }
        let total = sizes.reduce(0, +)
        guard total > 0 else { return }
        let fractions = sizes.map { $0 / total }
        let changed = zip(fractions, ratios).contains { abs($0 - $1) > 0.002 }
        if changed || fractions.count != ratios.count {
            ratios = fractions
            onRatiosChanged?(path, fractions)
        }
    }
}

/// Owns the pane tree of one workspace and the views that render it.
final class PaneContainerViewController: NSViewController {
    private(set) var tree: PaneTree
    private var panes: [UUID: PaneView] = [:]
    private(set) var focusedID: UUID
    var onLayoutChange: (() -> Void)?
    /// Opens a connection in a pane chosen by a drop.
    var onOpenConnection: ((Connection, PaneView) -> Void)?
    /// Builds the header context menu for a pane.
    var contextMenuProvider: ((PaneView) -> NSMenu?)?
    private var clickMonitor: Any?

    init() {
        let id = UUID()
        tree = .leaf(id)
        focusedID = id
        super.init(nibName: nil, bundle: nil)
        panes[id] = makePane(id)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError() }

    override func loadView() {
        view = NSView()
        view.wantsLayer = true
        rebuild()
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        // Clicking anywhere in a pane makes it the active one. SwiftTerm's
        // view takes first responder itself; this just keeps our bookkeeping
        // in step without subclassing it.
        clickMonitor = NSEvent.addLocalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown, .otherMouseDown]) { [weak self] event in
            self?.noteClick(event)
            return event
        }
    }

    deinit {
        if let clickMonitor {
            NSEvent.removeMonitor(clickMonitor)
        }
    }

    private func noteClick(_ event: NSEvent) {
        guard let window = view.window, event.window === window else { return }
        for (id, pane) in panes where id != focusedID {
            let point = pane.convert(event.locationInWindow, from: nil)
            if pane.bounds.contains(point) {
                setFocus(id, makeFirstResponder: false)
                return
            }
        }
    }

    var paneCount: Int { tree.leaves.count }

    /// The focused pane. Focus is repaired if it points at a pane that is
    /// gone; the tree always has at least one leaf and every leaf has a view.
    var focusedPane: PaneView {
        if let pane = panes[focusedID] { return pane }
        let fallback = tree.leaves.first ?? panes.keys.first!
        focusedID = fallback
        return panes[fallback]!
    }

    var liveSessionCount: Int {
        panes.values.filter { $0.content?.isLive == true }.count
    }

    /// The pane a new session should open in: the focused one if it is
    /// empty, otherwise a fresh pane split to the right of it.
    func paneForNewSession() -> PaneView {
        focusedPane.content == nil ? focusedPane : insertPane(relativeTo: focusedID, side: .right)
    }

    @discardableResult
    func split(_ axis: SplitAxis) -> PaneView {
        insertPane(relativeTo: focusedID, side: axis == .horizontal ? .right : .bottom)
    }

    /// Adds an empty pane on `side` of `target` and focuses it.
    @discardableResult
    func insertPane(relativeTo target: UUID, side: PaneTree.Side) -> PaneView {
        let id = UUID()
        let pane = makePane(id)
        panes[id] = pane
        tree = tree.inserting(id, relativeTo: target, side: side)
        rebuild()
        setFocus(id, makeFirstResponder: true)
        return pane
    }

    /// Closes a pane's session. The last remaining pane is emptied, not removed.
    func closePane(_ id: UUID) {
        guard let pane = panes[id] else { return }
        pane.setContent(nil)
        guard tree.leaves.count > 1 else {
            setFocus(id, makeFirstResponder: true)
            return
        }
        let order = tree.leaves
        let index = order.firstIndex(of: id) ?? 0
        tree = tree.removing(id) ?? .leaf(id)
        let remaining = tree.leaves
        // Decide where focus goes before the pane disappears: rebuild()
        // notifies the window, which validates its toolbar and asks for the
        // focused pane, so focus must never point at a removed pane.
        let next = focusedID == id ? remaining[min(index, remaining.count - 1)] : focusedID
        let focusMoved = next != focusedID
        focusedID = next
        panes[id] = nil
        pane.removeFromSuperview()
        rebuild()
        setFocus(next, makeFirstResponder: focusMoved)
    }

    func focusNeighbor(_ delta: Int) {
        let order = tree.leaves
        guard let index = order.firstIndex(of: focusedID), order.count > 1 else { return }
        setFocus(order[(index + delta + order.count) % order.count], makeFirstResponder: true)
    }

    func setFocus(_ id: UUID, makeFirstResponder: Bool) {
        guard let pane = panes[id] else { return }
        focusedID = id
        for (paneID, view) in panes {
            view.isFocused = paneID == id
        }
        if makeFirstResponder {
            pane.focus()
        }
        onLayoutChange?()
    }

    func terminateAll() {
        panes.values.forEach { $0.setContent(nil) }
    }

    // MARK: Drops

    /// A pane dragged onto another: swap on the centre, or move it to that
    /// edge. The pane keeps its view and session throughout.
    private func dropPane(_ sourceID: UUID, onto targetID: UUID, zone: DropZone) {
        guard sourceID != targetID, let source = panes[sourceID], let target = panes[targetID] else { return }
        guard let side = zone.side else {
            let a = source.takeContent()
            let b = target.takeContent()
            source.setContent(b)
            target.setContent(a)
            setFocus(targetID, makeFirstResponder: true)
            return
        }
        guard let without = tree.removing(sourceID) else { return }
        tree = without.inserting(sourceID, relativeTo: targetID, side: side)
        rebuild()
        setFocus(sourceID, makeFirstResponder: true)
    }

    /// A sidebar connection dropped on a pane: fill it if empty, otherwise
    /// open next to it on the chosen edge.
    private func dropConnection(_ connection: Connection, onto targetID: UUID, zone: DropZone) {
        guard let target = panes[targetID] else { return }
        let pane: PaneView
        if let side = zone.side {
            pane = insertPane(relativeTo: targetID, side: side)
        } else if target.content == nil {
            pane = target
        } else {
            pane = insertPane(relativeTo: targetID, side: .right)
        }
        onOpenConnection?(connection, pane)
    }

    // MARK: Building

    private func makePane(_ id: UUID) -> PaneView {
        let pane = PaneView(id: id)
        pane.onFocusRequest = { [weak self] in self?.setFocus(id, makeFirstResponder: false) }
        pane.onCloseRequest = { [weak self] in self?.closePane(id) }
        pane.onDropPane = { [weak self] source, zone in self?.dropPane(source, onto: id, zone: zone) }
        pane.onDropConnection = { [weak self] connection, zone in self?.dropConnection(connection, onto: id, zone: zone) }
        pane.contextMenuProvider = { [weak self, weak pane] in
            guard let self, let pane else { return nil }
            return self.contextMenuProvider?(pane)
        }
        return pane
    }

    /// Takes a pane's session out of this workspace, closing the pane.
    func lift(_ pane: PaneView) -> PaneContent? {
        let content = pane.takeContent()
        closePane(pane.id)
        return content
    }

    /// Places a session arriving from another workspace.
    func adopt(_ content: PaneContent) {
        let pane = paneForNewSession()
        pane.setContent(content)
        setFocus(pane.id, makeFirstResponder: true)
    }

    private func rebuild() {
        view.subviews.forEach { $0.removeFromSuperview() }
        let root = build(tree, path: [])
        root.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(root)
        NSLayoutConstraint.activate([
            root.topAnchor.constraint(equalTo: view.topAnchor),
            root.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            root.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            root.bottomAnchor.constraint(equalTo: view.bottomAnchor),
        ])
        let multiple = tree.leaves.count > 1
        panes.values.forEach { $0.showsFocusRing = multiple }
        onLayoutChange?()
    }

    private func build(_ node: PaneTree, path: [Int]) -> NSView {
        switch node {
        case .leaf(let id):
            let pane = panes[id]!
            pane.removeFromSuperview()
            pane.translatesAutoresizingMaskIntoConstraints = true
            return pane
        case .split(let axis, let children, let ratios):
            let split = PaneSplitView()
            split.isVertical = axis == .horizontal
            split.dividerStyle = .thin
            split.path = path
            split.ratios = ratios
            split.onRatiosChanged = { [weak self] path, fractions in
                guard let self else { return }
                self.tree = self.tree.withRatios(fractions, atPath: path)
            }
            for (index, child) in children.enumerated() {
                split.addArrangedSubview(build(child, path: path + [index]))
            }
            return split
        }
    }
}
