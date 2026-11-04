#ifndef OVERCLONE_BIBER_ENGINE_H
#define OVERCLONE_BIBER_ENGINE_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ABI 1. Fresh module per operation. All input bytes are copied/owned.
 * configure/add/run are one-way; no input mutation after the first run.
 * bcf is the strict relative UTF-8 ENTRY PATH, not an XML document.
 * Paths: 1..1024 bytes; context: 0..8192 bytes, valid UTF-8, no NUL.
 * max_log_bytes: 1..256KiB; max_output_bytes: 1..8MiB.
 * Files: at most 64; at most 8MiB each/aggregate. Names preserve original UTF-8.
 * Role0 uploads are refused: runtime is authenticated builtin .data only.
 * Role1 is explicit project/source data; role2 is ONLY the configured BCF entry.
 * BCF must be role2. Both roles share the 64-file/8MiB aggregate input limit.
 * max_output_bytes bounds BBL+BLG together; terminal has its own max_log_bytes.
 * Output kinds: 0=BBL, 1=BLG, 2=terminal log.
 * Only successful runs expose outputs; failed/quota-latched runs revoke getters.
 * Copies are exact, with no partial copy or appended NUL.
 * Negative returns: -1 invalid input, -2 invalid state, -3 quota, -4 native failure.
 * A positive run return preserves the genuine program's failed exit status.
 * error copies only a fixed safe code; no bibliography/path/credential text.
 */
uint32_t oc_biber_abi(void);
int32_t oc_biber_configure(const uint8_t *bcf, uint32_t bcf_len,
                         const uint8_t *context, uint32_t context_len,
                         uint32_t max_log_bytes, uint32_t max_output_bytes);
int32_t oc_biber_add_file(uint32_t role, const uint8_t *name, uint32_t name_len,
                        const uint8_t *bytes, uint32_t byte_len);
int32_t oc_biber_run(void);
int32_t oc_biber_output_size(uint32_t output_kind);
int32_t oc_biber_copy_output(uint32_t output_kind, uint8_t *dst, uint32_t capacity);
int32_t oc_biber_error(uint8_t *dst, uint32_t capacity);

#ifdef __cplusplus
}
#endif
#endif
