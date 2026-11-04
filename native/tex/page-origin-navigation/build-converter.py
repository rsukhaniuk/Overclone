#!/usr/bin/env python3
"""New origin1+graphics1 converter tuple; accepted recipes/binaries untouched."""
import argparse,hashlib,json,subprocess,sys
from pathlib import Path
p=argparse.ArgumentParser(description=__doc__)
for n in ['source','out','cache']:p.add_argument('--'+n,type=Path,required=True)
p.add_argument('--kind',choices=['dvi','xe'],required=True)
a=p.parse_args();out=a.out.resolve();source=a.source.resolve();cache=a.cache.resolve();here=Path(__file__).resolve().parent;parent=here.parent/'dvi-engine'
if out.exists():raise SystemExit('fresh origin output required')
for root in [source,cache,Path('/work'),Path('/inputs'),Path('/control/profile9-runtime')]:
 if out==root or root in out.parents:raise SystemExit('protected output overlap')
def sha(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
recipe=parent/'build.py';before=sha(recipe)
if before!='8aaffbf0d127a3555415478a314582d43eb21b0db9498c2e9e2a117619c6fa00':raise SystemExit('accepted recipe preimage changed')
s=recipe.read_text()
def change(x,y):
 global s
 if s.count(x)!=1:raise SystemExit('origin recipe seam mismatch: '+x[:80])
 s=s.replace(x,y)
change('owned = Path(__file__).resolve().parent','owned = Path('+repr(str(parent))+')')
change("str(doc_overlay)], 'geometry-overlay')","str(doc_overlay), '--graphics'], 'geometry-overlay')\n    run(['python3', "+repr(str(here/'converter-origin.py'))+", str(out / 'geometry-overlay'), str(source)], 'origin-overlay')")
change("('dpx-dvi.c', 'dpx-pdfdoc.c', 'dpx-specials.c', 'dpx-spc_pdfm.c')","('dpx-dvi.c', 'dpx-pdfdoc.c', 'dpx-specials.c', 'dpx-spc_pdfm.c', 'dpx-pdfdev.c', 'dpx-pdfparse.c')")
change("cpp = owned / 'oc_dvi.cpp'","cpp = out / 'geometry-overlay/oc_dvi_origin.cpp'")
change("['emcc', '-O2', *geometry_flags","['emcc', '-O2', '-MMD', *geometry_flags")
change("['em++', '-std=c++17', '-O2', *geometry_flags","['em++', '-std=c++17', '-O2', '-MMD', *geometry_flags")
change("exports = ['_malloc', '_free']","exports = ['_malloc', '_free', '_oc_dvi_graphics_abi', '_oc_dvi_origin_abi']")
out.parent.mkdir(parents=True,exist_ok=True);private=out.parent/(out.name+'-recipe.py')
if private.exists():raise SystemExit('fresh private recipe required')
private.write_text(s);cmd=[sys.executable,str(private),'--source',str(source),'--out',str(out),'--cache',str(cache),'--xdv-geometry'if a.kind=='xe'else'--geometry']
subprocess.run(cmd,check=True,timeout=300)
(out/'origin-build.json').write_text(json.dumps({'schemaVersion':1,'candidate':'explicit-origin1-graphics1-not-adopted','baseAbi':3 if a.kind=='xe'else 2,'capabilities':{'graphics':1,'origin':1},'parentRecipeSha256':before,'privateRecipeSha256':sha(private),'recipeSha256':sha(__file__),'argv':cmd,'outputs':{n:{'length':(out/n).stat().st_size,'sha256':sha(out/n)}for n in ['oc-dvi.js','oc-dvi.wasm']}},indent=2)+'\n')
