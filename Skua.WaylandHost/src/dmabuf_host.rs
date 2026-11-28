use std::io::{Read, Seek, Write};
use std::os::fd::{AsFd, AsRawFd, FromRawFd, OwnedFd, RawFd};
use std::os::unix::fs::MetadataExt;
use std::path::PathBuf;
use std::process::Command;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::mpsc::{self, Receiver, Sender};
use std::sync::{Arc, Mutex, OnceLock};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};
use uuid::Uuid;
use wayland_protocols::wp::cursor_shape::v1::server::{
    wp_cursor_shape_device_v1::{self, Shape as CursorShape, WpCursorShapeDeviceV1},
    wp_cursor_shape_manager_v1::{self, WpCursorShapeManagerV1},
};
use wayland_protocols::wp::linux_dmabuf::zv1::server::{
    zwp_linux_buffer_params_v1::{self, ZwpLinuxBufferParamsV1},
    zwp_linux_dmabuf_feedback_v1::{self, TrancheFlags, ZwpLinuxDmabufFeedbackV1},
    zwp_linux_dmabuf_v1::{self, ZwpLinuxDmabufV1},
};
use wayland_protocols::xdg::shell::server::{
    xdg_surface::{self, XdgSurface},
    xdg_toplevel::{self, XdgToplevel},
    xdg_wm_base::{self, XdgWmBase},
};
use wayland_server::{
    backend::{ClientData, ClientId, DisconnectReason},
    protocol::{
        wl_buffer::{self, WlBuffer},
        wl_callback::{self, WlCallback},
        wl_compositor::{self, WlCompositor},
        wl_data_device::{self, WlDataDevice},
        wl_data_device_manager::{self, WlDataDeviceManager},
        wl_data_offer::{self, WlDataOffer},
        wl_data_source::{self, WlDataSource},
        wl_keyboard::{self, KeyState, KeymapFormat, WlKeyboard},
        wl_output::{self, Mode, Subpixel, WlOutput},
        wl_pointer::{self, Axis, AxisSource, ButtonState, WlPointer},
        wl_region::{self, WlRegion},
        wl_seat::{self, Capability, WlSeat},
        wl_shm::{self, Format, WlShm},
        wl_shm_pool::{self, WlShmPool},
        wl_surface::{self, WlSurface},
    },
    Client, DataInit, Dispatch, Display, DisplayHandle, GlobalDispatch, ListeningSocket, New,
    Resource, WEnum,
};

macro_rules! warn { ($($t:tt)*) => { eprintln!($($t)*); }; }

#[derive(Debug, Clone)]
pub struct DmabufPlane {
    pub fd: Arc<OwnedFd>,
    pub offset: u32,
    pub stride: u32,
    pub modifier: u64,
}

static NEXT_BUFFER_ID: AtomicU64 = AtomicU64::new(1);

#[derive(Debug, Clone)]
pub struct DmabufFrame {
    pub id: u64,
    pub width: u32,
    pub height: u32,
    pub fourcc: u32,
    pub planes: Vec<DmabufPlane>,
}

#[derive(Debug, Clone)]
pub struct PublishedDmabuf {
    pub serial: u64,
    pub frame: Arc<DmabufFrame>,
}

#[derive(Debug, Clone)]
pub enum DmabufInput {
    PointerMotion { x: f64, y: f64 },
    PointerButton { button: u32, pressed: bool },
    PointerScroll { dx: f64, dy: f64 },
    Key { evdev_code: u32, pressed: bool },
    Resize { width: u32, height: u32 },
    SetRefreshMhz { mhz: u32 },
    SetUncapped { uncapped: bool },
    SetInterpolation { enabled: bool },
    SetVisible { visible: bool },
    SetClipboard { text: String },
}

pub type DmabufNotifyFn = extern "C" fn(usize);
pub type ClipboardCopyFn = extern "C" fn(usize, *const u8, usize);
pub type CursorShapeFn = extern "C" fn(usize, u32);

pub struct DmabufHost {
    pub socket_name: String,
    pub socket_path: PathBuf,
    pub frame: Arc<Mutex<Option<PublishedDmabuf>>>,
    pub frame_seq: Arc<AtomicU64>,
    on_frame: Arc<Mutex<Option<(DmabufNotifyFn, usize)>>>,
    on_clipboard: Arc<Mutex<Option<(ClipboardCopyFn, usize)>>>,
    on_cursor_shape: Arc<Mutex<Option<(CursorShapeFn, usize)>>>,
    input_tx: Sender<DmabufInput>,
    acquired_serial: Arc<AtomicU64>,
    presented_serial: Arc<AtomicU64>,
    wake_fd: RawFd,
    stop: Arc<AtomicBool>,
    thread: Option<JoinHandle<()>>,
}

impl DmabufHost {
    pub fn start(session_id: Uuid, width: u32, height: u32) -> anyhow::Result<Self> {
        let socket_name = format!("beyond-wl-{}", session_id.simple());
        let runtime_dir = std::env::var_os("XDG_RUNTIME_DIR")
            .map(PathBuf::from)
            .unwrap_or_else(std::env::temp_dir);
        let socket_path = runtime_dir.join(&socket_name);
        if socket_path.exists() {
            let _ = std::fs::remove_file(&socket_path);
        }

        let listener = ListeningSocket::bind(&socket_name)?;
        let frame = Arc::new(Mutex::new(None));
        let frame_clone = Arc::clone(&frame);
        let frame_seq = Arc::new(AtomicU64::new(0));
        let frame_seq_clone = Arc::clone(&frame_seq);
        let on_frame = Arc::new(Mutex::new(None));
        let on_frame_clone = Arc::clone(&on_frame);
        let on_clipboard = Arc::new(Mutex::new(None));
        let on_clipboard_clone = Arc::clone(&on_clipboard);
        let on_cursor_shape = Arc::new(Mutex::new(None));
        let on_cursor_shape_clone = Arc::clone(&on_cursor_shape);
        let (input_tx, input_rx) = mpsc::channel();
        let acquired_serial = Arc::new(AtomicU64::new(0));
        let acquired_clone = Arc::clone(&acquired_serial);
        let presented_serial = Arc::new(AtomicU64::new(0));
        let presented_clone = Arc::clone(&presented_serial);
        let wake_fd = unsafe { libc::eventfd(0, libc::EFD_NONBLOCK | libc::EFD_CLOEXEC) };
        let stop = Arc::new(AtomicBool::new(false));
        let stop_clone = Arc::clone(&stop);

        let thread = std::thread::Builder::new()
            .name(format!("beyond-dma-{}", &session_id.simple().to_string()[..8]))
            .spawn(move || {
                run_dmabuf_loop(
                    listener,
                    frame_clone,
                    frame_seq_clone,
                    on_frame_clone,
                    on_clipboard_clone,
                    on_cursor_shape_clone,
                    input_rx,
                    acquired_clone,
                    presented_clone,
                    wake_fd,
                    stop_clone,
                    width,
                    height,
                );
            })?;

        Ok(Self {
            socket_name,
            socket_path,
            frame,
            frame_seq,
            on_frame,
            on_clipboard,
            on_cursor_shape,
            input_tx,
            acquired_serial,
            presented_serial,
            wake_fd,
            stop,
            thread: Some(thread),
        })
    }

    pub fn set_frame_callback(&self, cb: Option<(DmabufNotifyFn, usize)>) {
        if let Ok(mut guard) = self.on_frame.lock() {
            *guard = cb;
        }
    }

    pub fn set_clipboard_callback(&self, cb: Option<(ClipboardCopyFn, usize)>) {
        if let Ok(mut guard) = self.on_clipboard.lock() {
            *guard = cb;
        }
    }

    pub fn set_cursor_shape_callback(&self, cb: Option<(CursorShapeFn, usize)>) {
        if let Ok(mut guard) = self.on_cursor_shape.lock() {
            *guard = cb;
        }
    }

    pub fn send_input(&self, ev: DmabufInput) {
        let _ = self.input_tx.send(ev);
        self.wake_loop();
    }

    pub fn notify_frame_acquired(&self, serial: u64) {
        if serial > 0 {
            let prev = self.acquired_serial.fetch_max(serial, Ordering::AcqRel);
            if serial > prev {
                self.wake_loop();
            }
        }
    }

    pub fn notify_presented_seq(&self, serial: u64) {
        if serial > 0 {
            let prev = self.presented_serial.fetch_max(serial, Ordering::AcqRel);
            if serial > prev {
                self.wake_loop();
            }
        }
    }

    pub fn notify_presented(&self) {
        let seq = self.frame_seq.load(Ordering::Acquire);
        if seq > 0 {
            self.presented_serial.fetch_max(seq, Ordering::AcqRel);
        }
        self.wake_loop();
    }

    fn wake_loop(&self) {
        if self.wake_fd >= 0 {
            let val: u64 = 1;
            unsafe {
                libc::write(self.wake_fd, &val as *const u64 as *const libc::c_void, 8);
            }
        }
    }

    pub fn latest_frame(&self) -> Option<PublishedDmabuf> {
        self.frame.lock().ok().and_then(|g| g.clone())
    }
}

impl Drop for DmabufHost {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Relaxed);
        self.wake_loop();
        if let Some(t) = self.thread.take() {
            let _ = t.join();
        }
        if self.wake_fd >= 0 {
            unsafe {
                libc::close(self.wake_fd);
            }
            self.wake_fd = -1;
        }
        let _ = std::fs::remove_file(&self.socket_path);
    }
}

struct ClientState;
impl ClientData for ClientState {
    fn initialized(&self, _client_id: ClientId) {}
    fn disconnected(&self, _client_id: ClientId, _reason: DisconnectReason) {}
}

enum DmabufBufferKind {
    CursorOnly,
    Dmabuf(Arc<DmabufFrame>),
}

#[derive(Default)]
struct DmabufParamsData {
    planes: Vec<(u32, DmabufPlane)>,
}

struct SurfaceData {
    pending_buffer: Option<WlBuffer>,
    pending_callbacks: Vec<WlCallback>,
}

#[derive(Default)]
struct DataSourceData {
    mime_types: Mutex<Vec<String>>,
}

struct DmabufState {
    frame: Arc<Mutex<Option<PublishedDmabuf>>>,
    frame_seq: Arc<AtomicU64>,
    frame_serial: u64,
    on_frame: Arc<Mutex<Option<(DmabufNotifyFn, usize)>>>,
    on_clipboard: Arc<Mutex<Option<(ClipboardCopyFn, usize)>>>,
    on_cursor_shape: Arc<Mutex<Option<(CursorShapeFn, usize)>>>,
    last_cursor_shape: u32,
    start_time: Instant,
    desired_width: i32,
    desired_height: i32,
    configured_width: i32,
    configured_height: i32,
    main_surface: Option<WlSurface>,
    toplevel: Option<XdgToplevel>,
    xdg_surface: Option<XdgSurface>,
    pointers: Vec<WlPointer>,
    keyboards: Vec<WlKeyboard>,
    data_devices: Vec<WlDataDevice>,
    clipboard_text: Option<String>,
    outputs: Vec<WlOutput>,
    serial_counter: u32,
    pointer_entered: bool,
    keyboard_entered: bool,
    keyboard_mods: u32,
    displayed_buffer: Option<WlBuffer>,
    staged_buffer: Option<(u64, WlBuffer)>,
    refresh_mhz: u32,
    uncapped: bool,
    interpolation: bool,
    visible: bool,
    hidden_since: Option<Instant>,
    hidden_shrunk: bool,
    uncapped_path: Option<PathBuf>,
    interpolation_path: Option<PathBuf>,
    last_uncapped_check: Instant,
    pending_frame_callbacks: Vec<WlCallback>,
    pending_cb_since: Option<Instant>,
    last_cb_flush: Instant,
}

const MIN_SURFACE_W: i32 = 160;
const MIN_SURFACE_H: i32 = 92;
const HIDDEN_SHRINK_DELAY: Duration = Duration::from_secs(3);

impl DmabufState {
    fn next_serial(&mut self) -> u32 {
        self.serial_counter = self.serial_counter.wrapping_add(1);
        self.serial_counter
    }

    fn time_ms(&self) -> u32 {
        self.start_time.elapsed().as_millis() as u32
    }

    fn notify_cursor_shape(&mut self, shape: u32) {
        if self.last_cursor_shape == shape {
            return;
        }
        self.last_cursor_shape = shape;
        if let Ok(cb_opt) = self.on_cursor_shape.lock() {
            if let Some((cb, user_data)) = *cb_opt {
                cb(user_data, shape);
            }
        }
    }

    fn send_clipboard_offer_to(&self, dd: &WlDataDevice, dh: &DisplayHandle, text: &str) {
        let Ok(client) = dh.get_client(dd.id()) else {
            return;
        };
        if let Ok(offer) = client.create_resource::<WlDataOffer, String, DmabufState>(
            dh,
            dd.version(),
            text.to_string(),
        ) {
            dd.data_offer(&offer);
            offer.offer("text/plain;charset=utf-8".to_string());
            offer.offer("text/plain".to_string());
            offer.offer("UTF8_STRING".to_string());
            dd.selection(Some(&offer));
        }
    }

    fn send_configure_size(&mut self, w: i32, h: i32) {
        if w != self.configured_width || h != self.configured_height {
            self.configured_width = w;
            self.configured_height = h;
            for out in &self.outputs {
                out.mode(Mode::Current | Mode::Preferred, w, h, self.refresh_mhz as i32);
                out.done();
            }
            if let (Some(tl), Some(xs)) = (self.toplevel.clone(), self.xdg_surface.clone()) {
                let serial = self.next_serial();
                tl.configure(w, h, vec![4, 0, 0, 0]);
                xs.configure(serial);
            }
        }
    }

    // Mugetsu's uncapped and FPS-limit settings only apply when Frame Interpolation is enabled.
    // With interpolation off, the game runs at native 24 FPS and wl_surface.frame MUST stay
    // presentation-synced even if mugetsu-uncapped.txt is "on".
    fn effective_uncapped(&self) -> bool {
        self.uncapped && self.interpolation
    }

    fn flush_frame_callbacks(&mut self) {
        if self.pending_frame_callbacks.is_empty() {
            self.pending_cb_since = None;
            return;
        }
        let t = self.time_ms();
        for cb in self.pending_frame_callbacks.drain(..) {
            cb.done(t);
        }
        self.pending_cb_since = None;
        self.last_cb_flush = Instant::now();
    }

    fn promote_acquired_buffer(&mut self, acq: u64) {
        if acq > 0
            && self
                .staged_buffer
                .as_ref()
                .is_some_and(|(seq, _)| *seq <= acq)
        {
            if let Some((_, new_disp)) = self.staged_buffer.take() {
                if let Some(old_disp) = self.displayed_buffer.replace(new_disp) {
                    old_disp.release();
                }
            }
        }
    }
}

fn read_uncapped_file(path: &Option<PathBuf>) -> bool {
    path.as_ref()
        .and_then(|p| std::fs::read_to_string(p).ok())
        .is_some_and(|s| s.trim().eq_ignore_ascii_case("on"))
}

fn read_interpolation_file(path: &Option<PathBuf>) -> bool {
    path.as_ref().is_some_and(|p| p.exists())
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum FrameCbDecision {
    FlushNow,
    WaitMs(i32),
}

// Tracks monotonic u64 `presented_serial >= frame_serial` (NOT a single AtomicBool, which can
// allow a stale presentation ack to flush a newer unpresented frame's callback) and enforces a
// 75% refresh-period floor (`750_000_000 / refresh_mhz`) to tolerate compositor vsync jitter
// without dropping frame cadence.
#[allow(clippy::too_many_arguments)]
fn decide_frame_cb_action(
    has_pending_cbs: bool,
    visible: bool,
    uncapped: bool,
    refresh_mhz: u32,
    frame_serial: u64,
    presented_serial: u64,
    elapsed_since_flush: Duration,
    elapsed_since_pending: Duration,
) -> FrameCbDecision {
    if !has_pending_cbs {
        return FrameCbDecision::WaitMs(250);
    }

    if !visible {
        const HIDDEN_CB_INTERVAL: Duration = Duration::from_millis(50);
        if elapsed_since_pending >= HIDDEN_CB_INTERVAL {
            return FrameCbDecision::FlushNow;
        }
        let rem = HIDDEN_CB_INTERVAL.saturating_sub(elapsed_since_pending);
        return FrameCbDecision::WaitMs((rem.as_millis() as i32).clamp(1, 50));
    }

    if uncapped {
        return FrameCbDecision::FlushNow;
    }

    let clamped_mhz = u64::from(refresh_mhz.max(24_000));
    let min_cb_interval = Duration::from_micros((750_000_000u64 / clamped_mhz).max(1000));
    let fallback_timeout = Duration::from_millis((2_500_000u64 / clamped_mhz).clamp(40, 100));
    let is_presented = presented_serial >= frame_serial;

    if (is_presented && elapsed_since_flush >= min_cb_interval)
        || elapsed_since_pending >= fallback_timeout
    {
        return FrameCbDecision::FlushNow;
    }

    if is_presented {
        let rem = min_cb_interval.saturating_sub(elapsed_since_flush);
        return FrameCbDecision::WaitMs((rem.as_millis() as i32).clamp(1, 50));
    }

    let rem = fallback_timeout.saturating_sub(elapsed_since_pending);
    FrameCbDecision::WaitMs((rem.as_millis() as i32).clamp(1, 50))
}

#[allow(clippy::too_many_arguments)]
fn run_dmabuf_loop(
    listener: ListeningSocket,
    frame: Arc<Mutex<Option<PublishedDmabuf>>>,
    frame_seq: Arc<AtomicU64>,
    on_frame: Arc<Mutex<Option<(DmabufNotifyFn, usize)>>>,
    on_clipboard: Arc<Mutex<Option<(ClipboardCopyFn, usize)>>>,
    on_cursor_shape: Arc<Mutex<Option<(CursorShapeFn, usize)>>>,
    input_rx: Receiver<DmabufInput>,
    acquired_serial: Arc<AtomicU64>,
    presented_serial: Arc<AtomicU64>,
    wake_fd: RawFd,
    stop: Arc<AtomicBool>,
    width: u32,
    height: u32,
) {
    let Ok(mut display) = Display::<DmabufState>::new() else {
        warn!("Failed to create Wayland DMA-BUF Display");
        return;
    };
    let dh = display.handle();
    dh.create_global::<DmabufState, WlCompositor, _>(4, ());
    dh.create_global::<DmabufState, WlShm, _>(1, ());
    dh.create_global::<DmabufState, ZwpLinuxDmabufV1, _>(4, ());
    dh.create_global::<DmabufState, XdgWmBase, _>(2, ());
    dh.create_global::<DmabufState, WlSeat, _>(5, ());
    dh.create_global::<DmabufState, WlOutput, _>(3, ());
    dh.create_global::<DmabufState, WlDataDeviceManager, _>(3, ());
    dh.create_global::<DmabufState, WpCursorShapeManagerV1, _>(1, ());

    let ruffle_config_dir = std::env::var_os("XDG_CONFIG_HOME")
        .filter(|v| !v.is_empty())
        .map(PathBuf::from)
        .or_else(|| std::env::var_os("HOME").map(|h| PathBuf::from(h).join(".config")))
        .map(|d| d.join("ruffle"));
    let uncapped_path = ruffle_config_dir
        .as_ref()
        .map(|d| d.join("mugetsu-uncapped.txt"));
    let interpolation_path = ruffle_config_dir
        .as_ref()
        .map(|d| d.join("mugetsu-interpolation.txt"));
    let initial_uncapped = read_uncapped_file(&uncapped_path);
    let initial_interpolation = read_interpolation_file(&interpolation_path);

    let mut state = DmabufState {
        frame,
        frame_seq,
        frame_serial: 0,
        on_frame,
        on_clipboard,
        on_cursor_shape,
        last_cursor_shape: 1,
        start_time: Instant::now(),
        desired_width: width as i32,
        desired_height: height as i32,
        configured_width: width as i32,
        configured_height: height as i32,
        main_surface: None,
        toplevel: None,
        xdg_surface: None,
        pointers: Vec::new(),
        keyboards: Vec::new(),
        data_devices: Vec::new(),
        clipboard_text: None,
        outputs: Vec::new(),
        serial_counter: 1,
        pointer_entered: false,
        keyboard_entered: false,
        keyboard_mods: 0,
        displayed_buffer: None,
        staged_buffer: None,
        refresh_mhz: 60_000,
        uncapped: initial_uncapped,
        interpolation: initial_interpolation,
        visible: true,
        hidden_since: None,
        hidden_shrunk: false,
        uncapped_path,
        interpolation_path,
        last_uncapped_check: Instant::now(),
        pending_frame_callbacks: Vec::new(),
        pending_cb_since: None,
        last_cb_flush: Instant::now(),
    };

    while !stop.load(Ordering::Relaxed) {
        while let Ok(Some(stream)) = listener.accept() {
            let _ = display
                .handle()
                .insert_client(stream, Arc::new(ClientState));
        }

        while let Ok(ev) = input_rx.try_recv() {
            handle_input(&mut state, &dh, ev);
        }

        let _ = display.dispatch_clients(&mut state);
        state.promote_acquired_buffer(acquired_serial.swap(0, Ordering::AcqRel));
        if state.last_uncapped_check.elapsed() >= Duration::from_millis(250) {
            state.last_uncapped_check = Instant::now();
            let was_effective_uncapped = state.effective_uncapped();
            state.uncapped = read_uncapped_file(&state.uncapped_path);
            state.interpolation = read_interpolation_file(&state.interpolation_path);
            if !was_effective_uncapped && state.effective_uncapped() && state.visible {
                state.flush_frame_callbacks();
            }
        }

        if !state.visible
            && !state.hidden_shrunk
            && state
                .hidden_since
                .is_some_and(|t| t.elapsed() >= HIDDEN_SHRINK_DELAY)
        {
            state.hidden_shrunk = true;
            state.send_configure_size(MIN_SURFACE_W, MIN_SURFACE_H);
            if let Ok(mut g) = state.frame.lock() {
                *g = None;
            }
            if let Some(old_disp) = state.displayed_buffer.take() {
                old_disp.release();
            }
            if let Some((_, old_staged)) = state.staged_buffer.take() {
                old_staged.release();
            }
            state.flush_frame_callbacks();
        }

        let elapsed_since_flush = state.last_cb_flush.elapsed();
        let elapsed_since_pending = state
            .pending_cb_since
            .map_or(Duration::ZERO, |s| s.elapsed());
        let mut poll_timeout_ms = match decide_frame_cb_action(
            !state.pending_frame_callbacks.is_empty(),
            state.visible,
            state.effective_uncapped(),
            state.refresh_mhz,
            state.frame_serial,
            presented_serial.load(Ordering::Acquire),
            elapsed_since_flush,
            elapsed_since_pending,
        ) {
            FrameCbDecision::FlushNow => {
                state.flush_frame_callbacks();
                250
            }
            FrameCbDecision::WaitMs(ms) => ms,
        };
        if !state.visible && !state.hidden_shrunk {
            if let Some(since) = state.hidden_since {
                let rem = HIDDEN_SHRINK_DELAY.saturating_sub(since.elapsed());
                let rem_ms = (rem.as_millis() as i32).clamp(1, 1000);
                poll_timeout_ms = poll_timeout_ms.min(rem_ms);
            }
        }

        let _ = display.flush_clients();
        let mut pfds = [
            libc::pollfd {
                fd: display.backend().poll_fd().as_raw_fd(),
                events: libc::POLLIN,
                revents: 0,
            },
            libc::pollfd {
                fd: wake_fd,
                events: libc::POLLIN,
                revents: 0,
            },
        ];
        let nfds = if wake_fd >= 0 { 2 } else { 1 };
        unsafe {
            libc::poll(pfds.as_mut_ptr(), nfds, poll_timeout_ms);
            if wake_fd >= 0 && pfds[1].revents != 0 {
                let mut val = 0u64;
                libc::read(
                    wake_fd,
                    &mut val as *mut u64 as *mut libc::c_void,
                    8,
                );
            }
        }
    }
}

fn handle_input(state: &mut DmabufState, dh: &DisplayHandle, ev: DmabufInput) {
    let time = state.time_ms();
    match ev {
        DmabufInput::PointerMotion { x, y } => {
            if let Some(surface) = state.main_surface.clone() {
                if !state.pointer_entered {
                    let serial = state.next_serial();
                    for ptr in &state.pointers {
                        ptr.enter(serial, &surface, x, y);
                        ptr.frame();
                    }
                    state.pointer_entered = true;
                }
                for ptr in &state.pointers {
                    ptr.motion(time, x, y);
                    ptr.frame();
                }
            }
        }
        DmabufInput::PointerButton { button, pressed } => {
            let serial = state.next_serial();
            if pressed {
                if let Some(surface) = state.main_surface.clone() {
                    for kb in &state.keyboards {
                        kb.enter(serial, &surface, vec![]);
                    }
                }
            }
            let bstate = if pressed {
                ButtonState::Pressed
            } else {
                ButtonState::Released
            };
            for ptr in &state.pointers {
                ptr.button(serial, time, button, bstate);
                ptr.frame();
            }
        }
        DmabufInput::PointerScroll { dx, dy } => {
            if !state.pointer_entered {
                if let Some(surface) = state.main_surface.clone() {
                    let serial = state.next_serial();
                    for ptr in &state.pointers {
                        ptr.enter(serial, &surface, 0.0, 0.0);
                        ptr.frame();
                    }
                    state.pointer_entered = true;
                }
            }
            let discrete_y = if dy.abs() > f64::EPSILON {
                let steps = (-dy / 15.0).round() as i32;
                if steps != 0 { steps } else if dy > 0.0 { -1 } else { 1 }
            } else {
                0
            };
            let discrete_x = if dx.abs() > f64::EPSILON {
                let steps = (dx / 15.0).round() as i32;
                if steps != 0 { steps } else if dx > 0.0 { 1 } else { -1 }
            } else {
                0
            };
            for ptr in &state.pointers {
                let has_v5 = ptr.version() >= 5;
                if has_v5 {
                    ptr.axis_source(AxisSource::Wheel);
                }
                if dy.abs() > f64::EPSILON {
                    if has_v5 {
                        ptr.axis_discrete(Axis::VerticalScroll, discrete_y);
                    }
                    ptr.axis(time, Axis::VerticalScroll, -dy);
                }
                if dx.abs() > f64::EPSILON {
                    if has_v5 {
                        ptr.axis_discrete(Axis::HorizontalScroll, discrete_x);
                    }
                    ptr.axis(time, Axis::HorizontalScroll, dx);
                }
                ptr.frame();
            }
        }
        DmabufInput::Key {
            evdev_code,
            pressed,
        } => {
            if !state.keyboard_entered {
                if let Some(surface) = state.main_surface.clone() {
                    let enter_serial = state.next_serial();
                    for kb in &state.keyboards {
                        kb.enter(enter_serial, &surface, vec![]);
                    }
                    state.keyboard_entered = true;
                }
            }
            let serial = state.next_serial();
            let kstate = if pressed {
                KeyState::Pressed
            } else {
                KeyState::Released
            };
            for kb in &state.keyboards {
                kb.key(serial, time, evdev_code, kstate);
            }
            let mod_bit = match evdev_code {
                42 | 54 => 1,
                29 | 97 => 4,
                56 | 100 => 8,
                125 | 126 => 64,
                _ => 0,
            };
            if mod_bit != 0 {
                if pressed {
                    state.keyboard_mods |= mod_bit;
                } else {
                    state.keyboard_mods &= !mod_bit;
                }
                let mod_serial = state.next_serial();
                for kb in &state.keyboards {
                    kb.modifiers(mod_serial, state.keyboard_mods, 0, 0, 0);
                }
            }
        }
        DmabufInput::Resize { width, height } => {
            let w = (width as i32).max(MIN_SURFACE_W);
            let h = (height as i32).max(MIN_SURFACE_H);
            state.desired_width = w;
            state.desired_height = h;
            if !state.hidden_shrunk {
                state.send_configure_size(w, h);
            }
        }
        DmabufInput::SetRefreshMhz { mhz } => {
            let clamped = mhz.clamp(24_000, 360_000);
            if state.refresh_mhz != clamped {
                state.refresh_mhz = clamped;
                for out in &state.outputs {
                    out.mode(
                        Mode::Current | Mode::Preferred,
                        state.desired_width,
                        state.desired_height,
                        clamped as i32,
                    );
                    out.done();
                }
            }
        }
        DmabufInput::SetUncapped { uncapped } => {
            state.uncapped = uncapped;
            if state.effective_uncapped() && state.visible {
                state.flush_frame_callbacks();
            }
        }
        DmabufInput::SetInterpolation { enabled } => {
            state.interpolation = enabled;
            if state.effective_uncapped() && state.visible {
                state.flush_frame_callbacks();
            }
        }
        DmabufInput::SetVisible { visible } => {
            let became_visible = visible && !state.visible;
            let became_hidden = !visible && state.visible;
            state.visible = visible;
            if became_hidden {
                state.hidden_since = Some(Instant::now());
            } else if became_visible {
                state.hidden_since = None;
                if state.hidden_shrunk {
                    state.hidden_shrunk = false;
                    let w = state.desired_width;
                    let h = state.desired_height;
                    state.send_configure_size(w, h);
                }
                state.flush_frame_callbacks();
            }
        }
        DmabufInput::SetClipboard { text } => {
            state.clipboard_text = Some(text.clone());
            state.data_devices.retain(|dd| dd.is_alive());
            let devices = state.data_devices.clone();
            for dd in &devices {
                state.send_clipboard_offer_to(dd, dh, &text);
            }
        }
    }
}

impl GlobalDispatch<WlCompositor, ()> for DmabufState {
    fn bind(
        _state: &mut Self,
        _dh: &DisplayHandle,
        _client: &Client,
        resource: New<WlCompositor>,
        _global_data: &(),
        data_init: &mut DataInit<'_, Self>,
    ) {
        data_init.init(resource, ());
    }
}

impl Dispatch<WlCompositor, ()> for DmabufState {
    fn request(
        _state: &mut Self,
        _client: &Client,
        _resource: &WlCompositor,
        request: wl_compositor::Request,
        _data: &(),
        _dh: &DisplayHandle,
        data_init: &mut DataInit<'_, Self>,
    ) {
        match request {
            wl_compositor::Request::CreateSurface { id } => {
                data_init.init(
                    id,
                    Mutex::new(SurfaceData {
                        pending_buffer: None,
                        pending_callbacks: Vec::new(),
                    }),
                );
            }
            wl_compositor::Request::CreateRegion { id } => {
                data_init.init(id, ());
            }
            _ => {}
        }
    }
}

impl Dispatch<WlRegion, ()> for DmabufState {
    fn request(
        _state: &mut Self,
        _client: &Client,
        _resource: &WlRegion,
        _request: wl_region::Request,
        _data: &(),
        _dh: &DisplayHandle,
        _data_init: &mut DataInit<'_, Self>,
    ) {
    }
}

impl Dispatch<WlSurface, Mutex<SurfaceData>> for DmabufState {
    fn request(
        state: &mut Self,
        _client: &Client,
        resource: &WlSurface,
        request: wl_surface::Request,
        data: &Mutex<SurfaceData>,
        _dh: &DisplayHandle,
        data_init: &mut DataInit<'_, Self>,
    ) {
        match request {
            wl_surface::Request::Attach { buffer, .. } => {
                if let Ok(mut sd) = data.lock() {
                    sd.pending_buffer = buffer;
                }
            }
            wl_surface::Request::Frame { callback } => {
                let cb = data_init.init(callback, ());
                if let Ok(mut sd) = data.lock() {
                    sd.pending_callbacks.push(cb);
                }
            }
            wl_surface::Request::Commit => {
                let (buf_opt, cbs) = match data.lock() {
                    Ok(mut sd) => (
                        sd.pending_buffer.take(),
                        std::mem::take(&mut sd.pending_callbacks),
                    ),
                    Err(_) => (None, Vec::new()),
                };

                let is_main = state.main_surface.as_ref() == Some(resource);
                let has_buf = buf_opt.is_some();
                if let Some(buf) = buf_opt {
                    if is_main && state.visible {
                        if let Some(DmabufBufferKind::Dmabuf(dframe)) = buf.data::<DmabufBufferKind>() {
                            publish_dmabuf_frame(state, dframe);
                        }
                        if let Some((_, old_staged)) =
                            state.staged_buffer.replace((state.frame_serial, buf))
                        {
                            old_staged.release();
                        }
                    } else {
                        if is_main {
                            if let Some(old_disp) = state.displayed_buffer.take() {
                                old_disp.release();
                            }
                            if let Some((_, old_staged)) = state.staged_buffer.take() {
                                old_staged.release();
                            }
                        }
                        buf.release();
                    }
                }

                if !cbs.is_empty() {
                    if is_main && has_buf && (!state.visible || !state.effective_uncapped()) {
                        state.pending_frame_callbacks.extend(cbs);
                        state.pending_cb_since.get_or_insert_with(Instant::now);
                    } else {
                        let t = state.time_ms();
                        for cb in cbs {
                            cb.done(t);
                        }
                    }
                }
            }
            _ => {}
        }
    }
}

fn publish_dmabuf_frame(state: &mut DmabufState, dframe: &Arc<DmabufFrame>) {
    state.frame_serial += 1;
    if let Ok(mut guard) = state.frame.lock() {
        *guard = Some(PublishedDmabuf {
            serial: state.frame_serial,
            frame: Arc::clone(dframe),
        });
    }
    state.frame_seq.store(state.frame_serial, Ordering::Release);
    if let Ok(guard) = state.on_frame.lock() {
        if let Some((f, user_data)) = *guard {
            f(user_data);
        }
    }
}

impl GlobalDispatch<WlShm, ()> for DmabufState {
    fn bind(
        _state: &mut Self,
        _dh: &DisplayHandle,
        _client: &Client,
        resource: New<WlShm>,
        _global_data: &(),
        data_init: &mut DataInit<'_, Self>,
    ) {
        let shm = data_init.init(resource, ());
        shm.format(Format::Argb8888);
        shm.format(Format::Xrgb8888);
    }
}

impl Dispatch<WlShm, ()> for DmabufState {
    fn request(
        _state: &mut Self,
        _client: &Client,
        _resource: &WlShm,
        request: wl_shm::Request,
        _data: &(),
        _dh: &DisplayHandle,
        data_init: &mut DataInit<'_, Self>,
    ) {
        if let wl_shm::Request::CreatePool { id, .. } = request {
            data_init.init(id, ());
        }
    }
}

impl Dispatch<WlShmPool, ()> for DmabufState {
    fn request(
        _state: &mut Self,
        _client: &Client,
        _resource: &WlShmPool,
        request: wl_shm_pool::Request,
        _data: &(),
        _dh: &DisplayHandle,
        data_init: &mut DataInit<'_, Self>,
    ) {
        if let wl_shm_pool::Request::CreateBuffer { id, .. } = request {
            data_init.init(id, DmabufBufferKind::CursorOnly);
        }
    }
}

impl Dispatch<WlBuffer, DmabufBufferKind> for DmabufState {
    fn request(
        _state: &mut Self,
        _client: &Client,
        _resource: &WlBuffer,
        _request: wl_buffer::Request,
        _data: &DmabufBufferKind,
        _dh: &DisplayHandle,
        _data_init: &mut DataInit<'_, Self>,
    ) {
    }
}

impl GlobalDispatch<ZwpLinuxDmabufV1, ()> for DmabufState {
    fn bind(
        _state: &mut Self,
        _dh: &DisplayHandle,
        _client: &Client,
        resource: New<ZwpLinuxDmabufV1>,
        _global_data: &(),
        data_init: &mut DataInit<'_, Self>,
    ) {
        let dmabuf = data_init.init(resource, ());
        let caps = cached_dmabuf_caps();
        const DRM_FORMAT_XRGB8888: u32 = 0x34325258;
        const DRM_FORMAT_ARGB8888: u32 = 0x34325241;
        for fmt in [DRM_FORMAT_XRGB8888, DRM_FORMAT_ARGB8888] {
            dmabuf.format(fmt);
        }
        if dmabuf.version() >= 3 {
            for &(fmt, modifier) in &caps.entries {
                dmabuf.modifier(fmt, (modifier >> 32) as u32, modifier as u32);
            }
        }
    }
}

impl Dispatch<ZwpLinuxDmabufV1, ()> for DmabufState {
    fn request(
        _state: &mut Self,
        _client: &Client,
        _resource: &ZwpLinuxDmabufV1,
        request: zwp_linux_dmabuf_v1::Request,
        _data: &(),
        _dh: &DisplayHandle,
        data_init: &mut DataInit<'_, Self>,
    ) {
        match request {
            zwp_linux_dmabuf_v1::Request::CreateParams { params_id } => {
                data_init.init(params_id, Mutex::new(DmabufParamsData::default()));
            }
            zwp_linux_dmabuf_v1::Request::GetDefaultFeedback { id }
            | zwp_linux_dmabuf_v1::Request::GetSurfaceFeedback { id, .. } => {
                let fb = data_init.init(id, ());
                send_dmabuf_feedback(&fb);
            }
            _ => {}
        }
    }
}

impl Dispatch<ZwpLinuxDmabufFeedbackV1, ()> for DmabufState {
    fn request(
        _state: &mut Self,
        _client: &Client,
        _resource: &ZwpLinuxDmabufFeedbackV1,
        _request: zwp_linux_dmabuf_feedback_v1::Request,
        _data: &(),
        _dh: &DisplayHandle,
        _data_init: &mut DataInit<'_, Self>,
    ) {
    }
}

struct DmabufCaps {
    main_device: Option<Vec<u8>>,
    entries: Vec<(u32, u64)>,
}

fn probe_host_dmabuf_caps() -> DmabufCaps {
    use std::io::Read;
    use wayland_client::protocol::wl_registry as c_reg;
    use wayland_client::{Connection, Dispatch as CDispatch, QueueHandle};
    use wayland_protocols::wp::linux_dmabuf::zv1::client::{
        zwp_linux_dmabuf_feedback_v1 as c_fb, zwp_linux_dmabuf_v1 as c_dmabuf,
    };

    const DRM_FORMAT_XRGB8888: u32 = 0x34325258;
    const DRM_FORMAT_ARGB8888: u32 = 0x34325241;
    const MOD_INVALID: u64 = 0x00ff_ffff_ffff_ffff;

    #[derive(Default)]
    struct DmabufProbe {
        dmabuf: Option<c_dmabuf::ZwpLinuxDmabufV1>,
        feedback: Option<c_fb::ZwpLinuxDmabufFeedbackV1>,
        main_device: Option<Vec<u8>>,
        entries: Vec<(u32, u64)>,
    }

    impl DmabufProbe {
        fn push_unique(&mut self, fmt: u32, modifier: u64) {
            if (fmt == DRM_FORMAT_XRGB8888 || fmt == DRM_FORMAT_ARGB8888)
                && !self.entries.contains(&(fmt, modifier))
            {
                self.entries.push((fmt, modifier));
            }
        }
    }

    impl CDispatch<c_reg::WlRegistry, ()> for DmabufProbe {
        fn event(
            state: &mut Self,
            registry: &c_reg::WlRegistry,
            event: c_reg::Event,
            _: &(),
            _: &Connection,
            qh: &QueueHandle<Self>,
        ) {
            if let c_reg::Event::Global {
                name,
                interface,
                version,
            } = event
            {
                if interface == "zwp_linux_dmabuf_v1" && state.dmabuf.is_none() {
                    let bound = registry.bind::<c_dmabuf::ZwpLinuxDmabufV1, _, _>(
                        name,
                        version.min(4),
                        qh,
                        (),
                    );
                    if version >= 4 {
                        state.feedback = Some(bound.get_default_feedback(qh, ()));
                    }
                    state.dmabuf = Some(bound);
                }
            }
        }
    }

    impl CDispatch<c_dmabuf::ZwpLinuxDmabufV1, ()> for DmabufProbe {
        fn event(
            state: &mut Self,
            _: &c_dmabuf::ZwpLinuxDmabufV1,
            event: c_dmabuf::Event,
            _: &(),
            _: &Connection,
            _: &QueueHandle<Self>,
        ) {
            if let c_dmabuf::Event::Modifier {
                format,
                modifier_hi,
                modifier_lo,
            } = event
            {
                let modifier = (u64::from(modifier_hi) << 32) | u64::from(modifier_lo);
                state.push_unique(format, modifier);
            }
        }
    }

    impl CDispatch<c_fb::ZwpLinuxDmabufFeedbackV1, ()> for DmabufProbe {
        fn event(
            state: &mut Self,
            _: &c_fb::ZwpLinuxDmabufFeedbackV1,
            event: c_fb::Event,
            _: &(),
            _: &Connection,
            _: &QueueHandle<Self>,
        ) {
            match event {
                c_fb::Event::MainDevice { device } => {
                    if !device.is_empty() {
                        state.main_device = Some(device);
                    }
                }
                c_fb::Event::FormatTable { fd, size } => {
                    let mut file = std::fs::File::from(fd);
                    let mut buf = vec![0u8; size as usize];
                    if file.read_exact(&mut buf).is_ok() {
                        for chunk in buf.chunks_exact(16) {
                            let fmt = u32::from_ne_bytes([chunk[0], chunk[1], chunk[2], chunk[3]]);
                            let modifier = u64::from_ne_bytes([
                                chunk[8], chunk[9], chunk[10], chunk[11], chunk[12], chunk[13],
                                chunk[14], chunk[15],
                            ]);
                            state.push_unique(fmt, modifier);
                        }
                    }
                }
                _ => {}
            }
        }
    }

    let mut probe = DmabufProbe::default();
    if let Ok(conn) = Connection::connect_to_env() {
        let mut queue = conn.new_event_queue();
        let qh = queue.handle();
        let _registry = conn.display().get_registry(&qh, ());
        let _ = queue.roundtrip(&mut probe);
        let _ = queue.roundtrip(&mut probe);
        if probe.entries.is_empty() {
            let _ = queue.roundtrip(&mut probe);
        }
    }

    // Always ensure LINEAR and MOD_INVALID are present as fallbacks after native tiled/compressed modifiers.
    for &(fmt, modifier) in &[
        (DRM_FORMAT_XRGB8888, 0u64),
        (DRM_FORMAT_ARGB8888, 0u64),
        (DRM_FORMAT_XRGB8888, MOD_INVALID),
        (DRM_FORMAT_ARGB8888, MOD_INVALID),
    ] {
        probe.push_unique(fmt, modifier);
    }

    let main_device = probe.main_device.or_else(|| {
        let dev_id: u64 = std::fs::metadata("/dev/dri/renderD128")
            .or_else(|_| std::fs::metadata("/dev/dri/card1"))
            .or_else(|_| std::fs::metadata("/dev/dri/card0"))
            .map(|m| m.rdev())
            .unwrap_or(0);
        if dev_id != 0 {
            Some(dev_id.to_ne_bytes().to_vec())
        } else {
            None
        }
    });

    DmabufCaps {
        main_device,
        entries: probe.entries,
    }
}

fn cached_dmabuf_caps() -> &'static DmabufCaps {
    static CAPS: OnceLock<DmabufCaps> = OnceLock::new();
    CAPS.get_or_init(probe_host_dmabuf_caps)
}

fn send_dmabuf_feedback(fb: &ZwpLinuxDmabufFeedbackV1) {
    let caps = cached_dmabuf_caps();
    let Some(dev_bytes) = caps.main_device.clone() else {
        return;
    };

    let mut table = Vec::with_capacity(caps.entries.len() * 16);
    for &(fmt, modifier) in &caps.entries {
        table.extend_from_slice(&fmt.to_ne_bytes());
        table.extend_from_slice(&0u32.to_ne_bytes());
        table.extend_from_slice(&modifier.to_ne_bytes());
    }

    let path = std::env::temp_dir().join(format!("beyond-dmabuf-tbl-{}", Uuid::new_v4().simple()));
    let Ok(mut file) = std::fs::OpenOptions::new()
        .read(true)
        .write(true)
        .create_new(true)
        .open(&path)
    else {
        return;
    };
    let _ = std::fs::remove_file(&path);
    if file.write_all(&table).is_err() || file.rewind().is_err() {
        return;
    }

    fb.main_device(dev_bytes.clone());
    fb.format_table(file.as_fd(), table.len() as u32);
    fb.tranche_target_device(dev_bytes);
    fb.tranche_flags(TrancheFlags::empty());
    let indices: Vec<u8> = (0u16..caps.entries.len() as u16)
        .flat_map(|i| i.to_ne_bytes())
        .collect();
    fb.tranche_formats(indices);
    fb.tranche_done();
    fb.done();
}

impl Dispatch<ZwpLinuxBufferParamsV1, Mutex<DmabufParamsData>> for DmabufState {
    fn request(
        _state: &mut Self,
        client: &Client,
        resource: &ZwpLinuxBufferParamsV1,
        request: zwp_linux_buffer_params_v1::Request,
        data: &Mutex<DmabufParamsData>,
        dh: &DisplayHandle,
        data_init: &mut DataInit<'_, Self>,
    ) {
        match request {
            zwp_linux_buffer_params_v1::Request::Add {
                fd,
                plane_idx,
                offset,
                stride,
                modifier_hi,
                modifier_lo,
            } => {
                let modifier = (u64::from(modifier_hi) << 32) | u64::from(modifier_lo);
                if let Ok(mut guard) = data.lock() {
                    guard.planes.push((
                        plane_idx,
                        DmabufPlane {
                            fd: Arc::new(fd),
                            offset,
                            stride,
                            modifier,
                        },
                    ));
                }
            }
            zwp_linux_buffer_params_v1::Request::CreateImmed {
                buffer_id,
                width,
                height,
                format,
                ..
            } => {
                let planes = data
                    .lock()
                    .map(|mut g| {
                        g.planes.sort_by_key(|(idx, _)| *idx);
                        g.planes.drain(..).map(|(_, p)| p).collect::<Vec<_>>()
                    })
                    .unwrap_or_default();
                data_init.init(
                    buffer_id,
                    DmabufBufferKind::Dmabuf(Arc::new(DmabufFrame {
                        id: NEXT_BUFFER_ID.fetch_add(1, Ordering::Relaxed),
                        width: width.max(1) as u32,
                        height: height.max(1) as u32,
                        fourcc: format,
                        planes,
                    })),
                );
            }
            zwp_linux_buffer_params_v1::Request::Create {
                width,
                height,
                format,
                ..
            } => {
                let planes = data
                    .lock()
                    .map(|mut g| {
                        g.planes.sort_by_key(|(idx, _)| *idx);
                        g.planes.drain(..).map(|(_, p)| p).collect::<Vec<_>>()
                    })
                    .unwrap_or_default();
                if let Ok(buf) =
                    client.create_resource::<WlBuffer, DmabufBufferKind, DmabufState>(
                        dh,
                        1,
                        DmabufBufferKind::Dmabuf(Arc::new(DmabufFrame {
                            id: NEXT_BUFFER_ID.fetch_add(1, Ordering::Relaxed),
                            width: width.max(1) as u32,
                            height: height.max(1) as u32,
                            fourcc: format,
                            planes,
                        })),
                    )
                {
                    resource.created(&buf);
                } else {
                    resource.failed();
                }
            }
            _ => {}
        }
    }
}

impl Dispatch<WlCallback, ()> for DmabufState {
    fn request(
        _state: &mut Self,
        _client: &Client,
        _resource: &WlCallback,
        _request: wl_callback::Request,
        _data: &(),
        _dh: &DisplayHandle,
        _data_init: &mut DataInit<'_, Self>,
    ) {
    }
}

impl GlobalDispatch<XdgWmBase, ()> for DmabufState {
    fn bind(
        _state: &mut Self,
        _dh: &DisplayHandle,
        _client: &Client,
        resource: New<XdgWmBase>,
        _global_data: &(),
        data_init: &mut DataInit<'_, Self>,
    ) {
        data_init.init(resource, ());
    }
}

impl Dispatch<XdgWmBase, ()> for DmabufState {
    fn request(
        state: &mut Self,
        _client: &Client,
        resource: &XdgWmBase,
        request: xdg_wm_base::Request,
        _data: &(),
        _dh: &DisplayHandle,
        data_init: &mut DataInit<'_, Self>,
    ) {
        match request {
            xdg_wm_base::Request::GetXdgSurface { id, surface } => {
                for out in &state.outputs {
                    surface.enter(out);
                }
                state.main_surface = Some(surface);
                state.pointer_entered = false;
                state.keyboard_entered = false;
                data_init.init(id, ());
            }
            xdg_wm_base::Request::Pong { .. } => {}
            _ => {
                let _ = resource;
            }
        }
    }
}

impl Dispatch<XdgSurface, ()> for DmabufState {
    fn request(
        state: &mut Self,
        _client: &Client,
        resource: &XdgSurface,
        request: xdg_surface::Request,
        _data: &(),
        _dh: &DisplayHandle,
        data_init: &mut DataInit<'_, Self>,
    ) {
        if let xdg_surface::Request::GetToplevel { id } = request {
            let tl = data_init.init(id, ());
            // Clear any stale state from a previous killed/restarted Mugetsu client.
            state.displayed_buffer = None;
            state.staged_buffer = None;
            state.pending_frame_callbacks.clear();
            state.pending_cb_since = None;
            state.hidden_shrunk = false;
            state.configured_width = state.desired_width;
            state.configured_height = state.desired_height;
            let serial = state.next_serial();
            tl.configure(state.desired_width, state.desired_height, vec![4, 0, 0, 0]);
            resource.configure(serial);
            state.toplevel = Some(tl);
            state.xdg_surface = Some(resource.clone());
        }
    }
}

impl Dispatch<XdgToplevel, ()> for DmabufState {
    fn request(
        _state: &mut Self,
        _client: &Client,
        _resource: &XdgToplevel,
        _request: xdg_toplevel::Request,
        _data: &(),
        _dh: &DisplayHandle,
        _data_init: &mut DataInit<'_, Self>,
    ) {
    }
}

impl GlobalDispatch<WlSeat, ()> for DmabufState {
    fn bind(
        _state: &mut Self,
        _dh: &DisplayHandle,
        _client: &Client,
        resource: New<WlSeat>,
        _global_data: &(),
        data_init: &mut DataInit<'_, Self>,
    ) {
        let seat = data_init.init(resource, ());
        seat.capabilities(Capability::Pointer | Capability::Keyboard);
        seat.name("beyond-seat".to_string());
    }
}

impl Dispatch<WlSeat, ()> for DmabufState {
    fn request(
        state: &mut Self,
        _client: &Client,
        _resource: &WlSeat,
        request: wl_seat::Request,
        _data: &(),
        _dh: &DisplayHandle,
        data_init: &mut DataInit<'_, Self>,
    ) {
        match request {
            wl_seat::Request::GetPointer { id } => {
                let ptr = data_init.init(id, ());
                state.pointers.push(ptr);
                state.pointer_entered = false;
            }
            wl_seat::Request::GetKeyboard { id } => {
                let kb = data_init.init(id, ());
                send_keymap(&kb);
                if let Some(surface) = state.main_surface.clone() {
                    let serial = state.next_serial();
                    kb.enter(serial, &surface, vec![]);
                    kb.modifiers(serial, state.keyboard_mods, 0, 0, 0);
                    state.keyboard_entered = true;
                }
                state.keyboards.push(kb);
            }
            _ => {}
        }
    }
}

impl Dispatch<WlPointer, ()> for DmabufState {
    fn request(
        state: &mut Self,
        _client: &Client,
        _resource: &WlPointer,
        request: wl_pointer::Request,
        _data: &(),
        _dh: &DisplayHandle,
        _data_init: &mut DataInit<'_, Self>,
    ) {
        if let wl_pointer::Request::SetCursor { surface: None, .. } = request {
            state.notify_cursor_shape(0);
        }
    }
}

impl GlobalDispatch<WpCursorShapeManagerV1, ()> for DmabufState {
    fn bind(
        _state: &mut Self,
        _dh: &DisplayHandle,
        _client: &Client,
        resource: New<WpCursorShapeManagerV1>,
        _global_data: &(),
        data_init: &mut DataInit<'_, Self>,
    ) {
        data_init.init(resource, ());
    }
}

impl Dispatch<WpCursorShapeManagerV1, ()> for DmabufState {
    fn request(
        _state: &mut Self,
        _client: &Client,
        _resource: &WpCursorShapeManagerV1,
        request: wp_cursor_shape_manager_v1::Request,
        _data: &(),
        _dh: &DisplayHandle,
        data_init: &mut DataInit<'_, Self>,
    ) {
        match request {
            wp_cursor_shape_manager_v1::Request::GetPointer {
                cursor_shape_device,
                ..
            }
            | wp_cursor_shape_manager_v1::Request::GetTabletToolV2 {
                cursor_shape_device,
                ..
            } => {
                data_init.init(cursor_shape_device, ());
            }
            _ => {}
        }
    }
}

impl Dispatch<WpCursorShapeDeviceV1, ()> for DmabufState {
    fn request(
        state: &mut Self,
        _client: &Client,
        _resource: &WpCursorShapeDeviceV1,
        request: wp_cursor_shape_device_v1::Request,
        _data: &(),
        _dh: &DisplayHandle,
        _data_init: &mut DataInit<'_, Self>,
    ) {
        if let wp_cursor_shape_device_v1::Request::SetShape {
            shape: WEnum::Value(shape),
            ..
        } = request
        {
            let code = match shape {
                CursorShape::Default | CursorShape::ContextMenu => 1,
                CursorShape::Pointer => 2,
                CursorShape::Text | CursorShape::VerticalText => 3,
                CursorShape::Grab
                | CursorShape::Grabbing
                | CursorShape::Move
                | CursorShape::AllScroll => 4,
                CursorShape::Crosshair => 5,
                CursorShape::Wait | CursorShape::Progress => 6,
                CursorShape::NotAllowed | CursorShape::NoDrop => 7,
                CursorShape::Help => 8,
                _ => 1,
            };
            state.notify_cursor_shape(code);
        }
    }
}

impl GlobalDispatch<WlDataDeviceManager, ()> for DmabufState {
    fn bind(
        _state: &mut Self,
        _dh: &DisplayHandle,
        _client: &Client,
        resource: New<WlDataDeviceManager>,
        _global_data: &(),
        data_init: &mut DataInit<'_, Self>,
    ) {
        data_init.init(resource, ());
    }
}

impl Dispatch<WlDataDeviceManager, ()> for DmabufState {
    fn request(
        state: &mut Self,
        _client: &Client,
        _resource: &WlDataDeviceManager,
        request: wl_data_device_manager::Request,
        _data: &(),
        dh: &DisplayHandle,
        data_init: &mut DataInit<'_, Self>,
    ) {
        match request {
            wl_data_device_manager::Request::CreateDataSource { id } => {
                data_init.init(id, DataSourceData::default());
            }
            wl_data_device_manager::Request::GetDataDevice { id, .. } => {
                let dd = data_init.init(id, ());
                state.data_devices.retain(|d| d.is_alive());
                state.data_devices.push(dd.clone());
                if let Some(text) = state.clipboard_text.clone() {
                    state.send_clipboard_offer_to(&dd, dh, &text);
                }
            }
            _ => {}
        }
    }
}

impl Dispatch<WlDataDevice, ()> for DmabufState {
    fn request(
        state: &mut Self,
        _client: &Client,
        _resource: &WlDataDevice,
        request: wl_data_device::Request,
        _data: &(),
        _dh: &DisplayHandle,
        _data_init: &mut DataInit<'_, Self>,
    ) {
        if let wl_data_device::Request::SetSelection {
            source: Some(src), ..
        } = request
        {
            let mime = src
                .data::<DataSourceData>()
                .and_then(|d| d.mime_types.lock().ok())
                .and_then(|mimes| {
                    for preferred in ["text/plain;charset=utf-8", "text/plain", "UTF8_STRING"] {
                        if mimes.iter().any(|m| m == preferred) {
                            return Some(preferred.to_string());
                        }
                    }
                    mimes.iter().find(|m| m.starts_with("text/")).cloned()
                });
            let Some(mime) = mime else {
                return;
            };
            let mut fds = [-1i32; 2];
            if unsafe { libc::pipe2(fds.as_mut_ptr(), libc::O_CLOEXEC) } != 0 {
                return;
            }
            let read_fd = unsafe { OwnedFd::from_raw_fd(fds[0]) };
            let write_fd = unsafe { OwnedFd::from_raw_fd(fds[1]) };
            src.send(mime, write_fd.as_fd());
            drop(write_fd);

            let on_clipboard = Arc::clone(&state.on_clipboard);
            let _ = std::thread::Builder::new()
                .name("bonxie-clip-rx".to_string())
                .spawn(move || {
                    let file = std::fs::File::from(read_fd);
                    let mut buf = Vec::new();
                    let _ = file.take(4 * 1024 * 1024).read_to_end(&mut buf);
                    if let Ok(cb_opt) = on_clipboard.lock() {
                        if let Some((cb, user_data)) = *cb_opt {
                            cb(user_data, buf.as_ptr(), buf.len());
                        }
                    }
                });
        }
    }
}

impl Dispatch<WlDataSource, DataSourceData> for DmabufState {
    fn request(
        _state: &mut Self,
        _client: &Client,
        _resource: &WlDataSource,
        request: wl_data_source::Request,
        data: &DataSourceData,
        _dh: &DisplayHandle,
        _data_init: &mut DataInit<'_, Self>,
    ) {
        if let wl_data_source::Request::Offer { mime_type } = request {
            if let Ok(mut mimes) = data.mime_types.lock() {
                mimes.push(mime_type);
            }
        }
    }
}

impl Dispatch<WlDataOffer, String> for DmabufState {
    fn request(
        _state: &mut Self,
        _client: &Client,
        _resource: &WlDataOffer,
        request: wl_data_offer::Request,
        data: &String,
        _dh: &DisplayHandle,
        _data_init: &mut DataInit<'_, Self>,
    ) {
        if let wl_data_offer::Request::Receive { fd, .. } = request {
            let mut file = std::fs::File::from(fd);
            let _ = file.write_all(data.as_bytes());
        }
    }
}

impl Dispatch<WlKeyboard, ()> for DmabufState {
    fn request(
        _state: &mut Self,
        _client: &Client,
        _resource: &WlKeyboard,
        _request: wl_keyboard::Request,
        _data: &(),
        _dh: &DisplayHandle,
        _data_init: &mut DataInit<'_, Self>,
    ) {
    }
}

fn probe_host_wayland_keymap() -> Option<Vec<u8>> {
    use std::io::Read;
    use wayland_client::protocol::{
        wl_keyboard as c_kb, wl_registry as c_reg, wl_seat as c_seat,
    };
    use wayland_client::{Connection, Dispatch as CDispatch, QueueHandle, WEnum};

    #[derive(Default)]
    struct KeymapProbe {
        seat: Option<c_seat::WlSeat>,
        kb: Option<c_kb::WlKeyboard>,
        keymap: Option<Vec<u8>>,
    }

    impl CDispatch<c_reg::WlRegistry, ()> for KeymapProbe {
        fn event(
            state: &mut Self,
            registry: &c_reg::WlRegistry,
            event: c_reg::Event,
            _: &(),
            _: &Connection,
            qh: &QueueHandle<Self>,
        ) {
            if let c_reg::Event::Global {
                name,
                interface,
                version,
            } = event
            {
                if interface == "wl_seat" && state.seat.is_none() {
                    state.seat = Some(registry.bind::<c_seat::WlSeat, _, _>(
                        name,
                        version.min(5),
                        qh,
                        (),
                    ));
                }
            }
        }
    }

    impl CDispatch<c_seat::WlSeat, ()> for KeymapProbe {
        fn event(
            state: &mut Self,
            seat: &c_seat::WlSeat,
            event: c_seat::Event,
            _: &(),
            _: &Connection,
            qh: &QueueHandle<Self>,
        ) {
            if let c_seat::Event::Capabilities {
                capabilities: WEnum::Value(caps),
            } = event
            {
                if caps.contains(c_seat::Capability::Keyboard) && state.kb.is_none() {
                    state.kb = Some(seat.get_keyboard(qh, ()));
                }
            }
        }
    }

    impl CDispatch<c_kb::WlKeyboard, ()> for KeymapProbe {
        fn event(
            state: &mut Self,
            _: &c_kb::WlKeyboard,
            event: c_kb::Event,
            _: &(),
            _: &Connection,
            _: &QueueHandle<Self>,
        ) {
            if let c_kb::Event::Keymap {
                format: WEnum::Value(c_kb::KeymapFormat::XkbV1),
                fd,
                size,
            } = event
            {
                let mut file = std::fs::File::from(fd);
                let mut buf = vec![0u8; size as usize];
                if file.read_exact(&mut buf).is_ok() {
                    while buf.last() == Some(&0) {
                        buf.pop();
                    }
                    if !buf.is_empty() {
                        state.keymap = Some(buf);
                    }
                }
            }
        }
    }

    let conn = Connection::connect_to_env().ok()?;
    let mut queue = conn.new_event_queue();
    let qh = queue.handle();
    let mut probe = KeymapProbe::default();
    let _registry = conn.display().get_registry(&qh, ());
    let _ = queue.roundtrip(&mut probe);
    let _ = queue.roundtrip(&mut probe);
    if probe.keymap.is_none() {
        let _ = queue.roundtrip(&mut probe);
    }
    probe.keymap
}

fn compile_xkb_fallback() -> Option<Vec<u8>> {
    let mut args: Vec<String> = Vec::new();
    if let Ok(model) = std::env::var("XKB_DEFAULT_MODEL") {
        if !model.trim().is_empty() {
            args.push("-model".into());
            args.push(model.trim().into());
        }
    }
    if let Ok(layout) = std::env::var("XKB_DEFAULT_LAYOUT") {
        if !layout.trim().is_empty() {
            args.push("-layout".into());
            args.push(layout.trim().into());
        }
    }
    if let Ok(variant) = std::env::var("XKB_DEFAULT_VARIANT") {
        if !variant.trim().is_empty() {
            args.push("-variant".into());
            args.push(variant.trim().into());
        }
    }
    args.push("-print".into());

    let setxkb = Command::new("setxkbmap")
        .args(&args)
        .output()
        .ok()
        .filter(|o| o.status.success() && !o.stdout.is_empty())
        .or_else(|| {
            Command::new("setxkbmap")
                .args(["-model", "pc105", "-layout", "us", "-print"])
                .output()
                .ok()
                .filter(|o| o.status.success() && !o.stdout.is_empty())
        })?;
    let mut child = Command::new("xkbcomp")
        .args(["-xkb", "-", "-"])
        .stdin(std::process::Stdio::piped())
        .stdout(std::process::Stdio::piped())
        .stderr(std::process::Stdio::null())
        .spawn()
        .ok()?;
    if let Some(mut stdin) = child.stdin.take() {
        let _ = stdin.write_all(&setxkb.stdout);
    }
    let out = child.wait_with_output().ok()?;
    if out.status.success() && !out.stdout.is_empty() {
        Some(out.stdout)
    } else {
        None
    }
}

fn cached_xkb_keymap() -> Option<&'static [u8]> {
    static KEYMAP: OnceLock<Option<Vec<u8>>> = OnceLock::new();
    KEYMAP
        .get_or_init(|| probe_host_wayland_keymap().or_else(compile_xkb_fallback))
        .as_deref()
}

fn send_keymap(kb: &WlKeyboard) {
    let Some(bytes) = cached_xkb_keymap() else {
        warn!("No XKB keymap available for embedded keyboard");
        return;
    };
    let path = std::env::temp_dir().join(format!("beyond-xkb-{}.xkb", Uuid::new_v4().simple()));
    let Ok(mut file) = std::fs::OpenOptions::new()
        .read(true)
        .write(true)
        .create_new(true)
        .open(&path)
    else {
        return;
    };
    let _ = std::fs::remove_file(&path);
    if file.write_all(bytes).is_err() || file.rewind().is_err() {
        return;
    }
    kb.keymap(KeymapFormat::XkbV1, file.as_fd(), bytes.len() as u32);
    if kb.version() >= 4 {
        kb.repeat_info(25, 400);
    }
}

impl GlobalDispatch<WlOutput, ()> for DmabufState {
    fn bind(
        state: &mut Self,
        _dh: &DisplayHandle,
        _client: &Client,
        resource: New<WlOutput>,
        _global_data: &(),
        data_init: &mut DataInit<'_, Self>,
    ) {
        let out = data_init.init(resource, ());
        out.geometry(
            0,
            0,
            338,
            190,
            Subpixel::Unknown,
            "Beyond".to_string(),
            "Embedded".to_string(),
            wl_output::Transform::Normal,
        );
        out.mode(
            Mode::Current | Mode::Preferred,
            state.desired_width,
            state.desired_height,
            state.refresh_mhz as i32,
        );
        out.scale(1);
        out.done();
        if let Some(surface) = &state.main_surface {
            surface.enter(&out);
        }
        state.outputs.push(out);
    }
}

impl Dispatch<WlOutput, ()> for DmabufState {
    fn request(
        _state: &mut Self,
        _client: &Client,
        _resource: &WlOutput,
        _request: wl_output::Request,
        _data: &(),
        _dh: &DisplayHandle,
        _data_init: &mut DataInit<'_, Self>,
    ) {
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn cadence_24fps_on_60hz_flushes_immediately_when_presented() {
        assert_eq!(
            decide_frame_cb_action(
                true,
                true,
                false,
                60_000,
                10,
                10,
                Duration::from_millis(41),
                Duration::from_millis(5),
            ),
            FrameCbDecision::FlushNow
        );
    }

    #[test]
    fn vsync_jitter_14ms_on_60hz_flushes_immediately() {
        // 75% of 16.67ms = 12.5ms, so 14ms early vsync must still flush immediately.
        assert_eq!(
            decide_frame_cb_action(
                true,
                true,
                false,
                60_000,
                20,
                20,
                Duration::from_millis(14),
                Duration::from_millis(6),
            ),
            FrameCbDecision::FlushNow
        );
    }

    #[test]
    fn too_early_presented_waits_exact_remaining_ms_then_flushes() {
        // At 60Hz, min_cb_interval = 12.5ms. At elapsed=4ms, wait ~8ms (not 50ms!),
        // then flush as soon as elapsed reaches 13ms.
        assert_eq!(
            decide_frame_cb_action(
                true,
                true,
                false,
                60_000,
                21,
                21,
                Duration::from_millis(4),
                Duration::from_millis(4),
            ),
            FrameCbDecision::WaitMs(8)
        );
        assert_eq!(
            decide_frame_cb_action(
                true,
                true,
                false,
                60_000,
                21,
                21,
                Duration::from_millis(13),
                Duration::from_millis(13),
            ),
            FrameCbDecision::FlushNow
        );
    }

    #[test]
    fn unpresented_frame_waits_until_fallback_timeout() {
        // 2_500_000 / 60_000 = 41ms fallback timeout on 60Hz.
        assert_eq!(
            decide_frame_cb_action(
                true,
                true,
                false,
                60_000,
                5,
                4,
                Duration::from_millis(20),
                Duration::from_millis(10),
            ),
            FrameCbDecision::WaitMs(31)
        );
        assert_eq!(
            decide_frame_cb_action(
                true,
                true,
                false,
                60_000,
                5,
                4,
                Duration::from_millis(41),
                Duration::from_millis(41),
            ),
            FrameCbDecision::FlushNow
        );
    }

    #[test]
    fn hidden_tab_throttles_to_50ms_and_uncapped_flushes_immediately() {
        assert_eq!(
            decide_frame_cb_action(
                true,
                false,
                true,
                60_000,
                8,
                0,
                Duration::from_millis(20),
                Duration::from_millis(20),
            ),
            FrameCbDecision::WaitMs(30)
        );
        assert_eq!(
            decide_frame_cb_action(
                true,
                false,
                false,
                60_000,
                8,
                8,
                Duration::from_millis(50),
                Duration::from_millis(50),
            ),
            FrameCbDecision::FlushNow
        );
        assert_eq!(
            decide_frame_cb_action(
                true,
                true,
                true,
                60_000,
                8,
                0,
                Duration::from_millis(0),
                Duration::from_millis(0),
            ),
            FrameCbDecision::FlushNow
        );
    }

    #[test]
    fn uncapped_setting_without_interpolation_remains_vsync_paced() {
        let state_effective_uncapped = false; // uncapped=true && interpolation=false
        // At 100Hz, fallback_timeout clamps to 40ms; with 5ms elapsed and frame 12 unpresented, WaitMs(35).
        assert_eq!(
            decide_frame_cb_action(
                true,
                true,
                state_effective_uncapped,
                100_000,
                12,
                11,
                Duration::from_millis(5),
                Duration::from_millis(5),
            ),
            FrameCbDecision::WaitMs(35)
        );
    }
}
