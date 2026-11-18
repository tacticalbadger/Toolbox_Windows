//! A local SSH server for trying the app without a real host.
//!
//!     cargo run --example echo_server --features test-support -- --key <openssh-private-key> [--port 2222] [--root <dir>]
//!
//! Accepts any public key and the password "secret", echoes what you type,
//! and serves `--root` (default: a temp dir with a few files) over SFTP.

use std::path::PathBuf;
use std::time::Duration;

use russh::keys::load_secret_key;
use toolbox_core::ssh_test_server::{self, TestServerConfig};

#[tokio::main]
async fn main() {
    let args: Vec<String> = std::env::args().collect();
    let arg = |name: &str| args.iter().position(|a| a == name).and_then(|i| args.get(i + 1)).cloned();
    let port: u16 = arg("--port").map(|p| p.parse().expect("port")).unwrap_or(2222);
    let exit_after = arg("--exit-after").map(|s| Duration::from_secs(s.parse().expect("seconds")));
    let host_key = arg("--key").map(|path| load_secret_key(path, None).expect("load host key"));
    let root = match arg("--root") {
        Some(dir) => PathBuf::from(dir),
        None => {
            let dir = std::env::temp_dir().join("toolbox-echo-sftp");
            std::fs::create_dir_all(dir.join("logs")).expect("sftp root");
            std::fs::create_dir_all(dir.join("etc")).expect("sftp root");
            std::fs::write(dir.join("README.txt"), "Served by the Toolbox echo server.\n").ok();
            std::fs::write(dir.join("logs/syslog"), "Nov 12 09:00:01 host sshd[1]: hello\n".repeat(200)).ok();
            std::fs::write(dir.join("etc/hosts"), "127.0.0.1 localhost\n").ok();
            dir
        }
    };
    let server = ssh_test_server::start(
        format!("127.0.0.1:{port}").parse().expect("address"),
        TestServerConfig {
            username: "matt".into(),
            password: "secret".into(),
            sftp_root: root.clone(),
            host_key,
            shell_exit_after: exit_after,
        },
    )
    .await
    .expect("start ssh test server");
    eprintln!("echo server listening on {} (matt / secret), sftp root {}", server.addr, root.display());
    loop {
        tokio::time::sleep(Duration::from_secs(3600)).await;
    }
}
