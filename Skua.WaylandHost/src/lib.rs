mod dmabuf_host;

use dmabuf_host::{DmabufHost, DmabufInput};
use std::os::fd::AsRawFd;
use uuid::Uuid;
use wayland_client::{
    protocol::{
        wl_output::{self, WlOutput},
        wl_registry::{self, WlRegistry},
    },
    Connection, Dispatch, QueueHandle,
};

pub struct BonxieWlHost {
    inner: DmabufHost,
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct BonxieDmaBufPlane {
    pub fd: i32,
    pub offset: u32,
    pub stride: u32,
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct BonxieDmaBufFrame {
    pub seq: u64,
    pub buffer_id: u64,
    pub width: u32,
    pub height: u32,
    pub fourcc: u32,
    pub modifier: u64,
    pub num_planes: u32,
    pub planes: [BonxieDmaBufPlane; 4],
}

#[no_mangle]
pub unsafe extern "C" fn bonxie_wl_start(
    width: u32,
    height: u32,
    _enable_dmabuf: u8,
    socket_buf: *mut u8,
    socket_buf_len: usize,
) -> *mut BonxieWlHost {
    let id = Uuid::new_v4();
    let Ok(inner) = DmabufHost::start(id, width.max(320), height.max(240)) else {
        return std::ptr::null_mut();
    };
    if !socket_buf.is_null() && socket_buf_len > 0 {
        let bytes = inner.socket_name.as_bytes();
        let n = bytes.len().min(socket_buf_len - 1);
        std::ptr::copy_nonoverlapping(bytes.as_ptr(), socket_buf, n);
        *socket_buf.add(n) = 0;
    }
    Box::into_raw(Box::new(BonxieWlHost { inner }))
}

#[no_mangle]
pub unsafe extern "C" fn bonxie_wl_stop(host: *mut BonxieWlHost) {
    if !host.is_null() {
        drop(Box::from_raw(host));
    }
}

#[no_mangle]
pub unsafe extern "C" fn bonxie_wl_frame_seq(host: *mut BonxieWlHost) -> u64 {
    if host.is_null() {
        return 0;
    }
    (&*host)
        .inner
        .frame_seq
        .load(std::sync::atomic::Ordering::Acquire)
}

#[no_mangle]
pub unsafe extern "C" fn bonxie_wl_set_frame_callback(
    host: *mut BonxieWlHost,
    cb: Option<extern "C" fn(usize)>,
    user_data: usize,
) {
    if host.is_null() {
        return;
    }
    (&*host)
        .inner
        .set_frame_callback(cb.map(|f| (f, user_data)));
}

#[no_mangle]
pub unsafe extern "C" fn bonxie_wl_poll_dmabuf(
    host: *mut BonxieWlHost,
    last_seq: u64,
    out_dmabuf: *mut BonxieDmaBufFrame,
) -> i32 {
    if host.is_null() || out_dmabuf.is_null() {
        return -1;
    }
    let h = &(&*host).inner;
    let Some(published) = h.latest_frame() else {
        return 0;
    };
    if published.serial <= last_seq {
        return 0;
    }
    let frame = &published.frame;
    let num_planes = frame.planes.len().min(4);
    if num_planes == 0 {
        return 0;
    }
    let mut out = BonxieDmaBufFrame {
        seq: published.serial,
        buffer_id: frame.id,
        width: frame.width,
        height: frame.height,
        fourcc: frame.fourcc,
        modifier: frame.planes[0].modifier,
        num_planes: num_planes as u32,
        planes: [BonxieDmaBufPlane::default(); 4],
    };
    for (i, p) in frame.planes.iter().take(4).enumerate() {
        let dup_fd = libc::dup(p.fd.as_raw_fd());
        if dup_fd < 0 {
            for j in 0..i {
                if out.planes[j].fd >= 0 {
                    libc::close(out.planes[j].fd);
                }
            }
            return -1;
        }
        out.planes[i] = BonxieDmaBufPlane {
            fd: dup_fd,
            offset: p.offset,
            stride: p.stride,
        };
    }
    h.notify_frame_acquired(published.serial);
    *out_dmabuf = out;
    1
}

#[no_mangle]
pub unsafe extern "C" fn bonxie_wl_notify_presented(host: *mut BonxieWlHost) {
    if !host.is_null() {
        (&*host).inner.notify_presented();
    }
}

#[no_mangle]
pub unsafe extern "C" fn bonxie_wl_set_refresh_mhz(host: *mut BonxieWlHost, mhz: u32) {
    if !host.is_null() {
        (&*host)
            .inner
            .send_input(DmabufInput::SetRefreshMhz { mhz });
    }
}

#[no_mangle]
pub unsafe extern "C" fn bonxie_wl_peek_frame(
    host: *mut BonxieWlHost,
    out_seq: *mut u64,
    out_buffer_id: *mut u64,
    out_width: *mut u32,
    out_height: *mut u32,
) -> i32 {
    if host.is_null() {
        return -1;
    }
    let h = &(&*host).inner;
    let Some(published) = h.latest_frame() else {
        return 0;
    };
    if !out_seq.is_null() {
        *out_seq = published.serial;
    }
    if !out_buffer_id.is_null() {
        *out_buffer_id = published.frame.id;
    }
    if !out_width.is_null() {
        *out_width = published.frame.width;
    }
    if !out_height.is_null() {
        *out_height = published.frame.height;
    }
    1
}

#[no_mangle]
pub unsafe extern "C" fn bonxie_wl_ack_frame(host: *mut BonxieWlHost, seq: u64) {
    if !host.is_null() && seq > 0 {
        (&*host).inner.notify_frame_acquired(seq);
    }
}

#[no_mangle]
pub unsafe extern "C" fn bonxie_wl_set_uncapped(host: *mut BonxieWlHost, uncapped: u8) {
    if !host.is_null() {
        (&*host).inner.send_input(DmabufInput::SetUncapped {
            uncapped: uncapped != 0,
        });
    }
}

#[no_mangle]
pub unsafe extern "C" fn bonxie_wl_set_visible(host: *mut BonxieWlHost, visible: u8) {
    if !host.is_null() {
        (&*host).inner.send_input(DmabufInput::SetVisible {
            visible: visible != 0,
        });
    }
}

#[no_mangle]
pub unsafe extern "C" fn bonxie_wl_resize(host: *mut BonxieWlHost, width: u32, height: u32) {
    if !host.is_null() {
        (&*host)
            .inner
            .send_input(DmabufInput::Resize { width, height });
    }
}

#[no_mangle]
pub unsafe extern "C" fn bonxie_wl_pointer_motion(host: *mut BonxieWlHost, x: f64, y: f64) {
    if !host.is_null() {
        (&*host)
            .inner
            .send_input(DmabufInput::PointerMotion { x, y });
    }
}

#[no_mangle]
pub unsafe extern "C" fn bonxie_wl_pointer_button(
    host: *mut BonxieWlHost,
    button: u32,
    pressed: u8,
) {
    if !host.is_null() {
        (&*host).inner.send_input(DmabufInput::PointerButton {
            button,
            pressed: pressed != 0,
        });
    }
}

#[no_mangle]
pub unsafe extern "C" fn bonxie_wl_pointer_scroll(host: *mut BonxieWlHost, dx: f64, dy: f64) {
    if !host.is_null() {
        (&*host)
            .inner
            .send_input(DmabufInput::PointerScroll { dx, dy });
    }
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct BonxieOutputInfo {
    pub x: i32,
    pub y: i32,
    pub width: i32,
    pub height: i32,
    pub refresh_mhz: u32,
}

#[derive(Default)]
struct OutputProbeState {
    outputs: Vec<BonxieOutputInfo>,
}

impl Dispatch<WlRegistry, ()> for OutputProbeState {
    fn event(
        state: &mut Self,
        registry: &WlRegistry,
        event: wl_registry::Event,
        _: &(),
        _: &Connection,
        qh: &QueueHandle<Self>,
    ) {
        if let wl_registry::Event::Global {
            name,
            interface,
            version,
        } = event
        {
            if interface == "wl_output" {
                let idx = state.outputs.len();
                state.outputs.push(BonxieOutputInfo::default());
                registry.bind::<WlOutput, usize, Self>(name, version.min(4), qh, idx);
            }
        }
    }
}

impl Dispatch<WlOutput, usize> for OutputProbeState {
    fn event(
        state: &mut Self,
        _: &WlOutput,
        event: wl_output::Event,
        idx: &usize,
        _: &Connection,
        _: &QueueHandle<Self>,
    ) {
        let Some(info) = state.outputs.get_mut(*idx) else {
            return;
        };
        match event {
            wl_output::Event::Geometry { x, y, .. } => {
                info.x = x;
                info.y = y;
            }
            wl_output::Event::Mode {
                flags,
                width,
                height,
                refresh,
            } => {
                if let wayland_client::WEnum::Value(f) = flags {
                    if f.contains(wl_output::Mode::Current) {
                        info.width = width;
                        info.height = height;
                        if refresh > 0 {
                            info.refresh_mhz = refresh as u32;
                        }
                    }
                }
            }
            _ => {}
        }
    }
}

#[no_mangle]
pub unsafe extern "C" fn bonxie_wl_query_outputs(
    out_buf: *mut BonxieOutputInfo,
    max_count: usize,
) -> i32 {
    if out_buf.is_null() || max_count == 0 {
        return -1;
    }
    let Ok(conn) = Connection::connect_to_env() else {
        return -1;
    };
    let display = conn.display();
    let mut queue = conn.new_event_queue::<OutputProbeState>();
    let qh = queue.handle();
    let mut state = OutputProbeState::default();
    let _registry = display.get_registry(&qh, ());
    if queue.roundtrip(&mut state).is_err() {
        return -1;
    }
    if queue.roundtrip(&mut state).is_err() {
        return -1;
    }

    let mut written = 0usize;
    for info in state.outputs.into_iter() {
        if info.width > 0 && info.height > 0 && info.refresh_mhz > 0 {
            if written >= max_count {
                break;
            }
            *out_buf.add(written) = info;
            written += 1;
        }
    }
    written as i32
}

#[no_mangle]
pub unsafe extern "C" fn bonxie_wl_key(host: *mut BonxieWlHost, evdev_code: u32, pressed: u8) {
    if !host.is_null() {
        (&*host).inner.send_input(DmabufInput::Key {
            evdev_code,
            pressed: pressed != 0,
        });
    }
}
