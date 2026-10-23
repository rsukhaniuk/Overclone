#ifndef LATEX_SYNTEX_QUERY_H
#define LATEX_SYNTEX_QUERY_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ABI/schema 1. Single-threaded, one load attempt per fresh module/process.
 * All pointers must address valid caller-owned memory of the stated length.
 * No pointer, filename or output string is an authority to open project files.
 */
enum sq_status {
    SQ_OK = 0,
    SQ_EMPTY = 1,
    SQ_INVALID = -1,
    SQ_STATE = -2,
    SQ_IO = -3,
    SQ_PARSE = -4,
    SQ_NATIVE = -5,
    SQ_BUDGET = -6,
    SQ_INTERNAL = -7,
    SQ_COPY = -8
};

uint32_t sq_version(void);
int32_t sq_status_code(void);
/* Fixed, non-sensitive ASCII string for the current status; never native logs. */
const char *sq_error_message(void);
/* Canonical uncompressed v1, 1..1048575 bytes, no NUL. Any attempt consumes load. */
int32_t sq_load(const uint8_t *bytes, uint32_t length);
/* UTF-8 project-relative path, 1..1024 bytes, no NUL/control/backslash/colon,
 * no empty, dot or parent segments. line > 0; column 0 = unspecified;
 * page_hint -1 = unspecified, otherwise 1..512. No guessed column spans.
 */
int32_t sq_forward(const uint8_t *filename, uint32_t length,
                   int32_t line, int32_t column, int32_t page_hint);
/* page 1..512; h/v finite within [-1000000,1000000], upstream visible bp. */
int32_t sq_inverse(int32_t page, double h, double v);
/* Exact UTF-8 JSON byte count (no trailing NUL), 0 on empty/error/close.
 * Copy is all-or-nothing, capacity >= length; no trailing NUL is copied.
 * An invalid copy clears internal result bytes too. Caller owns previous copies.
 */
uint32_t sq_result_length(void);
int32_t sq_result_copy(uint8_t *destination, uint32_t capacity);
/* Idempotent terminal close. A new module/process is required for another map. */
int32_t sq_close(void);

#ifdef __cplusplus
}
#endif
#endif
