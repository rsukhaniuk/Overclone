#!/usr/bin/env python3
"""Separate graphics1 candidate using an exact private overlay of old recipe.
Never edits the accepted default recipe/wrapper, source/cache or old candidates.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys
p=argparse.ArgumentParser(description=__doc__)
p.add_argument('--source',type=Path,required=True)
p.add_argument('--out',type=Path,required=True)
p.add_argument('--cache',type=Path,required=True)
p.add_argument('--kind',choices=['dvi','xe'],required=True)
a=p.parse_args();out=a.out.resolve();source=a.source.resolve();cache=a.cache.resolve();here=Path(__file__).resolve().parent;parent=here.parent/'dvi-engine'
if out.exists():raise SystemExit('fresh graphics output required')
for root in [source,cache,Path('/work'),Path('/inputs'),Path('/control/profile9-runtime'),Path('/control/r2-xe-maps/inputs')]:
 if out==root or root in out.parents:raise SystemExit('protected output overlap')
recipe=parent/'build.py';raw=recipe.read_bytes();before=hashlib.sha256(raw).hexdigest()
if before!='8aaffbf0d127a3555415478a314582d43eb21b0db9498c2e9e2a117619c6fa00':raise SystemExit('accepted converter recipe preimage changed')
s=raw.decode()
def change(old,new):
 global s
 if s.count(old)!=1:raise SystemExit('private recipe seam mismatch: '+old[:60])
 s=s.replace(old,new)
change('owned = Path(__file__).resolve().parent','owned = Path('+repr(str(parent))+')')
change("str(doc_overlay)], 'geometry-overlay')","str(doc_overlay), '--graphics'], 'geometry-overlay')")
change("('dpx-dvi.c', 'dpx-pdfdoc.c', 'dpx-specials.c', 'dpx-spc_pdfm.c')","('dpx-dvi.c', 'dpx-pdfdoc.c', 'dpx-specials.c', 'dpx-spc_pdfm.c', 'dpx-pdfdev.c')")
change("cpp = owned / 'oc_dvi.cpp'","cpp = out / 'geometry-overlay/oc_dvi_graphics.cpp'")
change("exports = ['_malloc', '_free']", "exports = ['_malloc', '_free', '_oc_dvi_graphics_abi']")
# Keep invocation recipe beside, not inside, the fresh output checked by parent.
out.parent.mkdir(parents=True,exist_ok=True);private=out.parent/(out.name+'-recipe.py');assert not private.exists();private.write_text(s)
cmd=[sys.executable,str(private),'--source',str(source),'--out',str(out),'--cache',str(cache),'--xdv-geometry'if a.kind=='xe'else'--geometry']
subprocess.run(cmd,check=True,timeout=300)
(out/'graphics-build.json').write_text(json.dumps({'schemaVersion':1,'candidate':'explicit-native-graphics1-not-adopted','baseAbi':3 if a.kind=='xe' else 2,'capability':'oc_dvi_graphics_abi()==1','parentRecipeSha256':before,'privateRecipeSha256':hashlib.sha256(private.read_bytes()).hexdigest(),'recipeSha256':hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),'argv':cmd},indent=2)+'\n')
