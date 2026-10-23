#!/usr/bin/env python3
"""Private test VFS only, not a package manager or release resource selection."""
import hashlib
import json
from pathlib import Path
import shutil

root = Path('/control/provided-texmf')
assert hashlib.sha256((root/'manifest.json').read_bytes()).hexdigest() == '959cf3e4615b85fa8988a178b8e90f7ea20c92bcadbae411f3f4b346594532b1'
dest = Path('/control/build/xetex/tex'); dest.mkdir(exist_ok=True)
entries = {}
for src in sorted(Path('/inputs/resources/files').iterdir()):
    if src.suffix not in ('.fmt', '.js', '.wasm'):
        entries[src.name] = src
provided = {}
try:
    source_manifest = json.loads((root/'manifest.json').read_text())
except (OSError, ValueError) as error:
    raise SystemExit('Invalid explicit input manifest') from error
for entry in source_manifest['files']:
    src = root/'files'/entry['name']
    data = src.read_bytes()
    assert len(data) == entry['length'] and hashlib.sha256(data).hexdigest() == entry['sha256'], src
    if ('/tex/' not in entry['name'] and '/fonts/' not in entry['name']) or src.suffix == '.lua':
        continue
    if src.name in provided and provided[src.name].read_bytes() != data:
        raise RuntimeError('Conflicting provided basename: '+src.name)
    provided[src.name] = src
entries.update(provided)
entries['icudt68l.dat'] = Path('/inputs/texlive/libs/icu/icu-src/source/data/in/icudt68l.dat')
manifest = []
for name, src in sorted(entries.items()):
    shutil.copyfile(src, dest/name)
    data = src.read_bytes()
    manifest.append(dict(name=name, source=str(src), length=len(data), sha256=hashlib.sha256(data).hexdigest()))
(dest.parent/'test-vfs.json').write_text(json.dumps(manifest, indent=2)+'\n')
print(f'{len(entries)} explicit files staged for private tests')
