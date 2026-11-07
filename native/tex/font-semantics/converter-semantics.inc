/* Source-owned, narrowly parsed semantic DVI specials. GPL-2.0-or-later.
 * Appended to the private graphics1 wrapper. Never exported mutation hooks.
 */
namespace {
bool oc_sem_open=false,oc_sem_page=false,oc_sem_failed=false;
uint32_t oc_sem_spans=0,oc_sem_scalars=0;
int oc_sem_refuse() {
    oc_sem_failed=true;geo_unsafe=true;geo_size=0;return 0;
}
}
extern "C" {
uint32_t oc_dvi_font_semantics_abi(void) { return 1; }
uint32_t oc_dvi_font_semantics_status(void) { return oc_sem_failed || oc_sem_open ? 2 : 1; }
uint32_t oc_dvi_font_semantics_spans(void) { return oc_sem_spans; }
void oc_dvi_semantic_page_begin(void) {
    if (oc_sem_page || oc_sem_open) oc_sem_refuse();
    oc_sem_page=true;
}
void oc_dvi_semantic_page_end(void) {
    if (!oc_sem_page || oc_sem_open) oc_sem_refuse();
    oc_sem_page=false;oc_sem_open=false;
}
int oc_dvi_semantic_special(const char *data,int32_t size) {
    static constexpr char head[]="pdf:literal direct /Span << /ActualText <FEFF";
    static constexpr char tail[]="> >> BDC";
    static constexpr char end[]="pdf:literal direct EMC";
    if (!data || size<0) return 0;
    const size_t n=static_cast<size_t>(size);
    const bool closing=n==sizeof(end)-1 && !std::memcmp(data,end,n);
    const bool opening=n>=sizeof(head)-1 && !std::memcmp(data,head,sizeof(head)-1);
    if (!opening && !closing) return 0;
    if (!oc_sem_page || oc_sem_failed) return oc_sem_refuse();
    if (closing) {
        if (!oc_sem_open) return oc_sem_refuse();
        oc_dvi_semantic_content("EMC",3);oc_sem_open=false;return 1;
    }
    const size_t fixed=sizeof(head)-1+sizeof(tail)-1;
    if (oc_sem_open || n<fixed+4 || n>fixed+64*4 || (n-fixed)%4 ||
        std::memcmp(data+n-(sizeof(tail)-1),tail,sizeof(tail)-1)) return oc_sem_refuse();
    const size_t count=(n-fixed)/4;
    bool marked=false;
    for (size_t offset=sizeof(head)-1;offset<n-(sizeof(tail)-1);offset+=4) {
        unsigned value=0;
        for (size_t k=0;k<4;++k) {
            const unsigned char c=static_cast<unsigned char>(data[offset+k]);
            if (!(c>='0' && c<='9') && !(c>='A' && c<='F')) return oc_sem_refuse();
            value=(value<<4)|(c<='9' ? c-'0' : c-'A'+10);
        }
        if (value==0x0406 || value==0x0456) marked=true;
        else if (value<32 || value>126) return oc_sem_refuse();
    }
    if (!marked || oc_sem_spans==65536 || count>262144-oc_sem_scalars) return oc_sem_refuse();
    ++oc_sem_spans;oc_sem_scalars+=static_cast<uint32_t>(count);oc_sem_open=true;
    constexpr size_t prefix=sizeof("pdf:literal direct ")-1;
    oc_dvi_semantic_content(data+prefix,static_cast<int32_t>(n-prefix));
    return 1;
}
}
