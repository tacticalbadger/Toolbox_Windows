import AppKit
import Combine

extension NSPasteboard.PasteboardType {
    static let sidebarNode = NSPasteboard.PasteboardType("com.dobbs.toolbox.sidebar-node")
}

/// What the sidebar can ask the window to do. Folder arguments are paths
/// like `AAN/Jump Boxes`; `nil` means the top level.
struct SidebarActions {
    var openConnection: (Connection) -> Void
    var browseFiles: (Connection) -> Void
    var editConnection: (Connection?, String?) -> Void
    var deleteConnection: (Connection) -> Void
    var editCredential: (Credential?, String?) -> Void
    var deleteCredential: (Credential) -> Void
    var editProvider: (Provider?, String?) -> Void
    var deleteProvider: (Provider) -> Void
    var syncProvider: (Provider) -> Void
    var newFolder: (String?) -> Void
    var renameFolder: (String) -> Void
    var deleteFolder: (String) -> Void
    var setFolderCredential: (String) -> Void
}

/// One row of the tree. Nodes are cached by id across rebuilds so the
/// outline view keeps expansion and selection by identity.
final class SidebarNode {
    enum Kind {
        case group(String)
        case folder(String)
        case provider(Provider)
        case connection(Connection)
        case credential(Credential)
    }

    let id: String
    var kind: Kind
    var children: [SidebarNode] = []
    weak var parent: SidebarNode?

    init(id: String, kind: Kind) {
        self.id = id
        self.kind = kind
    }

    var title: String {
        switch kind {
        case .group(let title): return title
        case .folder(let path): return (path as NSString).lastPathComponent
        case .provider(let provider): return provider.name
        case .connection(let connection): return connection.name
        case .credential(let credential): return credential.name
        }
    }

    var isGroup: Bool {
        if case .group = kind { return true }
        return false
    }

    var isContainer: Bool {
        switch kind {
        case .group, .folder, .provider: return true
        case .connection, .credential: return false
        }
    }

    var isGenerated: Bool {
        switch kind {
        case .connection(let c): return c.source != nil
        case .credential(let c): return c.source != nil
        default: return false
        }
    }

    var isDraggable: Bool {
        switch kind {
        case .group: return false
        case .folder, .provider: return true
        case .connection, .credential: return !isGenerated
        }
    }

    /// The folder this node *is* (folders, providers).
    var ownFolder: String? {
        switch kind {
        case .folder(let path): return path
        case .provider(let provider): return provider.folder
        default: return nil
        }
    }
}

/// Outline view with context menus, Return and Delete handling.
final class SidebarOutlineView: NSOutlineView {
    var menuProvider: ((SidebarNode?) -> NSMenu?)?
    var onReturn: (() -> Void)?
    var onDeleteKey: (() -> Void)?

    override func menu(for event: NSEvent) -> NSMenu? {
        let point = convert(event.locationInWindow, from: nil)
        let row = self.row(at: point)
        if row >= 0, let node = item(atRow: row) as? SidebarNode, !node.isGroup {
            if !selectedRowIndexes.contains(row) {
                selectRowIndexes(IndexSet(integer: row), byExtendingSelection: false)
            }
            return menuProvider?(node)
        }
        deselectAll(nil)
        return menuProvider?(nil)
    }

    override func keyDown(with event: NSEvent) {
        switch event.keyCode {
        case 36, 76: onReturn?()
        case 51, 117: onDeleteKey?()
        default: super.keyDown(with: event)
        }
    }
}

/// Icon, title and a secondary line.
final class SidebarCell: NSTableCellView {
    static let identifier = NSUserInterfaceItemIdentifier("SidebarCell")
    private let icon = NSImageView()
    private let title = NSTextField(labelWithString: "")
    private let detail = NSTextField(labelWithString: "")
    private let spinner = NSProgressIndicator()
    /// Shown on folders that pass a credential down.
    private let badge = NSImageView()
    private var detailHeight: NSLayoutConstraint!

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        identifier = Self.identifier
        icon.translatesAutoresizingMaskIntoConstraints = false
        icon.imageScaling = .scaleProportionallyUpOrDown
        title.translatesAutoresizingMaskIntoConstraints = false
        title.lineBreakMode = .byTruncatingTail
        title.font = NSFont.systemFont(ofSize: NSFont.systemFontSize)
        detail.translatesAutoresizingMaskIntoConstraints = false
        detail.lineBreakMode = .byTruncatingTail
        detail.font = NSFont.systemFont(ofSize: NSFont.smallSystemFontSize - 1)
        detail.textColor = .secondaryLabelColor
        spinner.translatesAutoresizingMaskIntoConstraints = false
        spinner.style = .spinning
        spinner.controlSize = .small
        spinner.isDisplayedWhenStopped = false
        badge.translatesAutoresizingMaskIntoConstraints = false
        badge.image = NSImage(systemSymbolName: "key.fill", accessibilityDescription: "Has a credential")
        badge.contentTintColor = .secondaryLabelColor
        badge.isHidden = true
        addSubview(icon)
        addSubview(title)
        addSubview(detail)
        addSubview(spinner)
        addSubview(badge)
        imageView = icon
        textField = title
        detailHeight = detail.heightAnchor.constraint(equalToConstant: 0)
        NSLayoutConstraint.activate([
            icon.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 2),
            icon.centerYAnchor.constraint(equalTo: centerYAnchor),
            icon.widthAnchor.constraint(equalToConstant: 18),
            icon.heightAnchor.constraint(equalToConstant: 18),
            title.leadingAnchor.constraint(equalTo: icon.trailingAnchor, constant: 6),
            title.trailingAnchor.constraint(lessThanOrEqualTo: badge.leadingAnchor, constant: -4),
            badge.trailingAnchor.constraint(equalTo: spinner.leadingAnchor, constant: -2),
            badge.centerYAnchor.constraint(equalTo: centerYAnchor),
            badge.widthAnchor.constraint(equalToConstant: 12),
            badge.heightAnchor.constraint(equalToConstant: 12),
            detail.leadingAnchor.constraint(equalTo: title.leadingAnchor),
            detail.trailingAnchor.constraint(lessThanOrEqualTo: trailingAnchor, constant: -4),
            detail.topAnchor.constraint(equalTo: title.bottomAnchor, constant: 0),
            spinner.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -6),
            spinner.centerYAnchor.constraint(equalTo: centerYAnchor),
            spinner.widthAnchor.constraint(equalToConstant: 14),
            spinner.heightAnchor.constraint(equalToConstant: 14),
        ])
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError() }

    private var centerTitle: NSLayoutConstraint?
    private var topTitle: NSLayoutConstraint?

    func configure(_ node: SidebarNode, syncing: Bool, credentialName: String? = nil) {
        title.stringValue = node.title
        badge.isHidden = credentialName == nil
        toolTip = credentialName.map { "Credential: \($0)" }
        var detailText = ""
        var image: NSImage?
        var tinted = false
        switch node.kind {
        case .group:
            break
        case .folder:
            image = NSImage(systemSymbolName: "folder.fill", accessibilityDescription: "Folder")
            tinted = true
        case .provider(let provider):
            image = NSImage(systemSymbolName: "folder.fill.badge.gearshape", accessibilityDescription: "Provider")
            tinted = true
            detailText = provider.lastStatus ?? "Not synced yet"
        case .connection(let connection):
            image = NSImage(systemSymbolName: connection.kind.symbol, accessibilityDescription: connection.kind.label)
            let user = connection.username.isEmpty ? "" : "\(connection.username)@"
            let port = Int(connection.port) == connection.kind.defaultPort ? "" : ":\(connection.port)"
            detailText = "\(user)\(connection.host)\(port)"
        case .credential(let credential):
            let symbol: String
            switch credential.kind {
            case .vault: symbol = "lock.fill"
            case .onePassword: symbol = "shield.lefthalf.filled"
            case .prompt: symbol = "key"
            }
            image = NSImage(systemSymbolName: symbol, accessibilityDescription: "Credential")
            detailText = credential.username.isEmpty ? "Credential" : credential.username
        }
        if tinted, let base = image {
            icon.image = base.withSymbolConfiguration(.init(paletteColors: [.controlAccentColor]))
            icon.contentTintColor = nil
        } else {
            icon.image = image
            icon.contentTintColor = .secondaryLabelColor
        }
        detail.stringValue = detailText
        detail.isHidden = detailText.isEmpty
        centerTitle?.isActive = false
        topTitle?.isActive = false
        if detailText.isEmpty {
            centerTitle = title.centerYAnchor.constraint(equalTo: centerYAnchor)
            centerTitle?.isActive = true
        } else {
            topTitle = title.topAnchor.constraint(equalTo: topAnchor, constant: 3)
            topTitle?.isActive = true
        }
        if syncing { spinner.startAnimation(nil) } else { spinner.stopAnimation(nil) }
    }
}

/// The sidebar: a tree of folders, providers, connections and credentials,
/// plus flat "all" lists, with drag and drop between folders.
@MainActor
final class SidebarOutlineController: NSViewController, NSOutlineViewDataSource, NSOutlineViewDelegate, NSSearchFieldDelegate {
    private let model: AppModel
    private let sync = ProviderSync.shared
    private let actions: SidebarActions
    private let outline = SidebarOutlineView()
    private let search = NSSearchField()
    private var roots: [SidebarNode] = []
    private var cache: [String: SidebarNode] = [:]
    private var cancellables = Set<AnyCancellable>()
    private var filter = ""
    private var didInitialExpand = false
    /// Expands every node on the first load (testing aid: `--expand-all`).
    var expandAllOnLoad = false

    private static let libraryID = "group:library"

    init(model: AppModel, actions: SidebarActions) {
        self.model = model
        self.actions = actions
        super.init(nibName: nil, bundle: nil)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError() }

    override func loadView() {
        let container = NSView()

        search.placeholderString = "Filter"
        search.delegate = self
        search.sendsSearchStringImmediately = true
        search.translatesAutoresizingMaskIntoConstraints = false
        search.controlSize = .small

        let scroll = NSScrollView()
        scroll.translatesAutoresizingMaskIntoConstraints = false
        scroll.hasVerticalScroller = true
        scroll.drawsBackground = false
        scroll.documentView = outline

        let column = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("main"))
        column.resizingMask = .autoresizingMask
        outline.addTableColumn(column)
        outline.outlineTableColumn = column
        outline.headerView = nil
        outline.style = .sourceList
        outline.floatsGroupRows = false
        outline.rowSizeStyle = .default
        outline.indentationPerLevel = 14
        outline.allowsMultipleSelection = true
        outline.allowsEmptySelection = true
        outline.autosaveExpandedItems = true
        outline.autosaveName = "sidebar.tree"
        outline.dataSource = self
        outline.delegate = self
        outline.target = self
        outline.doubleAction = #selector(doubleClicked)
        outline.registerForDraggedTypes([.sidebarNode])
        outline.setDraggingSourceOperationMask(.move, forLocal: true)
        outline.draggingDestinationFeedbackStyle = .sourceList
        outline.menuProvider = { [weak self] node in self?.menu(for: node) }
        outline.onReturn = { [weak self] in self?.activateSelection() }
        outline.onDeleteKey = { [weak self] in self?.deleteSelection() }

        let add = NSButton(image: NSImage(systemSymbolName: "plus", accessibilityDescription: "Add")!, target: self, action: #selector(showAddMenu(_:)))
        add.isBordered = false
        add.translatesAutoresizingMaskIntoConstraints = false
        add.toolTip = "New connection, credential, folder or provider"
        let addLabel = NSTextField(labelWithString: "New")
        addLabel.textColor = .secondaryLabelColor
        addLabel.font = NSFont.systemFont(ofSize: NSFont.smallSystemFontSize)
        addLabel.translatesAutoresizingMaskIntoConstraints = false

        container.addSubview(search)
        container.addSubview(scroll)
        container.addSubview(add)
        container.addSubview(addLabel)
        NSLayoutConstraint.activate([
            search.topAnchor.constraint(equalTo: container.topAnchor, constant: 8),
            search.leadingAnchor.constraint(equalTo: container.leadingAnchor, constant: 10),
            search.trailingAnchor.constraint(equalTo: container.trailingAnchor, constant: -10),
            scroll.topAnchor.constraint(equalTo: search.bottomAnchor, constant: 6),
            scroll.leadingAnchor.constraint(equalTo: container.leadingAnchor),
            scroll.trailingAnchor.constraint(equalTo: container.trailingAnchor),
            scroll.bottomAnchor.constraint(equalTo: add.topAnchor, constant: -4),
            add.leadingAnchor.constraint(equalTo: container.leadingAnchor, constant: 12),
            add.bottomAnchor.constraint(equalTo: container.bottomAnchor, constant: -8),
            add.widthAnchor.constraint(equalToConstant: 20),
            add.heightAnchor.constraint(equalToConstant: 20),
            addLabel.leadingAnchor.constraint(equalTo: add.trailingAnchor, constant: 4),
            addLabel.centerYAnchor.constraint(equalTo: add.centerYAnchor),
        ])
        view = container
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        Publishers.MergeMany(
            model.$connections.map { _ in () }.eraseToAnyPublisher(),
            model.$credentials.map { _ in () }.eraseToAnyPublisher(),
            model.$providers.map { _ in () }.eraseToAnyPublisher(),
            model.$folders.map { _ in () }.eraseToAnyPublisher(),
            sync.$inProgress.map { _ in () }.eraseToAnyPublisher()
        )
        .receive(on: DispatchQueue.main)
        .sink { [weak self] in self?.rebuild() }
        .store(in: &cancellables)
        rebuild()
    }

    // MARK: Tree

    private func node(_ id: String, _ kind: SidebarNode.Kind) -> SidebarNode {
        if let existing = cache[id] {
            existing.kind = kind
            return existing
        }
        let fresh = SidebarNode(id: id, kind: kind)
        cache[id] = fresh
        return fresh
    }

    private func matches(_ fields: [String?]) -> Bool {
        let query = filter.trimmingCharacters(in: .whitespaces).lowercased()
        return query.isEmpty || fields.contains { $0?.lowercased().contains(query) == true }
    }

    private func rebuild() {
        let selected = Set(outline.selectedRowIndexes.compactMap { (outline.item(atRow: $0) as? SidebarNode)?.id })
        for node in cache.values {
            node.children.removeAll()
            node.parent = nil
        }

        let library = node(Self.libraryID, .group("Library"))
        var folderNodes: [String: SidebarNode] = [:]
        func folderNode(_ path: String) -> SidebarNode {
            if let existing = folderNodes[path] { return existing }
            let made: SidebarNode
            if let provider = model.providers.first(where: { $0.folder == path }) {
                made = node("prov:\(provider.id)", .provider(provider))
            } else {
                made = node("folder:\(path)", .folder(path))
            }
            folderNodes[path] = made
            let parentPath = (path as NSString).deletingLastPathComponent
            let parent = parentPath.isEmpty ? library : folderNode(parentPath)
            parent.children.append(made)
            made.parent = parent
            return made
        }
        func attach(_ child: SidebarNode, to parent: SidebarNode) {
            parent.children.append(child)
            child.parent = parent
        }

        for path in model.folders { _ = folderNode(path) }
        for provider in model.providers { _ = folderNode(provider.folder) }
        for c in model.connections where matches([c.name, c.host, c.username, c.folder]) {
            attach(node("conn:\(c.id)", .connection(c)), to: c.folder.map(folderNode) ?? library)
        }
        for c in model.credentials where matches([c.name, c.username, c.folder]) {
            attach(node("cred:\(c.id)", .credential(c)), to: c.folder.map(folderNode) ?? library)
        }
        sortChildren(library)
        if !filter.isEmpty { prune(library) }

        let allConnections = node("group:connections", .group("All Connections"))
        for c in model.connections.sorted(by: { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending })
        where matches([c.name, c.host, c.username]) {
            attach(node("all-conn:\(c.id)", .connection(c)), to: allConnections)
        }
        let allCredentials = node("group:credentials", .group("All Credentials"))
        for c in model.credentials.sorted(by: { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending })
        where matches([c.name, c.username]) {
            attach(node("all-cred:\(c.id)", .credential(c)), to: allCredentials)
        }

        roots = [library, allConnections, allCredentials]
        outline.reloadData()

        if !filter.isEmpty {
            outline.expandItem(nil, expandChildren: true)
        } else if !didInitialExpand {
            didInitialExpand = true
            outline.expandItem(library, expandChildren: expandAllOnLoad)
            if expandAllOnLoad {
                outline.expandItem(allConnections)
                outline.expandItem(allCredentials)
            }
        }
        // Restore selection by id.
        var rows = IndexSet()
        for row in 0..<outline.numberOfRows {
            if let node = outline.item(atRow: row) as? SidebarNode, selected.contains(node.id) {
                rows.insert(row)
            }
        }
        outline.selectRowIndexes(rows, byExtendingSelection: false)
    }

    private func sortChildren(_ node: SidebarNode) {
        node.children.sort { a, b in
            rank(a) != rank(b) ? rank(a) < rank(b) : a.title.localizedCaseInsensitiveCompare(b.title) == .orderedAscending
        }
        node.children.forEach(sortChildren)
    }

    private func rank(_ node: SidebarNode) -> Int {
        switch node.kind {
        case .group: return 0
        case .folder, .provider: return 1
        case .connection: return 2
        case .credential: return 3
        }
    }

    /// Drops containers with nothing matching inside while filtering.
    @discardableResult
    private func prune(_ node: SidebarNode) -> Bool {
        node.children = node.children.filter { child in
            child.isContainer ? prune(child) : true
        }
        return !node.children.isEmpty || !node.isContainer
    }

    // MARK: NSOutlineViewDataSource

    func outlineView(_ outlineView: NSOutlineView, numberOfChildrenOfItem item: Any?) -> Int {
        item == nil ? roots.count : ((item as? SidebarNode)?.children.count ?? 0)
    }

    func outlineView(_ outlineView: NSOutlineView, child index: Int, ofItem item: Any?) -> Any {
        item == nil ? roots[index] : (item as! SidebarNode).children[index]
    }

    func outlineView(_ outlineView: NSOutlineView, isItemExpandable item: Any) -> Bool {
        (item as? SidebarNode)?.isContainer ?? false
    }

    func outlineView(_ outlineView: NSOutlineView, persistentObjectForItem item: Any?) -> Any? {
        (item as? SidebarNode)?.id
    }

    func outlineView(_ outlineView: NSOutlineView, itemForPersistentObject object: Any) -> Any? {
        (object as? String).flatMap { cache[$0] }
    }

    func outlineView(_ outlineView: NSOutlineView, pasteboardWriterForItem item: Any) -> NSPasteboardWriting? {
        guard let node = item as? SidebarNode, node.isDraggable else { return nil }
        let pasteboardItem = NSPasteboardItem()
        pasteboardItem.setString(node.id, forType: .sidebarNode)
        return pasteboardItem
    }

    func outlineView(_ outlineView: NSOutlineView, validateDrop info: NSDraggingInfo, proposedItem item: Any?,
                     proposedChildIndex index: Int) -> NSDragOperation {
        guard let (targetNode, folder) = dropTarget(for: item as? SidebarNode), let ids = draggedIDs(info) else { return [] }
        for id in ids {
            guard let node = cache[id] else { return [] }
            if let own = node.ownFolder, let destination = folder,
               destination == own || destination.hasPrefix(own + "/") {
                return []
            }
        }
        outlineView.setDropItem(targetNode, dropChildIndex: NSOutlineViewDropOnItemIndex)
        return .move
    }

    func outlineView(_ outlineView: NSOutlineView, acceptDrop info: NSDraggingInfo, item: Any?, childIndex index: Int) -> Bool {
        guard let (_, folder) = dropTarget(for: item as? SidebarNode), let ids = draggedIDs(info) else { return false }
        for id in ids {
            guard let node = cache[id] else { continue }
            do {
                switch node.kind {
                case .connection(let c): try model.moveConnection(c, to: folder)
                case .credential(let c): try model.moveCredential(c, to: folder)
                case .provider(let p): try model.moveProvider(p, to: folder)
                case .folder(let path):
                    let leaf = (path as NSString).lastPathComponent
                    try model.renameFolder(path, to: folder.map { "\($0)/\(leaf)" } ?? leaf)
                case .group: break
                }
            } catch {
                Prompts.error(title: "Couldn't move \(node.title)", message: describe(error))
            }
        }
        return true
    }

    /// The node a drop should land on and the folder path it means
    /// (`nil` path = top level). Items retarget to their containing folder.
    private func dropTarget(for node: SidebarNode?) -> (SidebarNode?, String?)? {
        guard let node else { return (cache[Self.libraryID], nil) }
        switch node.kind {
        case .group(let title): return title == "Library" ? (node, nil) : nil
        case .folder(let path): return (node, path)
        case .provider: return nil
        case .connection, .credential:
            guard let parent = node.parent, !node.isGenerated else { return nil }
            return dropTarget(for: parent)
        }
    }

    private func draggedIDs(_ info: NSDraggingInfo) -> [String]? {
        let ids = info.draggingPasteboard.pasteboardItems?.compactMap { $0.string(forType: .sidebarNode) } ?? []
        return ids.isEmpty ? nil : ids
    }

    // MARK: NSOutlineViewDelegate

    func outlineView(_ outlineView: NSOutlineView, isGroupItem item: Any) -> Bool {
        (item as? SidebarNode)?.isGroup ?? false
    }

    func outlineView(_ outlineView: NSOutlineView, shouldSelectItem item: Any) -> Bool {
        !((item as? SidebarNode)?.isGroup ?? true)
    }

    func outlineView(_ outlineView: NSOutlineView, heightOfRowByItem item: Any) -> CGFloat {
        guard let node = item as? SidebarNode else { return 24 }
        switch node.kind {
        case .group, .folder: return 24
        case .provider, .connection, .credential: return 36
        }
    }

    func outlineView(_ outlineView: NSOutlineView, viewFor tableColumn: NSTableColumn?, item: Any) -> NSView? {
        guard let node = item as? SidebarNode else { return nil }
        if case .group(let title) = node.kind {
            let identifier = NSUserInterfaceItemIdentifier("Header")
            let cell = outlineView.makeView(withIdentifier: identifier, owner: self) as? NSTableCellView ?? {
                let cell = NSTableCellView()
                cell.identifier = identifier
                let label = NSTextField(labelWithString: "")
                label.translatesAutoresizingMaskIntoConstraints = false
                cell.addSubview(label)
                cell.textField = label
                NSLayoutConstraint.activate([
                    label.leadingAnchor.constraint(equalTo: cell.leadingAnchor, constant: 2),
                    label.centerYAnchor.constraint(equalTo: cell.centerYAnchor),
                ])
                return cell
            }()
            cell.textField?.stringValue = title
            return cell
        }
        let cell = outlineView.makeView(withIdentifier: SidebarCell.identifier, owner: self) as? SidebarCell ?? SidebarCell()
        var syncing = false
        if case .provider(let provider) = node.kind { syncing = sync.inProgress.contains(provider.id) }
        let credentialName = node.ownFolder.flatMap(model.folderCredential)?.name
        cell.configure(node, syncing: syncing, credentialName: credentialName)
        return cell
    }

    // MARK: Actions

    private var selectedNodes: [SidebarNode] {
        outline.selectedRowIndexes.compactMap { outline.item(atRow: $0) as? SidebarNode }
    }

    @objc private func doubleClicked(_ sender: Any?) {
        guard outline.clickedRow >= 0, let node = outline.item(atRow: outline.clickedRow) as? SidebarNode else { return }
        activate(node)
    }

    private func activateSelection() {
        if let node = selectedNodes.first { activate(node) }
    }

    private func activate(_ node: SidebarNode) {
        switch node.kind {
        case .connection(let c): actions.openConnection(c)
        case .credential(let c): actions.editCredential(c, nil)
        case .provider(let p): actions.syncProvider(p)
        case .folder, .group:
            if outline.isItemExpanded(node) { outline.collapseItem(node) } else { outline.expandItem(node) }
        }
    }

    private func deleteSelection() {
        for node in selectedNodes {
            switch node.kind {
            case .connection(let c): actions.deleteConnection(c)
            case .credential(let c): actions.deleteCredential(c)
            case .provider(let p): actions.deleteProvider(p)
            case .folder(let path): actions.deleteFolder(path)
            case .group: break
            }
        }
    }

    @objc private func showAddMenu(_ sender: NSButton) {
        let menu = NSMenu()
        addItems(to: menu, folder: nil)
        menu.popUp(positioning: nil, at: NSPoint(x: 0, y: sender.bounds.height + 4), in: sender)
    }

    private func addItems(to menu: NSMenu, folder: String?) {
        let suffix = folder.map { " in \(($0 as NSString).lastPathComponent)" } ?? ""
        menu.addItem(item("New Connection\(suffix)…") { [self] in actions.editConnection(nil, folder) })
        menu.addItem(item("New Credential\(suffix)…") { [self] in actions.editCredential(nil, folder) })
        menu.addItem(item("New Folder\(suffix)") { [self] in actions.newFolder(folder) })
        menu.addItem(item("New Provider\(suffix)…") { [self] in actions.editProvider(nil, folder) })
    }

    private func menu(for node: SidebarNode?) -> NSMenu? {
        let menu = NSMenu()
        guard let node else {
            addItems(to: menu, folder: nil)
            return menu
        }
        switch node.kind {
        case .group:
            addItems(to: menu, folder: nil)
        case .folder(let path):
            addItems(to: menu, folder: path)
            menu.addItem(.separator())
            menu.addItem(item("Rename…") { [self] in actions.renameFolder(path) })
            menu.addItem(item(model.folderCredentials[path] == nil ? "Set Credential…" : "Change Credential…") {
                [self] in actions.setFolderCredential(path)
            })
            menu.addItem(item("Delete Folder") { [self] in actions.deleteFolder(path) })
        case .provider(let provider):
            menu.addItem(item("Sync Now") { [self] in actions.syncProvider(provider) })
            menu.addItem(item("Edit…") { [self] in actions.editProvider(provider, nil) })
            menu.addItem(item(model.folderCredentials[provider.folder] == nil ? "Set Credential…" : "Change Credential…") {
                [self] in actions.setFolderCredential(provider.folder)
            })
            menu.addItem(.separator())
            menu.addItem(item("Delete") { [self] in actions.deleteProvider(provider) })
        case .connection(let connection):
            menu.addItem(item(connection.kind.isFileTransfer ? "Open" : "Connect") { [self] in actions.openConnection(connection) })
            if connection.kind == .ssh {
                menu.addItem(item("Browse Files (SFTP)") { [self] in actions.browseFiles(connection) })
            }
            menu.addItem(item("Edit…") { [self] in actions.editConnection(connection, nil) })
            menu.addItem(.separator())
            menu.addItem(item("Delete") { [self] in actions.deleteConnection(connection) })
        case .credential(let credential):
            menu.addItem(item("Edit…") { [self] in actions.editCredential(credential, nil) })
            menu.addItem(.separator())
            menu.addItem(item("Delete") { [self] in actions.deleteCredential(credential) })
        }
        return menu
    }

    private func item(_ title: String, _ handler: @escaping () -> Void) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: #selector(runMenuAction(_:)), keyEquivalent: "")
        item.target = self
        item.representedObject = MenuAction(handler)
        return item
    }

    @objc private func runMenuAction(_ sender: NSMenuItem) {
        (sender.representedObject as? MenuAction)?.handler()
    }

    // MARK: NSSearchFieldDelegate

    func controlTextDidChange(_ obj: Notification) {
        filter = search.stringValue
        rebuild()
    }
}

private final class MenuAction: NSObject {
    let handler: () -> Void
    init(_ handler: @escaping () -> Void) { self.handler = handler }
}
