import AppKit
import SwiftTerm

/// A terminal pane bound to one SSH session from the Rust core.
final class TerminalSessionView: NSView, PaneContent {
    let connection: Connection

    private var auth: SshAuth?
    private var username: String
    private let terminal: TerminalView
    private var session: SshSession?
    private var bridge: SessionBridge?
    private var cols = 80
    private var rows = 24
    private var reconnectPending = false
    private var askedForPassphrase = false
    private var metalEnabled = false
    private var pendingResize: DispatchWorkItem?
    private var terminated = false

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

    /// Creates the pane in a "resolving" state; call `start` once the
    /// credentials are known.
    init(connection: Connection) {
        self.connection = connection
        self.auth = nil
        self.username = connection.username
        self.title = connection.name
        self.terminal = TerminalView(frame: NSRect(x: 0, y: 0, width: 800, height: 500))
        super.init(frame: .zero)

        terminal.terminalDelegate = self
        terminal.translatesAutoresizingMaskIntoConstraints = false
        addSubview(terminal)
        NSLayoutConstraint.activate([
            terminal.topAnchor.constraint(equalTo: topAnchor),
            terminal.leadingAnchor.constraint(equalTo: leadingAnchor),
            terminal.trailingAnchor.constraint(equalTo: trailingAnchor),
            terminal.bottomAnchor.constraint(equalTo: bottomAnchor),
        ])
        ThemeStore.shared.apply(to: terminal)
        NotificationCenter.default.addObserver(self, selector: #selector(themeChanged),
                                               name: ThemeStore.changed, object: nil)
        feed("\u{1b}[2mResolving credentials for \(connection.name)…\u{1b}[0m\r\n")
    }

    /// Begins the SSH session with resolved credentials. Ignored once the
    /// pane has been closed, so a slow prompt can't leave a zombie session.
    func start(username: String, auth: SshAuth) {
        guard !terminated else { return }
        self.username = username
        self.auth = auth
        connect()
    }

    /// The user declined a prompt while resolving credentials.
    func cancelled() {
        feed("\u{1b}[2m[Cancelled]\u{1b}[0m\r\n")
        isLive = false
    }

    /// Credentials could not be resolved.
    func failed(_ message: String) {
        feed("\u{1b}[31m\(message)\u{1b}[0m\r\n")
        isLive = false
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError() }

    deinit {
        NotificationCenter.default.removeObserver(self)
    }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        guard window != nil, !metalEnabled else { return }
        do {
            try terminal.setUseMetal(true)
            metalEnabled = true
        } catch {
            NSLog("Metal renderer unavailable, staying on CoreGraphics: \(error)")
        }
    }

    @objc private func themeChanged() {
        ThemeStore.shared.apply(to: terminal)
    }

    func focus() {
        window?.makeFirstResponder(terminal)
    }

    func terminate() {
        terminated = true
        pendingResize?.cancel()
        bridge?.detach()
        bridge = nil
        session?.close()
        session = nil
        isLive = false
    }

    // MARK: Session lifecycle

    private func connect() {
        guard let auth else { return }
        isLive = false
        feed("\u{1b}[2mConnecting to \(username)@\(connection.host):\(connection.port)…\u{1b}[0m\r\n")
        let bridge = SessionBridge(owner: self)
        self.bridge = bridge
        let config = SshConfig(
            host: connection.host,
            port: connection.port,
            username: username,
            auth: auth,
            term: "xterm-256color",
            cols: UInt16(cols),
            rows: UInt16(rows),
            connectTimeoutSecs: 15,
            // Normally ~/.ssh/known_hosts, shared with OpenSSH. Overridable for tests.
            knownHostsPath: ProcessInfo.processInfo.environment["TOOLBOX_KNOWN_HOSTS"]
        )
        session = SshSession.connect(config: config, listener: bridge)
    }

    private func feed(_ text: String) {
        terminal.feed(text: text)
    }

    // Called by SessionBridge, always on the main thread.

    func handleConnected() {
        isLive = true
    }

    func handleData(_ data: Data) {
        terminal.feed(byteArray: ArraySlice([UInt8](data)))
    }

    func handleError(_ message: String) {
        if !askedForPassphrase,
           case .keyFile(let path, let passphrase)? = auth,
           passphrase == nil,
           message.contains("cannot load key") {
            askedForPassphrase = true
            if let secret = Prompts.askSecret(title: "Passphrase for \(path)",
                                              message: "The private key is encrypted.") {
                auth = .keyFile(path: path, passphrase: secret)
                reconnectPending = true
                return
            }
        }
        feed("\r\n\u{1b}[31m\(message)\u{1b}[0m\r\n")
    }

    func handleClosed(clean: Bool) {
        isLive = false
        session = nil
        guard !terminated else { return }
        if reconnectPending {
            reconnectPending = false
            connect()
        } else if clean {
            feed("\r\n\u{1b}[2m[Session ended]\u{1b}[0m\r\n")
            onSessionEnded?(true)
        } else {
            feed("\r\n\u{1b}[2m[Disconnected]\u{1b}[0m\r\n")
            onSessionEnded?(false)
        }
    }

    func handleHostKey(host: String, port: UInt16, keyType: String, fingerprint: String) -> Bool {
        Prompts.confirmHostKey(host: host, port: port, keyType: keyType, fingerprint: fingerprint)
    }
}

// MARK: - TerminalViewDelegate

extension TerminalSessionView: TerminalViewDelegate {
    func send(source: TerminalView, data: ArraySlice<UInt8>) {
        session?.write(data: Data(data))
    }

    func sizeChanged(source: TerminalView, newCols: Int, newRows: Int) {
        cols = max(newCols, 2)
        rows = max(newRows, 1)
        // Layout passes fire this many times in a row; send the settled size.
        pendingResize?.cancel()
        let work = DispatchWorkItem { [weak self] in
            guard let self else { return }
            MainActor.assumeIsolated {
                self.session?.resize(cols: UInt16(self.cols), rows: UInt16(self.rows))
            }
        }
        pendingResize = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.08, execute: work)
    }

    func setTerminalTitle(source: TerminalView, title: String) {
        self.title = title.isEmpty ? connection.name : "\(connection.name) · \(title)"
    }

    func hostCurrentDirectoryUpdate(source: TerminalView, directory: String?) {}

    func scrolled(source: TerminalView, position: Double) {}

    func requestOpenLink(source: TerminalView, link: String, params: [String: String]) {
        guard let url = URL(string: link), ["http", "https", "mailto"].contains(url.scheme ?? "") else { return }
        NSWorkspace.shared.open(url)
    }

    func bell(source: TerminalView) {
        NSSound.beep()
    }

    func clipboardCopy(source: TerminalView, content: Data) {
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        pasteboard.setString(String(decoding: content, as: UTF8.self), forType: .string)
    }

    /// Remote programs may not read the local clipboard.
    func clipboardRead(source: TerminalView) -> Data? { nil }

    func iTermContent(source: TerminalView, content: ArraySlice<UInt8>) {}

    func rangeChanged(source: TerminalView, startY: Int, endY: Int) {}
}

// MARK: - Bridge from core threads to the main thread

/// Receives core callbacks on Tokio threads, coalesces output, and hands
/// everything to the view on the main thread in order.
final class SessionBridge: SshListener, @unchecked Sendable {
    private weak var owner: TerminalSessionView?
    private let lock = NSLock()
    private var pending = Data()
    private var flushScheduled = false

    init(owner: TerminalSessionView) {
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

    func onConnected() {
        onMain { [self] in owner?.handleConnected() }
    }

    func onData(data: Data) {
        lock.lock()
        pending.append(data)
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
        let chunk = pending
        pending = Data()
        flushScheduled = false
        lock.unlock()
        guard !chunk.isEmpty else { return }
        owner?.handleData(chunk)
    }

    func onError(message: String) {
        onMain { [self] in owner?.handleError(message) }
    }

    func onClosed(clean: Bool) {
        onMain { [self] in owner?.handleClosed(clean: clean) }
    }

    func confirmHostKey(host: String, port: UInt16, keyType: String, fingerprint: String) -> Bool {
        var accepted = false
        DispatchQueue.main.sync {
            MainActor.assumeIsolated {
                accepted = owner?.handleHostKey(host: host, port: port, keyType: keyType, fingerprint: fingerprint) ?? false
            }
        }
        return accepted
    }
}
