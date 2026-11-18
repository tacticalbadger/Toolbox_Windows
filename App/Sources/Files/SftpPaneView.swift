import AppKit
import Combine
import SwiftUI

/// A file browser (SFTP, FTP or FTPS) living in a workspace pane.
final class SftpPaneView: NSView, PaneContent {
    let connection: Connection
    let model: SftpModel

    private let host: NSHostingView<SftpBrowserView>
    private var cancellable: AnyCancellable?

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
        self.model = SftpModel(connection: connection)
        self.title = "Files · \(connection.name)"
        self.host = NSHostingView(rootView: SftpBrowserView(model: model))
        super.init(frame: .zero)
        host.translatesAutoresizingMaskIntoConstraints = false
        addSubview(host)
        NSLayoutConstraint.activate([
            host.topAnchor.constraint(equalTo: topAnchor),
            host.leadingAnchor.constraint(equalTo: leadingAnchor),
            host.trailingAnchor.constraint(equalTo: trailingAnchor),
            host.bottomAnchor.constraint(equalTo: bottomAnchor),
        ])
        cancellable = model.$isConnected.sink { [weak self] connected in
            self?.isLive = connected
        }
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError() }

    override func mouseDown(with event: NSEvent) {
        onFocus?()
        super.mouseDown(with: event)
    }

    func start(username: String, auth: SshAuth) {
        Task { await model.connect(username: username, auth: auth) }
    }

    func startFtp(username: String, password: String, secure: Bool) {
        Task { await model.connectFtp(username: username, password: password, secure: secure) }
    }

    func cancelled() {
        model.status = "Cancelled"
    }

    func failed(_ message: String) {
        model.status = message
    }

    func focus() {
        window?.makeFirstResponder(host)
    }

    func terminate() {
        model.disconnect()
    }
}
