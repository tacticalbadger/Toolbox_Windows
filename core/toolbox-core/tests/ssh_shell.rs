//! End-to-end test of `SshSession` against an in-process russh server.

use std::net::SocketAddr;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering::SeqCst};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use russh::keys::ssh_key::LineEnding;
use russh::keys::{Algorithm, PrivateKey};
use russh::server::{self, Auth, ChannelOpenHandle, Msg, Server as _, Session, VerifiedPublicKey};
use russh::{Channel, ChannelId, Pty};
use tokio::net::TcpListener;
use toolbox_core::{SshAuth, SshConfig, SshListener, SshSession};

// ---------- a tiny echo server ----------

#[derive(Default)]
struct ServerState {
    pty: Option<(u32, u32)>,
    resized: Option<(u32, u32)>,
}

#[derive(Clone, Default)]
struct EchoServer {
    state: Arc<Mutex<ServerState>>,
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
        Ok(if user == "matt" && password == "secret" {
            Auth::Accept
        } else {
            Auth::reject()
        })
    }

    async fn auth_publickey(&mut self, user: &str, _key: VerifiedPublicKey<'_>) -> Result<Auth, Self::Error> {
        Ok(if user == "matt" { Auth::Accept } else { Auth::reject() })
    }

    async fn channel_open_session(
        &mut self,
        _channel: Channel<Msg>,
        reply: ChannelOpenHandle,
        _session: &mut Session,
    ) -> Result<(), Self::Error> {
        reply.accept().await;
        Ok(())
    }

    #[allow(clippy::too_many_arguments)]
    async fn pty_request(
        &mut self,
        channel: ChannelId,
        _term: &str,
        cols: u32,
        rows: u32,
        _pix_width: u32,
        _pix_height: u32,
        _modes: &[(Pty, u32)],
        session: &mut Session,
    ) -> Result<(), Self::Error> {
        self.state.lock().unwrap().pty = Some((cols, rows));
        session.channel_success(channel)?;
        Ok(())
    }

    async fn shell_request(&mut self, channel: ChannelId, session: &mut Session) -> Result<(), Self::Error> {
        session.channel_success(channel)?;
        session.data(channel, &b"WELCOME\r\n"[..])?;
        Ok(())
    }

    async fn data(&mut self, channel: ChannelId, data: &[u8], session: &mut Session) -> Result<(), Self::Error> {
        if data == [3] {
            session.eof(channel)?;
            session.close(channel)?;
            return Ok(());
        }
        let mut out = b"echo:".to_vec();
        out.extend_from_slice(data);
        session.data(channel, out)?;
        Ok(())
    }

    async fn window_change_request(
        &mut self,
        _channel: ChannelId,
        cols: u32,
        rows: u32,
        _pix_width: u32,
        _pix_height: u32,
        _session: &mut Session,
    ) -> Result<(), Self::Error> {
        self.state.lock().unwrap().resized = Some((cols, rows));
        Ok(())
    }
}

fn start_server(rt: &tokio::runtime::Runtime) -> (u16, Arc<Mutex<ServerState>>) {
    let socket = rt.block_on(TcpListener::bind(("127.0.0.1", 0))).unwrap();
    let port = socket.local_addr().unwrap().port();
    let config = Arc::new(server::Config {
        keys: vec![PrivateKey::random(&mut rand::rng(), Algorithm::Ed25519).unwrap()],
        auth_rejection_time: Duration::from_millis(10),
        auth_rejection_time_initial: Some(Duration::ZERO),
        ..Default::default()
    });
    let mut server = EchoServer::default();
    let state = server.state.clone();
    rt.spawn(async move {
        server.run_on_socket(config, &socket).await.unwrap();
    });
    (port, state)
}

// ---------- a recording listener ----------

#[derive(Default)]
struct Recorded {
    connected: AtomicBool,
    closed: AtomicBool,
    clean: AtomicBool,
    data: Mutex<Vec<u8>>,
    errors: Mutex<Vec<String>>,
    host_key_prompts: AtomicUsize,
    fingerprint: Mutex<Option<String>>,
    accept_host_key: AtomicBool,
}

struct Recorder(Arc<Recorded>);

impl SshListener for Recorder {
    fn on_connected(&self) {
        self.0.connected.store(true, SeqCst);
    }
    fn on_data(&self, data: Vec<u8>) {
        self.0.data.lock().unwrap().extend(data);
    }
    fn on_error(&self, message: String) {
        self.0.errors.lock().unwrap().push(message);
    }
    fn on_closed(&self, clean: bool) {
        self.0.clean.store(clean, SeqCst);
        self.0.closed.store(true, SeqCst);
    }
    fn confirm_host_key(&self, _host: String, _port: u16, _key_type: String, fingerprint: String) -> bool {
        self.0.host_key_prompts.fetch_add(1, SeqCst);
        *self.0.fingerprint.lock().unwrap() = Some(fingerprint);
        self.0.accept_host_key.load(SeqCst)
    }
}

fn wait_until(what: &str, mut cond: impl FnMut() -> bool) {
    let deadline = Instant::now() + Duration::from_secs(10);
    while !cond() {
        assert!(Instant::now() < deadline, "timed out waiting for {what}");
        std::thread::sleep(Duration::from_millis(20));
    }
}

fn config(port: u16, password: &str, known_hosts: &std::path::Path) -> SshConfig {
    SshConfig {
        host: "127.0.0.1".into(),
        port,
        username: "matt".into(),
        auth: SshAuth::Password {
            password: password.into(),
        },
        term: "xterm-256color".into(),
        cols: 80,
        rows: 24,
        connect_timeout_secs: 5,
        known_hosts_path: Some(known_hosts.to_string_lossy().into()),
    }
}

fn recorder(accept_host_key: bool) -> (Arc<Recorded>, Box<dyn SshListener>) {
    let recorded = Arc::new(Recorded::default());
    recorded.accept_host_key.store(accept_host_key, SeqCst);
    (recorded.clone(), Box::new(Recorder(recorded)))
}

// ---------- tests ----------

#[test]
fn password_shell_roundtrip_and_host_key_learning() {
    let rt = tokio::runtime::Runtime::new().unwrap();
    let (port, server_state) = start_server(&rt);
    let dir = tempfile::tempdir().unwrap();
    let known_hosts = dir.path().join("known_hosts");

    let (seen, listener) = recorder(true);
    let session = SshSession::connect(config(port, "secret", &known_hosts), listener);

    wait_until("connect", || seen.connected.load(SeqCst));
    wait_until("banner", || {
        String::from_utf8_lossy(&seen.data.lock().unwrap()).contains("WELCOME")
    });
    assert_eq!(server_state.lock().unwrap().pty, Some((80, 24)));

    session.write(b"hi\n".to_vec());
    wait_until("echo", || {
        String::from_utf8_lossy(&seen.data.lock().unwrap()).contains("echo:hi\n")
    });

    session.resize(120, 40);
    wait_until("resize", || server_state.lock().unwrap().resized == Some((120, 40)));

    assert!(!session.is_closed());
    session.close();
    wait_until("close", || seen.closed.load(SeqCst));
    assert!(session.is_closed());
    assert!(seen.clean.load(SeqCst), "closing from our side is a clean end");
    assert!(seen.errors.lock().unwrap().is_empty(), "{:?}", seen.errors.lock().unwrap());

    // The host key was unknown, so we were asked once and it was recorded.
    assert_eq!(seen.host_key_prompts.load(SeqCst), 1);
    let fp = seen.fingerprint.lock().unwrap().clone().unwrap();
    assert!(fp.starts_with("SHA256:"), "fingerprint was {fp}");
    let recorded = std::fs::read_to_string(&known_hosts).unwrap();
    assert!(recorded.contains("127.0.0.1"), "known_hosts was: {recorded}");

    // Second connection to the same host: no prompt.
    let (seen2, listener2) = recorder(false);
    let session2 = SshSession::connect(config(port, "secret", &known_hosts), listener2);
    wait_until("second connect", || seen2.connected.load(SeqCst));
    assert_eq!(seen2.host_key_prompts.load(SeqCst), 0);
    session2.close();
    wait_until("second close", || seen2.closed.load(SeqCst));
}

#[test]
fn wrong_password_reports_auth_error() {
    let rt = tokio::runtime::Runtime::new().unwrap();
    let (port, _) = start_server(&rt);
    let dir = tempfile::tempdir().unwrap();

    let (seen, listener) = recorder(true);
    let _session = SshSession::connect(config(port, "nope", &dir.path().join("kh")), listener);

    wait_until("closed", || seen.closed.load(SeqCst));
    assert!(!seen.connected.load(SeqCst));
    assert!(!seen.clean.load(SeqCst), "a failed login is not a clean end");
    let errors = seen.errors.lock().unwrap().clone();
    assert_eq!(errors.len(), 1, "{errors:?}");
    assert!(errors[0].contains("authentication failed"), "{errors:?}");
}

#[test]
fn remote_logout_ends_cleanly() {
    let rt = tokio::runtime::Runtime::new().unwrap();
    let (port, _) = start_server(&rt);
    let dir = tempfile::tempdir().unwrap();
    let (seen, listener) = recorder(true);
    let session = SshSession::connect(config(port, "secret", &dir.path().join("kh")), listener);
    wait_until("connect", || seen.connected.load(SeqCst));
    // Ctrl-C makes the echo server close the channel from its side.
    session.write(vec![3]);
    wait_until("remote close", || seen.closed.load(SeqCst));
    assert!(seen.clean.load(SeqCst), "the server hanging up is a clean end");
    assert!(seen.errors.lock().unwrap().is_empty());
}

#[test]
fn rejected_host_key_aborts_before_auth() {
    let rt = tokio::runtime::Runtime::new().unwrap();
    let (port, _) = start_server(&rt);
    let dir = tempfile::tempdir().unwrap();
    let known_hosts = dir.path().join("known_hosts");

    let (seen, listener) = recorder(false);
    let _session = SshSession::connect(config(port, "secret", &known_hosts), listener);

    wait_until("closed", || seen.closed.load(SeqCst));
    assert!(!seen.connected.load(SeqCst));
    assert_eq!(seen.host_key_prompts.load(SeqCst), 1);
    let errors = seen.errors.lock().unwrap().clone();
    assert_eq!(errors.len(), 1, "{errors:?}");
    assert!(errors[0].contains("host key"), "{errors:?}");
    assert!(!known_hosts.exists());
}

#[test]
fn private_key_content_authenticates() {
    let rt = tokio::runtime::Runtime::new().unwrap();
    let (port, _) = start_server(&rt);
    let dir = tempfile::tempdir().unwrap();
    let key = PrivateKey::random(&mut rand::rng(), Algorithm::Ed25519).unwrap();
    let pem = key.to_openssh(LineEnding::LF).unwrap().to_string();

    let (seen, listener) = recorder(true);
    let mut cfg = config(port, "unused", &dir.path().join("kh"));
    cfg.auth = SshAuth::KeyData { key: pem, passphrase: None };
    let session = SshSession::connect(cfg, listener);
    wait_until("connect with key data", || seen.connected.load(SeqCst));
    session.close();
    wait_until("close", || seen.closed.load(SeqCst));
    assert!(seen.errors.lock().unwrap().is_empty(), "{:?}", seen.errors.lock().unwrap());

    // Garbage key content fails before touching the network.
    let (seen2, listener2) = recorder(true);
    let mut bad = config(port, "unused", &dir.path().join("kh"));
    bad.auth = SshAuth::KeyData { key: "not a key".into(), passphrase: None };
    let _s = SshSession::connect(bad, listener2);
    wait_until("bad key closed", || seen2.closed.load(SeqCst));
    let errors = seen2.errors.lock().unwrap().clone();
    assert!(errors.iter().any(|e| e.contains("cannot read private key")), "{errors:?}");
}
