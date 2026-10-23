#!/usr/bin/env python3
"""Offline XeTeX build. All generated/vendor bytes stay in the private build root."""
import argparse
import concurrent.futures
import hashlib
import json
import os
from pathlib import Path
import resource
import shutil
import subprocess
import time

HERE = Path(__file__).resolve().parent
p = argparse.ArgumentParser()
p.add_argument('--inputs', type=Path, default=Path('/inputs'))
p.add_argument('--build', type=Path, default=Path('/control/build/xetex'))
p.add_argument('--stage', choices=['deps', 'engine', 'all'], default='all')
a = p.parse_args()
B = a.build.resolve(); B.mkdir(parents=True, exist_ok=True)
I = a.inputs.resolve(); TL = I/'texlive'; PORT = I/'browser-ports/source/xetex.wasm'
CACHE = B.parent/'cache'
if not CACHE.exists():
    shutil.copytree(I/'sdk-cache', CACHE)
os.environ.update(EM_CACHE=str(CACHE), EM_FROZEN_CACHE='1', EMCC_CORES='2')
for key in ['http_proxy', 'https_proxy', 'all_proxy', 'HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY']:
    os.environ[key] = 'http://127.0.0.1:9'
os.environ.update(no_proxy='', NO_PROXY='')
resource.setrlimit(resource.RLIMIT_AS, (3*1024**3, 3*1024**3))
CC = shutil.which('emcc'); CXX = shutil.which('em++')
assert CC and CXX

def run(name, cmd, cwd=None, timeout=180):
    start = time.time()
    with (B/(name+'.log')).open('w') as log:
        result = subprocess.run([str(x) for x in cmd], cwd=cwd, stdout=log,
                                stderr=subprocess.STDOUT, timeout=timeout)
    with (B/'commands.jsonl').open('a') as f:
        f.write(json.dumps(dict(name=name, command=[str(x) for x in cmd],
                               cwd=str(cwd or Path.cwd()), exit=result.returncode,
                               seconds=round(time.time()-start, 3)))+'\n')
    if result.returncode:
        raise RuntimeError(f'{name}: exit {result.returncode}; see {B/name}.log')

def units(name, sources, flags):
    dest = B/name; dest.mkdir(exist_ok=True)
    def unit(src):
        obj = dest/(src.name+'.o')
        command = [CC if src.suffix == '.c' else CXX, '-c', '-O2', '-fno-exceptions',
                   '-Wno-deprecated-declarations', '-Wno-macro-redefined', *flags, src, '-o', obj]
        # Private-build cache only: this stamp hashes the command and translation
        # unit, NOT included headers. Reuse assumes every included header (vendor,
        # generated or owned) stays byte-identical in this task's private input set.
        # After any included-header change, remove affected objects/stamps or use
        # a fresh build directory; this is not general header dependency tracking.
        sig = hashlib.sha256((str(command)+hashlib.sha256(src.read_bytes()).hexdigest()).encode()).hexdigest()
        stamp = obj.with_suffix('.sha256')
        if not obj.exists() or not stamp.exists() or stamp.read_text() != sig:
            run(name+'-'+src.name, command)
            stamp.write_text(sig)
        return obj
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        objs = list(pool.map(unit, sources))
    archive = B/(name+'.a')
    run(name+'-archive', ['/emsdk/upstream/emscripten/emar', 'rcs', archive, *objs])
    return archive

def cmake(name, source, options):
    dest = B/name
    toolchain = B/'offline.cmake'
    toolchain.write_text('include(/emsdk/upstream/emscripten/cmake/Modules/Platform/Emscripten.cmake)\n'
                         + f'set(CMAKE_C_COMPILER {CC})\nset(CMAKE_CXX_COMPILER {CXX})\n')
    run(name+'-configure', ['cmake', '-S', source, '-B', dest,
        '-DCMAKE_TOOLCHAIN_FILE='+str(toolchain),
        '-DCMAKE_BUILD_TYPE=Release', '-DBUILD_SHARED_LIBS=OFF', *options])
    run(name+'-build', ['cmake', '--build', dest, '--parallel', '2'], timeout=600)

icu = TL/'libs/icu/icu-src/source'
ft = TL/'libs/freetype2/freetype-src'
gr = TL/'libs/graphite2/graphite2-src'
hb = TL/'libs/harfbuzz/harfbuzz-src'
hbinc = B/'hbinclude'; hbinc.mkdir(exist_ok=True)
# Generate the dependency's normal version header from its supplied template.
assert '[2.7.4]' in (hb/'configure.ac').read_text()
version_header = (hb/'src/hb-version.h.in').read_text()
for key, value in {'HB_VERSION_MAJOR': '2', 'HB_VERSION_MINOR': '7', 'HB_VERSION_MICRO': '4', 'HB_VERSION': '2.7.4'}.items():
    version_header = version_header.replace('@'+key+'@', value)
(hbinc/'hb-version.h').write_text(version_header)
if a.stage in ('deps', 'all'):
    cmake('freetype', ft, ['-DCMAKE_DISABLE_FIND_PACKAGE_ZLIB=TRUE',
        '-DCMAKE_DISABLE_FIND_PACKAGE_BZip2=TRUE', '-DCMAKE_DISABLE_FIND_PACKAGE_PNG=TRUE',
        '-DCMAKE_DISABLE_FIND_PACKAGE_HarfBuzz=TRUE', '-DCMAKE_DISABLE_FIND_PACKAGE_BrotliDec=TRUE'])
    grbuild = B/'graphite-source'
    shutil.copytree(gr, grbuild, dirs_exist_ok=True)
    grcmake = grbuild/'CMakeLists.txt'
    grcmake.write_text(grcmake.read_text().replace('add_subdirectory(tests)', '').replace('add_subdirectory(doc)', ''))
    cmake('graphite', grbuild, ['-DGRAPHITE2_NFILEFACE=ON', '-DGRAPHITE2_NTRACING=ON'])
    icflags = ['-I'+str(icu/'common'), '-I'+str(icu/'i18n'), '-DU_STATIC_IMPLEMENTATION',
               '-DU_USING_ICU_NAMESPACE=0', '-DU_HAVE_NL_LANGINFO_CODESET=0']
    units('icu-common', sorted((icu/'common').glob('*.cpp')), icflags+['-DU_COMMON_IMPLEMENTATION=1'])
    units('icu-i18n', sorted((icu/'i18n').glob('*.cpp')), icflags+['-DU_I18N_IMPLEMENTATION=1'])
    units('icu-stub', sorted((icu/'stubdata').glob('*.cpp')), icflags)
    # XeTeX supplies its own FreeType font callbacks (no hb_ft_* references).
    # Keep real ICU/Graphite adapters; do not compile the unused hb-ft adapter,
    # whose old callback casts are rejected by this pinned modern Clang.
    units('harfbuzz', [hb/'src/harfbuzz.cc', hb/'src/hb-icu.cc'],
        ['-DHB_NO_MT=1', '-DHAVE_GRAPHITE2=1', '-DHAVE_ICU=1', '-I'+str(hbinc), '-I'+str(hb/'src'),
         '-DU_STATIC_IMPLEMENTATION', '-I'+str(icu/'common'), '-I'+str(icu/'i18n'),
         '-I'+str(ft/'include'), '-I'+str(B/'freetype/include'), '-I'+str(gr/'include')])
    xp = B/'xpdf'; xp.mkdir(exist_ok=True)
    run('xpdf-configure', [TL/'libs/xpdf/configure', '--host=wasm32-unknown-emscripten',
        '--build=x86_64-pc-linux-gnu', '--disable-shared', '--without-x',
        'CC='+CC, 'CXX='+CXX, 'CXXFLAGS=-O2 -fno-exceptions'], cwd=xp)
    run('xpdf-build', ['make', '-j2'], cwd=xp, timeout=600)
if a.stage in ('engine', 'all'):
    run('prepare', ['python3', HERE/'prepare.py', PORT, B/'source'])
    src = B/'source'
    flags = ['-DWEBASSEMBLY_BUILD', '-DU_STATIC_IMPLEMENTATION', '-Wno-pointer-sign',
        '-Wno-parentheses-equality', '-I'+str(src), '-I'+str(src/'tex'),
        '-I'+str(src/'xetexdir'), '-I'+str(src/'teckit'), '-I'+str(hb/'src'), '-I'+str(hbinc),
        '-I'+str(icu/'common'), '-I'+str(icu/'i18n'), '-I'+str(ft/'include'),
        '-I'+str(B/'freetype/include'), '-I'+str(gr/'include'), '-sUSE_LIBPNG=1',
        '-I'+str(B/'xpdf'), '-I'+str(TL/'libs/xpdf/xpdf-src'),
        '-I'+str(TL/'libs/xpdf/xpdf-src/goo'), '-I'+str(TL/'libs/xpdf/xpdf-src/xpdf')]
    cfiles = ['tex/xetex0.c', 'tex/xetexini.c', 'tex/xetex-pool.c', 'md5.c',
        'xmemory.c', 'texfile.c', 'kpseemu.c', 'texmfmp.c', 'main.c', 'bibtex.c',
        'xetexdir/XeTeX_ext.c', 'xetexdir/XeTeX_pic.c', 'xetexdir/image/bmpimage.c',
        'xetexdir/image/jpegimage.c', 'xetexdir/image/pngimage.c', 'xetexdir/trans.c']
    cppfiles = ['xetexdir/XeTeXOTMath.cpp', 'xetexdir/XeTeXLayoutInterface.cpp',
        'xetexdir/XeTeXFontMgr.cpp', 'xetexdir/XeTeXFontInst.cpp',
        'xetexdir/XeTeXFontMgr_FC.cpp', 'xetexdir/hz.cpp', 'xetexdir/pdfimage.cpp',
        'teckit/teckit-Engine.cpp']
    objects = units('engine', [src/x for x in cfiles+cppfiles]+[HERE/'runtime.cpp'], flags)
    run('link', [CXX, '-O2', '-o', B/'xetex.js', '-Wl,--whole-archive', objects,
        '-Wl,--no-whole-archive', B/'harfbuzz.a', B/'graphite/src/libgraphite2.a',
        B/'freetype/libfreetype.a', B/'xpdf/libxpdf.a',
        B/'icu-i18n.a', B/'icu-common.a', B/'icu-stub.a',
        '-sUSE_LIBPNG=1', '-sMODULARIZE=1', '-sEXPORT_NAME=createXeTeX',
        '-sENVIRONMENT=web,worker,node', '-sALLOW_MEMORY_GROWTH=1', '-sMAXIMUM_MEMORY=536870912',
        '-sINITIAL_MEMORY=33554432', '-sSTACK_SIZE=5242880', '-sFORCE_FILESYSTEM=1',
        '-sINVOKE_RUN=0', '-sEXIT_RUNTIME=0',
        '-sEXPORTED_FUNCTIONS=["_compileFormat","_compileLaTeX","_setMainEntry","_compileBibtex","_malloc","_free"]',
        '-sEXPORTED_RUNTIME_METHODS=["FS","ccall","cwrap"]'], timeout=180)
