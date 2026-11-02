/* Explicit graphics1 variant only. Read actual native save/matrix state and
 * publish actual transformed node coordinates through upstream SyncTeX hooks.
 * No parser/source/log inference, no caller seal or JS graphics authority. */
#include "writer_graphics.h"
#include "writer_geometry.h"
#include "oc_synctex_memory_sink.h"
#include <cmath>
#include <cstdio>
#include <cstring>
#include <climits>
namespace {
struct Save { int matrix; int64_t h,v; };
Save stack[64] = {};
int depth=0, matrices=0, literal=0;
bool valid=true;
void bad() { valid=false;oc_lua_geometry_unsafe(); }
bool within_limit(double n) { return std::isfinite(n)&&std::abs(n)<=1000000; }
int narrow(double n) {
    if (!std::isfinite(n)||n<INT_MIN||n>INT_MAX) {oc_mem_abort();return 0;}
    return static_cast<int>(std::llround(n));
}
bool state(const double *m,int saves,int actual_matrices,double bp) {
    if (!valid || !m || saves!=depth || actual_matrices!=matrices ||
        !std::isfinite(bp)||bp<=0) {bad();return false;}
    // Canonical SyncTeX box dimensions cannot encode arbitrary shear/rotation
    // or reflected axes. These domains remain unavailable, never guessed.
    if (!within_limit(m[0])||!within_limit(m[1])||!within_limit(m[2])||!within_limit(m[3])||
        !within_limit(m[4]*bp)||!within_limit(m[5]*bp)||m[1]!=0||m[2]!=0||m[0]<=0||m[3]<=0) {
        bad();return false;
    }
    return true;
}
}
extern "C" uint32_t oc_lua_graphics_abi(void) {return 1;}
extern "C" void oc_lua_graphics_save(int before,int after,int matrix,int64_t h,int64_t v) {
    if (!valid||before!=depth||after!=before+1||matrix!=matrices||depth>=64) {bad();return;}
    stack[depth++]={matrix,h,v};
}
extern "C" void oc_lua_graphics_restore(int before,int after,int matrix,int64_t h,int64_t v) {
    if (!valid||depth<=0||before!=depth||after!=before-1) {bad();return;}
    const Save &s=stack[depth-1];
    if (s.matrix!=matrix||s.h!=h||s.v!=v) {bad();return;}
    --depth;matrices=matrix;
}
extern "C" void oc_lua_graphics_matrix(const char *bytes,size_t length,int before,int after) {
    double a,b,c,d;int consumed=-1;
    if (!bytes||!length||length>4096||strnlen(bytes,length+1)!=length) {bad();return;}
    // Native sscanf accepts exponent/hex/NAN prefixes that are not PDF decimal
    // numbers. Validate the COMPLETE actual token bytes before trusting its
    // parsed stack: exactly four signed decimal tokens, no NUL/program suffix.
    size_t pos=0;
    for(int token=0;token<4;++token){
        while(pos<length&&std::strchr(" \t\r\n\f",bytes[pos]))++pos;
        if(pos<length&&(bytes[pos]=='+'||bytes[pos]=='-'))++pos;
        bool digit=false,dot=false;
        while(pos<length){
            char c=bytes[pos];
            if(c>='0'&&c<='9'){digit=true;++pos;}
            else if(c=='.'&&!dot){dot=true;++pos;}
            else break;
        }
        if(!digit||(pos<length&&!std::strchr(" \t\r\n\f",bytes[pos]))){bad();return;}
    }
    while(pos<length&&std::strchr(" \t\r\n\f",bytes[pos]))++pos;
    if(pos!=length){bad();return;}
    if (!valid||depth<=0||before!=matrices||after!=before+1||
        std::sscanf(bytes," %lf %lf %lf %lf %n",&a,&b,&c,&d,&consumed)!=4||consumed<0) {bad();return;}
    const char *end=bytes+consumed;while(*end==' '||*end=='\t'||*end=='\r'||*end=='\n'||*end=='\f')++end;
    if (*end||!within_limit(a)||!within_limit(b)||!within_limit(c)||!within_limit(d)||b!=0||c!=0||a<=0||d<=0) {bad();return;}
    matrices=after;
}
extern "C" void oc_lua_graphics_page_end(int saves,int matrix) {
    if (depth||matrices||saves||matrix||literal) bad();
}
extern "C" void oc_lua_graphics_literal_begin(int kind) {
    if(literal||kind<1||kind>3)bad();literal=kind;
}
extern "C" int oc_lua_graphics_literal_consume(void) {
    if(!literal)return 0;
    literal=0;return 1;
}
extern "C" void oc_lua_graphics_literal_end(void) {if(literal)bad();literal=0;}
extern "C" int oc_lua_graphics_position(int axis,int64_t h,int64_t v,int64_t height,const double *m,int saves,int matrix,double bp) {
    if(!state(m,saves,matrix,bp))return narrow(axis?static_cast<double>(height)-v:static_cast<double>(h));
    const double x=m[0]*h+m[2]*v+m[4],y=m[1]*h+m[3]*v+m[5];
    return narrow(axis?height-y:x);
}
extern "C" int oc_lua_graphics_dimension(int vertical,int64_t value,const double *m,int saves,int matrix,double bp) {
    if(!state(m,saves,matrix,bp))return narrow(static_cast<double>(value));
    return narrow(value*(vertical?m[3]:m[0]));
}
