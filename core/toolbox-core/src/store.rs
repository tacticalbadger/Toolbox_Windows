//! Connections, credentials, providers and their on-disk store.
//!
//! The store is a single JSON document. Secrets never live here: they are in
//! the vault, in 1Password, or prompted for.

use std::collections::BTreeMap;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::{SystemTime, UNIX_EPOCH};

use serde::{Deserialize, Serialize};

use crate::ad;
use crate::error::{lock_or_recover, CoreError};
use crate::onepassword;

const DOCUMENT_VERSION: u32 = 2;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, uniffi::Enum)]
pub enum ConnectionKind {
    Ssh,
    Rdp,
    Vnc,
    /// File Transfer over SSH: opens the file browser, not a terminal.
    Sftp,
    /// File Transfer over plain FTP.
    Ftp,
    /// File Transfer over FTP upgraded with `AUTH TLS` (explicit FTPS).
    Ftps,
}

impl ConnectionKind {
    pub fn default_port(self) -> u16 {
        match self {
            ConnectionKind::Ssh | ConnectionKind::Sftp => 22,
            ConnectionKind::Rdp => 3389,
            ConnectionKind::Vnc => 5900,
            ConnectionKind::Ftp | ConnectionKind::Ftps => 21,
        }
    }
}

/// How a connection authenticates.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, uniffi::Enum)]
pub enum AuthKind {
    /// Ask for a password every time.
    Password,
    /// A private key on disk. Passphrase is prompted for if needed.
    KeyFile { path: String },
    /// Any SSH agent: the system one, or 1Password's, by socket path.
    Agent { socket_path: Option<String> },
    /// A credential object: vault entry, 1Password item or prompt.
    Credential { credential_id: String },
    /// Username on the connection, password kept in the vault under the
    /// connection's own id.
    StoredPassword,
    /// The credential assigned to the nearest folder above the connection.
    FolderCredential,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, uniffi::Record)]
pub struct Connection {
    pub id: String,
    pub name: String,
    pub kind: ConnectionKind,
    pub host: String,
    pub port: u16,
    /// Overrides the credential's username when non-empty.
    pub username: String,
    pub auth: AuthKind,
    pub folder: Option<String>,
    pub tags: Vec<String>,
    /// Provider that generated this record, if any. Replaced on each sync.
    #[serde(default)]
    pub source: Option<String>,
}

/// Where a credential's secret lives.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, uniffi::Enum)]
pub enum CredentialKind {
    /// Encrypted in the local vault under the credential's id.
    Vault,
    /// Fetched from 1Password with `op item get` when used.
    OnePassword {
        item_id: String,
        account: Option<String>,
        op_path: Option<String>,
    },
    /// Ask every time.
    Prompt,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, uniffi::Record)]
pub struct Credential {
    pub id: String,
    pub name: String,
    pub username: String,
    pub kind: CredentialKind,
    pub folder: Option<String>,
    /// Whether the secret includes an SSH private key.
    pub has_private_key: bool,
    pub tags: Vec<String>,
    #[serde(default)]
    pub source: Option<String>,
}

/// A dynamic folder: something that generates connections or credentials.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, uniffi::Enum)]
pub enum ProviderKind {
    OnePassword {
        /// Path to `op`; auto-detected when empty.
        op_path: Option<String>,
        /// Account shorthand for `--account`; all accounts when empty.
        account: Option<String>,
        /// Vault names to include; all vaults when empty.
        vaults: Vec<String>,
    },
    ActiveDirectory {
        /// Domain controller host, e.g. `dc01.corp.example`.
        server: String,
        /// Search base DN, e.g. `DC=corp,DC=example`.
        search_base: String,
        /// Credential used to bind (your regular domain account).
        bind_credential_id: Option<String>,
        /// Assigned to computers under a Domain Controllers OU.
        admin_credential_id: Option<String>,
        /// Assigned to every other computer.
        user_credential_id: Option<String>,
        /// Fall back to unencrypted LDAP when LDAPS fails.
        allow_plain_ldap: bool,
        accept_invalid_certs: bool,
    },
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, uniffi::Record)]
pub struct Provider {
    pub id: String,
    pub name: String,
    pub kind: ProviderKind,
    /// The provider's own folder, which the generated objects are placed
    /// under: the folder it sits in plus its name. Saving sets the last
    /// component from the name, so a rename moves the folder.
    pub folder: String,
    /// 0 disables automatic refresh.
    pub auto_refresh_minutes: u32,
    #[serde(default)]
    pub last_sync_epoch: Option<u64>,
    #[serde(default)]
    pub last_status: Option<String>,
}

/// Bind credentials for a sync, resolved by the app beforehand.
#[derive(Clone, uniffi::Record)]
pub struct BindCredentials {
    pub username: String,
    pub password: String,
}

impl std::fmt::Debug for BindCredentials {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("BindCredentials")
            .field("username", &self.username)
            .field("password", &"<redacted>")
            .finish()
    }
}

#[derive(Debug, Clone, PartialEq, Eq, uniffi::Record)]
pub struct SyncResult {
    pub provider_id: String,
    pub connections: u32,
    pub credentials: u32,
    pub warnings: Vec<String>,
}

/// A credential assigned to a folder. Connections underneath that use
/// `AuthKind::FolderCredential` pick it up, nearest folder first.
#[derive(Debug, Clone, PartialEq, Eq, uniffi::Record)]
pub struct FolderCredential {
    pub folder: String,
    pub credential_id: String,
}

#[derive(Debug, Default, Serialize, Deserialize)]
struct Document {
    version: u32,
    #[serde(default)]
    connections: Vec<Connection>,
    #[serde(default)]
    credentials: Vec<Credential>,
    #[serde(default)]
    providers: Vec<Provider>,
    /// Folder paths that exist on their own, so an empty folder survives.
    /// Slash-separated, e.g. `ACME/Jump Boxes`.
    #[serde(default)]
    folders: Vec<String>,
    /// Credential assigned to a folder, by path.
    #[serde(default)]
    folder_credentials: BTreeMap<String, String>,
}

#[derive(uniffi::Object)]
pub struct ConnectionStore {
    path: PathBuf,
    doc: Mutex<Document>,
}

#[uniffi::export]
impl ConnectionStore {
    /// Opens (or creates) the store at `path`.
    #[uniffi::constructor]
    pub fn open(path: String) -> Result<Arc<Self>, CoreError> {
        let path = PathBuf::from(path);
        let mut doc = if path.exists() {
            let text = fs::read_to_string(&path)?;
            if text.trim().is_empty() {
                Document::default()
            } else {
                serde_json::from_str(&text)?
            }
        } else {
            Document::default()
        };
        separate_provider_folders(&mut doc);
        Ok(Arc::new(Self {
            path,
            doc: Mutex::new(doc),
        }))
    }

    // ----- connections -----

    pub fn list(&self) -> Vec<Connection> {
        lock_or_recover(&self.doc).connections.clone()
    }

    pub fn get(&self, id: String) -> Option<Connection> {
        lock_or_recover(&self.doc).connections.iter().find(|c| c.id == id).cloned()
    }

    /// Inserts or replaces a connection. An empty `id` gets a fresh one.
    pub fn upsert(&self, mut connection: Connection) -> Result<Connection, CoreError> {
        if connection.name.trim().is_empty() {
            return Err(CoreError::Invalid("name is required".into()));
        }
        if connection.host.trim().is_empty() {
            return Err(CoreError::Invalid("host is required".into()));
        }
        if connection.id.trim().is_empty() {
            connection.id = new_id();
        }
        connection.folder = connection.folder.as_deref().and_then(normalize_folder);
        let mut doc = lock_or_recover(&self.doc);
        if connection.source.is_none() {
            if let Some(folder) = &connection.folder {
                register_folder(&mut doc, folder);
            }
        }
        match doc.connections.iter_mut().find(|c| c.id == connection.id) {
            Some(existing) => *existing = connection.clone(),
            None => doc.connections.push(connection.clone()),
        }
        sort_document(&mut doc);
        write_document(&self.path, &doc)?;
        Ok(connection)
    }

    pub fn delete(&self, id: String) -> Result<bool, CoreError> {
        let mut doc = lock_or_recover(&self.doc);
        let before = doc.connections.len();
        doc.connections.retain(|c| c.id != id);
        let removed = doc.connections.len() != before;
        if removed {
            write_document(&self.path, &doc)?;
        }
        Ok(removed)
    }

    // ----- credentials -----

    pub fn list_credentials(&self) -> Vec<Credential> {
        lock_or_recover(&self.doc).credentials.clone()
    }

    pub fn get_credential(&self, id: String) -> Option<Credential> {
        lock_or_recover(&self.doc).credentials.iter().find(|c| c.id == id).cloned()
    }

    pub fn upsert_credential(&self, mut credential: Credential) -> Result<Credential, CoreError> {
        if credential.name.trim().is_empty() {
            return Err(CoreError::Invalid("name is required".into()));
        }
        if let CredentialKind::OnePassword { item_id, .. } = &credential.kind {
            if item_id.trim().is_empty() {
                return Err(CoreError::Invalid("1Password item id is required".into()));
            }
        }
        if credential.id.trim().is_empty() {
            credential.id = new_id();
        }
        credential.folder = credential.folder.as_deref().and_then(normalize_folder);
        let mut doc = lock_or_recover(&self.doc);
        if credential.source.is_none() {
            if let Some(folder) = &credential.folder {
                register_folder(&mut doc, folder);
            }
        }
        match doc.credentials.iter_mut().find(|c| c.id == credential.id) {
            Some(existing) => *existing = credential.clone(),
            None => doc.credentials.push(credential.clone()),
        }
        sort_document(&mut doc);
        write_document(&self.path, &doc)?;
        Ok(credential)
    }

    pub fn delete_credential(&self, id: String) -> Result<bool, CoreError> {
        let mut doc = lock_or_recover(&self.doc);
        let before = doc.credentials.len();
        doc.credentials.retain(|c| c.id != id);
        let removed = doc.credentials.len() != before;
        let assigned = doc.folder_credentials.len();
        doc.folder_credentials.retain(|_, c| *c != id);
        if removed || doc.folder_credentials.len() != assigned {
            write_document(&self.path, &doc)?;
        }
        Ok(removed)
    }

    /// Connections that reference a credential, for warnings before deleting it.
    pub fn connections_using_credential(&self, id: String) -> Vec<Connection> {
        self.doc
            .lock()
            .unwrap()
            .connections
            .iter()
            .filter(|c| matches!(&c.auth, AuthKind::Credential { credential_id } if *credential_id == id))
            .cloned()
            .collect()
    }

    // ----- providers -----

    pub fn list_providers(&self) -> Vec<Provider> {
        lock_or_recover(&self.doc).providers.clone()
    }

    pub fn get_provider(&self, id: String) -> Option<Provider> {
        lock_or_recover(&self.doc).providers.iter().find(|p| p.id == id).cloned()
    }

    /// The provider as `upsert_provider` would store it (name trimmed, folder
    /// named after it), or what it would refuse, without saving anything:
    /// the app needs both before it writes the accounts typed into a provider.
    pub fn validate_provider(&self, provider: Provider) -> Result<Provider, CoreError> {
        checked_provider(&lock_or_recover(&self.doc), provider)
    }

    pub fn upsert_provider(&self, provider: Provider) -> Result<Provider, CoreError> {
        let mut doc = lock_or_recover(&self.doc);
        let mut provider = checked_provider(&doc, provider)?;
        if let Some(parent) = parent_path(&provider.folder) {
            register_folder(&mut doc, &parent);
        }
        let existing = doc.providers.iter().position(|p| p.id == provider.id);
        match existing {
            Some(index) => {
                let old = doc.providers[index].clone();
                // Keep sync bookkeeping unless the caller supplied it.
                if provider.last_sync_epoch.is_none() {
                    provider.last_sync_epoch = old.last_sync_epoch;
                    provider.last_status = old.last_status.clone();
                }
                // Everything inside a provider's folder (what it generated,
                // and anything placed there by hand) follows a rename or a
                // new location.
                if old.folder != provider.folder {
                    move_folder_contents(&mut doc, &old.folder, &provider.folder);
                }
                doc.providers[index] = provider.clone();
            }
            None => doc.providers.push(provider.clone()),
        }
        sort_document(&mut doc);
        write_document(&self.path, &doc)?;
        Ok(provider)
    }

    /// Removes a provider and everything it generated.
    pub fn delete_provider(&self, id: String) -> Result<bool, CoreError> {
        let mut doc = lock_or_recover(&self.doc);
        let Some(index) = doc.providers.iter().position(|p| p.id == id) else {
            return Ok(false);
        };
        let folder = doc.providers.remove(index).folder;
        doc.connections.retain(|c| c.source.as_deref() != Some(id.as_str()));
        doc.credentials.retain(|c| c.source.as_deref() != Some(id.as_str()));
        // With nothing else inside, the provider's folder goes with it, and
        // so does a credential assigned to the folder, which must not wait
        // there for the next one of that name. The accounts typed into the
        // provider do not count, nor does the folder entry saving them
        // made: the app deletes them with their secrets right after.
        let inside = |f: &str| f == folder || f.starts_with(&format!("{folder}/"));
        let own_account = |c: &Credential| c.source.is_none() && c.id.starts_with(&format!("{id}#"));
        let occupied = doc.folders.iter().any(|f| *f != folder && inside(f))
            || doc.providers.iter().any(|p| inside(&p.folder))
            || doc.connections.iter().any(|c| c.folder.as_deref().is_some_and(inside))
            || doc.credentials.iter().any(|c| !own_account(c) && c.folder.as_deref().is_some_and(inside));
        if !occupied {
            doc.folders.retain(|f| *f != folder);
            doc.folder_credentials.retain(|f, _| !inside(f));
        }
        write_document(&self.path, &doc)?;
        Ok(true)
    }

    // ----- folders -----

    /// Folders that exist in their own right (empty ones included), sorted.
    pub fn list_folders(&self) -> Vec<String> {
        lock_or_recover(&self.doc).folders.clone()
    }

    /// Creates a folder (and its ancestors). Returns the normalized path.
    pub fn add_folder(&self, path: String) -> Result<String, CoreError> {
        let path = normalize_folder(&path).ok_or_else(|| CoreError::Invalid("folder name is required".into()))?;
        let mut doc = lock_or_recover(&self.doc);
        register_folder(&mut doc, &path);
        write_document(&self.path, &doc)?;
        Ok(path)
    }

    /// Renames or moves a folder; everything inside follows.
    pub fn rename_folder(&self, from: String, to: String) -> Result<String, CoreError> {
        let from = normalize_folder(&from).ok_or_else(|| CoreError::Invalid("folder is required".into()))?;
        let to = normalize_folder(&to).ok_or_else(|| CoreError::Invalid("new name is required".into()))?;
        if to == from {
            return Ok(to);
        }
        if to.starts_with(&format!("{from}/")) {
            return Err(CoreError::Invalid("a folder cannot be moved into itself".into()));
        }
        let mut doc = lock_or_recover(&self.doc);
        check_provider_folders(&doc, &from, &to)?;
        let provider = doc.providers.iter().position(|p| p.folder == from);
        move_folder_contents(&mut doc, &from, &to);
        // A provider is named after its folder, so a new name for the
        // folder is a new name for the provider.
        if let Some(index) = provider {
            if leaf_name(&from) != leaf_name(&to) {
                doc.providers[index].name = leaf_name(&to).to_string();
            }
        }
        sort_document(&mut doc);
        write_document(&self.path, &doc)?;
        Ok(to)
    }

    /// Removes an empty folder. Anything inside has to be moved or deleted first.
    pub fn delete_folder(&self, path: String) -> Result<(), CoreError> {
        let path = normalize_folder(&path).ok_or_else(|| CoreError::Invalid("folder is required".into()))?;
        let mut doc = lock_or_recover(&self.doc);
        let inside = |f: Option<&str>| f.map(|f| f == path || f.starts_with(&format!("{path}/"))).unwrap_or(false);
        let occupied = doc.connections.iter().any(|c| inside(c.folder.as_deref()))
            || doc.credentials.iter().any(|c| inside(c.folder.as_deref()))
            || doc.providers.iter().any(|p| inside(Some(&p.folder)));
        if occupied {
            return Err(CoreError::Invalid(format!("“{path}” is not empty; move or delete its contents first")));
        }
        doc.folders.retain(|f| !inside(Some(f)));
        doc.folder_credentials.retain(|f, _| !inside(Some(f)));
        write_document(&self.path, &doc)?;
        Ok(())
    }

    // ----- folder credentials -----

    /// Every folder with a credential assigned, sorted by path.
    pub fn list_folder_credentials(&self) -> Vec<FolderCredential> {
        lock_or_recover(&self.doc)
            .folder_credentials
            .iter()
            .map(|(folder, credential_id)| FolderCredential {
                folder: folder.clone(),
                credential_id: credential_id.clone(),
            })
            .collect()
    }

    /// Assigns a credential to a folder, or clears it with `None`.
    pub fn set_folder_credential(&self, folder: String, credential_id: Option<String>) -> Result<(), CoreError> {
        let folder = normalize_folder(&folder).ok_or_else(|| CoreError::Invalid("folder is required".into()))?;
        let mut doc = lock_or_recover(&self.doc);
        match credential_id {
            Some(id) => {
                if !doc.credentials.iter().any(|c| c.id == id) {
                    return Err(CoreError::Invalid("no such credential".into()));
                }
                doc.folder_credentials.insert(folder, id);
            }
            None => {
                doc.folder_credentials.remove(&folder);
            }
        }
        write_document(&self.path, &doc)
    }

    /// What a connection in `folder` inherits: the nearest folder at or above
    /// it with a credential assigned that still exists.
    pub fn inherited_credential(&self, folder: Option<String>) -> Option<FolderCredential> {
        let doc = lock_or_recover(&self.doc);
        let mut current = folder.as_deref().and_then(normalize_folder);
        while let Some(path) = current {
            if let Some(id) = doc.folder_credentials.get(&path) {
                if doc.credentials.iter().any(|c| &c.id == id) {
                    return Some(FolderCredential { folder: path, credential_id: id.clone() });
                }
            }
            current = parent_path(&path);
        }
        None
    }

    /// Folders a credential is assigned to, for warnings before deleting it.
    pub fn folders_using_credential(&self, id: String) -> Vec<String> {
        lock_or_recover(&self.doc)
            .folder_credentials
            .iter()
            .filter(|(_, c)| **c == id)
            .map(|(f, _)| f.clone())
            .collect()
    }

    /// Moves a connection into a folder (`None` for the top level).
    /// Generated connections stay with their provider.
    pub fn move_connection(&self, id: String, folder: Option<String>) -> Result<(), CoreError> {
        let folder = folder.as_deref().and_then(normalize_folder);
        let mut doc = lock_or_recover(&self.doc);
        let connection = doc
            .connections
            .iter_mut()
            .find(|c| c.id == id)
            .ok_or_else(|| CoreError::Invalid("no such connection".into()))?;
        if connection.source.is_some() {
            return Err(CoreError::Invalid("generated connections stay with their provider".into()));
        }
        connection.folder = folder.clone();
        if let Some(folder) = &folder {
            register_folder(&mut doc, folder);
        }
        sort_document(&mut doc);
        write_document(&self.path, &doc)
    }

    pub fn move_credential(&self, id: String, folder: Option<String>) -> Result<(), CoreError> {
        let folder = folder.as_deref().and_then(normalize_folder);
        let mut doc = lock_or_recover(&self.doc);
        let credential = doc
            .credentials
            .iter_mut()
            .find(|c| c.id == id)
            .ok_or_else(|| CoreError::Invalid("no such credential".into()))?;
        if credential.source.is_some() {
            return Err(CoreError::Invalid("generated credentials stay with their provider".into()));
        }
        credential.folder = folder.clone();
        if let Some(folder) = &folder {
            register_folder(&mut doc, folder);
        }
        sort_document(&mut doc);
        write_document(&self.path, &doc)
    }

    /// Moves a provider (with everything it generated) under `parent`.
    pub fn move_provider(&self, id: String, parent: Option<String>) -> Result<(), CoreError> {
        let mut provider = self
            .get_provider(id)
            .ok_or_else(|| CoreError::Invalid("no such provider".into()))?;
        let parent = parent.as_deref().and_then(normalize_folder);
        provider.folder = provider_folder(parent.as_deref(), &provider.name);
        self.upsert_provider(provider).map(|_| ())
    }
}

/// Trims each path component and drops empty ones; `None` when nothing is left.
pub fn normalize_folder(path: &str) -> Option<String> {
    let parts: Vec<&str> = path
        .split('/')
        .map(str::trim)
        .filter(|p| !p.is_empty())
        .collect();
    if parts.is_empty() {
        None
    } else {
        Some(parts.join("/"))
    }
}

fn parent_path(path: &str) -> Option<String> {
    path.rsplit_once('/').map(|(parent, _)| parent.to_string())
}

/// The last component of a folder path: the folder's own name.
fn leaf_name(path: &str) -> &str {
    path.rsplit_once('/').map_or(path, |(_, leaf)| leaf)
}

/// Where a provider called `name` lives inside `location` (`None` for the
/// top level): a folder named after it.
fn provider_folder(location: Option<&str>, name: &str) -> String {
    match location {
        Some(location) => format!("{location}/{name}"),
        None => name.to_string(),
    }
}

/// Adds a folder and every ancestor to the explicit list.
fn register_folder(doc: &mut Document, path: &str) {
    let mut current = String::new();
    for part in path.split('/') {
        if !current.is_empty() {
            current.push('/');
        }
        current.push_str(part);
        if !doc.folders.iter().any(|f| f == &current) {
            doc.folders.push(current.clone());
        }
    }
    doc.folders.sort();
}

/// Moves everything at or under `from` to `to`: explicit folders,
/// connections, credentials, providers and folder credential assignments.
fn move_folder_contents(doc: &mut Document, from: &str, to: &str) {
    for folder in doc.folders.iter_mut() {
        if let Some(renamed) = replace_prefix(folder, from, to) {
            *folder = renamed;
        }
    }
    for c in doc.connections.iter_mut() {
        if let Some(renamed) = c.folder.as_deref().and_then(|f| replace_prefix(f, from, to)) {
            c.folder = Some(renamed);
        }
    }
    for c in doc.credentials.iter_mut() {
        if let Some(renamed) = c.folder.as_deref().and_then(|f| replace_prefix(f, from, to)) {
            c.folder = Some(renamed);
        }
    }
    for p in doc.providers.iter_mut() {
        if let Some(renamed) = replace_prefix(&p.folder, from, to) {
            p.folder = renamed;
        }
    }
    let moved: Vec<(String, String)> = doc
        .folder_credentials
        .iter()
        .filter_map(|(path, id)| replace_prefix(path, from, to).map(|renamed| (renamed, id.clone())))
        .collect();
    doc.folder_credentials.retain(|path, _| replace_prefix(path, from, to).is_none());
    doc.folder_credentials.extend(moved);
    if let Some(parent) = parent_path(to) {
        register_folder(doc, &parent);
    }
    doc.folders.sort();
    doc.folders.dedup();
}

/// `path` with a leading `from` folder replaced by `to`, or `None` when it
/// isn't inside `from`.
fn replace_prefix(path: &str, from: &str, to: &str) -> Option<String> {
    if path == from {
        Some(to.to_string())
    } else {
        path.strip_prefix(&format!("{from}/")).map(|rest| format!("{to}/{rest}"))
    }
}

/// A provider ready to save: required fields present, its folder named
/// after it and not another provider's, an id assigned.
fn checked_provider(doc: &Document, mut provider: Provider) -> Result<Provider, CoreError> {
    provider.name = provider.name.trim().to_string();
    if provider.name.is_empty() {
        return Err(CoreError::Invalid("name is required".into()));
    }
    if provider.name.contains('/') {
        return Err(CoreError::Invalid(
            "a provider's name cannot contain “/”; its location says which folder it is in".into(),
        ));
    }
    if let ProviderKind::ActiveDirectory { server, search_base, .. } = &provider.kind {
        if server.trim().is_empty() {
            return Err(CoreError::Invalid("LDAP server is required".into()));
        }
        if search_base.trim().is_empty() {
            return Err(CoreError::Invalid("search base is required".into()));
        }
    }
    // The folder given says where the provider sits; its own folder there
    // always carries its name, so the tree and the store cannot disagree.
    let location = normalize_folder(&provider.folder).and_then(|folder| parent_path(&folder));
    provider.folder = provider_folder(location.as_deref(), &provider.name);
    if provider.id.trim().is_empty() {
        provider.id = new_id();
    }
    match doc.providers.iter().find(|p| p.id == provider.id) {
        // A provider that is renamed or moved takes its folder's contents along.
        Some(old) => {
            if provider.folder.starts_with(&format!("{}/", old.folder)) {
                return Err(CoreError::Invalid("a provider cannot be moved into its own folder".into()));
            }
            check_provider_folders(doc, &old.folder, &provider.folder)?
        }
        None => {
            if let Some(owner) = doc.providers.iter().find(|p| p.folder == provider.folder) {
                return Err(folder_taken(&provider.folder, owner));
            }
        }
    }
    Ok(provider)
}

/// Refuses moving `from` to `to` when a provider would land in a folder
/// another provider owns. One folder, one provider: the sidebar shows a
/// provider as its folder, so a second one there is invisible while
/// everything it generates shows up next to the first one's.
fn check_provider_folders(doc: &Document, from: &str, to: &str) -> Result<(), CoreError> {
    let moved = |p: &Provider| replace_prefix(&p.folder, from, to);
    for target in doc.providers.iter().filter_map(moved) {
        if let Some(owner) = doc.providers.iter().find(|p| moved(p).is_none() && p.folder == target) {
            return Err(folder_taken(&target, owner));
        }
    }
    Ok(())
}

fn folder_taken(folder: &str, owner: &Provider) -> CoreError {
    CoreError::Invalid(format!(
        "the folder “{folder}” already belongs to the provider “{}”; choose another name or location",
        owner.name
    ))
}

/// Gives every provider a folder of its own in a document that has two in
/// one, which versions up to 0.3.0 let happen (every computer then showed
/// twice and the second provider could not be reached to edit or delete).
/// The folder stays with the provider it is named after, or else with the
/// first. Any other moves, with what it generated and the accounts typed
/// into it (`<id>#bind` and so on), to a folder of its own next to the
/// shared one, where it is visible and can be deleted. That folder carries
/// its name, as every provider's does; when the name leads to a folder in
/// use or is a neighbouring provider's too, the provider is renamed
/// `<name> 2` (or 3, …) to get one and to tell the two apart. A credential
/// assigned to the shared folder is assigned to the new one as well, so
/// connections that inherit it keep working.
fn separate_provider_folders(doc: &mut Document) {
    let mut separated = false;
    for index in 0..doc.providers.len() {
        let shared = doc.providers[index].folder.clone();
        let sharing: Vec<usize> = (0..doc.providers.len()).filter(|i| doc.providers[*i].folder == shared).collect();
        let named_after = |i: &&usize| doc.providers[**i].name == leaf_name(&shared);
        if sharing.iter().find(named_after).or(sharing.first()) == Some(&index) {
            continue;
        }
        let location = parent_path(&shared);
        let in_use = |path: &str| {
            let inside = |f: &str| f == path || f.starts_with(&format!("{path}/"));
            doc.folders.iter().any(|f| inside(f))
                || doc.providers.iter().any(|p| inside(&p.folder))
                || doc.connections.iter().any(|c| c.folder.as_deref().is_some_and(inside))
                || doc.credentials.iter().any(|c| c.folder.as_deref().is_some_and(inside))
        };
        let named_alike = |name: &str| {
            let mut others = doc.providers.iter().enumerate().filter(|(i, _)| *i != index);
            others.any(|(_, p)| p.name == name && parent_path(&p.folder) == location)
        };
        // Names saved by 0.3.0 and earlier could hold a slash.
        let mut base = doc.providers[index].name.trim().replace('/', "-");
        if base.is_empty() {
            base = leaf_name(&shared).to_string();
        }
        let mut n = 1;
        let (name, own) = loop {
            let name = if n == 1 { base.clone() } else { format!("{base} {n}") };
            let folder = provider_folder(location.as_deref(), &name);
            if !in_use(&folder) && !named_alike(&name) {
                break (name, folder);
            }
            n += 1;
        };
        let id = doc.providers[index].id.clone();
        let account_prefix = format!("{id}#");
        for c in doc.connections.iter_mut().filter(|c| c.source.as_deref() == Some(id.as_str())) {
            if let Some(renamed) = c.folder.as_deref().and_then(|f| replace_prefix(f, &shared, &own)) {
                c.folder = Some(renamed);
            }
        }
        let belongs = |c: &Credential| match &c.source {
            Some(source) => *source == id,
            None => c.id.starts_with(&account_prefix),
        };
        for c in doc.credentials.iter_mut().filter(|c| belongs(c)) {
            if let Some(renamed) = c.folder.as_deref().and_then(|f| replace_prefix(f, &shared, &own)) {
                c.folder = Some(renamed);
            }
        }
        let assigned: Vec<(String, String)> = doc
            .folder_credentials
            .iter()
            .filter_map(|(path, id)| replace_prefix(path, &shared, &own).map(|renamed| (renamed, id.clone())))
            .collect();
        doc.folder_credentials.extend(assigned);
        let provider = &mut doc.providers[index];
        provider.folder = own;
        provider.name = name;
        separated = true;
    }
    if separated {
        sort_document(doc);
    }
}

#[uniffi::export(async_runtime = "tokio")]
impl ConnectionStore {
    /// Runs a provider and replaces what it generated last time.
    /// Active Directory needs `bind`; 1Password ignores it.
    pub async fn sync_provider(&self, id: String, bind: Option<BindCredentials>) -> Result<SyncResult, CoreError> {
        let provider = self
            .get_provider(id.clone())
            .ok_or_else(|| CoreError::Invalid(format!("no provider with id {id}")))?;

        let outcome = match &provider.kind {
            ProviderKind::OnePassword { op_path, account, vaults } => {
                match onepassword::find_op_path(op_path.as_deref()) {
                    None => Err(CoreError::Invalid(
                        "1Password CLI (op) not found; install it or set its path on the provider".into(),
                    )),
                    Some(path) => onepassword::list_items(&path, account.as_deref(), vaults)
                        .await
                        .map(|items| {
                            let credentials = items_to_credentials(&provider, items, op_path.clone(), account.clone());
                            (Vec::new(), credentials, Vec::new())
                        }),
                }
            }
            ProviderKind::ActiveDirectory {
                server,
                search_base,
                admin_credential_id,
                user_credential_id,
                allow_plain_ldap,
                accept_invalid_certs,
                ..
            } => match bind {
                None => Err(CoreError::Auth("Active Directory sync needs a bind credential".into())),
                Some(bind) => {
                    let settings = ad::AdSettings {
                        server: server.clone(),
                        search_base: search_base.clone(),
                        allow_plain_ldap: *allow_plain_ldap,
                        accept_invalid_certs: *accept_invalid_certs,
                    };
                    let admin = admin_credential_id.as_ref().and_then(|id| self.get_credential(id.clone()));
                    let user = user_credential_id.as_ref().and_then(|id| self.get_credential(id.clone()));
                    ad::fetch_computers(&settings, &bind.username, &bind.password)
                        .await
                        .map(|(computers, warnings)| {
                            let connections = ad::to_connections(&computers, &provider, admin.as_ref(), user.as_ref());
                            (connections, Vec::new(), warnings)
                        })
                }
            },
        };

        let now = epoch_now();
        match outcome {
            Ok((mut connections, mut credentials, warnings)) => {
                let result = SyncResult {
                    provider_id: id.clone(),
                    connections: connections.len() as u32,
                    credentials: credentials.len() as u32,
                    warnings,
                };
                let status = match &provider.kind {
                    ProviderKind::OnePassword { .. } => format!("{} credentials", result.credentials),
                    ProviderKind::ActiveDirectory { .. } => format!("{} connections", result.connections),
                };
                let mut doc = lock_or_recover(&self.doc);
                // The answer took a while. A provider deleted meanwhile gets
                // nothing; one renamed or moved gets it in its folder of now.
                let Some(folder) = doc.providers.iter().find(|p| p.id == id).map(|p| p.folder.clone()) else {
                    return Err(CoreError::Invalid("the provider was deleted while it synced".into()));
                };
                if folder != provider.folder {
                    let generated = connections.iter_mut().map(|c| &mut c.folder);
                    for placed in generated.chain(credentials.iter_mut().map(|c| &mut c.folder)) {
                        if let Some(moved) = placed.as_deref().and_then(|f| replace_prefix(f, &provider.folder, &folder)) {
                            *placed = Some(moved);
                        }
                    }
                }
                doc.connections.retain(|c| c.source.as_deref() != Some(id.as_str()));
                doc.credentials.retain(|c| c.source.as_deref() != Some(id.as_str()));
                doc.connections.extend(connections);
                doc.credentials.extend(credentials);
                if let Some(p) = doc.providers.iter_mut().find(|p| p.id == id) {
                    p.last_sync_epoch = Some(now);
                    p.last_status = Some(status);
                }
                sort_document(&mut doc);
                write_document(&self.path, &doc)?;
                Ok(result)
            }
            Err(e) => {
                let mut doc = lock_or_recover(&self.doc);
                if let Some(p) = doc.providers.iter_mut().find(|p| p.id == id) {
                    p.last_status = Some(format!("failed: {e}"));
                }
                write_document(&self.path, &doc)?;
                Err(e)
            }
        }
    }
}

/// A fresh random identifier for a new record.
#[uniffi::export]
pub fn new_id() -> String {
    uuid::Uuid::new_v4().to_string()
}

/// Credential records for 1Password items. Ids are stable across syncs so
/// connections can keep referring to them.
fn items_to_credentials(
    provider: &Provider,
    items: Vec<onepassword::OpItem>,
    op_path: Option<String>,
    account: Option<String>,
) -> Vec<Credential> {
    items
        .into_iter()
        .map(|item| {
            let folder = match &item.vault {
                Some(vault) if !vault.is_empty() => format!("{}/{}", provider.folder, vault),
                _ => provider.folder.clone(),
            };
            let category = item.category.clone().unwrap_or_default();
            Credential {
                id: format!("{}:{}", provider.id, item.id),
                name: item.title,
                username: item.username.unwrap_or_default(),
                kind: CredentialKind::OnePassword {
                    item_id: item.id,
                    account: account.clone(),
                    op_path: op_path.clone(),
                },
                folder: Some(folder),
                has_private_key: category.eq_ignore_ascii_case("SSH_KEY"),
                tags: if category.is_empty() { Vec::new() } else { vec![category.to_lowercase()] },
                source: Some(provider.id.clone()),
            }
        })
        .collect()
}

fn epoch_now() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0)
}

fn sort_document(doc: &mut Document) {
    let by_folder_name = |a: &Option<String>, b: &Option<String>, an: &str, bn: &str| {
        a.cmp(b).then_with(|| an.to_lowercase().cmp(&bn.to_lowercase()))
    };
    doc.connections
        .sort_by(|a, b| by_folder_name(&a.folder, &b.folder, &a.name, &b.name));
    doc.credentials
        .sort_by(|a, b| by_folder_name(&a.folder, &b.folder, &a.name, &b.name));
    doc.providers.sort_by_key(|p| p.name.to_lowercase());
}

fn write_document(path: &Path, doc: &Document) -> Result<(), CoreError> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent)?;
    }
    let out = Document {
        version: DOCUMENT_VERSION,
        connections: doc.connections.clone(),
        credentials: doc.credentials.clone(),
        providers: doc.providers.clone(),
        folders: doc.folders.clone(),
        folder_credentials: doc.folder_credentials.clone(),
    };
    let text = serde_json::to_string_pretty(&out)?;
    // Write to a sibling temp file then rename so a crash never truncates.
    let tmp = path.with_extension("json.tmp");
    fs::write(&tmp, text)?;
    restrict_permissions(&tmp)?;
    fs::rename(&tmp, path)?;
    Ok(())
}

/// Owner read/write only: the document names hosts and accounts.
#[cfg(unix)]
pub(crate) fn restrict_permissions(path: &Path) -> std::io::Result<()> {
    use std::os::unix::fs::PermissionsExt;
    fs::set_permissions(path, fs::Permissions::from_mode(0o600))
}

/// On Windows the files live under the user's profile (`%APPDATA%`), whose
/// ACL already excludes other users; NTFS has no mode bits to tighten
/// without rewriting the ACL, so nothing is done here.
#[cfg(windows)]
pub(crate) fn restrict_permissions(_path: &Path) -> std::io::Result<()> {
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sample(name: &str) -> Connection {
        Connection {
            id: String::new(),
            name: name.into(),
            kind: ConnectionKind::Ssh,
            host: "example.local".into(),
            port: 22,
            username: "root".into(),
            auth: AuthKind::Agent { socket_path: None },
            folder: Some("Lab".into()),
            tags: vec!["nutanix".into()],
            source: None,
        }
    }

    fn open_temp() -> (tempfile::TempDir, Arc<ConnectionStore>) {
        let dir = tempfile::tempdir().unwrap();
        let store = ConnectionStore::open(dir.path().join("c.json").to_string_lossy().into()).unwrap();
        (dir, store)
    }

    #[test]
    fn roundtrip_through_disk() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("connections.json");
        let store = ConnectionStore::open(path.to_string_lossy().into()).unwrap();
        let stored = store.upsert(sample("nutanix1")).unwrap();
        assert!(!stored.id.is_empty());
        store.upsert(sample("aihost01")).unwrap();

        let reopened = ConnectionStore::open(path.to_string_lossy().into()).unwrap();
        let names: Vec<_> = reopened.list().into_iter().map(|c| c.name).collect();
        assert_eq!(names, vec!["aihost01", "nutanix1"]);

        assert!(reopened.delete(stored.id.clone()).unwrap());
        assert!(!reopened.delete(stored.id).unwrap());
        assert_eq!(reopened.list().len(), 1);
    }

    #[test]
    fn upsert_replaces_by_id() {
        let (_dir, store) = open_temp();
        let mut c = store.upsert(sample("one")).unwrap();
        c.host = "changed.local".into();
        store.upsert(c.clone()).unwrap();
        assert_eq!(store.list().len(), 1);
        assert_eq!(store.get(c.id).unwrap().host, "changed.local");
    }

    #[test]
    fn rejects_blank_name_and_host() {
        let (_dir, store) = open_temp();
        let mut c = sample("");
        assert!(matches!(store.upsert(c.clone()), Err(CoreError::Invalid(_))));
        c.name = "x".into();
        c.host = " ".into();
        assert!(matches!(store.upsert(c), Err(CoreError::Invalid(_))));
    }

    #[test]
    fn loads_a_version_one_document() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("c.json");
        fs::write(
            &path,
            r#"{"version":1,"connections":[{"id":"a","name":"old","kind":"Ssh","host":"h","port":22,
                "username":"u","auth":{"KeyFile":{"path":"/k"}},"folder":null,"tags":[]}]}"#,
        )
        .unwrap();
        let store = ConnectionStore::open(path.to_string_lossy().into()).unwrap();
        let c = store.get("a".into()).unwrap();
        assert_eq!(c.auth, AuthKind::KeyFile { path: "/k".into() });
        assert_eq!(c.source, None);
        assert!(store.list_credentials().is_empty());
        assert!(store.list_providers().is_empty());
    }

    #[test]
    fn credentials_and_their_users() {
        let (_dir, store) = open_temp();
        let cred = store
            .upsert_credential(Credential {
                id: String::new(),
                name: "domain admin".into(),
                username: "CORP\\admin".into(),
                kind: CredentialKind::Vault,
                folder: None,
                has_private_key: false,
                tags: vec![],
                source: None,
            })
            .unwrap();
        let mut c = sample("dc01");
        c.auth = AuthKind::Credential { credential_id: cred.id.clone() };
        store.upsert(c).unwrap();
        assert_eq!(store.connections_using_credential(cred.id.clone()).len(), 1);
        assert!(store.delete_credential(cred.id.clone()).unwrap());
        assert!(store.get_credential(cred.id).is_none());

        let bad = Credential {
            id: String::new(),
            name: "x".into(),
            username: String::new(),
            kind: CredentialKind::OnePassword { item_id: " ".into(), account: None, op_path: None },
            folder: None,
            has_private_key: false,
            tags: vec![],
            source: None,
        };
        assert!(matches!(store.upsert_credential(bad), Err(CoreError::Invalid(_))));
    }

    #[test]
    fn deleting_a_provider_removes_what_it_generated() {
        let (_dir, store) = open_temp();
        let provider = store
            .upsert_provider(Provider {
                id: String::new(),
                name: "1Password".into(),
                kind: ProviderKind::OnePassword { op_path: None, account: None, vaults: vec![] },
                folder: String::new(),
                auto_refresh_minutes: 0,
                last_sync_epoch: None,
                last_status: None,
            })
            .unwrap();
        assert_eq!(provider.folder, "1Password");
        let mut generated = sample("gen");
        generated.source = Some(provider.id.clone());
        store.upsert(generated).unwrap();
        store.upsert(sample("manual")).unwrap();
        assert!(store.delete_provider(provider.id).unwrap());
        let names: Vec<_> = store.list().into_iter().map(|c| c.name).collect();
        assert_eq!(names, vec!["manual"]);
    }

    #[test]
    fn deleting_a_provider_takes_its_folders_credential_along() {
        let (_dir, store) = open_temp();
        store.upsert_credential(sample_credential("admin")).unwrap();
        for id in ["gone", "kept"] {
            store.upsert_provider(sample_provider(id, id, id)).unwrap();
            store.set_folder_credential(id.into(), Some("admin".into())).unwrap();
            let mut account = sample_credential(&format!("{id}#bind"));
            account.folder = Some(id.into());
            store.upsert_credential(account).unwrap();
            let mut generated = sample("dc01");
            generated.folder = Some(format!("{id}/Domain Controllers"));
            generated.source = Some(id.into());
            store.upsert(generated).unwrap();
        }
        // Something put there by hand keeps the folder in the tree.
        let mut by_hand = sample("printer");
        by_hand.folder = Some("kept/Lab".into());
        store.upsert(by_hand).unwrap();

        for id in ["gone", "kept"] {
            assert!(store.delete_provider(id.into()).unwrap());
        }
        assert!(!store.delete_provider("gone".into()).unwrap());
        let assigned: Vec<_> = store.list_folder_credentials().into_iter().map(|f| f.folder).collect();
        assert_eq!(assigned, vec!["kept"]);
        assert_eq!(store.list_folders(), vec!["kept", "kept/Lab"]);
        assert_eq!(store.list().into_iter().map(|c| c.name).collect::<Vec<_>>(), vec!["printer"]);
    }

    #[test]
    fn provider_validation() {
        let (_dir, store) = open_temp();
        let ad = Provider {
            id: String::new(),
            name: "AD".into(),
            kind: ProviderKind::ActiveDirectory {
                server: String::new(),
                search_base: "DC=x".into(),
                bind_credential_id: None,
                admin_credential_id: None,
                user_credential_id: None,
                allow_plain_ldap: true,
                accept_invalid_certs: false,
            },
            folder: "AD".into(),
            auto_refresh_minutes: 0,
            last_sync_epoch: None,
            last_status: None,
        };
        assert!(matches!(store.upsert_provider(ad), Err(CoreError::Invalid(_))));
    }

    fn sample_provider(id: &str, name: &str, folder: &str) -> Provider {
        Provider {
            id: id.into(),
            name: name.into(),
            kind: ProviderKind::OnePassword { op_path: None, account: None, vaults: vec![] },
            folder: folder.into(),
            auto_refresh_minutes: 0,
            last_sync_epoch: None,
            last_status: None,
        }
    }

    #[test]
    fn a_folder_takes_one_provider() {
        let (_dir, store) = open_temp();
        let first = store.upsert_provider(sample_provider("ad", "corp", "Work/corp")).unwrap();

        // A new provider cannot be saved into the folder, however it is spelled.
        let second = sample_provider("", " corp ", " Work / corp ");
        for refused in [store.validate_provider(second.clone()), store.upsert_provider(second)] {
            let Err(CoreError::Invalid(message)) = refused else { panic!("accepted a second provider") };
            assert!(message.contains("the folder “Work/corp” already belongs to the provider “corp”"), "{message}");
        }
        assert_eq!(store.list_providers(), vec![first.clone()]);
        // Saving the owner again is not a clash.
        assert_eq!(store.validate_provider(first.clone()).unwrap(), first);
        store.upsert_provider(first).unwrap();

        // Nor can an existing one get there by a new name, a new location,
        // a move or a folder rename.
        let mut lab = store.upsert_provider(sample_provider("lab", "lab", "Work/lab")).unwrap();
        lab.name = "corp".into();
        assert!(matches!(store.upsert_provider(lab), Err(CoreError::Invalid(_))));
        assert!(matches!(store.rename_folder("Work/lab".into(), "Work/corp".into()), Err(CoreError::Invalid(_))));
        let mut other = store.upsert_provider(sample_provider("other", "corp", "Archive/corp")).unwrap();
        other.folder = "Work/corp".into();
        assert!(matches!(store.upsert_provider(other), Err(CoreError::Invalid(_))));
        assert!(matches!(store.move_provider("other".into(), Some("Work".into())), Err(CoreError::Invalid(_))));
        assert!(matches!(store.rename_folder("Archive".into(), "Work".into()), Err(CoreError::Invalid(_))));
        let placed = |id: &str| store.get_provider(id.into()).map(|p| (p.name, p.folder)).unwrap();
        assert_eq!(placed("lab"), ("lab".into(), "Work/lab".into()));
        assert_eq!(placed("other"), ("corp".into(), "Archive/corp".into()));
        assert_eq!(placed("ad"), ("corp".into(), "Work/corp".into()));

        // A free folder is fine.
        store.move_provider("other".into(), Some("Work/Old".into())).unwrap();
        assert_eq!(placed("other"), ("corp".into(), "Work/Old/corp".into()));
    }

    #[test]
    fn a_providers_folder_is_named_after_it() {
        let (_dir, store) = open_temp();
        // The folder given says where the provider goes; the name says what
        // its own folder there is called.
        let mut provider = store.upsert_provider(sample_provider("ad", " corp-ad ", "Work/anything")).unwrap();
        assert_eq!((provider.name.as_str(), provider.folder.as_str()), ("corp-ad", "Work/corp-ad"));
        assert_eq!(store.validate_provider(sample_provider("", "Logins", "")).unwrap().folder, "Logins");

        let mut generated = sample("dc01");
        generated.folder = Some("Work/corp-ad/Domain Controllers".into());
        generated.source = Some("ad".into());
        let generated = store.upsert(generated).unwrap();
        let mut by_hand = sample("printer");
        by_hand.folder = Some("Work/corp-ad/Lab".into());
        let by_hand = store.upsert(by_hand).unwrap();
        let mut bind = sample_credential("ad#bind");
        bind.folder = Some("Work/corp-ad".into());
        store.upsert_credential(bind).unwrap();
        store.set_folder_credential("Work/corp-ad".into(), Some("ad#bind".into())).unwrap();
        let folder = |id: &str| store.get(id.into()).unwrap().folder.unwrap();

        // A folder with the new name may be there already, with things in
        // it: the two become one.
        let mut bind = store.get_credential("ad#bind".into()).unwrap();
        bind.folder = Some("Work/Corp AD".into());
        store.upsert_credential(bind).unwrap();
        provider.name = "Corp AD".into();
        store.upsert_provider(provider.clone()).unwrap();
        assert_eq!(folder(&generated.id), "Work/Corp AD/Domain Controllers");
        assert_eq!(store.list_folders(), vec!["Work", "Work/Corp AD", "Work/Corp AD/Lab"]);

        // A new name moves the folder and everything in it: what the
        // provider generated, what was put there by hand, its own account
        // and the credential assigned to the folder.
        provider.name = "CORP.EXAMPLE".into();
        let renamed = store.upsert_provider(provider.clone()).unwrap();
        assert_eq!(renamed.folder, "Work/CORP.EXAMPLE");
        assert_eq!(folder(&generated.id), "Work/CORP.EXAMPLE/Domain Controllers");
        assert_eq!(folder(&by_hand.id), "Work/CORP.EXAMPLE/Lab");
        assert_eq!(store.get_credential("ad#bind".into()).unwrap().folder.as_deref(), Some("Work/CORP.EXAMPLE"));
        let assigned: Vec<_> = store.list_folder_credentials().into_iter().map(|f| f.folder).collect();
        assert_eq!(assigned, vec!["Work/CORP.EXAMPLE"]);
        assert_eq!(store.list_folders(), vec!["Work", "Work/CORP.EXAMPLE", "Work/CORP.EXAMPLE/Lab"]);

        // A slash in the name would make it two folders.
        provider.name = "CORP/EXAMPLE".into();
        let Err(CoreError::Invalid(message)) = store.upsert_provider(provider) else { panic!("accepted a slash") };
        assert!(message.contains("cannot contain “/”"), "{message}");

        // A move keeps the name, and cannot go into the provider's own folder.
        store.move_provider("ad".into(), None).unwrap();
        let placed = || store.get_provider("ad".into()).map(|p| (p.name, p.folder)).unwrap();
        assert_eq!(placed(), ("CORP.EXAMPLE".into(), "CORP.EXAMPLE".into()));
        assert_eq!(folder(&generated.id), "CORP.EXAMPLE/Domain Controllers");
        assert!(matches!(
            store.move_provider("ad".into(), Some("CORP.EXAMPLE/Lab".into())),
            Err(CoreError::Invalid(_))
        ));

        // Renaming the folder renames the provider; moving it does not.
        store.rename_folder("CORP.EXAMPLE".into(), "Lab/Directory".into()).unwrap();
        assert_eq!(placed(), ("Directory".into(), "Lab/Directory".into()));
        store.rename_folder("Lab".into(), "Bench".into()).unwrap();
        assert_eq!(placed(), ("Directory".into(), "Bench/Directory".into()));
        assert_eq!(folder(&generated.id), "Bench/Directory/Domain Controllers");
        let reopened = ConnectionStore::open(store.path.to_string_lossy().into()).unwrap();
        assert_eq!(reopened.list_providers(), store.list_providers());
    }

    #[test]
    fn a_folder_from_an_older_version_takes_the_name_at_the_next_save() {
        // Up to 0.3.0 a rename changed the name only, so a store can hold a
        // provider whose folder still has the name it was created with.
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("c.json");
        let mut generated = sample("dc01");
        generated.folder = Some("Work/AD/Domain Controllers".into());
        generated.source = Some("ad".into());
        let doc = Document {
            version: DOCUMENT_VERSION,
            connections: vec![generated],
            providers: vec![sample_provider("ad", "Active Directory", "Work/AD"), sample_provider("op", "Logins", "Work/1P")],
            ..Document::default()
        };
        write_document(&path, &doc).unwrap();

        // Opening moves nothing.
        let store = ConnectionStore::open(path.to_string_lossy().into()).unwrap();
        let folder = |id: &str| store.get_provider(id.into()).unwrap().folder;
        assert_eq!((folder("ad"), folder("op")), ("Work/AD".into(), "Work/1P".into()));

        // Saving it does, and so does moving it.
        store.upsert_provider(store.get_provider("ad".into()).unwrap()).unwrap();
        assert_eq!(folder("ad"), "Work/Active Directory");
        assert_eq!(store.list()[0].folder.as_deref(), Some("Work/Active Directory/Domain Controllers"));
        store.move_provider("op".into(), Some("Work".into())).unwrap();
        assert_eq!(folder("op"), "Work/Logins");
    }

    #[test]
    fn opening_separates_providers_that_share_a_folder() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("c.json");
        let in_folder = |id: &str, folder: &str, source: Option<&str>| {
            let mut c = sample("dc01");
            c.id = id.into();
            c.folder = Some(folder.into());
            c.source = source.map(Into::into);
            c
        };
        let credential_in = |id: &str, folder: &str, source: Option<&str>| {
            let mut c = sample_credential(id);
            c.folder = Some(folder.into());
            c.source = source.map(Into::into);
            c
        };
        let doc = Document {
            version: DOCUMENT_VERSION,
            connections: vec![
                in_folder("ad1:dc01", "Work/AD/Domain Controllers", Some("ad1")),
                in_folder("ad2:dc01", "Work/AD/Domain Controllers", Some("ad2")),
                in_folder("by-hand", "Work/AD", None),
                in_folder("elsewhere", "Work/AD 2", None),
                in_folder("old:dc01", "Lab/dir/Domain Controllers", Some("old")),
                in_folder("new:dc01", "Lab/dir/Domain Controllers", Some("new")),
            ],
            credentials: vec![
                credential_in("ad2#bind", "Work/AD", None),
                credential_in("op:i1", "Work/AD/vault", Some("op")),
                credential_in("admin", "Work", None),
            ],
            providers: vec![
                sample_provider("ad1", "AD", "Work/AD"),
                sample_provider("ad2", "AD", "Work/AD"),
                sample_provider("op", "Logins", "Work/AD"),
                // Renamed after it was made, then a second one made under the first name.
                sample_provider("old", "Directory", "Lab/dir"),
                sample_provider("new", "dir", "Lab/dir"),
                // Two renamed alike, in a folder neither is named after.
                sample_provider("x1", "Corp", "Lab/shared"),
                sample_provider("x2", "Corp", "Lab/shared"),
                sample_provider("x3", "Old/Corp", "Lab/shared"),
            ],
            folder_credentials: BTreeMap::from([("Work/AD".to_string(), "admin".to_string())]),
            ..Document::default()
        };
        write_document(&path, &doc).unwrap();
        let written = fs::read_to_string(&path).unwrap();

        let store = ConnectionStore::open(path.to_string_lossy().into()).unwrap();
        let folder = |id: &str| store.get(id.into()).unwrap().folder.unwrap();
        let provider = |id: &str| store.get_provider(id.into()).map(|p| (p.name, p.folder)).unwrap();
        // The folder stays with the first provider named after it. Another
        // goes to the folder its own name gives, with what it generated and
        // its own accounts, and is numbered when that folder is in use.
        assert_eq!(provider("ad1"), ("AD".into(), "Work/AD".into()));
        assert_eq!(provider("ad2"), ("AD 3".into(), "Work/AD 3".into()));
        assert_eq!(provider("op"), ("Logins".into(), "Work/Logins".into()));
        assert_eq!(folder("ad1:dc01"), "Work/AD/Domain Controllers");
        assert_eq!(folder("ad2:dc01"), "Work/AD 3/Domain Controllers");
        assert_eq!(store.get_credential("ad2#bind".into()).unwrap().folder.as_deref(), Some("Work/AD 3"));
        assert_eq!(store.get_credential("op:i1".into()).unwrap().folder.as_deref(), Some("Work/Logins/vault"));
        assert_eq!(folder("by-hand"), "Work/AD");
        assert_eq!(folder("elsewhere"), "Work/AD 2");
        assert_eq!(provider("new"), ("dir".into(), "Lab/dir".into()));
        assert_eq!(provider("old"), ("Directory".into(), "Lab/Directory".into()));
        assert_eq!(folder("old:dc01"), "Lab/Directory/Domain Controllers");
        assert_eq!(folder("new:dc01"), "Lab/dir/Domain Controllers");
        // With no one named after the folder the first keeps it. A name a
        // neighbour has is numbered too, and a slash cannot stay in one.
        assert_eq!(provider("x1"), ("Corp".into(), "Lab/shared".into()));
        assert_eq!(provider("x2"), ("Corp 2".into(), "Lab/Corp 2".into()));
        assert_eq!(provider("x3"), ("Old-Corp".into(), "Lab/Old-Corp".into()));
        // What the shared folder's credential reached, it still reaches.
        let inherited = |folder: &str| store.inherited_credential(Some(folder.into())).map(|f| f.folder);
        assert_eq!(inherited("Work/AD/Domain Controllers"), Some("Work/AD".into()));
        assert_eq!(inherited("Work/AD 3/Domain Controllers"), Some("Work/AD 3".into()));
        assert_eq!(inherited("Work/Logins/vault"), Some("Work/Logins".into()));
        // Opening alone writes nothing: a file the app cannot write must still open.
        assert_eq!(fs::read_to_string(&path).unwrap(), written);

        // Now reachable, the duplicate can be deleted, leaving each computer
        // once and nothing of the duplicate behind.
        assert!(store.delete_provider("ad2".into()).unwrap());
        store.delete_credential("ad2#bind".into()).unwrap();
        let reopened = ConnectionStore::open(path.to_string_lossy().into()).unwrap();
        let generated: Vec<_> = reopened.list().into_iter().filter_map(|c| c.source.map(|_| c.id)).collect();
        assert_eq!(generated, vec!["old:dc01", "new:dc01", "ad1:dc01"]);
        assert_eq!(reopened.get_provider("ad1".into()).unwrap().folder, "Work/AD");
        let assigned: Vec<_> = reopened.list_folder_credentials().into_iter().map(|f| f.folder).collect();
        assert_eq!(assigned, vec!["Work/AD", "Work/Logins"]);
        // What the repair did was saved with that change; a second open has nothing to do.
        assert_eq!(reopened.list_providers(), store.list_providers());
    }

    /// Writes a fake `op` that answers only once a file named `go` is next
    /// to it (or after half a minute), having noted in `op.log` that it was
    /// asked. On Windows a batch file; `ping` is its way of waiting a second.
    #[cfg(windows)]
    fn waiting_op(dir: &Path) -> PathBuf {
        let path = dir.join("op.cmd");
        fs::write(
            &path,
            "@echo off\r\n\
             echo %* >> \"%~dp0op.log\"\r\n\
             set n=0\r\n\
             :wait\r\n\
             if exist \"%~dp0go\" goto answer\r\n\
             set /a n+=1\r\n\
             if %n% GEQ 30 goto answer\r\n\
             ping -n 2 127.0.0.1 > nul\r\n\
             goto wait\r\n\
             :answer\r\n\
             echo [{\"id\":\"i1\",\"title\":\"nutanix1\",\"vault\":{\"name\":\"work\"},\"category\":\"LOGIN\"}]\r\n",
        )
        .unwrap();
        path
    }

    #[cfg(unix)]
    fn waiting_op(dir: &Path) -> PathBuf {
        use std::os::unix::fs::PermissionsExt;
        let path = dir.join("op");
        fs::write(
            &path,
            r#"#!/bin/sh
here="$(dirname "$0")"
echo "$@" >> "$here/op.log"
n=0
while [ ! -e "$here/go" ] && [ "$n" -lt 600 ]; do sleep 0.05; n=$((n + 1)); done
echo '[{"id":"i1","title":"nutanix1","vault":{"name":"work"},"category":"LOGIN"}]'
"#,
        )
        .unwrap();
        fs::set_permissions(&path, fs::Permissions::from_mode(0o755)).unwrap();
        path
    }

    /// Writes a fake `op` that answers `item list` and `item get` with canned JSON.
    /// On Windows it is a batch file, which `std::process::Command` runs
    /// through `cmd.exe`.
    #[cfg(windows)]
    fn fake_op(dir: &Path) -> PathBuf {
        let path = dir.join("op.cmd");
        fs::write(
            &path,
            "@echo off\r\n\
             echo %* >> \"%~dp0op.log\"\r\n\
             if \"%1 %2\"==\"item list\" (\r\n\
               echo [{\"id\":\"i1\",\"title\":\"nutanix1\",\"vault\":{\"name\":\"work\"},\"category\":\"LOGIN\",\"additional_information\":\"admin\"},\r\n\
               echo {\"id\":\"i2\",\"title\":\"lab key\",\"vault\":{\"name\":\"work\"},\"category\":\"SSH_KEY\"}]\r\n\
               exit /b 0\r\n\
             )\r\n\
             if \"%1 %2\"==\"item get\" (\r\n\
               echo {\"id\":\"i1\",\"fields\":[{\"id\":\"username\",\"value\":\"admin\"},{\"id\":\"password\",\"value\":\"pw\"}]}\r\n\
               exit /b 0\r\n\
             )\r\n\
             echo unexpected 1>&2\r\n\
             exit /b 1\r\n",
        )
        .unwrap();
        path
    }

    #[cfg(unix)]
    fn fake_op(dir: &Path) -> PathBuf {
        use std::os::unix::fs::PermissionsExt;
        let path = dir.join("op");
        fs::write(
            &path,
            r#"#!/bin/sh
echo "$@" >> "$(dirname "$0")/op.log"
case "$1 $2" in
  "item list")
    echo '[{"id":"i1","title":"nutanix1","vault":{"name":"work"},"category":"LOGIN","additional_information":"admin"},
           {"id":"i2","title":"lab key","vault":{"name":"work"},"category":"SSH_KEY"}]' ;;
  "item get")
    echo '{"id":"i1","fields":[{"id":"username","value":"admin"},{"id":"password","value":"pw"}]}' ;;
  *) echo "unexpected" >&2; exit 1 ;;
esac
"#,
        )
        .unwrap();
        fs::set_permissions(&path, fs::Permissions::from_mode(0o755)).unwrap();
        path
    }

    #[tokio::test]
    async fn one_password_sync_generates_credentials() {
        let (dir, store) = open_temp();
        let op = fake_op(dir.path());
        let provider = store
            .upsert_provider(Provider {
                id: String::new(),
                name: "1Password".into(),
                kind: ProviderKind::OnePassword {
                    op_path: Some(op.to_string_lossy().into()),
                    account: Some("work".into()),
                    vaults: vec!["work".into(), "shared".into()],
                },
                folder: "1Password".into(),
                auto_refresh_minutes: 30,
                last_sync_epoch: None,
                last_status: None,
            })
            .unwrap();

        let result = store.sync_provider(provider.id.clone(), None).await.unwrap();
        // Two vault filters, each answered with two items.
        assert_eq!(result.credentials, 4);
        let creds = store.list_credentials();
        assert_eq!(creds.len(), 4);
        let key = creds.iter().find(|c| c.name == "lab key").unwrap();
        assert!(key.has_private_key);
        assert_eq!(key.folder.as_deref(), Some("1Password/work"));
        assert_eq!(key.source.as_deref(), Some(provider.id.as_str()));
        let login = creds.iter().find(|c| c.name == "nutanix1").unwrap();
        assert_eq!(login.username, "admin");
        assert!(matches!(&login.kind, CredentialKind::OnePassword { item_id, account, .. }
            if item_id == "i1" && account.as_deref() == Some("work")));

        // Windows quotes `--format=json` when it launches the batch file
        // through cmd.exe; the quotes carry no meaning for this check.
        let log = fs::read_to_string(dir.path().join("op.log")).unwrap().replace('"', "");
        assert!(log.contains("item list --format=json --account work --vault work"), "{log}");
        assert!(log.contains("--vault shared"), "{log}");

        // Syncing again replaces rather than duplicates.
        store.sync_provider(provider.id.clone(), None).await.unwrap();
        assert_eq!(store.list_credentials().len(), 4);
        let synced = store.get_provider(provider.id).unwrap();
        assert!(synced.last_sync_epoch.is_some());
        assert_eq!(synced.last_status.as_deref(), Some("4 credentials"));
    }

    #[tokio::test]
    async fn a_sync_lands_where_the_provider_is_by_then() {
        let (dir, store) = open_temp();
        // An `op` that answers only once a file named `go` is next to it,
        // so the provider can change while its sync waits for the answer.
        let op = waiting_op(dir.path());
        let (asked, go) = (dir.path().join("op.log"), dir.path().join("go"));
        let mut provider = sample_provider("op", "Logins", "Work/Logins");
        provider.kind = ProviderKind::OnePassword {
            op_path: Some(op.to_string_lossy().into()),
            account: None,
            vaults: vec![],
        };
        let mut provider = store.upsert_provider(provider).unwrap();
        let start = || {
            let store = store.clone();
            tokio::spawn(async move { store.sync_provider("op".into(), None).await })
        };
        let wait_until_asked = || async {
            while !asked.exists() {
                tokio::time::sleep(std::time::Duration::from_millis(10)).await;
            }
        };

        // Renamed meanwhile: what the sync brings goes to the new folder.
        let sync = start();
        wait_until_asked().await;
        provider.name = "Vault items".into();
        store.upsert_provider(provider).unwrap();
        fs::write(&go, "").unwrap();
        assert_eq!(sync.await.unwrap().unwrap().credentials, 1);
        let folders: Vec<_> = store.list_credentials().into_iter().filter_map(|c| c.folder).collect();
        assert_eq!(folders, vec!["Work/Vault items/work"]);
        assert_eq!(store.get_provider("op".into()).unwrap().last_status.as_deref(), Some("1 credentials"));

        // Deleted meanwhile: nothing is left behind without a provider.
        fs::remove_file(&go).unwrap();
        fs::remove_file(&asked).unwrap();
        let sync = start();
        wait_until_asked().await;
        assert!(store.delete_provider("op".into()).unwrap());
        fs::write(&go, "").unwrap();
        assert!(matches!(sync.await.unwrap(), Err(CoreError::Invalid(_))));
        assert!(store.list_credentials().is_empty());
        let reopened = ConnectionStore::open(store.path.to_string_lossy().into()).unwrap();
        assert!(reopened.list_credentials().is_empty() && reopened.list_providers().is_empty());
    }

    #[test]
    fn folders_are_explicit_and_follow_renames() {
        let (_dir, store) = open_temp();
        assert_eq!(normalize_folder(" ACME / Jump Boxes /"), Some("ACME/Jump Boxes".into()));
        assert_eq!(normalize_folder(" / "), None);

        store.add_folder("ACME/Jump Boxes".into()).unwrap();
        assert_eq!(store.list_folders(), vec!["ACME", "ACME/Jump Boxes"]);

        let mut c = sample("jump01");
        c.folder = Some("ACME/Jump Boxes".into());
        let c = store.upsert(c).unwrap();
        let provider = store
            .upsert_provider(Provider {
                id: "ad".into(),
                name: "ACME-AD".into(),
                kind: ProviderKind::OnePassword { op_path: None, account: None, vaults: vec![] },
                folder: "ACME/ACME-AD".into(),
                auto_refresh_minutes: 0,
                last_sync_epoch: None,
                last_status: None,
            })
            .unwrap();
        let mut generated = sample("dc01");
        generated.folder = Some("ACME/ACME-AD/Domain Controllers".into());
        generated.source = Some(provider.id.clone());
        let generated = store.upsert(generated).unwrap();
        // Generated folders are derived, not explicit.
        assert_eq!(store.list_folders(), vec!["ACME", "ACME/Jump Boxes"]);

        // Rename the top folder: everything underneath follows.
        assert_eq!(store.rename_folder("ACME".into(), "Work".into()).unwrap(), "Work");
        assert_eq!(store.list_folders(), vec!["Work", "Work/Jump Boxes"]);
        assert_eq!(store.get(c.id.clone()).unwrap().folder.as_deref(), Some("Work/Jump Boxes"));
        assert_eq!(store.get_provider("ad".into()).unwrap().folder, "Work/ACME-AD");
        assert_eq!(store.get(generated.id.clone()).unwrap().folder.as_deref(), Some("Work/ACME-AD/Domain Controllers"));
        assert!(matches!(store.rename_folder("Work".into(), "Work/Inner".into()), Err(CoreError::Invalid(_))));

        // Moving: manual items move, generated ones refuse, providers carry their output.
        store.move_connection(c.id.clone(), None).unwrap();
        assert_eq!(store.get(c.id.clone()).unwrap().folder, None);
        assert!(matches!(store.move_connection(generated.id.clone(), None), Err(CoreError::Invalid(_))));
        store.move_provider("ad".into(), Some("Archive".into())).unwrap();
        assert_eq!(store.get_provider("ad".into()).unwrap().folder, "Archive/ACME-AD");
        assert_eq!(store.get(generated.id).unwrap().folder.as_deref(), Some("Archive/ACME-AD/Domain Controllers"));

        // Deleting: only empty folders go.
        assert!(matches!(store.delete_folder("Archive".into()), Err(CoreError::Invalid(_))));
        store.delete_folder("Work/Jump Boxes".into()).unwrap();
        assert_eq!(store.list_folders(), vec!["Archive", "Work"]);
    }

    fn sample_credential(id: &str) -> Credential {
        Credential {
            id: id.into(),
            name: format!("cred {id}"),
            username: "admin".into(),
            kind: CredentialKind::Prompt,
            folder: None,
            has_private_key: false,
            tags: vec![],
            source: None,
        }
    }

    #[test]
    fn folder_credentials_inherit_from_the_nearest_folder() {
        let (_dir, store) = open_temp();
        store.upsert_credential(sample_credential("admin")).unwrap();
        store.upsert_credential(sample_credential("user")).unwrap();
        assert!(matches!(
            store.set_folder_credential("ACME".into(), Some("missing".into())),
            Err(CoreError::Invalid(_))
        ));
        store.set_folder_credential("ACME".into(), Some("admin".into())).unwrap();
        store.set_folder_credential("ACME/Jump Boxes".into(), Some("user".into())).unwrap();

        let nearest = |folder: &str| {
            store
                .inherited_credential(Some(folder.into()))
                .map(|f| (f.folder, f.credential_id))
        };
        assert_eq!(nearest("ACME/Jump Boxes/Deep"), Some(("ACME/Jump Boxes".into(), "user".into())));
        assert_eq!(nearest("ACME/Other"), Some(("ACME".into(), "admin".into())));
        assert_eq!(nearest("Elsewhere"), None);
        assert_eq!(store.inherited_credential(None), None);
        assert_eq!(store.folders_using_credential("admin".into()), vec!["ACME"]);

        // Renames carry the assignment; deleting the credential clears it.
        store.rename_folder("ACME".into(), "Work".into()).unwrap();
        assert_eq!(nearest("Work/Jump Boxes/Deep"), Some(("Work/Jump Boxes".into(), "user".into())));
        store.delete_credential("user".into()).unwrap();
        assert_eq!(nearest("Work/Jump Boxes/Deep"), Some(("Work".into(), "admin".into())));
        assert_eq!(store.list_folder_credentials().len(), 1);

        // Clearing and deleting the folder both drop the assignment.
        store.set_folder_credential("Work".into(), None).unwrap();
        assert_eq!(nearest("Work/Anything"), None);
        store.set_folder_credential("Work/Jump Boxes".into(), Some("admin".into())).unwrap();
        store.delete_folder("Work/Jump Boxes".into()).unwrap();
        assert!(store.list_folder_credentials().is_empty());

        // Survives a reopen.
        store.set_folder_credential("Work".into(), Some("admin".into())).unwrap();
        let reopened = ConnectionStore::open(store.path.to_string_lossy().into()).unwrap();
        assert_eq!(
            reopened.inherited_credential(Some("Work/X".into())).map(|f| f.credential_id),
            Some("admin".into())
        );
    }

    #[test]
    fn file_transfer_kinds_round_trip() {
        let (_dir, store) = open_temp();
        for (kind, port) in [(ConnectionKind::Sftp, 22), (ConnectionKind::Ftp, 21), (ConnectionKind::Ftps, 21)] {
            assert_eq!(kind.default_port(), port);
            let mut c = sample(&format!("{kind:?}"));
            c.kind = kind;
            c.port = port;
            let c = store.upsert(c).unwrap();
            let reopened = ConnectionStore::open(store.path.to_string_lossy().into()).unwrap();
            assert_eq!(reopened.get(c.id).unwrap().kind, kind);
        }
    }

    #[test]
    fn auth_kinds_round_trip_through_json() {
        for auth in [AuthKind::StoredPassword, AuthKind::FolderCredential, AuthKind::Password] {
            let text = serde_json::to_string(&auth).unwrap();
            assert_eq!(serde_json::from_str::<AuthKind>(&text).unwrap(), auth);
        }
        let (_dir, store) = open_temp();
        let mut c = sample("stored");
        c.auth = AuthKind::StoredPassword;
        let c = store.upsert(c).unwrap();
        let reopened = ConnectionStore::open(store.path.to_string_lossy().into()).unwrap();
        assert_eq!(reopened.get(c.id).unwrap().auth, AuthKind::StoredPassword);
    }

    #[tokio::test]
    async fn failed_sync_is_recorded() {
        let (_dir, store) = open_temp();
        let provider = store
            .upsert_provider(Provider {
                id: String::new(),
                name: "broken".into(),
                kind: ProviderKind::OnePassword {
                    op_path: Some("/nonexistent/op".into()),
                    account: None,
                    vaults: vec![],
                },
                folder: "x".into(),
                auto_refresh_minutes: 0,
                last_sync_epoch: None,
                last_status: None,
            })
            .unwrap();
        assert!(store.sync_provider(provider.id.clone(), None).await.is_err());
        let status = store.get_provider(provider.id).unwrap().last_status.unwrap();
        assert!(status.starts_with("failed:"), "{status}");
    }
}
