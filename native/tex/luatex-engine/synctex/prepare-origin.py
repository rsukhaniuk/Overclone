#!/usr/bin/env python3
"""Private post-graphics1 native origin1 writer overlay. Parent stays exact."""
import hashlib,json,subprocess,sys,shutil
from pathlib import Path
source,out=map(Path,sys.argv[1:3]);here=Path(__file__).resolve().parent
sha=lambda p:hashlib.sha256(Path(p).read_bytes()).hexdigest()
if sha(here/'prepare-graphics.py')!='4d155b6ffb2975d2d605d61fd666f70bd44960e62a4bb45307910298f2f3e927':raise SystemExit('graphics parent recipe drift')
subprocess.run([sys.executable,str(here/'prepare-graphics.py'),str(source),str(out)],check=True)
a=out/'association.json'
try:v=json.loads(a.read_text())
except (OSError,ValueError)as error:raise SystemExit('invalid graphics native association')from error
for x in v['upstream']:assert sha(out/x['path'])==x['after'],x['path']
(out/'graphics1-association.json').write_bytes(a.read_bytes());p=out/'luatexdir/pdf/pdfgen.c';b=p.read_bytes()
old=r'''        pdf_add_name(pdf, "MediaBox");
        pdf_begin_array(pdf);
        pdf_add_int(pdf, 0);
        pdf_add_int(pdf, 0);
        pdf_add_bp(pdf, pdf->page_size.h);
        pdf_add_bp(pdf, pdf->page_size.v);
        pdf_end_array(pdf);
        page_attributes = pdf_page_attr ;
        if (page_attributes != null)
            pdf_print_toks(pdf, page_attributes);
        print_pdf_table_string(pdf, "pageattributes");'''
# Consume actual native expanded token/Lua strings exactly ONCE. Existing
# native formatting and output operations are unchanged when MediaBox absent.
# A valid explicit MediaBox suppresses the built-in key, never duplicates it.
new=r'''        {
            int oc_tex_len=0,oc_explicit;
            char *oc_tex=NULL;
            const char *oc_lua=NULL;
            size_t oc_lua_len=0;
            page_attributes=pdf_page_attr;
            if(page_attributes!=null)oc_tex=tokenlist_to_cstring(page_attributes,true,&oc_tex_len);
            lua_rawgeti(Luas,LUA_REGISTRYINDEX,lua_key_index(pdf_data));
            lua_rawget(Luas,LUA_REGISTRYINDEX);
            lua_pushstring(Luas,"pageattributes");
            lua_rawget(Luas,-2);
            if(lua_type(Luas,-1)==LUA_TSTRING)oc_lua=lua_tolstring(Luas,-1,&oc_lua_len);
            oc_explicit=oc_lua_origin_page(total_pages,oc_tex,(size_t)oc_tex_len,
                oc_lua,oc_lua_len,pdf->page_size.h,pdf->page_size.v);
            if(!oc_explicit) {
                pdf_add_name(pdf,"MediaBox");
                pdf_begin_array(pdf);
                pdf_add_int(pdf,0);
                pdf_add_int(pdf,0);
                pdf_add_bp(pdf,pdf->page_size.h);
                pdf_add_bp(pdf,pdf->page_size.v);
                pdf_end_array(pdf);
            }
            if(oc_tex_len>0) {
                pdf_check_space(pdf);pdf_puts(pdf,oc_tex);pdf_set_space(pdf);
            }
            if(oc_lua_len>0) {
                pdf_check_space(pdf);pdf_out_block(pdf,oc_lua,oc_lua_len);pdf_set_space(pdf);
            }
            lua_pop(Luas,2);
            xfree(oc_tex);
        }'''
assert b.count(old.encode())==1,'native page dictionary emission seam changed'
b=b.replace(old.encode(),new.encode());assert b.count(b'#include "writer_geometry.h"')==1
p.write_bytes(b.replace(b'#include "writer_geometry.h"',b'#include "writer_geometry.h"\n#include "writer_origin.h"'))
# Observe the ACTUAL final root attribute writer, not earlier assignments.
# Generic native token/Lua attribute emissions call the origin wrapper while
# inside this private root scope; no source/PDF scanning or public authority.
root_source=source/'texk/web2c/luatexdir/pdf/pdfpagetree.c'
expected='404c761770ef00112f2520d8b9e4be950d28ecd0adc32dbfea04d5c7a0e7d608'
if sha(root_source)!=expected:raise SystemExit('native root writer preimage drift')
root_out=out/'luatexdir/pdf/pdfpagetree.c';shutil.copyfile(root_source,root_out)
r=root_out.read_bytes()
assert r.count(b'#include "ptexlib.h"')==1
r=r.replace(b'#include "ptexlib.h"',b'#include "ptexlib.h"\n#include "writer_origin.h"')
begin=b'        pages_attributes = pdf_pages_attr;'
end=b'        print_pdf_table_string(pdf, "pagesattributes");\n        pdf_out(pdf, \' \');'
assert r.count(begin)==1 and r.count(end)==1,'native root emission scope drift'
r=r.replace(begin,b'        oc_lua_origin_root_begin();\n'+begin)
r=r.replace(end,end+b'\n        oc_lua_origin_root_end();');root_out.write_bytes(r)
v['upstream'].append({'path':'luatexdir/pdf/pdfpagetree.c','before':expected})
v['originVariant']={'version':1,'capability':'oc_lua_origin_abi()==1','graphicsRequired':1,'parentAssociationSha256':sha(out/'graphics1-association.json'),'recipe':sha(__file__),'nativeProvenance':'Exact native tokenlist_to_cstring/lua_tolstring bytes consumed once at actual final Page writer boundary; accepted explicit bounded MediaBox emitted once instead of duplicate built-in key. Actual native final root /Pages emission brackets consumed TeX/Lua root attributes; explicit origins require exactly one balanced root witness with both channels empty. No-explicit-origin attributes retain parent behavior. Independent native node matrix/units and close/seal remain unchanged.'}
for x in v['upstream']:x['after']=sha(out/x['path'])
a.write_text(json.dumps(v,indent=2)+'\n')
