//! 1Password through the `op` CLI.
//!
//! The CLI's desktop-app integration handles sign-in and Touch ID, so the
//! core never sees an account token. Listing is used by the provider to
//! create credential records; `get_item` fetches the actual secret at the
//! moment a connection is opened.

use std::path::{Path, PathBuf};

use serde::Deserialize;
use tokio::process::Command;

use crate::error::CoreError;

/// One entry from `op item list`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct OpItem {
    pub id: String,
    pub title: String,
    pub vault: Option<String>,
    pub category: Option<String>,
    /// `additional_information`, which is the username for Login items.
    pub username: Option<String>,
    pub url: Option<String>,
}

/// The parts of an item a connection can use.
#[derive(Clone, Default, PartialEq, Eq, uniffi::Record)]
pub struct ResolvedCredential {
    pub username: Option<String>,
    pub password: Option<String>,
    pub private_key: Option<String>,
}

impl std::fmt::Debug for ResolvedCredential {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ResolvedCredential")
            .field("username", &self.username)
            .field("password", &self.password.as_ref().map(|_| "<redacted>"))
            .field("private_key", &self.private_key.as_ref().map(|_| "<redacted>"))
            .finish()
    }
}

#[cfg(windows)]
const OP_BINARY: &str = "op.exe";
#[cfg(not(windows))]
const OP_BINARY: &str = "op";

/// Where the 1Password CLI lands when installed outside `PATH`: winget's
/// link folder and the MSI's folder on Windows, Homebrew on macOS.
fn candidate_paths() -> Vec<PathBuf> {
    if cfg!(windows) {
        let mut out = Vec::new();
        if let Some(local) = std::env::var_os("LOCALAPPDATA") {
            out.push(Path::new(&local).join(r"Microsoft\WinGet\Links").join(OP_BINARY));
        }
        if let Some(pf) = std::env::var_os("ProgramFiles") {
            out.push(Path::new(&pf).join("1Password CLI").join(OP_BINARY));
        }
        out
    } else {
        vec![PathBuf::from("/opt/homebrew/bin/op"), PathBuf::from("/usr/local/bin/op")]
    }
}

/// The `op` binary to use: an explicit path, whatever is on `PATH`, or a
/// known install location.
pub fn find_op_path(explicit: Option<&str>) -> Option<PathBuf> {
    if let Some(p) = explicit.map(str::trim).filter(|p| !p.is_empty()) {
        return Some(PathBuf::from(p));
    }
    if let Some(path_var) = std::env::var_os("PATH") {
        if let Some(found) = std::env::split_paths(&path_var)
            .map(|dir| dir.join(OP_BINARY))
            .find(|p| p.is_file())
        {
            return Some(found);
        }
    }
    candidate_paths().into_iter().find(|p| p.is_file())
}

#[uniffi::export]
pub fn detect_op_path() -> Option<String> {
    find_op_path(None).map(|p| p.to_string_lossy().into_owned())
}

pub async fn list_items(
    op_path: &Path,
    account: Option<&str>,
    vaults: &[String],
) -> Result<Vec<OpItem>, CoreError> {
    let filters: Vec<&str> = vaults
        .iter()
        .flat_map(|v| v.split(','))
        .map(str::trim)
        .filter(|v| !v.is_empty())
        .collect();
    let mut items = Vec::new();
    if filters.is_empty() {
        items.extend(list_vault(op_path, account, None).await?);
    } else {
        for vault in filters {
            items.extend(list_vault(op_path, account, Some(vault)).await?);
        }
    }
    Ok(items)
}

async fn list_vault(op_path: &Path, account: Option<&str>, vault: Option<&str>) -> Result<Vec<OpItem>, CoreError> {
    let mut args: Vec<String> = vec!["item".into(), "list".into(), "--format=json".into()];
    if let Some(account) = account.map(str::trim).filter(|a| !a.is_empty()) {
        args.push("--account".into());
        args.push(account.into());
    }
    if let Some(vault) = vault {
        args.push("--vault".into());
        args.push(vault.into());
    }
    let json = run_op(op_path, &args).await?;
    parse_item_list(&json)
}

pub async fn get_item(op_path: &Path, account: Option<&str>, item_id: &str) -> Result<ResolvedCredential, CoreError> {
    let mut args: Vec<String> = vec!["item".into(), "get".into(), item_id.into(), "--format=json".into()];
    if let Some(account) = account.map(str::trim).filter(|a| !a.is_empty()) {
        args.push("--account".into());
        args.push(account.into());
    }
    let json = run_op(op_path, &args).await?;
    extract_secret(&json)
}

/// Fetches username, password and private key for one item.
#[uniffi::export(async_runtime = "tokio")]
pub async fn resolve_one_password_item(
    op_path: Option<String>,
    account: Option<String>,
    item_id: String,
) -> Result<ResolvedCredential, CoreError> {
    let path = find_op_path(op_path.as_deref())
        .ok_or_else(|| CoreError::Invalid("1Password CLI (op) not found; install it or set its path on the provider".into()))?;
    get_item(&path, account.as_deref(), &item_id).await
}

async fn run_op(op_path: &Path, args: &[String]) -> Result<String, CoreError> {
    let output = Command::new(op_path)
        .args(args)
        // Matches the Royal TS script; silences an ObjC runtime warning.
        .env("OBJC_DEBUG_MISSING_POOLS", "NO")
        .output()
        .await
        .map_err(|e| CoreError::Io(format!("could not run {}: {e}", op_path.display())))?;
    if output.status.success() {
        return Ok(String::from_utf8_lossy(&output.stdout).into_owned());
    }
    let stderr = String::from_utf8_lossy(&output.stderr);
    let message = stderr
        .lines()
        .map(str::trim)
        .rfind(|l| !l.is_empty())
        .unwrap_or("unknown error")
        .to_string();
    Err(CoreError::Auth(format!("1Password: {message}")))
}

// ---------- JSON shapes ----------

#[derive(Deserialize)]
struct RawListItem {
    id: String,
    #[serde(default)]
    title: Option<String>,
    #[serde(default)]
    vault: Option<RawVault>,
    #[serde(default)]
    category: Option<String>,
    #[serde(default)]
    additional_information: Option<String>,
    #[serde(default)]
    urls: Vec<RawUrl>,
}

#[derive(Deserialize)]
struct RawVault {
    #[serde(default)]
    name: Option<String>,
}

#[derive(Deserialize)]
struct RawUrl {
    #[serde(default)]
    primary: bool,
    #[serde(default)]
    href: Option<String>,
}

#[derive(Deserialize)]
struct RawItem {
    #[serde(default)]
    fields: Vec<RawField>,
}

#[derive(Deserialize)]
struct RawField {
    #[serde(default)]
    id: Option<String>,
    #[serde(default)]
    label: Option<String>,
    #[serde(default)]
    purpose: Option<String>,
    #[serde(default)]
    value: Option<String>,
    #[serde(default)]
    ssh_formats: Option<RawSshFormats>,
}

#[derive(Deserialize)]
struct RawSshFormats {
    #[serde(default)]
    openssh: Option<RawSshFormat>,
}

#[derive(Deserialize)]
struct RawSshFormat {
    #[serde(default)]
    value: Option<String>,
}

pub fn parse_item_list(json: &str) -> Result<Vec<OpItem>, CoreError> {
    let raw: Vec<RawListItem> =
        serde_json::from_str(json).map_err(|e| CoreError::Store(format!("unexpected op output: {e}")))?;
    Ok(raw
        .into_iter()
        .map(|item| OpItem {
            id: item.id,
            title: item.title.unwrap_or_else(|| "Untitled".into()),
            vault: item.vault.and_then(|v| v.name),
            category: item.category,
            username: item.additional_information.filter(|u| !u.is_empty()),
            url: item
                .urls
                .iter()
                .find(|u| u.primary)
                .or(item.urls.first())
                .and_then(|u| u.href.clone()),
        })
        .collect())
}

/// Mirrors the Royal TS dynamic credential script: username, password and
/// private key by field id, label or purpose. SSH Key items prefer the
/// OpenSSH rendering of the key.
pub fn extract_secret(json: &str) -> Result<ResolvedCredential, CoreError> {
    let raw: RawItem =
        serde_json::from_str(json).map_err(|e| CoreError::Store(format!("unexpected op output: {e}")))?;
    let mut out = ResolvedCredential::default();
    for field in raw.fields {
        let id = field.id.as_deref().unwrap_or("").to_ascii_lowercase();
        let label = field.label.as_deref().unwrap_or("").to_ascii_lowercase();
        let purpose = field.purpose.as_deref().unwrap_or("").to_ascii_uppercase();
        if id == "username" || label == "username" || purpose == "USERNAME" {
            if out.username.is_none() {
                out.username = field.value;
            }
        } else if id == "password" || label == "password" || purpose == "PASSWORD" {
            if out.password.is_none() {
                out.password = field.value;
            }
        } else if id == "private_key" || label == "private key" {
            let openssh = field.ssh_formats.and_then(|f| f.openssh).and_then(|f| f.value);
            out.private_key = openssh.or(field.value);
        }
    }
    Ok(out)
}

#[cfg(test)]
mod tests {
    use super::*;

    const LIST: &str = r#"[
      {"id":"abc","title":"nutanix1","version":2,"vault":{"id":"v1","name":"work"},"category":"LOGIN",
       "additional_information":"admin","urls":[{"label":"website","primary":true,"href":"https://nutanix1.corp.example:9440"}]},
      {"id":"def","title":"lab key","vault":{"id":"v1","name":"work"},"category":"SSH_KEY"},
      {"id":"ghi","vault":{"name":"personal"},"category":"PASSWORD","urls":[{"href":"https://x"}]}
    ]"#;

    #[test]
    fn parses_item_list() {
        let items = parse_item_list(LIST).unwrap();
        assert_eq!(items.len(), 3);
        assert_eq!(items[0].title, "nutanix1");
        assert_eq!(items[0].vault.as_deref(), Some("work"));
        assert_eq!(items[0].username.as_deref(), Some("admin"));
        assert_eq!(items[0].url.as_deref(), Some("https://nutanix1.corp.example:9440"));
        assert_eq!(items[1].category.as_deref(), Some("SSH_KEY"));
        assert_eq!(items[1].username, None);
        assert_eq!(items[2].title, "Untitled");
        assert_eq!(items[2].url.as_deref(), Some("https://x"));
    }

    #[test]
    fn extracts_login_fields() {
        let json = r#"{"id":"abc","fields":[
          {"id":"username","type":"STRING","purpose":"USERNAME","label":"username","value":"root"},
          {"id":"password","type":"CONCEALED","purpose":"PASSWORD","label":"password","value":"s3cret"},
          {"id":"notesPlain","type":"STRING","purpose":"NOTES","label":"notesPlain"}
        ]}"#;
        let s = extract_secret(json).unwrap();
        assert_eq!(s.username.as_deref(), Some("root"));
        assert_eq!(s.password.as_deref(), Some("s3cret"));
        assert_eq!(s.private_key, None);
    }

    #[test]
    fn extracts_ssh_key_preferring_openssh_format() {
        let json = r#"{"id":"def","fields":[
          {"id":"private_key","type":"SSHKEY","label":"private key","value":"-----BEGIN PRIVATE KEY-----\nPKCS8\n",
           "ssh_formats":{"openssh":{"value":"-----BEGIN OPENSSH PRIVATE KEY-----\nOPENSSH\n"}}},
          {"id":"public_key","type":"STRING","label":"public key","value":"ssh-ed25519 AAAA"}
        ]}"#;
        let s = extract_secret(json).unwrap();
        assert!(s.private_key.unwrap().starts_with("-----BEGIN OPENSSH"));
    }

    #[test]
    fn extracts_fields_by_label_when_ids_differ() {
        let json = r#"{"fields":[
          {"id":"custom1","label":"Username","value":"demo"},
          {"id":"custom2","label":"Password","value":"pw"},
          {"id":"custom3","label":"Private Key","value":"KEY"}
        ]}"#;
        let s = extract_secret(json).unwrap();
        assert_eq!(s.username.as_deref(), Some("demo"));
        assert_eq!(s.password.as_deref(), Some("pw"));
        assert_eq!(s.private_key.as_deref(), Some("KEY"));
    }

    #[test]
    fn explicit_op_path_wins() {
        assert_eq!(find_op_path(Some("/x/op")), Some(PathBuf::from("/x/op")));
        assert_eq!(find_op_path(Some("  ")), find_op_path(None));
    }
}
