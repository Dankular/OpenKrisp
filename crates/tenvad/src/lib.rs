//! Safe wrapper over the TEN VAD C ABI (`include/ten_vad.h`).
//!
//! 16 kHz mono int16 input, one call per hop. Per `ten_vad.h` the hop is the frame size passed to
//! `ten_vad_create` (e.g. 256); the first call is observed to return probability -1.0 / flag -1
//! (no decision yet).

use std::ffi::{CStr, c_char, c_float, c_int, c_void};

use anyhow::{Result, bail};

unsafe extern "C" {
    fn ten_vad_create(handle: *mut *mut c_void, hop_size: usize, threshold: c_float) -> c_int;
    fn ten_vad_process(
        handle: *mut c_void,
        audio: *const i16,
        len: usize,
        prob: *mut c_float,
        flag: *mut c_int,
    ) -> c_int;
    fn ten_vad_destroy(handle: *mut *mut c_void) -> c_int;
    fn ten_vad_get_version() -> *const c_char;
}

pub struct TenVad {
    handle: *mut c_void,
    hop: usize,
}

// The handle is an owned opaque pointer with no thread affinity documented in ten_vad.h;
// we only allow moving it between threads, never sharing (&mut self on process).
unsafe impl Send for TenVad {}

impl TenVad {
    pub fn new(hop_size: usize, threshold: f32) -> Result<Self> {
        let mut handle = std::ptr::null_mut();
        // SAFETY: out-pointer is valid for the call.
        if unsafe { ten_vad_create(&mut handle, hop_size, threshold) } != 0 || handle.is_null() {
            bail!("ten_vad_create failed (hop {hop_size}, threshold {threshold})");
        }
        Ok(Self {
            handle,
            hop: hop_size,
        })
    }

    pub fn hop(&self) -> usize {
        self.hop
    }

    /// Process exactly one hop. Returns `(probability, flag)`.
    pub fn process(&mut self, frame: &[i16]) -> Result<(f32, i32)> {
        if frame.len() != self.hop {
            bail!("frame has {} samples, hop is {}", frame.len(), self.hop);
        }
        let (mut prob, mut flag) = (0.0f32, 0i32);
        // SAFETY: handle came from ten_vad_create; frame/out pointers are valid for the call.
        let rc = unsafe {
            ten_vad_process(
                self.handle,
                frame.as_ptr(),
                frame.len(),
                &mut prob,
                &mut flag,
            )
        };
        if rc != 0 {
            bail!("ten_vad_process failed");
        }
        Ok((prob, flag))
    }
}

impl Drop for TenVad {
    fn drop(&mut self) {
        // SAFETY: handle is valid and destroyed once; the call nulls it.
        unsafe { ten_vad_destroy(&mut self.handle) };
    }
}

pub fn version() -> String {
    // SAFETY: returns a static NUL-terminated string per ten_vad.h.
    unsafe { CStr::from_ptr(ten_vad_get_version()) }
        .to_string_lossy()
        .into_owned()
}
