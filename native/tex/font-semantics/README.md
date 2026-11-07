# Native T2A semantic provenance candidate

This directory owns a private source-built pdfTeX/LaTeX-DVI candidate. It does
not change a product profile, accepted runtime, project source or global font
ToUnicode mapping. Root integration and actual online/cold-offline acceptance
remain separate. The existing failed input-whatsit experiment remains under
`../font-compatibility`; its changed ligatures/raster and unsupported DVI
geometry are negative controls, not accepted behavior.

## Native seam and lifetime

The trusted format binds only T2A `CYRII`/`cyrii` to two genuine `char_given`
meanings. Both existing ligature lookahead branches already consume that command
as a character. `get_next` decodes the two exact internal meanings to original
slots 73/105 before any font/math/table index; numeric `char` explicitly clears
lookahead provenance. `let`/`futurelet` preserve the original meaning, while
`edef` retains the original control-sequence token. A normal `chardef` cannot
create either out-of-range internal meaning.

A checked 5,000,001-byte side table follows real character-node addresses in
the pinned 5,000,000-word native memory arena. Every allocator/free/list flush
clears it. Character-node copies copy it after copying the original words;
ligature components retain their actual nodes. Separate 64-slot scratch follows
the real `hu`/`hc` hyphenation reconstruction. Synthetic hyphens and boundaries
save, clear and restore their semantic slot, including a numeric hyphen that
happens to use slot 73/105. Character/font fields, widths, ligature programs,
discretionary nodes and source positions are never changed. Formats containing
live semantic boxes refuse to dump rather than silently losing provenance.

Shipout derives ActualText from the actual character/ligature components after
ligature construction and line breaking. Untagged components are admitted only
as printable ASCII, with 1–64 components and at least one explicit Cyrillic
tag. A tagged sequence with unsupported components/fonts or a reflected
ligature that TeXXeT destroys reports native semantic status 2. It never guesses
Unicode from nearby source, glyph positions or a global glyph-name replacement.
Arbitrary custom font/expansion combinations and reflected tagged ligatures are
outside the tested semantic domain; these limits are not a full compatibility
claim. Unmarked font-substituted ligatures do not gain semantic authority.

For PDF, BDC/EMC delimit the already formed glyph output with only native text
string flushing. For DVI, shipout emits semantic-only specials around that glyph.
The private converter intercepts only their exact syntax before the historical
`dvi_do_special` graphics-mode transition. It closes/reopens the PDF TJ string
without changing pdfdev's motion state, font, reference, offset, matrix or
rounding accumulators. Generic specials keep their existing dispatch/refusals.

The converter admits only exact uppercase hex UTF-16 for printable ASCII and
U+0406/U+0456, requires a Cyrillic tag, same-page balanced scope, depth 1,
64 scalars/span, 65,536 spans and 262,144 scalars/document. Invalid scope/quota
latches failure and unsupported geometry; later safe content cannot clear an
earlier unsafe transform. Native status and actual successful output/map
lifetimes must both be checked by consumers.

## Reproduction

Use the existing cached `oop-dev-tex-bootstrap:20261112` image, network disabled,
read-only repository/approved inputs, a private writable SDK cache and fresh
output directories. Limit each isolated build to two CPUs and 3 GiB. No host
installation, acquisition, accepted cache write or upstream Makefile execution
is needed. The complete supplied producer `candidate-hashes.json` is verified
before copying its `on/` sources. `prepare.py` then really runs TIE merge,
32 exact-preimage WEB edits, TANGLE, Web2C, fixwrites, splitup and makecpool.
Generated C is never edited. All tool/input/overlay/output hashes are retained.

```sh
python3 /work/native/tex/font-semantics/prepare.py \
  --baseline /inputs/dvi-producer --generators /inputs/generators \
  --out /candidate/producer-source
python3 /work/native/tex/font-semantics/build.py \
  --prepared /candidate/producer-source --out /candidate/producer \
  --cache /candidate/sdk-cache
python3 /work/native/tex/font-semantics/build-converter.py \
  --source /inputs/dvi-source --out /candidate/converter \
  --cache /candidate/sdk-cache
python3 /work/native/tex/font-semantics/matrix.py \
  --runtime /inputs/profile10 --overlay /inputs/font-overlay \
  --component /candidate/producer --out /candidate/native-matrix --faces
```

`probe.cjs` can independently repeat both fixed-clock `format` invocations.
`pdftex.ini` and the existing DVI ini explicitly fix 2025-01-01 00:00 before the
genuine LaTeX dump. New WEB pool strings/checksums and the trusted format hook
require new exact-bound formats; old P10 formats refuse. `compile-reject` checks
invalid numeric tags, `format-reject` checks live-node dump refusal, and
`compile-unsupported` checks reflected tagged ligatures. Original fixtures are
mounted unchanged and hashed after the actual pass.

`observe.py` retains current converter/E1 observations, including old generic
literal rendering/geometry failures. `convert.cjs` accepts an explicit candidate
directory as its fifth argument. `renderer.cjs` verifies exact accepted E1 bytes
and performs Unicode/Latin/ligature search. `adversarial.py` supplies actual
one-page DVI safety controls; it creates independent QA inputs only.
`compare.py` uses already installed Poppler to verify all pages independently,
then checks exact E1 rasters, page facts, native maps, DVI geometry and Unicode
versus ASCII text. ActualText may subdivide logical character boxes differently;
it does not move physical glyphs. App navigation/search acceptance is separate.

## Consumer boundary

Both modes use the modular `createFontSemantics` factory, native MEMFS `/tex`
resources and `/work` unchanged projects. Put the selected exact format at
`/tex/swiftlatexpdftex.fmt`; it selects PDF versus DVI. Configure
`oc_mem_configure(1,1048575,ordinal)`, set the original entry with `setMainEntry`,
then call `compileLaTeX` once on a fresh module. Existing raw SyncTeX ABI is
unchanged. Require `oc_font_semantics_abi()==1` at boot and
`oc_font_semantics_status()==1` after actual native success; counters are
read-only QA, not execution authority. The converter additionally requires
`oc_dvi_graphics_abi()==1`, `oc_dvi_font_semantics_abi()==1`, and final
`oc_dvi_font_semantics_status()==1`, plus the existing successful PDF/geometry
checks. No internal metadata/tagging/success hook is exported.

`compileBibtex` remains exported by the source-built port, but its new consumer
has not been accepted here. The proposed new profile keeps the already accepted
P10 engine/worker for BibTeX, and uses this new component only for the two TeX
phases. Biber, LuaHBTeX, XeTeX and final profile composition belong to other lanes.

The supplied pdfTeX/dvipdfmx sources and their existing notices are preserved.
The native extension files state GPL-2.0-or-later, matching their source-owned
extension role. Complete recipient source/build/notice publication remains a
release integration responsibility. No generated engine, format or output is
stored in this source directory.
