//! End-to-end test of `RdpSession` against the in-process RDP server.

use std::sync::atomic::{AtomicBool, Ordering::SeqCst};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use toolbox_core::rdp_test_server::{self, TestServerConfig};
use toolbox_core::{RdpConfig, RdpListener, RdpMouseButton, RdpSession};

#[derive(Default)]
struct Seen {
    connected: Mutex<Option<(u16, u16)>>,
    resized: Mutex<Option<(u16, u16)>>,
    regions: Mutex<Vec<(u16, u16, u16, u16)>>,
    errors: Mutex<Vec<String>>,
    closed: AtomicBool,
    clean: AtomicBool,
    close_reason: Mutex<Option<String>>,
}

struct Recorder(Arc<Seen>);

impl RdpListener for Recorder {
    fn on_connected(&self, width: u16, height: u16) {
        *self.0.connected.lock().unwrap() = Some((width, height));
    }
    fn on_desktop_resized(&self, width: u16, height: u16) {
        *self.0.resized.lock().unwrap() = Some((width, height));
    }
    fn on_region_updated(&self, x: u16, y: u16, width: u16, height: u16) {
        self.0.regions.lock().unwrap().push((x, y, width, height));
    }
    fn on_error(&self, message: String) {
        self.0.errors.lock().unwrap().push(message);
    }
    fn on_closed(&self, reason: String, clean: bool) {
        *self.0.close_reason.lock().unwrap() = Some(reason);
        self.0.clean.store(clean, SeqCst);
        self.0.closed.store(true, SeqCst);
    }
}

fn wait_until(what: &str, secs: u64, mut cond: impl FnMut() -> bool) {
    let deadline = Instant::now() + Duration::from_secs(secs);
    while !cond() {
        assert!(Instant::now() < deadline, "timed out waiting for {what}");
        std::thread::sleep(Duration::from_millis(25));
    }
}

fn config(addr: std::net::SocketAddr, password: &str) -> RdpConfig {
    RdpConfig {
        host: addr.ip().to_string(),
        port: addr.port(),
        username: "matt".into(),
        password: password.into(),
        domain: None,
        width: 640,
        height: 480,
        scale_factor: 100,
        accept_invalid_certs: true,
        connect_timeout_secs: 10,
    }
}

#[test]
fn connects_renders_inputs_and_resizes() {
    let rt = tokio::runtime::Runtime::new().unwrap();
    let server = rt.block_on(rdp_test_server::start(TestServerConfig::default())).unwrap();

    let seen = Arc::new(Seen::default());
    let session = RdpSession::connect(config(server.addr, "secret"), Box::new(Recorder(seen.clone())));

    wait_until("connect", 20, || seen.connected.lock().unwrap().is_some());
    assert_eq!(*seen.connected.lock().unwrap(), Some((640, 480)));
    assert_eq!(session.frame_size().width, 640);

    // The first update paints the whole desktop in the fill colour.
    wait_until("first frame", 10, || {
        seen.regions.lock().unwrap().iter().any(|&(x, y, w, h)| x == 0 && y == 0 && w >= 640 && h >= 480)
    });
    let pixels = session.copy_region(10, 10, 2, 1);
    assert_eq!(pixels, vec![0x40, 0x80, 0x20, 0xFF, 0x40, 0x80, 0x20, 0xFF], "pixels were {pixels:?}");
    let clipped = session.copy_region(636, 476, 100, 100);
    assert_eq!(clipped.len(), 4 * 4 * 4);
    assert!(session.copy_region(700, 10, 1, 1).is_empty());

    // Input reaches the server.
    session.mouse_move(100, 50);
    session.mouse_button(RdpMouseButton::Left, true);
    session.mouse_button(RdpMouseButton::Left, false);
    session.key(0x1E, false, true);
    session.key(0x1E, false, false);
    session.unicode(u32::from('é'), true);
    session.unicode(u32::from('é'), false);
    wait_until("input observed", 10, || {
        let o = server.observed.lock().unwrap();
        !o.mouse.is_empty() && o.keys.len() >= 4
    });

    // Resize through Display Control.
    session.resize(800, 600, 100);
    wait_until("resize", 20, || *seen.resized.lock().unwrap() == Some((800, 600)));
    assert_eq!(session.frame_size().width, 800);
    assert_eq!(server.observed.lock().unwrap().layouts.last(), Some(&(800, 600)));
    wait_until("frame after resize", 10, || {
        seen.regions.lock().unwrap().iter().any(|&(x, y, w, h)| x == 0 && y == 0 && w >= 800 && h >= 600)
    });

    assert!(!session.is_closed());
    session.close();
    wait_until("close", 10, || seen.closed.load(SeqCst));
    assert!(session.is_closed());
    assert!(seen.clean.load(SeqCst));
    assert!(seen.errors.lock().unwrap().is_empty(), "{:?}", seen.errors.lock().unwrap());
}

#[test]
fn wrong_password_is_reported() {
    let rt = tokio::runtime::Runtime::new().unwrap();
    let server = rt.block_on(rdp_test_server::start(TestServerConfig::default())).unwrap();

    let seen = Arc::new(Seen::default());
    let _session = RdpSession::connect(config(server.addr, "nope"), Box::new(Recorder(seen.clone())));
    wait_until("closed", 20, || seen.closed.load(SeqCst));
    assert!(seen.connected.lock().unwrap().is_none());
    assert!(!seen.clean.load(SeqCst));
    let errors = seen.errors.lock().unwrap().clone();
    assert_eq!(errors.len(), 1, "{errors:?}");
}

#[test]
fn unreachable_host_fails_fast() {
    let seen = Arc::new(Seen::default());
    let mut cfg = config("127.0.0.1:1".parse().unwrap(), "secret");
    cfg.connect_timeout_secs = 2;
    let _session = RdpSession::connect(cfg, Box::new(Recorder(seen.clone())));
    wait_until("closed", 10, || seen.closed.load(SeqCst));
    let errors = seen.errors.lock().unwrap().clone();
    assert!(errors.iter().any(|e| e.contains("127.0.0.1:1")), "{errors:?}");
}
