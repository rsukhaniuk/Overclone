#!/usr/bin/env python3
"""Offline actual native collector checks; no product runtime adoption.

Controlled-depth units invoke the installed producer/collector, not replacements.
All other vectors compile real unmodified fixture or TeX-authored glyph/kern input.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess

p = argparse.ArgumentParser(description=__doc__)
for name in ['runtime', 'component', 'overlay', 'amiri', 'results']:
    p.add_argument('--' + name, required=True, type=Path)
a = p.parse_args()
out = a.results.resolve()
assert not out.exists(), 'fresh results required'
out.mkdir(parents=True)
here = Path(__file__).resolve().parent
fixed = here.parents[2] / 'synctex-fixtures/dvi/fixed-clock.cjs'
test = here / 'test-budget.cjs'
cases = []
for fixture in ['original', 'mixed', 'lines-pages', 'latin']:
    cases += [(fixture + '-before', fixture, 0, '-'),
              (fixture + '-after', fixture, 0, str(a.overlay.resolve()))]
for kind, limit in [('units', 16384), ('nodes', 65536), ('depth', 32)]:
    for label, value in [('below', limit - 1), ('at', limit), ('above', limit + 1)]:
        cases.append((kind + '-' + label, kind, value, str(a.overlay.resolve())))
for label, overlay in [('before', '-'), ('after', str(a.overlay.resolve()))]:
    cases += [('deep-only-' + label, 'deep-only', 33, overlay),
              ('ordinary-' + label, 'ordinary', 1, overlay)]
commands = []
for label, kind, value, overlay in cases:
    command = ['node', '-r', str(fixed), str(test), str(a.runtime.resolve()),
               str(a.component.resolve()), overlay, str(a.amiri.resolve()),
               str(out / label), kind, str(value)]
    r = subprocess.run(command, env=dict(os.environ, TZ='UTC'), capture_output=True,
                       text=True, timeout=120)
    (out / (label + '.driver.log')).write_text(r.stdout + r.stderr)
    commands.append({'label': label, 'argv': command, 'exit': r.returncode})
    (out / 'commands.json').write_text(json.dumps(commands, indent=2) + '\n')
    if r.returncode:
        raise SystemExit(label + ' failed; see its driver.log')
    print(label + ': pass', flush=True)
for label in ['original', 'mixed', 'lines-pages', 'latin', 'ordinary']:
    for filename in ['result.pdf', 'synctex.raw', 'geometry.json']:
        assert (out / (label + '-before') / filename).read_bytes() == (
            out / (label + '-after') / filename).read_bytes(), (label, filename)
receipts = [json.loads((out / label / 'receipt.json').read_text()) for label, *_ in cases]
sha = lambda file: hashlib.sha256(file.read_bytes()).hexdigest()
receipt = {'status': 'pass', 'nativeInvocations': len(cases), 'skips': 0,
           'scope': 'Native collector adjacent budget QA, genuine TeX node vectors and '
                    'explicit installed-function depth units; no browser or product adoption',
           'tooling': {str(f): sha(f) for f in [Path(__file__), test, fixed]},
           'exactPdfMapGeometryCompatibility': ['original', 'mixed', 'lines-pages', 'latin', 'ordinary'],
           'results': receipts}
(out / 'receipt.json').write_text(json.dumps(receipt, indent=2) + '\n')
print(json.dumps({'status': 'pass', 'nativeInvocations': len(cases), 'skips': 0}))
