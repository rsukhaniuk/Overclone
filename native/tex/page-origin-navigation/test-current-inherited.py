#!/usr/bin/env python3
"""Actual root/page inheritance controls on current LuaHB+semantics lineage."""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
from pdf_fixture import objects

p = argparse.ArgumentParser(description=__doc__)
for name in ['candidate', 'baseline', 'results']:
    p.add_argument('--' + name, required=True, type=Path)
a = p.parse_args()
out = a.results.resolve()
assert not out.exists()
out.mkdir(parents=True)
here = Path(__file__).resolve().parent
clock = here.parent / 'synctex-fixtures/dvi/fixed-clock.cjs'
tex = r'\pdfvariable pagesattr{/CropBox [-10 -10 700 900]}'
lua = r'\directlua{pdf.setpagesattributes("/Rotate 90")}'
empty = r'\pdfvariable pagesattr{}\directlua{pdf.setpagesattributes("")}'
cases = [('reset-before', tex + lua + empty, '', True),
         ('reset-after', tex + lua, empty, True),
         ('root-crop', tex, '', False), ('root-rotate', lua, '', False),
         ('root-cross-channel', tex + lua, '', False),
         ('root-late', '', lua, False),
         ('root-userunit', r'\pdfvariable pagesattr{/UserUnit 1}', '', False)]
rows, commands = [], []
def run(label, cmd, env=None):
    r = subprocess.run(list(map(str, cmd)), env=env, capture_output=True, text=True, timeout=90)
    (out / (label + '.log')).write_text(r.stdout + r.stderr)
    commands.append({'label': label, 'exit': r.returncode})
    assert not r.returncode, (label, r.stdout, r.stderr)
def compile(label, project, component):
    folder = out / label
    run(label, ['node', '-r', clock, here / 'run-current-project.cjs', 'lua', 'latex', folder],
        dict(os.environ, PROJECT=str(project), REQUIRE_ORIGIN='0', LUA_CANDIDATE=str(component)))
    pdf = (folder / 'output.pdf').read_bytes()
    obj, _ = objects(pdf)
    roots = [v.decode('latin1') for v in obj.values() if re.search(rb'/Type\s*/Pages\b', v)]
    assert len(roots) == 1
    geometry = json.loads((folder / 'geometry.json').read_text())
    assert len(geometry['pages']) == 4
    return folder, roots[0], geometry
for label, early, late, available in cases:
    project = out / (label + '-project')
    run(label + '-prepare', ['python3', here / 'make-project.py', '--kind', 'lua', '--out', project, '--extra-tex', early, '--final-tex', late])
    folder, root, geometry = compile(label, project, a.candidate)
    assert all(g['status'] == ('available' if available else 'unsupported') for g in geometry['pages'])
    assert bool(re.search(r'/(?:CropBox|Rotate|UserUnit)\b', root)) != available
    if not available:
        assert all(g['writerToPdfRaw'] is None and g['writerPageBoxBp'] is None for g in geometry['pages'])
    rows.append({'label': label, 'available': available, 'actualRootDictionary': root, 'geometry': geometry})
for label, setup in [('legacy-crop', tex), ('legacy-rotate', lua), ('legacy-cross-channel', tex + lua)]:
    project = out / (label + '-project')
    run(label + '-prepare', ['python3', here / 'make-project.py', '--kind', 'lua', '--out', project, '--extra-tex', setup, '--no-media', '--view'])
    after, root, geometry = compile(label, project, a.candidate)
    before, _, _ = compile(label + '-baseline', project, a.baseline)
    for name in ['output.pdf', 'output.log', 'raw.synctex', 'geometry.json']:
        assert (before / name).read_bytes() == (after / name).read_bytes(), (label, name)
    rows.append({'label': label, 'byteIdenticalToP14': True, 'actualRootDictionary': root, 'geometry': geometry})
(out / 'receipt.json').write_text(json.dumps({'status': 'pass', 'nativeCompiles': 13, 'skips': 0, 'scope': 'actual current-lineage native inherited-root/refusal/reset controls and exact P14 legacy compatibility', 'rows': rows, 'commands': commands}, indent=2) + '\n')
print('Current LuaHB inherited domains and exact P14 legacy comparisons pass')
