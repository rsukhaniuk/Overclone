#!/usr/bin/env python3
"""Actual native unsupported origin-domain controls. No map/PDF mutation and
no source-derived geometry; each synthetic source runs a fresh native module."""
import hashlib,json,os,subprocess,sys
from pathlib import Path
here=Path(__file__).resolve().parent
root=Path(os.environ['RESULTS'])
if root.exists():raise SystemExit('fresh domain results required')
root.mkdir(parents=True)
cases={
 'unordered':'/MediaBox [12 18 10 818]',
 'degenerate':'/MediaBox [12 18 12 818]',
 'short':'/MediaBox [12 18 612]',
 'wrong-type':'/MediaBox (12 18 612 818)',
 'duplicate-media':'/MediaBox [12 18 612 818] /MediaBox [-24 -30 576 770]',
 'duplicate-crop':'/MediaBox [12 18 612 818] /CropBox [20 30 600 800] /CropBox [20 30 600 800]',
 'duplicate-rotate':'/MediaBox [12 18 612 818] /Rotate 0 /Rotate 90',
 'out-of-range':'/MediaBox [-1000001 18 612 818]',
 'nonfinite':'/MediaBox [NaN 18 612 818]',
 'exponent':'/MediaBox [1e1 18 612 818]',
 'oversize':'/MediaBox [12.'+'0'*4200+' 18 612 818]',
 'userunit2':'/MediaBox [12 18 612 818] /UserUnit 2',
 'userunit1':'/MediaBox [12 18 612 818] /UserUnit 1',
 'media-prefix':'/MediaBoxExtra [12 18 612 818]',
 'unsupported-rotation':'/MediaBox [12 18 612 818] /Rotate 45',
 'unsupported-attribute':'/MediaBox [12 18 612 818] /Contents 1 0 R',
 'crop-outside':'/MediaBox [12 18 612 818] /CropBox [-10 -10 700 900]',
}
rows=[];commands=[]
def run(label,cmd,env):
 p=subprocess.run(cmd,capture_output=True,text=True,env=env,timeout=60)
 (root/(label+'.log')).write_text(p.stdout+p.stderr);commands.append({'label':label,'argv':cmd,'exit':p.returncode})
 return p.returncode
for kind in ['dvi','xe','lua']:
 selected=dict(cases)
 selected['duplicate-native-channel']='/MediaBox [12 18 612 818]'
 for name,attrs in selected.items():
  target=root/(kind+'-'+name);project=target/'project';target.mkdir()
  cmd=[sys.executable,str(here/'make-project.py'),'--kind',kind,'--out',str(project),'--first-attributes',attrs]
  if name=='duplicate-native-channel':
   if kind=='lua':cmd+=['--extra-tex',r'\directlua{pdf.setpageattributes("/MediaBox [12 18 612 818]")}']
   else:cmd+=['--repeat-put']
  assert run(kind+'-'+name+'-prepare',cmd,os.environ)==0
  env=dict(os.environ,PROJECT=str(project),REQUIRE_ORIGIN='1',LUA_CANDIDATE=os.environ['CANDIDATE'])
  clock=here.parent/'synctex-fixtures/dvi/fixed-clock.cjs'
  cmd=['node','--require',str(clock),str(here/'run-project.cjs'),kind,'latex',str(target/'producer')]
  producer=run(kind+'-'+name+'-native',cmd,env)
  folder=target/'producer';converted=None
  receipt=folder/'receipt.json'
  assert receipt.exists(),f'{kind} {name}: setup/trap needs diagnosis, not a native negative pass'
  try:producer_receipt=json.loads(receipt.read_bytes())
  except (OSError,ValueError)as error:raise AssertionError('invalid actual producer receipt')from error
  assert producer_receipt['kind']==kind and producer_receipt['operation']=='latex'
  if producer:assert producer_receipt['nativeReturn']!=0
  if producer==0 and kind!='lua':
   env['CONVERTER']=os.environ['DVI_CONVERTER'if kind=='dvi'else'XE_CONVERTER']
   cmd=['node','--require',str(clock),str(here/'run-project.cjs'),kind,'convert',str(target/'convert'),str(target/'producer')]
   converted=run(kind+'-'+name+'-convert',cmd,env);folder=target/'convert'
  receipt=folder/'receipt.json'
  assert receipt.exists(),f'{kind} {name}: no actual native receipt'
  try:native_receipt=json.loads(receipt.read_bytes())
  except (OSError,ValueError)as error:raise AssertionError('invalid actual result receipt')from error
  assert native_receipt['kind']==kind
  if converted:assert native_receipt['nativeReturn']!=0
  g=folder/'geometry.json';payload=g.read_bytes()if g.exists()else b'';geometry=None
  if native_receipt['nativeReturn']==0:assert payload,'successful native negative must explicitly refuse all4 pages, not omit its witness'
  if payload:
   try:geometry=json.loads(payload)
   except ValueError as error:raise AssertionError('malformed native negative sidecar')from error
   assert geometry['pages']and all(p['status']=='unsupported'and p['writerToPdfRaw']is None and p['writerPageBoxBp']is None for p in geometry['pages']),f'{kind} {name}: unsafe native geometry became available'
  source=(project/'тези/my thesis.tex').read_bytes()
  rows.append({'kind':kind,'case':name,'sourceSha256':hashlib.sha256(source).hexdigest(),'producerExit':producer,'converterExit':converted,'geometryBytes':len(payload),'geometry':geometry,'status':'pass','semantics':'actual native failure OR complete unsupported geometry; never an available prefix'})
(root/'receipt.json').write_text(json.dumps({'schemaVersion':1,'status':'pass','rows':rows,'commands':commands,'skips':0},ensure_ascii=False,indent=2)+'\n')
print('all actual native unsupported origin-domain controls pass')
