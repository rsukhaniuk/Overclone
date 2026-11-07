# P13 semantic collection limits

`control.tex` is a labelled synthetic one-page source. The test compiles it with
the genuine, hash-pinned P13 engine to obtain its DVI and native source map. No
DVI or native output is committed or supplied as a fabricated compilation result.

`tests/browser/compiler-semantics-bounds.test.cjs` invokes unchanged product
workers with the exact P13 runtime. It appends explicit QA DVI specials to the
genuine control to probe converter parser budgets; these vectors are not proofs
of original-font correctness. Independent adjacent limits are:

- spans: 65,535 / 65,536 / 65,537;
- Unicode scalars: 262,143 / 262,144 / 262,145;
- genuine LuaHBTeX physical-line provenance: 16,383 / 16,384 / 16,385 UTF-16 units.

Converter inputs remain below the existing 8 MiB input limit. Above-limit native
conversion returns zero with semantic status2; the worker reports unsupported
and releases no PDF/maps. Above-limit Lua compilation reports its genuine native
error and releases no PDF/maps. Successful controls run before and after each
matrix, with exact converter PDF/geometry and Lua original-source map/geometry
comparison. Lua's callback observes real node provenance and emitted ActualText;
it does not change shaping, node contents, engine returns or worker replies.

Supply `COMPILER_SEMANTICS_BOUNDS_RUNTIME_DIR` (P13 `pins.json` and `files/`) and
`COMPILER_SEMANTICS_BOUNDS_RESULTS` (a fresh directory under a writable parent).
Run the converter matrix with:

```sh
node --expose-gc tests/browser/compiler-semantics-bounds.test.cjs
```

For Lua, also provide `COMPILER_SEMANTICS_BOUNDS_AMIRI`, the approved original
548,016-byte Amiri font with SHA256
`6988222efdde090e0926cabcaf695c5f551269efbf751b29b097eeb3dbf27e39`, and choose a
separate fresh results directory:

```sh
node --expose-gc tests/browser/compiler-semantics-bounds.test.cjs --lua
```

Use the cached pinned browser-tooling Docker image, networking disabled, a
read-only source/runtime/font mount and an explicitly writable result mount.
No host Node installation, runtime rebuild, download, SDK publish or real browser
is required. Missing explicit runtime input reports a skip, never acceptance.

These tests establish these ABI/parser bounds only. They do not establish Lua's
other node/depth bounds, every script/font, browser isolation, maximum document
support or full R1/R4 release acceptance.
