/* ABI5 private hooks, not WASM exports. All arguments are real writer state. */
#ifndef OC_LUA_WRITER_GEOMETRY_H
#define OC_LUA_WRITER_GEOMETRY_H
#include <stdint.h>
#include <stddef.h>
#ifdef __cplusplus
extern "C" {
#endif
void oc_lua_geometry_begin(int page, int64_t width, int64_t height,
                           int64_t synctex_height, double sp_to_raw,
                           double sp_to_bp, int supported_direction);
void oc_lua_geometry_observe(int64_t synctex_height, int supported_direction);
void oc_lua_geometry_end(int page, int64_t width, int64_t height);
/* Arbitrary PDF programs/form maps/order changes invalidate a single global
 * page affine model. Conservative run-wide fallback, without disabling PDF/map. */
void oc_lua_geometry_unsafe(void);
/* Accept ONLY an inert numeric CropBox/Rotate dictionary fragment; not a PDF
 * parser and not a source of PDF facts. Unknown syntax fails closed. */
void oc_lua_geometry_attributes(const char *bytes, size_t length);
#ifdef __cplusplus
}
#endif
#endif
