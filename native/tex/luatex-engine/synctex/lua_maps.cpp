/* Lua ABI4 public read boundary over the unchanged ABI1 bounded sink.
 * No caller-controlled finish, stream-close, shipout or seal is exported. */
#include "lua_maps.h"
#include "../../synctex-producer/oc_synctex_memory_sink.h"
#include <cstdint>
#include <cstdio>
#include <cstring>

extern "C" {
int oc_lua_maps_eligible(void);
uint32_t oc_lua_sink_status(void);
uint32_t oc_lua_sink_length(uint32_t);
uint32_t oc_lua_sink_copy(uint32_t, unsigned char *, uint32_t);
}
#if OC_LUATEX_ABI != 5
namespace {
constexpr uint32_t MaxGeometry = 131072;
char geometry[MaxGeometry] = {};
uint32_t geometrySize = 0;
uint32_t pages = 0;
bool pageSequenceValid = true;
}
extern "C" void oc_lua_maps_ship_page(int actual_total_pages)
{
    // Called after the real PDF backend end-page, not from document page labels.
    if (actual_total_pages <= 0 || actual_total_pages > 512 ||
        static_cast<uint32_t>(actual_total_pages) != pages + 1) {
        pageSequenceValid = false;
        return;
    }
    pages = static_cast<uint32_t>(actual_total_pages);
}
#endif
extern "C" uint32_t oc_synctex_status(void)
{
    (void)oc_lua_maps_eligible(); // Revokes the sink after any latched VFS failure.
    return oc_lua_sink_status();
}
extern "C" uint32_t oc_synctex_length(uint32_t pass_tag)
{
    return oc_lua_maps_eligible() ? oc_lua_sink_length(pass_tag) : 0;
}
extern "C" uint32_t oc_synctex_copy(uint32_t pass_tag, unsigned char *dst, uint32_t capacity)
{
    return oc_lua_maps_eligible() ? oc_lua_sink_copy(pass_tag, dst, capacity) : 0;
}
#if OC_LUATEX_ABI != 5
extern "C" uint32_t oc_synctex_geometry_length(uint32_t pass_tag)
{
    if (!oc_synctex_length(pass_tag) || !pageSequenceValid || pages == 0) return 0;
    if (geometrySize) return geometrySize;
    // Honest unavailable geometry. The upstream writer exposes changing posstruct,
    // page attributes, directions and Lua callbacks; no validated complete writer
    // origin/transform is established here. Page count alone is NOT geometry.
    int n = std::snprintf(geometry, sizeof(geometry), "{\"schemaVersion\":1,\"pages\":[");
    if (n <= 0) return 0;
    uint32_t used = static_cast<uint32_t>(n);
    for (uint32_t p = 1; p <= pages; ++p) {
        n = std::snprintf(geometry + used, sizeof(geometry) - used,
            "%s{\"page\":%u,\"status\":\"unsupported\",\"reason\":\"writer-origin-unavailable\","
            "\"writerToPdfRaw\":null,\"writerPageBoxBp\":null}", p == 1 ? "" : ",", p);
        if (n <= 0 || static_cast<uint32_t>(n) >= sizeof(geometry) - used) return 0;
        used += static_cast<uint32_t>(n);
    }
    if (used + 2 >= sizeof(geometry)) return 0;
    std::memcpy(geometry + used, "]}", 3);
    geometrySize = used + 2;
    return geometrySize;
}
extern "C" uint32_t oc_synctex_geometry_copy(uint32_t pass_tag, unsigned char *dst, uint32_t capacity)
{
    const uint32_t n = oc_synctex_geometry_length(pass_tag);
    if (!n || !dst || capacity < n || capacity > MaxGeometry) return 0;
    std::memcpy(dst, geometry, n);
    return n;
}
#endif
