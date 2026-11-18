//! A local RDP server for trying the app without a Windows host.
//!
//!     cargo run --example rdp_server --features test-support -- [--port 3390]
//!
//! Logs in with user "matt" / password "secret" over NLA, shows a green
//! desktop with a wandering block, and honours resize requests.

use std::time::Duration;

#[tokio::main]
async fn main() {
    let args: Vec<String> = std::env::args().collect();
    let port: u16 = args
        .iter()
        .position(|a| a == "--port")
        .and_then(|i| args.get(i + 1))
        .map(|p| p.parse().expect("port"))
        .unwrap_or(3390);

    // Bind the requested port by starting the server, then report it.
    let server = toolbox_core::rdp_test_server::start_on(
        format!("127.0.0.1:{port}").parse().expect("address"),
        toolbox_core::rdp_test_server::TestServerConfig::default(),
    )
    .await
    .expect("start rdp test server");
    eprintln!("rdp test server listening on {} (matt / secret)", server.addr);

    loop {
        tokio::time::sleep(Duration::from_secs(5)).await;
        let observed = server.observed.lock().unwrap();
        eprintln!(
            "keys={} mouse={} layouts={:?}",
            observed.keys.len(),
            observed.mouse.len(),
            observed.layouts
        );
    }
}
