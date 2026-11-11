#!/usr/bin/env python3
"""Offline source-pinned query build, optionally correcting wasm32 area overflow."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shlex
import subprocess
import sys

p = argparse.ArgumentParser(description=__doc__)
for name in ['source', 'cache', 'out']:
    p.add_argument('--' + name, required=True, type=Path)
p.add_argument('--area64', action='store_true')
a = p.parse_args()
source, out = a.source.resolve(), a.out.resolve()
assert not out.exists(), 'fresh output required'
out.mkdir(parents=True)
here = Path(__file__).resolve().parent
sha = lambda f: hashlib.sha256(Path(f).read_bytes()).hexdigest()
assert sha(source / 'synctex_parser.c') == 'fa8bb24365d1f92c2fe7d75aac0abeda79222d6398737263e8092656c91febc0'
assert sha(source / 'synctex_parser_utils.c') == 'f5395f741c4de96790c76d2287d9e427ae74f354984a9bb2719d7ca51ecc1a37'
env = dict(os.environ, EM_CACHE=str(a.cache.resolve()), EM_FROZEN_CACHE='1', EMCC_CORES='1')
commands = []
def run(label, command):
    command = list(map(str, command))
    with (out / (label + '.log')).open('wb') as log:
        r = subprocess.run(command, env=env, cwd=out, stdout=log, stderr=subprocess.STDOUT, timeout=120)
    commands.append({'label': label, 'argv': command, 'exit': r.returncode})
    (out / 'commands.json').write_text(json.dumps(commands, indent=2) + '\n')
    assert not r.returncode, label
parser = source / 'synctex_parser.c'
if a.area64:
    parser = out / 'synctex_parser.c'
    run('overlay', [sys.executable, here / 'area64.py', source / 'synctex_parser.c', parser])
objects = []
for name, file in [('synctex_parser', parser), ('synctex_parser_utils', source / 'synctex_parser_utils.c')]:
    obj = out / (name + '.o')
    run(name, ['emcc', '-O2', '-MMD', '-s', 'USE_ZLIB=1', '-I' + str(source), '-c', file, '-o', obj])
    objects.append(obj)
obj = out / 'query.o'
run('query', ['em++', '-std=c++17', '-O2', '-MMD', '-Wall', '-Wextra', '-Werror', '-I' + str(source), '-c', here / 'synctex_query.cpp', '-o', obj])
objects.insert(0, obj)
exports = ['_malloc', '_free', '_sq_version', '_sq_status_code', '_sq_error_message', '_sq_load', '_sq_forward', '_sq_inverse', '_sq_result_length', '_sq_result_copy', '_sq_close']
if a.area64:
    obj = out / 'area64-capability.o'
    run('capability', ['em++', '-std=c++17', '-O2', '-c', here / 'area64-capability.cpp', '-o', obj])
    objects.append(obj)
    exports.append('_sq_area64_abi')
run('link', ['em++', '-O2', *objects, '-s', 'USE_ZLIB=1', '-s', 'MODULARIZE=1', '-s', 'EXPORT_NAME=createSyncTeXQuery', '-s', 'ENVIRONMENT=worker,node', '-s', 'FORCE_FILESYSTEM=1', '-s', 'ALLOW_MEMORY_GROWTH=1', '-s', 'MAXIMUM_MEMORY=268435456', '-s', 'INITIAL_MEMORY=16777216', '-s', 'DYNAMIC_EXECUTION=0', '-s', 'EXPORTED_FUNCTIONS=' + json.dumps(exports), '-s', 'EXPORTED_RUNTIME_METHODS=["FS", "HEAPU8"]', '-Wl,-Map,' + str(out / 'query.map'), '-o', 'synctex-query.js'])
headers = {}
for dep in out.glob('*.d'):
    for name in shlex.split(dep.read_text().replace('\\\n', '').split(':', 1)[1]):
        headers[name] = sha(name)
manifest = {'schemaVersion': 1, 'status': 'native-candidate-not-adopted', 'queryAbi': 1, 'area64': a.area64, 'commands': commands, 'sources': {str(f): sha(f) for f in [Path(__file__), here / 'area64.py', here / 'area64-capability.cpp', here / 'synctex_query.cpp', source / 'synctex_parser.c', source / 'synctex_parser_utils.c', parser]}, 'headers': headers, 'objects': {f.name: sha(f) for f in objects}, 'outputs': {n: {'length': (out / n).stat().st_size, 'sha256': sha(out / n)} for n in ['synctex-query.js', 'synctex-query.wasm']}}
(out / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
print(json.dumps(manifest['outputs']))
