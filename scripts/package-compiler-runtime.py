#!/usr/bin/env python3
"""Wrap an existing pinned runtime directory for the application's ZIP reader.

Offline build tooling. Engine runtime.zip files contain flat resources; the
application envelope instead contains pins.json and files/<resource> entries.
"""
import argparse
import hashlib
import json
from pathlib import Path
import zipfile


def package(source, output):
    manifest = (source / 'pins.json').read_bytes()
    pins = json.loads(manifest)
    names = [p['name'] for p in pins]
    if not 0 < len(pins) <= 8192 or len(set(names)) != len(names):
        raise ValueError('Invalid pinned inventory')
    resources = []
    for pin in pins:
        name = pin['name']
        if not name or any(part in ('', '.', '..') for part in name.split('/')) or '\\' in name:
            raise ValueError('Invalid resource path')
        data = (source / 'files' / name).read_bytes()
        if len(data) != pin['length'] or hashlib.sha256(data).hexdigest() != pin['sha256']:
            raise ValueError('Resource integrity mismatch: ' + name)
        resources.append(('files/' + name, data))
    temporary = output.with_name(output.name + '.tmp')
    try:
        with zipfile.ZipFile(temporary, 'w', compression=zipfile.ZIP_STORED, allowZip64=False) as archive:
            for name, data in [('pins.json', manifest)] + sorted(resources):
                entry = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
                entry.create_system = 3
                entry.external_attr = 0o100644 << 16
                archive.writestr(entry, data)
        temporary.replace(output)
    finally:
        temporary.unlink(missing_ok=True)
    with output.open('rb') as stream:
        digest = hashlib.file_digest(stream, 'sha256').hexdigest()
    print(json.dumps({'resources': len(pins), 'length': output.stat().st_size, 'sha256': digest}))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source', type=Path, help='existing pins.json and files directory')
    parser.add_argument('output', type=Path)
    args = parser.parse_args()
    package(args.source, args.output)
