/* Candidate-only native MEMFS resolver. No JavaScript network resolver or host
 * search is linked. /tex/<flat basename> is an explicit read-only runtime alias;
 * complete relative project paths retain exact bytes and never use basename fallback. */
#ifndef OC_SYNCTEX_DVI_FILES_H
#define OC_SYNCTEX_DVI_FILES_H
static int oc_dvi_path_ok(const char *name, int writing) {
    const char *s=name,*part; size_t n,i;
    if (!s) return 0;
    n=strnlen(s,257); if (!n || n>256) return 0;
    if (!strncmp(s,"/tex/",5)) {
        if (writing || !s[5] || strchr(s+5,'/')) return 0;
        s+=5;
    } else if (*s=='/') return 0;
    if (!strncmp(s,"./",2)) s+=2;
    part=s;
    for (i=0;;++i) {
        unsigned char c=(unsigned char)s[i];
        if (c=='/' || !c) {
            size_t k=(s+i)-part;
            if (!k || (k==1 && part[0]=='.') || (k==2 && part[0]=='.' && part[1]=='.')) return 0;
            part=s+i+1; if (!c) break;
        } else if (c<32 || c==127 || c=='\\' || c==':' || c=='|') return 0;
    }
    return 1;
}
static char *oc_dvi_runtime_name(char *path) {
    char *result; struct stat st;
    if (stat(path,&st) || !S_ISREG(st.st_mode)) return NULL;
    result=xmalloc(strlen(path)+1);strcpy(result,path);return result;
}
static char *oc_dvi_find_runtime(const char *name, kpse_file_format_type format) {
    char path[300]; char *result;
    if (!name || !oc_dvi_path_ok(name,0) || strchr(name,'/')) return NULL;
    strcpy(path,"/tex/");strcat(path,name);
    result=oc_dvi_runtime_name(path);if (result) return result;
    if (!strchr(name,'.')) { fix_extension(path,format);return oc_dvi_runtime_name(path); }
    return NULL;
}
static char *oc_dvi_find_runtime_pk(const char *name,unsigned dpi) {
    char path[300];
    if (!name || !oc_dvi_path_ok(name,0) || strchr(name,'/') || strlen(name)>249) return NULL;
    snprintf(path,sizeof(path),"/tex/%s.%upk",name,dpi);
    return oc_dvi_runtime_name(path);
}
#endif
