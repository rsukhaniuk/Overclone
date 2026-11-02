#!/usr/bin/env python3
"""Full fresh Xe engine/header-unit build against byte-reproved cached deps.
No upstream Makefile, download, engine-object reuse or runtime admission.
"""
import argparse
import concurrent.futures
import hashlib
import json
import os
from pathlib import Path
import subprocess
HERE=Path(__file__).resolve().parent
p=argparse.ArgumentParser(description=__doc__)
p.add_argument('--prepared',type=Path,required=True)
p.add_argument('--dependencies',type=Path,required=True)
p.add_argument('--retained',type=Path,required=True)
p.add_argument('--texlive',type=Path,required=True)
p.add_argument('--cache',type=Path,required=True)
p.add_argument('--out',type=Path,required=True)
a=p.parse_args();src,deps,retained,tl,cache,out=[x.resolve() for x in [a.prepared,a.dependencies,a.retained,a.texlive,a.cache,a.out]]
if out.exists():raise SystemExit('fresh output required')
for root in [src,deps,retained,tl,cache,Path('/work'),Path('/inputs'),Path('/control/r2-xe-maps/inputs')]:
 if out==root or root in out.parents:raise SystemExit('protected output overlap')
if not (src/'maps-guard.h').is_file():raise SystemExit('explicit Xe maps source required')
def sha(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
try:proof=json.loads((deps/'proof.json').read_text())
except (OSError,ValueError) as error:raise SystemExit('missing complete dependency proof') from error
assert proof['status']=='pass' and len(proof['units'])==563
for group,x in proof['archives'].items():
 if sha(deps/(group+'.a'))!=x['actualSha256']:raise SystemExit('dependency archive mismatch: '+group)
# Header/source equality, not old TU-only stamps, permits dependency reuse.
for x in proof['units'].values():
 if sha(x['source'])!=x['sourceSha256']:raise SystemExit('dependency source drift')
 for n,h in x['headers'].items():
  if sha(n)!=h:raise SystemExit('dependency header drift: '+n)
out.mkdir(parents=True);env=dict(os.environ,EM_CACHE=str(cache),EM_FROZEN_CACHE='1',EMCC_CORES='1')
def run(cmd,label):
 with (out/(label+'.log')).open('wb') as f:r=subprocess.run(list(map(str,cmd)),env=env,stdout=f,stderr=subprocess.STDOUT,timeout=180)
 if r.returncode:raise SystemExit(label+' failed: '+str(out/(label+'.log')))
 return list(map(str,cmd))
icu=tl/'libs/icu/icu-src/source';ft=tl/'libs/freetype2/freetype-src';hb=tl/'libs/harfbuzz/harfbuzz-src';gr=tl/'libs/graphite2/graphite2-src'
flags=['-O2','-fno-exceptions','-DWEBASSEMBLY_BUILD','-D__SyncTeX__','-DU_STATIC_IMPLEMENTATION','-Wno-deprecated-declarations','-Wno-macro-redefined','-Wno-pointer-sign','-Wno-parentheses-equality','-sUSE_LIBPNG=1']
incs=[src,src/'tex',src/'xetexdir',src/'teckit',hb/'src',retained/'hbinclude',icu/'common',icu/'i18n',ft/'include',retained/'freetype/include',gr/'include',retained/'xpdf',tl/'libs/xpdf/xpdf-src',tl/'libs/xpdf/xpdf-src/goo',tl/'libs/xpdf/xpdf-src/xpdf']
flags+=['-I'+str(q) for q in incs]
flags+=['-ffile-prefix-map='+str(src)+'=/inputs/xe-maps-port','-ffile-prefix-map='+str(out)+'=/inputs/xe-maps-build','-ffile-prefix-map='+str(retained)+'=/inputs/xe-retained-dependencies']
cfiles=['tex/xetex0.c','tex/xetexini.c','tex/xetex-pool.c','md5.c','xmemory.c','texfile.c','kpseemu.c','texmfmp.c','main.c','bibtex.c','xetexdir/XeTeX_ext.c','xetexdir/XeTeX_pic.c','xetexdir/image/bmpimage.c','xetexdir/image/jpegimage.c','xetexdir/image/pngimage.c','xetexdir/trans.c','synctexdir/synctex.c','oc_synctex_port.c']
cppfiles=['xetexdir/XeTeXOTMath.cpp','xetexdir/XeTeXLayoutInterface.cpp','xetexdir/XeTeXFontMgr.cpp','xetexdir/XeTeXFontInst.cpp','xetexdir/XeTeXFontMgr_FC.cpp','xetexdir/hz.cpp','xetexdir/pdfimage.cpp','teckit/teckit-Engine.cpp','oc_synctex_memory_sink.cpp']
items=[src/n for n in cfiles+cppfiles]+[HERE/'runtime.cpp']
def compile_one(q):
 name=q.name;obj=out/(name+'.o');extra=['-DSYNCTEX_ENGINE_H=<oc_synctex_engine.h>'] if name=='synctex.c' else []
 cmd=run(['emcc' if q.suffix=='.c' else 'em++',*flags,*extra,'-c',q,'-MD','-MF',out/(name+'.d'),'-o',obj],name)
 return {'source':str(q),'sourceSha256':sha(q),'command':cmd,'objectSha256':sha(obj)}
with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:units=list(pool.map(compile_one,items))
exports=['compileFormat','compileLaTeX','compileBibtex','setMainEntry','malloc','free','oc_xe_maps_abi','oc_mem_configure','oc_synctex_abi','oc_synctex_status','oc_synctex_length','oc_synctex_copy']
archives=[deps/(n+'.a') for n in ['harfbuzz','graphite','freetype','xpdf','icu-i18n','icu-common','icu-stub']]
link=run(['em++','-O2',*sorted(out.glob('*.o')),*archives,'-sUSE_LIBPNG=1','-sMODULARIZE=1','-sEXPORT_NAME=createXeMaps','-sENVIRONMENT=web,worker,node','-sALLOW_MEMORY_GROWTH=1','-sMAXIMUM_MEMORY=536870912','-sINITIAL_MEMORY=33554432','-sSTACK_SIZE=5242880','-sFORCE_FILESYSTEM=1','-sINVOKE_RUN=0','-sEXIT_RUNTIME=0','-sEXPORTED_FUNCTIONS='+json.dumps(['_'+n for n in exports]),'-sEXPORTED_RUNTIME_METHODS=["FS","MEMFS","ccall","cwrap","HEAPU8"]','-Wl,-Map,'+str(out/'link.map'),'-o',out/'engine.js'],'link')
manifest={'schemaVersion':1,'capability':'Xe raw SyncTeX1; actual XDV7 close; new generated format required','units':units,'link':link,'dependencyProofSha256':sha(deps/'proof.json'),'dependencies':{str(q):sha(q) for q in archives},'sourceHeaders':{str(q.relative_to(src)):sha(q) for q in sorted(src.rglob('*')) if q.is_file()},'outputs':{n:{'length':(out/n).stat().st_size,'sha256':sha(out/n)} for n in ['engine.js','engine.wasm']},'recipeSha256':sha(__file__)}
(out/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n');print(json.dumps(manifest['outputs'],indent=2))
