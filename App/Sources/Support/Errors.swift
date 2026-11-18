import Foundation

/// A readable message for errors coming out of the core or the app.
func describe(_ error: Error) -> String {
    if let core = error as? CoreError {
        return core.message
    }
    return error.localizedDescription
}

extension CoreError {
    /// The human-readable text the core attached to the error.
    var message: String {
        switch self {
        case .Connect(let message),
             .Auth(let message),
             .HostKey(let message),
             .Io(let message),
             .Store(let message),
             .Invalid(let message):
            return message
        }
    }
}
