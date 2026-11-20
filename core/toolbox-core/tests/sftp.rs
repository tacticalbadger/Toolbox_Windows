//! End-to-end test of `SftpSession` against the in-process SSH server.

use std::sync::atomic::{AtomicUsize, Ordering::SeqCst};
use std::sync::{Arc, Mutex};

use toolbox_core::ssh_test_server::{self, TestServerConfig};
use toolbox_core::{CoreError, SftpListener, SftpSession, SshAuth, SshConfig};

#[derive(Default)]
struct Seen {
    prompts: AtomicUsize,
    progress: Mutex<Vec<(String, u64, u64)>>,
}

struct Recorder(Arc<Seen>);

impl SftpListener for Recorder {
    fn confirm_host_key(&self, _host: String, _port: u16, _key_type: String, _fingerprint: String) -> bool {
        self.0.prompts.fetch_add(1, SeqCst);
        true
    }
    fn on_progress(&self, transfer_id: String, done: u64, total: u64) {
        self.0.progress.lock().unwrap().push((transfer_id, done, total));
    }
}

fn config(addr: std::net::SocketAddr, known_hosts: &std::path::Path, password: &str) -> SshConfig {
    SshConfig {
        host: addr.ip().to_string(),
        port: addr.port(),
        username: "demo".into(),
        auth: SshAuth::Password { password: password.into() },
        term: "xterm".into(),
        cols: 80,
        rows: 24,
        connect_timeout_secs: 5,
        known_hosts_path: Some(known_hosts.to_string_lossy().into()),
    }
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn browse_transfer_and_manage_files() {
    let root = tempfile::tempdir().unwrap();
    std::fs::create_dir(root.path().join("logs")).unwrap();
    std::fs::write(root.path().join("README.txt"), b"hello from the server").unwrap();
    let big: Vec<u8> = (0..1_500_000u32).map(|i| (i % 251) as u8).collect();
    std::fs::write(root.path().join("logs/big.bin"), &big).unwrap();

    let server = ssh_test_server::start(
        "127.0.0.1:0".parse().unwrap(),
        TestServerConfig {
            username: "demo".into(),
            password: "secret".into(),
            sftp_root: root.path().to_path_buf(),
            host_key: None,
            shell_exit_after: None,
        },
    )
    .await
    .unwrap();
    let scratch = tempfile::tempdir().unwrap();
    let known_hosts = scratch.path().join("known_hosts");

    let seen = Arc::new(Seen::default());
    let sftp = SftpSession::connect(config(server.addr, &known_hosts, "secret"), Box::new(Recorder(seen.clone())))
        .await
        .unwrap();
    assert_eq!(seen.prompts.load(SeqCst), 1);
    assert_eq!(sftp.home().await.unwrap(), "/");

    // Listing: directories first, dot entries hidden.
    let entries = sftp.list("/".into()).await.unwrap();
    let names: Vec<_> = entries.iter().map(|e| e.name.as_str()).collect();
    assert_eq!(names, vec!["logs", "README.txt"]);
    assert!(entries[0].is_dir);
    assert_eq!(entries[1].size, 21);
    assert_eq!(entries[1].path, "/README.txt");
    assert!(entries[1].modified_epoch.is_some());
    let logs = sftp.list("/logs".into()).await.unwrap();
    assert_eq!(logs[0].path, "/logs/big.bin");
    assert_eq!(logs[0].size, big.len() as u64);

    // Download with progress.
    let local = scratch.path().join("big.bin");
    let written = sftp
        .download("/logs/big.bin".into(), local.to_string_lossy().into(), "t1".into())
        .await
        .unwrap();
    assert_eq!(written, big.len() as u64);
    assert_eq!(std::fs::read(&local).unwrap(), big);
    {
        let progress = seen.progress.lock().unwrap();
        assert!(progress.len() >= 2, "{progress:?}");
        let last = progress.last().unwrap();
        assert_eq!((last.1, last.2), (big.len() as u64, big.len() as u64));
    }

    // Upload, then verify on the server side.
    let upload = scratch.path().join("notes.txt");
    std::fs::write(&upload, b"uploaded content").unwrap();
    let sent = sftp
        .upload(upload.to_string_lossy().into(), "/logs/notes.txt".into(), "t2".into())
        .await
        .unwrap();
    assert_eq!(sent, 16);
    assert_eq!(std::fs::read(root.path().join("logs/notes.txt")).unwrap(), b"uploaded content");

    // Directory management.
    sftp.make_dir("/archive".into()).await.unwrap();
    assert!(root.path().join("archive").is_dir());
    sftp.rename("/logs/notes.txt".into(), "/archive/notes.txt".into()).await.unwrap();
    assert!(root.path().join("archive/notes.txt").exists());
    sftp.remove("/archive/notes.txt".into(), false).await.unwrap();
    sftp.remove("/archive".into(), true).await.unwrap();
    assert!(!root.path().join("archive").exists());

    // Errors are reported, not swallowed; `..` cannot escape the root.
    assert!(matches!(sftp.list("/missing".into()).await, Err(CoreError::Io(_))));
    assert_eq!(sftp.canonicalize("/../../etc".into()).await.unwrap(), "/etc");
    sftp.close().await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn wrong_password_is_an_auth_error() {
    let root = tempfile::tempdir().unwrap();
    let server = ssh_test_server::start(
        "127.0.0.1:0".parse().unwrap(),
        TestServerConfig {
            username: "demo".into(),
            password: "secret".into(),
            sftp_root: root.path().to_path_buf(),
            host_key: None,
            shell_exit_after: None,
        },
    )
    .await
    .unwrap();
    let scratch = tempfile::tempdir().unwrap();
    let seen = Arc::new(Seen::default());
    let result = SftpSession::connect(
        config(server.addr, &scratch.path().join("kh"), "nope"),
        Box::new(Recorder(seen.clone())),
    )
    .await;
    assert!(matches!(result, Err(CoreError::Auth(_))), "{:?}", result.as_ref().err());
}
