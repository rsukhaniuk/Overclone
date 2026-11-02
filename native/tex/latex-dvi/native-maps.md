# Opt-in native LaTeX/DVI map and converter geometry candidates

This is source/build support, not a runtime-profile allocation or application
adoption. Default pdfTeX preparation and converter ABI1 remain selected unless
explicit flags are supplied. Phase12 is reserved for master integration; no new
profile number is allocated here. Existing pdfTeX/Lua binaries, resource pins,
app transports, query ABI1 and PDF/rendering adapters are unchanged.

## Producer: real DVI completion, not PDF-mode inference

`../synctex-producer/prepare.py --dvi-maps` regenerates actual TIE/TANGLE/Web2C
outputs from exact pinned existing sources. Its default off/on preparation path
is unchanged. The separate candidate adds `producer-dvi.ch` at the real WEB
`b_close(dvi_file)` site, after native DVI postamble/font definitions/buffer flush.
It invokes the existing private checked FILE flush/error/close observer. That
observer's historical C name is `oc_synctex_pdf_close`; it observes a real FILE,
not PDF syntax or a caller success flag. The new call is on the **actual DVI
stream**, not a synthetic PDF close. The reviewed bounded sink is not duplicated.

The opt-in driver additionally requires actual native success, a bounded regular
DVI output with real matching header/postamble fields and native page count,
and a bounded native log before permitting the raw seal. Both PDF and DVI real
close hooks remain private. Format/BibTeX never publish raw maps. The candidate
consumes one attempt across LaTeX, INITEX and BibTeX, including failure; repeated
attempts/configuration/API errors revoke map access. Entry names retain exact
UTF8 bytes and reject command syntax, traversal and invalid scalars. Copies use
an exact positive uint32 pass token, cap1048575 and complete-or-none semantics.

No legacy JavaScript network resolver, original pre.js message loop, heap restore
or automatic BibTeX is linked into this candidate. Its native cache-only resolver
searches exact relative project paths and the explicit read-only `/tex/<flat>`
resource namespace; it never resolves a missing project path by basename or
contacts a server. The module has only its private browser/worker MEMFS, not a
host filesystem mount. Expensive work still requires worker destruction on
cancel/deadline. This is not a complete hostile-TeX/release security audit.

The separate producer exposes `oc_dvi_maps_abi() == 1` in addition to unchanged
raw SyncTeX ABI65536. It is modularized as `createDviMaps`, exporting the existing
compile/set-entry operations and FS/MEMFS/FFI for master worker glue. This loader
is deliberately **not** silently compatible with the old global pre.js worker;
master must explicitly admit/bootstrap it and retain actual VFS/log limits.

## Converter: separate ABI2

`../dvi-engine/build.py --geometry` builds ABI2; omission keeps ABI1. It preserves
the accepted explicit-leaf-MediaBox and TrueType glyph-name-bound overlays and
adds exact-preimage native hooks through `dvi-converter-overlay.py`.

Configure geometry once before run with `oc_dvi_geometry_configure(mode,token)`;
mode0/default is disabled. The immutable JSON getters and exact-token
length/copy readers have the same successful native PDF lifetime. A real native
PDF close, actual native return, output checks and all quota checks are required.
Failed/repeated run/configuration, late API mutation, native abort or output
limit cannot expose earlier geometry. No caller finish/seal/page hook is exported.

The schema is the frozen native sidecar schema1:
`{schemaVersion:1,pages:[{page,status,reason,writerToPdfRaw,writerPageBoxBp}]}`.
Pages are contiguous one-based, at most512; the fixed serialization buffer is
131072bytes. Over-cap output is absent, not a successful prefix. The extra native
page-state storage is fixed512 entries. Unsupported entries are
`unsupported/out-of-domain` with null matrix/box. Coefficients/boxes must be
finite within ±1000000 and the matrix invertible.

### Exact coordinate derivation

The actual engine and actual query parser are both relevant:

- Pinned `synctex-pdftex.h` selects DVI mode from native `pdfoutputvalue`.
  `synctex.c` records actual `curh/curv`, unit1, native magnification and
  preamble offsets4736287sp for DVI.
- Pinned `synctex_parser.c:6113–6122` scales **coordinates** by magnification,
  but does **not** magnify the preamble X/Y offsets. This distinction is
  independently checked using an actual `\mag=1200` document; assuming the
  offsets were magnified gives a measurable14.4bp error and is not supported.
- `dpx-dvi.c::do_scales` derives `U` (bp per DVI unit) and header magnification
  from the actual intermediate, not caller metadata. The DVI interpreter exposes
  device coordinates `(U*h_dvi,-U*v_dvi)`.
- `dpx-dvi.c::dvi_do_page` derives real device origins from resolved paper height
  and h/v margins. `dpx-pdfdoc.c::pdf_doc_begin_page` constructs the actual BOP
  matrix `M`; `pdf_dev_bop` concatenates that matrix into the PDF stream.

Let `B=72/72.27/65536`, `m=header_mag/1000`, `O=4736287*B`, and
`R=U/(B*m)`. Query-visible coordinates satisfy
`h=B*m*h_dvi+O`, `v=B*m*v_dvi+O`.
The native converter therefore maps `(R*(h-O),-R*(v-O))` through the **measured
native BOP M**. For `M=[a,b,c,d,e,f]`, the emitted final matrix is
`[aR,bR,-cR,-dR,e-aRO+cRO,f-bRO+dRO]`.
This is composed native engine/query/converter provenance, not a height flip
inferred from PDF dimensions. Nonstandard DVI units/identification are reported
unsupported rather than assumed compatible with this producer's canonical map.

The writer box is the preimage of the actual native converter paper frame under
that final matrix. It is obtained from native writer state before serialization,
not a parsed PDF box or a copied raw MediaBox array. Offset fixtures demonstrate
that its coordinates differ from the emitted MediaBox. It is expressed as
`[minH,minV,maxH,maxV]` in the same unrotated visible SyncTeX domain. No crop,
intrinsic rotation, CSS/raster transform or physical UserUnit is preapplied.

### Supported and unsafe page conditions

Native paper-size/margin specials are measured at the real BOP. Native parsed
`pdf:put @thispage` dictionaries permit only inert direct numeric CropBox and
Rotate0/90/180/270 fields; validation uses the already-supplied converter's actual
PDF object parser/accessors, not a new PDF parser or raw-PDF regex. Unknown names
such as Rotate90/CropBoxExtra, unknown dictionary targets/keys, references,
invalid values and arbitrary PDF/PS/transform/BOP/EOP programs mark navigation
unsupported. Such programs can persist across pages, so fallback is deliberately
run-wide. Per-page numerical/extent failures remain per-page unavailable. The
PDF itself remains usable when native conversion succeeds.

### MediaBox origins and map precision remain honest gaps

CropBox origin, TeX offsets and converter margins are **not MediaBox origins**.
The genuine DVI-mode controls cover changing paper dimensions, nonzero/negative
h/v margins, crop minima, all four intrinsic rotations and magnification.

The pinned DVI route in `dpx-dvipdfmx.c::do_dvi_pages` passes MediaBox origins0,0.
Appending MediaBox via a generic page-dictionary special is not an admitted
writer-origin capability. The accepted explicit-box writer resolves its own box;
that attempted override is unsupported here, never a successful origin fixture.
There is a separate `dpx-mpost.c::mps_do_page` bounding-box route, but it is not
this LaTeX/DVI producer/converter workflow and supplies no DVI map provenance.
Genuine supported nonzero/negative MediaBox origins remain **OPEN**, as do master
renderer/CSS/drawn-page and published cold-offline acceptance. No renderer guard
is removed and rewritten PDFs do not count as same-pass native proof.

Native SyncTeX is line/box provenance, not exact token mapping. The independent
oracle retains a counterexample: an unboxed inline include has a forward parent
box, but inverse selection at its first glyph can return the parent source.
Actual included-file-owned boxes (like normal source-owned paragraph boxes),
local class/style boxes and root boxes are tested separately. No source/log/text
heuristic repairs the weak native match. The counterexample remains an explicit
A01 precision/compatibility limitation, not silently counted as supported.

## Reproduce with finite supplied inputs

Use the existing cached container toolchain. Supply fresh private output paths
and a writable COPY of the existing SDK cache, with `EM_FROZEN_CACHE=1`.
No Makefiles, downloads, host toolchain installations or shared Compose volumes
are required. The engine recipe extracts a finite checked source list and
actually recompiles all selected native C/C++ sources, including supplied xpdf.

```sh
python3 native/tex/synctex-producer/prepare.py \
  --port "$R2_PDFTEX_PORT" --texlive "$R2_TEXLIVE" \
  --generators "$R2_GENERATORS" --out "$R2_PREPARED" --dvi-maps
python3 native/tex/synctex-producer/build-dvi.py \
  --prepared "$R2_PREPARED/on" --out "$R2_ENGINE_OUT" --cache "$R2_CACHE"
EM_FROZEN_CACHE=1 python3 native/tex/dvi-engine/build.py \
  --source "$R2_DVI_SOURCE" --out "$R2_CONVERTER_OUT" \
  --cache "$R2_CACHE" --geometry
```

Native fixture tooling is under `../synctex-fixtures/dvi/`. It uses real WASM,
real DVI/PDF outputs, independent Flate/object/text-matrix inspection and actual
query CLI/WASM. Source filenames use valid `\detokenize` syntax in the synthetic
8-bit pdfTeX class/include controls; no user source is silently rewritten.
Exact legacy PDF/raw-map/DVI/log controls bind wall time BEFORE Emscripten and
the reference VM initialize, keeping monotonic timing real and comparing complete
bytes without normalization. Renderer acceptance needs the genuine supplied
`e1.js/e1.wasm` pair; absence is a reported blocked check, not a mocked pass.

Master must carry the actual selected successful engine map across conversion,
verify the exact native intermediate/input facts, copy/hash the actual final
PDF/map/native sidecar, and assemble the §5.1 geometry wrapper. `finalPass` comes
from the selected ENGINE pass, not the converter ordinal. New DVI navigation
requires available converter geometry; it must never fall back to the legacy
zero-origin PDF writer rule just because a sidecar is absent. All hashes, source
preimages, commands, positive/negative observations and remaining shared changes
belong in the private current-package handoff. No application adoption is implied.
