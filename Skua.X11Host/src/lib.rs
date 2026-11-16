use std::os::fd::{AsRawFd, OwnedFd};
use std::sync::atomic::{AtomicBool, AtomicU32, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use x11rb::connection::Connection;
use x11rb::wrapper::ConnectionExt as _;
use x11rb::protocol::composite::{ConnectionExt as CompositeExt, Redirect};
use x11rb::protocol::damage::{ConnectionExt as DamageExt, ReportLevel};
use x11rb::protocol::dri3::ConnectionExt as Dri3Ext;
use x11rb::protocol::randr::ConnectionExt as RandrExt;
use x11rb::protocol::xproto::{
    AtomEnum, ChangeWindowAttributesAux, ConfigureWindowAux, ConnectionExt as XprotoExt,
    CreateWindowAux, EventMask, Window, WindowClass,
};
use x11rb::protocol::Event;
use x11rb::rust_connection::RustConnection;
use x11rb::COPY_DEPTH_FROM_PARENT;

const DRM_FORMAT_ARGB8888: u32 = 0x34325241;
const DRM_FORMAT_XRGB8888: u32 = 0x34325258;
const DRM_FORMAT_MOD_INVALID: u64 = 0x00ff_ffff_ffff_ffff;

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
    pub width: u32,
    pub height: u32,
    pub fourcc: u32,
    pub modifier: u64,
    pub num_planes: u32,
    pub planes: [BonxieDmaBufPlane; 4],
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct BonxieOutputInfo {
    pub x: i32,
    pub y: i32,
    pub width: u32,
    pub height: u32,
    pub refresh_mhz: u32,
}

struct CapturedPlane {
    fd: OwnedFd,
    offset: u32,
    stride: u32,
}

struct CapturedFrame {
    serial: u64,
    width: u32,
    height: u32,
    fourcc: u32,
    modifier: u64,
    planes: Vec<CapturedPlane>,
}

struct SharedState {
    stop: AtomicBool,
    frame_seq: AtomicU64,
    pending_resize_w: AtomicU32,
    pending_resize_h: AtomicU32,
    has_pending_resize: AtomicBool,
    latest: Mutex<Option<Arc<CapturedFrame>>>,
    frame_cb: Mutex<Option<(extern "C" fn(usize), usize)>>,
}

pub struct BonxieX11Host {
    shared: Arc<SharedState>,
    worker: Option<JoinHandle<()>>,
}

#[no_mangle]
pub unsafe extern "C" fn bonxie_x11_start(
    child_pid: u32,
    width: u32,
    height: u32,
) -> *mut BonxieX11Host {
    let w = width.max(320);
    let h = height.max(240);
    let shared = Arc::new(SharedState {
        stop: AtomicBool::new(false),
        frame_seq: AtomicU64::new(0),
        pending_resize_w: AtomicU32::new(w),
        pending_resize_h: AtomicU32::new(h),
        has_pending_resize: AtomicBool::new(false),
        latest: Mutex::new(None),
        frame_cb: Mutex::new(None),
    });

    let thread_shared = Arc::clone(&shared);
    let Ok(worker) = thread::Builder::new()
        .name(format!("bonxie-x11-{child_pid}"))
        .spawn(move || run_x11_host(child_pid, w, h, thread_shared))
    else {
        return std::ptr::null_mut();
    };

    Box::into_raw(Box::new(BonxieX11Host {
        shared,
        worker: Some(worker),
    }))
}

#[no_mangle]
pub unsafe extern "C" fn bonxie_x11_stop(host: *mut BonxieX11Host) {
    if host.is_null() {
        return;
    }
    let mut boxed = Box::from_raw(host);
    boxed.shared.stop.store(true, Ordering::Release);
    if let Some(worker) = boxed.worker.take() {
        let _ = worker.join();
    }
}

#[no_mangle]
pub unsafe extern "C" fn bonxie_x11_frame_seq(host: *mut BonxieX11Host) -> u64 {
    if host.is_null() {
        return 0;
    }
    (&*host).shared.frame_seq.load(Ordering::Acquire)
}

#[no_mangle]
pub unsafe extern "C" fn bonxie_x11_set_frame_callback(
    host: *mut BonxieX11Host,
    cb: Option<extern "C" fn(usize)>,
    user_data: usize,
) {
    if host.is_null() {
        return;
    }
    if let Ok(mut guard) = (&*host).shared.frame_cb.lock() {
        *guard = cb.map(|f| (f, user_data));
    }
}

#[no_mangle]
pub unsafe extern "C" fn bonxie_x11_resize(host: *mut BonxieX11Host, width: u32, height: u32) {
    if host.is_null() || width < 2 || height < 2 {
        return;
    }
    let shared = &(&*host).shared;
    shared.pending_resize_w.store(width, Ordering::Release);
    shared.pending_resize_h.store(height, Ordering::Release);
    shared.has_pending_resize.store(true, Ordering::Release);
}

#[no_mangle]
pub unsafe extern "C" fn bonxie_x11_poll_dmabuf(
    host: *mut BonxieX11Host,
    last_seq: u64,
    out_dmabuf: *mut BonxieDmaBufFrame,
) -> i32 {
    if host.is_null() || out_dmabuf.is_null() {
        return -1;
    }
    let frame = {
        let Ok(guard) = (&*host).shared.latest.lock() else {
            return 0;
        };
        let Some(f) = guard.as_ref() else {
            return 0;
        };
        if f.serial <= last_seq || f.planes.is_empty() {
            return 0;
        }
        Arc::clone(f)
    };

    let num_planes = frame.planes.len().min(4);
    let mut out = BonxieDmaBufFrame {
        seq: frame.serial,
        width: frame.width,
        height: frame.height,
        fourcc: frame.fourcc,
        modifier: frame.modifier,
        num_planes: num_planes as u32,
        planes: [BonxieDmaBufPlane::default(); 4],
    };

    for (i, plane) in frame.planes.iter().take(num_planes).enumerate() {
        let dup_fd = libc::dup(plane.fd.as_raw_fd());
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
            offset: plane.offset,
            stride: plane.stride,
        };
    }

    *out_dmabuf = out;
    1
}

#[no_mangle]
pub unsafe extern "C" fn bonxie_x11_query_outputs(
    out_buf: *mut BonxieOutputInfo,
    max_count: usize,
) -> i32 {
    if out_buf.is_null() || max_count == 0 {
        return 0;
    }
    let Ok((conn, screen_num)) = x11rb::connect(None) else {
        return 0;
    };
    let root = conn.setup().roots[screen_num].root;
    let Ok(res_cookie) = conn.randr_get_screen_resources_current(root) else {
        return 0;
    };
    let Ok(res) = res_cookie.reply() else {
        return 0;
    };

    let mut count = 0usize;
    for &crtc in &res.crtcs {
        if count >= max_count {
            break;
        }
        let Ok(info_cookie) = conn.randr_get_crtc_info(crtc, res.config_timestamp) else {
            continue;
        };
        let Ok(info) = info_cookie.reply() else {
            continue;
        };
        if info.width == 0 || info.height == 0 || info.mode == 0 {
            continue;
        }
        let mut refresh_mhz = 60_000u32;
        if let Some(mode) = res.modes.iter().find(|m| m.id == info.mode) {
            let total = (mode.htotal as u64) * (mode.vtotal as u64);
            if total > 0 && mode.dot_clock > 0 {
                refresh_mhz = ((mode.dot_clock as u64 * 1000) / total) as u32;
            }
        }
        *out_buf.add(count) = BonxieOutputInfo {
            x: info.x as i32,
            y: info.y as i32,
            width: info.width as u32,
            height: info.height as u32,
            refresh_mhz,
        };
        count += 1;
    }
    count as i32
}

fn find_pid_window(conn: &RustConnection, root: Window, pid_atom: u32, target_pid: u32) -> Option<Window> {
    let tree = conn.query_tree(root).ok()?.reply().ok()?;
    for &win in tree.children.iter().rev() {
        if window_matches_pid(conn, win, pid_atom, target_pid) {
            return Some(win);
        }
        if let Ok(sub) = conn.query_tree(win).and_then(|c| Ok(c.reply())) {
            if let Ok(sub_tree) = sub {
                for &child in sub_tree.children.iter().rev() {
                    if window_matches_pid(conn, child, pid_atom, target_pid) {
                        return Some(child);
                    }
                }
            }
        }
    }
    None
}

fn window_matches_pid(conn: &RustConnection, win: Window, pid_atom: u32, target_pid: u32) -> bool {
    let Ok(cookie) = conn.get_property(false, win, pid_atom, AtomEnum::CARDINAL, 0, 1) else {
        return false;
    };
    let Ok(reply) = cookie.reply() else {
        return false;
    };
    if reply.value_len == 0 {
        return false;
    }
    reply
        .value32()
        .and_then(|mut it| it.next())
        .map(|p| p == target_pid)
        .unwrap_or(false)
}

fn run_x11_host(child_pid: u32, init_w: u32, init_h: u32, shared: Arc<SharedState>) {
    let Ok((conn, screen_num)) = x11rb::connect(None) else {
        return;
    };
    let screen = &conn.setup().roots[screen_num];
    let root = screen.root;

    let _ = conn.composite_query_version(0, 4).and_then(|c| Ok(c.reply()));
    let _ = conn.damage_query_version(1, 1).and_then(|c| Ok(c.reply()));
    let _ = conn.dri3_query_version(1, 2).and_then(|c| Ok(c.reply()));

    let Ok(pid_atom_cookie) = conn.intern_atom(false, b"_NET_WM_PID") else {
        return;
    };
    let Ok(pid_atom_reply) = pid_atom_cookie.reply() else {
        return;
    };
    let pid_atom = pid_atom_reply.atom;

    let deadline = Instant::now() + Duration::from_secs(15);
    let mut target_win = None;
    while !shared.stop.load(Ordering::Acquire) && Instant::now() < deadline {
        if let Some(w) = find_pid_window(&conn, root, pid_atom, child_pid) {
            target_win = Some(w);
            break;
        }
        thread::sleep(Duration::from_millis(20));
    }

    let Some(target_win) = target_win else {
        eprintln!("[bonxie_x11] timed out waiting for X11 window of pid {child_pid}");
        return;
    };

    // Create an override-redirect offscreen holder window so the WM hides Mugetsu
    // from taskbars/pagers while keeping its offscreen backing pixmap active.
    let Ok(holder_win) = conn.generate_id() else {
        return;
    };
    let holder_aux = CreateWindowAux::new().override_redirect(1);
    if conn
        .create_window(
            COPY_DEPTH_FROM_PARENT,
            holder_win,
            root,
            -32000,
            -32000,
            init_w.min(u16::MAX as u32) as u16,
            init_h.min(u16::MAX as u32) as u16,
            0,
            WindowClass::INPUT_OUTPUT,
            0,
            &holder_aux,
        )
        .is_err()
    {
        return;
    }

    let _ = conn.map_window(holder_win);
    let _ = conn.reparent_window(target_win, holder_win, 0, 0);
    let _ = conn.configure_window(
        target_win,
        &ConfigureWindowAux::new()
            .x(0)
            .y(0)
            .width(init_w)
            .height(init_h),
    );
    let _ = conn.map_window(target_win);
    let _ = conn.composite_redirect_window(target_win, Redirect::MANUAL);
    let _ = conn.change_window_attributes(
        target_win,
        &ChangeWindowAttributesAux::new().event_mask(EventMask::STRUCTURE_NOTIFY),
    );

    let Ok(damage_id) = conn.generate_id() else {
        let _ = conn.destroy_window(holder_win);
        return;
    };
    if conn
        .damage_create(damage_id, target_win, ReportLevel::NON_EMPTY)
        .is_err()
    {
        let _ = conn.destroy_window(holder_win);
        return;
    }
    let _ = conn.flush();

    capture_and_publish(&conn, target_win, &shared);

    let x_fd = conn.stream().as_raw_fd();
    let mut poll_fd = libc::pollfd {
        fd: x_fd,
        events: libc::POLLIN,
        revents: 0,
    };

    while !shared.stop.load(Ordering::Acquire) {
        if shared.has_pending_resize.swap(false, Ordering::AcqRel) {
            let rw = shared.pending_resize_w.load(Ordering::Acquire);
            let rh = shared.pending_resize_h.load(Ordering::Acquire);
            let _ = conn.configure_window(
                holder_win,
                &ConfigureWindowAux::new().width(rw).height(rh),
            );
            let _ = conn.configure_window(
                target_win,
                &ConfigureWindowAux::new().x(0).y(0).width(rw).height(rh),
            );
            let _ = conn.flush();
        }

        let mut need_capture = false;
        while let Ok(Some(ev)) = conn.poll_for_event() {
            match ev {
                Event::DamageNotify(_) => {
                    let _ = conn.damage_subtract(damage_id, 0u32, 0u32);
                    need_capture = true;
                }
                Event::ConfigureNotify(cfg) if cfg.window == target_win => {
                    need_capture = true;
                }
                Event::DestroyNotify(dst) if dst.window == target_win => {
                    shared.stop.store(true, Ordering::Release);
                    break;
                }
                _ => {}
            }
        }

        if need_capture {
            let _ = conn.flush();
            capture_and_publish(&conn, target_win, &shared);
        }

        if shared.stop.load(Ordering::Acquire) {
            break;
        }

        unsafe {
            libc::poll(&mut poll_fd as *mut libc::pollfd, 1, 16);
        }
    }

    let _ = conn.damage_destroy(damage_id);
    let _ = conn.composite_unredirect_window(target_win, Redirect::MANUAL);
    let _ = conn.unmap_window(holder_win);
    let _ = conn.change_window_attributes(
        target_win,
        &ChangeWindowAttributesAux::new().override_redirect(1),
    );
    let _ = conn.reparent_window(target_win, root, -32000, -32000);
    let _ = conn.sync();
    let _ = conn.destroy_window(holder_win);
    let _ = conn.flush();
}

fn capture_and_publish(conn: &RustConnection, target_win: Window, shared: &SharedState) {
    let Ok(pixmap) = conn.generate_id() else {
        return;
    };
    if conn.composite_name_window_pixmap(target_win, pixmap).is_err() {
        return;
    }

    let captured = match conn.dri3_buffers_from_pixmap(pixmap).and_then(|c| Ok(c.reply())) {
        Ok(Ok(reply)) if !reply.buffers.is_empty() => {
            let fourcc = if reply.depth == 32 {
                DRM_FORMAT_ARGB8888
            } else {
                DRM_FORMAT_XRGB8888
            };
            let n = reply
                .buffers
                .len()
                .min(reply.strides.len())
                .min(reply.offsets.len())
                .min(4);
            let mut planes = Vec::with_capacity(n);
            for (i, fd) in reply.buffers.into_iter().take(n).enumerate() {
                planes.push(CapturedPlane {
                    fd,
                    offset: reply.offsets[i],
                    stride: reply.strides[i],
                });
            }
            Some((
                reply.width as u32,
                reply.height as u32,
                fourcc,
                reply.modifier,
                planes,
            ))
        }
        err1 => {
            if shared.frame_seq.load(Ordering::Relaxed) == 0 {
                eprintln!("[bonxie_x11] dri3_buffers_from_pixmap failed: {err1:?}");
            }
            match conn.dri3_buffer_from_pixmap(pixmap).and_then(|c| Ok(c.reply())) {
                Ok(Ok(reply)) => {
                    let fourcc = if reply.depth == 32 {
                        DRM_FORMAT_ARGB8888
                    } else {
                        DRM_FORMAT_XRGB8888
                    };
                    Some((
                        reply.width as u32,
                        reply.height as u32,
                        fourcc,
                        DRM_FORMAT_MOD_INVALID,
                        vec![CapturedPlane {
                            fd: reply.pixmap_fd,
                            offset: 0,
                            stride: reply.stride as u32,
                        }],
                    ))
                }
                err2 => {
                    if shared.frame_seq.load(Ordering::Relaxed) == 0 {
                        eprintln!("[bonxie_x11] dri3_buffer_from_pixmap failed: {err2:?}");
                    }
                    None
                }
            }
        }
    };

    let _ = conn.free_pixmap(pixmap);
    let _ = conn.flush();

    let Some((width, height, fourcc, modifier, planes)) = captured else {
        return;
    };
    if width == 0 || height == 0 || planes.is_empty() {
        return;
    }

    let serial = shared.frame_seq.fetch_add(1, Ordering::AcqRel) + 1;
    let frame = Arc::new(CapturedFrame {
        serial,
        width,
        height,
        fourcc,
        modifier,
        planes,
    });
    if let Ok(mut guard) = shared.latest.lock() {
        *guard = Some(frame);
    }
    if let Ok(cb_guard) = shared.frame_cb.lock() {
        if let Some((cb, user_data)) = *cb_guard {
            cb(user_data);
        }
    }
}
