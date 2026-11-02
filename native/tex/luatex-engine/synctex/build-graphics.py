from pathlib import Path
import hashlib,json,subprocess,os,shutil
import argparse
p=argparse.ArgumentParser(description='Explicit native Lua graphics1 candidate; accepted ABI5 build/default unchanged')
p.add_argument('--work',type=Path,required=True);p.add_argument('--out',type=Path,required=True);p.add_argument('--cache',type=Path,required=True)
a=p.parse_args();b=a.work.resolve();o=a.out.resolve();cache=a.cache.resolve()
for target in [b,o]:
 if target.exists():raise SystemExit('fresh graphics outputs required')
 for root in [Path('/work'),Path('/inputs'),Path('/control/profile9-runtime'),cache]:
  if target==root or root in target.parents:raise SystemExit('protected output overlap')
b.mkdir(parents=True);o.mkdir(parents=True);old=Path('/control/r2-native-geometry/build/reviewed');here=Path(__file__).resolve().parent;tl=Path('/inputs/texlive');headers=Path('/inputs/previous-lua/build/wasm');deps=Path('/control/build/wasm');w=tl/'texk/web2c';hb=headers/'texk/web2c'
def sha(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
def read_receipt(path):
 try:return json.loads(Path(path).read_text())
 except (OSError,ValueError)as error:raise SystemExit('Invalid retained native build input receipt')from error
for n,x in read_receipt('/control/r2-native-geometry/evidence/reused-link-inputs.json').items():
 p=Path(n);assert p.stat().st_size==x['length'] and sha(p)==x['sha256'],n
for n,x in read_receipt('/control/r2-native-geometry/evidence/headers-inventory.json').items():
 p=headers/n;assert p.stat().st_size==x['length'] and sha(p)==x['sha256'],n
subprocess.run(['python3',str(here/'prepare-graphics.py'),str(tl),str(b/'overlay')],check=True)
env=dict(os.environ,EM_CACHE=str(cache),EM_FROZEN_CACHE='1',EMCC_CORES='1');cmds=[]
def run(label,cmd):
 with(b/(label+'.log')).open('wb')as f:p=subprocess.run(list(map(str,cmd)),env=env,stdout=f,stderr=subprocess.STDOUT,timeout=180)
 cmds.append({'argv':list(map(str,cmd)),'exit':p.returncode});(b/'commands.json').write_text(json.dumps(cmds,indent=2)+'\n');assert not p.returncode,label
inc=['-DOC_LUATEX_ABI=5','-DOC_LUA_GRAPHICS=1','-DHAVE_CONFIG_H','-I'+str(b),'-I'+str(hb),'-I'+str(hb/'w2c'),'-I'+str(w),'-I'+str(headers/'texk'),'-I'+str(tl/'texk')]
inc+=['-I'+str(headers/('libs/'+x+'/include'))for x in ['zlib','libpng','pplib','lua53']]
inc+=['-DLUA_COMPAT_MODULE','-DLUA_COMPAT_5_2','-DLUAI_HASHLIMIT=6','-DLUA_USE_POSIX','-I'+str(w/'libmd5'),'-DpdfTeX','-I'+str(w/'luatexdir'),'-I'+str(w/'mplibdir'),'-I'+str(w/'synctexdir'),'-I/work/native/tex/synctex-producer','-I'+str(here),'-DSYNCTEX_ENGINE_H=<lua_engine.h>']
flags=['-O1','-ffile-prefix-map='+str(b/'overlay')+'=/inputs/texlive/texk/web2c','-Wno-error=incompatible-pointer-types','-Wno-error=incompatible-function-pointer-types','-Wno-error=implicit-function-declaration']
shutil.copyfile(old/'browser-luatex.a',b/'browser-luatex.a')
for name in ['synctex','pdfgen','pdfshipout','pdfliteral','pdfobj','lpdflib','luanode','pdfsaverestore','pdfsetmatrix']:
 src=b/'overlay'/('synctexdir/synctex.c'if name=='synctex'else 'luatexdir/'+('lua/'if name in ['lpdflib','luanode']else'pdf/')+name+'.c');obj=b/('libluatex_a-'+name+'.o');run(name,['emcc',*inc,*flags,'-c',src,'-o',obj]);run(name+'-archive',['emar','r',b/'browser-luatex.a',obj])
run('writer',['em++','-std=c++17','-O1','-I/work/native/tex/synctex-producer','-I'+str(here),'-c',here/'writer_geometry.cpp','-o',b/'writer_geometry.o'])
run('graphics',['em++','-std=c++17','-O1','-I/work/native/tex/synctex-producer','-I'+str(here),'-c',here/'writer_graphics.cpp','-o',b/'writer_graphics.o'])
archives=[deps/n for n in ['texk/web2c/mplibdir/luatex-lmplib.o']]+[Path('/control/build/browser-specific.a'),b/'browser-luatex.a']+[deps/n for n in ['texk/web2c/libff.a','texk/web2c/libluamisc.a','libs/lua53/.libs/libtexlua53.a','texk/web2c/libmplibcore.a','libs/zziplib/libzzip.a','libs/libpng/libpng.a','libs/pplib/libpplib.a','libs/zlib/libz.a','texk/web2c/lib/lib.a','texk/kpathsea/.libs/libkpathsea.a','texk/web2c/libmputil.a','texk/web2c/libunilib.a','texk/web2c/libmd5.a']]
exports=['malloc','free','oc_luatex_abi','oc_lua_graphics_abi','oc_luatex_version','oc_luatex_configure','oc_luatex_context','oc_luatex_add_file','oc_luatex_declare_output','oc_luatex_named_output_size','oc_luatex_copy_named_output','oc_luatex_compile_format','oc_luatex_compile_latex','oc_luatex_output_size','oc_luatex_copy_output','oc_luatex_error','oc_luatex_budget_json','oc_mem_configure','oc_synctex_abi','oc_synctex_status','oc_synctex_length','oc_synctex_copy','oc_synctex_geometry_length','oc_synctex_geometry_copy']
run('link',['em++','-O1',old/'engine.o',b/'writer_geometry.o',b/'writer_graphics.o',old/'lua_maps.o',old/'sink.o',old/'luatex-entry.o',*archives,'-Wl,--wrap=exit','-Wl,--wrap=system','-Wl,--wrap=popen','-sMODULARIZE=1','-sEXPORT_NAME=createLuaTeX','-sENVIRONMENT=web,worker,node','-sINVOKE_RUN=0','-sALLOW_MEMORY_GROWTH=1','-sINITIAL_MEMORY=33554432','-sMAXIMUM_MEMORY=536870912','-sSTACK_SIZE=5242880','-sFORCE_FILESYSTEM=1','-sFILESYSTEM=1','-sALLOW_TABLE_GROWTH=0','--pre-js',here/'../vfs-shapes.js','--pre-js',here/'../vfs-budget.js','-sEXPORTED_RUNTIME_METHODS=["ccall","cwrap","UTF8ToString","FS","HEAPU8"]','-sEXPORTED_FUNCTIONS='+json.dumps(['_'+n for n in exports]),'-o',o/'luatex.js'])

(o/'graphics-build.json').write_text(json.dumps({'schemaVersion':1,'candidate':'explicit-Lua-graphics1-not-adopted','baseAbi':5,'capability':'oc_lua_graphics_abi()==1','commands':cmds,'recipeSha256':sha(__file__),'outputs':{n:{'length':(o/n).stat().st_size,'sha256':sha(o/n)}for n in ['luatex.js','luatex.wasm']}},indent=2)+'\n')
