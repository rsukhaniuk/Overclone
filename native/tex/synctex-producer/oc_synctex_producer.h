/* Owned raw-byte ABI. No map parsing or navigation claim. */
#ifndef OC_SYNCTEX_PRODUCER_H
#define OC_SYNCTEX_PRODUCER_H
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
#define OC_SYNCTEX_ABI UINT32_C(0x00010000)
/* oc_mem_configure(mode, cap, nonzero pass_tag) from memory_sink.h, ONCE before
 * compileLaTeX on a fresh module. mode0/cap0 disables even document \\synctex.
 * mode1 cap1..1048575. No exported API accepts a success flag or seals output. */
uint32_t oc_synctex_abi(void);
/* 0 unavailable (fresh/off/armed/writing/staged), 1 sealed, 2 failed/discarded.
 * Getters return 0 for absent/wrong tag. No pointer to internal storage escapes.
 * copy requires capacity >= entire length; never writes a partial result. */
uint32_t oc_synctex_status(void);
uint32_t oc_synctex_length(uint32_t pass_tag);
uint32_t oc_synctex_copy(uint32_t pass_tag, unsigned char *dst, uint32_t capacity);
#ifdef __cplusplus
}
#endif
#endif
