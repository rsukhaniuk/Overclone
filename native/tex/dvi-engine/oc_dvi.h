#ifndef OC_DVI_H
#define OC_DVI_H
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
/* ABI 1. One engine attempt per module, including failed admission at run().
 * All strings are NUL-terminated UTF-8 relative paths, <=249 bytes.
 * add_file copies bytes; result pointers remain owned by the module.
 * No reset: destroy the containing worker/module for cancellation or reuse. */
uint32_t oc_dvi_abi_version(void);
const char *oc_dvi_engine_version(void);
/* 0 project, 1 provisioned resource; project total<=8MiB, resource total<=96MiB,
 * each file<=32MiB, combined count<=4096. Admission checks precede copying.
 * Resources have a flat basename namespace. Project paths are relative to
 * fixed /project; lookup never accesses a host or Emscripten filesystem.
 * The sole absolute lookup alias is /tex/<flat-basename>, resource-only,
 * matching XeTeX's fixed virtual font root. Other absolute paths are refused.
 * add_file itself always requires a relative name, even for resources. */
int oc_dvi_add_file(const char *path, const uint8_t *bytes, uint32_t length, uint32_t resource);
/* entry is an exact project path, not a command line or output stem.
 * Output always occupies a separate internal /output/result.pdf namespace.
 * Repeated run returns/latches status3 and clears PDF access. Earlier native
 * return and logs remain available. A failed first run also releases no PDF. */
int oc_dvi_run(const char *entry);
/* Status: 0 ready/success, 1 invalid input, 2 limit exceeded, 3 already used,
 * 4 native returned failure, 5 native abort (bridge return99), 6 invalid PDF.
 * WASM trap/abort is NOT a native return: host must report WorkerCrashed. */
int oc_dvi_status(void);
int oc_dvi_native_return(void); /* -1 until engine returns */
const char *oc_dvi_error(void); /* fixed safe text, never engine/document text */
const uint8_t *oc_dvi_pdf_data(void);
uint32_t oc_dvi_pdf_size(void);
const uint8_t *oc_dvi_log_data(void); /* untrusted engine log, plain text only */
uint32_t oc_dvi_log_size(void);
uint32_t oc_dvi_log_truncated(void);
/* First missing/rejected lookup; path data only, no automatic acquisition. */
const char *oc_dvi_missing_file(void);
#ifdef __cplusplus
}
#endif
#endif
