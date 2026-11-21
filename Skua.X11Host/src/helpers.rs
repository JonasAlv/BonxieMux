use std::sync::{Mutex, OnceLock};

use x11rb::connection::Connection;
use x11rb::errors::ReplyError;
use x11rb::protocol::xproto::{ConnectionExt as XprotoExt, Window};
use x11rb::rust_connection::RustConnection;

pub(crate) enum UtilErr {
    Conn,
    X11,
}

static UTIL_CONN: OnceLock<Mutex<Option<(RustConnection, Window)>>> = OnceLock::new();

pub(crate) fn with_util_conn<T, F>(mut f: F) -> Option<T>
where
    F: FnMut(&RustConnection, Window) -> Result<T, UtilErr>,
{
    let cell = UTIL_CONN.get_or_init(|| Mutex::new(None));
    let Ok(mut guard) = cell.lock() else {
        return None;
    };

    for _attempt in 0..2 {
        if guard.is_none() {
            let Ok((conn, screen_num)) = x11rb::connect(None) else {
                return None;
            };
            let root = conn.setup().roots[screen_num].root;
            *guard = Some((conn, root));
        }

        if let Some((conn, root)) = guard.as_ref() {
            match f(conn, *root) {
                Ok(val) => return Some(val),
                Err(UtilErr::X11) => return None,
                Err(UtilErr::Conn) => {
                    *guard = None;
                }
            }
        }
    }

    None
}

fn map_reply_err(err: ReplyError) -> UtilErr {
    match err {
        ReplyError::ConnectionError(_) => UtilErr::Conn,
        ReplyError::X11Error(_) => UtilErr::X11,
    }
}

#[no_mangle]
pub unsafe extern "C" fn bonxie_x11_init_threads() -> i32 {
    let handle = libc::dlopen(
        b"libX11.so.6\0".as_ptr().cast::<libc::c_char>(),
        libc::RTLD_LAZY,
    );
    if handle.is_null() {
        return 0;
    }

    let sym = libc::dlsym(handle, b"XInitThreads\0".as_ptr().cast::<libc::c_char>());
    if sym.is_null() {
        return 0;
    }

    let init_threads: extern "C" fn() -> libc::c_int = std::mem::transmute(sym);
    init_threads()
}

#[no_mangle]
pub unsafe extern "C" fn bonxie_x11_translate_to_root(
    window_xid: u32,
    out_x: *mut i32,
    out_y: *mut i32,
) -> i32 {
    if window_xid == 0 || out_x.is_null() || out_y.is_null() {
        return 0;
    }

    let Some(coords) = with_util_conn(|conn, root| {
        let cookie = conn
            .translate_coordinates(window_xid, root, 0, 0)
            .map_err(|_| UtilErr::Conn)?;
        let reply = cookie.reply().map_err(map_reply_err)?;
        if reply.same_screen {
            Ok(Some((i32::from(reply.dst_x), i32::from(reply.dst_y))))
        } else {
            Ok(None)
        }
    })
    else {
        return 0;
    };

    if let Some((x, y)) = coords {
        *out_x = x;
        *out_y = y;
        1
    } else {
        0
    }
}

#[no_mangle]
pub unsafe extern "C" fn bonxie_x11_query_pointer_root(out_x: *mut i32, out_y: *mut i32) -> i32 {
    if out_x.is_null() || out_y.is_null() {
        return 0;
    }

    let Some(coords) = with_util_conn(|conn, root| {
        let cookie = conn.query_pointer(root).map_err(|_| UtilErr::Conn)?;
        let reply = cookie.reply().map_err(map_reply_err)?;
        if reply.same_screen {
            Ok(Some((i32::from(reply.root_x), i32::from(reply.root_y))))
        } else {
            Ok(None)
        }
    })
    else {
        return 0;
    };

    if let Some((x, y)) = coords {
        *out_x = x;
        *out_y = y;
        1
    } else {
        0
    }
}

#[no_mangle]
pub unsafe extern "C" fn bonxie_x11_bell(percent: i32) -> i32 {
    let clamped = percent.clamp(-100, 100) as i8;
    let ok = with_util_conn(|conn, _root| {
        conn.bell(clamped).map_err(|_| UtilErr::Conn)?;
        conn.flush().map_err(|_| UtilErr::Conn)?;
        Ok(())
    });
    i32::from(ok.is_some())
}
