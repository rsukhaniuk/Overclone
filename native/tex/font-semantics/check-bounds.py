#!/usr/bin/env python3
"""ASan/UBSan helper-bound tests using exact generated native headers, in Docker."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
here=Path(__file__).resolve().parent
p=argparse.ArgumentParser(description=__doc__)
p.add_argument('--prepared',type=Path,required=True);p.add_argument('--out',type=Path,required=True)
a=p.parse_args()
if a.out.exists():raise SystemExit('fresh output required')
a.out.mkdir(parents=True)
includes=[a.prepared,a.prepared/'tex',a.prepared/'pdftexdir']
command=['cc','-O1','-g','-fsanitize=address,undefined','-fno-omit-frame-pointer',
         *['-I'+str(p) for p in includes],str(here/'bounds-test.c'),str(a.prepared/'oc_font_semantics.c'),'-o',str(a.out/'bounds-test')]
commands=[command]
for i,cmd in enumerate([command,*[[str(a.out/'bounds-test'),str(k)] for k in range(8)]]):
    r=subprocess.run(cmd,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,timeout=60)
    (a.out/(str(i)+'.log')).write_bytes(r.stdout)
    if r.returncode:raise SystemExit('sanitizer bound check failed: '+str(i))
    if i:commands.append(cmd)
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
(a.out/'receipt.json').write_text(json.dumps({'schemaVersion':1,'status':'pass','unitCases':8,'skips':0,
    'sanitizers':['address','undefined'],'commands':commands,'source':sha(a.prepared/'oc_font_semantics.c'),
    'actualGeneratedHeader':sha(a.prepared/'tex/pdftexd.h'),
    'scope':'Synthetic helper bounds only; genuine engine behavior has separate actual native receipts.'},indent=2)+'\n')
print('8 helper bound cases pass under ASan/UBSan; no compiler behavior inferred.')
