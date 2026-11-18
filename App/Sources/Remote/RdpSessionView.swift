import AppKit
import IOSurface

/// A remote desktop pane. Frames come from the Rust core as dirty rectangles
/// which are copied into an IOSurface that a layer displays; the surface is
/// sized to the remote desktop, which in turn follows this view's pixel size.
final class RdpSessionView: NSView, PaneContent {
    let connection: Connection

    private var session: RdpSession?
    private var bridge: RdpBridge?
    private let surfaceLayer = CALayer()
    private var surface: IOSurface?
    private var frameWidth = 0
    private var frameHeight = 0
    private var username = ""
    private var password = ""
    private var domain: String?
    private var pendingResize: DispatchWorkItem?
    private var lastRequestedSize: (Int, Int) = (0, 0)
    private var trackingArea: NSTrackingArea?
    private var modifierFlags: NSEvent.ModifierFlags = []
    private var terminated = false
    private let status = NSTextField(wrappingLabelWithString: "")

    // MARK: PaneContent

    var view: NSView { self }
    private(set) var title: String {
        didSet { onTitleChange?(title) }
    }
    private(set) var isLive = false {
        didSet { onStateChange?() }
    }
    var onTitleChange: ((String) -> Void)?
    var onStateChange: (() -> Void)?
    var onFocus: (() -> Void)?
    var onSessionEnded: ((Bool) -> Void)?

    init(connection: Connection) {
        self.connection = connection
        self.title = connection.name
        super.init(frame: .zero)
        wantsLayer = true
        layer?.backgroundColor = NSColor.black.cgColor
        surfaceLayer.contentsGravity = .resize
        surfaceLayer.magnificationFilter = .linear
        surfaceLayer.minificationFilter = .linear
        surfaceLayer.isOpaque = true
        layer?.addSublayer(surfaceLayer)

        status.alignment = .center
        status.textColor = .secondaryLabelColor
        status.font = NSFont.systemFont(ofSize: 13)
        status.translatesAutoresizingMaskIntoConstraints = false
        addSubview(status)
        NSLayoutConstraint.activate([
            status.centerXAnchor.constraint(equalTo: centerXAnchor),
            status.centerYAnchor.constraint(equalTo: centerYAnchor),
            status.widthAnchor.constraint(lessThanOrEqualTo: widthAnchor, constant: -40),
        ])
        setStatus("Resolving credentials for \(connection.name)…")
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError() }

    override var isFlipped: Bool { true }
    override var acceptsFirstResponder: Bool { true }

    override func becomeFirstResponder() -> Bool {
        onFocus?()
        return true
    }

    override func layout() {
        super.layout()
        surfaceLayer.frame = bounds
        scheduleResize()
    }

    override func viewDidChangeBackingProperties() {
        super.viewDidChangeBackingProperties()
        surfaceLayer.contentsScale = window?.backingScaleFactor ?? 2
        scheduleResize()
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let trackingArea { removeTrackingArea(trackingArea) }
        let area = NSTrackingArea(rect: bounds,
                                  options: [.mouseMoved, .mouseEnteredAndExited, .activeInKeyWindow, .inVisibleRect],
                                  owner: self, userInfo: nil)
        addTrackingArea(area)
        trackingArea = area
    }

    // MARK: Lifecycle

    func start(username: String, password: String, domain: String?) {
        guard !terminated else { return }
        self.username = username
        self.password = password
        self.domain = domain
        connect()
    }

    func cancelled() {
        setStatus("Cancelled")
    }

    func failed(_ message: String) {
        setStatus(message)
    }

    func focus() {
        window?.makeFirstResponder(self)
    }

    func terminate() {
        terminated = true
        pendingResize?.cancel()
        bridge?.detach()
        bridge = nil
        session?.close()
        session = nil
        isLive = false
        password = ""
    }

    private func connect() {
        let (width, height) = pixelSize()
        lastRequestedSize = (width, height)
        let bridge = RdpBridge(owner: self)
        self.bridge = bridge
        let config = RdpConfig(
            host: connection.host,
            port: connection.port,
            username: username,
            password: password,
            domain: domain,
            width: UInt16(width),
            height: UInt16(height),
            scaleFactor: scalePercent(),
            acceptInvalidCerts: true,
            connectTimeoutSecs: 20
        )
        setStatus("Connecting to \(connection.host)…")
        session = RdpSession.connect(config: config, listener: bridge)
    }

    private func scalePercent() -> UInt32 {
        let scale = window?.backingScaleFactor ?? 2
        return UInt32((scale * 100).rounded())
    }

    /// The desktop size to ask for: this view's size in device pixels, even
    /// numbers, within what RDP accepts.
    private func pixelSize() -> (Int, Int) {
        let scale = window?.backingScaleFactor ?? 2
        var width = Int(bounds.width * scale)
        var height = Int(bounds.height * scale)
        width = min(max(width, 200), 8192) & ~1
        height = min(max(height, 200), 8192) & ~1
        return (width, height)
    }

    private func scheduleResize() {
        guard session != nil, isLive else { return }
        let size = pixelSize()
        guard size != lastRequestedSize else { return }
        pendingResize?.cancel()
        let work = DispatchWorkItem { [weak self] in
            guard let self else { return }
            MainActor.assumeIsolated {
                let size = self.pixelSize()
                guard size != self.lastRequestedSize else { return }
                self.lastRequestedSize = size
                self.session?.resize(width: UInt16(size.0), height: UInt16(size.1), scaleFactor: self.scalePercent())
            }
        }
        pendingResize = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3, execute: work)
    }

    private func setStatus(_ text: String) {
        status.stringValue = text
        status.isHidden = text.isEmpty
    }

    // MARK: Frames (called by the bridge on the main thread)

    func handleConnected(width: UInt16, height: UInt16) {
        isLive = true
        setStatus("")
        allocateSurface(width: Int(width), height: Int(height))
        // Some servers ignore the size asked for at connect; bring the desktop
        // to the pane's pixel size through Display Control.
        let wanted = pixelSize()
        if (Int(width), Int(height)) != wanted {
            lastRequestedSize = wanted
            session?.resize(width: UInt16(wanted.0), height: UInt16(wanted.1), scaleFactor: scalePercent())
        }
    }

    func handleResized(width: UInt16, height: UInt16) {
        allocateSurface(width: Int(width), height: Int(height))
    }

    func handleRegion(x: UInt16, y: UInt16, width: UInt16, height: UInt16) {
        guard let session, let surface else { return }
        let x = Int(x), y = Int(y)
        let w = min(Int(width), frameWidth - x)
        let h = min(Int(height), frameHeight - y)
        guard w > 0, h > 0 else { return }
        let data = session.copyRegion(x: UInt16(x), y: UInt16(y), width: UInt16(w), height: UInt16(h))
        guard data.count >= w * h * 4 else { return }

        surface.lock(options: [], seed: nil)
        let base = surface.baseAddress
        let rowBytes = surface.bytesPerRow
        data.withUnsafeBytes { src in
            guard let srcBase = src.baseAddress else { return }
            for row in 0..<h {
                let dst = base.advanced(by: (y + row) * rowBytes + x * 4)
                memcpy(dst, srcBase.advanced(by: row * w * 4), w * 4)
            }
        }
        surface.unlock(options: [], seed: nil)

        CATransaction.begin()
        CATransaction.setDisableActions(true)
        surfaceLayer.contents = nil
        surfaceLayer.contents = surface
        CATransaction.commit()
    }

    func handleError(_ message: String) {
        setStatus(message)
    }

    func handleClosed(_ reason: String, clean: Bool) {
        isLive = false
        session = nil
        guard !terminated else { return }
        if clean {
            setStatus("Session ended: \(reason)")
            onSessionEnded?(true)
        } else {
            setStatus("Disconnected: \(reason)")
            onSessionEnded?(false)
        }
    }

    private func allocateSurface(width: Int, height: Int) {
        guard width > 0, height > 0 else { return }
        frameWidth = width
        frameHeight = height
        surface = IOSurface(properties: [
            .width: width,
            .height: height,
            .bytesPerElement: 4,
            .pixelFormat: kCVPixelFormatType_32BGRA,
        ])
        if let surface {
            // Start black rather than showing uninitialised memory.
            surface.lock(options: [], seed: nil)
            memset(surface.baseAddress, 0, surface.allocationSize)
            surface.unlock(options: [], seed: nil)
            CATransaction.begin()
            CATransaction.setDisableActions(true)
            surfaceLayer.contents = surface
            CATransaction.commit()
        }
    }

    // MARK: Mouse

    private func remotePoint(_ event: NSEvent) -> (UInt16, UInt16)? {
        guard frameWidth > 0, frameHeight > 0, bounds.width > 0, bounds.height > 0 else { return nil }
        let p = convert(event.locationInWindow, from: nil)
        let x = Int((p.x / bounds.width * CGFloat(frameWidth)).rounded())
        let y = Int((p.y / bounds.height * CGFloat(frameHeight)).rounded())
        return (UInt16(min(max(x, 0), frameWidth - 1)), UInt16(min(max(y, 0), frameHeight - 1)))
    }

    private func sendMove(_ event: NSEvent) {
        guard let (x, y) = remotePoint(event) else { return }
        session?.mouseMove(x: x, y: y)
    }

    private func sendButton(_ event: NSEvent, _ button: RdpMouseButton, pressed: Bool) {
        if let (x, y) = remotePoint(event) {
            session?.mouseMove(x: x, y: y)
        }
        session?.mouseButton(button: button, pressed: pressed)
    }

    override func mouseMoved(with event: NSEvent) { sendMove(event) }
    override func mouseDragged(with event: NSEvent) { sendMove(event) }
    override func rightMouseDragged(with event: NSEvent) { sendMove(event) }
    override func otherMouseDragged(with event: NSEvent) { sendMove(event) }

    override func mouseDown(with event: NSEvent) {
        window?.makeFirstResponder(self)
        sendButton(event, .left, pressed: true)
    }
    override func mouseUp(with event: NSEvent) { sendButton(event, .left, pressed: false) }
    override func rightMouseDown(with event: NSEvent) { sendButton(event, .right, pressed: true) }
    override func rightMouseUp(with event: NSEvent) { sendButton(event, .right, pressed: false) }

    override func otherMouseDown(with event: NSEvent) {
        sendButton(event, otherButton(event), pressed: true)
    }
    override func otherMouseUp(with event: NSEvent) {
        sendButton(event, otherButton(event), pressed: false)
    }

    private func otherButton(_ event: NSEvent) -> RdpMouseButton {
        switch event.buttonNumber {
        case 3: return .x1
        case 4: return .x2
        default: return .middle
        }
    }

    override func scrollWheel(with event: NSEvent) {
        if let (x, y) = remotePoint(event) {
            session?.mouseMove(x: x, y: y)
        }
        // RDP counts wheel notches in units of 120; trackpads deliver fine deltas.
        let factor: CGFloat = event.hasPreciseScrollingDeltas ? 8 : 120
        let dy = Int(event.scrollingDeltaY * factor)
        let dx = Int(event.scrollingDeltaX * factor)
        if dy != 0 { session?.scroll(vertical: true, units: Int16(clamping: dy)) }
        if dx != 0 { session?.scroll(vertical: false, units: Int16(clamping: -dx)) }
    }

    // MARK: Keyboard

    override func keyDown(with event: NSEvent) {
        guard let session else { return }
        if let sc = MacKeyCodes.scancode(forKeyCode: event.keyCode) {
            session.key(scancode: sc.code, extended: sc.extended, pressed: true)
        } else if let chars = event.characters {
            for scalar in chars.unicodeScalars {
                session.unicode(codePoint: scalar.value, pressed: true)
                session.unicode(codePoint: scalar.value, pressed: false)
            }
        }
    }

    override func keyUp(with event: NSEvent) {
        guard let sc = MacKeyCodes.scancode(forKeyCode: event.keyCode) else { return }
        session?.key(scancode: sc.code, extended: sc.extended, pressed: false)
    }

    override func flagsChanged(with event: NSEvent) {
        guard let session, let sc = MacKeyCodes.scancode(forKeyCode: event.keyCode) else {
            modifierFlags = event.modifierFlags
            return
        }
        let mask = modifierMask(for: event.keyCode)
        let pressed = event.modifierFlags.contains(mask)
        session.key(scancode: sc.code, extended: sc.extended, pressed: pressed)
        modifierFlags = event.modifierFlags
    }

    private func modifierMask(for keyCode: UInt16) -> NSEvent.ModifierFlags {
        switch keyCode {
        case 0x38, 0x3C: return .shift
        case 0x3B, 0x3E: return .control
        case 0x3A, 0x3D: return .option
        case 0x37, 0x36: return .command
        case 0x39: return .capsLock
        default: return []
        }
    }

    /// Keeps ⌘-shortcuts for the remote session rather than the local menu,
    /// except the ones that manage panes and workspaces.
    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        guard isLive, event.modifierFlags.contains(.command) else { return false }
        let reserved: Set<String> = ["t", "w", "n", "d", "q", "r", "[", "]", ",", "h", "m"]
        if let chars = event.charactersIgnoringModifiers?.lowercased(), reserved.contains(chars) {
            return false
        }
        keyDown(with: event)
        return true
    }
}

/// Core callbacks arrive on Tokio threads; dirty rectangles are merged and
/// delivered once per main-thread turn.
final class RdpBridge: RdpListener, @unchecked Sendable {
    private weak var owner: RdpSessionView?
    private let lock = NSLock()
    private var dirty: (x: Int, y: Int, maxX: Int, maxY: Int)?
    private var flushScheduled = false

    init(owner: RdpSessionView) {
        self.owner = owner
    }

    func detach() {
        lock.lock()
        owner = nil
        lock.unlock()
    }

    private func onMain(_ work: @escaping @MainActor () -> Void) {
        DispatchQueue.main.async {
            MainActor.assumeIsolated { work() }
        }
    }

    func onConnected(width: UInt16, height: UInt16) {
        onMain { [self] in owner?.handleConnected(width: width, height: height) }
    }

    func onDesktopResized(width: UInt16, height: UInt16) {
        lock.lock()
        dirty = nil
        lock.unlock()
        onMain { [self] in owner?.handleResized(width: width, height: height) }
    }

    func onRegionUpdated(x: UInt16, y: UInt16, width: UInt16, height: UInt16) {
        lock.lock()
        let (x0, y0, x1, y1) = (Int(x), Int(y), Int(x) + Int(width), Int(y) + Int(height))
        if let d = dirty {
            dirty = (min(d.x, x0), min(d.y, y0), max(d.maxX, x1), max(d.maxY, y1))
        } else {
            dirty = (x0, y0, x1, y1)
        }
        let schedule = !flushScheduled
        flushScheduled = true
        lock.unlock()
        if schedule {
            onMain { [self] in flush() }
        }
    }

    @MainActor
    private func flush() {
        lock.lock()
        let region = dirty
        dirty = nil
        flushScheduled = false
        lock.unlock()
        guard let region else { return }
        owner?.handleRegion(x: UInt16(region.x), y: UInt16(region.y),
                            width: UInt16(region.maxX - region.x), height: UInt16(region.maxY - region.y))
    }

    func onError(message: String) {
        onMain { [self] in owner?.handleError(message) }
    }

    func onClosed(reason: String, clean: Bool) {
        onMain { [self] in owner?.handleClosed(reason, clean: clean) }
    }
}
