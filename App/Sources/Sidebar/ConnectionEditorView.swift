import AppKit
import SwiftUI

enum AuthMethod: String, CaseIterable, Identifiable {
    case credential, stored, folder, agent, keyFile, password

    var id: String { rawValue }

    var label: String {
        switch self {
        case .credential: return "Use an existing credential"
        case .stored: return "Specify username and password"
        case .folder: return "Use the folder's credential"
        case .agent: return "Use the SSH agent"
        case .keyFile: return "Use a private key file"
        case .password: return "Ask for a password every time"
        }
    }

    static func available(for kind: ConnectionKind) -> [AuthMethod] {
        kind.usesSsh ? allCases : [.credential, .stored, .folder, .password]
    }

    /// Credential-based methods bring their own username; the rest need one.
    var needsUsername: Bool {
        switch self {
        case .credential, .folder: return false
        case .stored, .agent, .keyFile, .password: return true
        }
    }
}

/// The Type control: File Transfer covers SFTP, FTP and FTPS.
enum ConnectionFamily: String, CaseIterable, Identifiable {
    case ssh, rdp, vnc, files

    var id: String { rawValue }

    var label: String {
        switch self {
        case .ssh: return "SSH"
        case .rdp: return "RDP"
        case .vnc: return "VNC"
        case .files: return "File Transfer"
        }
    }

    init(_ kind: ConnectionKind) {
        switch kind {
        case .ssh: self = .ssh
        case .rdp: self = .rdp
        case .vnc: self = .vnc
        case .sftp, .ftp, .ftps: self = .files
        }
    }

    /// The kind chosen when switching to this family.
    var defaultKind: ConnectionKind {
        switch self {
        case .ssh: return .ssh
        case .rdp: return .rdp
        case .vnc: return .vnc
        case .files: return .sftp
        }
    }
}

/// Editable copy of a `Connection` for the form.
struct ConnectionDraft {
    var id = ""
    var name = ""
    var kind: ConnectionKind = .ssh
    var host = ""
    var port = 22
    var username = NSUserName()
    var folder = ""
    var authMethod: AuthMethod = .agent
    var credentialId: String?
    /// Password typed for "Username and password"; blank keeps the stored one.
    var password = ""
    var keyPath = ""
    var agentSocket = ""
    var tags: [String] = []
    var source: String?

    static let onePasswordAgentSocket = "~/Library/Group Containers/2BUA8C4S2C.com.1password/t/agent.sock"

    init() {}

    init(_ connection: Connection) {
        id = connection.id
        name = connection.name
        kind = connection.kind
        host = connection.host
        port = Int(connection.port)
        username = connection.username
        folder = connection.folder ?? ""
        tags = connection.tags
        source = connection.source
        switch connection.auth {
        case .password:
            authMethod = .password
        case .keyFile(let path):
            authMethod = .keyFile
            keyPath = path
        case .agent(let socketPath):
            authMethod = .agent
            agentSocket = socketPath ?? ""
        case .credential(let credentialId):
            authMethod = .credential
            self.credentialId = credentialId
        case .storedPassword:
            authMethod = .stored
        case .folderCredential:
            authMethod = .folder
        }
    }

    var validationError: String? {
        if name.trimmingCharacters(in: .whitespaces).isEmpty { return "Give the connection a name." }
        if host.trimmingCharacters(in: .whitespaces).isEmpty { return "Host is required." }
        if !(1...65535).contains(port) { return "Port must be between 1 and 65535." }
        if authMethod.needsUsername, username.trimmingCharacters(in: .whitespaces).isEmpty {
            return "Username is required."
        }
        switch authMethod {
        case .credential where credentialId == nil:
            return "Choose a credential."
        case .stored where id.isEmpty && password.isEmpty:
            return "Enter a password."
        case .keyFile where keyPath.trimmingCharacters(in: .whitespaces).isEmpty:
            return "Choose a private key file."
        default:
            break
        }
        return nil
    }

    func connection() -> Connection {
        let auth: AuthKind
        switch authMethod {
        case .password:
            auth = .password
        case .keyFile:
            auth = .keyFile(path: keyPath.trimmingCharacters(in: .whitespaces))
        case .agent:
            auth = .agent(socketPath: blankToNil(agentSocket))
        case .credential:
            auth = .credential(credentialId: credentialId ?? "")
        case .stored:
            auth = .storedPassword
        case .folder:
            auth = .folderCredential
        }
        return Connection(
            id: id,
            name: name.trimmingCharacters(in: .whitespaces),
            kind: kind,
            host: host.trimmingCharacters(in: .whitespaces),
            port: UInt16(clamping: port),
            // Credential-based methods take the credential's username.
            username: authMethod.needsUsername ? username.trimmingCharacters(in: .whitespaces) : "",
            auth: auth,
            folder: blankToNil(folder),
            tags: tags,
            source: source
        )
    }

    /// The password to store for "Username and password", if one was typed.
    func storedPassword() -> String? {
        authMethod == .stored ? blankToNil(password) : nil
    }
}

/// Picker over the credentials, grouped by where their secret lives.
struct CredentialPicker: View {
    let title: String
    let credentials: [Credential]
    @Binding var selection: String?
    var noneLabel = "Choose…"

    var body: some View {
        Picker(title, selection: $selection) {
            Text(noneLabel).tag(String?.none)
            section("Vault") { if case .vault = $0.kind { return true }; return false }
            section("1Password") { if case .onePassword = $0.kind { return true }; return false }
            section("Ask each time") { if case .prompt = $0.kind { return true }; return false }
        }
    }

    /// "Name (username)", or whichever of the two exists, so items without
    /// a title still read as something.
    static func label(_ credential: Credential) -> String {
        let name = credential.name.trimmingCharacters(in: .whitespaces)
        let user = credential.username.trimmingCharacters(in: .whitespaces)
        switch (name.isEmpty, user.isEmpty) {
        case (false, false): return name == user ? name : "\(name) (\(user))"
        case (false, true): return name
        case (true, false): return user
        case (true, true): return "Untitled credential"
        }
    }

    @ViewBuilder
    private func section(_ name: String, _ belongs: (Credential) -> Bool) -> some View {
        let items = credentials.filter(belongs)
            .sorted { Self.label($0).localizedCaseInsensitiveCompare(Self.label($1)) == .orderedAscending }
        if !items.isEmpty {
            Section(name) {
                ForEach(items) { credential in
                    Text(Self.label(credential)).tag(Optional(credential.id))
                }
            }
        }
    }
}

struct ConnectionEditorView: View {
    @ObservedObject var model: AppModel
    @State var draft: ConnectionDraft
    let onSave: (Connection, String?) -> Void
    let onCancel: () -> Void

    @State private var validation: String?

    var body: some View {
        VStack(spacing: 0) {
            Form {
                Section("Connection") {
                    Picker("Type", selection: Binding(
                        get: { ConnectionFamily(draft.kind) },
                        set: { family in
                            if ConnectionFamily(draft.kind) != family { draft.kind = family.defaultKind }
                        }
                    )) {
                        ForEach(ConnectionFamily.allCases) { Text($0.label).tag($0) }
                    }
                    .pickerStyle(.segmented)
                    if draft.kind.isFileTransfer {
                        Picker("Protocol", selection: $draft.kind) {
                            Text("SFTP (over SSH)").tag(ConnectionKind.sftp)
                            Text("FTP over TLS (explicit FTPS)").tag(ConnectionKind.ftps)
                            Text("FTP (not encrypted)").tag(ConnectionKind.ftp)
                        }
                    }
                    TextField("Name", text: $draft.name, prompt: Text("nutanix1"))
                    TextField("Host", text: $draft.host, prompt: Text("hostname or IP"))
                    TextField("Port", value: $draft.port, format: .number.grouping(.never))
                    TextField("Folder", text: $draft.folder, prompt: Text("Optional; use / for subfolders"))
                    if draft.kind == .rdp {
                        Text("The desktop follows the pane size.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    if draft.kind == .ftp {
                        Text("Plain FTP sends the password and files unencrypted. Prefer SFTP or FTPS when the server offers them.")
                            .font(.caption)
                            .foregroundStyle(.orange)
                    }
                }
                Section("Authentication") {
                    Picker("Method", selection: $draft.authMethod) {
                        ForEach(AuthMethod.available(for: draft.kind)) { method in
                            Text(method.label).tag(method)
                        }
                    }
                    if draft.authMethod.needsUsername {
                        TextField("Username", text: $draft.username,
                                  prompt: Text(draft.kind == .rdp ? "DOMAIN\\user or user@domain" : "user"))
                    }
                    switch draft.authMethod {
                    case .credential:
                        CredentialPicker(title: "Credential", credentials: model.credentials, selection: $draft.credentialId)
                        Text("The credential supplies the username and the secret.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                        if model.credentials.isEmpty {
                            Text("No credentials yet. Add one with File ▸ New Credential.")
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        }
                    case .stored:
                        SecureField("Password", text: $draft.password)
                        Text(draft.id.isEmpty
                             ? "Kept encrypted in the vault, just for this connection."
                             : "Leave blank to keep the stored password.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    case .folder:
                        if let inherited = model.inheritedCredential(forFolder: blankToNil(draft.folder)) {
                            Text("Uses “\(inherited.credential.name)” from folder “\(inherited.folder)”. A credential on a nearer folder takes over automatically.")
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        } else {
                            Text("No folder above this connection has a credential yet. Right-click a folder in the sidebar and choose Set Credential…, then pick this method.")
                                .font(.caption)
                                .foregroundStyle(.orange)
                        }
                    case .password:
                        Text("You'll be asked for the password every time.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    case .keyFile:
                        HStack {
                            TextField("Private key", text: $draft.keyPath, prompt: Text("~/.ssh/id_ed25519"))
                            Button("Choose…", action: chooseKey)
                        }
                        Text("An encrypted key prompts for its passphrase on connect.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    case .agent:
                        TextField("Agent socket", text: $draft.agentSocket, prompt: Text("Blank uses SSH_AUTH_SOCK"))
                        Button("Use 1Password's SSH agent") {
                            draft.agentSocket = ConnectionDraft.onePasswordAgentSocket
                        }
                    }
                    if draft.source != nil {
                        Text("Generated by a provider. The next sync replaces your edits.")
                            .font(.caption)
                            .foregroundStyle(.orange)
                    }
                }
            }
            .formStyle(.grouped)
            .onChange(of: draft.kind) { old, new in
                if draft.port == old.defaultPort { draft.port = new.defaultPort }
                if !AuthMethod.available(for: new).contains(draft.authMethod) { draft.authMethod = .credential }
            }

            if let validation {
                Text(validation)
                    .font(.caption)
                    .foregroundStyle(.red)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.horizontal, 20)
            }

            HStack {
                Spacer()
                Button("Cancel", action: onCancel)
                    .keyboardShortcut(.cancelAction)
                Button(draft.id.isEmpty ? "Add" : "Save", action: save)
                    .keyboardShortcut(.defaultAction)
                    .buttonStyle(.borderedProminent)
            }
            .padding(16)
        }
        .frame(width: 520, height: 580)
    }

    private func save() {
        if let problem = draft.validationError {
            validation = problem
            return
        }
        onSave(draft.connection(), draft.storedPassword())
    }

    private func chooseKey() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = false
        panel.showsHiddenFiles = true
        panel.directoryURL = URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent(".ssh")
        panel.message = "Choose a private key"
        if panel.runModal() == .OK, let url = panel.url {
            draft.keyPath = url.path
        }
    }
}
