/* Separate opt-in DVI candidate ONLY. Included by its checked main.c overlay.
 * No new export can finish/seal a pass. The historical sink FILE-close callback
 * observes the actual DVI close site; these checks run before its internal seal. */
#ifndef OC_SYNCTEX_DVI_GUARD_H
#define OC_SYNCTEX_DVI_GUARD_H
#include <stdint.h>
#include <sys/stat.h>
#include <string.h>
static int oc_dvi_entry_set = 0;
static int oc_dvi_attempted = 0;
static int oc_dvi_entry_valid(const char *s) {
    size_t n, i, start, end, part;
    if (!s) return 0;
    n = strnlen(s, 512);
    if (n < 5 || n >= 512) return 0;
    start = s[0] == '"' ? 1 : 0;
    if (start && s[n-1] != '"') return 0;
    end = n - start;
    if (end - start > 249 || end - start < 5 || s[start] == '/') return 0;
    if (s[start] == '.' && s[start+1] == '/') start += 2;
    if (s[end-4] != '.' || (s[end-3] != 't' && s[end-3] != 'T') ||
        (s[end-2] != 'e' && s[end-2] != 'E') || (s[end-1] != 'x' && s[end-1] != 'X')) return 0;
    part = start;
    for (i = start; i <= end; ++i) {
        unsigned char c = i == end ? '/' : (unsigned char)s[i];
        if (c == '/') {
            size_t k = i-part;
            if (!k || (k == 1 && s[part] == '.') || (k == 2 && s[part] == '.' && s[part+1] == '.')) return 0;
            part = i+1;
        } else if (c < 32 || c == 127 || strchr("\\\"%#^$&{}~:|", c)) return 0;
    }
    /* Validate scalar UTF-8 without case/Unicode normalization. */
    for (i = start; i < end;) {
        uint32_t cp, minimum; unsigned k;
        unsigned char c = (unsigned char)s[i++];
        if (c < 128) continue;
        if (c >= 0xc2 && c <= 0xdf) { k=1; minimum=0x80; cp=c&31; }
        else if (c >= 0xe0 && c <= 0xef) { k=2; minimum=0x800; cp=c&15; }
        else if (c >= 0xf0 && c <= 0xf4) { k=3; minimum=0x10000; cp=c&7; }
        else return 0;
        if (i+k > end) return 0;
        while (k--) { c=(unsigned char)s[i++]; if ((c&0xc0)!=0x80) return 0; cp=(cp<<6)|(c&63); }
        if (cp<minimum || cp>0x10ffff || (cp>=0xd800 && cp<=0xdfff) || (cp>=0x80 && cp<=0x9f)) return 0;
    }
    return 1;
}
static uint32_t oc_dvi_be32(const unsigned char *p) {
    return ((uint32_t)p[0]<<24)|((uint32_t)p[1]<<16)|((uint32_t)p[2]<<8)|p[3];
}
static int oc_dvi_native_output_valid(void) {
    struct stat st; FILE *f; unsigned char pre[15], tail[16], post[29];
    size_t pad=0, size, ptr; int ok;
    const char *name;
    if (totalpages <= 0 || outputfilename == 0) return 0;
    name = makecstring(outputfilename);
    if (stat(name,&st) || !S_ISREG(st.st_mode) || st.st_size < 100 || st.st_size > 8388608) return 0;
    size=(size_t)st.st_size;
    f=fopen(name,"rb"); if (!f) return 0;
    ok=fread(pre,1,sizeof(pre),f)==sizeof(pre) && !fseek(f,-16,SEEK_END) && fread(tail,1,16,f)==16;
    if (ok) {
        while (pad<16 && tail[15-pad]==223) ++pad;
        ok=pre[0]==247 && pre[1]==2 && pad>=4 && pad<=7 && tail[15-pad]==2 && tail[10-pad]==249;
        if (ok) {
            ptr=oc_dvi_be32(tail+11-pad);
            ok=ptr<size-29 && !fseek(f,(long)ptr,SEEK_SET) && fread(post,1,29,f)==29 &&
                post[0]==248 && !memcmp(pre+2,post+5,12) && oc_dvi_be32(pre+2)==25400000 &&
                oc_dvi_be32(pre+6)==473628672 && oc_dvi_be32(pre+10)>0 &&
                (((unsigned)post[27]<<8)|post[28])==(unsigned)totalpages;
        }
    }
    ok=!ferror(f) && ok; if (fclose(f)) ok=0;
    if (texmflogname) {
        name=makecstring(texmflogname);
        if (stat(name,&st) || !S_ISREG(st.st_mode) || st.st_size>262144) ok=0;
    }
    return ok;
}
#endif
