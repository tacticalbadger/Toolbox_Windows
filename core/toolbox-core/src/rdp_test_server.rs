//! An in-process RDP server for tests and the `rdp_server` example.
//!
//! Serves a solid-colour desktop with a small block that moves every few
//! hundred milliseconds, accepts one username/password over CredSSP, records
//! the input it receives, and honours Display Control resize requests.

use std::net::{SocketAddr, TcpListener};
use std::num::{NonZeroU16, NonZeroUsize};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use ironrdp::connector::DesktopSize;
use ironrdp::displaycontrol::pdu::DisplayControlMonitorLayout;
use ironrdp::server::{
    BitmapUpdate, Credentials, DisplayContext, DisplayUpdate, KeyboardEvent, MouseEvent, PixelFormat, RdpServer,
    RdpServerDisplay, RdpServerDisplayUpdates, RdpServerInputHandler, ServerResult, TlsIdentityCtx,
};
use tokio::sync::mpsc;

/// Everything the server saw from the client.
#[derive(Debug, Default)]
pub struct Observed {
    pub keys: Vec<KeyboardEvent>,
    pub mouse: Vec<MouseEvent>,
    pub layouts: Vec<(u32, u32)>,
}

pub struct TestServerConfig {
    pub username: String,
    pub password: String,
    pub width: u16,
    pub height: u16,
    /// Desktop colour as BGRA.
    pub fill: [u8; 4],
}

impl Default for TestServerConfig {
    fn default() -> Self {
        Self {
            username: "matt".into(),
            password: "secret".into(),
            width: 640,
            height: 480,
            fill: [0x40, 0x80, 0x20, 0xFF],
        }
    }
}

pub struct TestServer {
    pub addr: SocketAddr,
    pub observed: Arc<Mutex<Observed>>,
    stop: Option<tokio::sync::oneshot::Sender<()>>,
    thread: Option<std::thread::JoinHandle<()>>,
    _tls_dir: tempfile::TempDir,
}

impl Drop for TestServer {
    fn drop(&mut self) {
        if let Some(stop) = self.stop.take() {
            let _ = stop.send(());
        }
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}

#[derive(Clone)]
struct Handler {
    observed: Arc<Mutex<Observed>>,
}

impl RdpServerInputHandler for Handler {
    fn keyboard(&mut self, event: KeyboardEvent) {
        self.observed.lock().unwrap().keys.push(event);
    }

    fn mouse(&mut self, event: MouseEvent) {
        self.observed.lock().unwrap().mouse.push(event);
    }
}

struct Display {
    size: Arc<Mutex<DesktopSize>>,
    fill: [u8; 4],
    observed: Arc<Mutex<Observed>>,
    resize_tx: mpsc::UnboundedSender<DesktopSize>,
    resize_rx: Option<mpsc::UnboundedReceiver<DesktopSize>>,
}

#[async_trait::async_trait]
impl RdpServerDisplay for Display {
    async fn size(&mut self) -> DesktopSize {
        *self.size.lock().unwrap()
    }

    async fn updates(&mut self, _ctx: DisplayContext) -> ServerResult<Box<dyn RdpServerDisplayUpdates>> {
        let rx = self.resize_rx.take().unwrap_or_else(|| {
            let (tx, rx) = mpsc::unbounded_channel();
            self.resize_tx = tx;
            rx
        });
        Ok(Box::new(Updates {
            size: self.size.clone(),
            fill: self.fill,
            resize_rx: rx,
            sent_full: false,
            tick: 0,
        }))
    }

    fn request_layout(&mut self, layout: DisplayControlMonitorLayout) {
        if let Some(monitor) = layout.monitors().first() {
            let (width, height) = monitor.dimensions();
            self.observed.lock().unwrap().layouts.push((width, height));
            let size = DesktopSize {
                width: width as u16,
                height: height as u16,
            };
            *self.size.lock().unwrap() = size;
            let _ = self.resize_tx.send(size);
        }
    }
}

struct Updates {
    size: Arc<Mutex<DesktopSize>>,
    fill: [u8; 4],
    resize_rx: mpsc::UnboundedReceiver<DesktopSize>,
    sent_full: bool,
    tick: u32,
}

impl Updates {
    fn bitmap(&self, x: u16, y: u16, width: u16, height: u16, color: [u8; 4]) -> DisplayUpdate {
        let width = NonZeroU16::new(width).expect("non-zero width");
        let height = NonZeroU16::new(height).expect("non-zero height");
        let pixels = usize::from(width.get()) * usize::from(height.get());
        let mut data = Vec::with_capacity(pixels * 4);
        for _ in 0..pixels {
            data.extend_from_slice(&color);
        }
        DisplayUpdate::Bitmap(BitmapUpdate {
            x,
            y,
            width,
            height,
            format: PixelFormat::BgrA32,
            data: data.into(),
            stride: NonZeroUsize::new(usize::from(width.get()) * 4).expect("non-zero stride"),
        })
    }
}

#[async_trait::async_trait]
impl RdpServerDisplayUpdates for Updates {
    async fn next_update(&mut self) -> ServerResult<Option<DisplayUpdate>> {
        if !self.sent_full {
            self.sent_full = true;
            let size = *self.size.lock().unwrap();
            return Ok(Some(self.bitmap(0, 0, size.width, size.height, self.fill)));
        }
        tokio::select! {
            resized = self.resize_rx.recv() => {
                match resized {
                    Some(size) => {
                        self.sent_full = false;
                        Ok(Some(DisplayUpdate::Resize(size)))
                    }
                    None => Ok(None),
                }
            }
            _ = tokio::time::sleep(Duration::from_millis(400)) => {
                // A wandering block so a viewer can see updates flowing.
                let size = *self.size.lock().unwrap();
                self.tick = self.tick.wrapping_add(1);
                let block = 48u16;
                let slots_x = (size.width / block).max(1) as u32;
                let slots_y = (size.height / block).max(1) as u32;
                let slot = self.tick % (slots_x * slots_y);
                let x = (slot % slots_x) as u16 * block;
                let y = (slot / slots_x) as u16 * block;
                let color = if self.tick.is_multiple_of(2) { [0xF0, 0xF0, 0xF0, 0xFF] } else { [0x20, 0x20, 0xE0, 0xFF] };
                Ok(Some(self.bitmap(x, y, block.min(size.width - x), block.min(size.height - y), color)))
            }
        }
    }
}

/// Starts a server on a free loopback port using a throwaway self-signed
/// certificate and NLA (CredSSP) like a real Windows host.
pub async fn start(config: TestServerConfig) -> Result<TestServer, Box<dyn std::error::Error + Send + Sync>> {
    let addr = {
        let probe = TcpListener::bind("127.0.0.1:0")?;
        probe.local_addr()?
    };
    start_on(addr, config).await
}

/// Starts a server on a specific address.
pub async fn start_on(
    addr: SocketAddr,
    config: TestServerConfig,
) -> Result<TestServer, Box<dyn std::error::Error + Send + Sync>> {

    let tls_dir = tempfile::tempdir()?;
    let cert = rcgen::generate_simple_self_signed(vec!["localhost".to_owned(), "127.0.0.1".to_owned()])?;
    let cert_path = tls_dir.path().join("cert.pem");
    let key_path = tls_dir.path().join("key.pem");
    std::fs::write(&cert_path, cert.cert.pem())?;
    std::fs::write(&key_path, cert.signing_key.serialize_pem())?;
    let identity = TlsIdentityCtx::init_from_paths(&cert_path, &key_path)?;
    let acceptor = identity.make_acceptor()?;

    let observed = Arc::new(Mutex::new(Observed::default()));
    let handler = Handler {
        observed: observed.clone(),
    };
    let (resize_tx, resize_rx) = mpsc::unbounded_channel();
    let display = Display {
        size: Arc::new(Mutex::new(DesktopSize {
            width: config.width,
            height: config.height,
        })),
        fill: config.fill,
        observed: observed.clone(),
        resize_tx,
        resize_rx: Some(resize_rx),
    };

    // `RdpServer` is not `Send`, so it lives on its own thread with a
    // single-threaded runtime, like IronRDP's own example server.
    let (stop_tx, stop_rx) = tokio::sync::oneshot::channel::<()>();
    let pub_key = identity.pub_key.clone();
    let credentials = Credentials {
        username: config.username,
        password: config.password,
        domain: None,
    };
    let thread = std::thread::Builder::new()
        .name("rdp-test-server".into())
        .spawn(move || {
            let runtime = tokio::runtime::Builder::new_current_thread()
                .enable_all()
                .build()
                .expect("server runtime");
            runtime.block_on(async move {
                let mut server = RdpServer::builder()
                    .with_addr(addr)
                    .with_hybrid(acceptor, pub_key)
                    .with_input_handler(handler)
                    .with_display_handler(display)
                    .build();
                server.set_credentials(Some(credentials));
                tokio::select! {
                    result = server.run() => {
                        if let Err(e) = result {
                            eprintln!("rdp test server stopped: {e}");
                        }
                    }
                    _ = stop_rx => {}
                }
            });
        })?;

    // Give the listener a moment to bind.
    for _ in 0..50 {
        if tokio::net::TcpStream::connect(addr).await.is_ok() {
            break;
        }
        tokio::time::sleep(Duration::from_millis(20)).await;
    }

    Ok(TestServer {
        addr,
        observed,
        stop: Some(stop_tx),
        thread: Some(thread),
        _tls_dir: tls_dir,
    })
}
