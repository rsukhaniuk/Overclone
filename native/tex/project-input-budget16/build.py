#!/usr/bin/env python3
"""Offline pinned P15 parent reproduction then bounded wrapper-only derivative.

All parent objects/retained source headers/SDK libraries are hash-checked.
No old recipe/source/binary is mutated. Run twice into independent fresh outputs.
"""
import argparse,hashlib,json,os,shutil,subprocess
from pathlib import Path
from prepare import PINS,EXPORTS,prepare,sha
p=argparse.ArgumentParser(description=__doc__)
for name in ['parent','source','cache','out']:p.add_argument('--'+name,type=Path,required=True)
p.add_argument('--kind',choices=['xe','dvi'],required=True)
a=p.parse_args();parent,source,cache,out=(getattr(a,k).resolve() for k in ['parent','source','cache','out'])
for protected in [parent,source,cache,Path(__file__).resolve().parent]:
    if out==protected or protected in out.parents:raise SystemExit('output must be separate from immutable inputs')
if out.exists():raise SystemExit('fresh output required')
if sha(parent/'manifest.json')!=PINS[a.kind]['manifest']:raise SystemExit('unexpected exact P15 manifest')
m=json.loads((parent/'manifest.json').read_text());association=json.loads((parent/'geometry-overlay/association.json').read_text())
for name,digest in m['objects'].items():assert sha(parent/name)==digest,name
for name,digest in m['sdkLibraries'].items():assert sha(cache/name)==digest,name
for name,digest in m['inputs'].items():
    if name.startswith('/inputs/dvi-source/'):assert sha(source/name[len('/inputs/dvi-source/'):])==digest,name
for row in association['source']:assert sha(parent/'geometry-overlay'/row['path'])==row['after'],row['path']
assert sha(parent/'geometry-overlay/oc_dvi_origin.cpp')==PINS[a.kind]['wrapper']
assert sha(parent/'geometry-overlay/oc_dvi.h')==PINS[a.kind]['header']
out.mkdir(parents=True)
env=dict(os.environ,EM_CACHE=str(cache),EM_FROZEN_CACHE='1')
old='/next/'+a.kind+'-neutral02';commands=[]
compile_cmd=next(c for c in m['commands'] if c[0]=='em++' and '-c' in c)
link_cmd=m['commands'][-1];assert link_cmd[0]=='em++' and '-sFILESYSTEM=0' in link_cmd
assert '-sMAXIMUM_MEMORY=536870912' in link_cmd

def run(command,folder,name):
    with (folder/(name+'.log')).open('wb') as log:subprocess.run(command,env=env,stdout=log,stderr=subprocess.STDOUT,check=True,timeout=120)
    commands.append({'stage':folder.name,'label':name,'argv':command})

def build(folder,candidate):
    folder.mkdir();overlay=folder/'geometry-overlay';overlay.mkdir()
    if candidate:prepare(parent,overlay,a.kind);prepare(parent,overlay,a.kind) # exact idempotence
    else:
        for name in ['oc_dvi_origin.cpp','oc_dvi.h']:shutil.copyfile(parent/'geometry-overlay'/name,overlay/name)
    for name in m['objects']:shutil.copyfile(parent/name,folder/name)
    def mapped(arg):return arg.replace(old,str(folder)).replace('/inputs/dvi-source',str(source))
    command=[mapped(arg) for arg in compile_cmd]
    run(command,folder,'wrapper')
    if not candidate:assert sha(folder/'oc_dvi.o')==m['objects']['oc_dvi.o'],'exact parent wrapper object reproduction'
    command=[mapped(arg) for arg in link_cmd]
    if candidate:
        for i,arg in enumerate(command):
            if arg.startswith('-sEXPORTED_FUNCTIONS='):
                exports=json.loads(arg.split('=',1)[1]);assert not set(EXPORTS)&set(exports);command[i]='-sEXPORTED_FUNCTIONS='+json.dumps(exports+EXPORTS)
    run(command,folder,'link')
    pins={name:{'bytes':(folder/name).stat().st_size,'sha256':sha(folder/name)} for name in ['oc-dvi.js','oc-dvi.wasm']}
    if not candidate:assert pins==m['outputs'],'exact P15 parent binary reproduction required'
    changed=[name for name in m['objects'] if sha(folder/name)!=m['objects'][name]]
    assert changed==(['oc_dvi.o'] if candidate else [])
    return {'outputs':pins,'changedObjects':changed,'wrapperSha256':sha(overlay/'oc_dvi_origin.cpp'),'headerSha256':sha(overlay/'oc_dvi.h')}
original=build(out/'parent-reproduced',False);candidate=build(out/'candidate',True)
receipt={'schemaVersion':1,'status':'native-candidate-not-adopted','reservedFutureProfile':16,'kind':a.kind,'parentManifestSha256':sha(parent/'manifest.json'),'parentReproduced':original,'candidate':candidate,
 'limits':{'inputBudgetAbi':1,'projectInputBytes':16777216,'pdfOutputBytes':8388608,'resourceBytes':100663296,'perFileBytes':33554432,'files':4096,'maximumMemoryBytes':536870912},
 'sourceAssociation':association,'unchangedObjectHashes':{k:v for k,v in m['objects'].items() if k!='oc_dvi.o'},'sdkLibraries':m['sdkLibraries'],
 'recipe':{name:sha(Path(__file__).with_name(name)) for name in ['build.py','prepare.py']},'commands':commands,'toolchain':subprocess.check_output(['emcc','--version'],env=env,text=True).splitlines()[0]}
(out/'receipt.json').write_text(json.dumps(receipt,indent=2)+'\n');print(json.dumps({'parentExact':True,'kind':a.kind,'candidate':candidate['outputs']}))
