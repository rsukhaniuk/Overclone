#!/usr/bin/env python3
"""Build only the genuine approved native OTANGLE helper, no make/acquisition."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
p=argparse.ArgumentParser(description=__doc__)
p.add_argument('--texlive',type=Path,required=True)
p.add_argument('--generators',type=Path,required=True)
p.add_argument('--out',type=Path,required=True)
a=p.parse_args();tl,g,out=[x.resolve() for x in [a.texlive,a.generators,a.out]]
if out.exists():raise SystemExit('fresh output required')
for root in [tl,g,Path('/work'),Path('/inputs'),Path('/control/r2-xe-maps/inputs')]:
 if out==root or root in out.parents:raise SystemExit('protected output overlap')
out.mkdir(parents=True);rows=[];env=dict(os.environ,WEBINPUTS='.:',LC_ALL='C')
def sha(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
def run(n,argv,data=None):
 r=subprocess.run(list(map(str,argv)),cwd=out,env=env,input=data,stdout=subprocess.PIPE,stderr=subprocess.PIPE,timeout=60)
 (out/(n+'.stdout')).write_bytes(r.stdout);(out/(n+'.stderr')).write_bytes(r.stderr)
 rows.append({'argv':list(map(str,argv)),'exit':r.returncode});(out/'commands.json').write_text(json.dumps(rows,indent=2)+'\n')
 if r.returncode:raise SystemExit('OTANGLE bootstrap failed: '+n)
 return r.stdout
run('tangle',[g/'tangle',tl/'omegaware/otangle.web',tl/'omegaware/otangle.ch'])
c=run('web2c',[g/'web2c/web2c','-hcpascal.h','-cotangle'],(tl/'web2c/common.defines').read_bytes()+(out/'otangle.p').read_bytes())
(out/'otangle.c').write_bytes(run('fixwrites',[g/'web2c/fixwrites','otangle'],c))
run('compile',['gcc','-O2','-I'+str(out),'-I'+str(g),'-I'+str(tl),'-I'+str(g/'w2c'),'-I'+str(g.parent),'-I'+str(tl.parent),out/'otangle.c',g/'lib/lib.a',g.parent/'kpathsea/.libs/libkpathsea.a','-lm','-o',out/'otangle'])
inputs=[tl/'omegaware/otangle.web',tl/'omegaware/otangle.ch',tl/'web2c/common.defines',g/'tangle',g/'web2c/web2c',g/'web2c/fixwrites',g/'lib/lib.a',g.parent/'kpathsea/.libs/libkpathsea.a']
(out/'association.json').write_text(json.dumps({'schemaVersion':1,'status':'pass','inputs':{str(q):sha(q) for q in inputs},'outputs':{n:sha(out/n) for n in ['otangle','otangle.c','otangle.h','otangle.p']},'recipeSha256':sha(__file__)},indent=2)+'\n')
