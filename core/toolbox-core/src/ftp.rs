//! FTP and explicit FTPS (`AUTH TLS`) for File Transfer connections.
//!
//! `FtpSession` mirrors `SftpSession` method for method (and returns the same
//! `SftpEntry` records) so the app's file browser can drive either protocol.

use std::net::SocketAddr;
use std::sync::Arc;
use std::time::{Duration, UNIX_EPOCH};

use suppaftp::list::ListParser;
use suppaftp::tokio::{AsyncFtpStream, AsyncNativeTlsConnector, AsyncNativeTlsFtpStream};
use suppaftp::types::FileType;
use suppaftp::{FtpError, Status};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::sync::Mutex;

use crate::error::CoreError;
use crate::sftp::SftpEntry;

/// Connection settings for one FTP session.
#[derive(Clone, uniffi::Record)]
pub struct FtpConfig {
    pub host: String,
    pub port: u16,
    pub username: String,
    pub password: String,
    /// Upgrade the control and data channels with `AUTH TLS` before login.
    pub secure: bool,
    /// Skip certificate verification without asking (FTPS only).
    pub accept_invalid_certs: bool,
    pub connect_timeout_secs: u32,
}

impl std::fmt::Debug for FtpConfig {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("FtpConfig")
            .field("host", &self.host)
            .field("port", &self.port)
            .field("username", &self.username)
            .field("password", &"<redacted>")
            .field("secure", &self.secure)
            .field("accept_invalid_certs", &self.accept_invalid_certs)
            .finish()
    }
}

/// Implemented on the Swift side; invoked from core threads.
#[uniffi::export(callback_interface)]
pub trait FtpListener: Send + Sync {
    /// The FTPS server's certificate failed verification (`reason` says why).
    /// Return true to continue encrypted but without verifying the server.
    fn confirm_untrusted_certificate(&self, host: String, reason: String) -> bool;
    /// Transfer progress; `total` is 0 when unknown.
    fn on_progress(&self, transfer_id: String, done: u64, total: u64);
}

enum Client {
    Plain(AsyncFtpStream),
    Secure(AsyncNativeTlsFtpStream),
}

/// Runs `$body` with `$c` bound to whichever stream type the session uses.
macro_rules! with_client {
    ($self:ident, $c:ident => $body:expr) => {{
        let mut guard = $self.client.lock().await;
        match guard.as_mut() {
            None => Err(CoreError::Connect("the FTP session is closed".into())),
            Some(Client::Plain($c)) => $body,
            Some(Client::Secure($c)) => $body,
        }
    }};
}

const CHUNK: usize = 64 * 1024;
const PROGRESS_STEP: u64 = 256 * 1024;

#[derive(uniffi::Object)]
pub struct FtpSession {
    client: Mutex<Option<Client>>,
    listener: Arc<dyn FtpListener>,
    home: String,
}

#[uniffi::export(async_runtime = "tokio")]
impl FtpSession {
    /// Connects, upgrades to TLS when asked, logs in and switches to binary.
    #[uniffi::constructor]
    pub async fn connect(config: FtpConfig, listener: Box<dyn FtpListener>) -> Result<Arc<Self>, CoreError> {
        let listener: Arc<dyn FtpListener> = Arc::from(listener);
        let timeout = Duration::from_secs(u64::from(config.connect_timeout_secs.max(1)));
        let addr = resolve(&config.host, config.port).await?;

        let mut client = if config.secure {
            let attempt = connect_secure(addr, &config.host, timeout, config.accept_invalid_certs).await;
            match attempt {
                Ok(stream) => Client::Secure(stream),
                Err(FtpError::SecureError(reason)) if !config.accept_invalid_certs => {
                    let host = config.host.clone();
                    let ask = listener.clone();
                    let reason_for_prompt = reason.clone();
                    let accepted = tokio::task::spawn_blocking(move || ask.confirm_untrusted_certificate(host, reason_for_prompt))
                        .await
                        .unwrap_or(false);
                    if !accepted {
                        return Err(CoreError::HostKey(format!("the server's certificate was not trusted: {reason}")));
                    }
                    Client::Secure(connect_secure(addr, &config.host, timeout, true).await.map_err(ftp_err)?)
                }
                Err(e) => return Err(ftp_err(e)),
            }
        } else {
            let stream = AsyncFtpStream::connect_timeout(addr, timeout).await.map_err(ftp_err)?;
            Client::Plain(stream)
        };

        let home = {
            let result: Result<String, FtpError> = async {
                match &mut client {
                    Client::Plain(c) => {
                        c.set_passive_nat_workaround(true);
                        c.login(config.username.as_str(), config.password.as_str()).await?;
                        c.transfer_type(FileType::Binary).await?;
                        c.pwd().await
                    }
                    Client::Secure(c) => {
                        c.set_passive_nat_workaround(true);
                        c.login(config.username.as_str(), config.password.as_str()).await?;
                        c.transfer_type(FileType::Binary).await?;
                        c.pwd().await
                    }
                }
            }
            .await;
            match result {
                Ok(home) => home,
                Err(FtpError::UnexpectedResponse(r)) if r.status == Status::NotLoggedIn => {
                    return Err(CoreError::Auth(format!("FTP login as {} failed: {}", config.username, reply(&r))));
                }
                Err(e) => return Err(ftp_err(e)),
            }
        };

        Ok(Arc::new(Self {
            client: Mutex::new(Some(client)),
            listener,
            home: if home.is_empty() { "/".into() } else { home },
        }))
    }

    /// The login directory, as an absolute path.
    pub async fn home(&self) -> Result<String, CoreError> {
        Ok(self.home.clone())
    }

    /// Resolves `path` on the server by changing to it.
    pub async fn canonicalize(&self, path: String) -> Result<String, CoreError> {
        with_client!(self, c => {
            c.cwd(path.as_str()).await.map_err(ftp_err)?;
            c.pwd().await.map_err(ftp_err)
        })
    }

    /// Directory listing, directories first, then by name. Uses MLSD and
    /// falls back to LIST (Unix or DOS style) for servers without it.
    pub async fn list(&self, path: String) -> Result<Vec<SftpEntry>, CoreError> {
        let files = with_client!(self, c => {
            match c.mlsd(Some(path.as_str())).await {
                Ok(lines) => Ok(lines.iter().filter_map(|l| ListParser::parse_mlsd(l).ok()).collect::<Vec<_>>()),
                Err(FtpError::UnexpectedResponse(_)) => {
                    let lines = c.list(Some(path.as_str())).await.map_err(ftp_err)?;
                    Ok(lines
                        .iter()
                        .filter_map(|l| ListParser::parse_posix(l).or_else(|_| ListParser::parse_dos(l)).ok())
                        .collect())
                }
                Err(e) => Err(ftp_err(e)),
            }
        })?;
        let base = path.trim_end_matches('/');
        let mut entries: Vec<SftpEntry> = files
            .into_iter()
            .filter(|f| {
                // Same guard as SFTP: names must not steer a download elsewhere.
                let n = f.name();
                !n.is_empty() && n != "." && n != ".." && !n.contains('/') && !n.contains('\0')
            })
            .map(|f| {
                let name = f.name().to_string();
                SftpEntry {
                    path: if base.is_empty() { format!("/{name}") } else { format!("{base}/{name}") },
                    is_dir: f.is_directory(),
                    is_symlink: f.is_symlink(),
                    size: f.size() as u64,
                    modified_epoch: f.modified().duration_since(UNIX_EPOCH).ok().map(|d| d.as_secs()).filter(|s| *s > 0),
                    permissions: None,
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
        let mut file = tokio::fs::File::create(&local).await?;
        let listener = self.listener.clone();
        let written = with_client!(self, c => {
            let total = c.size(remote.as_str()).await.map(|s| s as u64).unwrap_or(0);
            let mut stream = c.retr_as_stream(remote.as_str()).await.map_err(ftp_err)?;
            let mut buf = vec![0u8; CHUNK];
            let mut done = 0u64;
            let mut reported = 0u64;
            loop {
                let n = stream.read(&mut buf).await?;
                if n == 0 {
                    break;
                }
                file.write_all(&buf[..n]).await?;
                done += n as u64;
                if done - reported >= PROGRESS_STEP {
                    reported = done;
                    listener.on_progress(transfer_id.clone(), done, total);
                }
            }
            stream.finish().await.map_err(ftp_err)?;
            listener.on_progress(transfer_id.clone(), done, total.max(done));
            Ok::<u64, CoreError>(done)
        })?;
        file.flush().await?;
        Ok(written)
    }

    /// Copies a local file to a remote path. Returns the bytes sent.
    pub async fn upload(&self, local: String, remote: String, transfer_id: String) -> Result<u64, CoreError> {
        let mut file = tokio::fs::File::open(&local).await?;
        let total = file.metadata().await.map(|m| m.len()).unwrap_or(0);
        let listener = self.listener.clone();
        with_client!(self, c => {
            let mut stream = c.put_with_stream(remote.as_str()).await.map_err(ftp_err)?;
            let mut buf = vec![0u8; CHUNK];
            let mut done = 0u64;
            let mut reported = 0u64;
            loop {
                let n = file.read(&mut buf).await?;
                if n == 0 {
                    break;
                }
                stream.write_all(&buf[..n]).await?;
                done += n as u64;
                if done - reported >= PROGRESS_STEP {
                    reported = done;
                    listener.on_progress(transfer_id.clone(), done, total);
                }
            }
            stream.flush().await?;
            stream.finish().await.map_err(ftp_err)?;
            listener.on_progress(transfer_id.clone(), done, total.max(done));
            Ok(done)
        })
    }

    pub async fn make_dir(&self, path: String) -> Result<(), CoreError> {
        with_client!(self, c => c.mkdir(path.as_str()).await.map_err(ftp_err))
    }

    pub async fn rename(&self, from: String, to: String) -> Result<(), CoreError> {
        with_client!(self, c => c.rename(from.as_str(), to.as_str()).await.map_err(ftp_err))
    }

    /// Removes a file, or an empty directory when `directory` is true.
    pub async fn remove(&self, path: String, directory: bool) -> Result<(), CoreError> {
        with_client!(self, c => {
            if directory {
                c.rmdir(path.as_str()).await.map_err(ftp_err)
            } else {
                c.rm(path.as_str()).await.map_err(ftp_err)
            }
        })
    }

    /// Says goodbye and drops the connection.
    pub async fn close(&self) {
        let client = self.client.lock().await.take();
        match client {
            Some(Client::Plain(mut c)) => {
                let _ = c.quit().await;
            }
            Some(Client::Secure(mut c)) => {
                let _ = c.quit().await;
            }
            None => {}
        }
    }
}

async fn resolve(host: &str, port: u16) -> Result<SocketAddr, CoreError> {
    tokio::net::lookup_host((host, port))
        .await
        .map_err(|e| CoreError::Connect(format!("could not resolve {host}: {e}")))?
        .next()
        .ok_or_else(|| CoreError::Connect(format!("{host} has no address")))
}

async fn connect_secure(
    addr: SocketAddr,
    host: &str,
    timeout: Duration,
    accept_invalid: bool,
) -> Result<AsyncNativeTlsFtpStream, FtpError> {
    let stream = AsyncNativeTlsFtpStream::connect_timeout(addr, timeout).await?;
    let connector = async_native_tls::TlsConnector::new()
        .danger_accept_invalid_certs(accept_invalid)
        .danger_accept_invalid_hostnames(accept_invalid);
    stream.into_secure(AsyncNativeTlsConnector::from(connector), host).await
}

fn reply(r: &suppaftp::types::Response) -> String {
    r.as_string().unwrap_or_default().trim().to_string()
}

fn ftp_err(e: FtpError) -> CoreError {
    match e {
        FtpError::ConnectionError(io) => CoreError::Connect(io.to_string()),
        FtpError::SecureError(s) => CoreError::Connect(format!("TLS: {s}")),
        FtpError::UnexpectedResponse(r) if r.status == Status::NotLoggedIn => CoreError::Auth(reply(&r)),
        FtpError::UnexpectedResponse(r) => CoreError::Io(format!("server said: {}", reply(&r))),
        other => CoreError::Io(other.to_string()),
    }
}
