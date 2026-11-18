import SwiftUI

/// Assigns a credential to a folder. Connections inside it (and its
/// subfolders) whose authentication is "Folder's credential" use it, with
/// the nearest folder winning.
struct FolderCredentialView: View {
    @ObservedObject var model: AppModel
    let folder: String
    @State var selection: String?
    let onSave: (String?) -> Void
    let onCancel: () -> Void

    var body: some View {
        VStack(spacing: 0) {
            Form {
                Section("Folder credential") {
                    LabeledContent("Folder", value: folder)
                    CredentialPicker(title: "Credential", credentials: model.credentials,
                                     selection: $selection, noneLabel: "None")
                    Text("Connections in this folder and its subfolders set to “Folder's credential” use it. A credential on a nearer folder wins.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    if selection == nil, let inherited = model.inheritedCredential(forFolder: parent) {
                        Text("With none set here they use “\(inherited.credential.name)” from “\(inherited.folder)”.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                }
            }
            .formStyle(.grouped)

            HStack {
                Spacer()
                Button("Cancel", action: onCancel).keyboardShortcut(.cancelAction)
                Button("Save") { onSave(selection) }
                    .keyboardShortcut(.defaultAction)
                    .buttonStyle(.borderedProminent)
            }
            .padding(16)
        }
        .frame(width: 500, height: 280)
    }

    private var parent: String? {
        let path = (folder as NSString).deletingLastPathComponent
        return path.isEmpty ? nil : path
    }
}
