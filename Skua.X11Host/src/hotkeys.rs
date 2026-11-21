use std::collections::HashSet;
use std::os::fd::{AsRawFd, RawFd};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::{self, JoinHandle};

use x11rb::connection::Connection;
use x11rb::errors::ReplyError;
use x11rb::protocol::xkb;
use x11rb::protocol::xproto::{
    ConnectionExt as XprotoExt, GrabMode, Keysym, ModMask, Window,
};
use x11rb::protocol::Event;
use x11rb::rust_connection::RustConnection;

const MOD_SHIFT_BIT: u32 = 1 << 0;
const MOD_CONTROL_BIT: u32 = 1 << 1;
const MOD_ALT_BIT: u32 = 1 << 2;

const XK_ALT_L: Keysym = 0xFFE9;
const XK_ALT_R: Keysym = 0xFFEA;
const XK_META_L: Keysym = 0xFFE7;
const XK_META_R: Keysym = 0xFFE8;
const XK_NUM_LOCK: Keysym = 0xFF7F;

#[repr(C)]
#[derive(Clone, Copy)]
pub struct BonxieHotkeySpec {
    pub tag: u64,
    pub keysym: u32,
    pub modifiers: u32,
}

pub type HotkeyCallback = extern "C" fn(usize, u64);

struct HotkeyState {
    window_xid: u32,
    specs: Vec<BonxieHotkeySpec>,
    changed: bool,
}

struct HotkeyShared {
    stop: AtomicBool,
    wake_fd: RawFd,
    state: Mutex<HotkeyState>,
    cb: HotkeyCallback,
    user_data: usize,
}

impl HotkeyShared {
    fn wake(&self) {
        if self.wake_fd >= 0 {
            let val: u64 = 1;
            unsafe {
                libc::write(self.wake_fd, &val as *const u64 as *const libc::c_void, 8);
            }
        }
    }
}

impl Drop for HotkeyShared {
    fn drop(&mut self) {
        if self.wake_fd >= 0 {
            unsafe {
                libc::close(self.wake_fd);
            }
            self.wake_fd = -1;
        }
    }
}

pub struct BonxieX11Hotkeys {
    shared: Arc<HotkeyShared>,
    worker: Option<JoinHandle<()>>,
}

struct ActiveGrab {
    keycode: u8,
    modifiers_mask: u16,
    tag: u64,
}

#[no_mangle]
pub unsafe extern "C" fn bonxie_x11_hotkeys_create(
    cb: HotkeyCallback,
    user_data: usize,
) -> *mut BonxieX11Hotkeys {
    let wake_fd = libc::eventfd(0, libc::EFD_NONBLOCK | libc::EFD_CLOEXEC);
    let shared = Arc::new(HotkeyShared {
        stop: AtomicBool::new(false),
        wake_fd,
        state: Mutex::new(HotkeyState {
            window_xid: 0,
            specs: Vec::new(),
            changed: false,
        }),
        cb,
        user_data,
    });

    let thread_shared = Arc::clone(&shared);
    let Ok(worker) = thread::Builder::new()
        .name("bonxie-x11-hotkeys".to_string())
        .spawn(move || run_hotkeys(thread_shared))
    else {
        return std::ptr::null_mut();
    };

    Box::into_raw(Box::new(BonxieX11Hotkeys {
        shared,
        worker: Some(worker),
    }))
}

#[no_mangle]
pub unsafe extern "C" fn bonxie_x11_hotkeys_update(
    handle: *mut BonxieX11Hotkeys,
    window_xid: u32,
    specs: *const BonxieHotkeySpec,
    count: usize,
) {
    if handle.is_null() {
        return;
    }
    let slice = if count == 0 || specs.is_null() {
        &[]
    } else {
        std::slice::from_raw_parts(specs, count)
    };

    let shared = &(&*handle).shared;
    if let Ok(mut guard) = shared.state.lock() {
        guard.window_xid = window_xid;
        guard.specs.clear();
        guard.specs.extend_from_slice(slice);
        guard.changed = true;
    }
    shared.wake();
}

#[no_mangle]
pub unsafe extern "C" fn bonxie_x11_hotkeys_destroy(handle: *mut BonxieX11Hotkeys) {
    if handle.is_null() {
        return;
    }
    let mut boxed = Box::from_raw(handle);
    boxed.shared.stop.store(true, Ordering::Release);
    boxed.shared.wake();
    if let Some(worker) = boxed.worker.take() {
        let _ = worker.join();
    }
}

fn drain_wake_fd(wake_fd: RawFd) {
    if wake_fd >= 0 {
        let mut val = 0u64;
        unsafe {
            libc::read(wake_fd, &mut val as *mut u64 as *mut libc::c_void, 8);
        }
    }
}

fn wait_retry(wake_fd: RawFd, timeout_ms: i32) {
    if wake_fd < 0 {
        thread::sleep(std::time::Duration::from_millis(timeout_ms.max(0) as u64));
        return;
    }
    let mut pfd = libc::pollfd {
        fd: wake_fd,
        events: libc::POLLIN,
        revents: 0,
    };
    unsafe {
        libc::poll(&mut pfd, 1, timeout_ms);
    }
    if pfd.revents != 0 {
        drain_wake_fd(wake_fd);
    }
}

fn run_hotkeys(shared: Arc<HotkeyShared>) {
    let mut retry_delay_ms = 1_000;
    while !shared.stop.load(Ordering::Acquire) {
        if run_connection(&shared) {
            retry_delay_ms = 1_000;
        }
        if shared.stop.load(Ordering::Acquire) {
            break;
        }
        wait_retry(shared.wake_fd, retry_delay_ms);
        retry_delay_ms = (retry_delay_ms * 2).min(30_000);
    }
}

fn keysym_to_keycode(
    min_keycode: u8,
    keysyms_per_keycode: usize,
    keysyms: &[Keysym],
    target: Keysym,
) -> Option<u8> {
    if target == 0 || keysyms_per_keycode == 0 {
        return None;
    }
    let rows = keysyms.len() / keysyms_per_keycode;
    // Scan columns first (base/unshifted column 0 wins), matching XKeysymToKeycode.
    for col in 0..keysyms_per_keycode {
        for row in 0..rows {
            if keysyms[row * keysyms_per_keycode + col] == target {
                return u8::try_from(usize::from(min_keycode) + row).ok();
            }
        }
    }
    None
}

fn collect_keycodes(
    min_keycode: u8,
    keysyms_per_keycode: usize,
    keysyms: &[Keysym],
    targets: &[Keysym],
) -> Vec<u8> {
    targets
        .iter()
        .filter_map(|&sym| keysym_to_keycode(min_keycode, keysyms_per_keycode, keysyms, sym))
        .collect()
}

fn find_modifier_mask(modmap_keycodes: &[u8], target_keycodes: &[u8]) -> Option<u16> {
    if target_keycodes.is_empty() || modmap_keycodes.len() < 8 {
        return None;
    }
    let kpm = modmap_keycodes.len() / 8;
    // Modifier rows 3..8 correspond to Mod1..Mod5.
    for modifier in 3..8 {
        let start = modifier * kpm;
        let row = &modmap_keycodes[start..start + kpm];
        if row.iter().any(|&kc| kc != 0 && target_keycodes.contains(&kc)) {
            return Some(1u16 << modifier);
        }
    }
    None
}

fn ungrab_all(
    conn: &RustConnection,
    window: Window,
    grabs: &mut Vec<ActiveGrab>,
    lock_combinations: &[u16; 4],
) {
    if window != 0 {
        for grab in grabs.iter() {
            for &locks in lock_combinations {
                let _ = conn.ungrab_key(
                    grab.keycode,
                    window,
                    ModMask::from(grab.modifiers_mask | locks),
                );
            }
        }
    }
    grabs.clear();
}

fn enable_detectable_auto_repeat(conn: &RustConnection) {
    if let Ok(ext_cookie) = xkb::use_extension(conn, 1, 0) {
        if ext_cookie.reply().map(|r| r.supported).unwrap_or(false) {
            if let Ok(pcf_cookie) = xkb::per_client_flags(
                conn,
                u16::from(xkb::ID::USE_CORE_KBD),
                xkb::PerClientFlag::DETECTABLE_AUTO_REPEAT,
                xkb::PerClientFlag::DETECTABLE_AUTO_REPEAT,
                xkb::BoolCtrl::from(0u32),
                xkb::BoolCtrl::from(0u32),
                xkb::BoolCtrl::from(0u32),
            ) {
                let _ = pcf_cookie.reply();
            }
        }
    }
}

fn apply_grabs(
    conn: &RustConnection,
    window_xid: Window,
    specs: &[BonxieHotkeySpec],
    grab_window: &mut Window,
    grabs: &mut Vec<ActiveGrab>,
    held_keycodes: &mut HashSet<u8>,
    alt_mask: &mut u16,
    lock_combinations: &mut [u16; 4],
) -> bool {
    ungrab_all(conn, *grab_window, grabs, lock_combinations);
    held_keycodes.clear();
    *grab_window = window_xid;

    let min_kc = conn.setup().min_keycode;
    let max_kc = conn.setup().max_keycode;
    let kc_count = max_kc.saturating_sub(min_kc).saturating_add(1);

    let Ok(km_cookie) = conn.get_keyboard_mapping(min_kc, kc_count) else {
        return false;
    };
    let Ok(mm_cookie) = conn.get_modifier_mapping() else {
        return false;
    };
    let Ok(km) = km_cookie.reply() else {
        return false;
    };
    let Ok(mm) = mm_cookie.reply() else {
        return false;
    };

    let kpk = usize::from(km.keysyms_per_keycode);
    let alt_kcs = collect_keycodes(min_kc, kpk, &km.keysyms, &[XK_ALT_L, XK_ALT_R]);
    let meta_kcs = collect_keycodes(min_kc, kpk, &km.keysyms, &[XK_META_L, XK_META_R]);
    *alt_mask = find_modifier_mask(&mm.keycodes, &alt_kcs)
        .or_else(|| find_modifier_mask(&mm.keycodes, &meta_kcs))
        .unwrap_or_else(|| u16::from(ModMask::M1));

    let num_kcs = collect_keycodes(min_kc, kpk, &km.keysyms, &[XK_NUM_LOCK]);
    let num_lock_mask = find_modifier_mask(&mm.keycodes, &num_kcs)
        .unwrap_or_else(|| u16::from(ModMask::M2));
    let lock_m = u16::from(ModMask::LOCK);
    *lock_combinations = [0, lock_m, num_lock_mask, lock_m | num_lock_mask];

    if *grab_window == 0 {
        let _ = conn.flush();
        return true;
    }

    for spec in specs {
        let Some(keycode) = keysym_to_keycode(min_kc, kpk, &km.keysyms, spec.keysym) else {
            continue;
        };

        let mut modifiers_mask = 0u16;
        if (spec.modifiers & MOD_SHIFT_BIT) != 0 {
            modifiers_mask |= u16::from(ModMask::SHIFT);
        }
        if (spec.modifiers & MOD_CONTROL_BIT) != 0 {
            modifiers_mask |= u16::from(ModMask::CONTROL);
        }
        if (spec.modifiers & MOD_ALT_BIT) != 0 {
            modifiers_mask |= *alt_mask;
        }

        let mut cookies = Vec::with_capacity(4);
        let mut send_ok = true;
        for &locks in lock_combinations.iter() {
            match conn.grab_key(
                true,
                *grab_window,
                ModMask::from(modifiers_mask | locks),
                keycode,
                GrabMode::ASYNC,
                GrabMode::ASYNC,
            ) {
                Ok(c) => cookies.push(c),
                Err(_) => {
                    send_ok = false;
                    break;
                }
            }
        }

        let mut check_ok = send_ok;
        let mut conn_broken = false;
        for cookie in cookies {
            if let Err(err) = cookie.check() {
                check_ok = false;
                if matches!(err, ReplyError::ConnectionError(_)) {
                    conn_broken = true;
                }
            }
        }

        if conn_broken {
            return false;
        }

        if !check_ok {
            for &locks in lock_combinations.iter() {
                let _ = conn.ungrab_key(
                    keycode,
                    *grab_window,
                    ModMask::from(modifiers_mask | locks),
                );
            }
            continue;
        }

        grabs.push(ActiveGrab {
            keycode,
            modifiers_mask,
            tag: spec.tag,
        });
    }

    conn.flush().is_ok()
}

fn run_connection(shared: &Arc<HotkeyShared>) -> bool {
    let Ok((conn, _)) = x11rb::connect(None) else {
        return false;
    };

    enable_detectable_auto_repeat(&conn);

    let mut needs_regrab = true;
    let mut grab_window: Window = 0;
    let mut grabs: Vec<ActiveGrab> = Vec::new();
    let mut held_keycodes: HashSet<u8> = HashSet::new();
    let mut alt_mask = u16::from(ModMask::M1);
    let mut lock_combinations = [
        0,
        u16::from(ModMask::LOCK),
        u16::from(ModMask::M2),
        u16::from(ModMask::LOCK) | u16::from(ModMask::M2),
    ];

    let x11_fd = conn.stream().as_raw_fd();
    let wake_fd = shared.wake_fd;

    while !shared.stop.load(Ordering::Acquire) {
        let regrab_req = {
            let Ok(mut st) = shared.state.lock() else {
                break;
            };
            if needs_regrab || st.changed {
                st.changed = false;
                needs_regrab = false;
                Some((st.window_xid, st.specs.clone()))
            } else {
                None
            }
        };

        if let Some((window_xid, specs)) = regrab_req {
            if !apply_grabs(
                &conn,
                window_xid,
                &specs,
                &mut grab_window,
                &mut grabs,
                &mut held_keycodes,
                &mut alt_mask,
                &mut lock_combinations,
            ) {
                return true;
            }
        }

        let mut disconnected = false;
        loop {
            match conn.poll_for_event() {
                Ok(Some(Event::MappingNotify(_))) => {
                    needs_regrab = true;
                }
                Ok(Some(Event::KeyRelease(kr))) => {
                    held_keycodes.remove(&kr.detail);
                }
                Ok(Some(Event::KeyPress(kp))) => {
                    if !held_keycodes.insert(kp.detail) {
                        continue;
                    }
                    let relevant =
                        u16::from(ModMask::SHIFT) | u16::from(ModMask::CONTROL) | alt_mask;
                    let state = u16::from(kp.state) & relevant;
                    if let Some(grab) = grabs
                        .iter()
                        .find(|g| g.keycode == kp.detail && g.modifiers_mask == state)
                    {
                        (shared.cb)(shared.user_data, grab.tag);
                    }
                }
                Ok(Some(_)) => {}
                Ok(None) => break,
                Err(_) => {
                    disconnected = true;
                    break;
                }
            }
        }

        if disconnected || shared.stop.load(Ordering::Acquire) {
            break;
        }
        if needs_regrab {
            continue;
        }

        let mut pfds = [
            libc::pollfd {
                fd: x11_fd,
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
        let timeout_ms = if wake_fd >= 0 { -1 } else { 250 };
        unsafe {
            libc::poll(pfds.as_mut_ptr(), nfds, timeout_ms);
        }
        if wake_fd >= 0 && pfds[1].revents != 0 {
            drain_wake_fd(wake_fd);
        }
        if (pfds[0].revents & (libc::POLLERR | libc::POLLHUP | libc::POLLNVAL)) != 0 {
            break;
        }
    }

    ungrab_all(&conn, grab_window, &mut grabs, &lock_combinations);
    let _ = conn.flush();
    true
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn keysym_to_keycode_prefers_base_column() {
        // min_keycode = 10, 2 keysyms per keycode, 2 rows (10 and 11).
        // Row 0 (kc 10): [0x0061 ('a'), 0xFFBE (F1)]  <- F1 only in shifted column 1
        // Row 1 (kc 11): [0xFFBE (F1),  0x0000]       <- F1 in base column 0
        let keysyms = [0x0061, 0xFFBE, 0xFFBE, 0];
        assert_eq!(keysym_to_keycode(10, 2, &keysyms, 0xFFBE), Some(11));
        assert_eq!(keysym_to_keycode(10, 2, &keysyms, 0x0061), Some(10));
        assert_eq!(keysym_to_keycode(10, 2, &keysyms, 0x1234), None);
        assert_eq!(keysym_to_keycode(10, 2, &keysyms, 0), None);
    }

    #[test]
    fn find_modifier_mask_scans_mod1_to_mod5() {
        // 8 rows * 2 keycodes per modifier = 16 entries.
        // Put keycode 64 (Alt_L) in row 3 (Mod1 -> 1<<3 = 8), keycode 77 (Num_Lock) in row 4 (Mod2 -> 1<<4 = 16).
        let mut modmap = vec![0u8; 16];
        modmap[3 * 2] = 64;
        modmap[4 * 2 + 1] = 77;

        assert_eq!(find_modifier_mask(&modmap, &[64]), Some(1 << 3));
        assert_eq!(find_modifier_mask(&modmap, &[77]), Some(1 << 4));
        assert_eq!(find_modifier_mask(&modmap, &[99]), None);
        assert_eq!(find_modifier_mask(&modmap, &[]), None);
    }
}
