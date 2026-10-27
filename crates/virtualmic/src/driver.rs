//! Windows-only handle to the MicyWMD control device. Compile-checked only; never run against the driver.

use std::os::windows::ffi::OsStrExt;

use anyhow::{Result, bail};
use windows_sys::Win32::Foundation::{
    CloseHandle, GENERIC_READ, GENERIC_WRITE, GetLastError, HANDLE, INVALID_HANDLE_VALUE,
};
use windows_sys::Win32::Storage::FileSystem::{CreateFileW, FILE_ATTRIBUTE_NORMAL, OPEN_EXISTING};
use windows_sys::Win32::System::IO::DeviceIoControl;

use crate::IOCTL_MICYAUDIO_SET_AUDIO_DATA;

pub struct MicyDevice(HANDLE);

// SAFETY: a Win32 file handle may be used from any thread.
unsafe impl Send for MicyDevice {}

impl MicyDevice {
    pub fn open() -> Result<Self> {
        let path: Vec<u16> = std::ffi::OsStr::new(r"\\.\MicyAudio")
            .encode_wide()
            .chain(Some(0))
            .collect();
        // SAFETY: `path` is NUL-terminated and outlives the call.
        let h = unsafe {
            CreateFileW(
                path.as_ptr(),
                GENERIC_READ | GENERIC_WRITE,
                0,
                std::ptr::null(),
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL,
                std::ptr::null_mut(),
            )
        };
        if h == INVALID_HANDLE_VALUE {
            bail!(
                "opening \\\\.\\MicyAudio failed (Win32 error {}): is the MicyWMD driver installed and are you elevated?",
                unsafe { GetLastError() }
            );
        }
        Ok(Self(h))
    }

    /// Send raw PCM bytes (no header) to the driver's ring buffer.
    pub fn send(&self, pcm: &[u8]) -> Result<()> {
        let mut returned = 0u32;
        // SAFETY: handle is valid; the buffer is valid for `pcm.len()` bytes; no output buffer.
        let ok = unsafe {
            DeviceIoControl(
                self.0,
                IOCTL_MICYAUDIO_SET_AUDIO_DATA,
                pcm.as_ptr().cast(),
                pcm.len() as u32,
                std::ptr::null_mut(),
                0,
                &mut returned,
                std::ptr::null_mut(),
            )
        };
        if ok == 0 {
            bail!("DeviceIoControl failed (Win32 error {})", unsafe {
                GetLastError()
            });
        }
        Ok(())
    }
}

impl Drop for MicyDevice {
    fn drop(&mut self) {
        // SAFETY: handle came from CreateFileW and is closed once.
        unsafe { CloseHandle(self.0) };
    }
}
