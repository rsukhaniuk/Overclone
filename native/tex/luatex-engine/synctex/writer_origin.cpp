/* New origin1 tuple only. Reuse the accepted native position/unit/close
 * observations verbatim; add the ACTUAL final page-dictionary writer witness. */
#define oc_synctex_geometry_length oc_origin_parent_length
#define oc_synctex_geometry_copy oc_origin_parent_copy
#define oc_lua_geometry_attributes oc_origin_parent_attributes
#include "writer_geometry.cpp"
#undef oc_synctex_geometry_length
#undef oc_synctex_geometry_copy
#undef oc_lua_geometry_attributes
#include "writer_origin.h"

namespace {
struct Origin {
    double media[4],crop[4];
    unsigned keys;
    bool witnessed,hasMedia;
};
Origin origins[MaxPages]={};
bool anyOrigin=false;
/* Actual root /Pages emission, NOT setters or source text. Existing native
 * attribute parsing still runs independently for legacy no-explicit-origin
 * behavior. Explicit origins conservatively require proven empty root channels. */
bool inheritedActive=false,inheritedBroken=false,inheritedNonempty=false;
uint32_t inheritedRoots=0;
/* Same bounded PDF decimal grammar as the accepted writer attributes. Read
 * exactly the token/Lua string bytes about to be consumed by the native writer;
 * no TeX/source scan, PDF parser, name prefix or filename admission. */
bool parse(const char *b,size_t n,Origin &o) {
    if(!n)return true;
    if(!b||n>4096)return false;
    Attributes a{b,b+n};
    for(;;) {
        a.skip();if(a.p==a.end)return true;
        unsigned key=0;double *box=nullptr;
        if(a.literal("/MediaBox")){key=1;box=o.media;}
        else if(a.literal("/CropBox")){key=2;box=o.crop;}
        else if(a.literal("/Rotate"))key=4;
        else return false;
        if(o.keys&key)return false;
        o.keys|=key;
        if(box) {
            if(!a.literal("["))return false;
            for(unsigned i=0;i<4;++i)if(!a.number(box[i]))return false;
            if(!a.literal("]")||box[0]>=box[2]||box[1]>=box[3])return false;
        } else {
            double r;
            if(!a.number(r)||(r!=0&&r!=90&&r!=180&&r!=270))return false;
        }
    }
}
}
extern "C" uint32_t oc_lua_origin_abi(void){return 1;}
extern "C" void oc_lua_origin_root_begin(void) {
    if(inheritedActive||inheritedRoots)inheritedBroken=true;
    inheritedActive=true;
}
extern "C" void oc_lua_origin_root_end(void) {
    if(!inheritedActive)inheritedBroken=true;
    inheritedActive=false;++inheritedRoots;
}
extern "C" void oc_lua_geometry_attributes(const char *bytes,size_t length) {
    if(inheritedActive&&length) {
        inheritedNonempty=true;
        geometrySize=0; /* a later native observation cannot leave a cached map */
    }
    oc_origin_parent_attributes(bytes,length);
}
extern "C" int oc_lua_origin_page(int page,const char *tex,size_t tex_len,
    const char *lua,size_t lua_len,int64_t width,int64_t height) {
    if(!active||page<=0||static_cast<uint32_t>(page)!=count+1||count>=MaxPages||
        origins[count].witnessed) {unsafe=true;return 0;}
    Origin &o=origins[count];o.witnessed=true;
    const Page &p=pages[count];
    if(p.width!=width||p.height!=height||tex_len>4096||lua_len>4096||tex_len+lua_len>4096||
        !parse(tex,tex_len,o)||!parse(lua,lua_len,o)) {unsafe=true;return 0;}
    o.hasMedia=(o.keys&1)!=0;
    if(o.hasMedia&&(o.keys&2)&&!(o.crop[0]>=o.media[0]&&o.crop[1]>=o.media[1]&&
        o.crop[2]<=o.media[2]&&o.crop[3]<=o.media[3])) {unsafe=true;return 0;}
    anyOrigin=anyOrigin||o.hasMedia;
    return o.hasMedia?1:0;
}
extern "C" uint32_t oc_synctex_geometry_length(uint32_t token) {
    if(!oc_synctex_length(token)||!sequenceValid||active||!count)return 0;
    for(uint32_t i=0;i<count;++i)if(!origins[i].witnessed)unsafe=true;
    if(!anyOrigin)return oc_origin_parent_length(token);
    if(inheritedActive||inheritedBroken||inheritedRoots!=1||inheritedNonempty) {
        unsafe=true;geometrySize=0;
    }
    if(geometrySize)return geometrySize;
    int n=std::snprintf(geometry,sizeof(geometry),"{\"schemaVersion\":1,\"pages\":[");
    if(n<=0)return 0;
    uint32_t used=static_cast<uint32_t>(n);
    for(uint32_t i=0;i<count;++i) {
        const Page &p=pages[i];const Origin &o=origins[i];
        double s=p.spToRaw/p.spToBp,f=p.synctexHeight*p.spToRaw;
        double box[4]={0,(p.synctexHeight-p.height)*p.spToBp,p.width*p.spToBp,p.synctexHeight*p.spToBp};
        bool valid=p.available&&!unsafe&&bounded(s)&&s>0&&bounded(f);
        if(o.hasMedia) {
            /* Inverse of the independently observed native writer relation;
             * this is an extent transformation, NOT a box-derived matrix. */
            box[0]=o.media[0]/s;box[1]=(f-o.media[3])/s;
            box[2]=o.media[2]/s;box[3]=(f-o.media[1])/s;
            for(double x:box)valid=valid&&bounded(x);
            valid=valid&&box[0]<box[2]&&box[1]<box[3];
        }
        if(valid)n=std::snprintf(geometry+used,sizeof(geometry)-used,
            "%s{\"page\":%u,\"status\":\"available\",\"reason\":\"none\","
            "\"writerToPdfRaw\":[%.17g,0,0,%.17g,0,%.17g],"
            "\"writerPageBoxBp\":[%.17g,%.17g,%.17g,%.17g]}",i?",":"",i+1,
            s,-s,f,box[0],box[1],box[2],box[3]);
        else n=std::snprintf(geometry+used,sizeof(geometry)-used,
            "%s{\"page\":%u,\"status\":\"unsupported\",\"reason\":\"out-of-domain\","
            "\"writerToPdfRaw\":null,\"writerPageBoxBp\":null}",i?",":"",i+1);
        if(n<=0||static_cast<uint32_t>(n)>=sizeof(geometry)-used)return 0;
        used+=static_cast<uint32_t>(n);
    }
    if(used+2>=sizeof(geometry))return 0;
    std::memcpy(geometry+used,"]}",3);geometrySize=used+2;return geometrySize;
}
extern "C" uint32_t oc_synctex_geometry_copy(uint32_t token,unsigned char *dst,uint32_t capacity) {
    uint32_t n=oc_synctex_geometry_length(token);
    if(!n||!dst||capacity<n||capacity>MaxBytes)return 0;
    std::memcpy(dst,geometry,n);return n;
}
