/* Separate candidate Lua ABI4/ABI5 ONLY; not engine admission into any profile.
 * Existing ABI3/default link.sh exports remain unchanged.
 * All maps are canonical uncompressed v1, independently owned from AUX carry.
 * Configure once BEFORE compilation (default disabled). Rejected configuration,
 * repeated/failed native run and latched output quota revoke every map reader.
 * Copies are complete-or-none; absent/wrong pass returns zero. Internal finish,
 * staging, stream close and page hooks MUST NOT appear in WASM exports.
 */
#ifndef OC_LUA_MAPS_API_H
#define OC_LUA_MAPS_API_H
#include "../../synctex-producer/oc_synctex_producer.h"
#ifdef __cplusplus
extern "C" {
#endif
int oc_mem_configure(uint32_t mode, uint32_t cap, uint32_t pass_tag);
/* schemaVersion1 {pages:[{page,status,reason,writerToPdfRaw,writerPageBoxBp}]}.
 * ABI4 remains unsupported/writer-origin-unavailable with null matrix and box.
 * ABI5 provides actual writer-derived transforms ONLY in the checked domain
 * documented in writer-geometry.md. Unsafe programs/order/forms/directions or
 * invalid writer state are unsupported/out-of-domain, with null matrix and box.
 * Nonzero MediaBox and renderer/CSS acceptance gates remain OPEN.
 * <=512 actual PDF pages; otherwise geometry capability is absent (zero bytes).
 * 131072-byte copy capacity hard ceiling, same sealed lifetime as raw map.
 */
uint32_t oc_synctex_geometry_length(uint32_t pass_tag);
uint32_t oc_synctex_geometry_copy(uint32_t pass_tag, unsigned char *dst, uint32_t capacity);
#ifdef __cplusplus
}
#endif
#endif
