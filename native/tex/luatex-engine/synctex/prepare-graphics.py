#!/usr/bin/env python3
"""Explicit graphics1 over accepted ABI5, defaults remain byte-independent."""
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
source,out=map(Path,sys.argv[1:3]);here=Path(__file__).resolve().parent
subprocess.run([sys.executable,str(here/'prepare-geometry.py'),str(source),str(out)],check=True)
def sha(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
a=out/'association.json'
try:v=json.loads(a.read_text())
except (OSError,ValueError)as error:raise SystemExit('invalid ABI5 association')from error
(out/'abi5-association.json').write_bytes(a.read_bytes())
for n,h in {'pdfsaverestore.c':'29b62d08e1b6c3c3ee0593c0c83d08c4ae1ff9be7b9ac5b25dc50d08e5edab25','pdfsetmatrix.c':'a1bc1c6043d8424b5a871184f89e1eb903f624d16b634f5e26ff8e5d2230c4b8'}.items():
 p=source/'texk/web2c/luatexdir/pdf'/n
 if sha(p)!=h:raise SystemExit('typed native graphics preimage mismatch: '+n)
 shutil.copyfile(p,out/'luatexdir/pdf'/n);v['upstream'].append({'path':'luatexdir/pdf/'+n,'before':h})
def replace(n,old,new):
 p=out/'luatexdir'/n;b=p.read_bytes();x,y=old.encode(),new.encode()
 if b.count(x)!=1:raise SystemExit('graphics typed hook mismatch: '+n+': '+old[:60])
 p.write_bytes(b.replace(x,y))
for n in ['pdf/pdfsetmatrix.c','pdf/pdfsaverestore.c','pdf/pdfshipout.c','pdf/pdfliteral.c']:
 replace(n,'#include "ptexlib.h"','#include "ptexlib.h"\n#include "writer_graphics.h"')
replace('pdf/pdfliteral.c','    oc_lua_geometry_unsafe(); /* includes setmatrix, colorstack and VF specials */','    if (!oc_lua_graphics_literal_consume()) oc_lua_geometry_unsafe(); /* arbitrary literals remain unsafe */')
replace('pdf/pdfsaverestore.c','    checkpdfsave(pdf->posstruct->pos);\n    pdf_literal(pdf, \'q\', set_origin, false);','    int before=pos_stack_used;\n    checkpdfsave(pdf->posstruct->pos);\n    oc_lua_graphics_save(before,pos_stack_used,matrix_stack_used,pdf->posstruct->pos.h,pdf->posstruct->pos.v);\n    oc_lua_graphics_literal_begin(1);\n    pdf_literal(pdf, \'q\', set_origin, false);\n    oc_lua_graphics_literal_end();')
replace('pdf/pdfsaverestore.c','    checkpdfrestore(pdf->posstruct->pos);\n    pdf_literal(pdf, \'Q\', set_origin, false);','    int before=pos_stack_used;\n    checkpdfrestore(pdf->posstruct->pos);\n    oc_lua_graphics_restore(before,pos_stack_used,matrix_stack_used,pdf->posstruct->pos.h,pdf->posstruct->pos.v);\n    oc_lua_graphics_literal_begin(2);\n    pdf_literal(pdf, \'Q\', set_origin, false);\n    oc_lua_graphics_literal_end();')
replace('pdf/pdfsetmatrix.c','    pdfsetmatrix((char *) cur_string, pos);','    int before=matrix_stack_used;\n    pdfsetmatrix((char *) cur_string, pos);\n    oc_lua_graphics_matrix((char *)cur_string,(size_t)cur_length,before,matrix_stack_used);')
replace('pdf/pdfsetmatrix.c','    pdf_literal(pdf, s, set_origin, false);','    oc_lua_graphics_literal_begin(3);\n    pdf_literal(pdf, s, set_origin, false);\n    oc_lua_graphics_literal_end();')
replace('pdf/pdfshipout.c','    if (shipping_mode == SHIPPING_PAGE && pdf->o_mode == OMODE_PDF)\n        oc_lua_geometry_end(total_pages, pdf->page_size.h, pdf->page_size.v);','    if (shipping_mode == SHIPPING_PAGE && pdf->o_mode == OMODE_PDF) {\n        oc_lua_graphics_page_end(pos_stack_used,matrix_stack_used);\n        oc_lua_geometry_end(total_pages, pdf->page_size.h, pdf->page_size.v);\n    }')
v['graphicsVariant']={'version':1,'baseAbi':5,'capability':'oc_lua_graphics_abi()==1','abi5AssociationSha256':sha(out/'abi5-association.json'),'domain':'Actual native balanced save/restore + fully consumed finite positive axis-aligned native setmatrix; actual native matrix stack supplies source-node coordinate/dimension observations. Arbitrary raw programs, forms, other dictionaries and unsupported transforms retain refusal.','recipeSha256':sha(__file__)}
for x in v['upstream']:x['after']=sha(out/x['path'])
a.write_text(json.dumps(v,indent=2)+'\n')
