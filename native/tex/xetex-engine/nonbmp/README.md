# Explicit XeTeX non-BMP filename candidate

This candidate fixes original non-BMP UTF-8 selected filenames without renaming
projects, rewriting source or changing accepted runtime profiles. It is built
from the accepted Xe native-map source and the existing approved dependencies.
The accepted `runtime.cpp`, ordinary builders, pins and defaults are unchanged.

The retained engine had four related boundaries:

- `scan_file_name` rejected scalar values above U+FFFF.
- `more_name` accepted only a UTF16 code unit, losing a scalar from the input buffer.
- `pack_file_name` encoded individual UTF16 units as up to three UTF-8 bytes.
- The handwritten `gettexstring` surrogate conversion omitted the U+10000 offset.

`nonbmp.ch` admits Unicode scalars, stores pairs in the native UTF16 string pool
and reconstructs UTF-8 filenames with four-byte encoding. `prepare.py` checks the
accepted source/generation/tool receipts, applies the overlay with genuine TIE,
OTANGLE and Web2C, and fixes the handwritten encoder with a checked preimage.
It never edits generated C directly. The candidate-only copy of the runtime
removes the old BMP restriction and exports `oc_xe_nonbmp_abi()==1`.

All existing valid-UTF-8, path, file-count, input/output byte, heap, shell and
network restrictions remain. Malformed UTF-8 and traversal are still refused.
An output that would overwrite its input is refused before deletion.

## Reproduce with existing cached inputs

Run inside the existing pinned offline Emscripten image. Inputs and their recorded
tool/header paths must be available read-only at the paths in the retained
receipts. Use a fresh private output directory and private SDK cache. No script
downloads dependencies or installs host tools.

```sh
python3 native/tex/xetex-engine/nonbmp/prepare.py \
  --base "$XE_MAPS_PREPARED" \
  --runtime native/tex/xetex-engine/runtime.cpp \
  --out "$XE_OUTPUT/prepared"

python3 native/tex/xetex-engine/nonbmp/build.py \
  --prepared "$XE_OUTPUT/prepared" \
  --dependencies "$XE_PROVED_DEPENDENCIES" \
  --retained "$XE_RETAINED_BUILD" \
  --texlive "$TEXLIVE_SOURCE" \
  --cache "$XE_PRIVATE_SDK_CACHE" \
  --out "$XE_OUTPUT/engine"
```

`--retained` is the previous **build** containing `hbinclude/`, `freetype/include/`
and Xpdf headers. It is not the original browser-port source directory.
The build recompiles all 28 engine/source units. It verifies all 563 dependency
source/header proofs and the seven archives before reusing those archives.
Its receipt includes source/object/header hashes, exact commands and output hashes.

Provision the same accepted resource tree at `/inputs/suite`. Generate a genuine
fresh format using the existing native fixture, then run the new native matrix:

```sh
ENGINE="$XE_OUTPUT/engine" RESULTS="$XE_OUTPUT/format" TZ=UTC \
  node -r ./native/tex/synctex-fixtures/dvi/fixed-clock.cjs \
  native/tex/synctex-fixtures/xe/test-format.cjs

python3 native/tex/xetex-engine/nonbmp/run.py \
  --engine "$XE_OUTPUT/engine" \
  --format "$XE_OUTPUT/format/xelatex.fmt" \
  --reference-engine "$XE_ACCEPTED_MAP_ENGINE" \
  --reference-format /inputs/suite/files/xelatex.fmt \
  --results "$XE_OUTPUT/native-results"
```

The reference engine uses the existing `engine.js`/`engine.wasm` test layout.
The accepted runtime loader and WASM may be copied under those exact names only
after verifying their inventory hashes; bytes are not modified.

`test-convert.cjs` separately converts the genuine intermediate through the
accepted converter using the actual entry `тези/розділ 😀.xdv`:

```sh
CONVERTER="$XE_ACCEPTED_CONVERTER" XDV_INPUT="$XE_OUTPUT/native-results/positive.xdv" \
  RESULTS="$XE_OUTPUT/pdf" node native/tex/xetex-engine/nonbmp/test-convert.cjs
```

The converter test layout is `oc-dvi.js`/`oc-dvi.wasm`, again using exact accepted
bytes. Set `PROJECT_FONTS=1` with `font.xdv` to seed the four captured project
font aliases. Repeat with `bib-pass3.xdv` for the genuinely resolved bibliography.
An independent extractor must find `ROOTMARK`, `INCLUDEMARK`, Ukrainian text,
and `BIBMARK` plus resolved `Citation: [1]` for the bibliography case. This does
not claim a rendered-layout or source/PDF geometry rule.

## Native acceptance, 2026-11-17

- Fresh source generation and 28-unit build passed after byte-reproving dependencies.
- Genuine INITEX passed. Its format is byte-identical to accepted P10:
  33,969,814 bytes, SHA-256
  `dc97f676666e6c04f6eb4552c26cfdbff009817bac98dc4ed2325245b48a5637`.
- The 35-command native matrix passed with zero skips: original
  `тези/розділ 😀.tex`, emoji include and project font names, exact native-map paths,
  filename/line diagnostics, three genuine TeX passes around BibTeX, invalid UTF-8,
  traversal, overlimit input, output collisions and stale-output replacement.
  U+10000/U+10FFFF and emoji-directory cases passed too.
- Physical Node-worker termination was witnessed after actual native `write16`;
  there was no terminal runaway receipt and a fresh worker recovered successfully.
- The old accepted engine still rejects the emoji root. Complete BMP XDV, log,
  AUX and raw-map bytes compare exactly against that engine with a fixed clock.
- Three actual PDF conversions passed, including non-BMP project fonts and the
  resolved bibliography. Independent MuPDF extraction passed all three.

Candidate outputs:

| File | Bytes | SHA-256 |
| --- | ---: | --- |
| engine.js | 69,948 | `38ba92890c5de6e957254dcda1c1345a75b35dabaa46d77fc4835f3ad4f33601` |
| engine.wasm | 3,302,272 | `5a08c4d0a2109adc43e087889be208deb71cf4ba55692b41d58da2c8284f1e8e` |

The first native matrix incorrectly expected citation resolution on the first
post-BibTeX TeX pass. The corrected runner requires the next genuine TeX pass;
the earlier failure is retained separately. No compiler behavior or expectation
was weakened to achieve the final pass.

## Master integration still required

The master must allocate a new closed profile/component identity and require the
new native capability before admitting these bytes. Do not change P10 or the
separate P11 candidate in place. Only then may the profile-aware selected-root
admission replace the unconditional Xe surrogate rejection in
`NativeCompilerSuiteContract.CreateEntry`. Old profiles must keep refusing it.
Update component pins/worker phase admission together and reject mixed closures.

Native success is not product adoption. Actual C#/worker transport, diagnostic
source actions, current source/PDF navigation, captured project-font conversion,
automatic provisioning and full host-stopped cold-offline browser acceptance
remain master-owned gates. No browser, complete R1 or redistribution claim is made.
