#pragma once
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
/* ABI 2. Exact UTF-8 relative paths; fresh module for EACH format/LaTeX attempt.
 * kind: 0 runtime, 1 project, 2 selected output carry (result.aux/toc/out/bbl/blg/lof/lot).
 * artifacts: 0 PDF, 1 log, 2 LuaLaTeX fmt, 3 AUX, 4 TOC, 5 OUT,
 *            6 BBL, 7 BLG, 8 LOF, 9 LOT. Fixed --jobname=result; no source rewriting.
 * Native engine status is returned by compile; negative values are ABI errors.
 * Context is opaque caller data; the parent must correlate revisions/tickets. */
uint32_t oc_luatex_abi(void);
const char *oc_luatex_version(void);
int oc_luatex_configure(const char *root, const char *context);
const char *oc_luatex_context(void);
int oc_luatex_add_file(int kind, const char *name, const uint8_t *bytes, uint32_t length);
/* Ten indexed outputs are declared by default. Add only .aux/.bbl/.blg/.toc/
 * .lof/.lot/.out names before compilation; cap 128 including defaults.
 * Parents are precreated; kind 2 accepts only declared generated names.
 * Raw copy ceilings: 32 MiB/file, 64 MiB selected total; no filesystem listing.
 * Automatic execution guard is stricter: 8 MiB/file (INITEX 32 MiB), 16 MiB
 * growth and 16 MiB shared backing/copy work, 128 new nodes, 256 KiB logs. */
int oc_luatex_declare_output(const char *name);
int oc_luatex_named_output_size(const char *name);
int oc_luatex_copy_named_output(const char *name, uint8_t *destination, uint32_t capacity);
int oc_luatex_compile_format(void);
int oc_luatex_compile_latex(void);
int oc_luatex_output_size(int artifact);
int oc_luatex_copy_output(int artifact, uint8_t *destination, uint32_t capacity);
const char *oc_luatex_error(void);
/* Read-only quota report, including failure latch/reason and bounded counters.
 * The guard seals automatically at compile entry, after explicit input seeding. */
const char *oc_luatex_budget_json(void);
#ifdef __cplusplus
}
#endif
