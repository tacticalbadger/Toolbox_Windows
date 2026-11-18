//! Local encrypted secret storage.
//!
//! Secrets are encrypted one by one with XChaCha20-Poly1305 under a 256-bit
//! key the app keeps in the macOS Keychain. The core only ever sees the key
//! while unlocked and zeroizes it on lock. The file is JSON so it stays easy
//! to back up and, later, to sync.

use std::collections::BTreeMap;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};

use base64::engine::general_purpose::STANDARD as BASE64;
use base64::Engine;
use chacha20poly1305::aead::{Aead, KeyInit, Payload};
use chacha20poly1305::{Key, XChaCha20Poly1305, XNonce};
use serde::{Deserialize, Serialize};
use zeroize::Zeroizing;

use crate::error::{lock_or_recover, CoreError};
use crate::store::restrict_permissions;

const VAULT_VERSION: u32 = 1;
const KEY_LEN: usize = 32;
const NONCE_LEN: usize = 24;
/// Entry whose plaintext is known, used to verify a key before accepting it.
const CANARY_ID: &str = "__canary";
const CANARY_TEXT: &str = "toolbox-vault-ok";

/// What a credential can hold. Any field may be absent.
#[derive(Clone, Default, PartialEq, Eq, Serialize, Deserialize, uniffi::Record)]
pub struct Secret {
    pub password: Option<String>,
    /// Private key file content (OpenSSH, PEM or PuTTY format).
    pub private_key: Option<String>,
    pub passphrase: Option<String>,
}

impl std::fmt::Debug for Secret {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Secret")
            .field("password", &self.password.as_ref().map(|_| "<redacted>"))
            .field("private_key", &self.private_key.as_ref().map(|_| "<redacted>"))
            .field("passphrase", &self.passphrase.as_ref().map(|_| "<redacted>"))
            .finish()
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
struct Entry {
    nonce: String,
    data: String,
}

#[derive(Debug, Default, Serialize, Deserialize)]
struct Document {
    version: u32,
    #[serde(default)]
    entries: BTreeMap<String, Entry>,
}

#[derive(uniffi::Object)]
pub struct Vault {
    path: PathBuf,
    doc: Mutex<Document>,
    key: Mutex<Option<Zeroizing<Vec<u8>>>>,
}

#[uniffi::export]
impl Vault {
    /// Opens (or prepares to create) the vault file at `path`.
    #[uniffi::constructor]
    pub fn open(path: String) -> Result<Arc<Self>, CoreError> {
        let path = PathBuf::from(path);
        let doc = if path.exists() {
            let text = fs::read_to_string(&path)?;
            if text.trim().is_empty() {
                Document::default()
            } else {
                serde_json::from_str(&text)?
            }
        } else {
            Document::default()
        };
        Ok(Arc::new(Self {
            path,
            doc: Mutex::new(doc),
            key: Mutex::new(None),
        }))
    }

    pub fn is_unlocked(&self) -> bool {
        lock_or_recover(&self.key).is_some()
    }

    /// Number of stored secrets (excluding the internal check entry).
    pub fn count(&self) -> u32 {
        self.doc
            .lock()
            .unwrap()
            .entries
            .keys()
            .filter(|k| k.as_str() != CANARY_ID)
            .count() as u32
    }

    /// Accepts a 32-byte key. A fresh vault records a check value under it;
    /// an existing vault refuses a key that does not decrypt that value.
    pub fn unlock(&self, key: Vec<u8>) -> Result<(), CoreError> {
        if key.len() != KEY_LEN {
            return Err(CoreError::Invalid(format!("vault key must be {KEY_LEN} bytes")));
        }
        let key = Zeroizing::new(key);
        let has_canary = lock_or_recover(&self.doc).entries.contains_key(CANARY_ID);
        if has_canary {
            let entry = lock_or_recover(&self.doc).entries[CANARY_ID].clone();
            let plain = decrypt(&key, CANARY_ID, &entry)
                .map_err(|_| CoreError::Auth("wrong vault key".into()))?;
            if plain != CANARY_TEXT.as_bytes() {
                return Err(CoreError::Auth("wrong vault key".into()));
            }
        } else {
            let entry = encrypt(&key, CANARY_ID, CANARY_TEXT.as_bytes())?;
            let mut doc = lock_or_recover(&self.doc);
            doc.entries.insert(CANARY_ID.to_string(), entry);
            write_document(&self.path, &doc)?;
        }
        *lock_or_recover(&self.key) = Some(key);
        Ok(())
    }

    pub fn lock(&self) {
        *lock_or_recover(&self.key) = None;
    }

    pub fn has_secret(&self, id: String) -> bool {
        id != CANARY_ID && lock_or_recover(&self.doc).entries.contains_key(&id)
    }

    pub fn set_secret(&self, id: String, secret: Secret) -> Result<(), CoreError> {
        if id.is_empty() || id == CANARY_ID {
            return Err(CoreError::Invalid("invalid secret id".into()));
        }
        let key = self.current_key()?;
        let plain = Zeroizing::new(serde_json::to_vec(&secret)?);
        let entry = encrypt(&key, &id, &plain)?;
        let mut doc = lock_or_recover(&self.doc);
        doc.entries.insert(id, entry);
        write_document(&self.path, &doc)
    }

    pub fn get_secret(&self, id: String) -> Result<Secret, CoreError> {
        let key = self.current_key()?;
        let entry = self
            .doc
            .lock()
            .unwrap()
            .entries
            .get(&id)
            .cloned()
            .ok_or_else(|| CoreError::Store(format!("no secret stored for {id}")))?;
        let plain = Zeroizing::new(decrypt(&key, &id, &entry)?);
        Ok(serde_json::from_slice(&plain)?)
    }

    pub fn delete_secret(&self, id: String) -> Result<bool, CoreError> {
        let mut doc = lock_or_recover(&self.doc);
        let removed = doc.entries.remove(&id).is_some();
        if removed {
            write_document(&self.path, &doc)?;
        }
        Ok(removed)
    }
}

impl Vault {
    fn current_key(&self) -> Result<Zeroizing<Vec<u8>>, CoreError> {
        self.key
            .lock()
            .unwrap()
            .clone()
            .ok_or_else(|| CoreError::Auth("the vault is locked".into()))
    }
}

/// 32 random bytes for a new vault, generated once and kept in the Keychain.
#[uniffi::export]
pub fn generate_vault_key() -> Vec<u8> {
    rand::random::<[u8; KEY_LEN]>().to_vec()
}

fn encrypt(key: &[u8], id: &str, plain: &[u8]) -> Result<Entry, CoreError> {
    let cipher = XChaCha20Poly1305::new(Key::from_slice(key));
    let nonce: [u8; NONCE_LEN] = rand::random();
    let data = cipher
        .encrypt(XNonce::from_slice(&nonce), Payload { msg: plain, aad: id.as_bytes() })
        .map_err(|_| CoreError::Store("encryption failed".into()))?;
    Ok(Entry {
        nonce: BASE64.encode(nonce),
        data: BASE64.encode(data),
    })
}

fn decrypt(key: &[u8], id: &str, entry: &Entry) -> Result<Vec<u8>, CoreError> {
    let cipher = XChaCha20Poly1305::new(Key::from_slice(key));
    let nonce = BASE64
        .decode(&entry.nonce)
        .map_err(|e| CoreError::Store(format!("corrupt vault entry: {e}")))?;
    let data = BASE64
        .decode(&entry.data)
        .map_err(|e| CoreError::Store(format!("corrupt vault entry: {e}")))?;
    if nonce.len() != NONCE_LEN {
        return Err(CoreError::Store("corrupt vault entry: bad nonce".into()));
    }
    cipher
        .decrypt(XNonce::from_slice(&nonce), Payload { msg: &data, aad: id.as_bytes() })
        .map_err(|_| CoreError::Auth("vault entry could not be decrypted with this key".into()))
}

fn write_document(path: &Path, doc: &Document) -> Result<(), CoreError> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent)?;
    }
    let out = Document {
        version: VAULT_VERSION,
        entries: doc.entries.clone(),
    };
    let tmp = path.with_extension("json.tmp");
    fs::write(&tmp, serde_json::to_string_pretty(&out)?)?;
    restrict_permissions(&tmp)?;
    fs::rename(&tmp, path)?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn secret() -> Secret {
        Secret {
            password: Some("hunter2".into()),
            private_key: None,
            passphrase: None,
        }
    }

    #[test]
    fn roundtrip_and_persist() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("vault.json");
        let key = generate_vault_key();

        let vault = Vault::open(path.to_string_lossy().into()).unwrap();
        assert!(!vault.is_unlocked());
        assert!(matches!(vault.set_secret("a".into(), secret()), Err(CoreError::Auth(_))));
        vault.unlock(key.clone()).unwrap();
        vault.set_secret("a".into(), secret()).unwrap();
        assert_eq!(vault.get_secret("a".into()).unwrap(), secret());
        assert_eq!(vault.count(), 1);
        assert!(vault.has_secret("a".into()));
        assert!(!vault.has_secret(CANARY_ID.into()));

        let reopened = Vault::open(path.to_string_lossy().into()).unwrap();
        reopened.unlock(key).unwrap();
        assert_eq!(reopened.get_secret("a".into()).unwrap(), secret());
        assert!(reopened.delete_secret("a".into()).unwrap());
        assert!(!reopened.delete_secret("a".into()).unwrap());
    }

    #[test]
    fn wrong_key_is_rejected() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("vault.json");
        let vault = Vault::open(path.to_string_lossy().into()).unwrap();
        vault.unlock(generate_vault_key()).unwrap();
        vault.set_secret("a".into(), secret()).unwrap();

        let other = Vault::open(path.to_string_lossy().into()).unwrap();
        assert!(matches!(other.unlock(generate_vault_key()), Err(CoreError::Auth(_))));
        assert!(!other.is_unlocked());
        assert!(matches!(other.unlock(vec![1, 2, 3]), Err(CoreError::Invalid(_))));
    }

    #[test]
    fn ciphertext_is_bound_to_the_id() {
        let key = generate_vault_key();
        let entry = encrypt(&key, "one", b"text").unwrap();
        assert!(decrypt(&key, "two", &entry).is_err());
        assert_eq!(decrypt(&key, "one", &entry).unwrap(), b"text");
    }

    #[test]
    fn lock_forgets_the_key() {
        let dir = tempfile::tempdir().unwrap();
        let vault = Vault::open(dir.path().join("v.json").to_string_lossy().into()).unwrap();
        vault.unlock(generate_vault_key()).unwrap();
        vault.lock();
        assert!(!vault.is_unlocked());
        assert!(matches!(vault.get_secret("x".into()), Err(CoreError::Auth(_))));
    }
}
