import AppKit
import Combine

/// What the file browser needs from a session. The core's SFTP and FTP
/// sessions expose identical methods, so both conform as they are.
protocol FileTransferSession: AnyObject, Sendable {
    func home() async throws -> String
    func canonicalize(path: String) async throws -> String
    func list(path: String) async throws -> [SftpEntry]
    func download(remote: String, local: String, transferId: String) async throws -> UInt64
    func upload(local: String, remote: String, transferId: String) async throws -> UInt64
    func makeDir(path: String) async throws
    func rename(from: String, to: String) async throws
    func remove(path: String, directory: Bool) async throws
    func close() async
}

extension SftpSession: FileTransferSession {}
extension FtpSession: FileTransferSession {}

/// State for one file browser pane (SFTP, FTP or FTPS): the remote directory
/// being shown and the transfers in flight.
@MainActor
final class SftpModel: ObservableObject {
    struct Transfer: Identifiable {
        let id: String
        let name: String
        let upload: Bool
        var done: UInt64 = 0
        var total: UInt64 = 0
        var finished = false
        var error: String?
    }

    let connection: Connection

    @Published private(set) var path = "/"
    @Published private(set) var entries: [SftpEntry] = []
    @Published private(set) var busy = false
    @Published private(set) var isConnected = false
    @Published var status: String?
    @Published private(set) var transfers: [Transfer] = []

    private var session: (any FileTransferSession)?
    private var bridge: SftpBridge?
    private var closed = false

    init(connection: Connection) {
        self.connection = connection
    }

    // MARK: Session

    func connect(username: String, auth: SshAuth) async {
        status = "Connecting to \(username)@\(connection.host)…"
        busy = true
        defer { busy = false }
        let bridge = SftpBridge(owner: self)
        self.bridge = bridge
        let config = SshConfig(
            host: connection.host,
            port: connection.port,
            username: username,
            auth: auth,
            term: "xterm-256color",
            cols: 80,
            rows: 24,
            connectTimeoutSecs: 15,
            knownHostsPath: ProcessInfo.processInfo.environment["TOOLBOX_KNOWN_HOSTS"]
        )
        do {
            let session = try await SftpSession.connect(config: config, listener: bridge)
            await attach(session)
        } catch {
            status = describe(error)
        }
    }

    /// FTP, or explicit FTPS when `secure`. Plain FTP sends the password
    /// unencrypted, which the status line says while connecting.
    func connectFtp(username: String, password: String, secure: Bool) async {
        status = secure
            ? "Connecting to \(username)@\(connection.host) over FTPS…"
            : "Connecting to \(username)@\(connection.host) over plain FTP (not encrypted)…"
        busy = true
        defer { busy = false }
        let bridge = SftpBridge(owner: self)
        self.bridge = bridge
        let config = FtpConfig(
            host: connection.host,
            port: connection.port,
            username: username,
            password: password,
            secure: secure,
            acceptInvalidCerts: false,
            connectTimeoutSecs: 15
        )
        do {
            let session = try await FtpSession.connect(config: config, listener: bridge)
            await attach(session)
        } catch {
            status = describe(error)
        }
    }

    private func attach(_ session: any FileTransferSession) async {
        if closed {
            await session.close()
            return
        }
        self.session = session
        isConnected = true
        status = nil
        do {
            let home = try await session.home()
            await load(home)
        } catch {
            status = describe(error)
        }
    }

    func disconnect() {
        closed = true
        bridge?.detach()
        isConnected = false
        guard let session else { return }
        self.session = nil
        Task { await session.close() }
    }

    // MARK: Navigation

    func load(_ newPath: String) async {
        guard let session else { return }
        busy = true
        defer { busy = false }
        do {
            let resolved = try await session.canonicalize(path: newPath)
            let listing = try await session.list(path: resolved)
            path = resolved
            entries = listing
            status = nil
        } catch {
            status = describe(error)
        }
    }

    func refresh() async {
        await load(path)
    }

    func goUp() async {
        guard path != "/" else { return }
        let parent = (path as NSString).deletingLastPathComponent
        await load(parent.isEmpty ? "/" : parent)
    }

    func goHome() async {
        guard let session else { return }
        if let home = try? await session.home() {
            await load(home)
        }
    }

    func open(_ entry: SftpEntry) {
        if entry.isDir {
            Task { await load(entry.path) }
        } else {
            download([entry])
        }
    }

    // MARK: Transfers

    /// Downloads files into a folder the user picks.
    func download(_ selected: [SftpEntry]) {
        let files = selected.filter { !$0.isDir }
        guard !files.isEmpty, let session else { return }
        let panel = NSOpenPanel()
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.canCreateDirectories = true
        panel.allowsMultipleSelection = false
        panel.prompt = "Download Here"
        panel.message = files.count == 1 ? "Save \(files[0].name) to:" : "Save \(files.count) files to:"
        guard panel.runModal() == .OK, let folder = panel.url else { return }
        for file in files {
            // Only ever write a plain file name inside the chosen folder.
            let safeName = (file.name as NSString).lastPathComponent
            guard !safeName.isEmpty, safeName != ".", safeName != ".." else { continue }
            let local = folder.appendingPathComponent(safeName)
            if FileManager.default.fileExists(atPath: local.path),
               !Prompts.confirm(title: "Replace \(file.name)?",
                                message: "A file with that name already exists in \(folder.lastPathComponent).",
                                ok: "Replace") {
                continue
            }
            let id = newId()
            transfers.append(Transfer(id: id, name: file.name, upload: false, total: file.size))
            Task {
                do {
                    _ = try await session.download(remote: file.path, local: local.path, transferId: id)
                    finish(id, error: nil)
                } catch {
                    finish(id, error: describe(error))
                }
            }
        }
    }

    /// Uploads files the user picks into the current directory.
    func upload() {
        guard let session else { return }
        let panel = NSOpenPanel()
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = true
        panel.prompt = "Upload"
        panel.message = "Upload to \(path)"
        guard panel.runModal() == .OK else { return }
        let destinationDir = path
        for url in panel.urls {
            let remote = (destinationDir as NSString).appendingPathComponent(url.lastPathComponent)
            if entries.contains(where: { $0.name == url.lastPathComponent }),
               !Prompts.confirm(title: "Replace \(url.lastPathComponent)?",
                                message: "A file with that name already exists in \(destinationDir).",
                                ok: "Replace") {
                continue
            }
            let size = (try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? UInt64) ?? 0
            let id = newId()
            transfers.append(Transfer(id: id, name: url.lastPathComponent, upload: true, total: size))
            Task {
                do {
                    _ = try await session.upload(local: url.path, remote: remote, transferId: id)
                    finish(id, error: nil)
                    await refresh()
                } catch {
                    finish(id, error: describe(error))
                }
            }
        }
    }

    func updateProgress(id: String, done: UInt64, total: UInt64) {
        guard let index = transfers.firstIndex(where: { $0.id == id }) else { return }
        transfers[index].done = done
        if total > 0 { transfers[index].total = total }
    }

    private func finish(_ id: String, error: String?) {
        guard let index = transfers.firstIndex(where: { $0.id == id }) else { return }
        transfers[index].finished = true
        transfers[index].error = error
        if error == nil {
            transfers[index].done = max(transfers[index].done, transfers[index].total)
            // Keep a finished transfer visible briefly, then tidy the list.
            DispatchQueue.main.asyncAfter(deadline: .now() + 4) { [weak self] in
                MainActor.assumeIsolated {
                    self?.transfers.removeAll { $0.id == id && $0.error == nil }
                }
            }
        }
    }

    func clearFinishedTransfers() {
        transfers.removeAll(where: \.finished)
    }

    // MARK: Management

    func newFolder() {
        guard let session else { return }
        guard let name = Prompts.askText(title: "New Folder", message: "Create in \(path)", initial: "untitled folder", ok: "Create"),
              !name.trimmingCharacters(in: .whitespaces).isEmpty else { return }
        let target = (path as NSString).appendingPathComponent(name)
        Task {
            do {
                try await session.makeDir(path: target)
                await refresh()
            } catch {
                status = describe(error)
            }
        }
    }

    func rename(_ entry: SftpEntry) {
        guard let session else { return }
        guard let name = Prompts.askText(title: "Rename", message: entry.path, initial: entry.name, ok: "Rename"),
              !name.trimmingCharacters(in: .whitespaces).isEmpty, name != entry.name else { return }
        let target = ((entry.path as NSString).deletingLastPathComponent as NSString).appendingPathComponent(name)
        Task {
            do {
                try await session.rename(from: entry.path, to: target)
                await refresh()
            } catch {
                status = describe(error)
            }
        }
    }

    func delete(_ selected: [SftpEntry]) {
        guard let session, !selected.isEmpty else { return }
        let names = selected.map(\.name).joined(separator: ", ")
        guard Prompts.confirm(title: "Delete \(selected.count == 1 ? names : "\(selected.count) items")?",
                              message: "This removes them from the server. Folders must be empty.",
                              ok: "Delete") else { return }
        Task {
            for entry in selected {
                do {
                    try await session.remove(path: entry.path, directory: entry.isDir)
                } catch {
                    status = describe(error)
                }
            }
            await refresh()
        }
    }
}

/// Core callbacks arrive on Tokio threads.
final class SftpBridge: SftpListener, FtpListener, @unchecked Sendable {
    private weak var owner: SftpModel?
    private let lock = NSLock()

    init(owner: SftpModel) {
        self.owner = owner
    }

    func detach() {
        lock.lock()
        owner = nil
        lock.unlock()
    }

    func confirmHostKey(host: String, port: UInt16, keyType: String, fingerprint: String) -> Bool {
        var accepted = false
        DispatchQueue.main.sync {
            MainActor.assumeIsolated {
                accepted = Prompts.confirmHostKey(host: host, port: port, keyType: keyType, fingerprint: fingerprint)
            }
        }
        return accepted
    }

    func confirmUntrustedCertificate(host: String, reason: String) -> Bool {
        var accepted = false
        DispatchQueue.main.sync {
            MainActor.assumeIsolated {
                accepted = Prompts.confirm(
                    title: "The certificate for \(host) is not trusted",
                    message: "\(reason)\n\nThe connection would be encrypted, but Toolbox cannot confirm it is talking to \(host). Continue only if you expect this server to use a self-signed or private certificate.",
                    ok: "Connect Anyway")
            }
        }
        return accepted
    }

    func onProgress(transferId: String, done: UInt64, total: UInt64) {
        DispatchQueue.main.async { [self] in
            MainActor.assumeIsolated {
                owner?.updateProgress(id: transferId, done: done, total: total)
            }
        }
    }
}

extension SftpEntry: Identifiable {
    public var id: String { path }
}
