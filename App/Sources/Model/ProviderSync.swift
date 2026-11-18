import AppKit
import Combine

enum SyncError: LocalizedError {
    case noBindCredential

    var errorDescription: String? {
        "Choose the credential to bind with on the provider first."
    }
}

/// Runs providers (dynamic folders) and keeps them fresh.
@MainActor
final class ProviderSync: ObservableObject {
    static let shared = ProviderSync()

    private var timers: [String: Timer] = [:]
    @Published private(set) var inProgress: Set<String> = []

    /// Syncs every provider; with `automatic` only those with auto-refresh on.
    func syncAll(automatic: Bool) async {
        for provider in AppModel.shared.providers where !automatic || provider.autoRefreshMinutes > 0 {
            await sync(provider, interactive: !automatic)
        }
    }

    /// Runs one provider. Interactive syncs report failures in a dialog;
    /// automatic ones just record the status on the provider.
    @discardableResult
    func sync(_ provider: Provider, interactive: Bool) async -> SyncResult? {
        guard !inProgress.contains(provider.id) else { return nil }
        inProgress.insert(provider.id)
        defer { inProgress.remove(provider.id) }
        do {
            var bind: BindCredentials?
            if case .activeDirectory(_, _, let bindId, _, _, _, _) = provider.kind {
                guard let bindId, let credential = AppModel.shared.credential(bindId) else {
                    throw SyncError.noBindCredential
                }
                guard let resolved = try await CredentialResolver.bind(for: credential) else { return nil }
                bind = resolved
            }
            let result = try await AppModel.shared.store.syncProvider(id: provider.id, bind: bind)
            AppModel.shared.reload()
            if interactive, !result.warnings.isEmpty {
                Prompts.info(title: "\(provider.name) synced with warnings",
                             message: result.warnings.joined(separator: "\n\n"))
            }
            return result
        } catch {
            AppModel.shared.reload()
            if interactive {
                Prompts.error(title: "Couldn't sync \(provider.name)", message: describe(error))
            } else {
                NSLog("provider \(provider.name) auto-sync failed: \(describe(error))")
            }
            return nil
        }
    }

    /// (Re)arms periodic refresh for providers that want it.
    func scheduleAutoRefresh() {
        timers.values.forEach { $0.invalidate() }
        timers = [:]
        for provider in AppModel.shared.providers where provider.autoRefreshMinutes > 0 {
            let id = provider.id
            let interval = TimeInterval(provider.autoRefreshMinutes) * 60
            timers[id] = Timer.scheduledTimer(withTimeInterval: interval, repeats: true) { _ in
                Task { @MainActor in
                    guard let current = AppModel.shared.provider(id) else { return }
                    await ProviderSync.shared.sync(current, interactive: false)
                }
            }
        }
    }
}
