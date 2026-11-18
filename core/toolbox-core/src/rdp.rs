//! Remote Desktop sessions over IronRDP.
//!
//! The session task owns the connection and the decoded framebuffer. Swift
//! learns about dirty rectangles through the listener and copies them out
//! with `copy_region`. Resizing goes through the Display Control channel and
//! falls back to reconnecting at the new size when the server lacks it.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use ironrdp::connector::connection_activation::ConnectionActivationState;
use ironrdp::connector::{self, ClientConnector, ConnectionResult, Credentials, DesktopSize};
use ironrdp::core::WriteBuf;
use ironrdp::displaycontrol::client::DisplayControlClient;
use ironrdp::dvc::DrdynvcClient;
use ironrdp::graphics::image_processing::PixelFormat;
use ironrdp::input::{Database, MouseButton, MousePosition, Operation, Scancode, WheelRotations};
use ironrdp::pdu::gcc::{ConnectionType, KeyboardType, RdpVersion};
use ironrdp::pdu::input::fast_path::FastPathInputEvent;
use ironrdp::pdu::rdp::capability_sets::{MajorPlatformType, RailSupportLevel};
use ironrdp::pdu::rdp::client_info::{CompressionType, PerformanceFlags, TimezoneInfo};
use ironrdp::session::image::DecodedImage;
use ironrdp::session::{ActiveStageBuilder, ActiveStageOutput};
use ironrdp_tls::CertificateValidation;
use ironrdp_tokio::reqwest::ReqwestNetworkClient;
use ironrdp_tokio::{
    connect_begin, connect_finalize, mark_as_upgraded, single_sequence_step_read, split_tokio_framed, FramedWrite as _,
    TokioFramed,
};
use tokio::io::{AsyncRead, AsyncWrite};
use tokio::net::TcpStream;
use tokio::sync::mpsc;

use crate::error::{lock_or_recover, CoreError};
use crate::runtime::RUNTIME;

#[derive(Clone, uniffi::Record)]
pub struct RdpConfig {
    pub host: String,
    pub port: u16,
    pub username: String,
    pub password: String,
    pub domain: Option<String>,
    /// Initial desktop size in pixels.
    pub width: u16,
    pub height: u16,
    /// Desktop scale percentage (100–500); 0 leaves it to the server.
    pub scale_factor: u32,
    pub accept_invalid_certs: bool,
    pub connect_timeout_secs: u32,
}

impl std::fmt::Debug for RdpConfig {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("RdpConfig")
            .field("host", &self.host)
            .field("port", &self.port)
            .field("username", &self.username)
            .field("password", &"<redacted>")
            .field("domain", &self.domain)
            .field("size", &(self.width, self.height))
            .field("scale_factor", &self.scale_factor)
            .field("accept_invalid_certs", &self.accept_invalid_certs)
            .finish()
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, uniffi::Enum)]
pub enum RdpMouseButton {
    Left,
    Middle,
    Right,
    X1,
    X2,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, uniffi::Record)]
pub struct FrameSize {
    pub width: u16,
    pub height: u16,
}

/// Implemented on the Swift side; invoked from core threads.
#[uniffi::export(callback_interface)]
pub trait RdpListener: Send + Sync {
    fn on_connected(&self, width: u16, height: u16);
    fn on_desktop_resized(&self, width: u16, height: u16);
    fn on_region_updated(&self, x: u16, y: u16, width: u16, height: u16);
    fn on_error(&self, message: String);
    /// `clean` is true when the server ended the session (sign-out,
    /// disconnect) or it was closed here; false after a failure.
    fn on_closed(&self, reason: String, clean: bool);
}

enum Cmd {
    Operations(Vec<Operation>),
    Resize { width: u16, height: u16, scale_factor: u32 },
    Close,
}

type SharedImage = Arc<Mutex<Option<DecodedImage>>>;

#[derive(uniffi::Object)]
pub struct RdpSession {
    tx: mpsc::UnboundedSender<Cmd>,
    image: SharedImage,
    closed: Arc<AtomicBool>,
}

#[uniffi::export]
impl RdpSession {
    /// Starts connecting immediately and returns. Progress arrives on `listener`.
    #[uniffi::constructor]
    pub fn connect(config: RdpConfig, listener: Box<dyn RdpListener>) -> Arc<Self> {
        let (tx, rx) = mpsc::unbounded_channel();
        let image: SharedImage = Arc::new(Mutex::new(None));
        let closed = Arc::new(AtomicBool::new(false));
        let listener: Arc<dyn RdpListener> = Arc::from(listener);
        RUNTIME.spawn(run_session(config, listener, rx, image.clone(), closed.clone()));
        Arc::new(Self { tx, image, closed })
    }

    pub fn frame_size(&self) -> FrameSize {
        match lock_or_recover(&self.image).as_ref() {
            Some(img) => FrameSize { width: img.width(), height: img.height() },
            None => FrameSize { width: 0, height: 0 },
        }
    }

    /// Pixels of a rectangle as tightly packed BGRA rows, clipped to the frame.
    pub fn copy_region(&self, x: u16, y: u16, width: u16, height: u16) -> Vec<u8> {
        let guard = lock_or_recover(&self.image);
        let Some(img) = guard.as_ref() else {
            return Vec::new();
        };
        let (frame_w, frame_h) = (usize::from(img.width()), usize::from(img.height()));
        let (x, y) = (usize::from(x), usize::from(y));
        if x >= frame_w || y >= frame_h {
            return Vec::new();
        }
        let w = usize::from(width).min(frame_w - x);
        let h = usize::from(height).min(frame_h - y);
        let stride = img.stride();
        let data = img.data();
        let mut out = Vec::with_capacity(w * h * 4);
        for row in y..y + h {
            let start = row * stride + x * 4;
            out.extend_from_slice(&data[start..start + w * 4]);
        }
        match img.pixel_format() {
            PixelFormat::BgrA32 | PixelFormat::BgrX32 => {}
            PixelFormat::RgbA32 | PixelFormat::RgbX32 => {
                for px in out.as_chunks_mut::<4>().0 {
                    px.swap(0, 2);
                }
            }
            PixelFormat::ARgb32 | PixelFormat::XRgb32 => {
                for px in out.as_chunks_mut::<4>().0 {
                    // A R G B -> B G R A
                    *px = [px[3], px[2], px[1], px[0]];
                }
            }
            PixelFormat::ABgr32 | PixelFormat::XBgr32 => {
                for px in out.as_chunks_mut::<4>().0 {
                    px.rotate_left(1);
                }
            }
        }
        out
    }

    pub fn mouse_move(&self, x: u16, y: u16) {
        self.send(vec![Operation::MouseMove(MousePosition { x, y })]);
    }

    pub fn mouse_button(&self, button: RdpMouseButton, pressed: bool) {
        let button = match button {
            RdpMouseButton::Left => MouseButton::Left,
            RdpMouseButton::Middle => MouseButton::Middle,
            RdpMouseButton::Right => MouseButton::Right,
            RdpMouseButton::X1 => MouseButton::X1,
            RdpMouseButton::X2 => MouseButton::X2,
        };
        self.send(vec![if pressed {
            Operation::MouseButtonPressed(button)
        } else {
            Operation::MouseButtonReleased(button)
        }]);
    }

    /// Wheel movement; 120 units is one notch.
    pub fn scroll(&self, vertical: bool, units: i16) {
        self.send(vec![Operation::WheelRotations(WheelRotations {
            is_vertical: vertical,
            rotation_units: units,
        })]);
    }

    /// A PC/AT set 1 scancode, `extended` for the E0-prefixed keys.
    pub fn key(&self, scancode: u8, extended: bool, pressed: bool) {
        let code = Scancode::from_u8(extended, scancode);
        self.send(vec![if pressed { Operation::KeyPressed(code) } else { Operation::KeyReleased(code) }]);
    }

    /// Text input for keys without a scancode.
    pub fn unicode(&self, code_point: u32, pressed: bool) {
        if let Some(ch) = char::from_u32(code_point) {
            self.send(vec![if pressed {
                Operation::UnicodeKeyPressed(ch)
            } else {
                Operation::UnicodeKeyReleased(ch)
            }]);
        }
    }

    /// Asks the server for a new desktop size.
    pub fn resize(&self, width: u16, height: u16, scale_factor: u32) {
        let _ = self.tx.send(Cmd::Resize { width, height, scale_factor });
    }

    pub fn close(&self) {
        let _ = self.tx.send(Cmd::Close);
    }

    pub fn is_closed(&self) -> bool {
        self.closed.load(Ordering::SeqCst)
    }
}

impl RdpSession {
    fn send(&self, ops: Vec<Operation>) {
        let _ = self.tx.send(Cmd::Operations(ops));
    }
}

enum Flow {
    Closed(String),
    Reconnect { width: u16, height: u16, scale_factor: u32 },
}

async fn run_session(
    config: RdpConfig,
    listener: Arc<dyn RdpListener>,
    mut rx: mpsc::UnboundedReceiver<Cmd>,
    image: SharedImage,
    closed: Arc<AtomicBool>,
) {
    let mut size = (config.width, config.height);
    let mut scale = config.scale_factor;
    let mut first = true;
    loop {
        match connect(&config, size, scale).await {
            Err(e) => {
                listener.on_error(e.to_string());
                listener.on_closed("connection failed".into(), false);
                break;
            }
            Ok((result, framed)) => {
                let desktop = result.desktop_size;
                *lock_or_recover(&image) = Some(DecodedImage::new(PixelFormat::BgrA32, desktop.width, desktop.height));
                if first {
                    listener.on_connected(desktop.width, desktop.height);
                    first = false;
                } else {
                    listener.on_desktop_resized(desktop.width, desktop.height);
                }
                match active_session(result, framed, &listener, &mut rx, &image).await {
                    Ok(Flow::Closed(reason)) => {
                        listener.on_closed(reason, true);
                        break;
                    }
                    Ok(Flow::Reconnect { width, height, scale_factor }) => {
                        log::info!("display control unavailable; reconnecting at {width}x{height}");
                        size = (width, height);
                        scale = scale_factor;
                    }
                    Err(e) => {
                        listener.on_error(e.to_string());
                        listener.on_closed("connection lost".into(), false);
                        break;
                    }
                }
            }
        }
    }
    closed.store(true, Ordering::SeqCst);
}

async fn connect(
    config: &RdpConfig,
    size: (u16, u16),
    scale: u32,
) -> Result<(ConnectionResult, TokioFramed<ironrdp_tls::TlsStream<TcpStream>>), CoreError> {
    if config.host.trim().is_empty() {
        return Err(CoreError::Invalid("host is required".into()));
    }
    let addr = format!("{}:{}", config.host.trim(), config.port);
    let timeout = Duration::from_secs(u64::from(config.connect_timeout_secs.max(1)));
    let tcp = tokio::time::timeout(timeout, TcpStream::connect(&addr))
        .await
        .map_err(|_| CoreError::Connect(format!("timed out connecting to {addr}")))?
        .map_err(|e| CoreError::Connect(format!("{addr}: {e}")))?;
    let _ = tcp.set_nodelay(true);
    let client_addr = tcp.local_addr().map_err(|e| CoreError::Connect(e.to_string()))?;

    let mut framed = TokioFramed::new(tcp);
    let mut connector = ClientConnector::new(build_config(config, size, scale), client_addr).with_static_channel(
        DrdynvcClient::new().with_dynamic_channel(DisplayControlClient::new(|_| Ok(Vec::new()))),
    );

    let should_upgrade = connect_begin(&mut framed, &mut connector)
        .await
        .map_err(|e| CoreError::Connect(format!("RDP negotiation with {addr} failed: {e}")))?;

    let initial = framed.into_inner_no_leftover();
    let validation = if config.accept_invalid_certs {
        CertificateValidation::DangerouslyAcceptInvalidCertificate
    } else {
        CertificateValidation::Strict
    };
    let (tls, certificate) = ironrdp_tls::upgrade_with_certificate_validation(initial, config.host.trim(), validation)
        .await
        .map_err(|e| CoreError::Connect(format!("TLS to {addr} failed: {e}")))?;
    let public_key = ironrdp_tls::extract_tls_server_public_key(&certificate)
        .ok_or_else(|| CoreError::Connect("server certificate has no usable public key".into()))?
        .to_vec();
    let upgraded = mark_as_upgraded(should_upgrade, &mut connector);

    let mut framed = TokioFramed::new(tls);
    let mut network = ReqwestNetworkClient::new();
    let outcome = connect_finalize(
        upgraded,
        connector,
        &mut framed,
        &mut network,
        config.host.trim().to_owned().into(),
        public_key,
        None,
    )
    .await
    .map_err(|e| {
        let text = e.to_string();
        if text.contains("CredSSP") || text.contains("credential") || text.contains("logon") {
            CoreError::Auth(format!("RDP logon to {addr} failed: {text}"))
        } else {
            CoreError::Connect(format!("RDP connection to {addr} failed: {text}"))
        }
    })?;
    let result = outcome
        .into_connected()
        .map_err(|e| CoreError::Connect(format!("RDP connection to {addr} was redirected: {e}")))?;
    Ok((result, framed))
}

fn build_config(config: &RdpConfig, size: (u16, u16), scale: u32) -> connector::Config {
    connector::Config {
        credentials: Credentials::UsernamePassword {
            username: config.username.clone(),
            password: config.password.clone(),
        },
        domain: config.domain.clone().filter(|d| !d.trim().is_empty()),
        enable_tls: true,
        enable_credssp: true,
        enable_standard_rdp_security: false,
        keyboard_type: KeyboardType::IBM_ENHANCED,
        keyboard_subtype: 0,
        keyboard_layout: 0,
        keyboard_functional_keys_count: 12,
        connection_type: ConnectionType::Lan,
        ime_file_name: String::new(),
        dig_product_id: String::new(),
        desktop_size: DesktopSize { width: size.0, height: size.1 },
        monitor_layout: None,
        bitmap: None,
        client_build: 0,
        client_name: "Toolbox".to_owned(),
        client_dir: "C:\\Windows\\System32\\mstscax.dll".to_owned(),
        platform: MajorPlatformType::MACINTOSH,
        // The server draws the pointer into the frame; no client-side cursor yet.
        enable_server_pointer: false,
        request_data: None,
        autologon: false,
        enable_audio_playback: false,
        enable_audio_capture: false,
        compression_type: Some(CompressionType::Rdp61),
        pointer_software_rendering: true,
        multitransport_flags: None,
        support_dyn_vc_gfx_protocol: false,
        rdp_version: RdpVersion::V5_PLUS,
        performance_flags: PerformanceFlags::default(),
        desktop_scale_factor: if (100..=500).contains(&scale) { scale } else { 0 },
        desktop_physical_size: None,
        desktop_orientation: None,
        device_scale_factor: None,
        monitor_extended_layout: None,
        relative_mouse_input: false,
        hardware_id: None,
        license_cache: None,
        timezone_info: TimezoneInfo::default(),
        alternate_shell: String::new(),
        work_dir: String::new(),
        remote_application_mode: false,
        rail_support_level: RailSupportLevel::empty(),
    }
}

/// How long to wait for Display Control capabilities before giving up on an
/// in-band resize and reconnecting instead.
const DISPLAY_CONTROL_WAIT: Duration = Duration::from_secs(3);

async fn active_session<S>(
    connection_result: ConnectionResult,
    framed: TokioFramed<S>,
    listener: &Arc<dyn RdpListener>,
    rx: &mut mpsc::UnboundedReceiver<Cmd>,
    image: &SharedImage,
) -> Result<Flow, CoreError>
where
    S: AsyncRead + AsyncWrite + Unpin + Send + Sync + 'static,
{
    let (mut reader, mut writer) = split_tokio_framed(framed);
    let activation_factory = connection_result.activation_factory.clone();
    let mut active_stage = ActiveStageBuilder {
        static_channels: connection_result.static_channels,
        user_channel_id: connection_result.user_channel_id,
        io_channel_id: connection_result.io_channel_id,
        message_channel_id: connection_result.message_channel_id,
        share_id: connection_result.share_id,
        compression_type: connection_result.compression_type,
        enable_server_pointer: connection_result.enable_server_pointer,
        pointer_software_rendering: connection_result.pointer_software_rendering,
    }
    .build();
    let mut input = Database::new();
    let mut pending_resize: Option<(u16, u16, u32, Instant)> = None;

    loop {
        enum Event {
            Frame(std::io::Result<(ironrdp::pdu::Action, bytes::BytesMut)>),
            Cmd(Option<Cmd>),
        }
        let event = tokio::select! {
            frame = reader.read_pdu() => Event::Frame(frame),
            cmd = rx.recv() => Event::Cmd(cmd),
        };

        let outputs = match event {
            Event::Frame(Err(e)) => return Err(CoreError::Connect(format!("connection lost: {e}"))),
            Event::Frame(Ok((action, payload))) => {
                let mut guard = lock_or_recover(image);
                let img = guard.as_mut().ok_or_else(|| CoreError::Connect("no framebuffer".into()))?;
                active_stage
                    .process(img, action, &payload, reader.last_read_at())
                    .map_err(|e| CoreError::Connect(format!("session error: {e}")))?
            }
            Event::Cmd(None) | Event::Cmd(Some(Cmd::Close)) => {
                if let Ok(outputs) = active_stage.graceful_shutdown() {
                    for output in outputs {
                        if let ActiveStageOutput::ResponseFrame(frame) = output {
                            let _ = writer.write_all(&frame).await;
                        }
                    }
                }
                return Ok(Flow::Closed("closed".into()));
            }
            Event::Cmd(Some(Cmd::Operations(ops))) => {
                let events: Vec<FastPathInputEvent> = input.apply(ops).into_iter().collect();
                if events.is_empty() {
                    Vec::new()
                } else {
                    let mut guard = lock_or_recover(image);
                    let img = guard.as_mut().ok_or_else(|| CoreError::Connect("no framebuffer".into()))?;
                    active_stage
                        .process_fastpath_input(img, &events)
                        .map_err(|e| CoreError::Connect(format!("input error: {e}")))?
                }
            }
            Event::Cmd(Some(Cmd::Resize { width, height, scale_factor })) => {
                pending_resize = Some((width, height, scale_factor, Instant::now()));
                Vec::new()
            }
        };

        for output in outputs {
            match output {
                ActiveStageOutput::ResponseFrame(frame) => writer
                    .write_all(&frame)
                    .await
                    .map_err(|e| CoreError::Connect(format!("write failed: {e}")))?,
                ActiveStageOutput::GraphicsUpdate(region) => {
                    let width = region.right.saturating_sub(region.left).saturating_add(1);
                    let height = region.bottom.saturating_sub(region.top).saturating_add(1);
                    listener.on_region_updated(region.left, region.top, width, height);
                }
                ActiveStageOutput::Terminate(reason) => return Ok(Flow::Closed(reason.to_string())),
                ActiveStageOutput::DeactivateAll => {
                    let mut activation = activation_factory.create();
                    let mut buf = WriteBuf::new();
                    loop {
                        let written = single_sequence_step_read(&mut reader, &mut activation, &mut buf)
                            .await
                            .map_err(|e| CoreError::Connect(format!("reactivation failed: {e}")))?;
                        if written.size().is_some() {
                            writer
                                .write_all(buf.filled())
                                .await
                                .map_err(|e| CoreError::Connect(format!("write failed: {e}")))?;
                        }
                        if let ConnectionActivationState::Finalized {
                            desktop_size,
                            share_id,
                            enable_server_pointer,
                            pointer_software_rendering,
                            static_channel_chunk_size,
                            window_support_level,
                            ..
                        } = activation.connection_activation_state()
                        {
                            *lock_or_recover(image) =
                                Some(DecodedImage::new(PixelFormat::BgrA32, desktop_size.width, desktop_size.height));
                            if !active_stage.reactivate(
                                activation.io_channel_id(),
                                activation.user_channel_id(),
                                share_id,
                                enable_server_pointer,
                                pointer_software_rendering,
                                static_channel_chunk_size,
                            ) {
                                return Err(CoreError::Connect("server proposed an invalid channel chunk size".into()));
                            }
                            active_stage.set_window_support_level(window_support_level);
                            pending_resize = None;
                            listener.on_desktop_resized(desktop_size.width, desktop_size.height);
                            break;
                        }
                    }
                }
                _ => {}
            }
        }

        if let Some((width, height, scale_factor, since)) = pending_resize {
            match active_stage.display_control_ready() {
                Some(true) => {
                    let messages = {
                        let dvc = active_stage
                            .get_dvc::<DisplayControlClient>()
                            .ok_or_else(|| CoreError::Connect("display control channel vanished".into()))?;
                        let scale = if (100..=500).contains(&scale_factor) { Some(scale_factor) } else { None };
                        dvc.processor()
                            .encode_single_primary_monitor(
                                dvc.channel_id(),
                                u32::from(width),
                                u32::from(height),
                                scale,
                                None,
                            )
                            .map_err(|e| CoreError::Connect(format!("resize request failed: {e}")))?
                    };
                    let frame = active_stage
                        .encode_dvc_messages(messages)
                        .map_err(|e| CoreError::Connect(format!("resize request failed: {e}")))?;
                    writer
                        .write_all(&frame)
                        .await
                        .map_err(|e| CoreError::Connect(format!("write failed: {e}")))?;
                    // The server answers with a deactivation-reactivation; clear now so a
                    // second request during it is queued fresh.
                    pending_resize = None;
                }
                Some(false) if since.elapsed() < DISPLAY_CONTROL_WAIT => {}
                _ => return Ok(Flow::Reconnect { width, height, scale_factor }),
            }
        }
    }
}
