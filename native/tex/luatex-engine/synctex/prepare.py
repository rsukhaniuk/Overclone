#!/usr/bin/env python3
"""ABI4-only overlay. Reuses the reviewed transport patch, NOT pdfTeX node ABI."""
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys

source, out = map(Path, sys.argv[1:3])
here = Path(__file__).resolve().parent
web = source / 'texk/web2c'
pins = {
    'synctexdir/synctex.c': 'c484955177f3e37a67aaecc4586592777ee4ccfaada1c08a7a9dc47e19238ecb',
    'synctexdir/synctex-luatex.h': '04fd86e540448bc9059e143a33e78bd32e9a5edd3487bb4ae59bc8ea5f525576',
    'luatexdir/pdf/pdfgen.c': '09c7d2c1f8869106768f7182ce4a29673a72e5bd9dac22d13687f5a1eead8367',
    'luatexdir/pdf/pdfshipout.c': '95e70da3d50dd9149ac7d8f7031a0fa961d63cc6efd36b0df4813e7ae6eb8e87',
    'luatexdir/pdf/pdftypes.h': '53596bccfa7223d974ed72a564ece012188991bc7a961d098c437dc34b62dedc',
    'luatexdir/tex/texnodes.h': '01d090c17223168d9dbef5fcc1a89ad71bd672937864e5f7615602d3b1e3b25e',
    'luatexdir/pdf/pdflistout.c': 'bee8864e8b9b82b0fded00d177633d27afb4944b82858da366073c308402cd80',
    'luatexdir/tex/mainbody.c': '079db9ba2845b59448561cb41848f1453b8f7aeca388455e9ad546641baf87d6',
    'luatexdir/tex/texfileio.c': 'b16f569cd44ea0dd92cdd043ff8eab52412b8244af759cdb150ab012190c52db',
}
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()
for name, expected in pins.items():
    if sha(web / name) != expected:
        raise SystemExit('source preimage mismatch: ' + name)
if out.exists():
    raise SystemExit('refuse existing overlay')
for name in pins:
    p = out / name
    p.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(web / name, p)
patch = here.parent.parent / 'synctex-producer/patches/synctex-memory.patch'
subprocess.run(['patch', '--batch', '--fuzz=0', '-p1', '-i', str(patch)], cwd=out, check=True)

def replace(name, old, new):
    p = out / name
    # Upstream contains legacy non-UTF8 copyright bytes; preserve them exactly.
    text = p.read_bytes()
    old, new = old.encode('ascii'), new.encode('ascii')
    if text.count(old) != 1:
        raise SystemExit('exact overlay preimage mismatch: ' + name)
    p.write_bytes(text.replace(old, new))

replace('synctexdir/synctex.c', '#   include SYNCTEX_ENGINE_H',
        '#   include SYNCTEX_ENGINE_H\n#include "oc_synctex_memory_sink.h"\n#include "oc_synctex_internal.h"')
# A document may disable maps but cannot turn a disabled/failed host mode on.
replace('synctexdir/synctex.c', '    if (SYNCTEX_NO_OPTION == synctex_options) {',
        '    if (!oc_synctex_enabled()) synctex_options = 0;\n'
        '    else synctex_options = -1; /* canonical raw v1; no document printf */\n'
        '    if (SYNCTEX_NO_OPTION == synctex_options) {')
replace('luatexdir/pdf/pdfgen.c', '#include "ptexlib.h"',
        '#include "ptexlib.h"\n#include "oc_synctex_internal.h"')
replace('luatexdir/pdf/pdfgen.c', '                close_file(pdf->file);',
        '                oc_synctex_pdf_close(pdf->file);')
replace('luatexdir/pdf/pdfshipout.c', '#include "ptexlib.h"',
        '#include "ptexlib.h"\n#include "lua_maps.h"')
replace('luatexdir/pdf/pdfshipout.c', '    backend_out_control[backend_control_end_page](pdf);',
        '    backend_out_control[backend_control_end_page](pdf);\n'
        '    if (shipping_mode == SHIPPING_PAGE && pdf->o_mode == OMODE_PDF)\n'
        '        oc_lua_maps_ship_page(total_pages);')
(out / 'association.json').write_text(json.dumps({
    'schemaVersion': 1, 'upstream': [{'path': n, 'before': h, 'after': sha(out / n)} for n, h in pins.items()],
    'transportPatch': {'path': str(patch), 'sha256': sha(patch)},
    'engineHeader': 'synctex-luatex.h (unmodified upstream) via lua_engine.h checked int64-to-int32 narrowing',
}, indent=2) + '\n')
