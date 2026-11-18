import AppKit
import SwiftUI

enum CredentialKindChoice: String, CaseIterable, Identifiable {
    case vault, onePassword, prompt

    var id: String { rawValue }

    var label: String {
        switch self {
        case .vault: return "Stored in the vault"
        case .onePassword: return "1Password item"
        case .prompt: return "Ask each time"
        }
    }
}

struct CredentialDraft {
    var id = ""
    var name = ""
    var username = ""
    var folder = ""
    var kind: CredentialKindChoice = .vault
    var password = ""
    var privateKey = ""
    var passphrase = ""
    var itemId = ""
    var account = ""
    var opPath = ""
    var hasPrivateKey = false
    var tags: [String] = []
    var source: String?

    init() {}

    init(_ credential: Credential) {
        id = credential.id
        name = credential.name
        username = credential.username
        folder = credential.folder ?? ""
        hasPrivateKey = credential.hasPrivateKey
        tags = credential.tags
        source = credential.source
        switch credential.kind {
        case .vault:
            kind = .vault
        case .onePassword(let itemId, let account, let opPath):
            kind = .onePassword
            self.itemId = itemId
            self.account = account ?? ""
            self.opPath = opPath ?? ""
        case .prompt:
            kind = .prompt
        }
    }

    var validationError: String? {
        if name.trimmingCharacters(in: .whitespaces).isEmpty { return "Give the credential a name." }
        switch kind {
        case .onePassword where itemId.trimmingCharacters(in: .whitespaces).isEmpty:
            return "Enter the 1Password item id (op item list shows it)."
        case .vault where id.isEmpty && password.isEmpty && privateKey.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty:
            return "Enter a password or a private key."
        default:
            return nil
        }
    }

    func credential() -> Credential {
        let kind: CredentialKind
        switch self.kind {
        case .vault:
            kind = .vault
        case .onePassword:
            kind = .onePassword(itemId: itemId.trimmingCharacters(in: .whitespaces),
                                account: blankToNil(account),
                                opPath: blankToNil(opPath))
        case .prompt:
            kind = .prompt
        }
        return Credential(
            id: id,
            name: name.trimmingCharacters(in: .whitespaces),
            username: username.trimmingCharacters(in: .whitespaces),
            kind: kind,
            folder: blankToNil(folder),
            hasPrivateKey: hasPrivateKey,
            tags: tags,
            source: source
        )
    }

    /// Secret fields entered this time. Only meaningful for vault credentials.
    func secret() -> Secret? {
        guard kind == .vault else { return nil }
        return Secret(password: blankToNil(password),
                      privateKey: blankToNil(privateKey),
                      passphrase: blankToNil(passphrase))
    }
}

func blankToNil(_ value: String) -> String? {
    let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
    return trimmed.isEmpty ? nil : trimmed
}

struct CredentialEditorView: View {
    @State var draft: CredentialDraft
    let onSave: (Credential, Secret?) -> Void
    let onCancel: () -> Void

    @State private var validation: String?

    var body: some View {
        VStack(spacing: 0) {
            Form {
                Section("Credential") {
                    TextField("Name", text: $draft.name, prompt: Text("Domain admin"))
                    TextField("Username", text: $draft.username, prompt: Text("DOMAIN\\user or root"))
                    TextField("Folder", text: $draft.folder, prompt: Text("Optional; use / for subfolders"))
                }
                Section("Secret") {
                    Picker("Kept", selection: $draft.kind) {
                        ForEach(CredentialKindChoice.allCases) { Text($0.label).tag($0) }
                    }
                    switch draft.kind {
                    case .vault:
                        SecureField("Password", text: $draft.password)
                        VStack(alignment: .leading, spacing: 4) {
                            HStack {
                                Text("Private key")
                                Spacer()
                                Button("Load Key File…", action: loadKeyFile)
                            }
                            TextEditor(text: $draft.privateKey)
                                .font(.system(.caption, design: .monospaced))
                                .frame(height: 80)
                                .overlay(RoundedRectangle(cornerRadius: 4).stroke(.separator))
                        }
                        SecureField("Key passphrase", text: $draft.passphrase)
                        Text(draft.id.isEmpty
                             ? "Encrypted in the local vault. Opening the vault asks for Touch ID."
                             : "Leave a field blank to keep what is stored.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    case .onePassword:
                        TextField("Item id", text: $draft.itemId, prompt: Text("from op item list"))
                        TextField("Account", text: $draft.account, prompt: Text("Optional shorthand, e.g. work"))
                        TextField("op path", text: $draft.opPath, prompt: Text(detectOpPath() ?? "auto-detect"))
                        Text("Fetched with the 1Password CLI when used. Nothing is stored locally.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    case .prompt:
                        Text("Toolbox asks for the password every time this credential is used.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    if draft.source != nil {
                        Text("Generated by a provider. The next sync replaces your edits.")
                            .font(.caption)
                            .foregroundStyle(.orange)
                    }
                }
            }
            .formStyle(.grouped)

            if let validation {
                Text(validation)
                    .font(.caption)
                    .foregroundStyle(.red)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.horizontal, 20)
            }

            HStack {
                Spacer()
                Button("Cancel", action: onCancel).keyboardShortcut(.cancelAction)
                Button(draft.id.isEmpty ? "Add" : "Save", action: save)
                    .keyboardShortcut(.defaultAction)
                    .buttonStyle(.borderedProminent)
            }
            .padding(16)
        }
        .frame(width: 520, height: 560)
    }

    private func save() {
        if let problem = draft.validationError {
            validation = problem
            return
        }
        onSave(draft.credential(), draft.secret())
    }

    private func loadKeyFile() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = false
        panel.showsHiddenFiles = true
        panel.directoryURL = URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent(".ssh")
        panel.message = "Choose a private key to copy into the vault"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            draft.privateKey = try String(contentsOf: url, encoding: .utf8)
        } catch {
            validation = "Couldn't read \(url.lastPathComponent): \(error.localizedDescription)"
        }
    }
}
