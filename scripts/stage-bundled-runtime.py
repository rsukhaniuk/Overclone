#!/usr/bin/env python3
"""Stage existing verified runtime inputs into the app's ignored static assets.

Offline build tooling only. Never downloads or commits generated runtimes.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil


def stage(compiler, renderer, output):
    pins = json.loads((Path(__file__).resolve().parents[1] /
        'src/Client/Compilation/Provisioning/bundled-runtime.json').read_text())
    sources = {'compiler': compiler, 'pdfLoader': renderer / 'e1.js', 'pdfWasm': renderer / 'e1.wasm'}
    # Verify every source before writing anything; names and hashes belong to the app.
    for role, source in sources.items():
        pin = pins[role]
        if not source.is_file() or source.stat().st_size != pin['length']:
            raise ValueError('Missing or wrong-sized bundled asset: ' + pin['name'])
        with source.open('rb') as stream:
            if hashlib.file_digest(stream, 'sha256').hexdigest() != pin['sha256']:
                raise ValueError('Bundled asset hash mismatch: ' + pin['name'])
    output.mkdir(parents=True, exist_ok=True)
    if any(p.name not in {pins[role]['name'] for role in sources} for p in output.iterdir()):
        raise ValueError('Output must contain only the three bundled runtime files')
    for role, source in sources.items():
        temporary = output / (pins[role]['name'] + '.tmp')
        shutil.copyfile(source, temporary)
        temporary.replace(output / pins[role]['name'])
    print(json.dumps({'files': 3, 'bytes': sum(pins[role]['length'] for role in sources), 'status': 'Verified bundled application assets staged'}))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--compiler-zip', type=Path, required=True)
    parser.add_argument('--renderer', type=Path, required=True, help='directory containing the supported e1.js and e1.wasm')
    parser.add_argument('--out', type=Path, default=Path(__file__).resolve().parents[1] / 'src/Client/wwwroot/bundled-runtime')
    args = parser.parse_args()
    stage(args.compiler_zip, args.renderer, args.out)
