#!/usr/bin/env python3
"""Exact-preimage ABI2 converter hooks; does not write supplied source trees."""
import hashlib
import json
from pathlib import Path
import sys
import subprocess

source,out,base_doc=map(Path,sys.argv[1:4])
if out.exists(): raise SystemExit('fresh converter overlay required')
pins={
 'dpx-dvi.c':'9d10d33fe0d0d4556b4082bb3fe1003a167fae00ed0ac6866e7a0ab99caf68f0',
 'dpx-pdfdoc.c':'66550979ce87335cd139fb6f55e40b91eac390c7fed8b38e99c9f4358ebaa370',
 'dpx-specials.c':'8ec9b987978fe07fee0a44a9e28b3379f70a8423a8b249e9c12bb58915836024',
 'dpx-spc_pdfm.c':'54ad93c26e9afcad94f263dda5e98a528514992b49e0845ae1583159c2367093',
}
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
for n,h in pins.items():
 if sha(source/n)!=h: raise SystemExit('converter preimage mismatch: '+n)
out.mkdir()
texts={n:(base_doc if n=='dpx-pdfdoc.c' else source/n).read_bytes() for n in pins}
def replace(n,a,b):
 a,b=a.encode('ascii'),b.encode('ascii')
 if texts[n].count(a)!=1: raise SystemExit('converter hook preimage mismatch: '+n+': '+a[:60].decode())
 texts[n]=texts[n].replace(a,b)
# Add private declarations to the existing native headers, not exported authority.
for n in pins:
 texts[n]=b'#define OC_DVI_INTERNAL_GEOMETRY 1\n#include "oc_dvi.h"\n'+texts[n]
replace('dpx-dvi.c','    dvi2pts  *= (72.0 / 254000.0);',
 '    dvi2pts  *= (72.0 / 254000.0);\n'
 '    oc_dvi_geo_units(dvi2pts, (double)dvi_info.mag/1000.0,\n'
 '        post_id_byte==2 && dvi_info.unit_num==25400000 && dvi_info.unit_den==473628672 ? 2 : 0);')
replace('dpx-pdfdoc.c','  pdf_doc_new_page(p);\n  pdf_dev_bop(&M);',
 '  pdf_doc_new_page(p);\n'
 '  {\n'
 '    pdf_rect writer_extent;\n'
 '    unsigned page=(unsigned)pdf_doc_current_page_number();\n'
 '    pdf_doc_get_mediabox(page, &writer_extent);\n'
 '    oc_dvi_geo_begin(page, M.a,M.b,M.c,M.d,M.e,M.f,\n'
 '        writer_extent.llx,writer_extent.lly,writer_extent.urx,writer_extent.ury);\n'
 '  }\n'
 '  pdf_dev_bop(&M);')
replace('dpx-pdfdoc.c','  pdf_doc_finish_page(p);\n\n  return;\n}',
 '  oc_dvi_geo_end((unsigned)pdf_doc_current_page_number());\n'
 '  pdf_doc_finish_page(p);\n\n  return;\n}')
replace('dpx-specials.c','  struct spc_handler special;',
 '  struct spc_handler special;\n  oc_dvi_geo_special(buffer,size);')
replace('dpx-specials.c','  check_garbage(&args);\n\n  return error;',
 '  check_garbage(&args);\n  oc_dvi_geo_special_end(error);\n\n  return error;')
# Reuse the ACTUAL supplied PDF dictionary parser/types. No raw-PDF regex or new
# parser dependency; permit only direct inert numeric crop/rotation page facts.
safe=r'''#include <math.h>
static int oc_dvi_geo_safe_key(pdf_obj *key,pdf_obj *value,void *context)
{
  const char *name;
  unsigned *count=(unsigned *)context;
  if (++*count>2 || pdf_obj_typeof(key)!=PDF_NAME) return 1;
  name=pdf_name_value(key);
  if (!strcmp(name,"Rotate")) {
    double r;
    if (pdf_obj_typeof(value)!=PDF_NUMBER) return 1;
    r=pdf_number_value(value);
    return !(r==0 || r==90 || r==180 || r==270);
  }
  if (!strcmp(name,"CropBox")) {
    double b[4]; unsigned i;
    if (pdf_obj_typeof(value)!=PDF_ARRAY || pdf_array_length(value)!=4) return 1;
    for (i=0;i<4;++i) {
      pdf_obj *v=pdf_get_array(value,(int)i);
      if (!v || pdf_obj_typeof(v)!=PDF_NUMBER) return 1;
      b[i]=pdf_number_value(v);
      if (!isfinite(b[i]) || fabs(b[i])>1000000) return 1;
    }
    return !(b[0]<b[2] && b[1]<b[3]);
  }
  return 1;
}
static int oc_dvi_geo_safe_put(const char *ident,pdf_obj *obj)
{
  unsigned count=0;
  return !strcmp(ident,"thispage") && pdf_obj_typeof(obj)==PDF_DICT &&
    !pdf_foreach_dict(obj,oc_dvi_geo_safe_key,&count);
}
'''
replace('dpx-spc_pdfm.c','static int\nspc_handler_pdfm_put',safe+'\nstatic int\nspc_handler_pdfm_put')
replace('dpx-spc_pdfm.c','  switch (pdf_obj_typeof(obj1)) {',
 '  oc_dvi_geo_put(oc_dvi_geo_safe_put(ident,obj2));\n\n  switch (pdf_obj_typeof(obj1)) {')
for n,b in texts.items(): (out/n).write_bytes(b)
(out/'association.json').write_text(json.dumps({'schemaVersion':1,'abi':2,
 'baseResolvedMediaBoxOverlaySha256':sha(base_doc),
 'source':[{'path':n,'before':h,'after':sha(out/n)} for n,h in pins.items()],
 'geometrySource':'Actual native DVI scale/header magnification, actual writer BOP matrix and resolved native writer extent, not PDF-derived approximation',
},indent=2)+'\n')
if sys.argv[4:]:
 if sys.argv[4:]!=['--graphics']:raise SystemExit('unknown explicit converter capability')
 subprocess.run([sys.executable,str(Path(__file__).resolve().parent.parent/'graphics-navigation/converter-graphics.py'),str(out),str(source)],check=True,timeout=60)
