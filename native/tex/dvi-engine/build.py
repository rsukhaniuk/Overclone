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
geometry_mode = p.add_mutually_exclusive_group()
geometry_mode.add_argument('--geometry', action='store_true', help='separate opt-in ABI2 DVI native geometry candidate')
geometry_mode.add_argument('--xdv-geometry', action='store_true', help='separate opt-in ABI3 XDV7 native geometry candidate')
a = p.parse_args()
has_geometry = a.geometry or a.xdv_geometry
abi = 3 if a.xdv_geometry else 2 if a.geometry else 1
source, out, cache = a.source.resolve(), a.out.resolve(), a.cache.resolve()
owned = Path(__file__).resolve().parent
if out.exists():
    raise SystemExit('Refusing existing output directory; choose a fresh build identity.')
for protected in (source, cache, Path('/work'), Path('/inputs'), Path('/control/r2-dvi-maps/inputs'), Path('/control/r2-xe-maps/inputs')):
    if out == protected or protected in out.parents:
        raise SystemExit('Refusing output inside supplied source, inputs or SDK cache.')
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
geometry_sources = {}
if has_geometry:
    script = owned.parent / ('xetex-engine/converter-overlay.py' if a.xdv_geometry else 'synctex-producer/dvi-converter-overlay.py')
    run(['python3', str(script), str(source), str(out / 'geometry-overlay'), str(doc_overlay)], 'geometry-overlay')
    geometry_sources = {n: out / 'geometry-overlay' / n for n in
                        ('dpx-dvi.c', 'dpx-pdfdoc.c', 'dpx-specials.c', 'dpx-spc_pdfm.c')}
# The remaining retained bridge and engine C stay unchanged. The network-enabled
# dvipdfmx-wasm.c and its JS resolver are deliberately not compiled or linked.
commands = []
def compile_one(path):
    target = out / (path.stem + '.o')
    actual = geometry_sources.get(path.name, doc_overlay if path == doc_source else post_overlay if path == post_source else path)
    geometry_flags = ['-I' + str(owned), '-ffile-prefix-map=' + str(out) + '=/inputs/dvi-build'] if has_geometry else []
    command = ['emcc', '-O2', *geometry_flags, '-DHAVE_ZLIB', '-DHAVE_ZLIB_COMPRESS2', '-Dprintf=oc_dvi_printf', '-sUSE_LIBPNG=1', '-I' + str(source), '-c', str(actual), '-o', str(target)]
    run(command, path.stem)
    return command
with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
    commands.extend(pool.map(compile_one, sources))
cpp = owned / 'oc_dvi.cpp'
geometry_flags = ['-DOC_DVI_ABI=' + str(abi), '-ffile-prefix-map=' + str(out) + '=/inputs/dvi-build'] if has_geometry else []
command = ['em++', '-std=c++17', '-O2', *geometry_flags, '-fno-exceptions', '-fno-rtti', '-I' + str(source), '-c', str(cpp), '-o', str(out / 'oc_dvi.o')]
commands.append(run(command, 'oc_dvi'))
exports = ['_malloc', '_free'] + ['_' + n for n in [
    'oc_dvi_abi_version', 'oc_dvi_engine_version', 'oc_dvi_add_file', 'oc_dvi_run',
    'oc_dvi_status', 'oc_dvi_native_return', 'oc_dvi_error', 'oc_dvi_pdf_data',
    'oc_dvi_pdf_size', 'oc_dvi_log_data', 'oc_dvi_log_size', 'oc_dvi_log_truncated', 'oc_dvi_missing_file']]
if has_geometry:
    exports += ['_' + n for n in ['oc_dvi_geometry_configure', 'oc_dvi_geometry_data', 'oc_dvi_geometry_size', 'oc_dvi_geometry_length', 'oc_dvi_geometry_copy']]
command = ['em++', '-O2', *map(str, sorted(out.glob('*.o'))) , '-sUSE_LIBPNG=1', '-sMODULARIZE=1', '-sEXPORT_NAME=createOcDvi', '-sENVIRONMENT=web,worker,node', '-sFILESYSTEM=0', '-sALLOW_MEMORY_GROWTH=1', '-sINITIAL_MEMORY=33554432', '-sMAXIMUM_MEMORY=536870912', '-sSTACK_SIZE=2097152', '-sNO_EXIT_RUNTIME=1', '-sEXPORTED_FUNCTIONS=' + json.dumps(exports), '-sEXPORTED_RUNTIME_METHODS=["HEAPU8"]', '-Wl,-Map,' + str(out / 'link.map'), '-o', str(out / 'oc-dvi.js')]
commands.append(run(command, 'link'))
manifest = {
    'abi': abi,
    'engine': 'oc-dvi-3/swiftlatex-tectonic-dvipdfmx/xdv-geometry1' if a.xdv_geometry else 'oc-dvi-2/swiftlatex-tectonic-dvipdfmx/geometry1' if a.geometry else 'oc-dvi-1/swiftlatex-tectonic-dvipdfmx',
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
if has_geometry:
    manifest['geometry'] = {
        'schemaVersion': 1, 'maxPages': 512, 'maxBytes': 131072,
        'defaultEnabled': False, 'runtimeProfileAllocated': False,
        'hookScriptSha256': sha(owned.parent / ('xetex-engine/converter-overlay.py' if a.xdv_geometry else 'synctex-producer/dvi-converter-overlay.py')),
        'associationSha256': sha(out / 'geometry-overlay/association.json'),
        'actualOverlaySources': {n: sha(path) for n, path in geometry_sources.items()},
    }
(out / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
print(json.dumps({'candidate': str(out), 'outputs': manifest['outputs']}, indent=2))
