#!/usr/bin/env python3
"""Assemble the pinned DVI decimal-page-label witness on the exact P20 closure."""
import argparse
import importlib.util
import json
from pathlib import Path

HERE = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location('assembly', HERE / 'prepare-biber-semantics.py')
COMMON = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(COMMON)
NOTICE = b"""The DVI converter derivatives retain the upstream notices and license.
The decimal PageLabels witness admits only a bounded, direct decimal number tree.
The source patch is native/tex/page-origin-navigation/dvi-decimal-page-label21.patch.
"""


def assemble(base, converter, output, lock):
    base = COMMON.safe_path(base, directory=True)
    converter = COMMON.safe_path(converter)
    output = COMMON.safe_path(output, fresh=True)
    if any(COMMON.overlap(output, source) for source in (base, converter, HERE)):
        raise ValueError('Output overlaps an input')
    if lock['schemaVersion'] != 1 or lock['profile'] != 21:
        raise ValueError('Unsupported recipe version')

    baseline, runtime = lock['base'], lock['runtime']
    parent = json.loads(COMMON.read_checked(base / 'pins.json', baseline['pins'], 2 * 1024 * 1024))
    if len(parent) != baseline['count'] or COMMON.closure(parent) != baseline['closure']:
        raise ValueError('Incomplete P20 baseline')
    replacement, notice = lock['converter'], lock['notice']
    replaced = replacement['name']
    rows = sorted([row for row in parent if row['name'] != replaced] + [replacement, notice],
                  key=lambda row: row['name'])
    manifest = (json.dumps(rows, indent=2) + '\n').encode()
    folded = {COMMON.resource_name(row['name']).lower() for row in rows}
    if (len(folded) != len(rows) or len(rows) != runtime['count']
            or sum(row['length'] for row in rows) != runtime['totalBytes']
            or COMMON.closure(rows) != runtime['closure']
            or COMMON.digest(manifest) != runtime['pins']['sha256']
            or len(NOTICE) != notice['length'] or COMMON.digest(NOTICE) != notice['sha256']):
        raise ValueError('Ambiguous or incomplete P21 inventory')

    material = {row['name']: COMMON.read_checked(base / 'files' / row['name'], row, 64 * 1024 * 1024)
                for row in parent}
    material[replaced] = COMMON.read_checked(converter, replacement, 2 * 1024 * 1024)
    material[notice['name']] = NOTICE
    output.mkdir()
    for name, data in material.items():
        path = output / 'files' / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
    (output / 'pins.json').write_bytes(manifest)
    return runtime


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('base', 'converter', 'out'):
        parser.add_argument('--' + name, type=Path, required=True)
    args = parser.parse_args()
    try:
        lock = json.loads((HERE / 'dvi-page-label21.lock.json').read_bytes())
        print(json.dumps(assemble(args.base, args.converter, args.out, lock)))
    except (ValueError, KeyError, TypeError, OSError) as error:
        parser.exit(2, 'Refused P21 runtime: ' + str(error) + '\n')
