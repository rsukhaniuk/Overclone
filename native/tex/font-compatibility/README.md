# T2A font closure and semantic fidelity gates

This directory contains native build/test tooling. It does not change application
formats, trusted runtime profiles, worker limits or the bundled default.

`inventory.py` derives the original T2A `cmr`, `cmss` and `cmtt` declarations from
integrity-checked FD/map bytes. The P10 baseline has **292 direct selections,
241 unique metric names, and only two complete selections**. Its three upstream
substitution declarations are recorded separately and never advertised as new
real faces. `coverage.lock.json` freezes that baseline/proposal, not the current
state of private acquired inputs. Cached index sizes are not acquired hashes.

With the separately authorized inputs, `acquire.py` retrieves exact approved
outlines and LH source/notice inputs under a 40 MiB download bound. It records
redirects, lengths, SHA-256 values and failed mirror attempts. `--resume` verifies
completed files and retries failures. `extract.py` rejects traversal, links,
special files, duplicate members and expansion above 96 MiB. Neither script
installs dependencies or changes the runtime.

The historical LH package contains MF programs rather than the required full
TFM set; the current package supplies only nine T2A metrics. The reproducible path
therefore builds genuine native Metafont from the existing TeX Live source and
cached native generator configuration, then uses original LH `fikparm` with
unmodified plain/CM/EC MF source data. `generate-metrics.py` records the actual
per-font input files and emits all 241 metrics. Generated `larm1000.tfm` and
`labx1000.tfm` must equal the accepted originals byte for byte.

`overlay.py` verifies those controls and builds a **font-only additive candidate**:
239 new metrics plus 237 new outlines, **476 files / 35,622,744 bytes**. It validates
PFB structure, existing-resource equality and source-receipt associations.
No font alias, patched outline, format or executable is included in this overlay.

## Reproduction

Supply explicit cached/acquired directories; no private paths are built in:

```sh
python3 native/tex/font-compatibility/inventory.py \
  --runtime "$COMPILER_RUNTIME" --metric-archive "$CACHED_SMALL_LH_ARCHIVE" \
  --outline-index "$CACHED_CM_SUPER_INDEX" --output "$NEW_INVENTORY"

# Run acquisition only after explicit approval for these source/font inputs.
python3 native/tex/font-compatibility/acquire.py \
  --approved-acquisition --output "$NEW_PRIVATE_INPUTS"
python3 native/tex/font-compatibility/extract.py \
  --acquired "$NEW_PRIVATE_INPUTS" --output "$NEW_EXTRACTED_INPUTS"

# Copy cached native generator output with symlinks preserved into a NEW build.
cp -a "$CACHED_NATIVE_GENERATORS/." "$NEW_NATIVE_BUILD/"
# Inside the existing network-disabled Docker image, source mounted /source:ro
# and the new build mounted /output:rw:
make -C /output/texk/web2c -j2 mf-nowin

# Inside that cached Docker image, with explicit source-data mounts:
python3 native/tex/font-compatibility/collect-mf-inputs.py \
  --plain "$PLAIN_MF_SOURCE" --cm "$CM_MF_SOURCE" --ec "$EC_MF_SOURCE" \
  --output "$PLAIN_CM_EC_MF_DATA"
python3 native/tex/font-compatibility/generate-metrics.py \
  --mf "$NEW_NATIVE_MF" --mf-inputs "$PLAIN_CM_EC_MF_DATA" \
  --lh "$EXTRACTED_LH_SOURCE" --inventory "$NEW_INVENTORY" \
  --output "$NEW_GENERATED_METRICS"
python3 native/tex/font-compatibility/overlay.py \
  --runtime "$COMPILER_RUNTIME" --inventory "$NEW_INVENTORY" \
  --acquired "$NEW_PRIVATE_INPUTS" --generated "$NEW_GENERATED_METRICS" \
  --output "$NEW_FONT_OVERLAY"
```

All executions use cached Docker, networking disabled, read-only source inputs,
fresh private output and at most two CPUs / 2 GiB. Existing MF source *data* can be
used as explicit inputs; no host Metafont executable is used or installed. Pin
those inputs and their original notices too; source/release clearance is separate.

## Completed native observations, 2026-11-17

The original Unicode `declared-faces.tex` exhausts all 292 direct selections on
pdfTeX and DVI without missing glyph or substitution warnings. The actual PDFs
have 23 pages; all fonts are embedded and have Unicode maps. The DVI converter
keeps every page's geometry available with the resource-only overlay.

The separately labelled `declared-font-commands.tex` uses literal Cyrillic
commands and explicitly chooses CM families. It verifies the same resources on
XeTeX and the private full-font LuaHB candidate. This is **not a repair of imported
Unicode/T2A source**. That unchanged Xe input fails; inputenc is ignored and the
Unicode codepoints do not select 8-bit TFM slots. The earlier implicit `lmr` default
warning is retained separately from the deliberately selected CM resource control.

The complete Lua runtime view is about 115.2 MB before project/carry; the original
96 MiB native bound refuses it. Existing engine filtering already removes other
engines/formats/ICU; duplicate logical data accounts for only 175,900 bytes. Dropping
necessary fonts or existing font/package capability is not a closure strategy.
Master-reviewed private LuaHB candidates add explicit 128 MiB runtime / 32 MiB
work / 160 MiB aggregate input limits and retain the 512 MiB heap, 8 MiB per-file
output and all other existing limits. They are not product/profile admission.

The first full-font candidate passed all 292 faces in 21 independent bounded
group PDFs but failed the single 23-page stress document at its original 16 MiB
cumulative SDK backing/copy-work limit. That failure remains a non-pass. The next
reviewed candidate uses a 64 MiB cumulative allocation-work ceiling; the same
stress document then succeeds (2,730,392-byte PDF, 16,973,982-byte at-return allocation
work, 194,772,992-byte native heap, 527,320 KiB process peak RSS). Browser/C# copy
coexistence and final application acceptance are not measured by this native probe.

`quota.cjs` checks real native role/aggregate seeding just below, at and above each
bound. `allocation-quota.cjs` installs the real SDK guard by compiling an actual
document, then checks allocation-work bounds and the unchanged per-file refusal.
These are native/SDK quota controls, not browser/hostile-TeX cancellation proofs.
`unicode-probe.cjs` records budget snapshots both **at compile return** and **after
private QA reads**, because the latter themselves consume copy accounting.

## Original Cyrillic semantics remain open

`actualtext.tex` is an experimental trusted-format hook, never a project rewrite.
`run.py` builds matched unmodified and candidate formats and checks the unchanged
237-byte original source, mixed Latin/Cyrillic text and strict mixed-script
ligatures through actual engines, current pinned E1, and independent Poppler.

```sh
python3 native/tex/font-compatibility/run.py --preflight \
  --runtime "$COMPILER_RUNTIME" --renderer "$E1_FILES" --results "$NEW_RESULTS"
python3 native/tex/font-compatibility/run.py \
  --runtime "$COMPILER_RUNTIME" --renderer "$E1_FILES" --results "$NEW_RESULTS"
```

The executed candidate is **rejected** (exit 2, `candidateAccepted:false`):

- ActualText fixes Cyrillic І/і extraction/search in E1 and Poppler while retaining
  Latin I/i; the original ordinary pdfTeX raster can remain identical.
- Native whatsits break original mixed-script `fі`, `ffі`, `fіlm` ligatures.
- DVI raster changes, and current converter geometry becomes unsupported because
  those arbitrary literal specials are outside its trusted geometry domain.

The original input/failed controls are retained. Do not ship this hook, replace
Unicode globally, weaken the expected text or call the extraction issue fixed.
A native provenance solution that preserves ligatures needs independent work.

## Source and notices

The exact source artifacts and their byte hashes belong in the acquisition,
extraction and generation receipts. [CTAN LH](https://ctan.org/pkg/lh) describes
LH 3.5g and its source bundle; [CTAN CM-Super](https://ctan.org/pkg/cm-super)
identifies the original Type 1 package. Preserve actual per-file notice/header
distinctions, including the LH authors' naming conditions, CM-Super COPYING and
document-embedding exception, and the LaTeX Cyrillic FD/source-bundle conditions.
Package labels alone do not establish a distributable source/notice closure.
