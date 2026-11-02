#!/usr/bin/env python3
"""Explicit ABI3 XDV7 measured-state variant; accepted ABI2 script is read-only."""
import hashlib
import json
from pathlib import Path
import subprocess
import sys
source,out,base_doc=map(Path,sys.argv[1:4]);shared=Path(__file__).resolve().parent.parent/'synctex-producer/dvi-converter-overlay.py'
subprocess.run(['python3',str(shared),str(source),str(out),str(base_doc)],check=True,timeout=60)
p=out/'dpx-dvi.c';b=p.read_text();old='post_id_byte==2 && dvi_info.unit_num==25400000 && dvi_info.unit_den==473628672 ? 2 : 0'
if b.count(old)!=1:raise SystemExit('XDV unit hook preimage mismatch')
p.write_text(b.replace(old,'post_id_byte==7 && dvi_info.unit_num==25400000 && dvi_info.unit_den==473628672 ? 7 : 0'))
def sha(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
a=out/'association.json'
try:v=json.loads(a.read_text())
except (OSError,ValueError) as error:raise SystemExit('invalid parent native hook association') from error
v['abi']=3;v['geometrySource']='Actual native XDV7 postamble units/magnification, writer BOP matrix and resolved native extent; pinned Xe query-domain composition'
v['sharedOverlayRecipeSha256']=sha(shared);v['xdvOverlayRecipeSha256']=sha(__file__)
for x in v['source']:x['after']=sha(out/x['path'])
a.write_text(json.dumps(v,indent=2)+'\n')
