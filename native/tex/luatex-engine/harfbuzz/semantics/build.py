#!/usr/bin/env python3
"""Derive one candidate from fonts04: rebuild only genuine SyncTeX + capability.
All other actual archive members and linked objects are checked byte-for-byte.
Run in the approved cached SDK container; no download/default/profile mutation.
"""
import argparse,hashlib,json,os,pathlib,shlex,shutil,subprocess,sys
p=argparse.ArgumentParser(description=__doc__)
for n in ['parent-build','parent-candidate','graphics-build','cache','work','out']:p.add_argument('--'+n,type=pathlib.Path,required=True)
a=p.parse_args();w=a.work.resolve();o=a.out.resolve();here=pathlib.Path(__file__).resolve().parent
assert not w.exists()and not o.exists();w.mkdir(parents=True);o.mkdir(parents=True)
hash=lambda b:hashlib.sha256(b).hexdigest()
pin=lambda f:{'length':pathlib.Path(f).stat().st_size,'sha256':hash(pathlib.Path(f).read_bytes())}
parent=json.loads((a.parent_candidate/'harfbuzz-build.json').read_text());assert parent['capabilities']['fontClosure']==1
for n,h in parent['inputs'].items():assert pin(n)['sha256']==h,n
for n,h in parent['sources'].items():assert pin(n)['sha256']==h,n
for n,h in parent['consumedHeaders'].items():assert pin(n)['sha256']==h,n
for n,expected in parent['outputs'].items():assert pin(a.parent_candidate/n)==expected,n
archive=a.graphics_build/'browser-luatex.a';assert pin(archive)['sha256']=='1662ccd3ac6cb3cbee67800cc88bf6244199c73e7968d1c2f9f81acb7a598968'
assert pin(a.graphics_build/'overlay/association.json')['sha256']=='8004aad4e299019db9e3850884b5c2959c1d489475794bd6a229054bc3096112'
commands=[];env=dict(os.environ,EM_CACHE=str(a.cache),EM_FROZEN_CACHE='1',EMCC_CORES='1')
def run(label,argv):
 argv=list(map(str,argv))
 with(w/(label+'.log')).open('wb')as log:r=subprocess.run(argv,env=env,stdout=log,stderr=subprocess.STDOUT,timeout=180)
 commands.append({'label':label,'argv':argv,'exit':r.returncode});(w/'commands.json').write_text(json.dumps(commands,indent=2)+'\n')
 assert not r.returncode,label
source=w/'overlay/synctexdir/synctex.c';source.parent.mkdir(parents=True)
run('source-overlay',[sys.executable,here/'fix-kern-provenance.py',a.graphics_build/'overlay/synctexdir/synctex.c',source])
original_commands=json.loads((a.graphics_build/'commands.json').read_text())
compile=next(x['argv']for x in original_commands if x['argv'][0]=='emcc'and any(s.endswith('/synctexdir/synctex.c')for s in x['argv']))
obj=w/'libluatex_a-synctex.o';oldsrc=compile[compile.index('-c')+1];oldobj=compile[compile.index('-o')+1]
compile=[str(source)if s==oldsrc else str(obj)if s==oldobj else s for s in compile]
compile+=['-MD','-MF',str(w/'synctex.d'),'-ffile-prefix-map='+str(w/'overlay')+'=/inputs/texlive/texk/web2c']
run('synctex',compile)
newarchive=w/'browser-luatex.a';shutil.copyfile(archive,newarchive);run('archive',['emar','r',newarchive,obj])
# Read trusted LLVM/GNU ar member bytes, excluding only symbol/name indexes.
# The archive is source-pinned above; this is an equality observer, not an import API.
def members(file):
 b=file.read_bytes();assert b.startswith(b'!<arch>\n');at=8;names=b'';result=[]
 while at<len(b):
  h=b[at:at+60];assert len(h)==60 and h[58:]==b'`\n';size=int(h[48:58]);name=h[:16].decode().strip();payload=b[at+60:at+60+size];assert len(payload)==size;at+=60+size+(size%2)
  if name=='//':names=payload;continue
  if name in ['/','/SYM64/']:continue
  if name.startswith('/')and name[1:].isdigit():
   offset=int(name[1:]);name=names[offset:names.index(b'/\n',offset)].decode()
  else:name=name.removesuffix('/')
  result.append({'name':name,'length':len(payload),'sha256':hash(payload)})
 return result
before,after=members(archive),members(newarchive)
assert [x['name']for x in before]==[x['name']for x in after]
changed=[x['name']for x,y in zip(before,after)if x!=y]
assert changed==['libluatex_a-synctex.o'],changed
run('capability',['em++','-std=c++17','-O1','-c',here/'capability.cpp','-o',w/'text-semantics-capability.o'])
link=json.loads((a.parent_build/'commands.json').read_text())[-1]['argv'];assert link[0]=='em++'
archive_arg=next(s for s in link if s.endswith('/browser-luatex.a'))
link=[str(newarchive)if s==archive_arg else str(o/'luatex.js')if s==str(a.parent_candidate/'luatex.js')else s for s in link]
assert link[-1]==str(o/'luatex.js')
export=next(s for s in link if s.startswith('-sEXPORTED_FUNCTIONS='));symbols=json.loads(export.split('=',1)[1]);symbols.append('_oc_lua_text_semantics_abi');link[link.index(export)]='-sEXPORTED_FUNCTIONS='+json.dumps(symbols)
link.insert(2,str(w/'text-semantics-capability.o'));run('link',link)
headers={}
for n in shlex.split((w/'synctex.d').read_text().replace('\\\n','').split(':',1)[1]):headers[n]=pin(n)
receipt={'schemaVersion':1,'status':'native-candidate-not-adopted','capabilities':{'abi':5,'graphics':1,'harfbuzz':1,'fontClosure':1,'textSemantics':1},'parentManifest':pin(a.parent_candidate/'harfbuzz-build.json'),'parentOutputs':parent['outputs'],'sources':{str(f):pin(f)for f in [here/'build.py',here/'fix-kern-provenance.py',here/'capability.cpp',source]},'sourceAssociation':pin(a.graphics_build/'overlay/association.json'),'archive':{'before':pin(archive),'after':pin(newarchive),'changedMembers':changed,'membersBefore':before,'membersAfter':after},'headers':headers,'allOtherLinkedInputsUnchanged':True,'inputLimits':parent['inputLimits'],'vfsBudgetSha256':parent['vfsBudgetSha256'],'commands':commands,'outputs':{n:pin(o/n)for n in ['luatex.js','luatex.wasm']}}
(o/'semantics-build.json').write_text(json.dumps(receipt,indent=2)+'\n');print(json.dumps(receipt['outputs']))
