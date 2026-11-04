#!/usr/bin/env python3
"""One coordinated isolated native origin1 acceptance batch. No app/profile
build, network, dependency acquisition, source mutation or browser claims."""
import hashlib,json,os,subprocess,sys
from pathlib import Path
here=Path(__file__).resolve().parent;graphics=here.parent/'graphics-navigation';fixtures=here.parent/'synctex-fixtures';clock=fixtures/'dvi/fixed-clock.cjs';repo=here.parents[2]
root=Path(os.environ['RESULTS'])
if root.exists():raise SystemExit('fresh results required')
for n in ['CANDIDATE','DVI_CONVERTER','XE_CONVERTER']:assert os.environ.get(n),n
root.mkdir(parents=True);commands=[]
def run(label,argv,**env):
 values=dict(os.environ,**{k:str(v)for k,v in env.items()})
 with(root/(label+'.log')).open('wb')as f:p=subprocess.run(list(map(str,argv)),env=values,stdout=f,stderr=subprocess.STDOUT,timeout=240)
 commands.append({'label':label,'argv':list(map(str,argv)),'environment':{k:str(v)for k,v in env.items()},'exit':p.returncode})
 (root/'commands.json').write_text(json.dumps(commands,indent=2)+'\n')
 assert p.returncode==0,f'{label}: native check failed; see retained log'
 print(label+' exit=0',flush=True)
def node(script,*args):return ['node','--require',clock,script,*args]
run('capabilities',node(here/'test-capabilities.cjs'),RESULTS=root/'capabilities')
ordinary=root/'ordinary';ordinary.mkdir()
ordinary_cases=[]
for kind in ['dvi','xe','lua']:
 for j,stage in enumerate(['pass1','bib','pass2','pass3']):
  args=[kind,'bibtex'if stage=='bib'else'latex',ordinary/(kind+'-'+stage)]
  if j:args+=[ordinary/(kind+'-'+['pass1','bib','pass2'][j-1])]
  run(kind+'-'+stage,node(graphics/'reproduce.cjs',*args),LUA_CANDIDATE=os.environ['CANDIDATE'],PASS_TAG=901+j)
 folder=ordinary/(kind+'-pass3')
 if kind!='lua':
  converted=ordinary/(kind+'-convert');run(kind+'-convert',node(graphics/'reproduce.cjs',kind,'convert',converted,folder),CONVERTER=os.environ['DVI_CONVERTER'if kind=='dvi'else'XE_CONVERTER'],PASS_TAG=905);folder=converted
 source=(repo/'tests/CompilerSuiteFixtures'/('main-latin.tex'if kind=='dvi'else'main-unicode.tex')).read_text().splitlines()
 ordinary_cases.append({'name':kind,'folder':str(folder),'map':str(ordinary/(kind+'-pass3')/'raw.synctex'),'rootLine':next(i for i,t in enumerate(source,1)if'Portable compiler fixture'in t),'bookLine':next(i for i,t in enumerate(source,1)if'Book citation'in t),'includeLine':2})
manifest=root/'ordinary-cases.json';manifest.write_text(json.dumps(ordinary_cases,indent=2)+'\n')
run('ordinary-pdf-query-oracle',['python3',graphics/'pdf-query-oracle.py'],RESULTS=root/'ordinary-oracle',CASES=manifest)
# The accepted oracle owns its own result directories; make only its top root.
origin=root/'origin';origin.mkdir();cases=[]
for j,(kind,mode)in enumerate([('dvi','normal'),('xe','normal'),('lua','normal'),('dvi','view'),('xe','view'),('lua','view'),('dvi','mag1200'),('xe','mag1200'),('lua','table')]):
 name=kind+'-'+mode;project=origin/(name+'-project');args=['python3',here/'make-project.py','--kind',kind,'--out',project]
 if mode!='normal':args+=['--lua-table'if mode=='table'else'--'+mode]
 run(name+'-prepare',args)
 producer=origin/(name+'-producer');run(name+'-producer',node(here/'run-project.cjs',kind,'latex',producer),PROJECT=project,REQUIRE_ORIGIN=1,LUA_CANDIDATE=os.environ['CANDIDATE'],PASS_TAG=1001+j*2)
 folder=producer
 if kind!='lua':
  folder=origin/(name+'-convert');run(name+'-convert',node(here/'run-project.cjs',kind,'convert',folder,producer),PROJECT=project,REQUIRE_ORIGIN=1,CONVERTER=os.environ['DVI_CONVERTER'if kind=='dvi'else'XE_CONVERTER'],PASS_TAG=1002+j*2)
 cases.append({'name':name,'project':str(project),'folder':str(folder),'map':str(producer/'raw.synctex'),'view':mode=='view'})
manifest=root/'origin-cases.json';manifest.write_text(json.dumps(cases,indent=2)+'\n')
run('origin-pdf-query-oracle',['python3',here/'pdf-query-oracle.py'],RESULTS=root/'origin-oracle',CASES=manifest)
run('inherited-root-controls',['python3',here/'test-inherited.py'],RESULTS=root/'inherited')
run('inherited-positive-pdf-query-oracle',['python3',here/'pdf-query-oracle.py'],RESULTS=root/'inherited-oracle',CASES=root/'inherited/oracle-cases.json')
run('origin-domain-negatives',['python3',here/'test-domains.py'],RESULTS=root/'domains')
for mode in ['scoped-axis','raw-balanced','matrix-suffix','matrix-exponent','matrix-short','matrix-singular','matrix-reflect','matrix-shear','missing-restore','missing-save','misplaced-restore']:
 run('lua-state-'+mode,node(graphics/'test-lua-states.cjs',mode),RESULTS=root/'lua-states')
for mode in ['literal','lua-print','lua-literal','setmatrix','colorstack','attributes','lua-attributes','form','raw-object','tex-object','tree-attributes','reorder']:
 run('lua-unsafe-'+mode,node(fixtures/'lua/test-geometry.cjs',mode),RESULTS=root/'lua-unsafe',LUA_EXPECTED_ABI=5)
for mode in ['cap','native-fail','invalid-config','repeat-config']:
 run('lua-boundary-'+mode,node(fixtures/'lua/test-runtime.cjs',mode),RESULTS=root/'lua-boundaries',LUA_EXPECTED_ABI=5)
for kind in ['dvi','xe']:
 converter=os.environ['DVI_CONVERTER'if kind=='dvi'else'XE_CONVERTER']
 run(kind+'-legacy-unsafe',node(here/'test-legacy-converter.cjs'),RESULTS=root/(kind+'-legacy-unsafe'),KIND=kind,CONVERTER=converter,INTERMEDIATE=ordinary/(kind+'-pass3')/('output.dvi'if kind=='dvi'else'output.xdv'),RESOURCES='/control/r2-graphics-navigation/build/converter-resources')
 for mode in ['empty','bad-header','truncated','bad-postamble','unknown-unit','invalid-config','post-config','mutate-caller-buffer']:
  run(kind+'-boundary-'+mode,node(fixtures/kind/'test-convert.cjs',mode),RESULTS=root/(kind+'-boundaries'),CONVERTER=converter,DVI_INPUT='/control/r2-dvi-maps/evidence/final/runtime/positive.dvi',XDV_INPUT='/control/r2-xe-maps/evidence/final/runtime/positive.xdv')
run('physical-cancellation',node(graphics/'test-cancel.cjs'),RESULTS=root/'cancellation',RECOVERY_ROOT=ordinary)
# Same fixture clock, actual selected carry/IR bytes and complete outputs. No
# filtering/normalization. Existing candidate directories remain read-only.
equality=[]
for kind in ['dvi','xe','lua']:
 old='/control/r2-graphics-navigation/candidate/'+('lua-graphics4'if kind=='lua'else kind+'-graphics1');target=root/(kind+'-parent')
 if kind=='lua':
  run(kind+'-parent-equality',node(graphics/'reproduce.cjs',kind,'latex',target,ordinary/'lua-pass2'),LUA_CANDIDATE=old,PASS_TAG=904)
  new=ordinary/'lua-pass3';names=['output.pdf','output.log','output.aux','output.bbl','raw.synctex','geometry.json']
 else:
  run(kind+'-parent-equality',node(graphics/'reproduce.cjs',kind,'convert',target,ordinary/(kind+'-pass3')),CONVERTER=old,PASS_TAG=905)
  new=ordinary/(kind+'-convert');names=['output.pdf','output.log','geometry.json']
 for n in names:
  a=(target/n).read_bytes();b=(new/n).read_bytes();assert a==b,f'{kind}: parent bytes differ: {n}'
  equality.append({'kind':kind,'artifact':n,'length':len(a),'sha256':hashlib.sha256(a).hexdigest(),'byteIdentical':True})
(root/'parent-equality.json').write_text(json.dumps({'schemaVersion':1,'status':'pass','rows':equality,'clock':'fixed-clock.cjs before Emscripten; real monotonic cancellation timing'},indent=2)+'\n')
(root/'receipt.json').write_text(json.dumps({'schemaVersion':1,'status':'pass','commands':commands,'skips':0,'claims':'Native origin1 source package only. New profile/browser/default adoption remains root-owned/unrun.'},indent=2)+'\n')
print('complete native origin1 batch pass',flush=True)
