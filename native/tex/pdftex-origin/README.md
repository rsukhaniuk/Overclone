# Direct pdfTeX page origin observer

This derivative observes the semantic pdfTeX engine used in P18/P19. The native
writer still emits its original PDF bytes. The compiler profile, worker transport
and published-browser acceptance belong to integration.

## Writer evidence

The observer records the actual page stream's fixed PDF origin and
magnification, the raw SyncTeX settings written successfully by the same
producer, and the final native page and root dictionary boundaries. The
existing default MediaBox printer supplies its two rounded operands. For an
explicit MediaBox, the observer requires one direct finite ordered rectangle
in the writer's own expanded attribute tokens and verifies that the historical
writer chose to omit its built-in MediaBox. A single direct ordered CropBox
inside that explicit MediaBox and intrinsic rotation 0, 90, 180 or 270 are
supported. Unclassified, duplicate or indirect page keys and any emitted
root-page attributes refuse measured geometry. PDF UserUnit is outside this
scope.

SyncTeX's pinned query normalizes a raw scaled point using the actual header
magnification, unit 1 and zero offsets. The PDF writer independently maps the
same scaled point relative to its initial native PDF origin and emits its
separate page magnification. The ratio and two offsets yield the source
writer → unrotated PDF matrix. MediaBox contributes only the extent in the
inverse of that frame. The consumer uses the actual renderer MediaBox to
cross-check the extent and applies the renderer crop and rotation separately.

Arbitrary emitted literals, page resources, catalog/trailer tokens, raw
objects and Form streams withhold the new measured transform. The literal
check admits complete finite, in-range gray/RGB/CMYK color operators without
any geometry operation. Ordinary previously supported package runs may
retain the old zero-origin method when **every** page used its actual
built-in zero-origin MediaBox, page/root frame witnesses are complete and
unambiguous, and the exact native pass sealed a raw map. An explicit
MediaBox on any page forbids this compatibility path. As before, the
consumer must require the actual renderer's zero-origin MediaBox for legacy
navigation. Navigation within arbitrary content transforms or imported PDF
objects is outside this measured proof.

## Native read interface

- `oc_pdftex_origin_abi()` returns 1 for this exact independently pinned
  component.
- `oc_pdftex_legacy_zero_origin(passOrdinal)` returns 1 only for the
  sealed, intentionally retained ordinary legacy mode. In this mode
  the native geometry length is zero and the native raw map is present.
- `oc_synctex_geometry_length(passOrdinal)` and
  `oc_synctex_geometry_copy(passOrdinal, destination, capacity)` return the
  existing sidecar schema 1. Available rows supply both transforms and
  extents. Unavailable explicit frames may contain only
  `unsupported/out-of-domain` rows. There is no raw-map navigation fallback
  for a measured mode, absent/wrong producer witness, malformed sidecar or
  refused explicit frame. Retain the successfully compiled PDF.
- Wrong pass ordinal, missing/disabled map, unsuccessful native close and
  over-budget sequence do not grant geometry or legacy access. No writer
  observer or seal entry point is exported. The observer keeps bounded
  slots for 512 pages, consumes at most 4096 bytes per recognized attribute
  or literal and copies at most 131072 sidecar bytes. The existing raw
  limits and compiler ownership checks still apply.

## Reproduction and native acceptance

Use the network-disabled, cached `oop-dev-tex-bootstrap:20261112` container
with a fresh private output and private writable copy of the existing SDK
cache. Mount the retained original DVI producer snapshot **producer-third**
and existing native WEB generators read-only. The later reviewed DVI snapshot
is a different source closure and does not reproduce the installed pdfTeX.

```sh
python3 native/tex/font-semantics/prepare.py \
  --baseline "$PARENT_BASE" --generators "$GENERATORS" --out "$PRIVATE/parent-source"
python3 native/tex/font-semantics/build.py \
  --prepared "$PRIVATE/parent-source" --out "$PRIVATE/parent" --cache "$PRIVATE/sdk-cache"
python3 native/tex/pdftex-origin/prepare.py \
  --parent "$PRIVATE/parent-source" --parent-build "$PRIVATE/parent" \
  --baseline "$PARENT_BASE" --generators "$GENERATORS" --out "$PRIVATE/origin-source"
python3 native/tex/font-semantics/build.py \
  --prepared "$PRIVATE/origin-source" --out "$PRIVATE/origin" --cache "$PRIVATE/sdk-cache"
python3 native/tex/pdftex-origin/verify.py \
  --runtime "$EXACT_RUNTIME" --candidate "$PRIVATE/origin" \
  --renderer "$EXACT_E1" --out "$PRIVATE/verification"
```

The parent build must reproduce the installed loader and WASM exactly. The
observer uses new WEB and SyncTeX hooks with TANGLE/Web2C regeneration. The
existing string pool and format stay unchanged. Independent builds compare both
new artifacts in full.

The synthetic two-file acceptance has one real include that builds four source
boxes on four distinct lines. Across four output pages it exercises positive,
negative and mixed MediaBox origins, independent crops, four rotations and
varying native TeX margins. The actual current E1 renderer identifies independent
glyph centers before the oracle loads SyncTeX, and the pinned native area64
query must return the exact authored Unicode path and line and a forward box
containing each center. An earlier fixture repeatedly read the same included
file and the pinned query selected the wrong occurrence on forward lookup.
That known alias case does not establish precise forward navigation.

Paired checks retain exact PDF, raw SyncTeX, compiler log, console and carry
bytes. Additional emitted-frame checks cover native magnification, built-in
and mixed pages, malformed/duplicate/indirect page attributes, late root
attributes and reset, unsupported content and catalog transforms, wrong
ordinals and buffer bounds, no-map mode and a 513-page over-budget attempt.
Existing portable, package and biblatex fixtures preserve their previous
legacy navigation mode.
