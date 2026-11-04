#pragma once
#include <stddef.h>
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
/* Private native writer boundary, never a WASM authority export. Return1 only
 * for exactly validated MediaBox bytes that the native writer will emit once. */
int oc_lua_origin_page(int page, const char *tex, size_t tex_len,
    const char *lua, size_t lua_len, int64_t width, int64_t height);
/* Private actual root /Pages writer scope. Consumed attributes inside this
 * scope stay legacy-parsed but conservatively exclude new explicit origins. */
void oc_lua_origin_root_begin(void);
void oc_lua_origin_root_end(void);
uint32_t oc_lua_origin_abi(void);
#ifdef __cplusplus
}
#endif
