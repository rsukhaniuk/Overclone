#!/usr/bin/env python3
"""Prepare a candidate overlay from retained inputs only; never writes the suite."""
import hashlib
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parent
OUT = Path(sys.argv[1] if len(sys.argv) > 1 else '/control/ukrainian-overlay')
SUITE = Path('/inputs/suite')

def pin(data):
    return {'length': len(data), 'sha256': hashlib.sha256(data).hexdigest()}

def main():
    files = OUT / 'files'
    files.mkdir(parents=True, exist_ok=True)
    try:
        base = json.loads((SUITE / 'pins.json').read_text())
    except (OSError, ValueError) as error:
        raise SystemExit(f'Cannot read accepted suite pins: {error}') from error
    base = {p['name']: p for p in base}
    try:
        lock = json.loads((ROOT / 'source.lock.json').read_text())
    except (OSError, ValueError) as error:
        raise SystemExit(f'Cannot read Ukrainian input lock: {error}') from error
    if pin((SUITE / 'pins.json').read_bytes()) != lock['suitePins']:
        raise SystemExit('Accepted suite pins changed')
    used = []
    def copy(source, name):
        data = Path(source).read_bytes()
        if pin(data) != lock['resources'][name]:
            raise SystemExit(f'Retained input changed: {source}')
        target = OUT / name if name.startswith('notices/') else files / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
        item = {'name': name, **pin(data), 'source': str(source), 'sourcePin': pin(data)}
        if name in base:
            item['replaces'] = base[name]
        used.append(item)
    copy('/inputs/host-texmf/tex/generic/babel/hyphen.cfg', 'hyphen.cfg')
    copy(ROOT / 'language.dat', 'language.dat')
    copy('/inputs/host-texmf/tex/generic/ruhyphen/hypht2.tex', 'hypht2.tex')
    for name in ['catlcy.tex', 'lcy2t2a.tex', 'ukrhypmp.tex', 'ukrhyph.tex']:
        copy('/inputs/host-texmf/tex/generic/ukrhyph/' + name, name)
    # Retain exact original upstream FD/metrics/outlines; no shape substitutions.
    copy('/inputs/t2a/project/t2acmr.fd', 't2acmr.fd')
    for name in ['larm1000.tfm', 'labx1000.tfm', 'cm-super-t2a.enc', 'sfrm1000.pfb', 'sfbx1000.pfb']:
        copy('/inputs/t2a-assets/assets/' + name, name)
    for name in ['cm-super-COPYING.txt', 'cm-super-INSTALL.txt']:
        copy('/inputs/t2a-assets/' + name, 'notices/' + name)
    copy('/inputs/host-texmf/tex/generic/ruhyphen/ruhyphen.tex', 'notices/ruhyphen.tex')
    copy('/inputs/host-texmf/tex/latex/base/lppl.tex', 'notices/lppl.tex')
    manifest = {'schema': 1, 'baseline': 'b4ec43d1cac535408d44ac3d28a583f1c08b509f',
                'basePins': pin((SUITE / 'pins.json').read_bytes()),
                'files': [p for p in used if not p['name'].startswith('notices/')],
                'notices': [p for p in used if p['name'].startswith('notices/')]}
    (OUT / 'provenance.json').write_text(json.dumps(manifest, indent=2) + '\n')
    print(json.dumps({'overlay': str(OUT), 'files': len(used)}))

if __name__ == '__main__':
    main()
