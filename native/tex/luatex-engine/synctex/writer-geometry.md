# Separate Lua ABI5 writer geometry candidate

This is native producer source, not runtime/profile adoption. `build.sh` still
builds ABI4, whose raw ABI1 and unsupported geometry behavior are unchanged.
`build-geometry.sh` builds ABI5 into a fresh explicitly supplied task output.
The default `link.sh`/ABI3 and immutable embedded profile7 stay untouched.

## Derivation from the pinned implementation

Exact preimages and after-images are recorded by `prepare-geometry.py` in its
association, retaining the ABI4 association separately. It reuses the reviewed
bounded sink and actual Lua node ABI; it does not copy pdfTeX node definitions.

- `synctex-luatex.h:53–57`: PDF `CURH = posstruct->pos.h` and
  `CURV = page_height_par - posstruct->pos.v`. These are absolute upright writer
  coordinates after `synch_pos_with_cur`, **not** offsets from the shipped box.
- `pdfshipout.c:126–208`: `page_size` is the actual native writer extent,
  independently selected from positive page dimensions or inferred from shipped
  box/margins. The native reference point and h/v offsets are applied before the
  hlist/vlist coordinate observations. `pdf_set_reference_point` in
  `pdfgen.c:2565–2568` uses the actual PDF origins; no second origin adjustment
  belongs in this sidecar. `pdfpage.c:75–103` applies direction to those positions.
- `synctex.c` emits PDF magnification1000, unit1 and offsets0; the existing query
  exposes these positions in bp. `pdftables.h:104` defines `by_one_bp`.
- `pdfpage.c:28–72,103–130`: page calculations initialize `k1` from the actual
  decimal exponent and `by_one_bp`, then `calc_pdfpos` rounds `pos * k1` into
  decimal PDF coordinates. The sidecar captures `k1 / 10^exponent`, not a guessed
  PDF page-height transform. Per-node checked narrowing remains ABI4-compatible.

Let `B = 1/by_one_bp` (sp→visible bp), `R = k1/10^exponent` (sp→raw PDF),
`S = page_height_par` and `(W,H) = page_size` at actual page begin. For a native
position `(X,Y)`, the observed SyncTeX point is `(h,v)=(B*X,B*(S-Y))`.
Consequently `x=(R/B)*h`, `y=-(R/B)*v+R*S`, so the matrix is
`[R/B,0,0,-R/B,0,R*S]`. This is algebra from both pinned coordinate expressions,
not a calibration to convenient PDF text. Native serialization rounding is
measured by the independent fixture oracle, not hidden in a layout tolerance.

The writer extent in that **same writer coordinate domain** is the ordered
minimum/maximum array `[minH,minV,maxH,maxV]=[0,(S-H)*B,W*B,S*B]`. It is obtained
from actual native extent, not a MediaBox read. In the fallback fixture `S=0`
but actual `H>0`: the box has negative minimumV and matrix translation0. A
MediaBox-height flip is demonstrably incorrect there. In ordinary positive
page-height cases it may numerically coincide with `[0,0,W*B,H*B]`; this does not
change its source/provenance. No crop/rotation/display transform is preapplied.

## Supported domain and conservative fallback

At most512 contiguous real PDF pages are captured. Page begin captures native
state after `init_pdf_pagecalculations`; actual PDF backend end-page records the
page, including its unchanged extent. Per-coordinate observation refuses a
changing `page_height_par` or non-TLT direction. Non-TLT page directions are
unsupported. This does not claim general rotated/vertical text box mapping.

Forms, arbitrary TeX/Lua PDF literals, setmatrix/colorstack/VF-special programs,
raw objects/dictionaries or page-order callbacks conservatively mark the entire
run `unsupported/out-of-domain`. This includes benign literal programs: there
is no attempt to recognize an arbitrary PDF program or claim a single affine
model after it. This is navigation-capability fallback, not a new sandbox claim;
PDF and canonical raw maps remain available after a successful native run.
Unsupported page state has null matrix/box, never a synthetic successful matrix.

The tiny bounded attribute recognizer accepts only inert numeric `/CropBox` and
`/Rotate` fragments (0/90/180/270), at most4096bytes. It validates **PDF name-token
boundaries**, rejects duplicate keys, escaped names, comments, references,
unknown names, exponents and trailing syntax. It is not a general PDF parser or
PDF-fact producer. Both token-list and Lua table attribute emission are checked;
unknown structural injection invalidates geometry, including late page-tree
attributes. Crop and intrinsic rotation belong to the consumer, exactly once.

Raw canonical SyncTeX ABI1 stays unchanged. Geometry length/copy consult the same
exact pass-token raw seal on every read, which itself requires actual postamble,
PDF close, successful native return and output/quota checks. A wrong token,
disabled map, cap/truncation, failed/repeated configuration or run, missing PDF,
native/PDF failure or late quota latch cannot expose sealed geometry. The sidecar
has a131072-byte static serialization buffer plus a fixed512-entry state array;
serialization overflow returns zero, not a successful prefix. Worker destruction
physically ends runaway work; no reset or caller-controlled hook/seal is exported.

## Precisely open origin/renderer cells

**TeX h/v origin, CropBox origin and MediaBox origin are different.** Genuine
compiled controls exercise changing positive/negative TeX origins, page sizes,
nonzero/negative CropBox minima and all four intrinsic rotations. They retain
zero-origin MediaBoxes.

The pinned `pdfgen.c:1803–1809` unconditionally emits
`/MediaBox [0 0 page_size.h page_size.v]`. Its supplied public page-attribute API
can only append another `/MediaBox`, yielding a duplicate dictionary key, not a
validated nonzero-MediaBox writer. ABI5 refuses that override as out-of-domain.
The smallest remaining native question is a real, separately reviewed writer
route for nonzero MediaBox origins; this package does not invent one or modify
writer output solely to manufacture passing geometry.

`geometry-oracle.py` also creates valid, separately labelled PDF controls with
positive and negative **MediaBox** origins, rebuilt xref/trailer, unchanged raw
text matrices and all four crop/rotation configurations. These are actual PDF
bytes but **not native-produced/same-pass sealed output**, and are never used to
claim native nonzero-MediaBox support. Their display equations test an independent
oracle only, not the master C# renderer/CSS pipeline. N-P2 and all-engine A02 stay
open for those unmet cells. Missing physical UserUnit/ink/style facts are not
invented here. No renderer guard is removed and no application phase is enabled.

## Validation ownership

The native fixture tooling lives in `native/tex/synctex-fixtures/lua/`.
`test-geometry.cjs` calls the real separate WASM candidate; `geometry-oracle.py`
reads PDF text matrices/ToUnicode before consulting maps and checks genuine
forward/inverse queries. Root owns registered-app, renderer/CSS and cold-offline
acceptance and any phase14/shared contracts/new closed-profile allocation.
