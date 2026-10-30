#!/usr/bin/env python3
"""Explicit closed profile6->7 upgrade; ordinary package overlays remain data-only.

Only a reviewed complete lock may replace the Lua executable pair and add the
matching local biblatex BST. No acquisition, runtime trust or license approval.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import zipfile

from expand import closure, inventory
from prepare import MAX_BYTES, read_json, read_verified

BASE_CLOSURE = 'ed990e9baa0ab9776d6cc9f2c9bcd8aed3c1f863cd4c871d7fba633c9fd6a7ac'
REPLACEMENTS = {'luatex.js', 'luatex.wasm'}
ADDITIONS = {'biblatex.bst'}


def fact(name, file):
    if not file.is_file() or file.stat().st_size > 64 * 1024 * 1024:
        raise ValueError('missing or oversized component: ' + name)
    data = file.read_bytes()
    return {'name': name, 'length': len(data), 'sha256': hashlib.sha256(data).hexdigest()}


def plan(base, lua, bst, lock):
    if (lock.get('schema'), lock.get('baseProfile'), lock.get('profile'),
        lock.get('baseClosureSha256'), lock.get('luaAbi')) != (1, 6, 7, BASE_CLOSURE, 3):
        raise ValueError('wrong bibliography upgrade contract')
    entries = inventory(base, 'pins.json')
    if len(entries) != 3702 or closure([p for p, _ in entries.values()]) != BASE_CLOSURE:
        raise ValueError('requires exact immutable profile6')
    if set(lock.get('replacements', {})) != REPLACEMENTS or set(lock.get('additions', {})) != ADDITIONS:
        raise ValueError('only the reviewed Lua pair and biblatex BST may change')
    for name in sorted(REPLACEMENTS):
        change = lock['replacements'][name]
        actual = fact(name, lua / name)
        if change.get('before') != entries[name][0] or change.get('after') != actual:
            raise ValueError('executable preimage/candidate mismatch: ' + name)
        if actual['length'] < 8 or actual['length'] > 64 * 1024 * 1024:
            raise ValueError('invalid executable size')
        if name.endswith('.wasm'):
            with (lua / name).open('rb') as stream:
                if stream.read(8) != b'\0asm\x01\0\0\0':
                    raise ValueError('not a WASM module')
        entries[name] = (actual, lua / name)
    if 'biblatex.bst' in entries or lock['additions']['biblatex.bst'] != fact('biblatex.bst', bst):
        raise ValueError('BST addition/hash mismatch')
    entries['biblatex.bst'] = (lock['additions']['biblatex.bst'], bst)
    pins = [entries[n][0] for n in sorted(entries)]
    if pins != lock.get('files') or closure(pins) != lock.get('closureSha256'):
        raise ValueError('complete bibliography inventory differs from reviewed lock')
    if len(pins) != 3703 or sum(p['length'] for p in pins) > MAX_BYTES:
        raise ValueError('bibliography inventory exceeds existing limits')
    # Existing Lua input/heap policy remains unchanged; preserve measured room
    # for actual captured project/carry. Executables are not seeded runtime data.
    from expand import COMPONENTS
    data = [p for p in pins if p['name'] == 'lualatex.fmt' or
            p['name'] not in COMPONENTS and not p['name'].endswith('.fmt') and
            p['name'] not in {'icudt68l.dat', 'xetexfontlist.txt'}]
    if len(data) >= 4608 or sum(p['length'] for p in data) >= 96 * 1024 * 1024:
        raise ValueError('no Lua project/carry headroom')
    return pins, entries


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--base', type=Path, required=True)
    parser.add_argument('--lua', type=Path, required=True)
    parser.add_argument('--bst', type=Path, required=True)
    parser.add_argument('--lock', type=Path, default=Path(__file__).with_name('bibliography.lock.json'))
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    if args.out.exists():
        raise ValueError('refusing existing output; accepted profiles are immutable')
    pins, entries = plan(args.base, args.lua, args.bst, read_json(args.lock))
    args.out.mkdir(parents=True)
    try:
        for name, (pin, source) in entries.items():
            target = args.out / 'files' / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, target)
            read_verified(args.out / 'files', pin)
        (args.out / 'pins.json').write_text(json.dumps(pins, indent=2) + '\n')
        with zipfile.ZipFile(args.out / 'compiler-suite.zip', 'x', compression=zipfile.ZIP_STORED) as archive:
            for name in ['pins.json'] + ['files/' + p['name'] for p in pins]:
                item = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
                item.create_system = 3
                item.external_attr = 0o100644 << 16
                archive.writestr(item, (args.out / name).read_bytes())
        zip_path = args.out / 'compiler-suite.zip'
        if zip_path.stat().st_size > MAX_BYTES:
            raise ValueError('archive exceeds existing limit')
        receipt = {'schema': 1, 'profile': 7, 'count': len(pins),
                   'bytes': sum(p['length'] for p in pins), 'closureSha256': closure(pins),
                   'zipBytes': zip_path.stat().st_size,
                   'zipSha256': hashlib.sha256(zip_path.read_bytes()).hexdigest(),
                   'baseClosureSha256': BASE_CLOSURE, 'luaAbi': 3,
                   'scope': 'Technical offline assembly only; no redistribution clearance'}
        (args.out / 'receipt.json').write_text(json.dumps(receipt, indent=2) + '\n')
        print(json.dumps(receipt))
    except BaseException:
        # This directory was created by this invocation; never touch accepted inputs.
        shutil.rmtree(args.out)
        raise


if __name__ == '__main__':
    main()
