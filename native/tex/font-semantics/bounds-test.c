/* Memory-safety unit against the actual generated port memoryword layout.
 * Synthetic nodes test only this helper's bounds; real TeX is tested separately.
 */
#include "ptexlib.h"
#include "oc_font_semantics.h"
#include <assert.h>
memoryword *zmem;
halfword himemmin,memend;
static char emitted[2048];
void pdf_puts(const char *s) { assert(strlen(emitted)+strlen(s)<sizeof emitted);strcat(emitted,s); }
static void character_node(int n,int c) {
    zmem[n].hh.b0=1;zmem[n].hh.b1=c;zmem[n].hh.v.RH=-268435455;
}
int main(int argc,char **argv) {
    assert(argc==2);himemmin=100;memend=599;zmem=calloc(600,sizeof *zmem);assert(zmem);
    const int which=atoi(argv[1]);
    if (which==0) {
        character_node(100,105);assert(oc_sem_decode(68,2031721)==105);oc_sem_attach(100);
        assert(oc_sem_meaning(68,105)==2031721);assert(!oc_sem_dump_safe());
        character_node(101,105);oc_sem_copy(101,100);oc_sem_clear(100);
        assert(oc_sem_prepare(101,-268435455)==1);oc_sem_pdf_begin();oc_sem_pdf_end();
        assert(!strcmp(emitted,"\n/Span << /ActualText <FEFF0456> >> BDC\n\nEMC\n"));
        oc_sem_hsave(1,101);oc_sem_hstash(1,0);character_node(102,105);oc_sem_hrestore(102,1);
        assert(oc_sem_prepare(102,-268435455)==0);oc_sem_hunstash(1,0);oc_sem_hrestore(102,1);
        assert(oc_sem_prepare(102,-268435455)==1);oc_sem_clear(101);oc_sem_clear(102);
        assert(oc_sem_dump_safe());assert(oc_font_semantics_status()==1);
    } else if (which==1 || which==2) {
        const int count=which==1?64:65;
        for(int i=0;i<count;++i){character_node(100+i,i==count-1?105:102);if(i<count-1)zmem[100+i].hh.v.RH=101+i;}
        oc_sem_decode(68,2031721);oc_sem_attach(99+count);character_node(500,11);
        assert(oc_sem_prepare(500,100)==(which==1));
        assert(oc_font_semantics_status()==(which==1?1:2));
    } else if (which==3) {
        for(int i=100;i<165;++i){character_node(i,102);if(i<164)zmem[i].hh.v.RH=i+1;}
        oc_sem_reverse(100);assert(oc_font_semantics_status()==2);
    } else if (which==4) {
        oc_sem_clear(-1);oc_sem_clear(5000001);assert(!oc_sem_prepare(5000000,-268435455));
        assert(oc_font_semantics_status()==2);
    } else if (which==5) {
        character_node(100,105);oc_sem_hrestore(100,64);oc_sem_hclear(-1);oc_sem_hstash(0,2);
        assert(oc_font_semantics_status()==2);
    } else if (which==6) {
        character_node(100,105);assert(oc_sem_decode(68,999999)==999999);oc_sem_attach(100);
        assert(!oc_sem_prepare(100,-268435455));assert(oc_font_semantics_status()==1);
    } else if (which==7) {
        character_node(100,105);oc_sem_copy(100,-1);assert(oc_font_semantics_status()==2);
    } else assert(0);
    free(zmem);return 0;
}
