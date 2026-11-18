import AppKit

enum MainMenu {
    static func install() {
        let main = NSMenu()

        // Application
        let appMenu = NSMenu()
        appMenu.addItem(withTitle: "About Toolbox", action: #selector(AppDelegate.showAbout(_:)), keyEquivalent: "")
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "Lock Vault", action: #selector(AppDelegate.lockVault(_:)), keyEquivalent: "")
        appMenu.addItem(withTitle: "Require Touch ID to Unlock Vault",
                        action: #selector(AppDelegate.toggleVaultAuthentication(_:)), keyEquivalent: "")
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "Hide Toolbox", action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
        let hideOthers = appMenu.addItem(withTitle: "Hide Others", action: #selector(NSApplication.hideOtherApplications(_:)), keyEquivalent: "h")
        hideOthers.keyEquivalentModifierMask = [.command, .option]
        appMenu.addItem(withTitle: "Show All", action: #selector(NSApplication.unhideAllApplications(_:)), keyEquivalent: "")
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "Quit Toolbox", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        let appItem = NSMenuItem()
        appItem.submenu = appMenu
        main.addItem(appItem)

        // File
        let file = NSMenu(title: "File")
        file.addItem(withTitle: "New Workspace", action: #selector(AppDelegate.newWorkspace(_:)), keyEquivalent: "t")
        file.addItem(withTitle: "New Connection…", action: #selector(WorkspaceWindowController.newConnection(_:)), keyEquivalent: "n")
        let newCredential = file.addItem(withTitle: "New Credential…", action: #selector(WorkspaceWindowController.newCredential(_:)), keyEquivalent: "n")
        newCredential.keyEquivalentModifierMask = [.command, .shift]
        file.addItem(withTitle: "New Provider…", action: #selector(WorkspaceWindowController.newProvider(_:)), keyEquivalent: "")
        file.addItem(.separator())
        let browse = file.addItem(withTitle: "Browse Files (SFTP)", action: #selector(WorkspaceWindowController.browseFiles(_:)), keyEquivalent: "f")
        browse.keyEquivalentModifierMask = [.command, .shift]
        file.addItem(.separator())
        file.addItem(withTitle: "Sync Providers", action: #selector(AppDelegate.syncProviders(_:)), keyEquivalent: "r")
        file.addItem(.separator())
        file.addItem(withTitle: "Close Pane", action: #selector(WorkspaceWindowController.closePane(_:)), keyEquivalent: "w")
        let closeWorkspace = file.addItem(withTitle: "Close Workspace", action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")
        closeWorkspace.keyEquivalentModifierMask = [.command, .shift]
        main.addItem(submenu: file)

        // Edit
        let edit = NSMenu(title: "Edit")
        edit.addItem(withTitle: "Undo", action: Selector(("undo:")), keyEquivalent: "z")
        let redo = edit.addItem(withTitle: "Redo", action: Selector(("redo:")), keyEquivalent: "z")
        redo.keyEquivalentModifierMask = [.command, .shift]
        edit.addItem(.separator())
        edit.addItem(withTitle: "Cut", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        edit.addItem(withTitle: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        edit.addItem(withTitle: "Paste", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        edit.addItem(withTitle: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        main.addItem(submenu: edit)

        // View
        let view = NSMenu(title: "View")
        let toggleSidebar = view.addItem(withTitle: "Toggle Sidebar", action: #selector(NSSplitViewController.toggleSidebar(_:)), keyEquivalent: "s")
        toggleSidebar.keyEquivalentModifierMask = [.command, .control]
        let themeItem = NSMenuItem(title: "Theme", action: nil, keyEquivalent: "")
        themeItem.submenu = themeMenu()
        view.addItem(themeItem)
        view.addItem(.separator())
        view.addItem(withTitle: "Make Text Bigger", action: #selector(AppDelegate.increaseFontSize(_:)), keyEquivalent: "+")
        view.addItem(withTitle: "Make Text Smaller", action: #selector(AppDelegate.decreaseFontSize(_:)), keyEquivalent: "-")
        view.addItem(withTitle: "Reset Text Size", action: #selector(AppDelegate.resetFontSize(_:)), keyEquivalent: "0")
        main.addItem(submenu: view)

        // Pane
        let pane = NSMenu(title: "Pane")
        pane.addItem(withTitle: "Split Right", action: #selector(WorkspaceWindowController.splitRight(_:)), keyEquivalent: "d")
        let splitDown = pane.addItem(withTitle: "Split Down", action: #selector(WorkspaceWindowController.splitDown(_:)), keyEquivalent: "d")
        splitDown.keyEquivalentModifierMask = [.command, .shift]
        pane.addItem(.separator())
        pane.addItem(withTitle: "Focus Next Pane", action: #selector(WorkspaceWindowController.focusNextPane(_:)), keyEquivalent: "]")
        pane.addItem(withTitle: "Focus Previous Pane", action: #selector(WorkspaceWindowController.focusPreviousPane(_:)), keyEquivalent: "[")
        pane.addItem(.separator())
        let moveNew = pane.addItem(withTitle: "Move Pane to New Workspace", action: #selector(WorkspaceWindowController.movePaneToNewWorkspace(_:)), keyEquivalent: "m")
        moveNew.keyEquivalentModifierMask = [.command, .shift]
        let moveTo = NSMenuItem(title: "Move Pane to Workspace", action: nil, keyEquivalent: "")
        let moveToMenu = NSMenu(title: "Move Pane to Workspace")
        moveToMenu.delegate = NSApp.delegate as? NSMenuDelegate
        moveTo.submenu = moveToMenu
        pane.addItem(moveTo)
        main.addItem(submenu: pane)

        // Window (AppKit appends the tab items itself)
        let window = NSMenu(title: "Window")
        window.addItem(withTitle: "Minimize", action: #selector(NSWindow.performMiniaturize(_:)), keyEquivalent: "m")
        window.addItem(withTitle: "Zoom", action: #selector(NSWindow.performZoom(_:)), keyEquivalent: "")
        let rename = window.addItem(withTitle: "Rename Workspace…", action: #selector(WorkspaceWindowController.renameWorkspace(_:)), keyEquivalent: "r")
        rename.keyEquivalentModifierMask = [.command, .shift]
        window.addItem(.separator())
        window.addItem(withTitle: "Bring All to Front", action: #selector(NSApplication.arrangeInFront(_:)), keyEquivalent: "")
        main.addItem(submenu: window)
        NSApp.windowsMenu = window

        let help = NSMenu(title: "Help")
        main.addItem(submenu: help)
        NSApp.helpMenu = help

        NSApp.mainMenu = main
    }

    private static func themeMenu() -> NSMenu {
        let menu = NSMenu(title: "Theme")
        for theme in TerminalTheme.all {
            let item = menu.addItem(withTitle: theme.name, action: #selector(AppDelegate.selectTheme(_:)), keyEquivalent: "")
            item.representedObject = theme.id
        }
        return menu
    }
}

private extension NSMenu {
    @discardableResult
    func addItem(submenu: NSMenu) -> NSMenuItem {
        let item = NSMenuItem(title: submenu.title, action: nil, keyEquivalent: "")
        item.submenu = submenu
        addItem(item)
        return item
    }
}
