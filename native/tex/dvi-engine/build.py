#!/usr/bin/env python3
"""Offline checked build. Never runs upstream Makefiles or modifies input sources."""
import argparse
import concurrent.futures
import hashlib
import json
import os
from pathlib import Path
import subprocess

p = argparse.ArgumentParser()
p.add_argument('--source', type=Path, required=True)
p.add_argument('--out', type=Path, required=True)
p.add_argument('--cache', type=Path, required=True)
a = p.parse_args()
source, out, cache = a.source.resolve(), a.out.resolve(), a.cache.resolve()
owned = Path(__file__).resolve().parent
if out.exists():
    raise SystemExit('Refusing existing output directory; choose a fresh build identity.')
out.mkdir(parents=True)
env = dict(os.environ, EM_CACHE=str(cache))

def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def run(command, label):
    with (out / (label + '.log')).open('wb') as log:
        result = subprocess.run(command, stdout=log, stderr=subprocess.STDOUT, env=env, timeout=180)
    if result.returncode:
        raise RuntimeError(f'{label} failed ({result.returncode}); see {out / (label + ".log")}')
    return command

sources = sorted(source.glob('dpx-*.c')) + [source / n for n in ['core-bridge.c', 'core-memory.c', 'md5.c']]
# Preserve the native page dimensions while spelling out inherited boxes for
# consumers whose public box API does not resolve the parent page tree.
doc_source = source / 'dpx-pdfdoc.c'
if sha(doc_source) != '66550979ce87335cd139fb6f55e40b91eac390c7fed8b38e99c9f4358ebaa370':
    raise RuntimeError('Unexpected retained PDF document writer preimage.')
doc_text = doc_source.read_text()
start = doc_text.index('  if (page->flags & USE_MY_MEDIABOX) {', doc_text.index('doc_flush_page ('))
end = doc_text.index('\n  count = 0;', start)
original = doc_text[start:end]
patched = original.replace('  if (page->flags & USE_MY_MEDIABOX) {',
    '  {\n    const pdf_rect *resolved_box = (page->flags & USE_MY_MEDIABOX)\n'
    '      ? &page->cropbox : &p->pages.mediabox;')
patched = patched.replace('page->cropbox.', 'resolved_box->')
doc_overlay = out / 'dpx-pdfdoc.c'
doc_overlay.write_text(doc_text[:start] + patched + doc_text[end:])
# Glyph-name pointers are indexed by glyph ID and have numberOfGlyphs entries.
# count bounds only the separate custom-name storage. Confusing the two loses
# names for high glyph IDs, including genuine DejaVu fi/ffl ligatures.
post_source = source / 'dpx-tt_post.c'
if sha(post_source) != 'fe1f8f77b8f208ece6e4da70a8fbe0eab480e9a84c5fc8d0b7a92141adccfbb7':
    raise RuntimeError('Unexpected retained TrueType post-table preimage.')
post_text = post_source.read_text()
post_patched = post_text.replace('gid < post->count', 'gid < post->numberOfGlyphs')
if post_text.count('gid < post->count') != 2:
    raise RuntimeError('Unexpected retained glyph-name lookup shape.')
post_overlay = out / 'dpx-tt_post.c'
post_overlay.write_text(post_patched)
# The remaining retained bridge and engine C stay unchanged. The network-enabled
# dvipdfmx-wasm.c and its JS resolver are deliberately not compiled or linked.
commands = []
def compile_one(path):
    target = out / (path.stem + '.o')
    actual = doc_overlay if path == doc_source else post_overlay if path == post_source else path
    command = ['emcc', '-O2', '-DHAVE_ZLIB', '-DHAVE_ZLIB_COMPRESS2', '-Dprintf=oc_dvi_printf', '-sUSE_LIBPNG=1', '-I' + str(source), '-c', str(actual), '-o', str(target)]
    run(command, path.stem)
    return command
with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
    commands.extend(pool.map(compile_one, sources))
cpp = owned / 'oc_dvi.cpp'
command = ['em++', '-std=c++17', '-O2', '-fno-exceptions', '-fno-rtti', '-I' + str(source), '-c', str(cpp), '-o', str(out / 'oc_dvi.o')]
commands.append(run(command, 'oc_dvi'))
exports = ['_malloc', '_free'] + ['_' + n for n in [
    'oc_dvi_abi_version', 'oc_dvi_engine_version', 'oc_dvi_add_file', 'oc_dvi_run',
    'oc_dvi_status', 'oc_dvi_native_return', 'oc_dvi_error', 'oc_dvi_pdf_data',
    'oc_dvi_pdf_size', 'oc_dvi_log_data', 'oc_dvi_log_size', 'oc_dvi_log_truncated', 'oc_dvi_missing_file']]
command = ['em++', '-O2', *map(str, sorted(out.glob('*.o'))), '-sUSE_LIBPNG=1', '-sMODULARIZE=1', '-sEXPORT_NAME=createOcDvi', '-sENVIRONMENT=web,worker,node', '-sFILESYSTEM=0', '-sALLOW_MEMORY_GROWTH=1', '-sINITIAL_MEMORY=33554432', '-sMAXIMUM_MEMORY=536870912', '-sSTACK_SIZE=2097152', '-sNO_EXIT_RUNTIME=1', '-sEXPORTED_FUNCTIONS=' + json.dumps(exports), '-sEXPORTED_RUNTIME_METHODS=["HEAPU8"]', '-Wl,-Map,' + str(out / 'link.map'), '-o', str(out / 'oc-dvi.js')]
commands.append(run(command, 'link'))
manifest = {
    'abi': 1, 'engine': 'oc-dvi-1/swiftlatex-tectonic-dvipdfmx',
    'commands': commands,
    'inputs': {str(path): sha(path) for path in sorted(source.glob('*.h')) + sources + [cpp, owned / 'oc_dvi.h', Path(__file__).resolve()]},
    'sourceOverlay': {'original': sha(doc_source), 'patched': sha(doc_overlay),
                      'change': 'Explicit resolved MediaBox on each page; native per-page/root values unchanged'},
    'fontNameOverlay': {'original': sha(post_source), 'patched': sha(post_overlay),
                        'change': 'Bound glyph-name pointer lookups by numberOfGlyphs, not custom-name count'},
    'objects': {path.name: sha(path) for path in sorted(out.glob('*.o'))},
    'linkMapSha256': sha(out / 'link.map'),
    'sdkLibraries': {str(path.relative_to(cache)): sha(path) for path in sorted((cache / 'sysroot/lib/wasm32-emscripten').glob('*.a'))},
    'logs': {path.name: sha(path) for path in sorted(out.glob('*.log'))},
    'outputs': {path.name: {'sha256': sha(path), 'bytes': path.stat().st_size} for path in [out / 'oc-dvi.js', out / 'oc-dvi.wasm']},
    'toolchain': subprocess.check_output(['emcc', '--version'], env=env, text=True).splitlines()[0],
    'bounds': {'projectBytes': 8388608, 'resourceBytes': 100663296, 'fileBytes': 33554432, 'files': 4096, 'pdfBytes': 8388608, 'logBytes': 262144, 'maximumMemory': 536870912},
    'virtualResourceAlias': '/tex/<flat-basename> (resource-only)',
}
(out / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
print(json.dumps({'candidate': str(out), 'outputs': manifest['outputs']}, indent=2))
