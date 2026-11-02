#!/usr/bin/env python3
"""Reprove retained dependencies offline by fresh compilation, never upstream make.
The cache is read-only. Historical TU-only stamps are NOT header proofs. This
recipe recompiles every archived unit with current headers/compiler, records all
actual -MD dependencies, and reuses a retained archive only if all member bytes
match its freshly compiled counterparts. Otherwise it supplies the fresh archive.
"""
import argparse
import concurrent.futures
import hashlib
import json
import os
from pathlib import Path
import re
import shlex
import shutil
import subprocess

p=argparse.ArgumentParser(description=__doc__)
p.add_argument('--retained',type=Path,required=True)
p.add_argument('--out',type=Path,required=True)
p.add_argument('--cache',type=Path,required=True)
a=p.parse_args();old=a.retained.resolve();out=a.out.resolve();cache=a.cache.resolve()
for root in [old,cache,Path('/work'),Path('/inputs'),Path('/control/r2-xe-maps/inputs')]:
 if out==root or root in out.parents:raise SystemExit('protected output overlap')
if out.exists():raise SystemExit('fresh output required')
out.mkdir(parents=True)
env=dict(os.environ,EM_CACHE=str(cache),EM_FROZEN_CACHE='1',EMCC_CORES='1')
for k in ['http_proxy','https_proxy','all_proxy','HTTP_PROXY','HTTPS_PROXY','ALL_PROXY']:env[k]='http://127.0.0.1:9'
def sha(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
def run(label,cmd,cwd=out):
 with (out/(label+'.log')).open('wb') as f:r=subprocess.run(cmd,cwd=cwd,env=env,stdout=f,stderr=subprocess.STDOUT,timeout=180)
 if r.returncode:raise RuntimeError(label+': see '+str(out/(label+'.log')))
 return {'argv':cmd,'cwd':str(cwd),'exit':r.returncode}
def alias(s):return s.replace('/control/build/xetex',str(old))
# Finite accepted compiler records only, not shell/Make/CMake execution.
try:
 rows=[json.loads(x) for x in (old/'commands.jsonl').read_text().splitlines()]
except (OSError, ValueError) as error:
 raise SystemExit('Invalid retained compiler records') from error
AR=shutil.which('emar')
if AR is None:raise SystemExit('Missing approved emar')
jobs={};archives={};order={}
for row in rows:
 name=row['name'];cmd=row['command']
 if not any(name.startswith(x+'-') for x in ['icu-common','icu-i18n','icu-stub','harfbuzz']):continue
 if name.endswith('-archive'):
  group=name.removesuffix('-archive');archives[group]=old/(group+'.a');order[group]=[Path(x).name for x in cmd[3:]];continue
 if '-c' not in cmd:continue
 group=next(x for x in ['icu-common','icu-i18n','icu-stub','harfbuzz'] if name.startswith(x+'-'))
 argv=[alias(x) for x in cmd[1:]];src=next(x for x in argv if x.endswith(('.cpp','.cc','.c')));target=Path(argv[argv.index('-o')+1]);jobs[(group,target.name)]=(src,argv,None)
for group,sub,target in [('freetype','freetype/CMakeFiles/freetype.dir','freetype/libfreetype.a'),('graphite','graphite/src/CMakeFiles/graphite2.dir','graphite/src/libgraphite2.a')]:
 meta=old/sub;text=(meta/'DependInfo.cmake').read_text();flags=(meta/'flags.make').read_text();opts=[]
 for line in flags.splitlines():
  if re.match(r'(C|CXX)_(DEFINES|INCLUDES|FLAGS) =',line):opts+=shlex.split(alias(line.split(' =',1)[1]))
 records=re.findall(r'"([^"]+)" "([^"]+\.o)" "gcc" "[^"]+"',text);assert records
 archives[group]=old/target;order[group]=[]
 for source,obj in records:
  dest=Path(obj).name;order[group].append(dest);jobs[(group,dest)]=(alias(source),['-c',*opts,alias(source),'-o','UNSET'],None)
# Xpdf's verbose retained log contains the actual compile flags. Parse argv;
# never execute its shell pipeline, dependency expansion or upstream Makefile.
archives['xpdf']=old/'xpdf/libxpdf.a';order['xpdf']=[]
for line in (old/'xpdf-build.log').read_text().splitlines():
 if not line.startswith('/control/bin/em++ ') or ' -c ' not in line:continue
 v=shlex.split(line.removesuffix(' &&\\'))[1:];src=v[-1];obj=v[v.index('-o')+1];name=Path(obj).name
 for flag in ['-MT','-MF']:
  i=v.index(flag);del v[i:i+2]
 v=[x for x in v if x not in ['-MD','-MP']];v=[alias(x) for x in v]
 if ('xpdf',name) not in jobs:order['xpdf'].append(name)
 jobs[('xpdf',name)]=(src,v,old/'xpdf')
assert 200<len(jobs)<2000 and all(order.values())
for group in archives:(out/group).mkdir()
def compile_one(item):
 (group,name),(src,argv,cwd)=item;assert Path(src).is_file()
 target=out/group/name;dep=target.with_suffix('.d');argv=list(argv);argv[argv.index('-o')+1]=str(target)
 cc=shutil.which('emcc' if src.endswith('.c') else 'em++');assert cc
 command=[cc,*argv,'-MD','-MF',str(dep)];receipt=run(group+'-'+name,command,cwd or out)
 deps=shlex.split(dep.read_text().replace('\\\n',' ').split(':',1)[1]);headers={}
 for n in deps:
  q=Path(n);q=q if q.is_absolute() else (cwd or out)/q
  headers[str(q.resolve())]=sha(q)
 receipt.update(source=src,sourceSha256=sha(src),headers=headers,objectSha256=sha(target))
 return group,name,receipt
proof={}
with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
 for group,name,receipt in pool.map(compile_one,jobs.items()):proof[group+'/'+name]=receipt
(out/'units.json').write_text(json.dumps(proof,indent=2)+'\n')
result={}
for group,archive in archives.items():
 objects=[out/group/n for n in order[group]];fresh=out/(group+'.a');run(group+'-archive',[AR,'rcs',str(fresh),*map(str,objects)])
 # Archive container ordering/padding need not match; member bytes must match
 # before the retained archive, rather than freshly rebuilt one, can be reused.
 members=subprocess.check_output([AR,'t',str(archive)],env=env,text=True).splitlines()
 if len(members)!=len(set(members)) or set(members)!=set(order[group]):
  raise RuntimeError('Retained archive member set mismatch: '+group)
 identical=True
 for n in members:
  b=subprocess.check_output([AR,'p',str(archive),n],env=env)
  if hashlib.sha256(b).hexdigest()!=proof[group+'/'+n]['objectSha256']:identical=False
 if identical:shutil.copyfile(archive,fresh)
 result[group]={'retained':str(archive),'retainedSha256':sha(archive),'actualSha256':sha(fresh),'reusedRetainedArchive':identical,'members':len(members)}
(out/'proof.json').write_text(json.dumps({'schemaVersion':1,'status':'pass','units':proof,'archives':result,'recipeSha256':sha(__file__),'compilerVersion':subprocess.check_output(['emcc','--version'],text=True).splitlines()[0]},indent=2)+'\n')
print(json.dumps({'status':'pass','freshUnits':len(proof),'archives':result},indent=2))
