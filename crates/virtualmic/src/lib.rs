//! Feeds audio to the MicyWMD virtual microphone (https://github.com/Mithronn/MicyWMD, MIT).
//!
//! What was read from the driver source (commit c5d2e11, 2025-11-18), NOT yet run on Windows:
//! - control device `\\.\MicyAudio`; `IOCTL_MICYAUDIO_SET_AUDIO_DATA` = CTL_CODE(29, 0x902, METHOD_BUFFERED, FILE_ANY_ACCESS);
//! - the handler appends the *entire* input buffer to one global ring (it does not parse the `StreamId/DataSize` header the
//!   project's test sender prepends), so this crate sends raw PCM with no header;
//! - the endpoint advertises 48 kHz, 2 channels, 32 bits, subtype PCM (a nearby comment says "float"; the GUID says integer).
//!   Which one it really is must be confirmed on Windows, so the sample format is selectable;
//! - underrun -> silence, overflow -> oldest data dropped; there is no way to read the ring's fill level.

pub mod convert;
#[cfg(windows)]
pub mod driver;

/// `CTL_CODE(device_type, function, method, access)` from `winioctl.h`.
pub const fn ctl_code(device_type: u32, function: u32, method: u32, access: u32) -> u32 {
    (device_type << 16) | (access << 14) | (function << 2) | method
}

pub const MICY_IOCTL_TYPE: u32 = 29;
pub const IOCTL_MICYAUDIO_SET_AUDIO_DATA: u32 = ctl_code(MICY_IOCTL_TYPE, 0x902, 0, 0);

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ioctl_code_value() {
        // (29 << 16) | (0x902 << 2) = 0x001D0000 | 0x2408
        assert_eq!(IOCTL_MICYAUDIO_SET_AUDIO_DATA, 0x001D_2408);
    }
}
