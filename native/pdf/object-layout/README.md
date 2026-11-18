# Optional direct-image placement observations

This separate, optional `e1_object_layout_facts(page)` export uses the existing
E1 ABI 1, 36-byte budget, immutable output handles, copy/release and reset APIs.
It does not change layout facts 1/2, glyph ink, text extraction, search or raster
behavior. Discover the symbol before calling: its absence is unsupported. The
public-header-only build returns `E1_UNSUPPORTED`, with result zero and no output.
This directory reproduces the renderer used for the bundled caption and image
review. Distribution and corresponding-source verification are tracked
separately in the release milestone.

The corresponding source is the existing pinned PDFium
`6688c576fd851f4159c6fae39396da50939de110`, with the already adopted ActualText,
Unicode and read-only outline guards. The exact matching static archive is
18,061,048 bytes, SHA-256
`a2512bf85e29ea765dffb79aeed34beef2b52d1a439ced40692360309bf9df19`.
No PDFium source/header or archive change is needed. `prepare.py` checks the
frozen wrapper and source/header pins before making a separate overlay; it never
edits the original files. Retain the parent source-build acquisition manifest,
corresponding source, BSD/third-party licenses and source recipe with any release.

## Schema 1

The root has exactly `schemaVersion`, `coordinateSpace`, `scope`, `pageIndex`,
`pageCount`, `media`, `crop`, `effective`, `rotation`, `userUnit`,
`directObjectCount`, `objectTypeCounts`, `images`, `scanComplete`, `limitations`.
Coordinate space is `pdf-raw-unrotated-bottom-left`; scope is
`direct-page-image-placement`. Boxes use `[left,bottom,right,top]` and reflect the
same native MediaBox, optional CropBox, intersection and clockwise page rotation
reported by the existing layout producer. An invalid frame fails the operation.
UserUnit uses the existing exact parsed-page-dictionary policy: missing means 1;
non-numeric, non-finite or values outside `(0,75000]` are indeterminate/null.
It is not inherited from a page-tree parent. Raw coordinates are never rescaled.

`objectTypeCounts` has exactly `text`, `path`, `image`, `shading`, `form`, `unknown`.
These sum to `directObjectCount` and describe **PDFium's parsed top-level native
objects**, including inactive objects, not all content tokens or resources.
`scanComplete:true` means this bounded native inventory was completed. It is not
an assertion that malformed operators were retained or all page paint is known.
Forms are counted but never traversed; a form count can hide any number of nested
images. Paths and shadings are counted without pretending to measure their ink.

Every top-level image occurrence, including repeated uses of the same resource,
has its native zero-based page-object `objectIndex`, `active`, `status`, `method`,
`matrix`, `quad`, `rawBox`, `uncertainty`, `reason`, `limitations`.
Method is always `native-image-unit-square-placement`. Available matrix order is
PDF **`[a,b,c,d,e,f]`**, so `x'=a*x+c*y+e`, `y'=b*x+d*y+f`.
Quad corners are the transformed unit-square points **`(0,0),(1,0),(1,1),(0,1)`**
in that order. `rawBox` is their axis-aligned envelope, not a generic object box.
Reflections, shears and rotations are preserved, not normalized away. Rows have
`status:"available"` only for the conservative domain below; otherwise status is
`indeterminate` and matrix, quad, rawBox and uncertainty are all null.

An available row requires an active direct native image, parsed positive integral
Width/Height, finite nondegenerate native image matrix, no retained native clip,
no content marks or image optional-content entry, no image Mask/SMask/stencil
mask, no page Group entry and the existing parsed default-general-state guard.
Every corner's full numeric error envelope must fit the effective page box.
Metadata inspection never decodes pixels. A malformed/missing dimension or a
degenerate matrix is explicitly indeterminate. Even an available placement does
not establish decoding success, visible paint, image ink or absence of later
occlusion. PDFium can remove redundant clips; the predicate is **no retained
native clip**, not proof that source contains no clipping operation.

`uncertainty` is `{absoluteErrorRaw,method:"pdfium-float32-matrix-bound"}`.
The scalar bounds each x/y coordinate around the **captured native render
matrix**, not an ideal source CTM. The formula is
`8*float_epsilon*S + 16*double_epsilon*S`, where
`S=max(1,abs(a)+abs(c)+abs(e),abs(b)+abs(d)+abs(f))`.
It conservatively allows float32 coefficient representation/rounding around that
matrix, the at-most-three-term unit-square arithmetic, and round-trip `%.17g`
JSON serialization. It is not a bound on an arbitrary chain of source CTM
concatenations, the source decimal literals, image pixels or physical calibration.
The fixed `source-ctm-accumulation-error-unmeasured` limitation appears on the root
and each row, including every available row. Finite error never grants normative
position certainty. Page-frame coordinates share the existing parsed-native
float representation; their source-literal uncertainty is likewise unmeasured.

The remaining fixed limitations are `direct-page-objects-only`,
`native-parsed-inventory-not-content-validation`, `image-placement-not-ink`,
`image-visibility-not-established`, `object-role-unestablished`,
`caption-association-unestablished`, `nested-forms-not-inspected`, and
`clip-or-compositing-uncertain-refused`. Status/reason never implies illustration,
table, caption or source-asset identity. Ordinals are valid only for this exact
PDF, renderer, page and immutable object-facts output hash. A later consumer must
bind both layout and object facts to the same actual compiled PDF/revision,
request, epoch and runtime. Author pairing remains conditional; nearest image is
not a reliable caption association. No font-name style or visual-case inference
is introduced.

## Bounds and lifecycle

Hard limits are 10,000 direct objects, 512 direct image occurrences and 1 MiB of
exact UTF-8 JSON. Lower caller output/live-handle limits still apply. Two bounded
serialization passes check the exact bytes before allocating the output. A
quota failure returns `E1_LIMIT`, result zero, no output handle or prefix success.
Invalid page/closed handles use the existing `E1_HANDLE`. No raster/decode, new
file/network access, new global handles or source mutation occurs. This
synchronous native call shares E1's worker-termination cancellation boundary:
there is no invented cooperative cancellation callback. Reset invalidates its
handles exactly as before; a fresh instance starts empty.

## Reproduction and verification

Use only the cached pinned Emsdk 6.0.13 image, no network, 2 CPUs and 3 GiB RAM.
For example, from the repository root:

```sh
python3 native/pdf/object-layout/build.py \
  --pdfium-source /tmp/oop-r2-ink-overlay/source \
  --destination /tmp/oop-r2-object-layout-20261118/build01
```

The build verifies the frozen renderer by reproducing its exact loader and WASM
before it links the overlay against the same archive and compatible headers.
Repeat in a fresh independent destination and compare candidate hashes. The
receipts record input pins, commands, wrapper overlay pins and output hashes.

```sh
python3 native/pdf/object-layout/make_fixtures.py "$FIXTURES"
node native/pdf/object-layout/test.cjs "$BUILD/parent" "$BUILD/candidate" \
  "$FIXTURES" "$RECEIPT" "$RETAINED_ARABIC_PDF"
```

The optional final argument is the retained PDF from the unchanged original
LuaHBTeX Arabic regression. The test verifies its digest and compares actual
text, literal search, raster and layout bytes with those returned by the frozen
renderer. The synthetic fixtures independently exercise image matrices, page
frames, paint and clipping refusals, exact caller output size, object and image
quota edges, output ownership, and handle reset. A known unrotated image also
compares the placement envelope with observed raster pixels. The receipt
identifies the exercised native capabilities and fixtures. Product adoption
requires separate browser and consumer validation.
