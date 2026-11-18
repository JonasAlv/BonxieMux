use std::io::{Seek, Write};
use std::os::fd::{AsFd, AsRawFd, OwnedFd, RawFd};
use std::os::unix::fs::MetadataExt;
use std::path::PathBuf;
use std::process::Command;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::mpsc::{self, Receiver, Sender};
use std::sync::{Arc, Mutex, OnceLock};
use std::thread::JoinHandle;
use std::time::Instant;
use uuid::Uuid;
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
    Resource,
};

macro_rules! warn { ($($t:tt)*) => { eprintln!($($t)*); }; }

#[derive(Debug, Clone)]
pub struct DmabufPlane {
    pub fd: Arc<OwnedFd>,
    pub offset: u32,
    pub stride: u32,
    pub modifier: u64,
}

#[derive(Debug, Clone)]
pub struct DmabufFrame {
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
}

pub type DmabufNotifyFn = extern "C" fn(usize);

pub struct DmabufHost {
    pub socket_name: String,
    pub socket_path: PathBuf,
    pub frame: Arc<Mutex<Option<PublishedDmabuf>>>,
    pub frame_seq: Arc<AtomicU64>,
    on_frame: Arc<Mutex<Option<(DmabufNotifyFn, usize)>>>,
    input_tx: Sender<DmabufInput>,
    acquired_serial: Arc<AtomicU64>,
    presented_flag: Arc<AtomicBool>,
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
        let (input_tx, input_rx) = mpsc::channel();
        let acquired_serial = Arc::new(AtomicU64::new(0));
        let acquired_clone = Arc::clone(&acquired_serial);
        let presented_flag = Arc::new(AtomicBool::new(false));
        let presented_clone = Arc::clone(&presented_flag);
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
            input_tx,
            acquired_serial,
            presented_flag,
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

    pub fn send_input(&self, ev: DmabufInput) {
        let _ = self.input_tx.send(ev);
        self.wake_loop();
    }

    pub fn notify_frame_acquired(&self, serial: u64) {
        self.acquired_serial.store(serial, Ordering::Release);
        self.wake_loop();
    }

    pub fn notify_presented(&self) {
        self.presented_flag.store(true, Ordering::Release);
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

struct DmabufState {
    frame: Arc<Mutex<Option<PublishedDmabuf>>>,
    frame_seq: Arc<AtomicU64>,
    frame_serial: u64,
    on_frame: Arc<Mutex<Option<(DmabufNotifyFn, usize)>>>,
    start_time: Instant,
    desired_width: i32,
    desired_height: i32,
    main_surface: Option<WlSurface>,
    toplevel: Option<XdgToplevel>,
    xdg_surface: Option<XdgSurface>,
    pointers: Vec<WlPointer>,
    keyboards: Vec<WlKeyboard>,
    outputs: Vec<WlOutput>,
    serial_counter: u32,
    pointer_entered: bool,
    keyboard_entered: bool,
    keyboard_mods: u32,
    displayed_buffer: Option<WlBuffer>,
    staged_buffer: Option<(u64, WlBuffer)>,
    refresh_mhz: u32,
    pending_frame_callbacks: Vec<WlCallback>,
    pending_cb_since: Option<Instant>,
    last_cb_flush: Instant,
}

impl DmabufState {
    fn next_serial(&mut self) -> u32 {
        self.serial_counter = self.serial_counter.wrapping_add(1);
        self.serial_counter
    }

    fn time_ms(&self) -> u32 {
        self.start_time.elapsed().as_millis() as u32
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

#[allow(clippy::too_many_arguments)]
fn run_dmabuf_loop(
    listener: ListeningSocket,
    frame: Arc<Mutex<Option<PublishedDmabuf>>>,
    frame_seq: Arc<AtomicU64>,
    on_frame: Arc<Mutex<Option<(DmabufNotifyFn, usize)>>>,
    input_rx: Receiver<DmabufInput>,
    acquired_serial: Arc<AtomicU64>,
    presented_flag: Arc<AtomicBool>,
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

    let mut state = DmabufState {
        frame,
        frame_seq,
        frame_serial: 0,
        on_frame,
        start_time: Instant::now(),
        desired_width: width as i32,
        desired_height: height as i32,
        main_surface: None,
        toplevel: None,
        xdg_surface: None,
        pointers: Vec::new(),
        keyboards: Vec::new(),
        outputs: Vec::new(),
        serial_counter: 1,
        pointer_entered: false,
        keyboard_entered: false,
        keyboard_mods: 0,
        displayed_buffer: None,
        staged_buffer: None,
        refresh_mhz: 60_000,
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
            handle_input(&mut state, ev);
        }

        let _ = display.dispatch_clients(&mut state);
        state.promote_acquired_buffer(acquired_serial.swap(0, Ordering::AcqRel));

        let min_cb_interval =
            std::time::Duration::from_micros((950_000_000u64 / state.refresh_mhz.max(24_000) as u64).max(1000));
        if (presented_flag.swap(false, Ordering::AcqRel)
            && state.last_cb_flush.elapsed() >= min_cb_interval)
            || state
                .pending_cb_since
                .is_some_and(|s| s.elapsed() >= std::time::Duration::from_millis(50))
        {
            state.flush_frame_callbacks();
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
            libc::poll(pfds.as_mut_ptr(), nfds, 5);
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

fn handle_input(state: &mut DmabufState, ev: DmabufInput) {
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
            let w = width.max(320) as i32;
            let h = height.max(240) as i32;
            if w != state.desired_width || h != state.desired_height {
                state.desired_width = w;
                state.desired_height = h;
                for out in &state.outputs {
                    out.mode(Mode::Current | Mode::Preferred, w, h, state.refresh_mhz as i32);
                    out.done();
                }
                if let (Some(tl), Some(xs)) = (state.toplevel.clone(), state.xdg_surface.clone()) {
                    let serial = state.next_serial();
                    tl.configure(w, h, vec![4, 0, 0, 0]);
                    xs.configure(serial);
                }
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
                    if is_main {
                        if let Some(DmabufBufferKind::Dmabuf(dframe)) = buf.data::<DmabufBufferKind>() {
                            publish_dmabuf_frame(state, dframe);
                        }
                        if let Some((_, old_staged)) =
                            state.staged_buffer.replace((state.frame_serial, buf))
                        {
                            old_staged.release();
                        }
                    } else {
                        buf.release();
                    }
                }

                if !cbs.is_empty() {
                    if is_main && has_buf {
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
    if let Some((f, user_data)) = state.on_frame.lock().ok().and_then(|g| *g) {
        f(user_data);
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
        const DRM_FORMAT_XRGB8888: u32 = 0x34325258;
        const DRM_FORMAT_ARGB8888: u32 = 0x34325241;
        const MOD_INVALID: u64 = 0x00ff_ffff_ffff_ffff;
        for fmt in [DRM_FORMAT_XRGB8888, DRM_FORMAT_ARGB8888] {
            dmabuf.format(fmt);
            if dmabuf.version() >= 3 {
                dmabuf.modifier(fmt, (MOD_INVALID >> 32) as u32, MOD_INVALID as u32);
                dmabuf.modifier(fmt, 0, 0);
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

fn send_dmabuf_feedback(fb: &ZwpLinuxDmabufFeedbackV1) {
    let dev_id: u64 = std::fs::metadata("/dev/dri/renderD128")
        .or_else(|_| std::fs::metadata("/dev/dri/card1"))
        .or_else(|_| std::fs::metadata("/dev/dri/card0"))
        .map(|m| m.rdev())
        .unwrap_or(0);
    if dev_id == 0 {
        return;
    }
    let dev_bytes = dev_id.to_ne_bytes().to_vec();

    const DRM_FORMAT_XRGB8888: u32 = 0x34325258;
    const DRM_FORMAT_ARGB8888: u32 = 0x34325241;
    const MOD_INVALID: u64 = 0x00ff_ffff_ffff_ffff;
    let entries: [(u32, u64); 4] = [
        (DRM_FORMAT_XRGB8888, MOD_INVALID),
        (DRM_FORMAT_ARGB8888, MOD_INVALID),
        (DRM_FORMAT_XRGB8888, 0),
        (DRM_FORMAT_ARGB8888, 0),
    ];

    let mut table = Vec::with_capacity(entries.len() * 16);
    for (fmt, modifier) in entries {
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
    let indices: Vec<u8> = (0u16..entries.len() as u16)
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
        _state: &mut Self,
        _client: &Client,
        _resource: &WlPointer,
        _request: wl_pointer::Request,
        _data: &(),
        _dh: &DisplayHandle,
        _data_init: &mut DataInit<'_, Self>,
    ) {
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

fn cached_xkb_keymap() -> Option<&'static [u8]> {
    static KEYMAP: OnceLock<Option<Vec<u8>>> = OnceLock::new();
    KEYMAP
        .get_or_init(|| {
            let setxkb = Command::new("setxkbmap")
                .args(["-model", "pc105", "-layout", "us", "-print"])
                .output()
                .ok()?;
            if !setxkb.status.success() {
                return None;
            }
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
        })
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
