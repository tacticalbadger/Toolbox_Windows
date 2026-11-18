//! In-process FTP / FTPS server for tests and the `ftp_server` example.
//! Serves a directory on disk and accepts one username and password.

use std::net::TcpListener as StdListener;
use std::path::PathBuf;
use std::sync::Arc;

use async_trait::async_trait;
use libunftp::ServerBuilder;
use unftp_core::auth::{AuthenticationError, Authenticator, Credentials, Principal};
use unftp_sbe_fs::Filesystem;

#[derive(Debug)]
struct OneUser {
    username: String,
    password: String,
}

#[async_trait]
impl Authenticator for OneUser {
    async fn authenticate(&self, username: &str, creds: &Credentials) -> Result<Principal, AuthenticationError> {
        if username != self.username {
            return Err(AuthenticationError::BadUser);
        }
        if creds.password.as_deref() != Some(self.password.as_str()) {
            return Err(AuthenticationError::BadPassword);
        }
        Ok(Principal { username: username.to_string() })
    }
}

pub struct FtpTestServerConfig {
    pub username: String,
    pub password: String,
    pub root: PathBuf,
    /// Offer `AUTH TLS` with a freshly generated self-signed certificate.
    pub tls: bool,
    /// 0 picks a free port.
    pub port: u16,
}

/// A running server; it stops when the runtime that spawned it ends.
pub struct FtpTestServer {
    pub port: u16,
    /// Keeps the generated certificate files alive while the server runs.
    _cert_dir: Option<tempfile::TempDir>,
}

/// Starts the server on 127.0.0.1 inside the current Tokio runtime.
pub async fn start(config: FtpTestServerConfig) -> Result<FtpTestServer, Box<dyn std::error::Error + Send + Sync>> {
    let port = if config.port == 0 {
        StdListener::bind("127.0.0.1:0")?.local_addr()?.port()
    } else {
        config.port
    };
    let root = config.root.clone();
    let auth = Arc::new(OneUser {
        username: config.username,
        password: config.password,
    });
    let mut builder = ServerBuilder::with_authenticator(Box::new(move || Filesystem::new(root.clone()).expect("ftp root")), auth)
        .greeting("Toolbox test FTP server")
        .passive_ports(50000..=50100);
    let mut cert_dir = None;
    if config.tls {
        let dir = tempfile::tempdir()?;
        let cert = rcgen::generate_simple_self_signed(vec!["localhost".to_owned(), "127.0.0.1".to_owned()])?;
        let cert_path = dir.path().join("cert.pem");
        let key_path = dir.path().join("key.pem");
        std::fs::write(&cert_path, cert.cert.pem())?;
        std::fs::write(&key_path, cert.signing_key.serialize_pem())?;
        builder = builder.ftps(cert_path, key_path);
        cert_dir = Some(dir);
    }
    let server = builder.build()?;
    tokio::spawn(async move {
        if let Err(e) = server.listen(format!("127.0.0.1:{port}")).await {
            eprintln!("ftp test server stopped: {e}");
        }
    });
    // Wait until the control port accepts connections.
    for _ in 0..50 {
        if tokio::net::TcpStream::connect(("127.0.0.1", port)).await.is_ok() {
            break;
        }
        tokio::time::sleep(std::time::Duration::from_millis(50)).await;
    }
    Ok(FtpTestServer { port, _cert_dir: cert_dir })
}
