#!/usr/bin/env python3
"""Private finite offline build of genuinely regenerated semantic pdfTeX/DVI. No Makefile runs."""
import argparse
import concurrent.futures
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess

p=argparse.ArgumentParser()
p.add_argument('--prepared',type=Path,required=True)
p.add_argument('--out',type=Path,required=True)
p.add_argument('--cache',type=Path,required=True)
a=p.parse_args()
src,out,cache=a.prepared.resolve(),a.out.resolve(),a.cache.resolve()
if out.exists(): raise SystemExit('fresh build directory required')
for protected in (src,cache,Path('/work'),Path('/inputs'),Path('/control/r2-dvi-maps/inputs')):
    if out==protected or protected in out.parents:
        raise SystemExit('build output overlaps protected input/cache/source')
if not (src/'semantics-preparation.json').is_file(): raise SystemExit('verified semantic source regeneration required')
out.mkdir(parents=True)
env=dict(os.environ,EM_CACHE=str(cache),EM_FROZEN_CACHE='1',http_proxy='http://127.0.0.1:9',https_proxy='http://127.0.0.1:9')
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest()
def run(cmd,label):
    with (out/(label+'.log')).open('wb') as f:
        x=subprocess.run(list(map(str,cmd)),env=env,stdout=f,stderr=subprocess.STDOUT,timeout=180)
    if x.returncode: raise SystemExit('build failed: '+label)
    return list(map(str,cmd))
# The finite source list is already supplied and pinned; parse declarations,
# never execute the upstream Makefile or arbitrary commands from it.
try: make=(src/'Makefile').read_text();xm=(src/'xpdf/Makefile').read_text()
except OSError as error: raise SystemExit('missing supplied source recipe') from error
cfiles=re.findall(r'\b(?:tex/|pdftexdir/)?[\w-]+\.c\b',make.split('PDFSOURCES')[0])
cfiles+=re.findall(r'pdftexdir/[\w-]+\.c\b',make.split('PDFSOURCES',1)[1].split('EPDFSOURCES',1)[0])
cfiles+=['synctexdir/synctex.c','oc_synctex_port.c','oc_font_semantics.c']
xfiles=re.findall(r'(?:goo|fofi|xpdf)/[\w-]+\.cc\b',xm.split('xpdfobjects')[0])
assert len(cfiles)==31 and len(set(cfiles))==len(cfiles)
assert len(xfiles)>=40 and len(set(xfiles))==len(xfiles)
includes=['-I'+str(src),'-I'+str(src/'tex'),'-I'+str(src/'pdftexdir'),'-I'+str(src/'xpdf'),'-I'+str(src/'xpdf/xpdf'),'-I'+str(src/'xpdf/goo'),'-I'+str(src/'xpdf/fofi'),'-I'+str(src/'xpdf/splash')]
flags=['-O1','-DWEBASSEMBLY_BUILD','-D__SyncTeX__','-sUSE_ZLIB=1','-sUSE_LIBPNG=1','-ffile-prefix-map='+str(src)+'=/inputs/pdftex-port','-Wno-pointer-sign','-Wno-error=implicit-function-declaration','-Wno-error=incompatible-pointer-types','-Wno-error=incompatible-function-pointer-types']
def one(item):
    rel,cpp=item
    target=out/(rel.replace('/','_')+'.o')
    extra=['-std=c++11','-fno-exceptions','-fno-rtti','-Wno-register'] if cpp else []
    if rel=='synctexdir/synctex.c': extra+=['-DSYNCTEX_ENGINE_H=<oc_synctex_engine.h>']
    return run(['em++' if cpp else 'emcc',*flags,*includes,*extra,'-c',src/rel,'-o',target],target.stem)
items=[(n,False) for n in cfiles]+[('pdftexdir/pdftoepdf.cc',True)]+[('xpdf/'+n,True) for n in xfiles]
with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool: commands=list(pool.map(one,items))
commands.append(run(['em++','-std=c++17','-O1','-c',src/'oc_synctex_memory_sink.cpp','-o',out/'sink.o'],'sink'))
exports=['malloc','free','compileLaTeX','compileBibtex','compileFormat','setMainEntry','oc_mem_configure','oc_synctex_abi','oc_synctex_status','oc_synctex_length','oc_synctex_copy','oc_font_semantics_abi','oc_font_semantics_status','oc_font_semantics_spans','oc_font_semantics_copies','oc_font_semantics_reconstructions','oc_font_semantics_current']
if (src/'oc_synctex_dvi_guard.h').is_file(): exports+=['oc_dvi_maps_abi']
commands.append(run(['em++','-O1',*sorted(out.glob('*.o')),'-sUSE_ZLIB=1','-sUSE_LIBPNG=1','-sMODULARIZE=1','-sEXPORT_NAME=createFontSemantics','-sENVIRONMENT=web,worker,node','-sINVOKE_RUN=0','-sALLOW_MEMORY_GROWTH=1','-sINITIAL_MEMORY=33554432','-sMAXIMUM_MEMORY=536870912','-sSTACK_SIZE=5242880','-sFORCE_FILESYSTEM=1','-sEXPORTED_FUNCTIONS='+json.dumps(['_'+n for n in exports]),'-sEXPORTED_RUNTIME_METHODS=["ccall","cwrap","FS","MEMFS","HEAPU8"]','-Wl,-Map,'+str(out/'link.map'),'-o',out/'engine.js'],'link'))
manifest={'schemaVersion':1,'candidate':'font semantics native1/raw SyncTeX ABI1 (private unadopted candidate)','commands':commands,'sources':{str(f.relative_to(src)):sha(f) for f in sorted(src.rglob('*')) if f.is_file()},'objects':{f.name:sha(f) for f in sorted(out.glob('*.o'))},'outputs':{n:{'length':(out/n).stat().st_size,'sha256':sha(out/n)} for n in ['engine.js','engine.wasm']},'toolchain':subprocess.check_output(['emcc','--version'],text=True,env=env).splitlines()[0]}
(out/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
print(json.dumps(manifest['outputs'],indent=2))
