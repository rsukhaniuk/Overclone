# Native page-origin navigation: explicit origin1 + graphics1 tuple

Bounded native compatibility package for DVI / Xe-XDV / Lua, not all-four-engine,
profile adoption, a new PDF renderer, browser/CSS or physical-rule acceptance.
Base ABIs stay exact2/3/5, graphics capability stays1. The separate tuple adds
`oc_dvi_origin_abi()==1` / `oc_lua_origin_abi()==1`. No private observation, finish,
seal, dictionary or coordinate authority is exported. Root must allocate a new
CLOSED profile; accepted P8/P9/P10 components and all product consumers/pins are
read-only. The older mapped pdfLaTeX adapter remains a separate integration gap.

## Native writer evidence, not an origin label

Changing a PDF MediaBox selects a rectangle in default user space. It does NOT
by itself translate content, change native BOP/SyncTeX coordinates, or justify
adding that rectangle's origin to a matrix. A crop is not a MediaBox either.
These variants retain independently observed native positions/matrix/units and
observe the actual final page-writer box. Writer extents are inverse images of
that native resolved extent through the measured writer relation, not boxes
copied into a sidecar or matrices inferred from PDF text/page size.

### DVI / Xe

The supplied writer unconditionally replaces `@thispage` MediaBox dictionary
values with its native root/per-page extent on final flush. Merely permitting
that dictionary key would falsely advertise support without emitting the origin.
The post-graphics overlay therefore reads the ACTUAL native parsed page object's
MediaBox at final `doc_flush_page`, validates its direct numeric rectangle and
selects it as the native per-page extent. The existing native `.01bp` rounding
and dictionary emitter still produce the final PDF. It then observes the emitted
typed MediaBox and actual resolved extent while the exact page object/ordinal
are available, before releasing it. Each page requires exactly one final witness.

The actual BOP matrix and DVI header scale/magnification remain the accepted
native measurements. The pinned query magnifies node positions but not its
preamble offsets; this composition is unchanged. The final witness recomputes
only the inverse extent under that measured matrix and verifies emitted values
against native rounding. Ordinary extents/PDF/log/geometry stay byte-identical.

The native PDF dictionary parser normally collapses duplicate keys before typed
validation. This variant observes its actual duplicate insertion return value
and latches geometry unsafe. A separate per-page key ledger refuses repeated
MediaBox/CropBox/Rotate writes across native `pdf:put` operations. No textual
special-prefix detector or source parser manufactures a dictionary witness.

### Lua

The supplied writer ALWAYS emits built-in `[0 0 width height]` before arbitrary
page attributes. Simply accepting `/MediaBox` there produces duplicate keys.
The new private page boundary instead consumes the exact expanded native token
string and exact length-bound Lua pageattributes string ONCE, with the native
page ordinal, dimensions and existing active writer observation. A bounded
schema accepts one direct numeric MediaBox and optional CropBox/Rotate; duplicate
keys across BOTH native channels are refused. For a fully valid MediaBox only,
the built-in key is suppressed, so the native writer emits the validated operand
exactly once. Native formatting/output operations remain exact when absent.
Invalid inputs retain unavailable geometry, not an invented successful origin.

`writer_origin.cpp` includes the accepted native geometry implementation under
private getter names: actual begin/observe/end, node/graphics state, units and
raw close/seal authority remain unchanged. It adds final page-writer witnesses
and serializes inverse extents only where MediaBox is explicitly selected. The
matrix still comes from native position/unit/height state, including native
local graphics coordinate observations. Root-page attributes, raw PDF/Lua,
forms, callbacks and generic unsupported domains retain the original unsafe
latches; they cannot be cleared by a later valid page dictionary.

The actual native `pdfpagetree.c:write_pages(parent==0)` now brackets root
attribute emission with a private balanced root witness. The existing native
attribute hook observes BOTH consumed TeX `pagesattr` and Lua `pagesattributes`
spans inside that scope, including values assigned after page shipout. For any
explicit MediaBox in the attempt, exactly one completed root witness and EMPTY
root channels are required. Any nonempty root payload conservatively makes all
geometry unavailable, even contained crop/rotation or a page override; effective
inherited attributes are not implemented or guessed. Empty/reset-before-emission
channels remain supported. No-explicit-origin attempts still use the exact parent
attribute interpretation/serialization, including its prior legacy behavior.

## Supported and refused domains

- Finite direct four-number ordered MediaBox, scalar and transformed writer
  coordinates bounded by1,000,000; independent finite invertible native relation.
- Axis-aligned native writer relation only: positive x, y-down native query frame
  mapped into unrotated PDF raw coordinates. No singular/sheared/reflected frame.
- Lua explicit origins require both final native root attribute channels empty;
  inherited root CropBox/Rotate (including duplicate/cross-channel roots) remain
  unavailable. This is an explicit domain restriction, not an effective-root
  interpretation or a source/setter/prefix heuristic.
- Optional direct ordered CropBox wholly within MediaBox for explicit origins;
  intrinsic Rotate exactly0/90/180/270. Crop/rotation are viewer semantics, not
  baked into `writerToPdfRaw`. Native oracle verifies actual emitted fields and
  intrinsic-coordinate round trips; this is not measured browser/CSS evidence.
- Existing native default PDF user space only. Absence of UserUnit is checked at
  the actual writer/output boundary; even explicit UserUnit1 remains unsupported
  here, as do other arbitrary page/root attributes and indirect/ambiguous keys.
- Lua's consumed-byte schema intentionally accepts only bounded PDF decimal
  numbers and exact unescaped field names. DVI/Xe use their existing native PDF
  parser and typed values. Neither uses substring/name-prefix whitelists.
- At most4096 consumed attribute/special bytes,512 pages/131072 geometry bytes,
  existing raw/map/PDF/log/VFS/runtime budgets and exact token/copy lifetimes.
  Unsafe state stays latched; no successful over-budget prefix is published.
- No token-perfect promise. A repeated source line included on multiple pages
  has no per-occurrence forward selector in pinned query ABI1; validate its
  actual chosen occurrence and inverse queries for each actual PDF point.
  Unboxed/no-glue single-word nodes can retain nearby/shipout source lines.
  Fixtures use ordinary single-line multiword glue anchors and ASSERT each native
  inverse filename/line pair against its independently located source line with
  zero-line tolerance, including the included-file anchors. Expectations are
  established before querying, not derived from returned lines or widened to
  whole files. This precise fixture result does not remove the general limits;
  maps and filename/query admission are never rewritten/widened. Existing Xe non-BMP
  filename and other engine/package gaps remain separate.

## Reproduction and verification

Existing acquired sources/formats/caches only, fresh private output directories,
private copied SDK cache, `EM_FROZEN_CACHE=1`. No input hardlinks are modified.

```sh
python3 native/tex/page-origin-navigation/build-converter.py \
  --source /control/r2-xe-maps/inputs/dvi-source --kind dvi \
  --cache PRIVATE_CACHE --out FRESH_DVI
# Repeat with --kind xe and a different fresh output.
python3 native/tex/luatex-engine/synctex/build-origin.py \
  --work FRESH_LUA_WORK --out FRESH_LUA --cache PRIVATE_CACHE
CANDIDATE=FRESH_LUA DVI_CONVERTER=FRESH_DVI XE_CONVERTER=FRESH_XE \
  RESULTS=FRESH_RESULTS python3 native/tex/page-origin-navigation/run.py
```

Recipes hash-pin accepted parents and make ONLY private post-graphics overlays.
Fresh changed units include actual compiler `.d` header dependencies; unchanged
approved archives/objects are reused under recorded source/header bindings, NOT
claimed as a clean whole-TeX-Live rebuild. Formats/layouts remain unchanged. All
six new JS/WASM files must reproduce byte-identically in independent fresh dirs.
Every existing1200 baseline source file must remain exact.

The coordinated batch retains unchanged portable root/include/BibTeX/PNG flows,
source/intermediate/final map ordinals and a separate native-PDF query oracle;
four-page positive/negative/mixed origins; actual crop/rotation, native mag1200
and Lua token/table channels; malformed/duplicate/budget/unit/domain negatives;
legacy unsafe graphics, native fail/cap/header/config/token/copy/lifetime and
physical native cancellation/fresh recovery. Additional inherited-root checks
exercise19 explicit-origin cases (empty/reset positives, early/late/inherited and
cross-channel negatives),7 full legacy parent byte comparisons, and strict
PDF/query/filename+line checks for5 empty/reset positives. Fixed Date is injected BEFORE each
native module, monotonic timing remains real. Parent/new comparisons use identical
actual intermediate/carry bytes and complete outputs, without normalization.

The fixture-only PDF decoder is derived from the accepted independent oracle,
with explicit numeric failures and a4-page assertion. It decodes final native
PDF objects/ObjStm/Flate/fonts/text matrices/image draws/page boxes BEFORE loading
maps/geometry. No source-generated expected matrix or sidecar-selected PDF point.
Repeated-source forward choices are recorded rather than forced onto a chosen
page. These checks do not establish glyph ink, physical compliance, consumer
profile integration, drawn-raster identity or browser/offline acceptance.

Old failed probes remain failures: a DVI special outside a shipped box was not
an emitted origin; an initial Lua long-string delimiter collided with a PDF array
end; a magnified oversized fixture placed marker text outside its page; early
single-word/repeated-source forward assumptions exceeded native semantics. Each
was diagnosed and retained, not normalized or relabelled as a passing release.
The first combined batch also caught an overbroad new CropBox containment check
on an old zero-origin fixture: accepted graphics allows crops extending outside
MediaBox. The correction limits containment to explicit new MediaBox selection,
preserving the old domain. Both focused native post-config controls and fresh
converter reproductions pass; the earlier batch/candidates/freeze are retained.

Root independent review then identified an actual Lua inherited-domain gap:
prior origin1 emitted `/Pages` CropBox outside an explicit MediaBox or duplicate
CropBox/Rotate across root channels while geometry remained available. Actual
PDF/root/page dictionaries and sidecars are preserved in the private review
probes. The new Lua origin2 adds the actual root writer witness above; prior
origin1 and all older freezes/receipts remain unchanged. The separate inverse
oracle evidence limit was also corrected: native returned filenames and lines
are now asserted as paired records against independent exact source anchors,
not merely recorded.
