#!/usr/bin/env python3
"""Actual current-lineage four-engine package/biblatex native navigation matrix.

Six real phases per case retain unchanged project and generated controls; this
finite native QA pipeline does not replace the application's final-pass policy.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess

p=argparse.ArgumentParser(description=__doc__)
for name in ['runtime','lua','dvi','xe','query','pdf-runtime','results']:
    p.add_argument('--'+name,type=Path,required=True)
a=p.parse_args();out=a.results.resolve();assert not out.exists();out.mkdir(parents=True);here=Path(__file__).resolve().parent;clock=here.parent/'synctex-fixtures/dvi/fixed-clock.cjs';repo=here.parents[2];commands=[];cases=[]
env=dict(os.environ,RUNTIME=str(a.runtime.resolve()),LUA_CANDIDATE=str(a.lua.resolve()),REQUIRE_ORIGIN='1',TZ='UTC')
def run(label,cmd,extra=None):
    cmd=list(map(str,cmd));r=subprocess.run(cmd,env=dict(env,**(extra or {})),capture_output=True,text=True,timeout=120)
    (out/(label+'.driver.log')).write_text(r.stdout+r.stderr);commands.append({'label':label,'argv':cmd,'exit':r.returncode});(out/'commands.json').write_text(json.dumps(commands,indent=2)+'\n')
    assert not r.returncode,label+': see retained driver log'
    print(label+': pass',flush=True)
for fixture in ['packages','biblatex']:
    for kind in ['pdf','dvi','xe','lua']:
        name=kind+'-'+fixture;previous=None
        for ordinal,operation in enumerate(['latex','bibtex','latex','bibtex','latex','latex'],1):
            folder=out/(name+'-'+str(ordinal)+'-'+operation)
            cmd=['node','-r',clock,here/'run-current-project.cjs',kind,operation,folder]+([previous]if previous else[])
            run(name+'-'+str(ordinal),cmd,{'CORPUS_CASE':fixture,'PASS_TAG':str(4000+ordinal)})
            previous=folder
        producer=previous
        if kind in ['dvi','xe']:
            folder=out/(name+'-convert');run(name+'-convert',['node','-r',clock,here/'run-current-project.cjs',kind,'convert',folder,producer],{'CORPUS_CASE':fixture,'CONVERTER':str(getattr(a,kind).resolve()),'PASS_TAG':'4007'});previous=folder
        directory='PackageFontFixtures'if fixture=='packages'else'CompilerBibliographyFixtures'
        cases.append({'name':name,'kind':kind,'fixture':fixture,'folder':str(previous),'map':str(producer/'raw.synctex'),'source':str(repo/'tests'/directory/('main-unicode.tex'if kind in ['lua','xe']else'main-latin.tex'))})
manifest=out/'cases.json';manifest.write_text(json.dumps(cases,indent=2)+'\n')
run('independent-E1-native-query',['node',here/'check-corpus-navigation.cjs',a.pdf_runtime.resolve(),a.query.resolve(),manifest,out/'oracle'])
oracle=json.loads((out/'oracle/receipt.json').read_text());assert len(oracle['rows'])==8
for row in oracle['rows']:
    assert len(row['observations'])==2
    assert all(o['inverse']and o['queryPoint']and o['status']in ['legacy-zero-origin-pdf','native-writer-geometry']for o in row['observations']),row['name']
sha=lambda f:hashlib.sha256(f.read_bytes()).hexdigest()
receipt={'schemaVersion':1,'status':'pass','engineCases':8,'nativePipelinePhases':52,'exactPdfGlyphInversePoints':16,'skips':0,'scope':'actual unchanged package/biblatex sources, genuine native BibTeX/BLX/carry phases, independent E1 glyph points and exact native root/include filename+line; no browser/product adoption','tooling':{str(f):sha(f)for f in [Path(__file__),here/'run-current-project.cjs',here/'check-corpus-navigation.cjs',clock]},'cases':cases,'commands':commands,'oracle':{'length':(out/'oracle/receipt.json').stat().st_size,'sha256':sha(out/'oracle/receipt.json')}}
(out/'receipt.json').write_text(json.dumps(receipt,indent=2)+'\n');print(json.dumps({'status':'pass','nativePipelinePhases':52,'exactPdfGlyphInversePoints':16}))
