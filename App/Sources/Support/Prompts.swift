import AppKit

/// Small app-modal prompts. All must be called on the main thread.
@MainActor
enum Prompts {
    static func confirm(title: String, message: String, ok: String) -> Bool {
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = message
        alert.alertStyle = .warning
        alert.addButton(withTitle: ok)
        alert.addButton(withTitle: "Cancel")
        return alert.runModal() == .alertFirstButtonReturn
    }

    static func info(title: String, message: String) {
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = message
        alert.alertStyle = .informational
        alert.runModal()
    }

    static func error(title: String, message: String) {
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = message
        alert.alertStyle = .critical
        alert.runModal()
    }

    static func askText(title: String, message: String, initial: String, ok: String = "OK") -> String? {
        let field = NSTextField(string: initial)
        field.frame = NSRect(x: 0, y: 0, width: 300, height: 24)
        return ask(title: title, message: message, field: field, ok: ok)
    }

    static func askSecret(title: String, message: String, ok: String = "Connect") -> String? {
        let field = NSSecureTextField(string: "")
        field.frame = NSRect(x: 0, y: 0, width: 300, height: 24)
        return ask(title: title, message: message, field: field, ok: ok)
    }

    static func confirmHostKey(host: String, port: UInt16, keyType: String, fingerprint: String) -> Bool {
        let alert = NSAlert()
        alert.messageText = "Unknown host key for \(host):\(port)"
        alert.informativeText = """
            The authenticity of this host can't be established yet.

            \(keyType) key fingerprint:
            \(fingerprint)

            Trust it to add it to your known_hosts file.
            """
        alert.alertStyle = .warning
        alert.addButton(withTitle: "Trust")
        alert.addButton(withTitle: "Cancel")
        return alert.runModal() == .alertFirstButtonReturn
    }

    private static func ask(title: String, message: String, field: NSTextField, ok: String) -> String? {
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = message
        alert.accessoryView = field
        alert.addButton(withTitle: ok)
        alert.addButton(withTitle: "Cancel")
        alert.window.initialFirstResponder = field
        guard alert.runModal() == .alertFirstButtonReturn else { return nil }
        return field.stringValue
    }
}
