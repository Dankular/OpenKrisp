/* OpenKrisp engine C API. Hand-maintained; keep in sync with src/ffi.rs.
 * All functions return 0 on success, negative on failure (-1 bad argument/too-small buffer, -2 processing error, -3 panic). */
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

#ifdef __cplusplus
}
#endif
#endif
