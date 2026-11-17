# Source-associated PDFium WASM build

This optional build retains E1 ABI1 and its36-byte budget. It does not replace the
accepted renderer automatically. `inputs.json` pins PDFium6688c576 and only the
minimal source/build inputs used here (134,262,596 decoded bytes). V8, XFA,
Fontations, Rust codecs, PartitionAlloc and external system fonts are disabled.
The PDFium/third-party licenses remain in the exact source archives. Release
adoption must retain those corresponding sources and notices, review the link
map, and run existing preview/search/raster/offline acceptance on the new pair.

Explicit acquisition, in a newly created output directory:

```sh
python3 native/pdf/source-build/acquire.py "$PDFIUM_BUILD"
python3 native/pdf/source-build/prepare.py "$PDFIUM_BUILD/source"
```

Use the pinned Emsdk image from `toolchains.env`, with no network,6GiB RAM and
four CPUs. Mount the build directory at `/build` and this repository read-only
at `/workspace`; all build writes stay under `/build`:

```sh
cd /build/source
/build/tools/gn/gn gen out/r2
/build/tools/ninja/ninja -C out/r2 -j4 pdfium
mkdir -p /build/e1
E1_PDFIUM_SOURCE=/build/source E1_PDFIUM_GENERATED=/build/source/out/r2/gen \
  sh /workspace/native/pdf/build.sh /build/source/public \
  /build/source/out/r2/obj/libpdfium.a /build/e1
```

The standalone overrides enable upstream's existing WASM toolchain as the
default, select the container Python, and expose POSIX declarations required by
OpenJPEG. `actualtext.py` applies an exact-source-hash guarded PDFium text fix:
parsed logical `/ActualText` owner spans remain atomic during native bidi line
ordering. Normal visual glyph text retains its previous bidi path. This prevents
double reordering Arabic words and unwanted mirroring of logical replacement
punctuation. It changes neither PDF/source bytes nor JavaScript text.
`../actualtext-lineage.json` records the repaired pair and its real-PDF
text/UTF-16/character/search/raster comparison against the accepted baseline.
`pdfium_wasm_platform.cpp` implements the missing WASM platform hook:
embedded/document fonts and PDFium's built-in fallbacks work; no host font search
or filesystem access is introduced. A public-headers-only old archive cannot
build this source-specific hook or establish physical units.

Run `node native/pdf/fixtures/layout/test.cjs /build/e1/e1.js` inside the existing
browser test image, never with mocked native replies. The fixtures independently
exercise parsed default/explicit/indirect/malformed/UserUnit, the non-inherited
page-tree case, all rotations, crop/nonzero origins, native character matrix/font
size, character/output limits and handle cleanup. These baseline cases do not themselves establish glyph ink, visual style,
full font parity, browser cancellation or release source/notice closure.

`UserUnit` is resolved through this exact revision's `CPDFPageFromFPDFPage`,
`CPDF_Page::GetDict` and `CPDF_Dictionary::GetDirectObjectFor`. Missing page entry
defaults to1; a `/Parent` entry is not inherited. Only finite numeric values in
`(0,75000]` are accepted; malformed/dangling/null/nonpositive entries remain
indeterminate. This adapter reports raw boxes plus the independent scale; it
never rewrites legacy geometry or claims `FPDFText_GetCharBox` measures painted
ink. Font flags/nominal size/matrix/render mode are observations; visual style
remain observations rather than proof of visual style. The schema2 instrument
below measures a conservative subset of actual painted outlines; all other
characters stay explicitly indeterminate or missing.

Generate complete synthetic ActualText/control PDFs into a separate output
directory, then run the literal extraction/search controls on the repaired pair:

```sh
python3 native/pdf/fixtures/actualtext/make_fixtures.py /outputs/actualtext
node native/pdf/fixtures/actualtext/test.cjs /build/e1 /outputs/actualtext /outputs/actualtext-result.json
```

The preserved schema1 receipt records two supplementary-Unicode limitations
of that exact old pair. Schema2 additionally applies `unicode_text.py`: parsed
ActualText accepts valid supplementary scalars; CMap destination strings decode
UTF-16 through PDFium's existing codec **after** bfRange arithmetic. E1's UTF-16
export reads the same parsed text page and native encoder instead of PDFium's
public UCS-2-only text export. Native character indexes and search ranges remain
scalar indexes. A single emoji is one native character and two UTF-16 units.
Malformed surrogate mappings are rejected; no JavaScript text rewriting is used.

`ink.py` adds two read-only guards to the exact matching PDFium source headers.
The complete archive must be rebuilt after applying **all** source patches;
linking newly patched headers against an older archive is invalid. Every patch
checks the full before/after source hashes and is idempotent. Original source
archives, pinned dependency inputs, notices and the exact recipe remain required.

Schema2 painted-ink facts use the embedded static font's actual glyph path,
renderer glyph adjustment, character transform and exact cubic extrema. A native
PDFium winding-fill mask witnesses that contours paint noncancelled ink at each
extreme. Reported uncertainty combines mask resolution, font-outline coordinate
quantization and transform roundoff; it is a measurement error, never a guideline
tolerance. The entire error envelope must fit inside the effective MediaBox /
CropBox intersection. Pure opaque black fill only is admitted. Substitution,
variable/color/SVG/tricky/Type3/vertical fonts, clip paths, nested Forms, page
transparency groups, nondefault graphics effects, extraction deduplication and
unproven later paint remain indeterminate. Only native-generated glyphless separators prove missing ink. A visible glyph
mapped to SPACE/NBSP is measured; unknown ActualText whitespace stays
indeterminate and consumers refuse to treat it as blank. Font metadata never proves visual bold/italic or the ambiguous
1.8 mm font-height requirement.

The schema2 capability `glyphInk: "available"` declares this instrument exists;
each character independently declares its result. Available ink requires a box
and `inkUncertainty: { absoluteErrorRaw, method: "native-outline-fill-witness" }`.
All other statuses require both fields null. The required capability method is
`embedded-static-font-outline-opaque-fill`; visual style stays indeterminate and
line grouping unsupported. Schema1 consumers preserve their exact old shape.
The legacy ABI1 and 36-byte budget stay unchanged. Caps: 100,000 characters,
10,000 page objects, 4,096 outline points/glyph, 1,024 mask-cache entries,
256 by 256 mask pixels and 20 million work units. Existing raster/output caps
still apply; cap exhaustion rejects the complete operation instead of truncating.

Generate and run the genuine schema2 oracle/control sets in the existing test
container, using a fresh output directory:

```sh
python3 native/pdf/fixtures/ink/make_fixtures.py /outputs/ink
node native/pdf/fixtures/ink/test.cjs /build/e1 /outputs/ink /outputs
python3 native/pdf/fixtures/unicode/make_fixtures.py /outputs/unicode
node native/pdf/fixtures/unicode/test.cjs /build/e1 /outputs/unicode /outputs/unicode-results.json
```

Ink controls use self-generated embedded TrueType rectangle/quadratic glyphs,
independent analytic extrema and high-resolution native rasters. They cover scale,
shear, mirror, rotation, winding cancellation, clipping/uncertainty edges,
invisible/stroked paint, occlusion, extraction deduplication, Forms and page groups.
Unicode controls preserve the original literal Arabic/mixed-direction oracles,
add supplementary bfRange/array/BMP-ligature/boundary cases and reject malformed
surrogates. The emitted `native-unicode-transport-v2.json` preserves genuine
`e1_facts`/`e1_text`/`e1_search` replies for managed parser and preview assertions.
These checks establish bounded native facts, not a complete rendered-document
standard, browser acceptance or full release clearance.
