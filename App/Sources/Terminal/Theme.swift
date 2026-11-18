import AppKit
import SwiftTerm

struct TerminalTheme: Identifiable {
    let id: String
    let name: String
    let background: NSColor
    let foreground: NSColor
    let cursor: NSColor
    let selection: NSColor
    /// The 16 ANSI colors: normal black…white, then bright black…white.
    let ansi: [NSColor]

    init(id: String, name: String, background: UInt32, foreground: UInt32, cursor: UInt32,
         selection: UInt32, ansi: [UInt32]) {
        precondition(ansi.count == 16, "a theme needs exactly 16 ANSI colors")
        self.id = id
        self.name = name
        self.background = NSColor(hex: background)
        self.foreground = NSColor(hex: foreground)
        self.cursor = NSColor(hex: cursor)
        self.selection = NSColor(hex: selection).withAlphaComponent(0.45)
        self.ansi = ansi.map { NSColor(hex: $0) }
    }

    func apply(to terminal: TerminalView) {
        terminal.nativeBackgroundColor = background
        terminal.nativeForegroundColor = foreground
        terminal.caretColor = cursor
        terminal.selectedTextBackgroundColor = selection
        terminal.installColors(ansi.map { $0.terminalColor })
    }
}

extension TerminalTheme {
    static let homebrew = TerminalTheme(
        id: "homebrew", name: "Homebrew",
        background: 0x000000, foreground: 0x00FF00, cursor: 0x23FF18, selection: 0x4D4D4D,
        ansi: [0x000000, 0x990000, 0x00A600, 0x999900, 0x0000B2, 0xB200B2, 0x00A6B2, 0xBFBFBF,
               0x666666, 0xE50000, 0x00D900, 0xE5E500, 0x0000FF, 0xE500E5, 0x00E5E5, 0xE5E5E5])

    static let oneDark = TerminalTheme(
        id: "one-dark", name: "Atom One Dark",
        background: 0x282C34, foreground: 0xABB2BF, cursor: 0x528BFF, selection: 0x3E4451,
        ansi: [0x282C34, 0xE06C75, 0x98C379, 0xE5C07B, 0x61AFEF, 0xC678DD, 0x56B6C2, 0xABB2BF,
               0x5C6370, 0xE06C75, 0x98C379, 0xE5C07B, 0x61AFEF, 0xC678DD, 0x56B6C2, 0xFFFFFF])

    static let gruvboxDark = TerminalTheme(
        id: "gruvbox-dark", name: "Gruvbox Dark",
        background: 0x282828, foreground: 0xEBDBB2, cursor: 0xEBDBB2, selection: 0x504945,
        ansi: [0x282828, 0xCC241D, 0x98971A, 0xD79921, 0x458588, 0xB16286, 0x689D6A, 0xA89984,
               0x928374, 0xFB4934, 0xB8BB26, 0xFABD2F, 0x83A598, 0xD3869B, 0x8EC07C, 0xEBDBB2])

    static let dracula = TerminalTheme(
        id: "dracula", name: "Dracula",
        background: 0x282A36, foreground: 0xF8F8F2, cursor: 0xF8F8F2, selection: 0x44475A,
        ansi: [0x21222C, 0xFF5555, 0x50FA7B, 0xF1FA8C, 0xBD93F9, 0xFF79C6, 0x8BE9FD, 0xF8F8F2,
               0x6272A4, 0xFF6E6E, 0x69FF94, 0xFFFFA5, 0xD6ACFF, 0xFF92DF, 0xA4FFFF, 0xFFFFFF])

    static let solarizedDark = TerminalTheme(
        id: "solarized-dark", name: "Solarized Dark",
        background: 0x002B36, foreground: 0x839496, cursor: 0x93A1A1, selection: 0x073642,
        ansi: [0x073642, 0xDC322F, 0x859900, 0xB58900, 0x268BD2, 0xD33682, 0x2AA198, 0xEEE8D5,
               0x002B36, 0xCB4B16, 0x586E75, 0x657B83, 0x839496, 0x6C71C4, 0x93A1A1, 0xFDF6E3])

    static let catppuccinMocha = TerminalTheme(
        id: "catppuccin-mocha", name: "Catppuccin Mocha",
        background: 0x1E1E2E, foreground: 0xCDD6F4, cursor: 0xF5E0DC, selection: 0x45475A,
        ansi: [0x45475A, 0xF38BA8, 0xA6E3A1, 0xF9E2AF, 0x89B4FA, 0xF5C2E7, 0x94E2D5, 0xBAC2DE,
               0x585B70, 0xF38BA8, 0xA6E3A1, 0xF9E2AF, 0x89B4FA, 0xF5C2E7, 0x94E2D5, 0xA6ADC8])

    static let all: [TerminalTheme] = [homebrew, oneDark, gruvboxDark, dracula, solarizedDark, catppuccinMocha]
}

/// The active theme and font. Changes are broadcast to every open terminal.
@MainActor
final class ThemeStore {
    static let shared = ThemeStore()
    static let changed = Notification.Name("ToolboxThemeChanged")
    static let defaultFontSize: CGFloat = 13

    private let defaults = UserDefaults.standard

    var current: TerminalTheme {
        didSet {
            defaults.set(current.id, forKey: "theme")
            NotificationCenter.default.post(name: Self.changed, object: nil)
        }
    }

    var fontSize: CGFloat {
        didSet {
            fontSize = min(max(fontSize, 8), 40)
            defaults.set(fontSize, forKey: "fontSize")
            NotificationCenter.default.post(name: Self.changed, object: nil)
        }
    }

    var font: NSFont {
        if let name = defaults.string(forKey: "fontName"), let font = NSFont(name: name, size: fontSize) {
            return font
        }
        return NSFont.monospacedSystemFont(ofSize: fontSize, weight: .regular)
    }

    private init() {
        let id = defaults.string(forKey: "theme")
        current = TerminalTheme.all.first { $0.id == id } ?? .homebrew
        let stored = defaults.double(forKey: "fontSize")
        fontSize = stored > 0 ? stored : Self.defaultFontSize
    }

    func apply(to terminal: TerminalView) {
        current.apply(to: terminal)
        terminal.font = font
    }
}

private extension NSColor {
    convenience init(hex: UInt32) {
        self.init(srgbRed: CGFloat((hex >> 16) & 0xFF) / 255,
                  green: CGFloat((hex >> 8) & 0xFF) / 255,
                  blue: CGFloat(hex & 0xFF) / 255,
                  alpha: 1)
    }

    var terminalColor: SwiftTerm.Color {
        let c = usingColorSpace(.sRGB) ?? self
        func channel(_ v: CGFloat) -> UInt16 { UInt16(min(max(v, 0), 1) * 65535) }
        return SwiftTerm.Color(red: channel(c.redComponent), green: channel(c.greenComponent), blue: channel(c.blueComponent))
    }
}
