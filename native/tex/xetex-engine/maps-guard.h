/* Private include after actual XeTeX generated declarations in main.c.
 * Observes real just-closed XDV, never manufactures map/close authority.
 * No declarations here are WASM exports. */
#ifndef OC_XE_MAPS_GUARD_H
#define OC_XE_MAPS_GUARD_H
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
static uint32_t oc_xe_be32(const unsigned char *p) {
    return ((uint32_t)p[0]<<24)|((uint32_t)p[1]<<16)|((uint32_t)p[2]<<8)|p[3];
}
static int oc_xe_native_output_valid(void) {
    struct stat st; FILE *f; unsigned char pre[15],tail[16],post[29];
    size_t pad=0,size,ptr; int ok; char *name;
    if (totalpages<=0 || outputfilename==0) return 0;
    name=gettexstring(outputfilename);
    if (!name) return 0;
    if (stat(name,&st) || !S_ISREG(st.st_mode) || st.st_size<100 || st.st_size>8388608) { free(name);return 0; }
    size=(size_t)st.st_size;f=fopen(name,"rb");free(name);if (!f) return 0;
    ok=fread(pre,1,sizeof(pre),f)==sizeof(pre) && !fseek(f,-16,SEEK_END) && fread(tail,1,16,f)==16;
    if (ok) {
        while (pad<16 && tail[15-pad]==223) ++pad;
        ok=pre[0]==247 && pre[1]==7 && pad>=4 && pad<=7 && tail[15-pad]==7 && tail[10-pad]==249;
        if (ok) {
            ptr=oc_xe_be32(tail+11-pad);
            ok=ptr<size-29 && !fseek(f,(long)ptr,SEEK_SET) && fread(post,1,29,f)==29 &&
                post[0]==248 && !memcmp(pre+2,post+5,12) && oc_xe_be32(pre+2)==25400000 &&
                oc_xe_be32(pre+6)==473628672 && oc_xe_be32(pre+10)>0 &&
                (((unsigned)post[27]<<8)|post[28])==(unsigned)totalpages;
        }
    }
    ok=!ferror(f) && ok;if (fclose(f)) ok=0;
    if (texmflogname) {
        name=gettexstring(texmflogname);
        if (!name) return 0;
        if (stat(name,&st) || !S_ISREG(st.st_mode) || st.st_size>262144) ok=0;
        free(name);
    }
    return ok;
}
#endif
