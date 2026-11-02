# Lua native SyncTeX candidate (ABI4)

This is a **new, unadmitted candidate**, not a profile7 replacement. The default
`../link.sh` still builds ABI3. `build.sh` writes only its new private build/output
directories and reuses the supplied reviewed Lua engine/dependency archives.
No source/toolchain acquisition is performed.

The checked overlay uses the pinned TeX Live `synctex.c`, **Lua's** unmodified
`synctex-luatex.h` and `texnodes.h`, with the existing read-only raw memory
transport patch and unchanged ABI1 bounded formatter. There is no pdfTeX node
layout or synthetic record generator here. Lua's actual `scaledpos` fields are
`int64_t`; `lua_engine.h` preserves upstream coordinate expressions but narrows
them to checked `int` arguments for canonical `%i` formatting. Without this,
wasm32 varargs consume the high word of the horizontal coordinate as the vertical
coordinate. The independent runtime/PDF/query fixtures catch that defect.
Out-of-int32 coordinates invalidate the optional map instead of truncating it.

`oc_mem_configure(mode, cap, positive_pass_tag)` is one-shot, default disabled.
Disabled/failed host configuration forces upstream command-line mode0 before
TeX runs, so a document's `\\synctex=1` cannot enable it. The actual upstream
postamble return only stages the bounded bytes. The real PDF finalization call
site checks its actual stream's `ferror/fflush/fclose`. Internal finish runs only
after native success, expected output, and all existing output/quota checks.
Public readers also recheck the native output/quota authority. Internal stage,
close, page, begin and finish hooks are **not exported**. Raw map bytes are never
AUX carry or a generated filesystem output; indexes0–9 are unchanged.

## Deliberately unavailable geometry

The sidecar counts only actual completed PDF writer pages, not TeX page labels,
logs, parsed PDF boxes or caller input. It emits schemaVersion1 ordered pages,
each `unsupported/writer-origin-unavailable` with **null matrix and box**.
There is no claimed writer origin/extent/matrix. Above512 pages the optional
sidecar is absent. It shares raw-map sealing, token and complete-or-none lifetime.
Static owned storage is the unchanged1MiB sink plus a128KiB geometry buffer.

Although this source exposes current positions and page dimensions, this adapter
does **not** establish a complete independently validated relation among the
upstream query's visible coordinates, automatic page extents, directions,
magnification, Lua callbacks/literals and user page attributes/origins. Neither a
zero-origin identity nor a PDF MediaBox is substituted for writer provenance.
The geometry/all-engine navigation gate remains **OPEN**. Native success with a
real raw map is separately reviewable.

## Reproduction and scope

Inside the supplied cached Emscripten container, using the already mounted inputs:

```sh
LUA_MAPS_BUILD=/control/r2-lua-maps/build/new-run \
OUT=/control/r2-lua-maps/candidate \
bash native/tex/luatex-engine/synctex/build.sh

RESULTS=/control/r2-lua-maps/evidence/new-run \
bash native/tex/synctex-fixtures/lua/run.sh
```

Both directories must be fresh (the candidate may exist but contain neither
output). The recipe normalizes only the private overlay's embedded `__FILE__`
prefix; independent fresh directories can therefore produce identical bytes.
Dependency archives remain explicitly reused, not relabelled a full source
rebuild. Final private receipts identify sources, pre/postimages, dependency
archives/headers, tools, recipes, candidate and reproduction hashes.

Fixtures and their limits are documented in `../../synctex-fixtures/lua/README.md`.
No C#/worker/profile/cache admission, other engine, PDF-fact adapter, UI/offline
acceptance, distribution/license selection, publication or R2 completion is part
of this producer delivery. Root reviews and integrates; this lane does not
self-accept or self-refine.
