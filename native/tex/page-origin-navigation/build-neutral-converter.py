#!/usr/bin/env python3
"""Separate typed-native neutral-channel derivative of frozen origin builder."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys

p=argparse.ArgumentParser(description=__doc__)
for name in ['source','cache','out']:
    p.add_argument('--'+name,type=Path,required=True)
p.add_argument('--kind',choices=['dvi','xe'],required=True)
a=p.parse_args();out=a.out.resolve();here=Path(__file__).resolve().parent
assert not out.exists()
parent=here/'build-current-converter.py';text=parent.read_text()
assert hashlib.sha256(parent.read_bytes()).hexdigest()=='94df6a9a75fc0c414be40cb75190d8bb7d39f22fefefa0446756911382e4be1f'
def change(old,new):
    global text
    assert text.count(old)==1,old[:100]
    text=text.replace(old,new)
change('here = Path(__file__).resolve().parent','here = Path('+repr(str(here))+')')
seam='change("str(doc_overlay)], \'geometry-overlay\')", overlay)'
change(seam,'overlay += "\\n    run([\'python3\', " + repr(str(here / \'neutral-converter.py\')) + ", str(out / \'geometry-overlay\'), str(source)], \'neutral-overlay\')"\n'+seam)
change("'dpx-pdfdev.c', 'dpx-pdfparse.c')\")","'dpx-pdfdev.c', 'dpx-pdfparse.c', 'dpx-spc_color.c', 'dpx-pdfcolor.c', 'dpx-spc_dvipdfmx.c')\")")
seam='change("exports = [\'_malloc\', \'_free\']", \'exports = \' + repr(exports))'
change(seam,"exports.append('_oc_dvi_neutral_abi')\n"+seam)
private=out.parent/(out.name+'-neutral-builder.py');assert not private.exists();private.write_text(text)
subprocess.run([sys.executable,str(private),*sys.argv[1:]],check=True,timeout=300)
sha=lambda f:hashlib.sha256(Path(f).read_bytes()).hexdigest()
receipt={'schemaVersion':1,'status':'native-candidate-not-adopted','proposedProfile':15,'kind':a.kind,'neutralAbi':1,'sources':{str(f):sha(f)for f in [Path(__file__),parent,here/'neutral-converter.py',here/'neutral-dictionaries.inc',private]},'association':sha(out/'geometry-overlay/association.json'),'outputs':{n:{'length':(out/n).stat().st_size,'sha256':sha(out/n)}for n in ['oc-dvi.js','oc-dvi.wasm']}}
(out/'neutral-build.json').write_text(json.dumps(receipt,indent=2)+'\n')
print(json.dumps(receipt['outputs']))
