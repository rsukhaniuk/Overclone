# Genuine LuaHBTeX candidate checks

The full-font candidate used by the P12 application evaluation has loader
193,208 bytes, SHA-256
`b66ac597a51b06a97598c95db54329005db54ff5631e461e36f1a841bec86d90`,
and WASM 6,013,318 bytes, SHA-256
`7122d44f4d63a4626a3bcc55bb6bb404cfb8d78fd5419dda38849e991e23b7c6`.
It adds the explicit `oc_lua_font_closure_abi()==1` capability. Its genuinely
generated format repeats byte for byte: 12,437,864 bytes, SHA-256
`fed204915ab2eb5b593ee232ae2a484d58e2dc39737b902aefbaf123a2d14e02`.
The earlier narrow candidate measurements below remain historical controls.

`build.py --font-closure` retains the 512 MiB native heap and 8 MiB file/output
bounds, with separately reviewed 128 MiB runtime / 32 MiB work / 160 MiB total
inputs and 64 MiB cumulative SDK allocation work. Macro-disabled builds keep
their original bounds. The native 23-page, 292-face stress document succeeds;
the real P12 published application also passes all 292 selections, preview,
downloads and a full host-stopped cold offline restart. These are bounded font
checks; the Arabic text failure below requires the separate semantics work.

This is a native candidate, **not an adopted application runtime**. The build
recipe is owned by the master. Tests use the existing ABI5, genuine HarfBuzz
module and existing graphics capability; P10 and previous LuaTeX artifacts remain
unchanged. No source rename or engine-selector alias stands in for shaping.

## Native evidence, 2026-11-17

Candidate02 loader: 193,086 bytes,
`a21a8ac177ec58a47da3a6a80e65e5b34aade58f1f810fca80a8a4395b18a12a`.
WASM: 6,013,150 bytes,
`cd52af4a5b53f1d5f7c3c44865db2adb413ea177475a3499ebc19fda612196d0`.
Both the C API and actual native compilation banner identify LuaHBTeX 1.13.0.
The loaded HarfBuzz module reports 2.7.4.

The original P10 format fails honestly: it was written by `luatex`. A genuine
candidate-produced format is required. The independently generated format is
12,276,277 bytes,
`e97ff3a79a0f2bc49ef19ad063a12f4b695e5834d42b5c1421ddb73800578d91`.
No dumped-format header was rewritten. Its production resource-profile integration
and final browser acceptance remain master-owned.

Actual native checks pass for:

- Three unchanged upstream `hb-shape` vectors: Urdu with Noto Nastaliq, normal
  Amiri digits and Amiri `+numr` feature alternates. Glyph count, IDs, UTF-8
  clusters, advances, offsets and optional flags compare exactly. In particular,
  the two-codepoint Urdu input shapes into four positioned glyphs.
- A plain LuaLaTeX PDF and a real `fontspec` `Renderer=HarfBuzz` Arabic PDF.
- Refusal of shell, dynamic-library and forbidden ffi/socket modules. LuaTeX's
  harmless restored wrappers are exercised, rather than requiring their slots
  to remain nil. The same assertions pass against immutable P10 as a control.
- Physical worker termination after a flushed witness from the actual infinite
  native Lua loop, with no eligible output. This is a native cancellation check,
  not the full browser/R4 isolation claim.

**Arabic PDF semantics remain a failing gate.** The accepted pinned E1 renderer
extracts `ملاعلابابحرم` and cannot find the unchanged literal original phrase
`مرحبا بالعالم`. The PDF renders nonblank Arabic glyphs (408 ink pixels at
612×792), but this does not prove searchable original text. Independent Mutool
extracts logical letters without the source space; Poppler's raw extraction
separates Arabic characters with bidi controls/newlines. Inspection of the
decompressed actual PDF shows no `/ActualText` runs, only individual ToUnicode
mappings and one visual-order Arabic `TJ` run, without an encoded U+0020 mapping.
No reversal, source-derived text replacement or query normalization is applied.

Earlier failures are retained: incompatible P10 format; a test parser that did
not capture decimal glyph metrics/absent flags; an unflushed cancellation witness;
and overly strict nil-slot denial assertions. Corrections preserve the native
oracles and test actual refusal. Successful compilation does not close the Arabic
text/search failure. BibTeX with this candidate and broader scripts are not yet
accepted by this test package.

## Inputs and commands

`fixtures/upstream-pins.json` pins the exact already-acquired fonts, wrapper,
license and unchanged oracle JSON from the approved TeX Live source's
`texk/web2c/luatexdir/luaharfbuzz`. Its upstream generator uses `hb-shape` with
OpenType font functions/shaper and UTF-8 clusters. No font binaries or generated
WASM/PDF/format artifacts are stored in this directory.

Run inside the cached network-disabled Docker test environment, at most two CPUs
and 1 GiB, with explicit read-only mounts. Supply:

| Variable | Input |
|---|---|
| `LUA_CANDIDATE_DIR` | Fresh candidate directory with `harfbuzz-build.json`, `luatex.js` and `luatex.wasm` |
| `CANDIDATE_MANIFEST_SHA256` | Independently recorded exact candidate build-manifest hash |
| `P10_RUNTIME_DIR` | Immutable accepted P10 stage; all consumed resources are hash checked |
| `LUAHARFBUZZ_SOURCE_DIR` | The approved source directory described above |
| `RESULT_DIR` | Fresh empty writable output directory |
| `FORMAT_FILE`, `FORMAT_SHA256` | Exact genuinely generated candidate format; omit only for format generation or the original-P10 control |
| `PDF_RUNTIME_DIR` | Accepted E1 files directory, required for the `harfbuzz` PDF text/search/raster gate |

```sh
node test-candidate.cjs format
node test-candidate.cjs shape
node test-candidate.cjs plain
node test-candidate.cjs harfbuzz
node test-candidate.cjs denied
node test-candidate.cjs runaway
```

Each invocation needs its own empty result directory and fresh worker. The master
executes format generation. `P10_BASELINE=1` is allowed only with `denied`, uses
the original pinned P10 loader/WASM/format, and cannot establish candidate shaping.
The `harfbuzz` case currently returns nonzero at the original-text assertion and
retains `native-attempt.json`, actual PDF and `pdf-inspection.json`. It must remain
failing until the genuine output/renderer semantic gap is resolved.
