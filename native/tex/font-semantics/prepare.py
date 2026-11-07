#!/usr/bin/env python3
"""Verify a complete prepared producer, flatten actual changes, overlay WEB,
then genuinely TANGLE/Web2C. Writes only a fresh private --out tree.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
from overlay import apply

HERE = Path(__file__).resolve().parent
p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--baseline', type=Path, required=True, help='prepared root containing on/ and candidate-hashes.json')
p.add_argument('--generators', type=Path, required=True)
p.add_argument('--out', type=Path, required=True)
a = p.parse_args()
base, tools, out = a.baseline.resolve(), a.generators.resolve(), a.out.resolve()
if out.exists(): raise SystemExit('fresh output required')
for root in (base, tools, HERE, Path('/work'), Path('/inputs'), Path('/control')):
    if out == root or root in out.parents: raise SystemExit('protected source overlap')
sha = lambda f: hashlib.sha256(f.read_bytes()).hexdigest()
pins = json.loads((base/'candidate-hashes.json').read_text())['on']
for name, expected in pins.items():
    if sha(base/'on'/name) != expected: raise SystemExit('baseline source mismatch: '+name)
shutil.copytree(base/'on', out)
shutil.rmtree(out/'regenerate')
gen = out/'regenerate'; gen.mkdir()
logs = out/'semantics-logs'; logs.mkdir()
commands = []
def run(args, label, data=None):
    cmd = list(map(str,args)); commands.append(cmd)
    r = subprocess.run(cmd,cwd=gen,input=data,stdout=subprocess.PIPE,stderr=subprocess.PIPE,
                       env=dict(os.environ,WEBINPUTS='.:',LC_ALL='C'),timeout=120)
    (logs/(label+'.stdout')).write_bytes(r.stdout)
    (logs/(label+'.stderr')).write_bytes(r.stderr)
    if r.returncode: raise SystemExit(label+' failed; see private log')
    return r.stdout
run([tools/'tie','-m','preimage.web',base/'on/pdftexdir/pdftex.web',
     base/'on/regenerate/pdftex-final.ch'],'tie')
preimage = (gen/'preimage.web').read_text()
web, edits = apply(preimage)
(gen/'pdftex.web').write_text(web)
(gen/'empty.ch').write_text('')
run([tools/'tangle','pdftex.web','empty.ch'],'tangle')
definitions = [base/'helpers/web2c/common.defines',base/'helpers/web2c/texmf.defines',
               base/'helpers/synctexdir/synctex.defines',out/'pdftexdir/pdftex.defines',
               HERE.parent/'synctex-producer/producer.defines',HERE/'semantics.defines']
data = b''.join(f.read_bytes() for f in definitions)+(gen/'pdftex.p').read_bytes()
data = run([tools/'web2c/web2c','-htexmfmp.h','-t','-cpdftexcoerce'],'web2c',data)
data = run([tools/'web2c/fixwrites','-t','pdftex'],'fixwrites',data)
run([tools/'web2c/splitup','-i','-l','65000','pdftex'],'splitup',data)
coerce = gen/'pdftexcoerce.h'
coerce.write_bytes(coerce.read_bytes()+(base/'helpers/web2c/coerce.h').read_bytes())
(gen/'pdftex-pool.c').write_bytes(run([tools/'web2c/makecpool','pdftex'],'makecpool'))
for n in ('pdftex0.c','pdftexini.c','pdftexd.h','pdftexcoerce.h','pdftex-pool.c'):
    shutil.copyfile(gen/n,out/'tex'/n)
for n in ('oc_font_semantics.c','oc_font_semantics.h'):
    shutil.copyfile(HERE/n,out/n)
header = out/'texmfmp.h'
original = header.read_text()
assert original.count('#include "xmemory.h"') == 1
header.write_text(original.replace('#include "xmemory.h"','#include "xmemory.h"\n#include "oc_font_semantics.h"'))
receipt = {'schemaVersion':1,'baselineManifest':sha(base/'candidate-hashes.json'),
           'baselineFiles':pins,'flattenedWeb':hashlib.sha256(preimage.encode()).hexdigest(),
           'edits':edits,'commands':commands,
           'generators':{str(f.relative_to(tools)):sha(f) for f in [tools/'tie',tools/'tangle',
               *[tools/'web2c'/n for n in ('web2c','fixwrites','splitup','makecpool')]]},
           'sources':{str(f.relative_to(out)):sha(f) for f in sorted(out.rglob('*')) if f.is_file()}}
(out/'semantics-preparation.json').write_text(json.dumps(receipt,indent=2)+'\n')
print(json.dumps({'prepared':str(out),'edits':len(edits),'generatedFromWeb':True}))
