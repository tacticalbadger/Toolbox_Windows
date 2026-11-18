//! An in-process SSH server for tests and the `echo_server` example: a shell
//! that echoes what you type, and an SFTP subsystem serving a directory.

use std::collections::{HashMap, HashSet};
use std::fs;
use std::net::SocketAddr;
use std::os::unix::fs::FileExt;
use std::path::{Component, Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::{Duration, UNIX_EPOCH};

use russh::keys::{Algorithm, PrivateKey};
use russh::server::{self, Auth, ChannelOpenHandle, Msg, Server as _, Session, VerifiedPublicKey};
use russh::{Channel, ChannelId, Pty};
use russh_sftp::protocol::{
    Attrs, Data, File, FileAttributes, Handle, Name, OpenFlags, Status, StatusCode, Version,
};
use tokio::net::TcpListener;

pub struct TestServerConfig {
    pub username: String,
    pub password: String,
    /// Directory served over SFTP as `/`.
    pub sftp_root: PathBuf,
    /// Host key; a random Ed25519 key when `None`.
    pub host_key: Option<PrivateKey>,
    /// Close every shell this long after it starts, like a remote logout.
    pub shell_exit_after: Option<Duration>,
}

pub struct TestServer {
    pub addr: SocketAddr,
    task: tokio::task::JoinHandle<()>,
}

impl Drop for TestServer {
    fn drop(&mut self) {
        self.task.abort();
    }
}

/// Starts the server on `addr` (port 0 picks a free port).
pub async fn start(addr: SocketAddr, config: TestServerConfig) -> std::io::Result<TestServer> {
    let socket = TcpListener::bind(addr).await?;
    let addr = socket.local_addr()?;
    let key = config
        .host_key
        .unwrap_or_else(|| PrivateKey::random(&mut rand::rng(), Algorithm::Ed25519).expect("host key"));
    let server_config = Arc::new(server::Config {
        keys: vec![key],
        auth_rejection_time: Duration::from_millis(10),
        auth_rejection_time_initial: Some(Duration::ZERO),
        inactivity_timeout: None,
        ..Default::default()
    });
    let mut server = EchoServer {
        username: config.username,
        password: config.password,
        sftp_root: Arc::new(config.sftp_root),
        channels: Arc::new(Mutex::new(HashMap::new())),
        sftp_channels: Arc::new(Mutex::new(HashSet::new())),
        shell_exit_after: config.shell_exit_after,
    };
    let task = tokio::spawn(async move {
        if let Err(e) = server.run_on_socket(server_config, &socket).await {
            eprintln!("ssh test server stopped: {e}");
        }
    });
    Ok(TestServer { addr, task })
}

#[derive(Clone)]
struct EchoServer {
    username: String,
    password: String,
    sftp_root: Arc<PathBuf>,
    channels: Arc<Mutex<HashMap<ChannelId, Channel<Msg>>>>,
    /// Channels running SFTP; russh also hands their data to `data()`, which
    /// must not echo it.
    sftp_channels: Arc<Mutex<HashSet<ChannelId>>>,
    shell_exit_after: Option<Duration>,
}

impl server::Server for EchoServer {
    type Handler = Self;
    fn new_client(&mut self, _: Option<SocketAddr>) -> Self {
        self.clone()
    }
}

impl server::Handler for EchoServer {
    type Error = russh::Error;

    async fn auth_password(&mut self, user: &str, password: &str) -> Result<Auth, Self::Error> {
        Ok(if user == self.username && password == self.password {
            Auth::Accept
        } else {
            Auth::reject()
        })
    }

    async fn auth_publickey(&mut self, user: &str, _key: VerifiedPublicKey<'_>) -> Result<Auth, Self::Error> {
        Ok(if user == self.username { Auth::Accept } else { Auth::reject() })
    }

    async fn channel_open_session(
        &mut self,
        channel: Channel<Msg>,
        reply: ChannelOpenHandle,
        _session: &mut Session,
    ) -> Result<(), Self::Error> {
        self.channels.lock().unwrap().insert(channel.id(), channel);
        reply.accept().await;
        Ok(())
    }

    #[allow(clippy::too_many_arguments)]
    async fn pty_request(
        &mut self,
        channel: ChannelId,
        _term: &str,
        _cols: u32,
        _rows: u32,
        _pix_width: u32,
        _pix_height: u32,
        _modes: &[(Pty, u32)],
        session: &mut Session,
    ) -> Result<(), Self::Error> {
        session.channel_success(channel)?;
        Ok(())
    }

    async fn shell_request(&mut self, channel: ChannelId, session: &mut Session) -> Result<(), Self::Error> {
        session.channel_success(channel)?;
        session.data(
            channel,
            &b"\x1b[1;32mToolbox echo server\x1b[0m\r\nEverything you type comes back. \
               Colors: \x1b[31mred\x1b[0m \x1b[34mblue\x1b[0m \x1b[33myellow\x1b[0m \x1b[35mmagenta\x1b[0m\r\n$ "[..],
        )?;
        if let Some(delay) = self.shell_exit_after {
            let handle = session.handle();
            tokio::spawn(async move {
                tokio::time::sleep(delay).await;
                let _ = handle.data(channel, b"\r\nlogout\r\n".to_vec()).await;
                let _ = handle.eof(channel).await;
                let _ = handle.close(channel).await;
            });
        }
        Ok(())
    }

    async fn subsystem_request(
        &mut self,
        channel_id: ChannelId,
        name: &str,
        session: &mut Session,
    ) -> Result<(), Self::Error> {
        if name != "sftp" {
            session.channel_failure(channel_id)?;
            return Ok(());
        }
        let Some(channel) = self.channels.lock().unwrap().remove(&channel_id) else {
            session.channel_failure(channel_id)?;
            return Ok(());
        };
        session.channel_success(channel_id)?;
        self.sftp_channels.lock().unwrap().insert(channel_id);
        let handler = DirSftp::new(self.sftp_root.clone());
        tokio::spawn(async move {
            russh_sftp::server::run(channel.into_stream(), handler).await;
        });
        Ok(())
    }

    async fn data(&mut self, channel: ChannelId, data: &[u8], session: &mut Session) -> Result<(), Self::Error> {
        if self.sftp_channels.lock().unwrap().contains(&channel) {
            return Ok(());
        }
        let mut out = Vec::with_capacity(data.len() + 8);
        for &byte in data {
            match byte {
                b'\r' => out.extend_from_slice(b"\r\n$ "),
                0x7f => out.extend_from_slice(b"\x08 \x08"),
                3 => {
                    session.data(channel, &b"\r\n[Ctrl-C] closing\r\n"[..])?;
                    session.eof(channel)?;
                    session.close(channel)?;
                    return Ok(());
                }
                _ => out.push(byte),
            }
        }
        session.data(channel, out)?;
        Ok(())
    }
}

/// SFTP over a local directory; remote `/` is the root and `..` cannot escape it.
struct DirSftp {
    root: Arc<PathBuf>,
    next_handle: u32,
    dirs: HashMap<String, Option<Vec<File>>>,
    files: HashMap<String, fs::File>,
}

impl DirSftp {
    fn new(root: Arc<PathBuf>) -> Self {
        Self {
            root,
            next_handle: 1,
            dirs: HashMap::new(),
            files: HashMap::new(),
        }
    }

    fn normalize(remote: &str) -> String {
        let mut parts: Vec<&str> = Vec::new();
        for component in Path::new(remote).components() {
            match component {
                Component::Normal(p) => parts.push(p.to_str().unwrap_or("")),
                Component::ParentDir => {
                    parts.pop();
                }
                _ => {}
            }
        }
        format!("/{}", parts.join("/"))
    }

    fn local(&self, remote: &str) -> PathBuf {
        let normalized = Self::normalize(remote);
        self.root.join(normalized.trim_start_matches('/'))
    }

    fn handle(&mut self) -> String {
        let h = self.next_handle.to_string();
        self.next_handle += 1;
        h
    }

    fn ok(id: u32) -> Status {
        Status {
            id,
            status_code: StatusCode::Ok,
            error_message: "Ok".into(),
            language_tag: "en-US".into(),
        }
    }

    fn attrs_for(path: &Path) -> Result<FileAttributes, StatusCode> {
        let meta = fs::symlink_metadata(path).map_err(|_| StatusCode::NoSuchFile)?;
        Ok(FileAttributes::from(&meta))
    }
}

impl russh_sftp::server::Handler for DirSftp {
    type Error = StatusCode;

    fn unimplemented(&self) -> Self::Error {
        StatusCode::OpUnsupported
    }

    async fn init(&mut self, _version: u32, _extensions: HashMap<String, String>) -> Result<Version, Self::Error> {
        Ok(Version::new())
    }

    async fn realpath(&mut self, id: u32, path: String) -> Result<Name, Self::Error> {
        let normalized = Self::normalize(&path);
        let attrs = Self::attrs_for(&self.local(&normalized)).unwrap_or_default();
        Ok(Name {
            id,
            files: vec![File::new(normalized, attrs)],
        })
    }

    async fn stat(&mut self, id: u32, path: String) -> Result<Attrs, Self::Error> {
        Ok(Attrs {
            id,
            attrs: Self::attrs_for(&self.local(&path))?,
        })
    }

    async fn lstat(&mut self, id: u32, path: String) -> Result<Attrs, Self::Error> {
        self.stat(id, path).await
    }

    async fn fstat(&mut self, id: u32, handle: String) -> Result<Attrs, Self::Error> {
        let file = self.files.get(&handle).ok_or(StatusCode::BadMessage)?;
        let meta = file.metadata().map_err(|_| StatusCode::Failure)?;
        Ok(Attrs {
            id,
            attrs: FileAttributes::from(&meta),
        })
    }

    async fn opendir(&mut self, id: u32, path: String) -> Result<Handle, Self::Error> {
        let local = self.local(&path);
        let read = fs::read_dir(&local).map_err(|_| StatusCode::NoSuchFile)?;
        let mut files = Vec::new();
        for entry in read.flatten() {
            let name = entry.file_name().to_string_lossy().into_owned();
            let attrs = Self::attrs_for(&entry.path()).unwrap_or_default();
            files.push(File::new(name, attrs));
        }
        let handle = self.handle();
        self.dirs.insert(handle.clone(), Some(files));
        Ok(Handle { id, handle })
    }

    async fn readdir(&mut self, id: u32, handle: String) -> Result<Name, Self::Error> {
        let slot = self.dirs.get_mut(&handle).ok_or(StatusCode::BadMessage)?;
        match slot.take() {
            Some(files) => Ok(Name { id, files }),
            None => Err(StatusCode::Eof),
        }
    }

    async fn open(
        &mut self,
        id: u32,
        filename: String,
        pflags: OpenFlags,
        _attrs: FileAttributes,
    ) -> Result<Handle, Self::Error> {
        let local = self.local(&filename);
        let mut options = fs::OpenOptions::new();
        options.read(pflags.contains(OpenFlags::READ) || !pflags.contains(OpenFlags::WRITE));
        options.write(pflags.contains(OpenFlags::WRITE));
        options.create(pflags.contains(OpenFlags::CREATE));
        options.truncate(pflags.contains(OpenFlags::TRUNCATE));
        options.append(pflags.contains(OpenFlags::APPEND));
        let file = options.open(&local).map_err(|_| StatusCode::NoSuchFile)?;
        let handle = self.handle();
        self.files.insert(handle.clone(), file);
        Ok(Handle { id, handle })
    }

    async fn read(&mut self, id: u32, handle: String, offset: u64, len: u32) -> Result<Data, Self::Error> {
        let file = self.files.get(&handle).ok_or(StatusCode::BadMessage)?;
        let mut buf = vec![0u8; len as usize];
        let n = file.read_at(&mut buf, offset).map_err(|_| StatusCode::Failure)?;
        if n == 0 {
            return Err(StatusCode::Eof);
        }
        buf.truncate(n);
        Ok(Data { id, data: buf })
    }

    async fn write(&mut self, id: u32, handle: String, offset: u64, data: Vec<u8>) -> Result<Status, Self::Error> {
        let file = self.files.get(&handle).ok_or(StatusCode::BadMessage)?;
        file.write_all_at(&data, offset).map_err(|_| StatusCode::Failure)?;
        Ok(Self::ok(id))
    }

    async fn close(&mut self, id: u32, handle: String) -> Result<Status, Self::Error> {
        self.dirs.remove(&handle);
        self.files.remove(&handle);
        Ok(Self::ok(id))
    }

    async fn mkdir(&mut self, id: u32, path: String, _attrs: FileAttributes) -> Result<Status, Self::Error> {
        fs::create_dir(self.local(&path)).map_err(|_| StatusCode::Failure)?;
        Ok(Self::ok(id))
    }

    async fn rmdir(&mut self, id: u32, path: String) -> Result<Status, Self::Error> {
        fs::remove_dir(self.local(&path)).map_err(|_| StatusCode::Failure)?;
        Ok(Self::ok(id))
    }

    async fn remove(&mut self, id: u32, filename: String) -> Result<Status, Self::Error> {
        fs::remove_file(self.local(&filename)).map_err(|_| StatusCode::NoSuchFile)?;
        Ok(Self::ok(id))
    }

    async fn rename(&mut self, id: u32, oldpath: String, newpath: String) -> Result<Status, Self::Error> {
        fs::rename(self.local(&oldpath), self.local(&newpath)).map_err(|_| StatusCode::Failure)?;
        Ok(Self::ok(id))
    }
}

/// Seconds since the epoch for a local file's modification time.
pub fn mtime_epoch(path: &Path) -> Option<u64> {
    fs::metadata(path)
        .ok()?
        .modified()
        .ok()?
        .duration_since(UNIX_EPOCH)
        .ok()
        .map(|d| d.as_secs())
}
