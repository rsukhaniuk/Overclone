#ifndef OC_LUA_NEUTRAL_H
#define OC_LUA_NEUTRAL_H
#include <stddef.h>
#ifdef __cplusplus
extern "C" {
#endif
/* Private native caller scopes; never exported as document/JS authority. */
void oc_lua_neutral_tokens_begin(int role);
int oc_lua_neutral_tokens(const char *bytes,size_t length);
void oc_lua_neutral_tokens_end(void);
void oc_lua_neutral_color_begin(void);
int oc_lua_neutral_color_pending(void);
int oc_lua_neutral_color_program(const char *bytes,size_t length,int mode_safe);
void oc_lua_neutral_color_end(void);
#ifdef __cplusplus
}
#endif
#endif
