#!/usr/bin/env python3
"""New origin1+graphics1 tuple using a hash-pinned private parent recipe."""
import argparse,hashlib,json,subprocess,sys
from pathlib import Path
p=argparse.ArgumentParser(description=__doc__)
for n in ['work','out','cache']:p.add_argument('--'+n,type=Path,required=True)
a=p.parse_args();here=Path(__file__).resolve().parent;out=a.out.resolve();work=a.work.resolve()
def sha(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
parent=here/'build-graphics.py'
if sha(parent)!='25a09880776650067e71816326dc563565d3918b3de37cad3b382d9baeb349ad':raise SystemExit('accepted graphics builder drift')
if sha(here/'writer_geometry.cpp')!='b9d848e7d25ef4ae38a35b13411cf95c4b2d67aae51fc7188f8218f18d6fd1e7':raise SystemExit('accepted geometry source drift')
s=parent.read_text()
def change(x,y):
 global s
 if s.count(x)!=1:raise SystemExit('origin builder seam mismatch: '+x[:70])
 s=s.replace(x,y)
change('here=Path(__file__).resolve().parent','here=Path('+repr(str(here))+')')
change("here/'prepare-graphics.py'","here/'prepare-origin.py'")
change("'-DOC_LUA_GRAPHICS=1'","'-DOC_LUA_GRAPHICS=1','-DOC_LUA_ORIGIN=1'")
change("here/'writer_geometry.cpp'","here/'writer_origin.cpp'")
change("flags=['-O1'","flags=['-MMD','-O1'")
change("'pdfobj','lpdflib'","'pdfobj','pdfpagetree','lpdflib'")
s=s.replace("'em++','-std=c++17','-O1'","'em++','-std=c++17','-MMD','-O1'")
change("'oc_lua_graphics_abi'","'oc_lua_graphics_abi','oc_lua_origin_abi'")
s=s.replace("b/'writer_geometry.o'","b/'writer_origin.o'")
private=work.parent/(work.name+'-recipe.py');private.parent.mkdir(parents=True,exist_ok=True)
if private.exists():raise SystemExit('fresh private origin recipe required')
private.write_text(s);cmd=[sys.executable,str(private),'--work',str(work),'--out',str(out),'--cache',str(a.cache.resolve())]
subprocess.run(cmd,check=True,timeout=300)
(out/'origin-build.json').write_text(json.dumps({'schemaVersion':1,'candidate':'explicit-Lua-origin1-graphics1-not-adopted','baseAbi':5,'capabilities':{'origin':1,'graphics':1},'parentRecipeSha256':sha(parent),'privateRecipeSha256':sha(private),'recipeSha256':sha(__file__),'argv':cmd,'outputs':{n:{'length':(out/n).stat().st_size,'sha256':sha(out/n)}for n in ['luatex.js','luatex.wasm']}},indent=2)+'\n')
