#!/usr/bin/env python3
"""Compose accepted actual-origin witnesses onto the exact P14 LuaHB lineage.

Retain HarfBuzz, semantic kern correction, output declarations256 and VFS guards.
Only native page/root writers and the geometry observer gain origin support.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shlex
import shutil
import subprocess
import sys

p = argparse.ArgumentParser(description=__doc__)
for name in ['source', 'parent', 'graphics-build', 'cache', 'work', 'out']:
    p.add_argument('--' + name, type=Path, required=True)
a = p.parse_args()
work, out = a.work.resolve(), a.out.resolve()
assert not work.exists() and not out.exists(), 'fresh outputs required'
work.mkdir(parents=True)
out.mkdir(parents=True)
here = Path(__file__).resolve().parent
lua = here.parent / 'luatex-engine/synctex'
sha = lambda f: hashlib.sha256(Path(f).read_bytes()).hexdigest()
pin = lambda f: {'length': Path(f).stat().st_size, 'sha256': sha(f)}
parent = json.loads((a.parent / 'semantics-build.json').read_text())
expected = {'luatex.js': {'length': 193516, 'sha256': '0cba8f1f2f5a71d703fe80300521dbc4c21d54d016845fa2181cc77f666bb224'}, 'luatex.wasm': {'length': 6013393, 'sha256': '406aa23eb3c799b7c1cc0cbb3fa46ab1681880705ed30de35dd288c14576a4d9'}}
assert parent['outputs'] == expected
for name, wanted in expected.items():
    assert pin(a.parent / name) == wanted
link = parent['commands'][-1]['argv']
assert link[0] == 'em++' and link[-2] == '-o'
linked = [Path(s) for s in link if s.endswith(('.o', '.a'))]
inputs = {str(f): pin(f) for f in linked}
commands = []
env = dict(os.environ, EM_CACHE=str(a.cache.resolve()), EM_FROZEN_CACHE='1', EMCC_CORES='1')
def run(label, command):
    command = list(map(str, command))
    with (work / (label + '.log')).open('wb') as log:
        r = subprocess.run(command, env=env, stdout=log, stderr=subprocess.STDOUT, timeout=180)
    commands.append({'label': label, 'argv': command, 'exit': r.returncode})
    (work / 'commands.json').write_text(json.dumps(commands, indent=2) + '\n')
    assert not r.returncode, label
# Re-link the exact retained objects first: stale or mismatched archives fail.
baseline = work / 'baseline'
baseline.mkdir()
relink = link[:-1] + [str(baseline / 'luatex.js')]
run('parent-exact-relink', relink)
for name, wanted in expected.items():
    assert pin(baseline / name) == wanted, name
run('origin-source', [sys.executable, lua / 'prepare-origin.py', a.source, work / 'overlay'])
base_commands = json.loads((a.graphics_build / 'commands.json').read_text())
compile = next(c['argv'] for c in base_commands if c['argv'][0] == 'emcc' and any(s.endswith('/pdfgen.c') for s in c['argv']))
oldsrc, oldobj = compile[compile.index('-c') + 1], compile[compile.index('-o') + 1]
archive = next(f for f in linked if f.name == 'browser-luatex.a')
newarchive = work / 'browser-luatex.a'
shutil.copyfile(archive, newarchive)
for name in ['pdfgen', 'pdfpagetree']:
    src, obj = work / ('overlay/luatexdir/pdf/' + name + '.c'), work / ('libluatex_a-' + name + '.o')
    cmd = [str(src) if s == oldsrc else str(obj) if s == oldobj else s for s in compile]
    cmd += ['-MD', '-MF', str(work / (name + '.d')), '-ffile-prefix-map=' + str(work / 'overlay') + '=/inputs/texlive/texk/web2c']
    run(name, cmd)
    run(name + '-archive', ['emar', 'r', newarchive, obj])
def members(file):
    raw, at, names, result = file.read_bytes(), 8, b'', []
    assert raw.startswith(b'!<arch>\n')
    while at < len(raw):
        header = raw[at:at + 60]
        assert len(header) == 60 and header[58:] == b'`\n'
        size, name = int(header[48:58]), header[:16].decode().strip()
        data = raw[at + 60:at + 60 + size]
        assert len(data) == size
        at += 60 + size + size % 2
        if name == '//':
            names = data
            continue
        if name in ['/', '/SYM64/']:
            continue
        if name.startswith('/') and name[1:].isdigit():
            offset = int(name[1:])
            name = names[offset:names.index(b'/\n', offset)].decode()
        else:
            name = name.removesuffix('/')
        result.append({'name': name, 'sha256': hashlib.sha256(data).hexdigest()})
    return result
before, after = members(archive), members(newarchive)
assert [r['name'] for r in before] == [r['name'] for r in after]
changed = sorted(left['name'] for left, right in zip(before, after) if left != right)
assert changed == ['libluatex_a-pdfgen.o', 'libluatex_a-pdfpagetree.o'], changed
observer = work / 'writer_origin.o'
run('origin-observer', ['em++', '-std=c++17', '-O1', '-MMD', '-I' + str(here.parent / 'synctex-producer'), '-I' + str(lua), '-c', lua / 'writer_origin.cpp', '-o', observer])
final = [str(newarchive) if s == str(archive) else str(observer) if s.endswith('/writer_geometry.o') else str(out / 'luatex.js') if s == link[-1] else s for s in link]
exports = next(s for s in final if s.startswith('-sEXPORTED_FUNCTIONS='))
symbols = json.loads(exports.split('=', 1)[1])
symbols.append('_oc_lua_origin_abi')
final[final.index(exports)] = '-sEXPORTED_FUNCTIONS=' + json.dumps(symbols)
run('link-origin-luahb', final)
assert inputs == {str(f): pin(f) for f in linked}, 'retained P14 inputs changed'
headers = {}
for dep in work.glob('*.d'):
    for f in shlex.split(dep.read_text().replace('\\\n', '').split(':', 1)[1]):
        headers[f] = pin(f)
receipt = {'schemaVersion': 1, 'status': 'native-candidate-not-adopted', 'proposedProfile': 15, 'capabilities': {'abi': 5, 'graphics': 1, 'harfbuzz': 1, 'fontClosure': 1, 'textSemantics': 1, 'outputDeclarations': 256, 'origin': 1}, 'parent': pin(a.parent / 'semantics-build.json'), 'parentExactRelink': expected, 'retainedLinkedInputs': inputs, 'commands': commands, 'sources': {str(f): pin(f) for f in [Path(__file__), lua / 'prepare-origin.py', lua / 'writer_origin.cpp', lua / 'writer_origin.h', lua / 'writer_geometry.cpp']}, 'originAssociation': pin(work / 'overlay/association.json'), 'headers': headers, 'archiveBefore': pin(archive), 'archiveAfter': pin(newarchive), 'changedMembers': changed, 'archiveMembersBefore': before, 'archiveMembersAfter': after, 'outputs': {name: pin(out / name) for name in ['luatex.js', 'luatex.wasm']}}
(out / 'origin-luahb-build.json').write_text(json.dumps(receipt, indent=2) + '\n')
print(json.dumps(receipt['outputs']))
