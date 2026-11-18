import AppKit
import SwiftUI

/// One workspace: a window (shown as a native tab) holding the connection
/// sidebar and a pane layout.
final class WorkspaceWindowController: NSWindowController, NSWindowDelegate, NSToolbarDelegate,
    NSMenuItemValidation, NSToolbarItemValidation {
    static var counter = 0
    static let tabbingIdentifier = NSWindow.TabbingIdentifier("com.dobbs.Toolbox.workspace")

    let paneVC = PaneContainerViewController()
    private let splitVC = NSSplitViewController()
    private var editorSheet: NSWindow?

    init(name: String) {
        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 1280, height: 820),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false
        )
        window.title = name
        window.tabbingMode = .preferred
        window.tabbingIdentifier = Self.tabbingIdentifier
        window.toolbarStyle = .unified
        window.minSize = NSSize(width: 720, height: 420)
        window.center()
        super.init(window: window)
        window.delegate = self

        let sidebar = SidebarOutlineController(
            model: AppModel.shared,
            actions: SidebarActions(
                openConnection: { [weak self] in self?.open($0) },
                browseFiles: { [weak self] in self?.openFiles($0) },
                editConnection: { [weak self] in self?.edit($0, inFolder: $1) },
                deleteConnection: { [weak self] in self?.delete($0) },
                editCredential: { [weak self] in self?.editCredential($0, inFolder: $1) },
                deleteCredential: { [weak self] in self?.deleteCredential($0) },
                editProvider: { [weak self] in self?.editProvider($0, inFolder: $1) },
                deleteProvider: { [weak self] in self?.deleteProvider($0) },
                syncProvider: { [weak self] in self?.syncProvider($0) },
                newFolder: { [weak self] in self?.newFolder(in: $0) },
                renameFolder: { [weak self] in self?.renameFolder($0) },
                deleteFolder: { [weak self] in self?.deleteFolder($0) },
                setFolderCredential: { [weak self] in self?.setFolderCredential($0) }
            )
        )
        sidebar.expandAllOnLoad = CommandLine.arguments.contains("--expand-all")
        let sidebarItem = NSSplitViewItem(sidebarWithViewController: sidebar)
        sidebarItem.minimumThickness = 220
        sidebarItem.maximumThickness = 520
        sidebarItem.preferredThicknessFraction = 0.25
        sidebarItem.canCollapse = true
        let content = NSSplitViewItem(viewController: paneVC)
        content.minimumThickness = 420
        splitVC.addSplitViewItem(sidebarItem)
        splitVC.addSplitViewItem(content)
        window.contentViewController = splitVC
        // Assigning a content view controller shrinks the window to its fitting
        // size; restore the intended size afterwards.
        window.setContentSize(NSSize(width: 1280, height: 820))
        window.center()

        let toolbar = NSToolbar(identifier: "workspace")
        toolbar.delegate = self
        toolbar.displayMode = .iconOnly
        toolbar.allowsUserCustomization = false
        window.toolbar = toolbar

        paneVC.onLayoutChange = { [weak self] in self?.window?.toolbar?.validateVisibleItems() }
        paneVC.onOpenConnection = { [weak self] connection, pane in self?.open(connection, in: pane) }
        paneVC.contextMenuProvider = { [weak self] pane in self?.paneMenu(for: pane) }
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError() }

    // MARK: Connections

    /// Opens a connection in `pane`, or in the pane a new session should use.
    func open(_ connection: Connection, in target: PaneView? = nil) {
        if connection.kind.isFileTransfer {
            openFiles(connection, in: target)
            return
        }
        let pane = target ?? paneVC.paneForNewSession()
        switch connection.kind {
        case .sftp, .ftp, .ftps:
            return
        case .ssh:
            let session = TerminalSessionView(connection: connection)
            pane.setContent(session)
            paneVC.setFocus(pane.id, makeFirstResponder: true)
            Task { @MainActor in
                do {
                    guard let resolved = try await CredentialResolver.sshAuth(for: connection) else {
                        session.cancelled()
                        return
                    }
                    session.start(username: resolved.username, auth: resolved.auth)
                } catch {
                    session.failed(describe(error))
                }
            }
        case .rdp:
            let session = RdpSessionView(connection: connection)
            pane.setContent(session)
            paneVC.setFocus(pane.id, makeFirstResponder: true)
            Task { @MainActor in
                do {
                    guard let resolved = try await CredentialResolver.passwordAuth(for: connection) else {
                        session.cancelled()
                        return
                    }
                    session.start(username: resolved.username, password: resolved.password, domain: resolved.domain)
                } catch {
                    session.failed(describe(error))
                }
            }
        case .vnc:
            let session = VncSessionView(connection: connection)
            pane.setContent(session)
            paneVC.setFocus(pane.id, makeFirstResponder: true)
            Task { @MainActor in
                do {
                    guard let resolved = try await CredentialResolver.passwordAuth(for: connection) else {
                        session.cancelled()
                        return
                    }
                    session.start(username: resolved.username, password: resolved.password)
                } catch {
                    session.failed(describe(error))
                }
            }
        }
    }

    /// Opens a file browser: SFTP for SSH and SFTP connections, FTP or FTPS
    /// for those kinds.
    func openFiles(_ connection: Connection, in target: PaneView? = nil) {
        guard connection.kind.usesSsh || connection.kind.isFileTransfer else {
            Prompts.info(title: "File browsing needs SSH or FTP",
                         message: "This connection is \(connection.kind.label). Create a File Transfer connection for the host instead.")
            return
        }
        let pane = target ?? paneVC.paneForNewSession()
        let files = SftpPaneView(connection: connection)
        pane.setContent(files)
        paneVC.setFocus(pane.id, makeFirstResponder: true)
        Task { @MainActor in
            do {
                if connection.kind.usesSsh {
                    guard let resolved = try await CredentialResolver.sshAuth(for: connection) else {
                        files.cancelled()
                        return
                    }
                    files.start(username: resolved.username, auth: resolved.auth)
                } else {
                    guard let resolved = try await CredentialResolver.ftpAuth(for: connection) else {
                        files.cancelled()
                        return
                    }
                    files.startFtp(username: resolved.username, password: resolved.password,
                                   secure: connection.kind == .ftps)
                }
            } catch {
                files.failed(describe(error))
            }
        }
    }

    /// The connection behind the focused pane, if any.
    private var focusedConnection: Connection? {
        switch paneVC.focusedPane.content {
        case let terminal as TerminalSessionView: return terminal.connection
        case let files as SftpPaneView: return files.connection
        case let rdp as RdpSessionView: return rdp.connection
        case let vnc as VncSessionView: return vnc.connection
        default: return nil
        }
    }

    @objc func browseFiles(_ sender: Any?) {
        guard let connection = focusedConnection else { return }
        openFiles(connection)
    }

    private func presentSheet<Content: View>(_ content: Content) {
        guard editorSheet == nil else { return }
        let sheet = NSWindow(contentViewController: NSHostingController(rootView: content))
        sheet.styleMask = [.titled]
        editorSheet = sheet
        window?.beginSheet(sheet)
    }

    func edit(_ connection: Connection?, inFolder folder: String? = nil) {
        guard editorSheet == nil else { return }
        var draft = connection.map(ConnectionDraft.init) ?? ConnectionDraft()
        if connection == nil, let folder {
            draft.folder = folder
            // A folder that hands down a credential is the natural default.
            if AppModel.shared.inheritedCredential(forFolder: folder) != nil { draft.authMethod = .folder }
        }
        let editor = ConnectionEditorView(
            model: AppModel.shared,
            draft: draft,
            onSave: { [weak self] result, password in
                guard let self else { return }
                Task { @MainActor in
                    do {
                        try await AppModel.shared.save(result, storedPassword: password)
                        self.endEditor()
                    } catch {
                        Prompts.error(title: "Couldn't save the connection", message: describe(error))
                    }
                }
            },
            onCancel: { [weak self] in self?.endEditor() }
        )
        presentSheet(editor)
    }

    // MARK: Credentials

    func editCredential(_ credential: Credential?, inFolder folder: String? = nil) {
        var draft = credential.map(CredentialDraft.init) ?? CredentialDraft()
        if credential == nil, let folder { draft.folder = folder }
        let editor = CredentialEditorView(
            draft: draft,
            onSave: { [weak self] record, secret in
                guard let self else { return }
                Task { @MainActor in
                    do {
                        try await AppModel.shared.saveCredential(record, secret: secret)
                        self.endEditor()
                    } catch {
                        Prompts.error(title: "Couldn't save the credential", message: describe(error))
                    }
                }
            },
            onCancel: { [weak self] in self?.endEditor() }
        )
        presentSheet(editor)
    }

    func deleteCredential(_ credential: Credential) {
        let users = AppModel.shared.store.connectionsUsingCredential(id: credential.id)
        let folders = AppModel.shared.store.foldersUsingCredential(id: credential.id)
        var message = "The credential is removed from Toolbox."
        if case .vault = credential.kind { message = "The stored secret is deleted from the vault." }
        if !users.isEmpty {
            message += " \(users.count) connection\(users.count == 1 ? "" : "s") still reference it and will ask for a password instead."
        }
        if !folders.isEmpty {
            message += " \(folders.count) folder\(folders.count == 1 ? " passes" : "s pass") it down to connections; they lose it."
        }
        guard Prompts.confirm(title: "Delete “\(credential.name)”?", message: message, ok: "Delete") else { return }
        do {
            try AppModel.shared.deleteCredential(credential)
        } catch {
            Prompts.error(title: "Couldn't delete the credential", message: describe(error))
        }
    }

    // MARK: Providers

    func editProvider(_ provider: Provider?, inFolder folder: String? = nil) {
        var draft = provider.map(ProviderDraft.init) ?? ProviderDraft()
        if provider == nil, let folder { draft.parent = folder }
        let editor = ProviderEditorView(
            model: AppModel.shared,
            draft: draft,
            onSave: { [weak self] saved, syncNow in
                guard let self else { return }
                Task { @MainActor in
                    do {
                        let stored = try await AppModel.shared.saveProvider(saved)
                        self.endEditor()
                        if syncNow {
                            self.syncProvider(stored)
                        }
                    } catch {
                        Prompts.error(title: "Couldn't save the provider", message: describe(error))
                    }
                }
            },
            onCancel: { [weak self] in self?.endEditor() }
        )
        presentSheet(editor)
    }

    func deleteProvider(_ provider: Provider) {
        guard Prompts.confirm(title: "Delete “\(provider.name)”?",
                              message: "Everything it generated is removed from the sidebar too.",
                              ok: "Delete") else { return }
        do {
            try AppModel.shared.deleteProvider(provider)
        } catch {
            Prompts.error(title: "Couldn't delete the provider", message: describe(error))
        }
    }

    func syncProvider(_ provider: Provider) {
        Task { @MainActor in
            await ProviderSync.shared.sync(provider, interactive: true)
        }
    }

    // MARK: Folders

    func newFolder(in parent: String?) {
        let location = parent.map { "Inside \(($0 as NSString).lastPathComponent)" } ?? "At the top level"
        guard let name = Prompts.askText(title: "New Folder", message: location, initial: "untitled folder", ok: "Create"),
              !name.trimmingCharacters(in: .whitespaces).isEmpty else { return }
        do {
            try AppModel.shared.addFolder(parent.map { "\($0)/\(name)" } ?? name)
        } catch {
            Prompts.error(title: "Couldn't create the folder", message: describe(error))
        }
    }

    func renameFolder(_ path: String) {
        let leaf = (path as NSString).lastPathComponent
        let parent = (path as NSString).deletingLastPathComponent
        guard let name = Prompts.askText(title: "Rename Folder", message: path, initial: leaf, ok: "Rename"),
              !name.trimmingCharacters(in: .whitespaces).isEmpty, name != leaf else { return }
        do {
            try AppModel.shared.renameFolder(path, to: parent.isEmpty ? name : "\(parent)/\(name)")
        } catch {
            Prompts.error(title: "Couldn't rename the folder", message: describe(error))
        }
    }

    func deleteFolder(_ path: String) {
        guard Prompts.confirm(title: "Delete folder “\((path as NSString).lastPathComponent)”?",
                              message: "Only empty folders can be deleted.", ok: "Delete") else { return }
        do {
            try AppModel.shared.deleteFolder(path)
        } catch {
            Prompts.error(title: "Couldn't delete the folder", message: describe(error))
        }
    }

    func setFolderCredential(_ path: String) {
        guard editorSheet == nil else { return }
        let editor = FolderCredentialView(
            model: AppModel.shared,
            folder: path,
            selection: AppModel.shared.folderCredentials[path],
            onSave: { [weak self] id in
                guard let self else { return }
                do {
                    try AppModel.shared.setFolderCredential(path, id)
                    self.endEditor()
                } catch {
                    Prompts.error(title: "Couldn't set the folder credential", message: describe(error))
                }
            },
            onCancel: { [weak self] in self?.endEditor() }
        )
        presentSheet(editor)
    }

    private func endEditor() {
        guard let sheet = editorSheet else { return }
        window?.endSheet(sheet)
        editorSheet = nil
    }

    func delete(_ connection: Connection) {
        guard Prompts.confirm(title: "Delete “\(connection.name)”?",
                              message: "This removes the connection from the sidebar. Open sessions are not affected.",
                              ok: "Delete") else { return }
        do {
            try AppModel.shared.delete(connection)
        } catch {
            Prompts.error(title: "Couldn't delete the connection", message: describe(error))
        }
    }

    // MARK: Workspaces

    private var otherWorkspaces: [WorkspaceWindowController] {
        ((NSApp.delegate as? AppDelegate)?.workspaces ?? []).filter { $0 !== self }
    }

    /// Moves a pane's session into another workspace, keeping it running.
    func movePane(_ pane: PaneView, to target: WorkspaceWindowController) {
        guard target !== self, let content = paneVC.lift(pane) else { return }
        target.paneVC.adopt(content)
        target.window?.makeKeyAndOrderFront(nil)
    }

    @objc func movePaneToNewWorkspace(_ sender: Any?) {
        let pane = paneVC.focusedPane
        guard pane.content != nil, let delegate = NSApp.delegate as? AppDelegate else { return }
        let target = delegate.newWorkspaceWindow(asTab: true)
        movePane(pane, to: target)
    }

    @objc func movePaneToWorkspace(_ sender: NSMenuItem) {
        guard let target = sender.representedObject as? WorkspaceWindowController else { return }
        movePane(paneVC.focusedPane, to: target)
    }

    /// Header context menu: where to send the pane, and close.
    private func paneMenu(for pane: PaneView) -> NSMenu? {
        guard pane.content != nil else { return nil }
        let menu = NSMenu()
        let toNew = NSMenuItem(title: "Move to New Workspace", action: #selector(movePaneToNewWorkspace(_:)), keyEquivalent: "")
        toNew.target = self
        menu.addItem(toNew)
        let others = otherWorkspaces
        if !others.isEmpty {
            let submenu = NSMenu(title: "Move to Workspace")
            for workspace in others {
                let item = NSMenuItem(title: workspace.window?.title ?? "Workspace",
                                      action: #selector(movePaneToWorkspace(_:)), keyEquivalent: "")
                item.target = self
                item.representedObject = workspace
                submenu.addItem(item)
            }
            let parent = NSMenuItem(title: "Move to Workspace", action: nil, keyEquivalent: "")
            parent.submenu = submenu
            menu.addItem(parent)
        }
        menu.addItem(.separator())
        let close = NSMenuItem(title: "Close Pane", action: #selector(closePane(_:)), keyEquivalent: "")
        close.target = self
        menu.addItem(close)
        return menu
    }

    // MARK: Actions (menu + toolbar)

    @objc func newConnection(_ sender: Any?) { edit(nil) }
    @objc func newCredential(_ sender: Any?) { editCredential(nil) }
    @objc func newProvider(_ sender: Any?) { editProvider(nil) }
    @objc func splitRight(_ sender: Any?) { paneVC.split(.horizontal) }
    @objc func splitDown(_ sender: Any?) { paneVC.split(.vertical) }
    @objc func focusNextPane(_ sender: Any?) { paneVC.focusNeighbor(1) }
    @objc func focusPreviousPane(_ sender: Any?) { paneVC.focusNeighbor(-1) }

    @objc func closePane(_ sender: Any?) {
        let pane = paneVC.focusedPane
        if pane.content?.isLive == true,
           !Prompts.confirm(title: "Close pane?", message: "The session will be disconnected.", ok: "Close") {
            return
        }
        paneVC.closePane(pane.id)
    }

    /// Also reached by double-clicking the workspace's tab.
    @objc func renameWorkspace(_ sender: Any?) {
        guard let window else { return }
        if let name = Prompts.askText(title: "Rename Workspace", message: "Double-click a tab to rename it any time.",
                                      initial: window.title, ok: "Rename"),
           !name.trimmingCharacters(in: .whitespaces).isEmpty {
            window.title = name.trimmingCharacters(in: .whitespaces)
        }
    }

    private var canClosePane: Bool {
        paneVC.focusedPane.content != nil || paneVC.paneCount > 1
    }

    func validateMenuItem(_ menuItem: NSMenuItem) -> Bool {
        switch menuItem.action {
        case #selector(closePane(_:)): return canClosePane
        case #selector(browseFiles(_:)): return focusedConnection.map { $0.kind.usesSsh || $0.kind.isFileTransfer } ?? false
        case #selector(movePaneToNewWorkspace(_:)), #selector(movePaneToWorkspace(_:)):
            return paneVC.focusedPane.content != nil
        case #selector(focusNextPane(_:)), #selector(focusPreviousPane(_:)): return paneVC.paneCount > 1
        default: return true
        }
    }

    func validateToolbarItem(_ item: NSToolbarItem) -> Bool {
        switch item.itemIdentifier {
        case Self.closePaneItem: return canClosePane
        case Self.browseFilesItem: return focusedConnection.map { $0.kind.usesSsh || $0.kind.isFileTransfer } ?? false
        default: return true
        }
    }

    // MARK: NSWindowDelegate

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        let live = paneVC.liveSessionCount
        guard live > 0 else { return true }
        return Prompts.confirm(title: "Close “\(sender.title)”?",
                               message: "\(live) active session\(live == 1 ? "" : "s") will be disconnected.",
                               ok: "Close")
    }

    func windowWillClose(_ notification: Notification) {
        paneVC.terminateAll()
        (NSApp.delegate as? AppDelegate)?.windowControllerDidClose(self)
    }

    // MARK: NSToolbarDelegate

    private static let newConnectionItem = NSToolbarItem.Identifier("newConnection")
    private static let splitRightItem = NSToolbarItem.Identifier("splitRight")
    private static let splitDownItem = NSToolbarItem.Identifier("splitDown")
    private static let closePaneItem = NSToolbarItem.Identifier("closePane")
    private static let browseFilesItem = NSToolbarItem.Identifier("browseFiles")

    func toolbarDefaultItemIdentifiers(_ toolbar: NSToolbar) -> [NSToolbarItem.Identifier] {
        [.toggleSidebar, .sidebarTrackingSeparator, Self.newConnectionItem, .flexibleSpace,
         Self.browseFilesItem, Self.splitRightItem, Self.splitDownItem, Self.closePaneItem]
    }

    func toolbarAllowedItemIdentifiers(_ toolbar: NSToolbar) -> [NSToolbarItem.Identifier] {
        toolbarDefaultItemIdentifiers(toolbar)
    }

    func toolbar(_ toolbar: NSToolbar, itemForItemIdentifier id: NSToolbarItem.Identifier,
                 willBeInsertedIntoToolbar flag: Bool) -> NSToolbarItem? {
        switch id {
        case Self.newConnectionItem:
            return toolbarItem(id, symbol: "plus", label: "New Connection", action: #selector(newConnection(_:)))
        case Self.browseFilesItem:
            return toolbarItem(id, symbol: "folder", label: "Browse Files", action: #selector(browseFiles(_:)))
        case Self.splitRightItem:
            return toolbarItem(id, symbol: "rectangle.split.2x1", label: "Split Right", action: #selector(splitRight(_:)))
        case Self.splitDownItem:
            return toolbarItem(id, symbol: "rectangle.split.1x2", label: "Split Down", action: #selector(splitDown(_:)))
        case Self.closePaneItem:
            return toolbarItem(id, symbol: "xmark.rectangle", label: "Close Pane", action: #selector(closePane(_:)))
        default:
            return nil
        }
    }

    private func toolbarItem(_ id: NSToolbarItem.Identifier, symbol: String, label: String, action: Selector) -> NSToolbarItem {
        let item = NSToolbarItem(itemIdentifier: id)
        item.image = NSImage(systemSymbolName: symbol, accessibilityDescription: label)
        item.label = label
        item.toolTip = label
        item.target = self
        item.action = action
        item.isBordered = true
        return item
    }
}
