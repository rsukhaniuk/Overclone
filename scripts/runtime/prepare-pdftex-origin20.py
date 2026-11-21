#!/usr/bin/env python3
"""Assemble the pinned direct pdfTeX origin producer on the exact P19 closure."""
import argparse
import importlib.util
import json
from pathlib import Path

HERE = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location('assembly', HERE / 'prepare-biber-semantics.py')
COMMON = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(COMMON)


def assemble(base, producer, output, lock):
    base = COMMON.safe_path(base, directory=True)
    producer = COMMON.safe_path(producer, directory=True)
    output = COMMON.safe_path(output, fresh=True)
    if any(COMMON.overlap(output, source) for source in (base, producer, HERE)):
        raise ValueError('Output overlaps an input')
    if lock['schemaVersion'] != 1 or lock['profile'] != 20:
        raise ValueError('Unsupported recipe version')

    baseline, runtime = lock['base'], lock['runtime']
    parent = json.loads(COMMON.read_checked(base / 'pins.json', baseline['pins'], 2 * 1024 * 1024))
    if len(parent) != baseline['count'] or COMMON.closure(parent) != baseline['closure']:
        raise ValueError('Incomplete P19 baseline')
    new_rows = lock['added']
    rows = sorted(parent + new_rows, key=lambda row: row['name'])
    manifest = (json.dumps(rows, indent=2) + '\n').encode()
    folded = {COMMON.resource_name(row['name']).lower() for row in rows}
    if (len(folded) != len(rows) or len(rows) != runtime['count']
            or sum(p['length'] for p in rows) != runtime['totalBytes']
            or COMMON.closure(rows) != runtime['closure']
            or COMMON.digest(manifest) != runtime['pins']['sha256']):
        raise ValueError('Ambiguous or incomplete P20 inventory')

    material = {row['name']: COMMON.read_checked(base / 'files' / row['name'], row, 64 * 1024 * 1024)
                for row in parent}
    for row in new_rows:
        material[row['name']] = COMMON.read_checked(producer / lock['producer'][row['name']], row, 2 * 1024 * 1024)
    output.mkdir()
    for name, data in material.items():
        path = output / 'files' / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
    (output / 'pins.json').write_bytes(manifest)
    return runtime


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('base', 'producer', 'out'):
        parser.add_argument('--' + name, type=Path, required=True)
    args = parser.parse_args()
    try:
        lock = json.loads((HERE / 'pdftex-origin20.lock.json').read_bytes())
        print(json.dumps(assemble(args.base, args.producer, args.out, lock)))
    except (ValueError, KeyError, TypeError, OSError) as error:
        parser.exit(2, 'Refused P20 runtime: ' + str(error) + '\n')
