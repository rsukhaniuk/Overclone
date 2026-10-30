#!/usr/bin/env python3
"""Stage explicitly named installed runtime bytes; never replace accepted data."""
import argparse
import hashlib
import json
from pathlib import Path


def digest(data):
    return hashlib.sha256(data).hexdigest()


def read_json(file):
    try:
        return json.loads(file.read_text())
    except (OSError, ValueError) as error:
        raise RuntimeError(f'Invalid JSON input: {file}') from error


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('sources', nargs='*', type=Path)
    parser.add_argument('--out', type=Path, default=Path('/control/package-overlay'))
    args = parser.parse_args()
    root = args.out
    (root / 'files').mkdir(parents=True, exist_ok=True)
    target = root / 'manifest.json'
    provenance = root / 'provenance.json'
    manifest = read_json(provenance) if provenance.exists() else {'schema': 1, 'files': []}
    accepted = {p['name']: p for p in read_json(Path('/inputs/suite/pins.json'))}
    allowed = [Path('/inputs/host-texmf'), Path('/inputs/host-texmf-extra')]
    for source in args.sources:
        source = source.resolve(strict=True)
        require(any(source.is_relative_to(p) for p in allowed), f'Unapproved source: {source}')
        name = source.name
        data = source.read_bytes()
        require(name not in accepted, f'Accepted file cannot be replaced, even identically: {name}')
        pin = {'name': name, 'length': len(data), 'sha256': digest(data),
               'source': str(source), 'sourceTree': str(next(p for p in allowed if source.is_relative_to(p))),
               'upstreamRelativePath': str(source.relative_to(next(p for p in allowed if source.is_relative_to(p)))),
               'notice': 'Original source bytes, including embedded upstream notices, unchanged.'}
        existing = next((p for p in manifest['files'] if p['name'] == name), None)
        require(existing is None or existing == pin, f'Differing basename collision: {name}')
        if existing is None:
            (root / 'files' / name).write_bytes(data)
            manifest['files'].append(pin)
    manifest['files'].sort(key=lambda p: p['name'])
    for pin in manifest['files']:
        name = pin['name']
        require(isinstance(name, str) and Path(name).name == name and name not in {'.', '..'}, 'Invalid overlay name')
        data = (root / 'files' / name).read_bytes()
        require(len(data) == pin['length'] and digest(data) == pin['sha256'], f'Overlay pin mismatch: {name}')
        require(name not in accepted, f'Accepted-file collision: {name}')
        source = Path(pin['source']).resolve(strict=True)
        require(any(source.is_relative_to(p) for p in allowed), f'Unapproved source: {source}')
        require(source.read_bytes() == data, f'Source mismatch: {name}')
    manifest['totalBytes'] = sum(p['length'] for p in manifest['files'])
    manifest['closureSha256'] = digest('\n'.join(f"{p['name']}\t{p['length']}\t{p['sha256']}" for p in manifest['files']).encode())
    provenance.write_text(json.dumps(manifest, indent=2) + '\n')
    target.write_text(json.dumps({'schema': 1, 'files': [
        {k: p[k] for k in ('name', 'length', 'sha256')} for p in manifest['files']
    ]}, indent=2) + '\n')
    print(json.dumps({'files': len(manifest['files']), 'bytes': manifest['totalBytes'], 'closure': manifest['closureSha256']}))


if __name__ == '__main__':
    main()
