#!/usr/bin/env python3
"""Finite actual native matrix. Run inside the cached network-disabled image.
This is native evidence only; the caller separately performs browser admission.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess

HERE=Path(__file__).resolve().parent
REPO=HERE.parents[2]
p=argparse.ArgumentParser(description=__doc__)
for name in ('runtime','component','overlay','out'):p.add_argument('--'+name,type=Path,required=True)
p.add_argument('--faces',action='store_true')
a=p.parse_args()
if a.out.exists():raise SystemExit('fresh output required')
a.out.mkdir(parents=True)
calls=[]
def run(operation,fixture,fmt,out):
    cmd=['node',str(HERE/'probe.cjs'),str(a.runtime),str(a.component),operation,str(fixture),str(fmt),str(out),str(a.overlay)]
    result=subprocess.run(cmd,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,timeout=180)
    (a.out/(out.name+'.driver.log')).write_bytes(result.stdout)
    calls.append({'arguments':cmd,'exitCode':result.returncode})
    (a.out/'native-calls.json').write_text(json.dumps(calls,indent=2)+'\n')
    print(result.stdout.decode(errors='replace'),flush=True)
    if result.returncode:raise SystemExit('native check failed: '+out.name)

cases={'mixed':REPO/'tests/PackageFontFixtures/fonts/mixed-semantic.tex',
       'ligature':REPO/'tests/PackageFontFixtures/fonts/mixed-ligature.tex',
       'lifetime':HERE/'fixtures/lifetime.tex','hyphenation':HERE/'fixtures/hyphenation.tex',
       'meaning':HERE/'fixtures/primitive-meaning.tex'}
if a.faces:cases['faces']=REPO/'tests/PackageFontFixtures/fonts/declared-faces.tex'
for engine,ini in [('pdftex',HERE/'pdftex.ini'),('latex',HERE.parent/'latex-dvi/latex-dvi.ini')]:
    for variant in ('control','candidate'):
        run('format-control' if variant=='control' else 'format',ini,'-',a.out/(engine+'-format-'+variant))
    for case,fixture in cases.items():
        for variant in ('control','candidate'):
            run('compile',fixture,a.out/(engine+'-format-'+variant)/'pdflatex.fmt',a.out/(case+'-'+engine+'-'+variant))
        control=a.out/(case+'-'+engine+'-control')/'native.synctex'
        candidate=a.out/(case+'-'+engine+'-candidate')/'native.synctex'
        assert control.read_bytes()==candidate.read_bytes(),case+' native source-map position drift'
    run('compile-reject',HERE/'fixtures/forged.tex',a.out/(engine+'-format-candidate')/'pdflatex.fmt',a.out/('forged-'+engine))
run('format-reject',HERE/'fixtures/dump-tagged.ini','-',a.out/'dump-tagged')
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
(a.out/'native-matrix.json').write_text(json.dumps({'schemaVersion':1,'status':'pass',
    'nativeInvocations':len(calls),'skips':0,'cases':list(cases),'engines':['pdftex','latex'],
    'componentManifestSha256':sha(a.component/'manifest.json'),
    'sourceFiles':{str(p.relative_to(HERE)):sha(p) for p in sorted(HERE.rglob('*')) if p.is_file()},
    'sourceMapsByteIdentical':True,'productRuntimeChanged':False,'browserAcceptance':False},indent=2)+'\n')
