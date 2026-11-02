#!/usr/bin/env python3
"""Separate ABI5 overlay over the accepted ABI4 adapter, exact preimages only."""
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys

source, out = map(Path, sys.argv[1:3])
here = Path(__file__).resolve().parent
web = source / 'texk/web2c'
subprocess.run([sys.executable, str(here / 'prepare.py'), str(source), str(out)], check=True)
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()
extra = {
    'luatexdir/pdf/pdfobj.c': 'bb0d31803676785fd484a976370495bd082a07efbf88e23a3209d3c0e1376620',
    'luatexdir/pdf/pdfpage.c': '2de12bef5a595ac3c558b3c109d32ad57d4edae6513657eff2815bdba2b3fe49',
    'luatexdir/pdf/pdfliteral.c': 'c0a947d36c693ac22716be85c41e835ab770a3658ca1d34afca503afb6df917d',
    'luatexdir/lua/lpdflib.c': '0ad55c7b5f4b0de2aeec34b24aa96cd175009a832c15a533820a62567fc93494',
    'luatexdir/lua/luanode.c': '68b8cdb8c3ae4a43e85ce0420be80d1626ea64f0400d123e58d93d6497e33acc',
    'luatexdir/pdf/pdftables.h': '51c49336d6a6b5a4d58dfbf2f1ede03f5dac32136801c51d6eb1d2cadeea3983',
}
for name, expected in extra.items():
    if sha(web / name) != expected:
        raise SystemExit('source preimage mismatch: ' + name)
    p = out / name
    p.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(web / name, p)
try:
    association = json.loads((out / 'association.json').read_text())
except (OSError, ValueError) as error:
    raise SystemExit('invalid ABI4 association') from error
# Preserve the ABI4 overlay identity as a separate input, not relabelled ABI5.
(out / 'abi4-association.json').write_bytes((out / 'association.json').read_bytes())

def replace(name, old, new):
    p = out / name
    raw = p.read_bytes()
    old, new = old.encode('ascii'), new.encode('ascii')
    if raw.count(old) != 1:
        raise SystemExit('exact geometry overlay mismatch: ' + name)
    p.write_bytes(raw.replace(old, new))

for name in ('pdf/pdfgen.c', 'pdf/pdfshipout.c', 'pdf/pdfliteral.c', 'pdf/pdfobj.c', 'lua/lpdflib.c', 'lua/luanode.c'):
    replace('luatexdir/' + name, '#include "ptexlib.h"', '#include "ptexlib.h"\n#include "writer_geometry.h"')
replace('luatexdir/pdf/pdfshipout.c',
        '        oc_lua_maps_ship_page(total_pages);',
        '        oc_lua_geometry_end(total_pages, pdf->page_size.h, pdf->page_size.v);')
replace('luatexdir/pdf/pdfgen.c', '    ensure_output_state(pdf, ST_HEADER_WRITTEN);\n    init_pdf_pagecalculations(pdf);',
        '    ensure_output_state(pdf, ST_HEADER_WRITTEN);\n    init_pdf_pagecalculations(pdf);\n'
        '    if (global_shipping_mode == SHIPPING_PAGE)\n'
        '        oc_lua_geometry_begin(total_pages + 1, pdf->page_size.h, pdf->page_size.v,\n'
        '            page_height_par, pdf->pstruct->k1 / ten_pow[pdf->pstruct->pdf.h.e],\n'
        '            1.0 / by_one_bp, page_direction_par == dir_TLT && pdf->posstruct->dir == dir_TLT);\n'
        '    else oc_lua_geometry_unsafe(); /* form maps have their own local coordinates */')
replace('luatexdir/pdf/pdfgen.c', '        pdf->last_pages = pdf_do_page_divert(pdf, pdf->last_page, location);',
        '        if (callback_id || location) oc_lua_geometry_unsafe();\n'
        '        pdf->last_pages = pdf_do_page_divert(pdf, pdf->last_page, location);')
replace('luatexdir/pdf/pdfgen.c', '    char *s = tokenlist_to_cstring(p, true, &len);',
        '    char *s = tokenlist_to_cstring(p, true, &len);\n'
        '    if (len > 0) {\n'
        '        if (p == pdf_page_attr || p == pdf_pages_attr)\n'
        '            oc_lua_geometry_attributes(s, (size_t)len);\n'
        '        else oc_lua_geometry_unsafe(); /* arbitrary dictionary/program injection */\n'
        '    }')
replace('luatexdir/pdf/pdfgen.c', '            pdf_out_block(pdf, ls, len);',
        '            if (!strcmp(s, "pageattributes") || !strcmp(s, "pagesattributes"))\n'
        '                oc_lua_geometry_attributes(ls, len);\n'
        '            else oc_lua_geometry_unsafe();\n'
        '            pdf_out_block(pdf, ls, len);')
replace('luatexdir/pdf/pdfliteral.c', 'void pdf_literal(PDF pdf, str_number s, int literal_mode, boolean warn)\n{',
        'void pdf_literal(PDF pdf, str_number s, int literal_mode, boolean warn)\n{\n'
        '    oc_lua_geometry_unsafe(); /* includes setmatrix, colorstack and VF specials */')
replace('luatexdir/pdf/pdfliteral.c', 'void pdf_out_literal(PDF pdf, halfword p)\n{',
        'void pdf_out_literal(PDF pdf, halfword p)\n{\n    oc_lua_geometry_unsafe();')
replace('luatexdir/lua/lpdflib.c', 'int luapdfprint(lua_State * L)\n{',
        'int luapdfprint(lua_State * L)\n{\n    oc_lua_geometry_unsafe();')
replace('luatexdir/lua/luanode.c', 'void lua_pdf_literal(PDF pdf, int i, int noline)\n{',
        'void lua_pdf_literal(PDF pdf, int i, int noline)\n{\n    oc_lua_geometry_unsafe();')
for signature in ('static int l_immediateobj(lua_State * L)', 'static int table_obj(lua_State * L)'):
    replace('luatexdir/lua/lpdflib.c', signature + '\n{',
            signature + '\n{\n    oc_lua_geometry_unsafe();')
replace('luatexdir/pdf/pdfobj.c', 'void pdf_write_obj(PDF pdf, int k)\n{',
        'void pdf_write_obj(PDF pdf, int k)\n{\n    oc_lua_geometry_unsafe();')
# Use the exact same header-derived coordinate expressions as ABI4, with a
# read-only observation of the actual per-node coordinate frame in ABI5 only.
replace('synctexdir/synctex.c', '#include "oc_synctex_internal.h"',
        '#include "oc_synctex_internal.h"\n#include "writer_geometry.h"')
# The inline checked-narrowing header is shared by ABI4, so observe at the
# actual upstream current-coordinate call sites using an ABI5-only header flag.
association['candidateAbi'] = 5
association['abi4AssociationSha256'] = sha(out / 'abi4-association.json')
association['upstream'] += [{'path': n, 'before': h} for n, h in extra.items()]
for entry in association['upstream']:
    entry['after'] = sha(out / entry['path'])
(out / 'association.json').write_text(json.dumps(association, indent=2) + '\n')
