import AppKit
import RoyalVNCKit

/// A VNC pane on RoyalVNCKit. Its framebuffer view does the drawing and the
/// input; this wrapper owns the connection, credentials and status.
final class VncSessionView: NSView, PaneContent, VNCConnectionDelegate {
    let connection: Connection

    private var vnc: VNCConnection?
    private var framebufferView: VNCCAFramebufferView?
    private var username = ""
    private var password = ""
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
    private var wasConnected = false

    init(connection: Connection) {
        self.connection = connection
        self.title = connection.name
        super.init(frame: .zero)
        wantsLayer = true
        layer?.backgroundColor = NSColor.black.cgColor
        status.alignment = .center
        status.textColor = .secondaryLabelColor
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

    override var acceptsFirstResponder: Bool { true }

    override func becomeFirstResponder() -> Bool {
        onFocus?()
        return true
    }

    override func mouseDown(with event: NSEvent) {
        onFocus?()
        super.mouseDown(with: event)
    }

    func start(username: String, password: String) {
        guard !terminated else { return }
        self.username = username
        self.password = password
        let settings = VNCConnection.Settings(
            isDebugLoggingEnabled: false,
            hostname: connection.host,
            port: connection.port,
            isShared: true,
            isScalingEnabled: true,
            useDisplayLink: true,
            inputMode: .forwardKeyboardShortcutsIfNotInUseLocally,
            isClipboardRedirectionEnabled: true,
            colorDepth: .depth24Bit,
            frameEncodings: .default
        )
        let vnc = VNCConnection(settings: settings)
        vnc.delegate = self
        self.vnc = vnc
        setStatus("Connecting to \(connection.host)…")
        vnc.connect()
    }

    func cancelled() {
        setStatus("Cancelled")
    }

    func failed(_ message: String) {
        setStatus(message)
    }

    func focus() {
        window?.makeFirstResponder(framebufferView ?? self)
    }

    func terminate() {
        terminated = true
        vnc?.disconnect()
        isLive = false
        password = ""
    }

    private func setStatus(_ text: String) {
        status.stringValue = text
        status.isHidden = text.isEmpty
    }

    private func onMain(_ work: @escaping @MainActor () -> Void) {
        if Thread.isMainThread {
            MainActor.assumeIsolated { work() }
        } else {
            DispatchQueue.main.async { MainActor.assumeIsolated { work() } }
        }
    }

    // MARK: VNCConnectionDelegate

    func connection(_ connection: VNCConnection, stateDidChange connectionState: VNCConnection.ConnectionState) {
        onMain { [self] in
            switch connectionState.status {
            case .connecting:
                setStatus("Connecting to \(self.connection.host)…")
            case .connected:
                isLive = true
                wasConnected = true
                setStatus("")
            case .disconnecting:
                break
            case .disconnected:
                isLive = false
                framebufferView?.removeFromSuperview()
                framebufferView = nil
                if let error = connectionState.error {
                    setStatus("Disconnected: \(error.localizedDescription)")
                    if !terminated { onSessionEnded?(false) }
                } else {
                    setStatus("Session ended")
                    if !terminated { onSessionEnded?(wasConnected) }
                }
                if vnc === connection { vnc = nil }
            @unknown default:
                break
            }
        }
    }

    func connection(_ connection: VNCConnection, credentialFor authenticationType: VNCAuthenticationType,
                    completion: @escaping (VNCCredential?) -> Void) {
        onMain { [self] in
            if authenticationType.requiresUsername {
                completion(VNCUsernamePasswordCredential(username: username, password: password))
            } else if authenticationType.requiresPassword {
                completion(VNCPasswordCredential(password: password))
            } else {
                completion(nil)
            }
        }
    }

    func connection(_ connection: VNCConnection, didCreateFramebuffer framebuffer: VNCFramebuffer) {
        onMain { [self] in installFramebufferView(framebuffer, connection: connection) }
    }

    func connection(_ connection: VNCConnection, didResizeFramebuffer framebuffer: VNCFramebuffer) {
        onMain { [self] in installFramebufferView(framebuffer, connection: connection) }
    }

    func connection(_ connection: VNCConnection, didUpdateFramebuffer framebuffer: VNCFramebuffer,
                    x: UInt16, y: UInt16, width: UInt16, height: UInt16) {
        // The framebuffer view draws itself.
    }

    func connection(_ connection: VNCConnection, didUpdateCursor cursor: VNCCursor) {
        // The framebuffer view manages the cursor.
    }

    private func installFramebufferView(_ framebuffer: VNCFramebuffer, connection: VNCConnection) {
        framebufferView?.removeFromSuperview()
        let fbView = VNCCAFramebufferView(frame: bounds, framebuffer: framebuffer,
                                          connection: connection, connectionDelegate: self)
        fbView.autoresizingMask = [.width, .height]
        addSubview(fbView, positioned: .below, relativeTo: status)
        framebufferView = fbView
        if window?.firstResponder === self {
            window?.makeFirstResponder(fbView)
        }
    }
}
