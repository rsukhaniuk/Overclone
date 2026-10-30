#!/usr/bin/env python3
"""Build a separately locked package/font upgrade from verified offline inputs.

The original four-engine suite is immutable. Only the root-reviewed lock can
authorize replacement data/formats; executable components cannot be replaced.
This is build tooling, not runtime trust or redistribution approval.
"""
import argparse
import json
from pathlib import Path
import shutil
import zipfile

from prepare import MAX_BYTES, MAX_COUNT, digest, read_json, read_verified, safe_name

BASE_CLOSURE = 'b58c8f79ce22a516f69f0a66211aab0c6298d904012229c7c063dcee36d927db'
REPLACEABLE = {'pdflatex.fmt', 'latex-dvi.fmt', 'xelatex.fmt', 'lualatex.fmt', 'hyphen.cfg'}
COMPONENTS = {'engine.js', 'engine.wasm', 'xetex.js', 'xetex.wasm', 'luatex.js', 'luatex.wasm',
              'dvi-converter.js', 'dvi-converter.wasm', 'synctex-query.js', 'synctex-query.wasm'}


def closure(pins):
    return digest('\n'.join(f"{p['name']}\t{p['length']}\t{p['sha256']}" for p in sorted(pins, key=lambda p: p['name'])).encode())


def inventory(root, manifest):
    pins = read_json(root / manifest)
    if isinstance(pins, dict):
        pins = pins['files']
    if not isinstance(pins, list) or not pins or len(pins) > MAX_COUNT:
        raise ValueError('invalid manifest count')
    entries = {}
    for pin in pins:
        name = safe_name(pin['name'])
        if name in entries:
            raise ValueError('duplicate manifest name: ' + name)
        if set(pin) != {'name', 'length', 'sha256'}:
            raise ValueError('manifest must contain public byte identities only')
        read_verified(root / 'files', pin)
        entries[name] = (pin, root / 'files' / name)
    return entries


def plan(base, overlays, lock):
    if lock.get('schema') != 1 or lock.get('baseClosureSha256') != BASE_CLOSURE:
        raise ValueError('wrong upgrade lock/base')
    entries = inventory(base, 'pins.json')
    if len(entries) != 3677 or closure([pin for pin, _ in entries.values()]) != BASE_CLOSURE:
        raise ValueError('requires the exact accepted original four-engine suite')
    original = dict(entries)
    replacements = {}
    for overlay in overlays:
        for name, (pin, source) in inventory(overlay, 'manifest.json').items():
            if name in COMPONENTS or name.endswith(('.js', '.wasm')):
                raise ValueError('package/font overlay cannot supply executable components: ' + name)
            if name in entries and entries[name][0] != pin:
                if name not in REPLACEABLE or name in replacements:
                    raise ValueError('unapproved or conflicting replacement: ' + name)
                replacements[name] = {'before': original[name][0], 'after': pin}
            entries[name] = (pin, source)
    pins = [entries[name][0] for name in sorted(entries)]
    if pins != lock.get('files') or replacements != lock.get('replacements'):
        raise ValueError('complete upgraded inventory/replacements differ from reviewed lock')
    total = sum(p['length'] for p in pins)
    if len(pins) > MAX_COUNT or total > MAX_BYTES:
        raise ValueError('expanded suite exceeds existing resource limits')
    for engine, fmt, count_limit, byte_limit in [
        ('pdftex', 'pdflatex.fmt', 8192, 160 * 1024 * 1024),
        ('latex', 'latex-dvi.fmt', 8192, 160 * 1024 * 1024),
        ('xetex', 'xelatex.fmt', 8192, 160 * 1024 * 1024),
        ('luatex', 'lualatex.fmt', 4608, 96 * 1024 * 1024),
    ]:
        view = [p for p in pins if p['name'] == fmt or
                p['name'] not in COMPONENTS and not p['name'].endswith('.fmt') and
                (engine == 'xetex' or p['name'] not in {'icudt68l.dat', 'xetexfontlist.txt'})]
        if len(view) >= count_limit or sum(p['length'] for p in view) >= byte_limit:
            raise ValueError('expanded phase has no project/carry headroom: ' + engine)
    return pins, entries


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--base', type=Path, required=True)
    parser.add_argument('--overlay', type=Path, action='append', required=True)
    parser.add_argument('--lock', type=Path, default=Path(__file__).with_name('compatibility.lock.json'))
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    if args.out.exists():
        raise ValueError('refusing existing output; original suites must be preserved')
    pins, entries = plan(args.base, args.overlay, read_json(args.lock))
    args.out.mkdir(parents=True)
    for name, (pin, source) in entries.items():
        target = args.out / 'files' / name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
        read_verified(args.out / 'files', pin)
    (args.out / 'pins.json').write_text(json.dumps(pins, indent=2) + '\n')
    with zipfile.ZipFile(args.out / 'compiler-suite.zip', 'x', compression=zipfile.ZIP_STORED) as archive:
        for name in ['pins.json'] + ['files/' + p['name'] for p in pins]:
            info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            info.create_system = 3
            info.external_attr = 0o100644 << 16
            archive.writestr(info, (args.out / name).read_bytes())
    size = (args.out / 'compiler-suite.zip').stat().st_size
    if size > MAX_BYTES:
        raise ValueError('expanded archive exceeds suite limit')
    receipt = {'schema': 1, 'count': len(pins), 'bytes': sum(p['length'] for p in pins),
               'closureSha256': closure(pins), 'zipBytes': size,
               'zipSha256': digest((args.out / 'compiler-suite.zip').read_bytes()),
               'baseClosureSha256': BASE_CLOSURE,
               'scope': 'Verified offline technical assembly; not redistribution approval'}
    (args.out / 'receipt.json').write_text(json.dumps(receipt, indent=2) + '\n')
    print(json.dumps(receipt))


if __name__ == '__main__':
    main()
