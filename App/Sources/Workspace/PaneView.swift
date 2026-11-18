import AppKit

extension NSPasteboard.PasteboardType {
    static let pane = NSPasteboard.PasteboardType("com.dobbs.toolbox.pane")
}

/// Something that can live in a pane: a terminal, a file browser, an RDP
/// or VNC surface.
@MainActor
protocol PaneContent: AnyObject {
    var view: NSView { get }
    var title: String { get }
    var isLive: Bool { get }
    var onTitleChange: ((String) -> Void)? { get set }
    var onStateChange: (() -> Void)? { get set }
    var onFocus: (() -> Void)? { get set }
    /// The session is over. `true` for a clean end (sign-out, logout,
    /// closed by us), `false` after a failure worth leaving on screen.
    var onSessionEnded: ((Bool) -> Void)? { get set }
    func focus()
    func terminate()
}

/// The strip above a session; dragging it moves the pane.
final class PaneHeaderView: NSView {
    var onBeginDrag: ((NSEvent) -> Void)?
    var onClick: (() -> Void)?
    var menuProvider: (() -> NSMenu?)?
    private var downPoint: NSPoint?

    override func menu(for event: NSEvent) -> NSMenu? {
        menuProvider?()
    }

    override func mouseDown(with event: NSEvent) {
        downPoint = event.locationInWindow
        onClick?()
    }

    override func mouseDragged(with event: NSEvent) {
        guard let start = downPoint else { return }
        if hypot(event.locationInWindow.x - start.x, event.locationInWindow.y - start.y) > 4 {
            downPoint = nil
            onBeginDrag?(event)
        }
    }

    override func mouseUp(with event: NSEvent) {
        downPoint = nil
    }

    override func resetCursorRects() {
        addCursorRect(bounds, cursor: .openHand)
    }
}

/// Translucent highlight of where a drop would land.
final class DropOverlayView: NSView {
    var zone: DropZone? {
        didSet { isHidden = zone == nil; needsDisplay = true }
    }

    override var isFlipped: Bool { true }

    override func hitTest(_ point: NSPoint) -> NSView? { nil }

    override func draw(_ dirtyRect: NSRect) {
        guard let zone else { return }
        let b = bounds
        let rect: NSRect
        switch zone {
        case .center: rect = b.insetBy(dx: b.width * 0.12, dy: b.height * 0.12)
        case .left: rect = NSRect(x: 0, y: 0, width: b.width / 2, height: b.height)
        case .right: rect = NSRect(x: b.width / 2, y: 0, width: b.width / 2, height: b.height)
        case .top: rect = NSRect(x: 0, y: 0, width: b.width, height: b.height / 2)
        case .bottom: rect = NSRect(x: 0, y: b.height / 2, width: b.width, height: b.height / 2)
        }
        let path = NSBezierPath(roundedRect: rect.insetBy(dx: 4, dy: 4), xRadius: 8, yRadius: 8)
        NSColor.controlAccentColor.withAlphaComponent(0.22).setFill()
        path.fill()
        NSColor.controlAccentColor.withAlphaComponent(0.9).setStroke()
        path.lineWidth = 2
        path.stroke()
    }
}

/// One cell of the workspace layout: a header strip plus the session view,
/// or a placeholder when empty. Panes can be dragged by their header and
/// accept drops of other panes and of sidebar connections.
final class PaneView: NSView, NSDraggingSource {
    let id: UUID
    private(set) var content: PaneContent?
    var onFocusRequest: (() -> Void)?
    var onCloseRequest: (() -> Void)?
    var onDropPane: ((UUID, DropZone) -> Void)?
    var onDropConnection: ((Connection, DropZone) -> Void)?
    /// Context menu for the header (move to workspace, close).
    var contextMenuProvider: (() -> NSMenu?)?

    var isFocused = false {
        didSet { updateBorder() }
    }

    /// Whether to draw the focus border at all (pointless with a single pane).
    var showsFocusRing = false {
        didSet { updateBorder() }
    }

    private let header = PaneHeaderView()
    private let statusDot = NSView()
    private let titleLabel = NSTextField(labelWithString: "")
    private let placeholder = NSTextField(wrappingLabelWithString: "No session\n\nDouble-click a connection in the sidebar, drag one here,\nor press ⌘N to create one.")
    private let dropOverlay = DropOverlayView()
    private var contentConstraints: [NSLayoutConstraint] = []

    init(id: UUID) {
        self.id = id
        super.init(frame: .zero)
        wantsLayer = true
        layer?.borderWidth = 1
        buildHeader()
        buildPlaceholder()
        dropOverlay.translatesAutoresizingMaskIntoConstraints = false
        dropOverlay.zone = nil
        addSubview(dropOverlay)
        NSLayoutConstraint.activate([
            dropOverlay.topAnchor.constraint(equalTo: topAnchor),
            dropOverlay.leadingAnchor.constraint(equalTo: leadingAnchor),
            dropOverlay.trailingAnchor.constraint(equalTo: trailingAnchor),
            dropOverlay.bottomAnchor.constraint(equalTo: bottomAnchor),
        ])
        registerForDraggedTypes([.pane, .sidebarNode])
        updateBorder()
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError() }

    // MARK: Content

    func setContent(_ newContent: PaneContent?) {
        content?.terminate()
        attach(newContent)
    }

    /// Detaches the content without ending its session, for moving it to
    /// another pane.
    func takeContent() -> PaneContent? {
        let current = content
        attach(nil)
        return current
    }

    private func attach(_ newContent: PaneContent?) {
        content?.view.removeFromSuperview()
        content?.onTitleChange = nil
        content?.onStateChange = nil
        content?.onFocus = nil
        content?.onSessionEnded = nil
        NSLayoutConstraint.deactivate(contentConstraints)
        contentConstraints = []
        content = newContent

        guard let newContent else {
            header.isHidden = true
            placeholder.isHidden = false
            titleLabel.stringValue = ""
            return
        }

        header.isHidden = false
        placeholder.isHidden = true
        titleLabel.stringValue = newContent.title
        newContent.onTitleChange = { [weak self] title in self?.titleLabel.stringValue = title }
        newContent.onStateChange = { [weak self] in self?.refreshStatus() }
        newContent.onFocus = { [weak self] in
            guard let self, !self.isFocused else { return }
            self.onFocusRequest?()
        }
        // A clean end (sign-out, logout) takes the pane with it after a
        // moment; a failure stays visible so the message can be read.
        newContent.onSessionEnded = { [weak self, weak newContent] clean in
            guard clean else { return }
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.7) {
                MainActor.assumeIsolated {
                    guard let self, let ended = newContent, self.content === ended else { return }
                    self.onCloseRequest?()
                }
            }
        }

        let view = newContent.view
        view.translatesAutoresizingMaskIntoConstraints = false
        addSubview(view, positioned: .below, relativeTo: dropOverlay)
        contentConstraints = [
            view.topAnchor.constraint(equalTo: header.bottomAnchor),
            view.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 1),
            view.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -1),
            view.bottomAnchor.constraint(equalTo: bottomAnchor, constant: -1),
        ]
        NSLayoutConstraint.activate(contentConstraints)
        refreshStatus()
    }

    func focus() {
        if let content {
            content.focus()
        } else {
            window?.makeFirstResponder(self)
        }
    }

    override var acceptsFirstResponder: Bool { true }

    override func becomeFirstResponder() -> Bool {
        if !isFocused { onFocusRequest?() }
        return true
    }

    override func mouseDown(with event: NSEvent) {
        if !isFocused { onFocusRequest?() }
        super.mouseDown(with: event)
    }

    private func refreshStatus() {
        let live = content?.isLive ?? false
        statusDot.layer?.backgroundColor = (live ? NSColor.systemGreen : NSColor.systemGray).cgColor
    }

    private func updateBorder() {
        let color: NSColor = (isFocused && showsFocusRing)
            ? NSColor.controlAccentColor.withAlphaComponent(0.8)
            : NSColor.separatorColor.withAlphaComponent(0.6)
        layer?.borderColor = color.cgColor
    }

    // MARK: Dragging this pane

    private func beginDrag(with event: NSEvent) {
        guard content != nil else { return }
        let item = NSPasteboardItem()
        item.setString(id.uuidString, forType: .pane)
        let dragItem = NSDraggingItem(pasteboardWriter: item)
        let image = snapshot()
        let scale = min(1, 280 / max(image.size.width, 1))
        let size = NSSize(width: image.size.width * scale, height: image.size.height * scale)
        let point = convert(event.locationInWindow, from: nil)
        dragItem.setDraggingFrame(NSRect(x: point.x - size.width / 2, y: point.y - size.height / 2, width: size.width, height: size.height),
                                  contents: image)
        beginDraggingSession(with: [dragItem], event: event, source: self)
    }

    private func snapshot() -> NSImage {
        let image = NSImage(size: bounds.size)
        if let rep = bitmapImageRepForCachingDisplay(in: bounds) {
            cacheDisplay(in: bounds, to: rep)
            image.addRepresentation(rep)
        }
        return image
    }

    func draggingSession(_ session: NSDraggingSession, sourceOperationMaskFor context: NSDraggingContext) -> NSDragOperation {
        context == .withinApplication ? .move : []
    }

    // MARK: Accepting drops

    private func zone(for sender: NSDraggingInfo) -> DropZone {
        let p = convert(sender.draggingLocation, from: nil)
        let b = bounds
        guard b.width > 0, b.height > 0 else { return .center }
        let fx = p.x / b.width
        let fy = p.y / b.height // 0 at the bottom; PaneView is not flipped
        let margin: CGFloat = 0.25
        // Pick the edge the pointer is closest to when inside a margin band.
        let distances: [(DropZone, CGFloat)] = [(.left, fx), (.right, 1 - fx), (.bottom, fy), (.top, 1 - fy)]
        if let nearest = distances.min(by: { $0.1 < $1.1 }), nearest.1 < margin {
            return nearest.0
        }
        return .center
    }

    private func payload(_ sender: NSDraggingInfo) -> (UUID?, Connection?) {
        let pasteboard = sender.draggingPasteboard
        if let text = pasteboard.pasteboardItems?.compactMap({ $0.string(forType: .pane) }).first, let uuid = UUID(uuidString: text) {
            return (uuid, nil)
        }
        let nodeIDs = pasteboard.pasteboardItems?.compactMap { $0.string(forType: .sidebarNode) } ?? []
        for nodeID in nodeIDs {
            let prefixes = ["conn:", "all-conn:"]
            for prefix in prefixes where nodeID.hasPrefix(prefix) {
                let connectionID = String(nodeID.dropFirst(prefix.count))
                if let connection = AppModel.shared.connections.first(where: { $0.id == connectionID }) {
                    return (nil, connection)
                }
            }
        }
        return (nil, nil)
    }

    override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation {
        draggingUpdated(sender)
    }

    override func draggingUpdated(_ sender: NSDraggingInfo) -> NSDragOperation {
        let (paneID, connection) = payload(sender)
        guard paneID != nil || connection != nil else {
            dropOverlay.zone = nil
            return []
        }
        let zone = zone(for: sender)
        if paneID == id && zone == .center {
            dropOverlay.zone = nil
            return []
        }
        dropOverlay.zone = zone
        return .move
    }

    override func draggingExited(_ sender: NSDraggingInfo?) {
        dropOverlay.zone = nil
    }

    override func draggingEnded(_ sender: NSDraggingInfo) {
        dropOverlay.zone = nil
    }

    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        defer { dropOverlay.zone = nil }
        let zone = zone(for: sender)
        let (paneID, connection) = payload(sender)
        if let paneID {
            guard paneID != id || zone != .center else { return false }
            onDropPane?(paneID, zone)
            return true
        }
        if let connection {
            onDropConnection?(connection, zone)
            return true
        }
        return false
    }

    // MARK: Building

    private func buildHeader() {
        header.translatesAutoresizingMaskIntoConstraints = false
        header.wantsLayer = true
        header.layer?.backgroundColor = NSColor.windowBackgroundColor.cgColor
        header.isHidden = true
        header.toolTip = "Drag to rearrange"
        header.onBeginDrag = { [weak self] event in self?.beginDrag(with: event) }
        header.onClick = { [weak self] in
            guard let self, !self.isFocused else { return }
            self.onFocusRequest?()
        }
        header.menuProvider = { [weak self] in
            guard let self else { return nil }
            if !self.isFocused { self.onFocusRequest?() }
            return self.contextMenuProvider?()
        }
        addSubview(header)

        statusDot.translatesAutoresizingMaskIntoConstraints = false
        statusDot.wantsLayer = true
        statusDot.layer?.cornerRadius = 4
        statusDot.layer?.backgroundColor = NSColor.systemGray.cgColor

        titleLabel.font = NSFont.systemFont(ofSize: 11, weight: .medium)
        titleLabel.textColor = .secondaryLabelColor
        titleLabel.lineBreakMode = .byTruncatingTail
        titleLabel.translatesAutoresizingMaskIntoConstraints = false

        let grip = NSImageView(image: NSImage(systemSymbolName: "line.3.horizontal", accessibilityDescription: "Drag handle")!)
        grip.contentTintColor = .tertiaryLabelColor
        grip.translatesAutoresizingMaskIntoConstraints = false

        let close = NSButton(image: NSImage(systemSymbolName: "xmark", accessibilityDescription: "Close pane")!,
                             target: self, action: #selector(closeTapped))
        close.isBordered = false
        close.imageScaling = .scaleProportionallyDown
        close.contentTintColor = .secondaryLabelColor
        close.toolTip = "Close pane"
        close.translatesAutoresizingMaskIntoConstraints = false

        header.addSubview(statusDot)
        header.addSubview(titleLabel)
        header.addSubview(grip)
        header.addSubview(close)

        NSLayoutConstraint.activate([
            header.topAnchor.constraint(equalTo: topAnchor, constant: 1),
            header.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 1),
            header.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -1),
            header.heightAnchor.constraint(equalToConstant: 24),

            statusDot.leadingAnchor.constraint(equalTo: header.leadingAnchor, constant: 8),
            statusDot.centerYAnchor.constraint(equalTo: header.centerYAnchor),
            statusDot.widthAnchor.constraint(equalToConstant: 8),
            statusDot.heightAnchor.constraint(equalToConstant: 8),

            titleLabel.leadingAnchor.constraint(equalTo: statusDot.trailingAnchor, constant: 6),
            titleLabel.centerYAnchor.constraint(equalTo: header.centerYAnchor),
            titleLabel.trailingAnchor.constraint(lessThanOrEqualTo: grip.leadingAnchor, constant: -6),

            grip.centerXAnchor.constraint(equalTo: header.centerXAnchor),
            grip.centerYAnchor.constraint(equalTo: header.centerYAnchor),
            grip.widthAnchor.constraint(equalToConstant: 14),
            grip.heightAnchor.constraint(equalToConstant: 14),

            close.trailingAnchor.constraint(equalTo: header.trailingAnchor, constant: -6),
            close.centerYAnchor.constraint(equalTo: header.centerYAnchor),
            close.widthAnchor.constraint(equalToConstant: 18),
            close.heightAnchor.constraint(equalToConstant: 18),
        ])
    }

    private func buildPlaceholder() {
        placeholder.alignment = .center
        placeholder.textColor = .tertiaryLabelColor
        placeholder.font = NSFont.systemFont(ofSize: 13)
        placeholder.translatesAutoresizingMaskIntoConstraints = false
        addSubview(placeholder)
        NSLayoutConstraint.activate([
            placeholder.centerXAnchor.constraint(equalTo: centerXAnchor),
            placeholder.centerYAnchor.constraint(equalTo: centerYAnchor),
            placeholder.widthAnchor.constraint(lessThanOrEqualTo: widthAnchor, constant: -40),
        ])
    }

    @objc private func closeTapped() {
        onCloseRequest?()
    }
}
