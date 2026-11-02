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
#ifdef OC_LUA_GRAPHICS
#include "writer_graphics.h"
/* Actual opened filename, translated only from the fixed private project root
 * (and its literal bootstrap ./ prefix) to complete captured relative bytes.
 * Runtime filenames remain runtime filenames. Never match by basename. */
static inline char *oc_lua_graphics_current_name(void) {
    char *name=luatex_synctex_get_current_name();
    if(name && !strncmp(name,"/work/project/",14)) {
        memmove(name,name+14,strlen(name+14)+1);
        if(!strncmp(name,"./",2))memmove(name,name+2,strlen(name+2)+1);
    }
    return name;
}
#undef SYNCTEX_GET_CURRENT_NAME
#define SYNCTEX_GET_CURRENT_NAME() oc_lua_graphics_current_name()
/* Actual native PDF matrix stack, not inferred source/display geometry. */
static inline void oc_lua_synctex_matrix(double m[6]) {
    m[0]=m[3]=1; m[1]=m[2]=m[4]=m[5]=0;
    if (matrix_stack_used>0) {
        matrix_entry *p=&matrix_stack[matrix_stack_used-1];
        m[0]=p->a;m[1]=p->b;m[2]=p->c;m[3]=p->d;m[4]=p->e;m[5]=p->f;
    }
}
static inline int oc_lua_synctex_position(int axis) {
    double m[6];oc_lua_synctex_matrix(m);
    return oc_lua_synctex_coord(oc_lua_graphics_position(axis,
        static_pdf->posstruct->pos.h,static_pdf->posstruct->pos.v,page_height_par,
        m,pos_stack_used,matrix_stack_used,1.0/by_one_bp));
}
static inline int oc_lua_synctex_dimension(int vertical,int64_t value) {
    double m[6];oc_lua_synctex_matrix(m);
    return oc_lua_synctex_coord(oc_lua_graphics_dimension(vertical,value,m,
        pos_stack_used,matrix_stack_used,1.0/by_one_bp));
}
static inline int oc_lua_synctex_width(halfword p) {return oc_lua_synctex_dimension(0,width(p));}
static inline int oc_lua_synctex_height(halfword p) {return oc_lua_synctex_dimension(1,height(p));}
static inline int oc_lua_synctex_depth(halfword p) {return oc_lua_synctex_dimension(1,depth(p));}
#undef SYNCTEX_WIDTH
#undef SYNCTEX_HEIGHT
#undef SYNCTEX_DEPTH
#undef SYNCTEX_RULE_WD
#undef SYNCTEX_RULE_HT
#undef SYNCTEX_RULE_DP
#define SYNCTEX_WIDTH(NODE) oc_lua_synctex_width(NODE)
#define SYNCTEX_HEIGHT(NODE) oc_lua_synctex_height(NODE)
#define SYNCTEX_DEPTH(NODE) oc_lua_synctex_depth(NODE)
#define SYNCTEX_RULE_WD oc_lua_synctex_width(p)
#define SYNCTEX_RULE_HT oc_lua_synctex_height(p)
#define SYNCTEX_RULE_DP oc_lua_synctex_depth(p)
static inline int oc_lua_synctex_curh(void) {return oc_lua_synctex_position(0);}
static inline int oc_lua_synctex_curv(void) {return oc_lua_synctex_position(1);}
#else
static inline int oc_lua_synctex_curh(void) { return oc_lua_synctex_coord(SYNCTEX_CURH); }
static inline int oc_lua_synctex_curv(void) { return oc_lua_synctex_coord(SYNCTEX_CURV); }
#endif
#undef SYNCTEX_CURH
#undef SYNCTEX_CURV
#define SYNCTEX_CURH oc_lua_synctex_curh()
#define SYNCTEX_CURV oc_lua_synctex_curv()
