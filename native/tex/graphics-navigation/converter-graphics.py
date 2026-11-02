#!/usr/bin/env python3
"""Explicit graphics1 source overlay AFTER accepted ABI2/3 geometry hooks.
No default source mutation. Native parsed raster handler + actual native gstate
restoration are required; an unknown string prefix is never sufficient.
"""
import hashlib
import json
from pathlib import Path
import sys
out=Path(sys.argv[1]);source=Path(sys.argv[2]);owned=Path(__file__).resolve().parent.parent/'dvi-engine'
def sha(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
association=out/'association.json'
try:a=json.loads(association.read_text())
except (OSError,ValueError) as error:raise SystemExit('invalid native parent association')from error
for x in a['source']:
 if sha(out/x['path'])!=x['after']:raise SystemExit('native overlay drift: '+x['path'])
def replace(p,before,after):
 b=p.read_bytes();x,y=before.encode(),after.encode()
 if b.count(x)!=1:raise SystemExit('graphics preimage mismatch: '+p.name+': '+before[:60])
 p.write_bytes(b.replace(x,y))
# Private wrapper copy: defer UNKNOWN specials until actual native dispatch has
# either supplied a complete typed raster witness or returned failure. Unknown
# programs still latch unsafe at end. An earlier unsafe state is never cleared.
cpp=out/'oc_dvi_graphics.cpp';cpp.write_bytes((owned/'oc_dvi.cpp').read_bytes())
replace(cpp,'bool geo_pending_put=false, geo_put_checked=false;', 'bool geo_pending_put=false, geo_put_checked=false;\nbool geo_unverified=false,geo_image_draw_checked=false,geo_image_draw_safe=false;')
replace(cpp,'    geo_pending_put=false;geo_put_checked=false;','    geo_pending_put=false;geo_put_checked=false;\n    geo_unverified=false;geo_image_draw_checked=false;geo_image_draw_safe=false;')
replace(cpp,'    geo_unsafe=true;\n}\nvoid oc_dvi_geo_put', '    geo_unverified=true;\n}\nvoid oc_dvi_geo_image_draw(int safe) { geo_image_draw_checked=true;geo_image_draw_safe=safe!=0; }\nvoid oc_dvi_geo_image(int safe) {\n    if (safe && geo_image_draw_checked && geo_image_draw_safe) geo_unverified=false;\n    else geo_unsafe=true;\n}\nuint32_t oc_dvi_graphics_abi(void) { return 1; }\nvoid oc_dvi_geo_put')
replace(cpp,'    if (actual_result || (geo_pending_put && !geo_put_checked)) geo_unsafe=true;','    if (actual_result || geo_unverified || (geo_pending_put && !geo_put_checked)) geo_unsafe=true;')
# Extend only private declarations, never native authority exports.
h=out/'oc_dvi.h';h.write_bytes((owned/'oc_dvi.h').read_bytes())
replace(h,'void oc_dvi_geo_units(double unit_bp, double header_mag, int kind);','void oc_dvi_geo_units(double unit_bp, double header_mag, int kind);\nvoid oc_dvi_geo_image_draw(int safe);\nvoid oc_dvi_geo_image(int safe);')
# Native image draw: observe ACTUAL q/Q return values, before/after stack depth
# and CTM; validate final composed local matrix, clipping extent and restoration.
dev=out/'dpx-pdfdev.c';dev.write_bytes((source/'dpx-pdfdev.c').read_bytes())
if sha(dev)!='99be0b7b4d392b160d4afd49fd974c77d8a5ed59819864f394145402af56debb':
 raise SystemExit('native pdfdev source preimage mismatch')
dev.write_bytes(b'#define OC_DVI_INTERNAL_GEOMETRY 1\n#include "oc_dvi.h"\n'+dev.read_bytes())
replace(dev,'  pdf_tmatrix  M, M1;\n  pdf_rect     r;','  pdf_tmatrix  M, M1, oc_before, oc_after;\n  int oc_depth=pdf_dev_current_depth(),oc_save,oc_restore,oc_valid;\n  pdf_rect     r;\n  pdf_dev_currentmatrix(&oc_before);')
replace(dev,'  graphics_mode();\n  pdf_dev_gsave();\n\n  pdf_ximage_scale_image','  graphics_mode();\n  oc_save=pdf_dev_gsave();\n\n  pdf_ximage_scale_image')
replace(dev,'  pdf_concatmatrix(&M, &M1);\n  pdf_dev_concat(&M);','  pdf_concatmatrix(&M, &M1);\n  oc_valid=oc_save==0 && pdf_dev_current_depth()==oc_depth+1 &&\n    isfinite(M.a)&&isfinite(M.b)&&isfinite(M.c)&&isfinite(M.d)&&isfinite(M.e)&&isfinite(M.f)&&\n    fabs(M.a)<=1000000&&fabs(M.b)<=1000000&&fabs(M.c)<=1000000&&fabs(M.d)<=1000000&&fabs(M.e)<=1000000&&fabs(M.f)<=1000000&&\n    isfinite(M.a*M.d-M.b*M.c)&&(M.a*M.d-M.b*M.c)!=0 &&\n    isfinite(r.llx)&&isfinite(r.lly)&&isfinite(r.urx)&&isfinite(r.ury)&&r.llx<r.urx&&r.lly<r.ury;\n  pdf_dev_concat(&M);')
replace(dev,'  pdf_dev_grestore();\n\n  pdf_doc_add_page_resource("XObject",','  oc_restore=pdf_dev_grestore();\n  pdf_dev_currentmatrix(&oc_after);\n  oc_dvi_geo_image_draw(oc_valid && oc_restore==0 && pdf_dev_current_depth()==oc_depth &&\n    oc_before.a==oc_after.a&&oc_before.b==oc_after.b&&oc_before.c==oc_after.c&&\n    oc_before.d==oc_after.d&&oc_before.e==oc_after.e&&oc_before.f==oc_after.f);\n\n  pdf_doc_add_page_resource("XObject",')
# Actual native parser/resource type witness. No custom PDF dictionary accepted,
# no hidden image, no trailing program, no external PDF/form/PS interpretation.
p=out/'dpx-spc_pdfm.c'
replace(p,'  if (!(ti.flags & INFO_DO_HIDE))\n    pdf_dev_put_image(xobj_id, &ti, spe->x_user, spe->y_user);','  if (!(ti.flags & INFO_DO_HIDE))\n    pdf_dev_put_image(xobj_id, &ti, spe->x_user, spe->y_user);\n  skip_white(&args->curptr,args->endptr);\n  oc_dvi_geo_image(options.dict==NULL && !(ti.flags & INFO_DO_HIDE) &&\n    args->curptr==args->endptr && pdf_ximage_get_subtype(xobj_id)==PDF_XOBJECT_TYPE_IMAGE);')
a['graphicsVariant']={'version':1,'baseAbi':a['abi'],'newCapability':'oc_dvi_graphics_abi()==1','wrapperBefore':sha(owned/'oc_dvi.cpp'),'wrapperAfter':sha(cpp),'headerBefore':sha(owned/'oc_dvi.h'),'headerAfter':sha(h),'pdfdevBefore':sha(source/'dpx-pdfdev.c'),'pdfdevAfter':sha(dev),'recipeSha256':sha(__file__),'domain':'Actual native visible raster XObject image, no added dictionary/program, actual finite invertible local CTM, actual successful balanced native gsave/grestore with restored parent matrix/depth.'}
for x in a['source']:x['after']=sha(out/x['path'])
a['source'].append({'path':'dpx-pdfdev.c','before':sha(source/'dpx-pdfdev.c'),'after':sha(dev)})
association.write_text(json.dumps(a,indent=2)+'\n')
