//! FTP and FTPS client against the in-process libunftp server.

use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::Arc;

use toolbox_core::ftp::{FtpConfig, FtpListener, FtpSession};
use toolbox_core::ftp_test_server::{start, FtpTestServerConfig};
use toolbox_core::CoreError;

struct Recorder {
    accept_untrusted: bool,
    prompts: Arc<AtomicUsize>,
}

impl FtpListener for Recorder {
    fn confirm_untrusted_certificate(&self, _host: String, _reason: String) -> bool {
        self.prompts.fetch_add(1, Ordering::SeqCst);
        self.accept_untrusted
    }
    fn on_progress(&self, _transfer_id: String, _done: u64, _total: u64) {}
}

fn config(port: u16, password: &str, secure: bool, accept_invalid: bool) -> FtpConfig {
    FtpConfig {
        host: "127.0.0.1".into(),
        port,
        username: "demo".into(),
        password: password.into(),
        secure,
        accept_invalid_certs: accept_invalid,
        connect_timeout_secs: 5,
    }
}

async fn server(tls: bool) -> (tempfile::TempDir, u16) {
    let root = tempfile::tempdir().unwrap();
    std::fs::create_dir(root.path().join("docs")).unwrap();
    std::fs::write(root.path().join("hello.txt"), b"hello over ftp\n").unwrap();
    let s = start(FtpTestServerConfig {
        username: "demo".into(),
        password: "secret".into(),
        root: root.path().to_path_buf(),
        tls,
        port: 0,
    })
    .await
    .unwrap();
    (root, s.port)
}

fn recorder(accept: bool) -> (Box<Recorder>, Arc<AtomicUsize>) {
    let prompts = Arc::new(AtomicUsize::new(0));
    (Box::new(Recorder { accept_untrusted: accept, prompts: prompts.clone() }), prompts)
}

#[tokio::test(flavor = "multi_thread")]
async fn plain_ftp_browse_and_transfer() {
    let (root, port) = server(false).await;
    let (listener, _) = recorder(false);
    let session = FtpSession::connect(config(port, "secret", false, false), listener).await.unwrap();
    let home = session.home().await.unwrap();
    assert_eq!(home, "/");

    let names: Vec<_> = session.list(home.clone()).await.unwrap().into_iter().map(|e| (e.name, e.is_dir)).collect();
    assert_eq!(names, vec![("docs".to_string(), true), ("hello.txt".to_string(), false)]);
    assert_eq!(session.canonicalize("/docs".into()).await.unwrap(), "/docs");

    // Download, then upload a copy and read it back from disk.
    let local = tempfile::tempdir().unwrap();
    let got = local.path().join("hello.txt");
    let n = session.download("/hello.txt".into(), got.to_string_lossy().into(), "d1".into()).await.unwrap();
    assert_eq!(n, 15);
    assert_eq!(std::fs::read(&got).unwrap(), b"hello over ftp\n");
    session.upload(got.to_string_lossy().into(), "/docs/copy.txt".into(), "u1".into()).await.unwrap();
    assert_eq!(std::fs::read(root.path().join("docs/copy.txt")).unwrap(), b"hello over ftp\n");

    // Management.
    session.make_dir("/new".into()).await.unwrap();
    session.rename("/docs/copy.txt".into(), "/new/moved.txt".into()).await.unwrap();
    assert!(root.path().join("new/moved.txt").exists());
    session.remove("/new/moved.txt".into(), false).await.unwrap();
    session.remove("/new".into(), true).await.unwrap();
    assert!(!root.path().join("new").exists());
    session.close().await;
    assert!(matches!(session.list("/".into()).await, Err(CoreError::Connect(_))));
}

#[tokio::test(flavor = "multi_thread")]
async fn wrong_password_is_an_auth_error() {
    let (_root, port) = server(false).await;
    let (listener, _) = recorder(false);
    let result = FtpSession::connect(config(port, "nope", false, false), listener).await;
    assert!(matches!(result, Err(CoreError::Auth(_))), "{:?}", result.err());
}

#[tokio::test(flavor = "multi_thread")]
async fn ftps_untrusted_certificate_is_asked_about() {
    let (_root, port) = server(true).await;

    // Refused: no session, one prompt.
    let (listener, prompts) = recorder(false);
    let refused = FtpSession::connect(config(port, "secret", true, false), listener).await;
    assert!(matches!(refused, Err(CoreError::HostKey(_))), "{:?}", refused.err());
    assert_eq!(prompts.load(Ordering::SeqCst), 1);

    // Accepted at the prompt: encrypted session works.
    let (listener, prompts) = recorder(true);
    let session = FtpSession::connect(config(port, "secret", true, false), listener).await.unwrap();
    assert_eq!(prompts.load(Ordering::SeqCst), 1);
    let names: Vec<_> = session.list("/".into()).await.unwrap().into_iter().map(|e| e.name).collect();
    assert_eq!(names, vec!["docs", "hello.txt"]);
    session.close().await;

    // Pre-approved: no prompt at all.
    let (listener, prompts) = recorder(false);
    let session = FtpSession::connect(config(port, "secret", true, true), listener).await.unwrap();
    assert_eq!(prompts.load(Ordering::SeqCst), 0);
    session.close().await;
}
