#!/usr/bin/env python3
"""Assemble a closed additive suite only after root supplies FINAL accepted components.

Component JSON: {"files": [{"name", "length", "sha256", "source", "kind", "phase"}]}.
kind is loader/wasm/format/xe-data; phase is xetex/luatex. No provisional pins.
Outputs use the existing pins.json + files/* ZIP layout; profile registration is root-owned.
"""
import argparse
import json
from pathlib import Path
import shutil
import zipfile
from prepare import MAX_BYTES, MAX_COUNT, digest, read_json, read_verified, safe_name

BASE_CLOSURE = 'cf144348691396f53189299dcc50400fafab1db4128476923542032f8d471352'
XE_DATA = {
    'icudt68l.dat': (19163040, '63a8210766507546a73b7bfdb796c37fad0ec0485b992c1a7e68fd24f893ae6d'),
    'xetexfontlist.txt': (12315, '380ef7057355bff9024f448cf132e6c563364071ee36251d557d95c4652d1e65'),
}
OLD_COMPONENTS = {'engine.js', 'engine.wasm', 'pdflatex.fmt', 'latex-dvi.fmt',
                  'dvi-converter.js', 'dvi-converter.wasm', 'synctex-query.js', 'synctex-query.wasm'}
PHASE_LIMITS = {'luatex': (4608, 96 * 1024 * 1024), 'xetex': (8192, 160 * 1024 * 1024),
                'pdftex': (8192, 160 * 1024 * 1024), 'latex': (8192, 160 * 1024 * 1024)}


def union_add(entries, pin, source):
    name = safe_name(pin['name'])
    identity = {k: pin[k] for k in ('name', 'length', 'sha256')}
    read_verified(source.parent, {**identity, 'name': source.name})
    if name in entries and identity != entries[name][0]:
        raise ValueError('differing suite collision: ' + name)
    entries.setdefault(name, (identity, source))


def closure(pins):
    canonical = '\n'.join(f"{p['name']}\t{p['length']}\t{p['sha256']}" for p in pins)
    return digest(canonical.encode())


def plan(common, old_root, components):
    entries = {}
    common_manifest = read_json(common / 'common.json')
    if common_manifest != read_json(Path(__file__).with_name('common.lock.json')):
        raise ValueError('common manifest does not match frozen lock')
    common_pins = common_manifest['files']
    old = read_json(old_root / 'pins.json')
    if len(old) != 507 or closure(sorted(old, key=lambda p: p['name'])) != BASE_CLOSURE:
        raise ValueError('requires the exact accepted suite-base04 507-entry base, not a replacement profile')
    if len({p['name'] for p in common_pins}) != len(common_pins) or len({p['name'] for p in old}) != len(old):
        raise ValueError('duplicate manifest name')
    for pin in common_pins:
        if pin['name'] in OLD_COMPONENTS or pin['name'].endswith(('.fmt', '.js', '.wasm')) or pin['name'] == 'icudt68l.dat':
            raise ValueError('engine-specific artifact in common FS')
        union_add(entries, pin, common / 'files' / safe_name(pin['name']))
    for pin in old:
        union_add(entries, pin, old_root / 'files' / safe_name(pin['name']))
    # Every old non-component must still be present in common, with identical bytes.
    if {p['name'] for p in old} - OLD_COMPONENTS - {p['name'] for p in common_pins}:
        raise ValueError('old runtime data missing from common view')
    supplied = set()
    for pin in components:
        name = safe_name(pin['name'])
        if name in entries or name in supplied:
            raise ValueError('component redefines a common/base/other entry: ' + name)
        supplied.add(name)
        if pin['kind'] not in ('loader', 'wasm', 'format', 'xe-data') or pin['phase'] not in ('xetex', 'luatex'):
            raise ValueError('invalid component kind/phase')
        if pin['kind'] == 'xe-data':
            if pin['phase'] != 'xetex' or XE_DATA.get(name) != (pin['length'], pin['sha256']):
                raise ValueError('requires exact accepted Xe-only data/catalog')
        union_add(entries, pin, Path(pin['source']))
    for phase in ('xetex', 'luatex'):
        for kind in ('loader', 'wasm', 'format'):
            if sum(p['phase'] == phase and p['kind'] == kind for p in components) != 1:
                raise ValueError('requires exactly one final ' + phase + ' ' + kind)
    xe_data = {p['name'] for p in components if p['kind'] == 'xe-data'}
    if xe_data != {'icudt68l.dat', 'xetexfontlist.txt'}:
        raise ValueError('requires exact ICU and generated explicit font catalog as Xe-only data')
    pins = [entries[name][0] for name in sorted(entries)]
    if len(pins) > MAX_COUNT or sum(p['length'] for p in pins) > MAX_BYTES:
        raise ValueError('suite count/byte limit exceeded')
    views = {}
    for phase, (count_limit, byte_limit) in PHASE_LIMITS.items():
        names = {p['name'] for p in common_pins}
        if phase in ('pdftex', 'latex'):
            names.add('pdflatex.fmt' if phase == 'pdftex' else 'latex-dvi.fmt')
        names.update(p['name'] for p in components if p['phase'] == phase and p['kind'] in ('format', 'xe-data'))
        view_pins = [entries[name][0] for name in sorted(names)]
        total = sum(p['length'] for p in view_pins)
        if len(names) >= count_limit or total >= byte_limit:
            raise ValueError('phase has no promised project/carry headroom: ' + phase)
        views[phase] = {'mount': '/tex', 'count': len(names), 'bytes': total,
                        'remainingCount': count_limit - len(names), 'remainingBytes': byte_limit - total,
                        'closureSha256': closure(view_pins), 'files': sorted(names)}
    return pins, entries, views


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--common', type=Path, required=True)
    parser.add_argument('--accepted-dvi', type=Path, required=True)
    parser.add_argument('--components', type=Path, required=True, help='root-authenticated final component pins with private sources')
    parser.add_argument('--out', type=Path, required=True, help='new private directory; never replace an earlier closure')
    args = parser.parse_args()
    if args.out.exists():
        raise ValueError('refusing existing output')
    pins, entries, views = plan(args.common, args.accepted_dvi, read_json(args.components)['files'])
    args.out.mkdir(parents=True)
    for pin, source in entries.values():
        target = args.out / 'files' / pin['name']
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
        read_verified(args.out / 'files', pin)
    (args.out / 'pins.json').write_text(json.dumps(pins, indent=2) + '\n')
    (args.out / 'views.json').write_text(json.dumps(views, indent=2) + '\n')
    # Stable timestamps, modes, ordinal paths and stored payloads; no zlib-version variability.
    with zipfile.ZipFile(args.out / 'compiler-suite.zip', 'x', compression=zipfile.ZIP_STORED) as archive:
        for name in ['pins.json'] + ['files/' + pin['name'] for pin in pins]:
            info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            info.create_system = 3
            info.external_attr = 0o100644 << 16
            archive.writestr(info, (args.out / name).read_bytes())
    if (args.out / 'compiler-suite.zip').stat().st_size > MAX_BYTES:
        raise ValueError('complete ZIP exceeds suite byte limit')
    receipt = {'schema': 1, 'count': len(pins), 'bytes': sum(p['length'] for p in pins),
               'closureSha256': closure(pins), 'zipSha256': digest((args.out / 'compiler-suite.zip').read_bytes()),
               'status': 'Assembled technical data, not profile registration or distribution approval'}
    (args.out / 'receipt.json').write_text(json.dumps(receipt, indent=2) + '\n')
    print(json.dumps(receipt))


if __name__ == '__main__':
    main()
