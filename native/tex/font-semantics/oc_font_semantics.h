/* Source-owned, output-only Unicode provenance. No mutation API is exported. */
#ifndef OC_FONT_SEMANTICS_H
#define OC_FONT_SEMANTICS_H
#include <stdint.h>
int oc_sem_decode(int command, int value);
int oc_sem_meaning(int command, int value);
void oc_sem_clear(int node);
void oc_sem_attach(int node);
void oc_sem_copy(int target, int source);
void oc_sem_hsave(int index, int node);
void oc_sem_hrestore(int node, int index);
void oc_sem_hclear(int index);
void oc_sem_hstash(int index, int slot);
void oc_sem_hunstash(int index, int slot);
void oc_sem_reverse(int components);
int oc_sem_prepare(int node, int components);
void oc_sem_pdf_begin(void);
void oc_sem_pdf_end(void);
int oc_sem_dvi_length(int ending);
int oc_sem_dvi_byte(int index);
int oc_sem_dump_safe(void);
uint32_t oc_font_semantics_abi(void);
uint32_t oc_font_semantics_status(void);
uint32_t oc_font_semantics_spans(void);
uint32_t oc_font_semantics_copies(void);
uint32_t oc_font_semantics_reconstructions(void);
uint32_t oc_font_semantics_current(void);
#define ocsemdecode oc_sem_decode
#define ocsemmeaning oc_sem_meaning
#define ocsemclear oc_sem_clear
#define ocsemattach oc_sem_attach
#define ocsemcopy oc_sem_copy
#define ocsemhsave oc_sem_hsave
#define ocsemhrestore oc_sem_hrestore
#define ocsemhclear oc_sem_hclear
#define ocsemhstash oc_sem_hstash
#define ocsemhunstash oc_sem_hunstash
#define ocsemreverse oc_sem_reverse
#define ocsemprepare oc_sem_prepare
#define ocsempdfbegin oc_sem_pdf_begin
#define ocsempdfend oc_sem_pdf_end
#define ocsemdvilength oc_sem_dvi_length
#define ocsemdvibyte oc_sem_dvi_byte
#define ocsemdumpsafe oc_sem_dump_safe
#define ocsemresetcurrent() ((void)oc_sem_decode(0, 0))
#endif
