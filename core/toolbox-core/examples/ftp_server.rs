//! Local FTP (or FTPS) server for trying File Transfer connections.
//!
//!   cargo run --example ftp_server --features test-support -- [--port 2121] [--root DIR] [--tls]
//!
//! Login: matt / secret. Without --root a temporary folder with a few files is served.

use std::path::PathBuf;

use toolbox_core::ftp_test_server::{start, FtpTestServerConfig};

#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    let mut port = 2121u16;
    let mut root: Option<PathBuf> = None;
    let mut tls = false;
    let mut args = std::env::args().skip(1);
    while let Some(arg) = args.next() {
        match arg.as_str() {
            "--port" => port = args.next().ok_or("--port needs a value")?.parse()?,
            "--root" => root = Some(PathBuf::from(args.next().ok_or("--root needs a value")?)),
            "--tls" => tls = true,
            other => return Err(format!("unknown argument {other}").into()),
        }
    }
    let root = match root {
        Some(r) => r,
        None => {
            let dir = std::env::temp_dir().join("toolbox-ftp-root");
            std::fs::create_dir_all(dir.join("docs"))?;
            std::fs::write(dir.join("readme.txt"), "Toolbox test FTP server\n")?;
            std::fs::write(dir.join("docs/notes.txt"), "nested file\n")?;
            dir
        }
    };
    let server = start(FtpTestServerConfig {
        username: "matt".into(),
        password: "secret".into(),
        root: root.clone(),
        tls,
        port,
    })
    .await?;
    println!(
        "{} server listening on 127.0.0.1:{} (matt / secret), root {}",
        if tls { "FTPS (explicit)" } else { "FTP" },
        server.port,
        root.display()
    );
    std::future::pending::<()>().await;
    Ok(())
}
