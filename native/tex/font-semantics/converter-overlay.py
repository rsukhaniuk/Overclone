#!/usr/bin/env python3
"""Explicit semantic-only overlay AFTER exact accepted graphics1 sources."""
import hashlib
import json
from pathlib import Path
import sys

out=Path(sys.argv[1]);here=Path(__file__).resolve().parent
sha=lambda p:hashlib.sha256(Path(p).read_bytes()).hexdigest()
a=json.loads((out/'association.json').read_text())
if a.get('graphicsVariant',{}).get('version')!=1:raise SystemExit('graphics1 parent required')
for row in a['source']:
    if sha(out/row['path'])!=row['after']:raise SystemExit('native parent drift: '+row['path'])
for key,name in [('wrapperAfter','oc_dvi_graphics.cpp'),('headerAfter','oc_dvi.h')]:
    if sha(out/name)!=a['graphicsVariant'][key]:raise SystemExit('graphics parent drift: '+name)
changes=[]
def edit(name,before,after):
    p=out/name;s=p.read_text()
    if s.count(before)!=1:raise SystemExit('semantic preimage mismatch: '+name+': '+before[:60])
    changes.append({'path':name,'before':before,'after':after})
    p.write_text(s.replace(before,after))
edit('oc_dvi.h','void oc_dvi_geo_image(int safe);','''void oc_dvi_geo_image(int safe);
int oc_dvi_semantic_special(const char *data,int32_t size);
void oc_dvi_semantic_content(const char *data,int32_t size);
void oc_dvi_semantic_page_begin(void);
void oc_dvi_semantic_page_end(void);''')
edit('oc_dvi_graphics.cpp','#include "oc_dvi.h"','#define OC_DVI_INTERNAL_GEOMETRY 1\n#include "oc_dvi.h"')
# Existing DVI arithmetic/font/glyph dispatch remains literally untouched.
edit('dpx-dvi.c','''    graphics_mode();

    p = (const char *) buffer;''','''    p = (const char *) buffer;
    if (oc_dvi_semantic_special(p,size)) return;
    graphics_mode();''')
# Split only the PDF text string. Keep motion_state, font, text reference,
# offset, matrix and every rounding/correction accumulator exactly unchanged.
edit('dpx-pdfdev.c','''void
graphics_mode (void)''','''void
oc_dvi_semantic_content(const char *data,int32_t size)
{
  if (motion_state == STRING_MODE)
    pdf_doc_add_page_content(text_state.is_mb ? ">]TJ" : ")]TJ",4);
  pdf_doc_add_page_content("\\n",1);
  pdf_doc_add_page_content(data,size);
  pdf_doc_add_page_content("\\n",1);
  if (motion_state == STRING_MODE)
    pdf_doc_add_page_content(text_state.is_mb ? "[<" : "[(",2);
}

void
graphics_mode (void)''')
edit('oc_dvi_graphics.cpp','''                      double llx,double lly,double urx,double ury) {
    if (geo_active''','''                      double llx,double lly,double urx,double ury) {
    oc_dvi_semantic_page_begin();
    if (geo_active''')
edit('oc_dvi_graphics.cpp','''void oc_dvi_geo_end(uint32_t page) {
    if (!geo_active''','''void oc_dvi_geo_end(uint32_t page) {
    oc_dvi_semantic_page_end();
    if (!geo_active''')
cpp=out/'oc_dvi_graphics.cpp'
cpp.write_bytes(cpp.read_bytes()+b'\n'+(here/'converter-semantics.inc').read_bytes())
for row in a['source']:row['after']=sha(out/row['path'])
a['fontSemanticsVariant']={'version':1,'capability':'oc_dvi_font_semantics_abi()==1',
    'status':'oc_dvi_font_semantics_status()==1 after actual successful run',
    'overlayRecipe':sha(__file__),'implementation':sha(here/'converter-semantics.inc'),
    'wrapperAfter':sha(cpp),'headerAfter':sha(out/'oc_dvi.h'),'edits':changes,
    'bounds':{'scalarsPerSpan':64,'spansPerDocument':65536,'scalarsPerDocument':262144,'nesting':1},
    'domain':'exact native emitted same-page balanced semantic-only ActualText; STRING_MODE offset/reference/font/matrices preserved'}
(out/'association.json').write_text(json.dumps(a,indent=2)+'\n')
