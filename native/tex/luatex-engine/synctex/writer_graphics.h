/* graphics1 private native observations; never WASM authority exports. */
#ifndef OC_LUA_WRITER_GRAPHICS_H
#define OC_LUA_WRITER_GRAPHICS_H
#include <stdint.h>
#include <stddef.h>
#ifdef __cplusplus
extern "C" {
#endif
void oc_lua_graphics_save(int before,int after,int matrix,int64_t h,int64_t v);
void oc_lua_graphics_restore(int before,int after,int matrix,int64_t h,int64_t v);
void oc_lua_graphics_matrix(const char *bytes,size_t length,int before,int after);
void oc_lua_graphics_page_end(int saves,int matrices);
void oc_lua_graphics_literal_begin(int kind);
int oc_lua_graphics_literal_consume(void);
void oc_lua_graphics_literal_end(void);
int oc_lua_graphics_position(int axis,int64_t h,int64_t v,int64_t height,const double *m,int saves,int matrices,double bp);
int oc_lua_graphics_dimension(int vertical,int64_t value,const double *m,int saves,int matrices,double bp);
uint32_t oc_lua_graphics_abi(void);
#ifdef __cplusplus
}
#endif
#endif
