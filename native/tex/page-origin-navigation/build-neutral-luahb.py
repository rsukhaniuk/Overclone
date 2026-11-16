#!/usr/bin/env python3
"""Typed neutral native-channel derivative of the frozen current-origin LuaHB."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shlex
import shutil
import subprocess

p=argparse.ArgumentParser(description=__doc__)
for name in ['parent','source','graphics-build','cache','work','out']:
    p.add_argument('--'+name,type=Path,required=True)
a=p.parse_args();work=a.work.resolve();out=a.out.resolve();assert not work.exists()and not out.exists();work.mkdir(parents=True);out.mkdir(parents=True)
here=Path(__file__).resolve().parent;lua=here.parent/'luatex-engine/synctex';sha=lambda f:hashlib.sha256(Path(f).read_bytes()).hexdigest();pin=lambda f:{'length':Path(f).stat().st_size,'sha256':sha(f)}
parent=json.loads((a.parent/'origin-luahb-build.json').read_text());expected={'luatex.js':{'length':193626,'sha256':'98f6efae0509df0b3c5b739c3c60037b514df76eb07b43561ebb124d617c2c22'},'luatex.wasm':{'length':6017681,'sha256':'54155196638dc8da2b8c5732d3f1c78da6e7147537d96d5fd1cc19725370af4b'}};assert parent['outputs']==expected
for name,wanted in expected.items():assert pin(a.parent/name)==wanted
link=parent['commands'][-1]['argv'];linked=[Path(x)for x in link if x.endswith(('.o','.a'))];inputs={str(f):pin(f)for f in linked};commands=[];env=dict(os.environ,EM_CACHE=str(a.cache.resolve()),EM_FROZEN_CACHE='1',EMCC_CORES='1')
def run(label,cmd):
    cmd=list(map(str,cmd))
    with(work/(label+'.log')).open('wb')as log:r=subprocess.run(cmd,env=env,stdout=log,stderr=subprocess.STDOUT,timeout=180)
    commands.append({'label':label,'argv':cmd,'exit':r.returncode});(work/'commands.json').write_text(json.dumps(commands,indent=2)+'\n');assert not r.returncode,label
baseline=work/'baseline';baseline.mkdir();run('exact-parent-relink',link[:-1]+[baseline/'luatex.js'])
for name,wanted in expected.items():assert pin(baseline/name)==wanted
archive=next(f for f in linked if f.name=='browser-luatex.a');newarchive=work/'browser-luatex.a';shutil.copyfile(archive,newarchive)
origin_source=Path(next(c['argv'][c['argv'].index('-c')+1]for c in parent['commands']if c['label']=='pdfgen')).parent
base_commands=json.loads((a.graphics_build/'commands.json').read_text())
sources={}
for name in ['pdfgen','pdfliteral','pdfcolorstack']:
    original=origin_source/(name+'.c')if name!='pdfcolorstack'else a.source/'texk/web2c/luatexdir/pdf/pdfcolorstack.c'
    if name=='pdfcolorstack':assert sha(original)=='67e2e10dd2a90c664bdc22530f8bdffa20bb7811550e6fa2ff346cc0e69eba29'
    text=original.read_bytes();sources[str(original)]=pin(original)
    def change(before,after):
        global text
        x,y=before.encode(),after.encode();assert text.count(x)==1,(name,before[:80]);text=text.replace(x,y)
    change('#include "ptexlib.h"','#include "ptexlib.h"\n#include "lua-neutral.h"')
    if name=='pdfgen':
        change('else oc_lua_geometry_unsafe(); /* arbitrary dictionary/program injection */','else if(!oc_lua_neutral_tokens(s,(size_t)len)) oc_lua_geometry_unsafe(); /* actual scoped native writer channel only */')
        for call,role in [('pdf_print_toks(pdf, pdf_annot_data(j));',2),('pdf_print_toks(pdf, pdf_link_attr(j));',1),('pdf_print_toks(pdf, pdf_catalog_toks);',3)]:
            change(call,'{ oc_lua_neutral_tokens_begin('+str(role)+'); '+call+' oc_lua_neutral_tokens_end(); }')
        change('write_action(pdf, pdf_link_action(j));','{ int oc_user=pdf_action_type(pdf_link_action(j))==pdf_action_user; if(oc_user)oc_lua_neutral_tokens_begin(2); write_action(pdf, pdf_link_action(j)); if(oc_user)oc_lua_neutral_tokens_end(); }')
    elif name=='pdfliteral':
        change('    if (!oc_lua_graphics_literal_consume()) oc_lua_geometry_unsafe(); /* arbitrary literals remain unsafe */','    int oc_color=oc_lua_neutral_color_pending();\n    if (!oc_lua_graphics_literal_consume()&&!oc_color) oc_lua_geometry_unsafe(); /* arbitrary literals remain unsafe */')
        change('        pdf_out_block(pdf, (const char *) (ss + j), l);','        if(oc_color&&!oc_lua_neutral_color_program((const char *)(ss+j),l,literal_mode==direct_always||literal_mode==direct_page))oc_lua_geometry_unsafe();\n        pdf_out_block(pdf, (const char *) (ss + j), l);')
    else:
        change('#include "lua-neutral.h"','#include "lua-neutral.h"\n#include "writer_geometry.h"')
        change('        case colorstack_pop:\n            literal_mode = colorstackpop(stack_no);\n            break;', '        case colorstack_pop: {\n            colstack_type *oc_stack=get_colstack(stack_no);\n            if(global_shipping_mode!=SHIPPING_PAGE||oc_stack->page_used<=0)oc_lua_geometry_unsafe();\n            literal_mode = colorstackpop(stack_no);\n            break;\n        }')
        seam=b'pdf_literal(pdf, s, literal_mode, false);';assert text.count(seam)==3
        text=text.replace(seam,b'{ oc_lua_neutral_color_begin(); pdf_literal(pdf, s, literal_mode, false); oc_lua_neutral_color_end(); }')
    src=work/(name+'.c');src.write_bytes(text)
    template_name='pdfgen'if name=='pdfcolorstack'else name
    cmd=next(c['argv']for c in base_commands if c['argv'][0]=='emcc'and any(x.endswith('/'+template_name+'.c')for x in c['argv']));oldsrc=cmd[cmd.index('-c')+1];oldobj=cmd[cmd.index('-o')+1];obj=work/('libluatex_a-'+name+'.o');cmd=[str(src)if x==oldsrc else str(obj)if x==oldobj else x for x in cmd];cmd+=['-I'+str(here),'-MD','-MF',str(work/(name+'.d')),'-ffile-prefix-map='+str(work)+'=/inputs/lua-neutral-build'];run(name,cmd);run(name+'-archive',['emar','r',newarchive,obj])
def members(file):
    raw=file.read_bytes();assert raw.startswith(b'!<arch>\n');at=8;names=b'';result=[]
    while at<len(raw):
        h=raw[at:at+60];assert len(h)==60 and h[58:]==b'`\n';n=int(h[48:58]);name=h[:16].decode().strip();data=raw[at+60:at+60+n];assert len(data)==n;at+=60+n+n%2
        if name=='//':names=data;continue
        if name in ['/','/SYM64/']:continue
        if name.startswith('/')and name[1:].isdigit():
            offset=int(name[1:]);name=names[offset:names.index(b'/\n',offset)].decode()
        else:name=name.removesuffix('/')
        result.append({'name':name,'sha256':hashlib.sha256(data).hexdigest()})
    return result
before,after=members(archive),members(newarchive);assert [r['name']for r in before]==[r['name']for r in after]
changed=sorted(x['name']for x,y in zip(before,after)if x!=y)
assert changed==['libluatex_a-pdfcolorstack.o','libluatex_a-pdfgen.o','libluatex_a-pdfliteral.o'],changed
obj=work/'lua-neutral.o';run('validator',['em++','-std=c++17','-O1','-Wall','-Wextra','-Werror','-MMD','-I'+str(lua),'-c',here/'lua-neutral.cpp','-o',obj])
final=[str(newarchive)if x==str(archive)else str(out/'luatex.js')if x==link[-1]else x for x in link];final.insert(2,str(obj));exports=next(x for x in final if x.startswith('-sEXPORTED_FUNCTIONS='));names=json.loads(exports.split('=',1)[1]);names.append('_oc_lua_neutral_abi');final[final.index(exports)]='-sEXPORTED_FUNCTIONS='+json.dumps(names);run('link',final);assert inputs=={str(f):pin(f)for f in linked}
headers={}
for dep in work.glob('*.d'):
    for f in shlex.split(dep.read_text().replace('\\\n','').split(':',1)[1]):headers[f]=pin(f)
receipt={'schemaVersion':1,'status':'native-candidate-not-adopted','proposedProfile':15,'neutralAbi':1,'parent':pin(a.parent/'origin-luahb-build.json'),'retainedLinkedInputs':inputs,'commands':commands,'sources':{**sources,**{str(f):pin(f)for f in [Path(__file__),here/'lua-neutral.cpp',here/'lua-neutral.h']}},'headers':headers,'changedArchiveMembers':changed,'archiveMembersBefore':before,'archiveMembersAfter':after,'outputs':{name:pin(out/name)for name in ['luatex.js','luatex.wasm']}}
(out/'neutral-luahb-build.json').write_text(json.dumps(receipt,indent=2)+'\n');print(json.dumps(receipt['outputs']))
