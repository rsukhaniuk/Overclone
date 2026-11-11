#!/usr/bin/env python3
"""Fresh non-BMP Xe compilation with byte-reproved accepted dependency archives.

Run offline in the existing Emscripten Docker toolchain, with a private SDK cache.
"""
import argparse
import concurrent.futures
import hashlib
import json
import os
from pathlib import Path
import shlex
import subprocess

parser = argparse.ArgumentParser(description=__doc__)
for name in ['prepared', 'dependencies', 'retained', 'texlive', 'cache', 'out']:
    parser.add_argument('--' + name, type=Path, required=True)
args = parser.parse_args()
prepared, deps, retained, tl, cache, out = (
    getattr(args, name).resolve()
    for name in ['prepared', 'dependencies', 'retained', 'texlive', 'cache', 'out'])
if out.exists():
    raise SystemExit('fresh output required')
source = prepared / 'source'


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


preparation = json.loads((prepared / 'manifest.json').read_text())
work_files = preparation.get('combinedWorkFileLimit', 192)
if work_files not in (192, 256):
    raise SystemExit('unsupported explicit work-file contract')
for name, expected in preparation['source'].items():
    if sha(source / name) != expected:
        raise SystemExit('prepared source mismatch: ' + name)
proof = json.loads((deps / 'proof.json').read_text())
if proof['status'] != 'pass' or len(proof['units']) != 563:
    raise SystemExit('complete reviewed dependency proof required')
for group, item in proof['archives'].items():
    if sha(deps / (group + '.a')) != item['actualSha256']:
        raise SystemExit('dependency archive mismatch: ' + group)
for item in proof['units'].values():
    if sha(item['source']) != item['sourceSha256']:
        raise SystemExit('dependency source drift: ' + item['source'])
    for name, expected in item['headers'].items():
        if sha(name) != expected:
            raise SystemExit('dependency header drift: ' + name)

out.mkdir(parents=True)
env = dict(os.environ, EM_CACHE=str(cache), EM_FROZEN_CACHE='1', EMCC_CORES='1')


def run(command, label):
    command = list(map(str, command))
    with (out / (label + '.log')).open('wb') as log:
        result = subprocess.run(command, env=env, stdout=log, stderr=subprocess.STDOUT, timeout=180)
    if result.returncode:
        raise RuntimeError(label + ' failed: ' + str(out / (label + '.log')))
    return command


icu = tl / 'libs/icu/icu-src/source'
ft = tl / 'libs/freetype2/freetype-src'
hb = tl / 'libs/harfbuzz/harfbuzz-src'
graphite = tl / 'libs/graphite2/graphite2-src'
flags = ['-O2', '-fno-exceptions', '-DWEBASSEMBLY_BUILD', '-D__SyncTeX__',
         '-DU_STATIC_IMPLEMENTATION', '-Wno-deprecated-declarations',
         '-Wno-macro-redefined', '-Wno-pointer-sign', '-Wno-parentheses-equality', '-sUSE_LIBPNG=1']
includes = [source, source / 'tex', source / 'xetexdir', source / 'teckit', hb / 'src',
            retained / 'hbinclude', icu / 'common', icu / 'i18n', ft / 'include',
            retained / 'freetype/include', graphite / 'include', retained / 'xpdf',
            tl / 'libs/xpdf/xpdf-src', tl / 'libs/xpdf/xpdf-src/goo', tl / 'libs/xpdf/xpdf-src/xpdf']
flags += ['-I' + str(p) for p in includes]
flags += ['-ffile-prefix-map=' + str(source) + '=/inputs/xe-nonbmp-port',
          '-ffile-prefix-map=' + str(out) + '=/inputs/xe-nonbmp-build',
          '-ffile-prefix-map=' + str(retained) + '=/inputs/xe-retained-dependencies']
c_names = ['tex/xetex0.c', 'tex/xetexini.c', 'tex/xetex-pool.c', 'md5.c', 'xmemory.c',
           'texfile.c', 'kpseemu.c', 'texmfmp.c', 'main.c', 'bibtex.c',
           'xetexdir/XeTeX_ext.c', 'xetexdir/XeTeX_pic.c', 'xetexdir/image/bmpimage.c',
           'xetexdir/image/jpegimage.c', 'xetexdir/image/pngimage.c', 'xetexdir/trans.c',
           'synctexdir/synctex.c', 'oc_synctex_port.c']
cpp_names = ['xetexdir/XeTeXOTMath.cpp', 'xetexdir/XeTeXLayoutInterface.cpp',
             'xetexdir/XeTeXFontMgr.cpp', 'xetexdir/XeTeXFontInst.cpp',
             'xetexdir/XeTeXFontMgr_FC.cpp', 'xetexdir/hz.cpp', 'xetexdir/pdfimage.cpp',
             'teckit/teckit-Engine.cpp', 'oc_synctex_memory_sink.cpp', 'oc_nonbmp_runtime.cpp']


def compile_one(path):
    name = path.name
    dependency_file = out / (name + '.d')
    extra = ['-DSYNCTEX_ENGINE_H=<oc_synctex_engine.h>'] if name == 'synctex.c' else []
    command = run(['emcc' if path.suffix == '.c' else 'em++', *flags, *extra, '-c', path,
                   '-MD', '-MF', dependency_file, '-o', out / (name + '.o')], name)
    paths = shlex.split(dependency_file.read_text().split(':', 1)[1].replace('\\\n', ' '))
    return {'source': str(path), 'sourceSha256': sha(path), 'command': command,
            'headers': {name: sha(name) for name in paths},
            'objectSha256': sha(out / (name + '.o'))}


with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
    units = list(pool.map(compile_one, [source / n for n in c_names + cpp_names]))
exports = ['compileFormat', 'compileLaTeX', 'compileBibtex', 'setMainEntry', 'malloc', 'free',
           'oc_xe_maps_abi', 'oc_mem_configure', 'oc_synctex_abi', 'oc_synctex_status',
           'oc_synctex_length', 'oc_synctex_copy', 'oc_xe_nonbmp_abi']
if work_files == 256:
    exports.append('oc_xe_work_file_limit')
archives = [deps / (name + '.a') for name in
            ['harfbuzz', 'graphite', 'freetype', 'xpdf', 'icu-i18n', 'icu-common', 'icu-stub']]
link = run(['em++', '-O2', *sorted(out.glob('*.o')), *archives, '-sUSE_LIBPNG=1',
            '-sMODULARIZE=1', '-sEXPORT_NAME=createXeMaps', '-sENVIRONMENT=web,worker,node',
            '-sALLOW_MEMORY_GROWTH=1', '-sMAXIMUM_MEMORY=536870912', '-sINITIAL_MEMORY=33554432',
            '-sSTACK_SIZE=5242880', '-sFORCE_FILESYSTEM=1', '-sINVOKE_RUN=0', '-sEXIT_RUNTIME=0',
            '-sEXPORTED_FUNCTIONS=' + json.dumps(['_' + name for name in exports]),
            '-sEXPORTED_RUNTIME_METHODS=["FS","MEMFS","ccall","cwrap","HEAPU8"]',
            '-Wl,-Map,' + str(out / 'link.map'), '-o', out / 'engine.js'], 'link')
manifest = {
    'schemaVersion': 1, 'capability': 'candidate-only genuine Xe non-BMP filenames ABI1',
    'recipeSha256': sha(__file__), 'preparationManifestSha256': sha(prepared / 'manifest.json'),
    'dependencyProofSha256': sha(deps / 'proof.json'), 'combinedWorkFileLimit': work_files,
    'dependencies': {str(path): sha(path) for path in archives}, 'units': units, 'link': link,
    'outputs': {name: {'length': (out / name).stat().st_size, 'sha256': sha(out / name)}
                for name in ['engine.js', 'engine.wasm']}}
(out / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
print(json.dumps(manifest['outputs'], indent=2))
