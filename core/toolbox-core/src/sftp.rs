//! SFTP file browsing and transfers over the same SSH connection setup
//! the terminal uses.

use std::path::Path;
use std::sync::{Arc, Mutex};

use russh::client;
use russh_sftp::client::SftpSession as RawSftp;
use tokio::io::{AsyncReadExt, AsyncWriteExt};

use crate::error::{lock_or_recover, CoreError};
use crate::ssh::{map_russh_error, open_handle, Handler, HostKeyPrompt, SshConfig};

#[derive(Debug, Clone, PartialEq, Eq, uniffi::Record)]
pub struct SftpEntry {
    pub name: String,
    pub path: String,
    pub is_dir: bool,
    pub is_symlink: bool,
    pub size: u64,
    pub modified_epoch: Option<u64>,
    pub permissions: Option<u32>,
}

/// Implemented on the Swift side; invoked from core threads.
#[uniffi::export(callback_interface)]
pub trait SftpListener: Send + Sync {
    fn confirm_host_key(&self, host: String, port: u16, key_type: String, fingerprint: String) -> bool;
    /// Transfer progress; `total` is 0 when unknown.
    fn on_progress(&self, transfer_id: String, done: u64, total: u64);
}

struct SftpPrompt {
    listener: Arc<dyn SftpListener>,
    last_error: Mutex<Option<String>>,
}

impl HostKeyPrompt for SftpPrompt {
    fn confirm_host_key(&self, host: String, port: u16, key_type: String, fingerprint: String) -> bool {
        self.listener.confirm_host_key(host, port, key_type, fingerprint)
    }

    fn report_error(&self, message: String) {
        *lock_or_recover(&self.last_error) = Some(message);
    }
}

const CHUNK: usize = 64 * 1024;
/// Progress callbacks are rate-limited to one per this many bytes.
const PROGRESS_STEP: u64 = 256 * 1024;

#[derive(uniffi::Object)]
pub struct SftpSession {
    sftp: RawSftp,
    _handle: client::Handle<Handler>,
    listener: Arc<dyn SftpListener>,
}

#[uniffi::export(async_runtime = "tokio")]
impl SftpSession {
    /// Connects, authenticates and opens the SFTP subsystem.
    #[uniffi::constructor]
    pub async fn connect(config: SshConfig, listener: Box<dyn SftpListener>) -> Result<Arc<Self>, CoreError> {
        let listener: Arc<dyn SftpListener> = Arc::from(listener);
        let prompt = Arc::new(SftpPrompt {
            listener: listener.clone(),
            last_error: Mutex::new(None),
        });
        let handle = match open_handle(&config, prompt.clone()).await {
            Ok(handle) => handle,
            Err(CoreError::HostKey(default)) => {
                let detail = lock_or_recover(&prompt.last_error).take();
                return Err(CoreError::HostKey(detail.unwrap_or(default)));
            }
            Err(e) => return Err(e),
        };
        let channel = handle.channel_open_session().await.map_err(map_russh_error)?;
        channel
            .request_subsystem(true, "sftp")
            .await
            .map_err(|e| CoreError::Connect(format!("the server refused the SFTP subsystem: {e}")))?;
        let sftp = RawSftp::new(channel.into_stream())
            .await
            .map_err(|e| CoreError::Connect(format!("SFTP handshake failed: {e}")))?;
        Ok(Arc::new(Self {
            sftp,
            _handle: handle,
            listener,
        }))
    }

    /// The login directory, as an absolute path.
    pub async fn home(&self) -> Result<String, CoreError> {
        self.sftp.canonicalize(".").await.map_err(sftp_err)
    }

    pub async fn canonicalize(&self, path: String) -> Result<String, CoreError> {
        self.sftp.canonicalize(path).await.map_err(sftp_err)
    }

    /// Directory listing, directories first, then by name.
    pub async fn list(&self, path: String) -> Result<Vec<SftpEntry>, CoreError> {
        let dir = self.sftp.read_dir(path.clone()).await.map_err(sftp_err)?;
        let base = path.trim_end_matches('/');
        // A hostile server could answer with names like "../x" to steer a
        // download outside the folder the user picked; such entries are dropped.
        let mut entries: Vec<SftpEntry> = dir
            .filter(|e| {
                let n = e.file_name();
                !n.is_empty() && n != "." && n != ".." && !n.contains('/') && !n.contains('\0')
            })
            .map(|entry| {
                let name = entry.file_name();
                let meta = entry.metadata();
                SftpEntry {
                    path: if base.is_empty() { format!("/{name}") } else { format!("{base}/{name}") },
                    is_dir: meta.is_dir(),
                    is_symlink: meta.is_symlink(),
                    size: meta.size.unwrap_or(0),
                    modified_epoch: meta.mtime.map(u64::from),
                    permissions: meta.permissions,
                    name,
                }
            })
            .collect();
        entries.sort_by(|a, b| {
            b.is_dir
                .cmp(&a.is_dir)
                .then_with(|| a.name.to_lowercase().cmp(&b.name.to_lowercase()))
        });
        Ok(entries)
    }

    /// Copies a remote file to a local path. Returns the bytes written.
    pub async fn download(&self, remote: String, local: String, transfer_id: String) -> Result<u64, CoreError> {
        let mut source = self.sftp.open(remote.clone()).await.map_err(sftp_err)?;
        let total = source.metadata().await.map(|m| m.size.unwrap_or(0)).unwrap_or(0);
        let mut target = tokio::fs::File::create(Path::new(&local))
            .await
            .map_err(|e| CoreError::Io(format!("cannot create {local}: {e}")))?;
        let mut buf = vec![0u8; CHUNK];
        let mut done = 0u64;
        let mut last_report = 0u64;
        loop {
            let n = source.read(&mut buf).await.map_err(|e| CoreError::Io(format!("read {remote}: {e}")))?;
            if n == 0 {
                break;
            }
            target
                .write_all(&buf[..n])
                .await
                .map_err(|e| CoreError::Io(format!("write {local}: {e}")))?;
            done += n as u64;
            if done - last_report >= PROGRESS_STEP {
                last_report = done;
                self.listener.on_progress(transfer_id.clone(), done, total);
            }
        }
        target.flush().await.map_err(|e| CoreError::Io(e.to_string()))?;
        self.listener.on_progress(transfer_id, done, total.max(done));
        Ok(done)
    }

    /// Copies a local file to a remote path, replacing it. Returns the bytes written.
    pub async fn upload(&self, local: String, remote: String, transfer_id: String) -> Result<u64, CoreError> {
        let mut source = tokio::fs::File::open(Path::new(&local))
            .await
            .map_err(|e| CoreError::Io(format!("cannot open {local}: {e}")))?;
        let total = source.metadata().await.map(|m| m.len()).unwrap_or(0);
        let mut target = self.sftp.create(remote.clone()).await.map_err(sftp_err)?;
        let mut buf = vec![0u8; CHUNK];
        let mut done = 0u64;
        let mut last_report = 0u64;
        loop {
            let n = source.read(&mut buf).await.map_err(|e| CoreError::Io(format!("read {local}: {e}")))?;
            if n == 0 {
                break;
            }
            target
                .write_all(&buf[..n])
                .await
                .map_err(|e| CoreError::Io(format!("write {remote}: {e}")))?;
            done += n as u64;
            if done - last_report >= PROGRESS_STEP {
                last_report = done;
                self.listener.on_progress(transfer_id.clone(), done, total);
            }
        }
        target.shutdown().await.map_err(|e| CoreError::Io(format!("finish {remote}: {e}")))?;
        self.listener.on_progress(transfer_id, done, total.max(done));
        Ok(done)
    }

    pub async fn make_dir(&self, path: String) -> Result<(), CoreError> {
        self.sftp.create_dir(path).await.map_err(sftp_err)
    }

    pub async fn rename(&self, from: String, to: String) -> Result<(), CoreError> {
        self.sftp.rename(from, to).await.map_err(sftp_err)
    }

    /// Removes a file, or an empty directory when `directory` is set.
    pub async fn remove(&self, path: String, directory: bool) -> Result<(), CoreError> {
        if directory {
            self.sftp.remove_dir(path).await.map_err(sftp_err)
        } else {
            self.sftp.remove_file(path).await.map_err(sftp_err)
        }
    }

    pub async fn close(&self) {
        let _ = self.sftp.close().await;
    }
}

fn sftp_err(e: russh_sftp::client::error::Error) -> CoreError {
    CoreError::Io(format!("SFTP: {e}"))
}
