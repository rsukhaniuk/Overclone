#!/usr/bin/env python3
"""Private, checked source overlay; never modify generated engine C."""
import hashlib
import json
from pathlib import Path
import shutil
import sys

source, target = map(Path, sys.argv[1:3])
if target.exists():
    raise SystemExit('overlay already exists: refusing to overwrite')
expected_preimages = {
    'lua/luastuff.c': '94270c83db8c4b78c16fe4c66dffc3cc05c1eb081c4e43dd5ca0d13787ecd11a',
    'lua/luainit.c': '00725905b14ff383c053592daa27b2f284ae675a030d464d1a1ea9ab8a28f285',
    'lua/luatex-core.lua': '687aea17875ab61a837a7b10e70e876b03a8e65d5e3836c947df32a53e83785c',
}
for name, expected in expected_preimages.items():
    actual = hashlib.sha256((source / 'texk/web2c/luatexdir' / name).read_bytes()).hexdigest()
    if actual != expected:
        raise SystemExit(f'unapproved source preimage: {name}')
shutil.copytree(source / 'texk/web2c/luatexdir', target / 'luatexdir')
records = {}

def replace(name, old, new):
    path = target / name
    data = path.read_text()
    if data.count(old) != 1:
        raise SystemExit(f'preimage mismatch: {name}')
    original = path.read_bytes()
    path.write_text(data.replace(old, new))
    previous = records.get(name)
    records[name] = dict(path=name,
                        before=previous['before'] if previous else hashlib.sha256(original).hexdigest(),
                        after=hashlib.sha256(path.read_bytes()).hexdigest())

# These are authored .c sources in TL2021, not generated CWEB output.
replace('luatexdir/lua/luastuff.c', '    { "ffi",       luaopen_ffi },\n', '')
p = target / 'luatexdir/lua/luastuff.c'
data = p.read_text()
a = data.index('    if (!nosocket_option) {')
b = data.index('    luaopen_zlib(L);', a)
replace('luatexdir/lua/luastuff.c', data[a:b],
        '    /* Browser build: no socket or mime authority is registered. */\n')
replace('luatexdir/lua/luastuff.c', '    Luas = L;\n',
        '    extern void oc_restrict_lua(lua_State *);\n'
        '    oc_restrict_lua(L);\n    Luas = L;\n')
# Do not leave hidden references to loadlib/C searchers in the registry.
replace('luatexdir/lua/luainit.c', '    if (safer_option) {\n        /*tex library not found in this path */',
        '    if (1) { /* Browser build: C modules are never loadable. */\n        /*tex library not found in this path */')
replace('luatexdir/lua/luatex-core.lua', "    ffi = require('ffi')\n\n    if ffi then\n        for k, v in next, ffi do\n            if k ~= 'gc' then\n                ffi[k] = nil\n            end\n        end\n    end\n\n    ffi = nil\n", '    -- Browser build: FFI is not registered, including during startup.\n')
# The checked upstream self-generator block is executed by regenerate-core.lua.
(target / 'preimages.json').write_text(json.dumps(list(records.values()), indent=2) + '\n')
