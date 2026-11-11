#!/usr/bin/env python3
"""Derive a bounded collector from the exact accepted P13 resource.

Old overlay.py/rtl-spans.lua and accepted P13 binary/data inputs stay unchanged.
This deterministic build-only derivative does not adopt profile14.
"""
import argparse
import hashlib
import json
from pathlib import Path

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('resource', type=Path, help='exact P13 luaotfload-harf-plug.lua')
p.add_argument('out', type=Path, help='fresh candidate resource directory')
a = p.parse_args()
here = Path(__file__).resolve().parent
if a.out.exists():
    raise SystemExit('fresh output required')
raw = a.resource.read_bytes()
sha = lambda b: hashlib.sha256(b).hexdigest()
if len(raw) != 44737 or sha(raw) != 'b8ee9bf48072f929a2d159e67c16ab575ea0b4056e915b6b368412b45cfbb0cd':
    raise SystemExit('exact accepted P13 resource preimage required')
old = (here / 'rtl-spans.lua').read_bytes()
new = (here / 'rtl-spans-bounded.lua').read_bytes()
if raw.count(old) != 1:
    raise SystemExit('exact original collector source association required')
result = raw.replace(old, new)
if not result.startswith(raw[:2000]):
    raise SystemExit('upstream notice/header drift')
a.out.mkdir(parents=True)
name = 'luaotfload-harf-plug.lua'
(a.out / name).write_bytes(result)
manifest = {
    'schemaVersion': 1, 'status': 'candidate-not-adopted', 'profile': 14,
    'preimage': {'name': name, 'length': len(raw), 'sha256': sha(raw), 'profile': 13},
    'files': [{'name': name, 'length': len(result), 'sha256': sha(result)}],
    'source': [{'name': f.name, 'length': f.stat().st_size, 'sha256': sha(f.read_bytes())}
               for f in [here / 'rtl-spans.lua', here / 'rtl-spans-bounded.lua', Path(__file__)]],
    'scope': 'Only budget truncation moves before the no-RTL return. Ordinary non-RTL unsupported nodes retain old handling; engine/format/shaping and glyph metrics are unchanged.',
    'license': 'Original Luaotfload GPL-2.0 notices and authors remain intact',
}
(a.out / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
print(json.dumps(manifest))
