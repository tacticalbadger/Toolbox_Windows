import CryptoKit
import Foundation
import LocalAuthentication
import Security

enum VaultError: LocalizedError {
    case cancelled
    case unavailable(String)
    case keychain(OSStatus)

    var errorDescription: String? {
        switch self {
        case .cancelled:
            return "Vault unlock was cancelled."
        case .unavailable(let reason):
            return "The vault can't be unlocked on this Mac: \(reason)"
        case .keychain(let status):
            if status == errSecUserCanceled || status == errSecAuthFailed {
                return "Access to the vault key in the Keychain was denied."
            }
            let text = SecCopyErrorMessageString(status, nil) as String? ?? "status \(status)"
            return "Keychain error: \(text)"
        }
    }
}

/// Owns the vault's key. The key is random, lives in the login Keychain,
/// and is handed to the core once per launch after Touch ID (or the login
/// password) confirms it's you.
///
/// macOS ties Keychain access to the app's code signature: the app that
/// created an item reads it silently, anything else gets the "wants to use
/// your confidential information" dialog, and "Always Allow" is remembered
/// per signature. An ad-hoc signature changes with every build, which brings
/// the dialog back after each rebuild; `make signing` (or a Developer ID)
/// gives the app a stable one. When the signing certificate does change, the
/// item is rewritten so the current build owns it and one "Allow" is enough.
@MainActor
final class VaultManager {
    static let shared = VaultManager()

    let vault: Vault
    /// `TOOLBOX_KEYCHAIN_SERVICE` lets the test harness use its own Keychain
    /// item instead of the real vault key.
    private static let service: String = {
        if let override = ProcessInfo.processInfo.environment["TOOLBOX_KEYCHAIN_SERVICE"], !override.isEmpty {
            return override
        }
        return "com.dobbs.Toolbox.vault-key"
    }()
    private static let account = "default"
    /// Temporary name used while the item is rewritten under a new signature.
    private static let stagingAccount = "default.rekey"
    private static let ownerDefaultsKey = "vaultKeyOwnerCertificate"
    private var unlocking: Task<Void, Error>?

    var requiresAuthentication: Bool {
        get {
            let defaults = UserDefaults.standard
            // bool(forKey:) also understands "-vaultRequiresAuth NO" on the command line.
            return defaults.object(forKey: "vaultRequiresAuth") == nil ? true : defaults.bool(forKey: "vaultRequiresAuth")
        }
        set { UserDefaults.standard.set(newValue, forKey: "vaultRequiresAuth") }
    }

    private init() {
        vault = Self.openVault(at: AppModel.vaultURL)
    }

    private static func openVault(at url: URL) -> Vault {
        do {
            return try Vault.open(path: url.path)
        } catch {
            let stamp = Int(Date().timeIntervalSince1970)
            let broken = url.deletingPathExtension().appendingPathExtension("broken-\(stamp).json")
            try? FileManager.default.moveItem(at: url, to: broken)
            NSLog("vault.json could not be read (\(error)); moved to \(broken.path)")
            if let fresh = try? Vault.open(path: url.path) {
                return fresh
            }
            let temp = FileManager.default.temporaryDirectory.appendingPathComponent("toolbox-vault.json")
            return try! Vault.open(path: temp.path)
        }
    }

    /// The vault, unlocked. Prompts at most once even if called concurrently.
    func unlockedVault() async throws -> Vault {
        if vault.isUnlocked() { return vault }
        if let unlocking {
            try await unlocking.value
            return vault
        }
        let task = Task { @MainActor in
            defer { self.unlocking = nil }
            if self.requiresAuthentication {
                try await self.authenticate()
            }
            let key = try self.loadOrCreateKey()
            try self.vault.unlock(key: key)
        }
        unlocking = task
        try await task.value
        return vault
    }

    func lock() {
        vault.lock()
    }

    private func authenticate() async throws {
        let context = LAContext()
        context.localizedCancelTitle = "Cancel"
        var unavailable: NSError?
        guard context.canEvaluatePolicy(.deviceOwnerAuthentication, error: &unavailable) else {
            // Refuse rather than silently skipping the owner check.
            throw VaultError.unavailable(unavailable?.localizedDescription ?? "no login password or Touch ID is set up")
        }
        let ok = try await context.evaluatePolicy(.deviceOwnerAuthentication,
                                                  localizedReason: "unlock the Toolbox vault")
        if !ok { throw VaultError.cancelled }
    }

    // MARK: - Keychain item

    private func loadOrCreateKey() throws -> Data {
        if let key = try Self.readKey(account: Self.account) {
            adoptKeyIfSignatureChanged(key)
            return key
        }
        // A rewrite interrupted after the old item was removed leaves the key
        // under the staging name; take it back.
        if let key = try Self.readKey(account: Self.stagingAccount) {
            try Self.rename(account: Self.stagingAccount, to: Self.account)
            Self.rememberOwner()
            return key
        }
        let key = generateVaultKey()
        try Self.addKey(key, account: Self.account)
        Self.rememberOwner()
        return key
    }

    /// Rewrites the item when the app's signing certificate differs from the
    /// one that last wrote it, so this build becomes the item's creator and
    /// reads it without a dialog from now on. Every step leaves the key in at
    /// least one item, so an interruption cannot lose it.
    private func adoptKeyIfSignatureChanged(_ key: Data) {
        guard let current = Self.signingCertificateFingerprint else { return } // ad-hoc: nothing stable to tie it to
        guard UserDefaults.standard.string(forKey: Self.ownerDefaultsKey) != current else { return }
        do {
            try? Self.delete(account: Self.stagingAccount)
            try Self.addKey(key, account: Self.stagingAccount)
            try Self.delete(account: Self.account)
            try Self.rename(account: Self.stagingAccount, to: Self.account)
            Self.rememberOwner()
        } catch {
            NSLog("vault key could not be re-owned by this build's signature: \(error.localizedDescription)")
        }
    }

    private static func rememberOwner() {
        UserDefaults.standard.set(signingCertificateFingerprint, forKey: ownerDefaultsKey)
    }

    private static func baseQuery(account: String) -> [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
    }

    private static func readKey(account: String) throws -> Data? {
        var query = baseQuery(account: account)
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var item: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &item)
        switch status {
        case errSecSuccess:
            guard let data = item as? Data, data.count == 32 else {
                throw VaultError.unavailable("the vault key in the Keychain has an unexpected size")
            }
            return data
        case errSecItemNotFound:
            return nil
        default:
            throw VaultError.keychain(status)
        }
    }

    private static func addKey(_ key: Data, account: String) throws {
        var add = baseQuery(account: account)
        add[kSecAttrLabel as String] = service == "com.dobbs.Toolbox.vault-key"
            ? "Toolbox vault key" : "Toolbox vault key (\(service))"
        add[kSecAttrAccessible as String] = kSecAttrAccessibleWhenUnlockedThisDeviceOnly
        add[kSecValueData as String] = key
        let status = SecItemAdd(add as CFDictionary, nil)
        guard status == errSecSuccess else { throw VaultError.keychain(status) }
    }

    private static func delete(account: String) throws {
        let status = SecItemDelete(baseQuery(account: account) as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else { throw VaultError.keychain(status) }
    }

    private static func rename(account from: String, to: String) throws {
        let update: [String: Any] = [kSecAttrAccount as String: to]
        let status = SecItemUpdate(baseQuery(account: from) as CFDictionary, update as CFDictionary)
        guard status == errSecSuccess else { throw VaultError.keychain(status) }
    }

    /// SHA-256 of the leaf certificate this app is signed with, or nil for an
    /// ad-hoc or missing signature.
    private static let signingCertificateFingerprint: String? = {
        var code: SecCode?
        guard SecCodeCopySelf([], &code) == errSecSuccess, let code else { return nil }
        var staticCode: SecStaticCode?
        guard SecCodeCopyStaticCode(code, [], &staticCode) == errSecSuccess, let staticCode else { return nil }
        var info: CFDictionary?
        let flags = SecCSFlags(rawValue: UInt32(kSecCSSigningInformation))
        guard SecCodeCopySigningInformation(staticCode, flags, &info) == errSecSuccess,
              let dict = info as? [String: Any],
              let certificates = dict[kSecCodeInfoCertificates as String] as? [SecCertificate],
              let leaf = certificates.first else { return nil }
        let der = SecCertificateCopyData(leaf) as Data
        return SHA256.hash(data: der).map { String(format: "%02x", $0) }.joined()
    }()
}
