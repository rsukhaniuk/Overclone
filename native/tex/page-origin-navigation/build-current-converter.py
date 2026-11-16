#!/usr/bin/env python3
"""New origin candidate retaining current DVI font semantics and Xe graphics.

Accepted recipes and P13/P14 components remain unchanged. Uses the actual native
parsed dictionary/writer observations; no synthetic geometry or source filtering.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys

p = argparse.ArgumentParser(description=__doc__)
for name in ['source', 'cache', 'out']:
    p.add_argument('--' + name, type=Path, required=True)
p.add_argument('--kind', choices=['dvi', 'xe'], required=True)
a = p.parse_args()
out, source = a.out.resolve(), a.source.resolve()
assert not out.exists(), 'fresh output required'
here = Path(__file__).resolve().parent
parent = here.parent / 'dvi-engine'
raw = (parent / 'build.py').read_bytes()
assert hashlib.sha256(raw).hexdigest() == '8aaffbf0d127a3555415478a314582d43eb21b0db9498c2e9e2a117619c6fa00'
text = raw.decode()
def change(old, new):
    global text
    assert text.count(old) == 1, old[:100]
    text = text.replace(old, new)
change('owned = Path(__file__).resolve().parent', 'owned = Path(' + repr(str(parent)) + ')')
overlay = "str(doc_overlay), '--graphics'], 'geometry-overlay')"
if a.kind == 'dvi':
    overlay += "\n    run(['python3', " + repr(str(here.parent / 'font-semantics/converter-overlay.py')) + ", str(out / 'geometry-overlay')], 'semantic-overlay')"
overlay += "\n    run(['python3', " + repr(str(here / 'converter-origin.py')) + ", str(out / 'geometry-overlay'), str(source)], 'origin-overlay')"
change("str(doc_overlay)], 'geometry-overlay')", overlay)
change("('dpx-dvi.c', 'dpx-pdfdoc.c', 'dpx-specials.c', 'dpx-spc_pdfm.c')", "('dpx-dvi.c', 'dpx-pdfdoc.c', 'dpx-specials.c', 'dpx-spc_pdfm.c', 'dpx-pdfdev.c', 'dpx-pdfparse.c')")
change("cpp = owned / 'oc_dvi.cpp'", "cpp = out / 'geometry-overlay/oc_dvi_origin.cpp'")
change("geometry_flags = ['-I' + str(owned), '-ffile-prefix-map=' + str(out) + '=/inputs/dvi-build'] if has_geometry else []",
       "geometry_flags = ['-I' + str(owned), '-ffile-prefix-map=' + str(out) + '=/inputs/dvi-build', '-ffile-prefix-map=' + str(source) + '=/inputs/dvi-source'] if has_geometry else []")
change("['emcc', '-O2', *geometry_flags", "['emcc', '-O2', '-MMD', *geometry_flags")
change("['em++', '-std=c++17', '-O2', *geometry_flags", "['em++', '-std=c++17', '-O2', '-MMD', *geometry_flags")
exports = ['_malloc', '_free', '_oc_dvi_graphics_abi', '_oc_dvi_origin_abi']
if a.kind == 'dvi':
    exports += ['_oc_dvi_font_semantics_abi', '_oc_dvi_font_semantics_status', '_oc_dvi_font_semantics_spans']
change("exports = ['_malloc', '_free']", 'exports = ' + repr(exports))
out.parent.mkdir(parents=True, exist_ok=True)
recipe = out.parent / (out.name + '-recipe.py')
assert not recipe.exists()
recipe.write_text(text)
cmd = [sys.executable, str(recipe), '--source', str(source), '--out', str(out), '--cache', str(a.cache.resolve()), '--geometry' if a.kind == 'dvi' else '--xdv-geometry']
subprocess.run(cmd, check=True, timeout=300)
sha = lambda f: hashlib.sha256(Path(f).read_bytes()).hexdigest()
receipt = {'schemaVersion': 1, 'status': 'native-candidate-not-adopted', 'proposedProfile': 15, 'kind': a.kind, 'baseAbi': 2 if a.kind == 'dvi' else 3, 'graphics': 1, 'origin': 1, 'fontSemantics': int(a.kind == 'dvi'), 'recipe': sha(__file__), 'privateRecipe': sha(recipe), 'argv': cmd, 'association': sha(out / 'geometry-overlay/association.json'), 'outputs': {n: {'length': (out / n).stat().st_size, 'sha256': sha(out / n)} for n in ['oc-dvi.js', 'oc-dvi.wasm']}}
(out / 'current-origin-build.json').write_text(json.dumps(receipt, indent=2) + '\n')
print(json.dumps(receipt['outputs']))
