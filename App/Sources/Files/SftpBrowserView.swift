import SwiftUI

/// Remote directory browser with transfers, shown inside an SFTP pane.
struct SftpBrowserView: View {
    @ObservedObject var model: SftpModel
    @State private var selection = Set<String>()
    @State private var pathField = ""

    private var selectedEntries: [SftpEntry] {
        model.entries.filter { selection.contains($0.path) }
    }

    var body: some View {
        VStack(spacing: 0) {
            toolbar
            Divider()
            if model.isConnected {
                table
            } else {
                Spacer()
                Text(model.status ?? "Not connected")
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                    .padding()
                Spacer()
            }
            if !model.transfers.isEmpty {
                Divider()
                transfersList
            }
            if model.isConnected, let status = model.status {
                Divider()
                Text(status)
                    .font(.caption)
                    .foregroundStyle(.red)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.horizontal, 10)
                    .padding(.vertical, 4)
            }
        }
        .onReceive(model.$path) { pathField = $0 }
    }

    private var toolbar: some View {
        HStack(spacing: 6) {
            Button { Task { await model.goUp() } } label: { Image(systemName: "chevron.up") }
                .help("Parent folder")
                .disabled(!model.isConnected || model.path == "/")
            Button { Task { await model.goHome() } } label: { Image(systemName: "house") }
                .help("Home folder")
                .disabled(!model.isConnected)
            TextField("Path", text: $pathField)
                .textFieldStyle(.roundedBorder)
                .font(.system(.body, design: .monospaced))
                .onSubmit { Task { await model.load(pathField) } }
                .disabled(!model.isConnected)
            Button { Task { await model.refresh() } } label: { Image(systemName: "arrow.clockwise") }
                .help("Refresh")
                .disabled(!model.isConnected)
            if model.busy {
                ProgressView().controlSize(.small)
            }
            Divider().frame(height: 16)
            Button { model.upload() } label: { Image(systemName: "arrow.up.doc") }
                .help("Upload files here")
                .disabled(!model.isConnected)
            Button { model.download(selectedEntries) } label: { Image(systemName: "arrow.down.doc") }
                .help("Download selected files")
                .disabled(selectedEntries.allSatisfy(\.isDir))
            Button { model.newFolder() } label: { Image(systemName: "folder.badge.plus") }
                .help("New folder")
                .disabled(!model.isConnected)
            Button { model.delete(selectedEntries) } label: { Image(systemName: "trash") }
                .help("Delete selected")
                .disabled(selectedEntries.isEmpty)
        }
        .buttonStyle(.borderless)
        .padding(.horizontal, 10)
        .padding(.vertical, 6)
    }

    private var table: some View {
        Table(model.entries, selection: $selection) {
            TableColumn("Name") { entry in
                HStack(spacing: 6) {
                    Image(systemName: entry.isDir ? "folder.fill" : (entry.isSymlink ? "link" : "doc"))
                        .foregroundStyle(entry.isDir ? Color.accentColor : Color.secondary)
                    Text(entry.name)
                }
            }
            TableColumn("Size") { entry in
                Text(entry.isDir ? "—" : Self.sizeFormatter.string(fromByteCount: Int64(entry.size)))
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, alignment: .trailing)
            }
            .width(min: 70, ideal: 90, max: 120)
            TableColumn("Modified") { entry in
                Text(entry.modifiedEpoch.map { Self.dateFormatter.string(from: Date(timeIntervalSince1970: TimeInterval($0))) } ?? "")
                    .foregroundStyle(.secondary)
            }
            .width(min: 110, ideal: 150, max: 200)
        }
        .contextMenu(forSelectionType: String.self) { items in
            let picked = model.entries.filter { items.contains($0.path) }
            if picked.count == 1, let entry = picked.first {
                if entry.isDir {
                    Button("Open") { model.open(entry) }
                } else {
                    Button("Download…") { model.download([entry]) }
                }
                Button("Rename…") { model.rename(entry) }
                Divider()
            } else if picked.contains(where: { !$0.isDir }) {
                Button("Download…") { model.download(picked) }
                Divider()
            }
            Button("Delete", role: .destructive) { model.delete(picked) }
        } primaryAction: { items in
            if let entry = model.entries.first(where: { items.contains($0.path) }) {
                model.open(entry)
            }
        }
    }

    private var transfersList: some View {
        VStack(alignment: .leading, spacing: 4) {
            ForEach(model.transfers) { transfer in
                HStack(spacing: 8) {
                    Image(systemName: transfer.upload ? "arrow.up.circle" : "arrow.down.circle")
                        .foregroundStyle(transfer.error == nil ? Color.secondary : Color.red)
                    Text(transfer.name).lineLimit(1)
                    if let error = transfer.error {
                        Text(error).font(.caption).foregroundStyle(.red).lineLimit(1)
                    } else if transfer.finished {
                        Text("Done").font(.caption).foregroundStyle(.secondary)
                    } else {
                        ProgressView(value: Double(transfer.done), total: Double(max(transfer.total, 1)))
                            .frame(maxWidth: 160)
                        Text(Self.sizeFormatter.string(fromByteCount: Int64(transfer.done)))
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    Spacer()
                }
            }
            if model.transfers.contains(where: { $0.finished }) {
                Button("Clear finished") { model.clearFinishedTransfers() }
                    .buttonStyle(.link)
                    .font(.caption)
            }
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 6)
    }

    private static let sizeFormatter: ByteCountFormatter = {
        let f = ByteCountFormatter()
        f.countStyle = .file
        return f
    }()

    private static let dateFormatter: DateFormatter = {
        let f = DateFormatter()
        f.dateStyle = .medium
        f.timeStyle = .short
        return f
    }()
}
