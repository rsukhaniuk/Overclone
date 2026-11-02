/* Use the ACTUAL pinned LuaTeX node/position ABI, never pdfTeX's layout.
 * Lua scaledpos is int64_t but the canonical upstream writer uses %i. In wasm32
 * varargs an uncast int64_t consumes TWO words and corrupts following arguments.
 * Preserve upstream coordinate expressions, with checked int32 narrowing only.
 */
#include "synctex-luatex.h"
#include "oc_synctex_memory_sink.h"
#include <stdint.h>
#include <limits.h>
#if OC_LUATEX_ABI == 5
#include "writer_geometry.h"
#endif
static inline int oc_lua_synctex_coord(int64_t value)
{
#if OC_LUATEX_ABI == 5
    oc_lua_geometry_observe(page_height_par, static_pdf->posstruct->dir == dir_TLT);
#endif
    if (value < INT_MIN || value > INT_MAX) { oc_mem_abort(); return 0; }
    return (int)value;
}
static inline int oc_lua_synctex_curh(void) { return oc_lua_synctex_coord(SYNCTEX_CURH); }
static inline int oc_lua_synctex_curv(void) { return oc_lua_synctex_coord(SYNCTEX_CURV); }
#undef SYNCTEX_CURH
#undef SYNCTEX_CURV
#define SYNCTEX_CURH oc_lua_synctex_curh()
#define SYNCTEX_CURV oc_lua_synctex_curv()
