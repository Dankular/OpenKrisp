/* OpenKrisp engine C API. Hand-maintained; keep in sync with src/ffi.rs.
 * All functions return 0 on success, negative on failure (-1 bad argument/too-small buffer, -2 processing/session error, -3 panic, -4 not available in this build). */
#ifndef OPENKRISP_ENGINE_H
#define OPENKRISP_ENGINE_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct VcStatus {
    uint8_t mic_nc;
    uint8_t speaker_nc;
    uint8_t voice_isolation;        /* 0 off, 1 auto (stored setting) */
    uint8_t voice_isolation_active; /* 1 only if Auto is requested and at least one capability below runs */
    uint8_t echo_cancel_active;     /* always 0 today: not implemented */
    uint8_t nearby_voice_removal_active; /* always 0 today: not implemented */
    uint8_t reserved[2];
    uint32_t sample_rate;
    uint32_t block;                 /* samples per block; output lags input by one block */
    uint64_t mic_samples;
    uint64_t speaker_samples;
} VcStatus;

/* Returns NULL on failure and writes a message to err. sample_rate is the model's native rate (48000 for dpdfnet2_48khz_hr). */
void *vc_engine_create(const char *model_path_utf8, uint32_t sample_rate, char *err, size_t err_cap);
void vc_engine_destroy(void *h);

int vc_set_mic_nc(void *h, int on);
int vc_set_speaker_nc(void *h, int on);
int vc_set_voice_isolation(void *h, int mode); /* 0 off, 1 auto */
int vc_get_status(void *h, VcStatus *out);

/* lane: 0 mic, 1 speaker. Mono float samples at sample_rate. cap should be >= n + block. */
int vc_process(void *h, int lane, const float *in, size_t n, float *out, size_t cap, size_t *written);

/* ---- Live audio (needs an engine built with the `audio` feature; otherwise these return -4) ----
 * Mic lane:     capture device -> mic lane -> MicyWMD virtual mic (Windows) or a render device.
 * Speaker lane: loopback capture of a render device -> speaker lane -> another render device. The capture device must
 *               differ from the playback device (otherwise the cleaned audio would loop); in practice the capture
 *               device is a virtual speaker that applications play into. */
typedef struct VcAudioConfig {
    const char *mic_input;        /* capture device name (UTF-8), NULL = default input */
    int32_t mic_sink;             /* 0 discard (mic lane off), 1 MicyWMD virtual mic (Windows), 2 render device */
    const char *mic_sink_device;  /* for mic_sink == 2: render device name, NULL = default output */
    int32_t micy_format;          /* for mic_sink == 1: 0 i32, 1 f32, 2 i16 */
    const char *speaker_loopback; /* render device to loop back; NULL = incoming-audio lane off */
    const char *speaker_output;   /* where cleaned incoming audio plays, NULL = default output */
} VcAudioConfig;

typedef struct VcAudioStats {
    uint8_t running;
    uint8_t reserved[7];
    uint64_t mic_input_dropped;
    uint64_t mic_output_underrun_frames;
    uint64_t speaker_input_dropped;
    uint64_t speaker_output_underrun_frames;
    uint64_t mic_worker_max_block_us;
    uint64_t speaker_worker_max_block_us;
} VcAudioStats;

/* Device names, one per line (UTF-8, NUL-terminated). kind: 0 capture, 1 render. Returns bytes written or <0. */
int vc_audio_list_devices(int kind, char *buf, size_t cap);
/* Starts (or restarts) live audio. On failure returns <0 and writes a message to err. */
int vc_audio_start(void *h, const VcAudioConfig *cfg, char *err, size_t err_cap);
int vc_audio_stop(void *h);
int vc_audio_get_stats(void *h, VcAudioStats *out);

#ifdef __cplusplus
}
#endif
#endif
