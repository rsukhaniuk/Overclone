#include <cstdint>

// This export belongs only to the separately pinned genuine HarfBuzz candidate.
// Runtime acceptance also requires its executed module/shaping/browser proofs.
extern "C" std::uint32_t oc_lua_harfbuzz_abi() { return 1; }

#if defined(OC_LUA_FONT_CLOSURE) && OC_LUA_FONT_CLOSURE
extern "C" std::uint32_t oc_lua_font_closure_abi() { return 1; }
#endif
