#!/usr/bin/env python3
"""Observe actual outputs through the pinned current DVI converter and E1.
Does not accept converter geometry refusals as product readiness.
"""
import argparse
import json
from pathlib import Path
import subprocess

HERE=Path(__file__).resolve().parent
p=argparse.ArgumentParser(description=__doc__)
for name in ('runtime','overlay','matrix','renderer','out'):p.add_argument('--'+name,type=Path,required=True)
p.add_argument('--converter',type=Path,help='explicit semantic candidate; omitted retains the old negative control')
a=p.parse_args()
if a.out.exists():raise SystemExit('fresh output required')
a.out.mkdir(parents=True)
matrix=json.loads((a.matrix/'native-matrix.json').read_text())
calls=[]
def run(cmd,label):
    r=subprocess.run(list(map(str,cmd)),stdout=subprocess.PIPE,stderr=subprocess.STDOUT,timeout=180)
    (a.out/(label+'.log')).write_bytes(r.stdout)
    calls.append({'arguments':list(map(str,cmd)),'exitCode':r.returncode})
    (a.out/'observer-calls.json').write_text(json.dumps(calls,indent=2)+'\n')
    print(label,r.returncode,flush=True)
    if r.returncode:raise SystemExit('observer failed: '+label)
for engine in matrix['engines']:
    for case in matrix['cases']:
        for variant in ('control','candidate'):
            stem=case+'-'+engine+'-'+variant
            source=a.matrix/stem/('main.dvi' if engine=='latex' else 'main.pdf')
            if engine=='latex':
                pdf=a.out/(stem+'.pdf')
                cmd=['node',HERE/'convert.cjs' if a.converter else HERE.parent/'font-compatibility/convert.cjs',a.runtime,source,pdf,a.overlay]
                if a.converter:cmd.append(a.converter)
                run(cmd,stem+'-convert')
            else:pdf=source
            run(['node',HERE/'renderer.cjs',a.renderer,pdf,a.out/(stem+'.e1.json')],stem+'-e1')
(a.out/'observations.json').write_text(json.dumps({'schemaVersion':1,'status':'executed',
    'invocations':len(calls),'skips':0,'productRuntimeChanged':False},indent=2)+'\n')
