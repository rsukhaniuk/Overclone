#!/usr/bin/env python3
"""Freeze already-provided TeX data, offline. No fetching, engine builds or publication."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import shutil

EXTENSIONS = set('sty cls ltx def fd clo ini cfg tex lua dat txt map enc tfm pfb otf ttf cmap pro icc bbx cbx lbx ldf dfu cnf sto ps bst vf'.split())
MAX_FILE = 64 * 1024 * 1024
MAX_BYTES = 256 * 1024 * 1024
MAX_COUNT = 8192
OVERRIDES = {'pdflatex.ini', 'hyphen.cfg'}


def digest(data):
    return hashlib.sha256(data).hexdigest()


def safe_name(name):
    if not isinstance(name, str) or not name or '\\' in name or any(ord(c) < 32 for c in name):
        raise ValueError('invalid resource path')
    path = PurePosixPath(name)
    if path.is_absolute() or any(p in ('', '.', '..') for p in name.split('/')) or ':' in name:
        raise ValueError('unsafe resource path: ' + name)
    return name


def read_verified(root, pin):
    name = safe_name(pin['name'])
    path = root / name
    if any(p.is_symlink() for p in [path, *path.parents]):
        raise ValueError('symlink input: ' + name)
    if not path.is_file() or path.stat().st_size > MAX_FILE:
        raise ValueError('missing or oversized input: ' + name)
    data = path.read_bytes()
    if len(data) != pin['length'] or digest(data) != pin['sha256']:
        raise ValueError('input hash/length mismatch: ' + name)
    return data


def runtime_name(name):
    parts = PurePosixPath(name).parts
    # pgf/gd/doc is a real require()-able module tree, not a host documentation
    # directory. Only classify directories before the Lua module root as docs.
    source_parts = PurePosixPath(name.split('/lua/', 1)[0]).parts if name.endswith('.lua') else parts
    if any(p in ('doc', 'docs', 'notices', 'source', 'build') for p in source_parts):
        return None
    if PurePosixPath(name).suffix[1:] not in EXTENSIONS:
        return None
    # Lua require() needs package-qualified paths, NOT flattened colliding basenames.
    # PGF ships its luamath modules under libraries/luamath rather than lua/.
    if name.endswith('.lua'):
        for module_root in ('/lua/', '/libraries/luamath/'):
            if module_root in name:
                return safe_name(name.split(module_root, 1)[1])
    return safe_name(parts[-1])


def freeze(sources):
    selected, provenance, excluded, resolutions = {}, {}, [], []
    for label, root, pins in sources:
        seen = set()
        for pin in pins:
            original = safe_name(pin['name'])
            if original in seen:
                raise ValueError('duplicate source entry: ' + original)
            seen.add(original)
            data = read_verified(root, pin)
            name = runtime_name(original)
            if name is None:
                excluded.append({'sourceSet': label, 'name': original})
                continue
            identity = {'name': name, 'length': len(data), 'sha256': digest(data)}
            source = {'sourceSet': label, 'name': original, 'path': str(root / original),
                      'length': len(data), 'sha256': identity['sha256']}
            if name in selected and identity != selected[name][0]:
                if name not in OVERRIDES or provenance[name][0]['sourceSet'] != 'accepted-dvi':
                    raise ValueError('differing destination collision: ' + name)
                resolutions.append({'name': name, 'kept': provenance[name][0], 'excluded': source,
                                    'reason': 'Preserve accepted deterministic bootstrap / English-only format configuration'})
                continue
            if name not in selected:
                selected[name] = (identity, root / original)
                provenance[name] = []
            provenance[name].append(source)
    entries = [selected[k][0] for k in sorted(selected)]
    if len(entries) > MAX_COUNT or sum(e['length'] for e in entries) > MAX_BYTES:
        raise ValueError('common data exceeds suite limits')
    manifest = {'schema': 1, 'kind': 'common-tex-data', 'mount': '/tex',
                'kernel': '2023-11-01 patch 1', 'files': entries,
                'count': len(entries), 'bytes': sum(e['length'] for e in entries)}
    return manifest, selected, {'sources': provenance, 'excluded': excluded, 'resolutions': resolutions}


def read_json(path):
    try:
        return json.loads(path.read_text())
    except (OSError, ValueError) as error:
        raise ValueError('invalid JSON input: ' + str(path)) from error


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for label in ('accepted-dvi', 'texmf', 'font-extra', 'lua-unicode'):
        parser.add_argument('--' + label, type=Path, required=True, help='provided directory containing files/ and pins.json or manifest.json')
    parser.add_argument('--out', type=Path, required=True, help='new private output directory')
    parser.add_argument('--lock', type=Path, help='require exact frozen common manifest before writing output')
    args = parser.parse_args()
    sources = []
    for label in ('accepted-dvi', 'texmf', 'font-extra', 'lua-unicode'):
        directory = getattr(args, label.replace('-', '_')).resolve()
        manifest_path = directory / ('pins.json' if label == 'accepted-dvi' else 'manifest.json')
        manifest = read_json(manifest_path)
        sources.append((label, directory / 'files', manifest if isinstance(manifest, list) else manifest['files']))
    manifest, selected, provenance = freeze(sources)
    if args.lock and read_json(args.lock) != manifest:
        raise ValueError('common lock mismatch; no output written')
    if args.out.exists():
        raise ValueError('output already exists; refusing replacement')
    args.out.mkdir(parents=True)
    for name, (_, source) in selected.items():
        target = args.out / 'files' / name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
        read_verified(args.out / 'files', selected[name][0])
    (args.out / 'common.json').write_text(json.dumps(manifest, indent=2) + '\n')
    (args.out / 'provenance.private.json').write_text(json.dumps(provenance, indent=2) + '\n')
    print(json.dumps({k: manifest[k] for k in ('count', 'bytes')}))


if __name__ == '__main__':
    main()
