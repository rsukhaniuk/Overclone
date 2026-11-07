#!/usr/bin/env python3
"""Build a private graphics1+semantic candidate; accepted recipes stay read-only."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys
here=Path(__file__).resolve().parent
p=argparse.ArgumentParser(description=__doc__)
for name in ('source','out','cache'):p.add_argument('--'+name,type=Path,required=True)
a=p.parse_args();out=a.out.resolve();source=a.source.resolve();cache=a.cache.resolve()
if out.exists():raise SystemExit('fresh output required')
for root in (source,cache,here,Path('/work'),Path('/inputs'),Path('/control')):
    if out==root or root in out.parents:raise SystemExit('protected output overlap')
parent=here.parent/'dvi-engine';raw=(parent/'build.py').read_bytes()
if hashlib.sha256(raw).hexdigest()!='8aaffbf0d127a3555415478a314582d43eb21b0db9498c2e9e2a117619c6fa00':raise SystemExit('accepted recipe drift')
s=raw.decode()
def change(before,after):
    global s
    if s.count(before)!=1:raise SystemExit('recipe seam mismatch: '+before[:70])
    s=s.replace(before,after)
change('owned = Path(__file__).resolve().parent','owned = Path('+repr(str(parent))+')')
change("str(doc_overlay)], 'geometry-overlay')", "str(doc_overlay), '--graphics'], 'geometry-overlay')\n    run(['python3',"+repr(str(here/'converter-overlay.py'))+",str(out/'geometry-overlay')], 'font-semantics-overlay')")
change("('dpx-dvi.c', 'dpx-pdfdoc.c', 'dpx-specials.c', 'dpx-spc_pdfm.c')", "('dpx-dvi.c', 'dpx-pdfdoc.c', 'dpx-specials.c', 'dpx-spc_pdfm.c', 'dpx-pdfdev.c')")
change("cpp = owned / 'oc_dvi.cpp'", "cpp = out / 'geometry-overlay/oc_dvi_graphics.cpp'")
change("exports = ['_malloc', '_free']", "exports = ['_malloc', '_free', '_oc_dvi_graphics_abi', '_oc_dvi_font_semantics_abi', '_oc_dvi_font_semantics_status', '_oc_dvi_font_semantics_spans']")
out.parent.mkdir(parents=True,exist_ok=True)
recipe=out.parent/(out.name+'-recipe.py')
if recipe.exists():raise SystemExit('fresh recipe required')
recipe.write_text(s)
cmd=[sys.executable,str(recipe),'--source',str(source),'--out',str(out),'--cache',str(cache),'--geometry']
subprocess.run(cmd,check=True,timeout=300)
(out/'font-semantics-build.json').write_text(json.dumps({'schemaVersion':1,'candidate':'unadopted graphics1+font-semantics1',
    'parentRecipe':hashlib.sha256(raw).hexdigest(),'privateRecipe':hashlib.sha256(recipe.read_bytes()).hexdigest(),
    'recipe':hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),'arguments':cmd},indent=2)+'\n')
