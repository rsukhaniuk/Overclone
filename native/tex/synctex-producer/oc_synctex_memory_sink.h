/* Owned byte formatter/stager. Producer/read API is in oc_synctex_producer.h;
 * sealing hooks are internal and must NOT be exported to the driver. */
#ifndef OC_SYNCTEX_MEMORY_SINK_H
#define OC_SYNCTEX_MEMORY_SINK_H
#include <stdint.h>

/* Physical buffer includes the one vsnprintf NUL; no extra scratch buffer. */
#define OC_MEM_STORAGE_BYTES UINT32_C(1048576)
#define OC_MEM_MAX_BYTES (OC_MEM_STORAGE_BYTES - UINT32_C(1))

#ifdef __cplusplus
extern "C" {
#endif
/* One attempt per fresh module/process. Default disabled. Mode0 accepts cap0;
 * mode1 needs cap1..MAX. Both need positive pass_token. Invalid/repeated config
 * latches failure and wipes; discard/abort never grant reset permission. */
int oc_mem_configure(uint32_t raw_mode, uint32_t cap, uint32_t pass_token);
/* Opaque private handle, NEVER FILE*, descriptor or filename. */
void *oc_mem_open(void);
/* Exact historical synctex_fprintf_t ABI. Only canonical trusted ENGINE format
 * constants and correctly promoted int/char* arguments; never document formats.
 * Returns positive EXACT byte count or -1 after whole-buffer wiping. */
int oc_mem_printf(void *token, const char *format, ...);
void oc_mem_abort(void);
/* Pass ONLY the ACTUAL historical record_postamble int return (NOERR=0).
 * Staging is internal retention, NOT seal/compile/PDF success or map access.
 * No C boundary can authenticate the origin of an integer status or a pointer. */
int oc_mem_stage_checked(int actual_postamble_return);
void oc_mem_discard(void);
#ifdef __cplusplus
}
/* Reject direct C++ bool-as-success-flag calls. C callers still must supply the
 * actual int status. Staging grants no byte access; engine finish seals later. */
int oc_mem_stage_checked(bool) = delete;
#endif
#endif
