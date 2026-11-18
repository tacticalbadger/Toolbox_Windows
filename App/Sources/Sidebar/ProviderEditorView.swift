import SwiftUI

enum ProviderKindChoice: String, CaseIterable, Identifiable {
    case onePassword, activeDirectory

    var id: String { rawValue }

    var label: String {
        switch self {
        case .onePassword: return "1Password"
        case .activeDirectory: return "Active Directory"
        }
    }
}

/// One of the Active Directory provider's accounts.
enum ProviderRole: String, CaseIterable {
    case bind, admin, user

    var title: String {
        switch self {
        case .bind: return "Bind as"
        case .admin: return "Domain controllers use"
        case .user: return "Other computers use"
        }
    }

    var accountLabel: String {
        switch self {
        case .bind: return "bind account"
        case .admin: return "domain controller account"
        case .user: return "computer account"
        }
    }

    /// Id of the vault credential the provider owns for this role when the
    /// username and password are typed into the provider itself.
    func managedId(provider: String) -> String { "\(provider)#\(rawValue)" }
}

/// How a role gets its credential: none, an existing credential, or a
/// username and password entered here (kept in the vault, owned by the
/// provider).
struct RoleCredentialDraft {
    enum Mode: String { case none, existing, specify }
    var mode: Mode = .none
    var credentialId: String?
    var username = ""
    /// Blank keeps the stored password when editing.
    var password = ""
    /// A provider-owned credential already exists for this role.
    var hasStored = false
}

struct ProviderDraft {
    var id = ""
    var name = ""
    /// Folder the provider sits in; its own node is named after it.
    var parent = ""
    /// Name of the provider's own folder node; follows `name` for new ones.
    var leaf = ""
    var kind: ProviderKindChoice = .onePassword
    var autoRefreshMinutes = 60
    var opPath = ""
    var account = ""
    var vaults = ""
    var server = ""
    var searchBase = ""
    var bind = RoleCredentialDraft(mode: .existing)
    var admin = RoleCredentialDraft()
    var user = RoleCredentialDraft()
    var allowPlainLdap = true
    var acceptInvalidCerts = false
    var lastSyncEpoch: UInt64?
    var lastStatus: String?

    init() {}

    @MainActor
    init(_ provider: Provider) {
        id = provider.id
        name = provider.name
        parent = (provider.folder as NSString).deletingLastPathComponent
        leaf = (provider.folder as NSString).lastPathComponent
        autoRefreshMinutes = Int(provider.autoRefreshMinutes)
        lastSyncEpoch = provider.lastSyncEpoch
        lastStatus = provider.lastStatus
        switch provider.kind {
        case .onePassword(let opPath, let account, let vaults):
            kind = .onePassword
            self.opPath = opPath ?? ""
            self.account = account ?? ""
            self.vaults = vaults.joined(separator: ", ")
        case .activeDirectory(let server, let searchBase, let bind, let admin, let user, let plain, let invalid):
            kind = .activeDirectory
            self.server = server
            self.searchBase = searchBase
            self.bind = Self.role(.bind, id: bind, provider: provider.id)
            self.admin = Self.role(.admin, id: admin, provider: provider.id)
            self.user = Self.role(.user, id: user, provider: provider.id)
            allowPlainLdap = plain
            acceptInvalidCerts = invalid
        }
    }

    @MainActor
    private static func role(_ role: ProviderRole, id: String?, provider: String) -> RoleCredentialDraft {
        guard let id else { return RoleCredentialDraft(mode: role == .bind ? .existing : .none) }
        if id == role.managedId(provider: provider) {
            let stored = AppModel.shared.credential(id)
            return RoleCredentialDraft(mode: .specify, credentialId: nil,
                                       username: stored?.username ?? "", password: "", hasStored: stored != nil)
        }
        return RoleCredentialDraft(mode: .existing, credentialId: id)
    }

    subscript(role: ProviderRole) -> RoleCredentialDraft {
        get {
            switch role {
            case .bind: return bind
            case .admin: return admin
            case .user: return user
            }
        }
        set {
            switch role {
            case .bind: bind = newValue
            case .admin: admin = newValue
            case .user: user = newValue
            }
        }
    }

    var validationError: String? {
        if name.trimmingCharacters(in: .whitespaces).isEmpty { return "Give the provider a name." }
        if kind == .activeDirectory {
            if server.trimmingCharacters(in: .whitespaces).isEmpty { return "Enter the domain controller to query." }
            if searchBase.trimmingCharacters(in: .whitespaces).isEmpty { return "Enter the search base, e.g. DC=corp,DC=example." }
            if bind.mode == .none { return "Choose how to bind (your regular domain account)." }
            for role in ProviderRole.allCases {
                let r = self[role]
                switch r.mode {
                case .none:
                    break
                case .existing where r.credentialId == nil:
                    return "\(role.title): choose a credential."
                case .specify where r.username.trimmingCharacters(in: .whitespaces).isEmpty:
                    return "\(role.title): enter a username."
                case .specify where r.password.isEmpty && !r.hasStored:
                    return "\(role.title): enter a password."
                default:
                    break
                }
            }
        }
        return nil
    }

    /// The credential id a role resolves to once saved.
    func credentialId(_ role: ProviderRole) -> String? {
        let r = self[role]
        switch r.mode {
        case .none: return nil
        case .existing: return r.credentialId
        case .specify: return role.managedId(provider: id)
        }
    }

    /// Provider-owned accounts to write to the vault before the provider.
    func managedAccounts(folder: String) -> [(credential: Credential, password: String?)] {
        guard kind == .activeDirectory else { return [] }
        let label = name.trimmingCharacters(in: .whitespaces)
        return ProviderRole.allCases.compactMap { role in
            let r = self[role]
            guard r.mode == .specify else { return nil }
            let record = Credential(
                id: role.managedId(provider: id),
                name: "\(label) \(role.accountLabel)",
                username: r.username.trimmingCharacters(in: .whitespaces),
                kind: .vault,
                folder: folder,
                hasPrivateKey: false,
                tags: ["provider-account"],
                source: nil
            )
            return (record, r.password.isEmpty ? nil : r.password)
        }
    }

    func provider() -> Provider {
        let kind: ProviderKind
        switch self.kind {
        case .onePassword:
            let list = vaults.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
            kind = .onePassword(opPath: blankToNil(opPath), account: blankToNil(account), vaults: list)
        case .activeDirectory:
            kind = .activeDirectory(server: server.trimmingCharacters(in: .whitespaces),
                                    searchBase: searchBase.trimmingCharacters(in: .whitespaces),
                                    bindCredentialId: credentialId(.bind),
                                    adminCredentialId: credentialId(.admin),
                                    userCredentialId: credentialId(.user),
                                    allowPlainLdap: allowPlainLdap,
                                    acceptInvalidCerts: acceptInvalidCerts)
        }
        let trimmedName = name.trimmingCharacters(in: .whitespaces)
        let node = leaf.trimmingCharacters(in: .whitespaces).isEmpty ? trimmedName : leaf.trimmingCharacters(in: .whitespaces)
        let location = parent.trimmingCharacters(in: .whitespaces)
        return Provider(
            id: id,
            name: trimmedName,
            kind: kind,
            folder: location.isEmpty ? node : "\(location)/\(node)",
            autoRefreshMinutes: UInt32(max(autoRefreshMinutes, 0)),
            lastSyncEpoch: lastSyncEpoch,
            lastStatus: lastStatus
        )
    }
}

struct ProviderEditorView: View {
    @ObservedObject var model: AppModel
    @State var draft: ProviderDraft
    let onSave: (ProviderDraft, Bool) -> Void
    let onCancel: () -> Void

    @State private var validation: String?

    var body: some View {
        VStack(spacing: 0) {
            Form {
                Section("Provider") {
                    TextField("Name", text: $draft.name, prompt: Text(draft.kind.label))
                    TextField("Location", text: $draft.parent, prompt: Text("Top level; use / for subfolders"))
                    Text("Appears as a folder named after the provider; what it generates lands inside.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    Picker("Source", selection: $draft.kind) {
                        ForEach(ProviderKindChoice.allCases) { Text($0.label).tag($0) }
                    }
                    Stepper(value: $draft.autoRefreshMinutes, in: 0...1440, step: 15) {
                        Text(draft.autoRefreshMinutes == 0
                             ? "Refresh manually"
                             : "Refresh every \(draft.autoRefreshMinutes) min")
                    }
                    Text("Automatic refresh also runs when Toolbox starts.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                switch draft.kind {
                case .onePassword:
                    Section("1Password") {
                        TextField("op path", text: $draft.opPath, prompt: Text(detectOpPath() ?? "/opt/homebrew/bin/op"))
                        TextField("Account", text: $draft.account, prompt: Text("All accounts"))
                        TextField("Vaults", text: $draft.vaults, prompt: Text("All vaults; separate names with commas"))
                        Text("Items become credentials, grouped by vault. Uses the desktop app integration: turn on “Integrate with 1Password CLI” under 1Password ▸ Settings ▸ Developer, then the first sync asks for Touch ID.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                case .activeDirectory:
                    Section("Active Directory") {
                        TextField("Domain controller", text: $draft.server, prompt: Text("dc01.corp.example"))
                        TextField("Search base", text: $draft.searchBase, prompt: Text("DC=corp,DC=example"))
                    }
                    ForEach(ProviderRole.allCases, id: \.self) { role in
                        roleSection(role)
                    }
                    Section("Connection security") {
                        Toggle("Fall back to unencrypted LDAP if LDAPS fails", isOn: $draft.allowPlainLdap)
                        Toggle("Accept untrusted certificates", isOn: $draft.acceptInvalidCerts)
                        Text("Computers become connections: Windows → RDP, macOS → VNC, Linux → SSH. OUs become folders.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
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
                Button("Save") { save(syncNow: false) }
                Button("Save and Sync") { save(syncNow: true) }
                    .keyboardShortcut(.defaultAction)
                    .buttonStyle(.borderedProminent)
            }
            .padding(16)
        }
        .frame(width: 580, height: 760)
    }

    @ViewBuilder
    private func roleSection(_ role: ProviderRole) -> some View {
        let binding = Binding(get: { draft[role] }, set: { draft[role] = $0 })
        Section(role.title) {
            Picker("Credential", selection: binding.mode) {
                if role != .bind {
                    Text("None (ask when connecting)").tag(RoleCredentialDraft.Mode.none)
                }
                Text("Use an existing credential").tag(RoleCredentialDraft.Mode.existing)
                Text("Specify username and password").tag(RoleCredentialDraft.Mode.specify)
            }
            switch draft[role].mode {
            case .none:
                EmptyView()
            case .existing:
                CredentialPicker(title: "Credential", credentials: model.credentials, selection: binding.credentialId)
            case .specify:
                TextField("Username", text: binding.username, prompt: Text("DOMAIN\\user or user@domain"))
                SecureField("Password", text: binding.password,
                            prompt: Text(draft[role].hasStored ? "Leave blank to keep the stored password" : ""))
                Text("Kept encrypted in the vault as a credential inside this provider's folder.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
    }

    private func save(syncNow: Bool) {
        if let problem = draft.validationError {
            validation = problem
            return
        }
        if draft.id.isEmpty { draft.id = newId() }
        onSave(draft, syncNow)
    }
}
