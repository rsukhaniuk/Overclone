#!/usr/bin/env python3
"""Add LuaLaTeX fontspec aliases that reference existing pinned font files."""
import argparse
import hashlib
import json
from pathlib import Path
import zipfile

from importlib import import_module

archive_tools = import_module('prepare-xetex-compatible-fonts')
LOCK = Path(__file__).with_name('luatex-compatible-fonts.lock.json')


def assemble(base, out, lock):
    archive_tools.verify_file(base, lock['base'])
    with zipfile.ZipFile(base) as old:
        pins = {row['name']: row for row in json.loads(old.read('pins.json'))}
        added = {}
        for alias in lock['aliases']:
            if alias['name'] in pins or any(font not in pins for font in alias['fonts']):
                raise ValueError('Font alias overlaps the base or references an absent font')
            options = ', '.join(f'{key}={{{font}}}' for key, font in zip(
                ('UprightFont', 'BoldFont', 'ItalicFont', 'BoldItalicFont'), alias['fonts'], strict=True))
            data = f"\\defaultfontfeatures[{alias['family']}]{{{options}}}\n".encode('ascii')
            name = alias['name']
            added[name] = data
            pins[name] = dict(name=name, length=len(data), sha256=hashlib.sha256(data).hexdigest())
        rows = sorted(pins.values(), key=lambda row: row['name'])
        with out.open('xb') as target:
            try:
                with zipfile.ZipFile(target, 'w', compression=zipfile.ZIP_STORED, allowZip64=False) as bundle:
                    bundle.writestr(zipfile.ZipInfo('pins.json'), (json.dumps(rows, indent=2) + '\n').encode())
                    for row in rows:
                        name = row['name']
                        bundle.writestr(zipfile.ZipInfo('files/' + name), added[name] if name in added else old.read('files/' + name))
                archive_tools.verify_file(out, lock['archive'])
            except Exception:
                out.unlink()
                raise


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--base', required=True, type=Path)
    parser.add_argument('--out', required=True, type=Path)
    args = parser.parse_args()
    try:
        assemble(args.base, args.out, json.loads(LOCK.read_bytes()))
    except (ValueError, KeyError, OSError, zipfile.BadZipFile) as error:
        parser.exit(2, f'Refused LuaTeX compatibility archive: {error}\n')
