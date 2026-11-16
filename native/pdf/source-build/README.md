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
OpenJPEG. `pdfium_wasm_platform.cpp` implements the missing WASM platform hook:
embedded/document fonts and PDFium's built-in fallbacks work; no host font search
or filesystem access is introduced. A public-headers-only old archive cannot
build this source-specific hook or establish physical units.

Run `node native/pdf/fixtures/layout/test.cjs /build/e1/e1.js` inside the existing
browser test image, never with mocked native replies. The fixtures independently
exercise parsed default/explicit/indirect/malformed/UserUnit, the non-inherited
page-tree case, all rotations, crop/nonzero origins, native character matrix/font
size, character/output limits and handle cleanup. They do not establish visual
glyph ink, clipping/style, full font parity, browser cancellation or release
source/notice closure.

`UserUnit` is resolved through this exact revision's `CPDFPageFromFPDFPage`,
`CPDF_Page::GetDict` and `CPDF_Dictionary::GetDirectObjectFor`. Missing page entry
defaults to1; a `/Parent` entry is not inherited. Only finite numeric values in
`(0,75000]` are accepted; malformed/dangling/null/nonpositive entries remain
indeterminate. This adapter reports raw boxes plus the independent scale; it
never rewrites legacy geometry or claims `FPDFText_GetCharBox` measures painted
ink. Font flags/nominal size/matrix/render mode are observations; visual style
and ink remain explicitly unsupported until transforms, clipping, substitution,
Type3 and synthetic styles have a validated measurement path.
