import Foundation

enum ResolveError: LocalizedError {
    case missingCredential(String)
    case emptyCredential(String)
    case missingPassword(String)
    case noFolderCredential(String)

    var errorDescription: String? {
        switch self {
        case .missingCredential(let id):
            return "The credential this connection uses (\(id)) no longer exists."
        case .emptyCredential(let name):
            return "Credential “\(name)” has neither a password nor a private key."
        case .missingPassword(let name):
            return "“\(name)” has no stored password. Edit the connection and enter one."
        case .noFolderCredential(let name):
            return "“\(name)” uses its folder's credential, but no folder above it has one. Right-click a folder and choose Set Credential…."
        }
    }
}

/// Turns stored references into usable secrets at the moment they're needed:
/// vault entries after unlocking, 1Password items through `op`, prompts
/// otherwise. Nothing resolved here is written back anywhere.
@MainActor
enum CredentialResolver {
    struct SecretBundle {
        var username: String?
        var password: String?
        var privateKey: String?
        var passphrase: String?
    }

    struct ResolvedSsh {
        var username: String
        var auth: SshAuth
    }

    /// SSH credentials for a connection. `nil` means the user cancelled.
    static func sshAuth(for connection: Connection) async throws -> ResolvedSsh? {
        switch connection.auth {
        case .password:
            let who = connection.username.isEmpty ? connection.host : "\(connection.username)@\(connection.host)"
            guard let password = Prompts.askSecret(title: "Password for \(who)",
                                                   message: "This connection asks every time.") else { return nil }
            return ResolvedSsh(username: connection.username, auth: .password(password: password))
        case .keyFile(let path):
            return ResolvedSsh(username: connection.username, auth: .keyFile(path: path, passphrase: nil))
        case .agent(let socketPath):
            return ResolvedSsh(username: connection.username, auth: .agent(socketPath: socketPath))
        case .credential(let id):
            guard let credential = AppModel.shared.credential(id) else {
                throw ResolveError.missingCredential(id)
            }
            return try await ssh(with: credential, for: connection)
        case .folderCredential:
            return try await ssh(with: folderCredential(for: connection), for: connection)
        case .storedPassword:
            let password = try await storedPassword(for: connection)
            return ResolvedSsh(username: connection.username, auth: .password(password: password))
        }
    }

    private static func ssh(with credential: Credential, for connection: Connection) async throws -> ResolvedSsh? {
        guard let secret = try await secret(for: credential, purpose: "connect to \(connection.name)") else {
            return nil
        }
        let username = connection.username.isEmpty
            ? (secret.username ?? credential.username)
            : connection.username
        if let key = secret.privateKey, !key.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            return ResolvedSsh(username: username, auth: .keyData(key: key, passphrase: secret.passphrase))
        }
        if let password = secret.password {
            return ResolvedSsh(username: username, auth: .password(password: password))
        }
        throw ResolveError.emptyCredential(credential.name)
    }

    /// The credential the nearest folder above the connection assigns.
    private static func folderCredential(for connection: Connection) throws -> Credential {
        guard let inherited = AppModel.shared.inheritedCredential(forFolder: connection.folder) else {
            throw ResolveError.noFolderCredential(connection.name)
        }
        return inherited.credential
    }

    /// The password kept in the vault for a "Username and password" connection.
    private static func storedPassword(for connection: Connection) async throws -> String {
        let vault = try await VaultManager.shared.unlockedVault()
        guard let password = (try? vault.getSecret(id: connection.id))?.password.nonEmpty else {
            throw ResolveError.missingPassword(connection.name)
        }
        return password
    }

    struct ResolvedPassword {
        var username: String
        var password: String
        /// Windows domain split off a `DOMAIN\\user` or `user@domain` username.
        var domain: String?
    }

    /// Username and password for RDP and VNC. `nil` means the user cancelled.
    static func passwordAuth(for connection: Connection) async throws -> ResolvedPassword? {
        guard let raw = try await usernameAndPassword(for: connection) else { return nil }
        return split(username: raw.username, password: raw.password)
    }

    /// Username and password for FTP: the username is used exactly as
    /// written, without splitting off a Windows domain.
    static func ftpAuth(for connection: Connection) async throws -> (username: String, password: String)? {
        try await usernameAndPassword(for: connection)
    }

    /// The username (as written) and password a connection resolves to.
    /// `nil` means the user cancelled a prompt.
    private static func usernameAndPassword(for connection: Connection) async throws -> (username: String, password: String)? {
        switch connection.auth {
        case .password:
            let who = connection.username.isEmpty ? connection.host : "\(connection.username)@\(connection.host)"
            guard let password = Prompts.askSecret(title: "Password for \(who)",
                                                   message: "This connection asks every time.") else { return nil }
            return (connection.username, password)
        case .credential(let id):
            guard let credential = AppModel.shared.credential(id) else {
                throw ResolveError.missingCredential(id)
            }
            return try await password(with: credential, for: connection)
        case .folderCredential:
            return try await password(with: folderCredential(for: connection), for: connection)
        case .storedPassword:
            return (connection.username, try await storedPassword(for: connection))
        case .keyFile, .agent:
            throw ResolveError.emptyCredential("\(connection.name) (SSH keys don't apply to \(connection.kind.label))")
        }
    }

    private static func password(with credential: Credential, for connection: Connection) async throws -> (username: String, password: String)? {
        guard let secret = try await secret(for: credential, purpose: "connect to \(connection.name)") else {
            return nil
        }
        guard let password = secret.password else { throw ResolveError.emptyCredential(credential.name) }
        let username = connection.username.isEmpty ? (secret.username ?? credential.username) : connection.username
        return (username, password)
    }

    private static func split(username: String, password: String) -> ResolvedPassword {
        if let backslash = username.firstIndex(of: "\\") {
            return ResolvedPassword(username: String(username[username.index(after: backslash)...]),
                                    password: password,
                                    domain: String(username[..<backslash]))
        }
        if let at = username.firstIndex(of: "@") {
            return ResolvedPassword(username: String(username[..<at]),
                                    password: password,
                                    domain: String(username[username.index(after: at)...]))
        }
        return ResolvedPassword(username: username, password: password, domain: nil)
    }

    /// Username and password for binding to a directory.
    static func bind(for credential: Credential) async throws -> BindCredentials? {
        guard let secret = try await secret(for: credential, purpose: "query Active Directory") else { return nil }
        guard let password = secret.password else { throw ResolveError.emptyCredential(credential.name) }
        return BindCredentials(username: secret.username ?? credential.username, password: password)
    }

    static func secret(for credential: Credential, purpose: String) async throws -> SecretBundle? {
        switch credential.kind {
        case .vault:
            let vault = try await VaultManager.shared.unlockedVault()
            let stored = try vault.getSecret(id: credential.id)
            return SecretBundle(username: credential.username,
                                password: stored.password,
                                privateKey: stored.privateKey,
                                passphrase: stored.passphrase)
        case .onePassword(let itemId, let account, let opPath):
            let item = try await resolveOnePasswordItem(opPath: opPath, account: account, itemId: itemId)
            return SecretBundle(username: item.username ?? credential.username,
                                password: item.password,
                                privateKey: item.privateKey,
                                passphrase: nil)
        case .prompt:
            let who = credential.username.isEmpty ? credential.name : credential.username
            guard let password = Prompts.askSecret(title: "Password for \(who)",
                                                   message: "Needed to \(purpose).") else { return nil }
            return SecretBundle(username: credential.username, password: password, privateKey: nil, passphrase: nil)
        }
    }
}
