//! C ABI. All functions return 0 on success and a negative code on failure; none unwind across the boundary.
//! Header: `include/openkrisp_engine.h` (kept in sync by hand).

use std::ffi::{CStr, c_char, c_int, c_void};
use std::panic::{AssertUnwindSafe, catch_unwind};

use crate::{Engine, Lane, VoiceIsolation};

const OK: c_int = 0;
const ERR_ARG: c_int = -1;
const ERR_FAILED: c_int = -2;
const ERR_PANIC: c_int = -3;

#[repr(C)]
pub struct VcStatus {
    pub mic_nc: u8,
    pub speaker_nc: u8,
    pub voice_isolation: u8,
    pub voice_isolation_active: u8,
    pub echo_cancel_active: u8,
    pub nearby_voice_removal_active: u8,
    pub reserved: [u8; 2],
    pub sample_rate: u32,
    pub block: u32,
    pub mic_samples: u64,
    pub speaker_samples: u64,
}

fn write_err(buf: *mut c_char, cap: usize, msg: &str) {
    if buf.is_null() || cap == 0 {
        return;
    }
    let n = msg.len().min(cap - 1);
    // SAFETY: caller guarantees `buf` is valid for `cap` bytes.
    unsafe {
        std::ptr::copy_nonoverlapping(msg.as_ptr(), buf as *mut u8, n);
        *buf.add(n) = 0;
    }
}

/// Returns null on failure and writes a message into `err`.
///
/// # Safety
/// `model_path_utf8` must be NUL-terminated; `err` must be null or valid for `err_cap` bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn vc_engine_create(
    model_path_utf8: *const c_char,
    sample_rate: u32,
    err: *mut c_char,
    err_cap: usize,
) -> *mut c_void {
    let r = catch_unwind(AssertUnwindSafe(|| -> Result<*mut c_void, String> {
        if model_path_utf8.is_null() {
            return Err("null model path".into());
        }
        let path = unsafe { CStr::from_ptr(model_path_utf8) }
            .to_str()
            .map_err(|e| e.to_string())?;
        let e = Engine::new(path, sample_rate).map_err(|e| format!("{e:#}"))?;
        Ok(Box::into_raw(Box::new(e)) as *mut c_void)
    }));
    match r {
        Ok(Ok(p)) => p,
        Ok(Err(m)) => {
            write_err(err, err_cap, &m);
            std::ptr::null_mut()
        }
        Err(_) => {
            write_err(err, err_cap, "panic in vc_engine_create");
            std::ptr::null_mut()
        }
    }
}

/// # Safety
/// `h` must come from `vc_engine_create` and not be used afterwards.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn vc_engine_destroy(h: *mut c_void) {
    if !h.is_null() {
        let _ = catch_unwind(AssertUnwindSafe(|| {
            drop(unsafe { Box::from_raw(h as *mut Engine) })
        }));
    }
}

fn with_engine(h: *mut c_void, f: impl FnOnce(&Engine) -> c_int) -> c_int {
    if h.is_null() {
        return ERR_ARG;
    }
    catch_unwind(AssertUnwindSafe(|| f(unsafe { &*(h as *const Engine) }))).unwrap_or(ERR_PANIC)
}

/// # Safety
/// `h` must be a live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn vc_set_mic_nc(h: *mut c_void, on: c_int) -> c_int {
    with_engine(h, |e| {
        e.set_noise_cancel(Lane::Mic, on != 0);
        OK
    })
}

/// # Safety
/// `h` must be a live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn vc_set_speaker_nc(h: *mut c_void, on: c_int) -> c_int {
    with_engine(h, |e| {
        e.set_noise_cancel(Lane::Speaker, on != 0);
        OK
    })
}

/// `mode`: 0 = off, 1 = auto. Stored only; see `voice_isolation_active` in the status.
///
/// # Safety
/// `h` must be a live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn vc_set_voice_isolation(h: *mut c_void, mode: c_int) -> c_int {
    with_engine(h, |e| match mode {
        0 => {
            e.set_voice_isolation(VoiceIsolation::Off);
            OK
        }
        1 => {
            e.set_voice_isolation(VoiceIsolation::Auto);
            OK
        }
        _ => ERR_ARG,
    })
}

/// # Safety
/// `h` must be a live handle; `out` must be valid for one `VcStatus`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn vc_get_status(h: *mut c_void, out: *mut VcStatus) -> c_int {
    if out.is_null() {
        return ERR_ARG;
    }
    with_engine(h, |e| {
        unsafe {
            *out = VcStatus {
                mic_nc: e.noise_cancel(Lane::Mic) as u8,
                speaker_nc: e.noise_cancel(Lane::Speaker) as u8,
                voice_isolation: e.voice_isolation() as u8,
                voice_isolation_active: e.voice_isolation_active() as u8,
                echo_cancel_active: e.echo_cancel_active() as u8,
                nearby_voice_removal_active: e.nearby_voice_removal_active() as u8,
                reserved: [0; 2],
                sample_rate: e.sample_rate(),
                block: e.block() as u32,
                mic_samples: e.samples_processed(Lane::Mic),
                speaker_samples: e.samples_processed(Lane::Speaker),
            };
        }
        OK
    })
}

/// `lane`: 0 = mic, 1 = speaker. Processes `n` input samples and writes up to `cap` samples to `out`; the count
/// written is stored in `*written`. Returns -1 if `cap` is too small (output for that call is dropped).
/// Choose `cap >= n + block`.
///
/// # Safety
/// `input` valid for `n` floats, `out` valid for `cap` floats, `written` valid for one `usize`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn vc_process(
    h: *mut c_void,
    lane: c_int,
    input: *const f32,
    n: usize,
    out: *mut f32,
    cap: usize,
    written: *mut usize,
) -> c_int {
    if input.is_null() || out.is_null() || written.is_null() {
        return ERR_ARG;
    }
    let lane = match lane {
        0 => Lane::Mic,
        1 => Lane::Speaker,
        _ => return ERR_ARG,
    };
    with_engine(h, |e| {
        let inp = unsafe { std::slice::from_raw_parts(input, n) };
        let mut v = Vec::with_capacity(n + e.block());
        if e.process(lane, inp, &mut v).is_err() {
            return ERR_FAILED;
        }
        if v.len() > cap {
            return ERR_ARG;
        }
        unsafe {
            std::ptr::copy_nonoverlapping(v.as_ptr(), out, v.len());
            *written = v.len();
        }
        OK
    })
}
