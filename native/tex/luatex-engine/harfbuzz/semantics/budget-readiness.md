# Separate bounded collector and declaration candidate

The accepted P13 collector returned early when no visited node was RTL. A real
RTL glyph only below depth 32 could therefore be hidden by truncation and leave
the attempt successful. `rtl-spans-bounded.lua` records budget exhaustion
separately and refuses before that early return. Ordinary non-RTL missing
provenance and unsupported nested boxes retain their existing handling.
Shaping, original font/source bytes, glyph metrics and placement are unchanged.

`budget-overlay.py P13_RESOURCE FRESH_OUT` accepts only the exact P13 resource
preimage. It creates a separately pinned derivative and preserves original
upstream notices. It changes neither P13 nor the original `overlay.py` recipe.
The new resource is 45083 bytes, SHA-256
`726563b3936eb23badd8fdafdf6e8d901f96e73aa9f3970987ce614bfc9b6b0b`.

`run-budget.py --runtime PINNED_RUNTIME --component NATIVE_COMPONENT --overlay
BOUNDED_OVERLAY --amiri APPROVED_AMIRI --results FRESH_OUT` runs 21 native
controls in a network-disabled cached Docker toolchain. It checks:

- Original Arabic, mixed text, wrapped two-page RTL and Latin fixtures before
  and after the resource change, with byte-identical PDFs, native maps and
  geometry under the existing fixed-clock preload.
- Genuine TeX-authored 16383/16384/16385 UTF-16 units and
  65535/65536/65537 physical nodes, using the real RTL font and zero-width kerns.
- Depth 31/32/33 using explicitly labelled QA real LuaTeX discretionary nodes
  passed to the actual installed producer/collector functions. The functions
  are located read-only; no producer, collector or result is replaced.
- Retained deep-only-RTL depth33: P13 succeeds, candidate refuses. Ordinary
  non-RTL unsupported glyph/nested-box controls stay successful and unchanged.
- Every over-budget attempt releases no eligible PDF, map or geometry.

The controlled-depth unit is not original-font end-to-end shaping evidence.
The other fixtures preserve genuine source and font bytes. Native unit success
does not establish product-worker or browser acceptance.

The full-font build now accepts explicit `--output-declarations 256`. This
only raises the native selected-output-name table, including its ten fixed
defaults, and exports `oc_lua_output_declaration_limit()==256`. The default128
build reproduces the P13 executable bytes. Input limits, output-byte limits,
heap and VFS new-node quota are unchanged; actual generated carry remains an
independent application policy of 128 files. The new semantic executable is:

| File | Bytes | SHA-256 |
| --- | ---: | --- |
| luatex.js | 193516 | `0cba8f1f2f5a71d703fe80300521dbc4c21d54d016845fa2181cc77f666bb224` |
| luatex.wasm | 6013393 | `406aa23eb3c799b7c1cc0cbb3fa46ab1681880705ed30de35dd288c14576a4d9` |

`../test-declarations.cjs COMPONENT RUNTIME FRESH_OUT TOTAL LIMIT [--work]`
checks totals255/256/257 against the new256 export and127/128/129 against
immutable128. The `--work` check at256 also compiles128 real captured sources
with128 legitimately predeclared carry files and consumed root AUX. It does
not promise that arbitrary projects can create more than128 new VFS nodes.

Reproduction must retain the SDK's source-path comment for its generated
`font-vfs-budget.js` pre-js input (`/task/build-fonts04` for the P13 control).
The source-built control WASM is path-independent; the exact loader includes
that comment. Preserve complete build/source/header/archive manifests with
the candidate. Final resource closure, component admission, provisioning and
published-browser acceptance remain master-owned.
