//! Interactive SSH shell sessions over russh.
//!
//! A session is driven entirely by events: the caller constructs it with a
//! listener, receives `on_connected`, then a stream of `on_data`, and finally
//! `on_closed`. Writes and resizes are queued through the session handle.

use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::time::Duration;

use russh::client::{self, AuthResult};
use russh::keys::agent::client::AgentClient;
use russh::keys::{self, HashAlg, PrivateKeyWithHashAlg, PublicKeyOrCertificate};
use russh::{ChannelMsg, Disconnect, Pty};
use tokio::sync::mpsc;

use crate::error::CoreError;
use crate::runtime::RUNTIME;

/// Credentials for one SSH session. Passwords and passphrases are supplied
/// for exactly this connection attempt and are not retained by the core.
#[derive(Clone, uniffi::Enum)]
pub enum SshAuth {
    Password {
        password: String,
    },
    KeyFile {
        path: String,
        passphrase: Option<String>,
    },
    /// Private key content (OpenSSH, PEM or PuTTY), e.g. from the vault or 1Password.
    KeyData {
        key: String,
        passphrase: Option<String>,
    },
    /// The system agent (`SSH_AUTH_SOCK`) when `socket_path` is `None`, or an
    /// explicit socket such as 1Password's agent.
    Agent {
        socket_path: Option<String>,
    },
}

impl std::fmt::Debug for SshAuth {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            SshAuth::Password { .. } => write!(f, "Password(<redacted>)"),
            SshAuth::KeyFile { path, passphrase } => f
                .debug_struct("KeyFile")
                .field("path", path)
                .field("passphrase", &passphrase.as_ref().map(|_| "<redacted>"))
                .finish(),
            SshAuth::KeyData { passphrase, .. } => f
                .debug_struct("KeyData")
                .field("key", &"<redacted>")
                .field("passphrase", &passphrase.as_ref().map(|_| "<redacted>"))
                .finish(),
            SshAuth::Agent { socket_path } => f.debug_struct("Agent").field("socket_path", socket_path).finish(),
        }
    }
}

#[derive(Debug, Clone, uniffi::Record)]
pub struct SshConfig {
    pub host: String,
    pub port: u16,
    pub username: String,
    pub auth: SshAuth,
    /// `TERM` advertised to the server, e.g. `xterm-256color`.
    pub term: String,
    pub cols: u16,
    pub rows: u16,
    pub connect_timeout_secs: u32,
    /// known_hosts file to check against and learn into.
    /// `None` uses `~/.ssh/known_hosts`, shared with OpenSSH.
    pub known_hosts_path: Option<String>,
}

/// Implemented on the Swift side. Methods are invoked from core threads, never
/// the main thread, so UI work must be dispatched.
#[uniffi::export(callback_interface)]
pub trait SshListener: Send + Sync {
    fn on_connected(&self);
    fn on_data(&self, data: Vec<u8>);
    fn on_error(&self, message: String);
    /// `clean` is true when the shell ended on its own or was closed here,
    /// false after a failure that `on_error` reported.
    fn on_closed(&self, clean: bool);
    /// Called for a host whose key is not yet in known_hosts.
    /// Return `true` to trust it; it is then recorded for next time.
    fn confirm_host_key(&self, host: String, port: u16, key_type: String, fingerprint: String)
        -> bool;
}

enum Cmd {
    Write(Vec<u8>),
    Resize { cols: u16, rows: u16 },
    Close,
}

#[derive(uniffi::Object)]
pub struct SshSession {
    tx: mpsc::UnboundedSender<Cmd>,
    closed: Arc<AtomicBool>,
}

#[uniffi::export]
impl SshSession {
    /// Starts connecting immediately and returns. Progress arrives on `listener`.
    #[uniffi::constructor]
    pub fn connect(config: SshConfig, listener: Box<dyn SshListener>) -> Arc<Self> {
        let (tx, rx) = mpsc::unbounded_channel();
        let closed = Arc::new(AtomicBool::new(false));
        let listener: Arc<dyn SshListener> = Arc::from(listener);
        RUNTIME.spawn(run_session(config, listener, rx, closed.clone()));
        Arc::new(Self { tx, closed })
    }

    /// Sends keyboard input (or pasted bytes) to the remote shell.
    pub fn write(&self, data: Vec<u8>) {
        let _ = self.tx.send(Cmd::Write(data));
    }

    /// Tells the remote pty the terminal size changed.
    pub fn resize(&self, cols: u16, rows: u16) {
        let _ = self.tx.send(Cmd::Resize { cols, rows });
    }

    /// Closes the channel and disconnects. `on_closed` follows.
    pub fn close(&self) {
        let _ = self.tx.send(Cmd::Close);
    }

    pub fn is_closed(&self) -> bool {
        self.closed.load(Ordering::SeqCst)
    }
}

/// Whoever answers host-key questions for a connection attempt: the
/// terminal listener for shells, the SFTP listener for file browsing.
pub(crate) trait HostKeyPrompt: Send + Sync {
    fn confirm_host_key(&self, host: String, port: u16, key_type: String, fingerprint: String) -> bool;
    fn report_error(&self, message: String);
}

struct ListenerPrompt(Arc<dyn SshListener>);

impl HostKeyPrompt for ListenerPrompt {
    fn confirm_host_key(&self, host: String, port: u16, key_type: String, fingerprint: String) -> bool {
        self.0.confirm_host_key(host, port, key_type, fingerprint)
    }

    fn report_error(&self, message: String) {
        self.0.on_error(message)
    }
}

async fn run_session(
    config: SshConfig,
    listener: Arc<dyn SshListener>,
    rx: mpsc::UnboundedReceiver<Cmd>,
    closed: Arc<AtomicBool>,
) {
    let prompt: Arc<dyn HostKeyPrompt> = Arc::new(ListenerPrompt(listener.clone()));
    let clean = match open_shell(&config, prompt).await {
        Ok((handle, channel)) => {
            listener.on_connected();
            pump(handle, channel, listener.clone(), rx).await
        }
        Err(e) => {
            listener.on_error(e.to_string());
            false
        }
    };
    closed.store(true, Ordering::SeqCst);
    listener.on_closed(clean);
}

type Shell = (client::Handle<Handler>, russh::Channel<client::Msg>);

/// Connects and authenticates; shared by shells and SFTP.
pub(crate) async fn open_handle(
    config: &SshConfig,
    prompt: Arc<dyn HostKeyPrompt>,
) -> Result<client::Handle<Handler>, CoreError> {
    if config.host.trim().is_empty() {
        return Err(CoreError::Invalid("host is required".into()));
    }
    if config.username.trim().is_empty() {
        return Err(CoreError::Invalid("username is required".into()));
    }

    let cfg = client::Config {
        inactivity_timeout: None,
        keepalive_interval: Some(Duration::from_secs(30)),
        keepalive_max: 3,
        nodelay: true,
        ..client::Config::default()
    };

    let handler = Handler {
        host: config.host.clone(),
        port: config.port,
        known_hosts: config.known_hosts_path.as_deref().map(expand_tilde),
        prompt,
    };

    let timeout = Duration::from_secs(u64::from(config.connect_timeout_secs.max(1)));
    let connecting = client::connect(Arc::new(cfg), (config.host.as_str(), config.port), handler);
    let mut handle = tokio::time::timeout(timeout, connecting)
        .await
        .map_err(|_| {
            CoreError::Connect(format!(
                "timed out after {}s connecting to {}:{}",
                timeout.as_secs(),
                config.host,
                config.port
            ))
        })?
        .map_err(map_russh_error)?;

    // A server that accepts the TCP connection but never answers the auth
    // exchange must not hang the session forever.
    tokio::time::timeout(AUTH_TIMEOUT, authenticate(&mut handle, config))
        .await
        .map_err(|_| CoreError::Auth(format!("{}:{} did not complete authentication in time", config.host, config.port)))??;
    Ok(handle)
}

const AUTH_TIMEOUT: Duration = Duration::from_secs(60);

async fn open_shell(config: &SshConfig, prompt: Arc<dyn HostKeyPrompt>) -> Result<Shell, CoreError> {
    let handle = open_handle(config, prompt).await?;

    let channel = handle
        .channel_open_session()
        .await
        .map_err(map_russh_error)?;
    let modes = [
        (Pty::ECHO, 1),
        (Pty::TTY_OP_ISPEED, 14400),
        (Pty::TTY_OP_OSPEED, 14400),
    ];
    channel
        .request_pty(
            false,
            &config.term,
            u32::from(config.cols),
            u32::from(config.rows),
            0,
            0,
            &modes,
        )
        .await
        .map_err(map_russh_error)?;
    channel
        .request_shell(false)
        .await
        .map_err(map_russh_error)?;
    Ok((handle, channel))
}

async fn authenticate(handle: &mut client::Handle<Handler>, config: &SshConfig) -> Result<(), CoreError> {
    let user = config.username.as_str();
    let result = match &config.auth {
        SshAuth::Password { password } => handle
            .authenticate_password(user, password.as_str())
            .await
            .map_err(map_russh_error)?,
        SshAuth::KeyFile { path, passphrase } => {
            let path = expand_tilde(path);
            let key = keys::load_secret_key(&path, passphrase.as_deref()).map_err(|e| {
                CoreError::Auth(format!("cannot load key {}: {e}", path.display()))
            })?;
            let hash = handle
                .best_supported_rsa_hash()
                .await
                .map_err(map_russh_error)?
                .flatten();
            handle
                .authenticate_publickey(user, PrivateKeyWithHashAlg::new(Arc::new(key), hash))
                .await
                .map_err(map_russh_error)?
        }
        SshAuth::KeyData { key, passphrase } => {
            let key = keys::decode_secret_key(key, passphrase.as_deref())
                .map_err(|e| CoreError::Auth(format!("cannot read private key: {e}")))?;
            let hash = handle
                .best_supported_rsa_hash()
                .await
                .map_err(map_russh_error)?
                .flatten();
            handle
                .authenticate_publickey(user, PrivateKeyWithHashAlg::new(Arc::new(key), hash))
                .await
                .map_err(map_russh_error)?
        }
        SshAuth::Agent { socket_path } => {
            authenticate_with_agent(handle, user, socket_path.as_deref()).await?
        }
    };

    match result {
        AuthResult::Success => Ok(()),
        AuthResult::Failure {
            remaining_methods,
            partial_success,
        } => {
            let why = if partial_success {
                "the server wants a further method"
            } else {
                "the server rejected it"
            };
            Err(CoreError::Auth(format!(
                "{} for {user}: {why}; accepted methods: {remaining_methods:?}",
                describe_auth(&config.auth)
            )))
        }
    }
}

async fn authenticate_with_agent(
    handle: &mut client::Handle<Handler>,
    user: &str,
    socket_path: Option<&str>,
) -> Result<AuthResult, CoreError> {
    let mut agent = connect_agent(socket_path)
        .await
        .map_err(|e| CoreError::Auth(format!("cannot reach ssh-agent: {e}")))?;

    let identities = agent
        .request_identities()
        .await
        .map_err(|e| CoreError::Auth(format!("ssh-agent: {e}")))?;
    if identities.is_empty() {
        return Err(CoreError::Auth("ssh-agent holds no identities".into()));
    }

    let hash = handle
        .best_supported_rsa_hash()
        .await
        .map_err(map_russh_error)?
        .flatten();

    let mut last = None;
    for identity in identities {
        let key = identity.public_key().into_owned();
        let result = handle
            .authenticate_publickey_with(user, key, hash, &mut agent)
            .await
            .map_err(|e| CoreError::Auth(format!("ssh-agent signing failed: {e}")))?;
        let success = result.success();
        last = Some(result);
        if success {
            break;
        }
    }
    Ok(last.expect("identities is non-empty"))
}

pub(crate) struct Handler {
    host: String,
    port: u16,
    known_hosts: Option<PathBuf>,
    prompt: Arc<dyn HostKeyPrompt>,
}

impl client::Handler for Handler {
    type Error = russh::Error;

    async fn check_server_key(
        &mut self,
        server_key: &PublicKeyOrCertificate,
    ) -> Result<bool, Self::Error> {
        let key = server_key.public_key();
        let check = match &self.known_hosts {
            Some(path) => keys::check_known_hosts_path(&self.host, self.port, &key, path),
            None => keys::check_known_hosts(&self.host, self.port, &key),
        };
        match check {
            Ok(true) => Ok(true),
            Err(keys::Error::KeyChanged { line }) => {
                self.prompt.report_error(format!(
                    "WARNING: the host key for {}:{} has changed (known_hosts line {line}). \
                     Refusing to connect; remove the stale entry if the host was reinstalled.",
                    self.host, self.port
                ));
                Ok(false)
            }
            // Unknown host, or no known_hosts file yet: ask the user.
            Ok(false) | Err(_) => {
                let fingerprint = key.fingerprint(HashAlg::Sha256).to_string();
                let key_type = key.algorithm().to_string();
                let (host, port, prompt) = (self.host.clone(), self.port, self.prompt.clone());
                // The prompt may block on a dialog; keep the worker free.
                let accepted = tokio::task::block_in_place(move || {
                    prompt.confirm_host_key(host, port, key_type, fingerprint)
                });
                if accepted {
                    let learned = match &self.known_hosts {
                        Some(path) => keys::known_hosts::learn_known_hosts_path(
                            &self.host, self.port, &key, path,
                        ),
                        None => keys::known_hosts::learn_known_hosts(&self.host, self.port, &key),
                    };
                    if let Err(e) = learned {
                        log::warn!("could not record host key: {e}");
                    }
                }
                Ok(accepted)
            }
        }
    }
}

/// Runs the shell until it ends. Returns whether it ended cleanly.
async fn pump(
    handle: client::Handle<Handler>,
    mut channel: russh::Channel<client::Msg>,
    listener: Arc<dyn SshListener>,
    mut rx: mpsc::UnboundedReceiver<Cmd>,
) -> bool {
    enum Event {
        Server(Option<ChannelMsg>),
        Client(Option<Cmd>),
    }

    let clean = loop {
        let event = tokio::select! {
            msg = channel.wait() => Event::Server(msg),
            cmd = rx.recv() => Event::Client(cmd),
        };
        match event {
            Event::Server(Some(ChannelMsg::Data { data })) => listener.on_data(data.to_vec()),
            Event::Server(Some(ChannelMsg::ExtendedData { data, .. })) => {
                listener.on_data(data.to_vec())
            }
            Event::Server(Some(ChannelMsg::Close)) | Event::Server(None) => break true,
            Event::Server(Some(_)) => {}
            Event::Client(Some(Cmd::Write(bytes))) => {
                if let Err(e) = channel.data_bytes(bytes).await {
                    listener.on_error(format!("write failed: {e}"));
                    break false;
                }
            }
            Event::Client(Some(Cmd::Resize { cols, rows })) => {
                if let Err(e) = channel
                    .window_change(u32::from(cols), u32::from(rows), 0, 0)
                    .await
                {
                    log::warn!("window change failed: {e}");
                }
            }
            Event::Client(Some(Cmd::Close)) | Event::Client(None) => {
                let _ = channel.eof().await;
                let _ = channel.close().await;
                break true;
            }
        }
    };
    let _ = handle
        .disconnect(Disconnect::ByApplication, "", "en")
        .await;
    clean
}

pub(crate) fn map_russh_error(e: russh::Error) -> CoreError {
    match e {
        russh::Error::UnknownKey => CoreError::HostKey("server host key was not accepted".into()),
        russh::Error::IO(io) => CoreError::Connect(io.to_string()),
        other => CoreError::Connect(other.to_string()),
    }
}

fn describe_auth(auth: &SshAuth) -> String {
    match auth {
        SshAuth::Password { .. } => "password".into(),
        SshAuth::KeyFile { path, .. } => format!("key file {path}"),
        SshAuth::KeyData { .. } => "private key".into(),
        SshAuth::Agent { socket_path: None } => "ssh-agent".into(),
        SshAuth::Agent {
            socket_path: Some(p),
        } => format!("ssh-agent at {p}"),
    }
}

/// Named pipe the Windows OpenSSH agent listens on; 1Password's SSH agent
/// serves the same name, so one default covers both.
#[cfg(windows)]
pub const DEFAULT_AGENT_PIPE: &str = r"\\.\pipe\openssh-ssh-agent";

/// Opens the agent at `socket_path`, or the platform default: `SSH_AUTH_SOCK`
/// on Unix, the OpenSSH named pipe on Windows.
#[cfg(unix)]
async fn connect_agent(socket_path: Option<&str>) -> Result<AgentClient<tokio::net::UnixStream>, russh::keys::Error> {
    match socket_path {
        Some(path) => AgentClient::connect_uds(expand_tilde(path)).await,
        None => AgentClient::connect_env().await,
    }
}

#[cfg(windows)]
async fn connect_agent(
    socket_path: Option<&str>,
) -> Result<AgentClient<tokio::net::windows::named_pipe::NamedPipeClient>, russh::keys::Error> {
    let pipe = socket_path.map(|p| p.to_string()).unwrap_or_else(|| {
        std::env::var("SSH_AUTH_SOCK")
            .ok()
            .filter(|s| s.starts_with(r"\\.\pipe\"))
            .unwrap_or_else(|| DEFAULT_AGENT_PIPE.to_string())
    });
    AgentClient::connect_named_pipe(pipe).await
}

/// `~/x` relative to the home directory (`HOME`, or the profile folder on
/// Windows); other paths unchanged. Windows users may also write `~\x`.
pub(crate) fn expand_tilde(path: &str) -> PathBuf {
    let rest = path
        .strip_prefix("~/")
        .or_else(|| if cfg!(windows) { path.strip_prefix("~\\") } else { None });
    if let Some(rest) = rest {
        if let Some(home) = std::env::home_dir() {
            return home.join(rest);
        }
    }
    PathBuf::from(path)
}
