//! C ABI. All functions return 0 on success and a negative code on failure; none unwind across the boundary.
//! Header: `include/openkrisp_engine.h` (kept in sync by hand).
//! Codes: -1 bad argument, -2 processing/session error, -3 panic, -4 not available in this build (no `audio` feature).

use std::ffi::{CStr, c_char, c_int, c_void};
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::sync::{Arc, Mutex};

use crate::{Engine, Lane, VoiceIsolation};

const OK: c_int = 0;
const ERR_ARG: c_int = -1;
const ERR_FAILED: c_int = -2;
const ERR_PANIC: c_int = -3;
#[cfg_attr(feature = "audio", allow(dead_code))]
const ERR_UNAVAILABLE: c_int = -4;

/// What the C handle points at: the engine plus the running audio session (if any). Dropping the handle stops the
/// session before the engine goes away.
struct Handle {
    engine: Arc<Engine>,
    #[cfg(feature = "audio")]
    session: Mutex<Option<crate::session::Session>>,
    #[cfg(not(feature = "audio"))]
    #[allow(dead_code)]
    session: Mutex<Option<()>>,
}

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

#[repr(C)]
pub struct VcAudioConfig {
    /// Capture device name (UTF-8), NULL = default input.
    pub mic_input: *const c_char,
    /// 0 = discard (mic lane off), 1 = MicyWMD virtual microphone (Windows), 2 = a render device (`mic_sink_device`).
    pub mic_sink: i32,
    /// For `mic_sink == 2`: render device name, NULL = default output.
    pub mic_sink_device: *const c_char,
    /// For `mic_sink == 1`: PCM format written to the driver. 0 = i32, 1 = f32, 2 = i16.
    pub micy_format: i32,
    /// Render device to loop back for the incoming-audio lane; NULL = that lane off.
    pub speaker_loopback: *const c_char,
    /// Where cleaned incoming audio is played; NULL = default output.
    pub speaker_output: *const c_char,
}

#[repr(C)]
pub struct VcAudioStats {
    pub running: u8,
    pub reserved: [u8; 7],
    pub mic_input_dropped: u64,
    pub mic_output_underrun_frames: u64,
    pub speaker_input_dropped: u64,
    pub speaker_output_underrun_frames: u64,
    pub mic_worker_max_block_us: u64,
    pub speaker_worker_max_block_us: u64,
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
        let h = Handle {
            engine: Arc::new(e),
            session: Mutex::new(None),
        };
        Ok(Box::into_raw(Box::new(h)) as *mut c_void)
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

/// Stops any running audio session, then frees the engine.
///
/// # Safety
/// `h` must come from `vc_engine_create` and not be used afterwards.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn vc_engine_destroy(h: *mut c_void) {
    if !h.is_null() {
        let _ = catch_unwind(AssertUnwindSafe(|| {
            drop(unsafe { Box::from_raw(h as *mut Handle) })
        }));
    }
}

fn with_handle(h: *mut c_void, f: impl FnOnce(&Handle) -> c_int) -> c_int {
    if h.is_null() {
        return ERR_ARG;
    }
    catch_unwind(AssertUnwindSafe(|| f(unsafe { &*(h as *const Handle) }))).unwrap_or(ERR_PANIC)
}

fn with_engine(h: *mut c_void, f: impl FnOnce(&Engine) -> c_int) -> c_int {
    with_handle(h, |hd| f(&hd.engine))
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

/// `mode`: 0 = off, 1 = auto. Stored only; see the capability flags in the status.
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

/// Writes the names of audio devices, one per line (UTF-8, `\n`-separated, NUL-terminated), into `buf`.
/// `kind`: 0 = capture devices, 1 = render devices. Returns the number of bytes written (without the NUL),
/// or a negative code (-1 if `cap` is too small, -4 if this build has no audio support).
///
/// # Safety
/// `buf` must be valid for `cap` bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn vc_audio_list_devices(kind: c_int, buf: *mut c_char, cap: usize) -> c_int {
    #[cfg(feature = "audio")]
    {
        if buf.is_null() || cap == 0 || !(0..=1).contains(&kind) {
            return ERR_ARG;
        }
        catch_unwind(AssertUnwindSafe(|| {
            let names = if kind == 0 {
                live::devices::list_inputs()
            } else {
                live::devices::list_outputs()
            };
            match names {
                Err(_) => ERR_FAILED,
                Ok(v) => {
                    let text = v.join("\n");
                    if text.len() + 1 > cap {
                        return ERR_ARG;
                    }
                    write_err(buf, cap, &text);
                    text.len() as c_int
                }
            }
        }))
        .unwrap_or(ERR_PANIC)
    }
    #[cfg(not(feature = "audio"))]
    {
        let _ = (kind, buf, cap);
        ERR_UNAVAILABLE
    }
}

#[cfg(feature = "audio")]
unsafe fn opt_str(p: *const c_char) -> Result<Option<String>, String> {
    if p.is_null() {
        return Ok(None);
    }
    unsafe { CStr::from_ptr(p) }
        .to_str()
        .map(|s| Some(s.to_owned()))
        .map_err(|e| e.to_string())
}

/// Starts live audio: devices -> engine lanes -> sinks. Replaces a session that is already running. On failure
/// returns a negative code and writes a message into `err`.
///
/// # Safety
/// `cfg` must point to a valid `VcAudioConfig` whose strings are NUL-terminated or NULL; `err` null or valid for `err_cap` bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn vc_audio_start(
    h: *mut c_void,
    cfg: *const VcAudioConfig,
    err: *mut c_char,
    err_cap: usize,
) -> c_int {
    #[cfg(feature = "audio")]
    {
        use crate::session::{MicSink, Session, SessionConfig};
        use virtualmic::convert::SampleFormat;
        if cfg.is_null() {
            return ERR_ARG;
        }
        let rc = with_handle(h, |hd| {
            let c = unsafe { &*cfg };
            let build = || -> Result<SessionConfig, String> {
                let mic_sink = match c.mic_sink {
                    0 => MicSink::Discard,
                    1 => MicSink::Micy(match c.micy_format {
                        0 => SampleFormat::I32,
                        1 => SampleFormat::F32,
                        2 => SampleFormat::I16,
                        _ => return Err("unknown micy_format".into()),
                    }),
                    2 => MicSink::Output(unsafe { opt_str(c.mic_sink_device) }?),
                    _ => return Err("unknown mic_sink".into()),
                };
                Ok(SessionConfig {
                    mic_input: unsafe { opt_str(c.mic_input) }?,
                    mic_sink,
                    speaker_loopback: unsafe { opt_str(c.speaker_loopback) }?,
                    speaker_output: unsafe { opt_str(c.speaker_output) }?,
                })
            };
            let config = match build() {
                Ok(v) => v,
                Err(m) => {
                    write_err(err, err_cap, &m);
                    return ERR_ARG;
                }
            };
            let mut slot = match hd.session.lock() {
                Ok(s) => s,
                Err(_) => return ERR_FAILED,
            };
            if let Some(old) = slot.take() {
                old.stop();
            }
            match Session::start(hd.engine.clone(), config) {
                Ok(s) => {
                    *slot = Some(s);
                    OK
                }
                Err(e) => {
                    write_err(err, err_cap, &format!("{e:#}"));
                    ERR_FAILED
                }
            }
        });
        rc
    }
    #[cfg(not(feature = "audio"))]
    {
        let _ = (h, cfg);
        write_err(
            err,
            err_cap,
            "this build of the engine has no live audio support",
        );
        ERR_UNAVAILABLE
    }
}

/// Stops the audio session (no-op if none is running).
///
/// # Safety
/// `h` must be a live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn vc_audio_stop(h: *mut c_void) -> c_int {
    with_handle(h, |hd| {
        #[cfg(feature = "audio")]
        if let Ok(mut slot) = hd.session.lock() {
            if let Some(s) = slot.take() {
                s.stop();
            }
        }
        #[cfg(not(feature = "audio"))]
        let _ = hd;
        OK
    })
}

/// # Safety
/// `h` must be a live handle; `out` valid for one `VcAudioStats`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn vc_audio_get_stats(h: *mut c_void, out: *mut VcAudioStats) -> c_int {
    if out.is_null() {
        return ERR_ARG;
    }
    with_handle(h, |hd| {
        #[cfg(feature = "audio")]
        let s = hd
            .session
            .lock()
            .ok()
            .and_then(|g| g.as_ref().map(|s| s.stats()))
            .unwrap_or_default();
        #[cfg(not(feature = "audio"))]
        let s = {
            let _ = hd;
            Default::default()
        };
        let s: SessionStatsView = s;
        unsafe {
            *out = VcAudioStats {
                running: s.running as u8,
                reserved: [0; 7],
                mic_input_dropped: s.mic_input_dropped,
                mic_output_underrun_frames: s.mic_output_underrun_frames,
                speaker_input_dropped: s.speaker_input_dropped,
                speaker_output_underrun_frames: s.speaker_output_underrun_frames,
                mic_worker_max_block_us: s.mic_worker_max_block_us,
                speaker_worker_max_block_us: s.speaker_worker_max_block_us,
            };
        }
        OK
    })
}

#[cfg(feature = "audio")]
type SessionStatsView = crate::session::SessionStats;

/// Same fields as `session::SessionStats`, for builds without the audio feature (always zero).
#[cfg(not(feature = "audio"))]
#[derive(Default)]
struct SessionStatsView {
    running: bool,
    mic_input_dropped: u64,
    mic_output_underrun_frames: u64,
    speaker_input_dropped: u64,
    speaker_output_underrun_frames: u64,
    mic_worker_max_block_us: u64,
    speaker_worker_max_block_us: u64,
}
