use std::ffi::c_void;
use std::os::fd::AsRawFd;
use std::ptr::null_mut;
use std::sync::OnceLock;

use crate::BonxieWlHost;

const GL_TEXTURE_2D: u32 = 0x0DE1;
const GL_TEXTURE_BINDING_2D: u32 = 0x8069;
const GL_TEXTURE_MIN_FILTER: u32 = 0x2801;
const GL_TEXTURE_MAG_FILTER: u32 = 0x2800;
const GL_TEXTURE_WRAP_S: u32 = 0x2802;
const GL_TEXTURE_WRAP_T: u32 = 0x2803;
const GL_LINEAR: i32 = 0x2601;
const GL_CLAMP_TO_EDGE: i32 = 0x812F;
const DRM_FORMAT_MOD_INVALID: u64 = 0x00FF_FFFF_FFFF_FFFF;

const EGL_WIDTH: i32 = 0x3057;
const EGL_HEIGHT: i32 = 0x3056;
const EGL_NONE: i32 = 0x3038;
const EGL_LINUX_DMA_BUF_EXT: i32 = 0x3270;
const EGL_LINUX_DRM_FOURCC_EXT: i32 = 0x3271;

// (FD, OFFSET, PITCH, MODIFIER_LO, MODIFIER_HI)
const PLANE_ATTRS: [(i32, i32, i32, i32, i32); 4] = [
    (0x3272, 0x3273, 0x3274, 0x3443, 0x3444),
    (0x3275, 0x3276, 0x3277, 0x3445, 0x3446),
    (0x3278, 0x3279, 0x327A, 0x3447, 0x3448),
    (0x3440, 0x3441, 0x3442, 0x3449, 0x344A),
];

type EglGetCurrentDisplayFn = unsafe extern "C" fn() -> *mut c_void;
type EglGetCurrentContextFn = unsafe extern "C" fn() -> *mut c_void;
type EglGetProcAddressFn = unsafe extern "C" fn(*const libc::c_char) -> *mut c_void;
type EglCreateImageKhrFn =
    unsafe extern "C" fn(*mut c_void, *mut c_void, i32, *mut c_void, *const i32) -> *mut c_void;
type EglDestroyImageKhrFn = unsafe extern "C" fn(*mut c_void, *mut c_void) -> i32;
type GlEglImageTargetTexture2DOesFn = unsafe extern "C" fn(u32, *mut c_void);
type GlGenTexturesFn = unsafe extern "C" fn(i32, *mut u32);
type GlDeleteTexturesFn = unsafe extern "C" fn(i32, *const u32);
type GlBindTextureFn = unsafe extern "C" fn(u32, u32);
type GlTexParameteriFn = unsafe extern "C" fn(u32, u32, i32);
type GlGetIntegervFn = unsafe extern "C" fn(u32, *mut i32);

struct EglProcs {
    get_current_display: EglGetCurrentDisplayFn,
    get_current_context: EglGetCurrentContextFn,
    create_image_khr: EglCreateImageKhrFn,
    destroy_image_khr: EglDestroyImageKhrFn,
    image_target_texture_2d: GlEglImageTargetTexture2DOesFn,
    gen_textures: GlGenTexturesFn,
    delete_textures: GlDeleteTexturesFn,
    bind_texture: GlBindTextureFn,
    tex_parameteri: GlTexParameteriFn,
    get_integerv: GlGetIntegervFn,
}

static EGL_PROCS: OnceLock<Option<EglProcs>> = OnceLock::new();

unsafe fn resolve_gl_proc(get_proc_address: EglGetProcAddressFn, name: &[u8]) -> *mut c_void {
    let ptr = get_proc_address(name.as_ptr().cast::<libc::c_char>());
    if !ptr.is_null() {
        return ptr;
    }
    libc::dlsym(libc::RTLD_DEFAULT, name.as_ptr().cast::<libc::c_char>())
}

fn get_egl_procs() -> Option<&'static EglProcs> {
    EGL_PROCS
        .get_or_init(|| unsafe {
            let mut handle = libc::dlopen(
                b"libEGL.so.1\0".as_ptr().cast::<libc::c_char>(),
                libc::RTLD_LAZY | libc::RTLD_NOLOAD,
            );
            if handle.is_null() {
                handle = libc::dlopen(
                    b"libEGL.so.1\0".as_ptr().cast::<libc::c_char>(),
                    libc::RTLD_LAZY,
                );
            }
            if handle.is_null() {
                return None;
            }

            let cur_disp_sym = libc::dlsym(
                handle,
                b"eglGetCurrentDisplay\0".as_ptr().cast::<libc::c_char>(),
            );
            let cur_ctx_sym = libc::dlsym(
                handle,
                b"eglGetCurrentContext\0".as_ptr().cast::<libc::c_char>(),
            );
            let get_proc_sym = libc::dlsym(
                handle,
                b"eglGetProcAddress\0".as_ptr().cast::<libc::c_char>(),
            );
            if cur_disp_sym.is_null() || cur_ctx_sym.is_null() || get_proc_sym.is_null() {
                return None;
            }

            let get_current_display: EglGetCurrentDisplayFn = std::mem::transmute(cur_disp_sym);
            let get_current_context: EglGetCurrentContextFn = std::mem::transmute(cur_ctx_sym);
            let get_proc_address: EglGetProcAddressFn = std::mem::transmute(get_proc_sym);

            let create_sym = resolve_gl_proc(get_proc_address, b"eglCreateImageKHR\0");
            let destroy_sym = resolve_gl_proc(get_proc_address, b"eglDestroyImageKHR\0");
            let target_sym = resolve_gl_proc(get_proc_address, b"glEGLImageTargetTexture2DOES\0");
            let gen_tex_sym = resolve_gl_proc(get_proc_address, b"glGenTextures\0");
            let del_tex_sym = resolve_gl_proc(get_proc_address, b"glDeleteTextures\0");
            let bind_tex_sym = resolve_gl_proc(get_proc_address, b"glBindTexture\0");
            let tex_param_sym = resolve_gl_proc(get_proc_address, b"glTexParameteri\0");
            let get_int_sym = resolve_gl_proc(get_proc_address, b"glGetIntegerv\0");

            if create_sym.is_null()
                || destroy_sym.is_null()
                || target_sym.is_null()
                || gen_tex_sym.is_null()
                || del_tex_sym.is_null()
                || bind_tex_sym.is_null()
                || tex_param_sym.is_null()
                || get_int_sym.is_null()
            {
                return None;
            }

            Some(EglProcs {
                get_current_display,
                get_current_context,
                create_image_khr: std::mem::transmute(create_sym),
                destroy_image_khr: std::mem::transmute(destroy_sym),
                image_target_texture_2d: std::mem::transmute(target_sym),
                gen_textures: std::mem::transmute(gen_tex_sym),
                delete_textures: std::mem::transmute(del_tex_sym),
                bind_texture: std::mem::transmute(bind_tex_sym),
                tex_parameteri: std::mem::transmute(tex_param_sym),
                get_integerv: std::mem::transmute(get_int_sym),
            })
        })
        .as_ref()
}

fn build_dmabuf_attribs(
    width: u32,
    height: u32,
    fourcc: u32,
    planes: &[(i32, u32, u32, u64)],
) -> [i32; 48] {
    let mut attribs = [EGL_NONE; 48];
    let mut idx = 0usize;

    attribs[idx] = EGL_WIDTH;
    attribs[idx + 1] = width as i32;
    attribs[idx + 2] = EGL_HEIGHT;
    attribs[idx + 3] = height as i32;
    attribs[idx + 4] = EGL_LINUX_DRM_FOURCC_EXT;
    attribs[idx + 5] = fourcc as i32;
    idx += 6;

    for (&(fd, offset, stride, modifier), &(attr_fd, attr_off, attr_pitch, attr_mod_lo, attr_mod_hi)) in
        planes.iter().take(4).zip(PLANE_ATTRS.iter())
    {
        attribs[idx] = attr_fd;
        attribs[idx + 1] = fd;
        attribs[idx + 2] = attr_off;
        attribs[idx + 3] = offset as i32;
        attribs[idx + 4] = attr_pitch;
        attribs[idx + 5] = stride as i32;
        idx += 6;

        if modifier != DRM_FORMAT_MOD_INVALID {
            attribs[idx] = attr_mod_lo;
            attribs[idx + 1] = (modifier & 0xFFFF_FFFF) as i32;
            attribs[idx + 2] = attr_mod_hi;
            attribs[idx + 3] = (modifier >> 32) as i32;
            idx += 4;
        }
    }

    attribs[idx] = EGL_NONE;
    attribs
}

#[no_mangle]
pub unsafe extern "C" fn bonxie_wl_import_dmabuf_to_bound_texture(
    host: *mut BonxieWlHost,
    last_seq: u64,
    out_seq: *mut u64,
    out_buffer_id: *mut u64,
    out_width: *mut u32,
    out_height: *mut u32,
    out_egl_display: *mut *mut c_void,
    out_egl_image: *mut *mut c_void,
) -> i32 {
    if host.is_null()
        || out_seq.is_null()
        || out_buffer_id.is_null()
        || out_width.is_null()
        || out_height.is_null()
        || out_egl_display.is_null()
        || out_egl_image.is_null()
    {
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
    if frame.planes.is_empty() {
        return 0;
    }

    let Some(procs) = get_egl_procs() else {
        return -1;
    };
    let display = (procs.get_current_display)();
    if display.is_null() {
        return -1;
    }

    let mut plane_specs = [(0i32, 0u32, 0u32, DRM_FORMAT_MOD_INVALID); 4];
    let count = frame.planes.len().min(4);
    for (i, p) in frame.planes.iter().take(count).enumerate() {
        plane_specs[i] = (p.fd.as_raw_fd(), p.offset, p.stride, p.modifier);
    }

    let attribs = build_dmabuf_attribs(
        frame.width,
        frame.height,
        frame.fourcc,
        &plane_specs[..count],
    );
    let img = (procs.create_image_khr)(
        display,
        null_mut(),
        EGL_LINUX_DMA_BUF_EXT,
        null_mut(),
        attribs.as_ptr(),
    );
    if img.is_null() {
        eprintln!(
            "[WaylandDmabuf] eglCreateImageKHR failed for {}x{} fourcc=0x{:08X}",
            frame.width, frame.height, frame.fourcc
        );
        return -1;
    }

    (procs.image_target_texture_2d)(GL_TEXTURE_2D, img);
    h.notify_frame_acquired(published.serial);

    *out_seq = published.serial;
    *out_buffer_id = frame.id;
    *out_width = frame.width;
    *out_height = frame.height;
    *out_egl_display = display;
    *out_egl_image = img;
    1
}

/// Imports the latest published DMA-BUF into a fresh GL 2D texture and `EGLImage`.
/// Intentionally does NOT ack/release the buffer: the caller MUST call `bonxie_wl_ack_frame`
/// only after `grContext.Flush()` so `wl_buffer.release` cannot race with Skia's GPU read.
/// Saves and restores `GL_TEXTURE_BINDING_2D` to keep Skia's cached GL state valid.
#[no_mangle]
pub unsafe extern "C" fn bonxie_wl_import_dmabuf_texture(
    host: *mut BonxieWlHost,
    last_seq: u64,
    out_seq: *mut u64,
    out_buffer_id: *mut u64,
    out_width: *mut u32,
    out_height: *mut u32,
    out_gl_tex: *mut u32,
    out_egl_display: *mut *mut c_void,
    out_egl_image: *mut *mut c_void,
) -> i32 {
    if host.is_null()
        || out_seq.is_null()
        || out_buffer_id.is_null()
        || out_width.is_null()
        || out_height.is_null()
        || out_gl_tex.is_null()
        || out_egl_display.is_null()
        || out_egl_image.is_null()
    {
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
    if frame.planes.is_empty() {
        return 0;
    }

    let Some(procs) = get_egl_procs() else {
        return -1;
    };
    let display = (procs.get_current_display)();
    let ctx = (procs.get_current_context)();
    if display.is_null() || ctx.is_null() {
        return -1;
    }

    let mut plane_specs = [(0i32, 0u32, 0u32, DRM_FORMAT_MOD_INVALID); 4];
    let count = frame.planes.len().min(4);
    for (i, p) in frame.planes.iter().take(count).enumerate() {
        plane_specs[i] = (p.fd.as_raw_fd(), p.offset, p.stride, p.modifier);
    }

    let attribs = build_dmabuf_attribs(
        frame.width,
        frame.height,
        frame.fourcc,
        &plane_specs[..count],
    );
    let img = (procs.create_image_khr)(
        display,
        null_mut(),
        EGL_LINUX_DMA_BUF_EXT,
        null_mut(),
        attribs.as_ptr(),
    );
    if img.is_null() {
        eprintln!(
            "[WaylandDmabuf] eglCreateImageKHR failed for {}x{} fourcc=0x{:08X}",
            frame.width, frame.height, frame.fourcc
        );
        return -1;
    }

    let mut prev_tex: i32 = 0;
    (procs.get_integerv)(GL_TEXTURE_BINDING_2D, &mut prev_tex);

    let mut tex: u32 = 0;
    (procs.gen_textures)(1, &mut tex);
    if tex == 0 {
        (procs.destroy_image_khr)(display, img);
        return -1;
    }

    (procs.bind_texture)(GL_TEXTURE_2D, tex);
    (procs.tex_parameteri)(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
    (procs.tex_parameteri)(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
    (procs.tex_parameteri)(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
    (procs.tex_parameteri)(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
    (procs.image_target_texture_2d)(GL_TEXTURE_2D, img);
    (procs.bind_texture)(GL_TEXTURE_2D, prev_tex as u32);

    *out_seq = published.serial;
    *out_buffer_id = frame.id;
    *out_width = frame.width;
    *out_height = frame.height;
    *out_gl_tex = tex;
    *out_egl_display = display;
    *out_egl_image = img;
    1
}

/// Rebinds an existing GL 2D texture to its imported `EGLImage` on cached `buffer_id` hits;
/// skipping `glEGLImageTargetTexture2DOES` on reuse risks stale reads on AMD `radeonsi`
/// DCC-compressed DMA-BUFs across processes. Preserves `GL_TEXTURE_BINDING_2D` for Skia.
#[no_mangle]
pub unsafe extern "C" fn bonxie_wl_rebind_dmabuf_texture(
    gl_tex: u32,
    egl_image: *mut c_void,
) -> i32 {
    if gl_tex == 0 || egl_image.is_null() {
        return 0;
    }
    let Some(procs) = get_egl_procs() else {
        return -1;
    };
    if (procs.get_current_context)().is_null() {
        return 0;
    }

    let mut prev_tex: i32 = 0;
    (procs.get_integerv)(GL_TEXTURE_BINDING_2D, &mut prev_tex);
    (procs.bind_texture)(GL_TEXTURE_2D, gl_tex);
    (procs.image_target_texture_2d)(GL_TEXTURE_2D, egl_image);
    (procs.bind_texture)(GL_TEXTURE_2D, prev_tex as u32);
    1
}

#[no_mangle]
pub unsafe extern "C" fn bonxie_wl_destroy_dmabuf_texture(
    egl_display: *mut c_void,
    egl_image: *mut c_void,
    gl_tex: u32,
) {
    let Some(procs) = get_egl_procs() else {
        return;
    };
    if gl_tex != 0 && !(procs.get_current_context)().is_null() {
        (procs.delete_textures)(1, &gl_tex);
    }
    if !egl_image.is_null() {
        let display = if !egl_display.is_null() {
            egl_display
        } else {
            (procs.get_current_display)()
        };
        if !display.is_null() {
            (procs.destroy_image_khr)(display, egl_image);
        }
    }
}

#[no_mangle]
pub unsafe extern "C" fn bonxie_wl_destroy_egl_image(
    egl_display: *mut c_void,
    egl_image: *mut c_void,
) {
    if egl_image.is_null() {
        return;
    }
    let Some(procs) = get_egl_procs() else {
        return;
    };
    let display = if !egl_display.is_null() {
        egl_display
    } else {
        (procs.get_current_display)()
    };
    if !display.is_null() {
        (procs.destroy_image_khr)(display, egl_image);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn as_used_slice(attribs: &[i32; 48]) -> &[i32] {
        let end = attribs
            .iter()
            .position(|&v| v == EGL_NONE)
            .expect("EGL_NONE terminator missing");
        &attribs[..=end]
    }

    #[test]
    fn dmabuf_attribs_include_linear_modifier_zero() {
        // DRM_FORMAT_MOD_LINEAR == 0 MUST still emit MODIFIER_LO / MODIFIER_HI.
        let attribs = build_dmabuf_attribs(1280, 720, 0x34325258, &[(42, 0, 5120, 0)]);
        assert_eq!(
            as_used_slice(&attribs),
            &[
                EGL_WIDTH,
                1280,
                EGL_HEIGHT,
                720,
                EGL_LINUX_DRM_FOURCC_EXT,
                0x34325258,
                PLANE_ATTRS[0].0,
                42,
                PLANE_ATTRS[0].1,
                0,
                PLANE_ATTRS[0].2,
                5120,
                PLANE_ATTRS[0].3,
                0,
                PLANE_ATTRS[0].4,
                0,
                EGL_NONE,
            ]
        );
    }

    #[test]
    fn dmabuf_attribs_encode_nonzero_64bit_modifier() {
        let modifier = 0x0100_0000_1234_5678u64;
        let attribs = build_dmabuf_attribs(800, 600, 0x34325241, &[(7, 64, 3200, modifier)]);
        assert_eq!(
            as_used_slice(&attribs),
            &[
                EGL_WIDTH,
                800,
                EGL_HEIGHT,
                600,
                EGL_LINUX_DRM_FOURCC_EXT,
                0x34325241,
                PLANE_ATTRS[0].0,
                7,
                PLANE_ATTRS[0].1,
                64,
                PLANE_ATTRS[0].2,
                3200,
                PLANE_ATTRS[0].3,
                0x1234_5678u32 as i32,
                PLANE_ATTRS[0].4,
                0x0100_0000u32 as i32,
                EGL_NONE,
            ]
        );
    }

    #[test]
    fn dmabuf_attribs_skip_invalid_modifier() {
        let attribs = build_dmabuf_attribs(
            640,
            480,
            0x34325258,
            &[(9, 0, 2560, DRM_FORMAT_MOD_INVALID)],
        );
        assert_eq!(
            as_used_slice(&attribs),
            &[
                EGL_WIDTH,
                640,
                EGL_HEIGHT,
                480,
                EGL_LINUX_DRM_FOURCC_EXT,
                0x34325258,
                PLANE_ATTRS[0].0,
                9,
                PLANE_ATTRS[0].1,
                0,
                PLANE_ATTRS[0].2,
                2560,
                EGL_NONE,
            ]
        );
    }
}
