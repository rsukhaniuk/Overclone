# Offline compiler-suite resources (integration input, not an installed profile)

This directory contains offline **build/test tooling**, synthetic package fixtures
and an exact common-data lock. It does not replace an engine, modify accepted
formats, register a profile or enable distribution. No font, generated WASM,
format or other generated runtime payload is committed here. Python handles
packaging; C++ extracts actual font metadata; the two small Node bridges are
necessary to access the existing Emscripten modules and their virtual filesystems.
No network, host font discovery, host installation or full native rebuild is used.

## Frozen common data

The LaTeX 2023-11-01 patch 1 common view contains **3,661 explicit files /
65,964,339 bytes** (about 62.91 MiB). `common.lock.json` contains only logical
names, sizes and SHA-256s. Its file SHA-256 is
`d0d7e47a0093a09f063038ba7b30d083b933453d5d71a66a561d6b8a13e6a372`;
its canonical name/tab/length/tab/hash closure SHA-256 is
`fb2340bfac5a76a6ead6465264e133d982582369ab8daaebca7dffc7f242aec9`.
The canonical closure joins ordinally sorted entries with newlines, without a
trailing newline.

Sources are the already-provided accepted 507-resource DVI bundle, copied local
TeX data, extra glyph maps/DejaVu fonts and five Lua Unicode algorithm modules.
The preparation command verifies **every source byte** against its supplied pin,
including excluded entries. Source paths and all overlap/exclusion evidence go
only to `provenance.private.json`, never the runtime pins or ZIP.

- Runtime TeX/package/font data are flat unique basenames. All 374 supplied Lua
  files are retained. Module-relative paths below real `lua/` roots, and PGF's
  `libraries/luamath/` root, remain qualified: e.g. `pgf/gd/model/Edge.lua`,
  `pgf/luamath/functions.lua`, `pgfplotsoldpgfsupp/luamath/functions.lua`.
  There are 225 qualified paths. The six differing-basename collision groups
  (`doc.lua`, `library.lua`, `Edge.lua`, `layered.lua`, `functions.lua`,
  `parser.lua`) retain every distinct module; none is arbitrarily selected.
  `pgf/gd/doc/...` is a require-able Lua module tree, not a host doc directory.
- The only differing non-module overlaps preserve the accepted bundle's
  **160-byte `pdflatex.ini`** instead of the original 131-byte bootstrap, and
  **100-byte English-only `hyphen.cfg`** instead of the 6,425-byte Babel config.
  Exact kept/rejected bytes and hashes are recorded privately. This does **not**
  provide Ukrainian hyphenation or synthesize language patterns or font maps.
- An explicit extension allowlist excludes host docs/notices/source/build trees,
  PFM sidecars and executable engine/build artifacts. There are 174 excluded
  source entries (including the eight separately packaged base components).
  The real `plain.bst`, maps, encodings, metrics and runtime configuration remain.
  License notices within original runtime files are unchanged; separate notices
  remain release materials, not compiler filesystem inputs.
- Common data include 4 DejaVu Serif TTF styles, 72 Latin Modern text OTFs and
  `latinmodern-math.otf`. Largest common file: `luaotfload-characters.lua`,
  3,287,334 bytes. No ICU data, format, loader, WASM, converter or query component
  is in this common filesystem view.

Use environment variables referring to the existing private input directories:

```sh
python3 native/tex/suite-resources/prepare.py \
  --accepted-dvi "$ACCEPTED_DVI" --texmf "$PROVIDED_TEXMF" \
  --font-extra "$PROVIDED_FONT_EXTRA" --lua-unicode "$PROVIDED_LUA_UNICODE" \
  --lock native/tex/suite-resources/common.lock.json --out "$NEW_COMMON"
```

Inputs have `files/` plus `pins.json` (accepted DVI) or `manifest.json` (other
sets). Output must not exist. The command copies exact selected bytes, rechecks
copies and writes `common.json` and private provenance. A second independent
preparation matched the lock and all output bytes. Do not use preliminary freezes
made before the qualified-module checks; the final lock above is authoritative.

## Genuine XeTeX font catalog

`font-catalog.cpp` reads the **explicit 77 supplied font files** using the already
built FreeType library. No fontconfig/host inventory is inherited. It extracts
English SFNT preferred family/style names (falling back to SFNT legacy names),
full names, FreeType PostScript names, OS/2 weight/width/selection, `head` style
bits, `post` italic angle and GPOS `size` metadata. It does not invent aliases,
metrics, glyph maps or a Times New Roman substitution. Missing GPOS design size
uses XeTeX's existing 10pt default; this is not an asserted intrinsic font metric.

```sh
# Existing SDK wrapper and existing static library; not a native engine rebuild.
em++ native/tex/suite-resources/font-catalog.cpp "$FREETYPE_BUILD/libfreetype.a" \
  -I"$FREETYPE_SOURCE/include" -I"$FREETYPE_BUILD/include" \
  -O2 -fexceptions -sMODULARIZE=1 -sINVOKE_RUN=0 -sFORCE_FILESYSTEM=1 \
  -sEXPORTED_RUNTIME_METHODS=FS,callMain -sENVIRONMENT=node \
  -o "$PRIVATE_TOOLS/font-catalog.js"
node native/tex/suite-resources/make-font-catalog.cjs \
  "$PRIVATE_TOOLS/font-catalog.js" "$NEW_COMMON" "$NEW_CATALOG"
```

Generated `xetexfontlist.txt`: **12,315 bytes**, SHA-256
`380ef7057355bff9024f448cf132e6c563364071ee36251d557d95c4652d1e65`.
Independent regeneration matched byte-for-byte. `catalog.json` records source
font pins, the actual extracted metadata and the output pin. The catalog contains
only `/tex/<provided-font>` virtual paths and belongs **only to the Xe phase**.

The accepted `XeTeXFontMgr_FC.cpp` reader consumes its final `subFamilyID` field
twice, not `nameCode`. The emitter repeats the actual subfamily ID to match that
existing ABI; actual GPOS nameCode remains in the metadata receipt. The engine's
unused nameCode-copy quirk is not fixed here. Optical-size values are real GPOS
values converted from PostScript decipoints to TeX points as in the existing
native implementation. The passing Latin Modern test is not exhaustive proof
of every optical-size/font variant or arbitrary future font support.

## Representative accepted-Xe package compiles

Run each fixture in a fresh instance of the accepted Xe module; no INITEX or
whole-engine rebuild is needed:

```sh
node native/tex/suite-resources/test-xetex.cjs "$ACCEPTED_XE" "$NEW_COMMON" \
  "$NEW_CASE_RESULTS" native/tex/suite-resources/fixtures/fontspec-unicode.tex \
  "$NEW_CATALOG/xetexfontlist.txt"
```

The harness verifies accepted loader/WASM/format pins and all common file bytes,
mounts common data at `/tex` and synthetic project inputs at `/work`, and mounts
only accepted ICU and the catalog as additional Xe data. It calls the actual
`setMainEntry` / `compileLaTeX` ABI and records logs, opened paths, source/resource
identities and actual outputs. It never substitutes successful outputs.

| Fixture | Actual result with final common data + accepted Xe |
| --- | --- |
| `amsmath.tex` | Native 0; genuine XDV 7; amsmath/amssymb matrix, integral and blackboard math |
| `geometry.tex` | Native 0; genuine XDV 7; real A4 geometry package options |
| `graphics.tex` | Native 0; genuine XDV 7; graphicx/xcolor plus actual small project EPS input |
| `authoring.tex` | Native 0; genuine XDV 7; booktabs, enumitem and setspace |
| `tikz.tex` | Native 0; genuine XDV 7; TikZ arrows/positioning/calc and PGFplots function plot |
| `fontspec-unicode.tex` | Native 0; genuine XDV 7; normal `DejaVu Serif` family name, four automatic styles, literal `Привіт, Україно! Іі Її Єє Ґґ.` in each, no missing-glyph warnings |
| `unicode-math.tex` | Native 0; genuine XDV 7; normal Latin Modern Roman/Math family names, four text styles and real Unicode math |
| `local-override.tex` | Native 0; genuine XDV 7; `/work/geometry.sty` read, `/tex/geometry.sty` not read |
| `hyperref.tex` | **Failed**: real package stops at missing `hycolor.sty`; no successful XDV claim |

For the two font cases, the harness checks that the actual output XDV names all
expected selected fonts, not just that fontspec probed their input bytes.
`hycolor.sty` was not found among the provided inputs, including retained approved
source trees and their retained TAR/ZIP member names. The concrete missing input
is `hycolor.sty`, required by `texlive/tex/latex/hyperref/hyperref.sty:113`.
A follow-up search checked 1,670 TeX/package source files for its package
declaration and all four retained TAR archives; no implementation or source was
found. No substitute is authored. Additional transitive hyperref gaps after
that first failure are unknown. Biber is not provided and remains unsupported;
existing genuine BibTeX remains a separate accepted component, not a Biber fallback.
BibTeX was not rerun in this bounded package matrix.

These are synthetic package-use tests, **not** PDF rendering/geometry/link/glyph
visual acceptance, browser/offline acceptance, a real thesis compatibility claim,
Lua graphdrawing acceptance or full TeX Live coverage. Root owns converter/PDF,
C#/worker/service-worker integration and published browser acceptance.

## Final assembly contract — run only after final Lua freeze

`assemble.py` makes a new closed additive bundle, never modifies an old bundle.
It requires the exact **base02** 507-entry closure
`a58e68644dd954f79d12bf96a205a3071076cd5c4daa38fcd780f3e5ee0eee08`
(60,298,660 bytes; runtime identity
`fc21cc06a7d069d297f73da7e2aa59c9f0b9c815525c2993fe41451581720e71`),
checks the common lock, preserves every base02 pin and refuses differing
collisions. Base02 includes the accepted page-box-normalized DVI converter WASM:
685,953 bytes, SHA-256
`6dc117f6ca3b16cea8e675683486e1f641a933fbfdccc9de8f94f51224e18c61`.
The previous base01 remains preserved separately but is now rejected as a suite
assembly input. Converter bytes are excluded from common data, so the common
lock, catalog and Xe filesystem view are unchanged. It does not change what the separate historical 497/499/507 profiles
mean. Its output pins use the existing `pins.json` + `files/*` ZIP layout.

Root must supply a **private** components JSON `files` array, with one final
`loader`, `wasm` and genuine `format` for each `phase` (`xetex`, `luatex`), plus
two `xe-data` entries for `icudt68l.dat` and `xetexfontlist.txt`. Each entry has
`name`, `length`, `sha256`, `source`, `kind`, `phase`. `source` is a private local
file path and is never emitted in ZIP pins/views. There is intentionally no
example with invented component hashes and no provisional suite ZIP.

```sh
python3 native/tex/suite-resources/assemble.py --common "$NEW_COMMON" \
  --accepted-dvi "$ACCEPTED_DVI" --components "$FINAL_COMPONENTS_JSON" \
  --out "$NEW_FINAL_SUITE"
```

- Per-file ceiling: **64 MiB**; complete payload and ZIP ceilings: **256 MiB**;
  resource count ceiling: **8,192**. The ZIP uses stable ordinal paths, DOS epoch,
  fixed modes and stored exact payloads (no compression-version variability).
- The accepted Xe format remains exactly **33,847,140 bytes**, SHA-256
  `0601a5dec935457c9d3e06150c8ffb1037b8befef3cbf5cb4793dc1f4876fce5`.
  It is reused, not regenerated or relabeled.
- **19,163,040-byte `icudt68l.dat`**, SHA-256
  `63a8210766507546a73b7bfdb796c37fad0ec0485b992c1a7e68fd24f893ae6d`,
  belongs only to Xe. Lua/pdfTeX views do not contain it or the Xe catalog.
- Common + unchanged eight base02 components total **3,669 entries /
  113,453,830 bytes**, before the final added Xe/Lua components and Xe data.
- Xe filesystem view: **3,664 files / 118,986,834 bytes**, including its genuine
  format, ICU and catalog. This leaves **48,785,326 bytes / 4,528 entries** under
  the 160 MiB / 8,192-input ceiling for project/carry inputs.
- Lua's common-data portion leaves **34,698,957 bytes / 947 entries** under
  96 MiB / 4,608 inputs **before its final genuine format and project/carry**.
  Its actual final view size/hash is pending that format; it is not asserted here.
- `views.json` separates common FS plus phase format/data from separately loaded
  engines/converter/query artifacts. It gives exact final phase hashes, sizes
  and headroom when final components are present. Format aliases/actual mounting
  are root's ABI mapping, not a rename of format content.

Root's role-aware preflight must keep runtime `/tex` separate from project
`/work` (Lua's accepted wrapper uses `/work/runtime` and project/output). It must
allow project-local package overrides, not force a collision with runtime
`geometry.sty`. Filesystem directories are explicit virtual inputs, not host
font search roots. Runtime assets must not be copied into the project namespace.
The final Lua quota fix, component hashes, final suite ZIP, profile-5 closure pin,
actual app registration and browser checks remain root/Lua-owner work.
The assembler's missing-final-components refusal was tested; successful complete
assembly is deliberately not claimed before those accepted inputs exist.

Focused checks:

```sh
python3 native/tex/suite-resources/test-packaging.py --common "$NEW_COMMON" \
  --accepted-dvi "$ACCEPTED_DVI" --texmf "$PROVIDED_TEXMF" \
  --rejected-dvi "$PREVIOUS_DVI_BASE01" -v
```

`ACCEPTED_DVI` must refer to base02 for preparation, checks and assembly. A fresh
preparation against base02 reproduced the original common lock and every selected
runtime byte without changing the original common freeze or catalog. Converter
page-box/PDF/browser acceptance is master-owned evidence, not rerun by this tool.

Five checks passed: tamper/duplicate/traversal/symlink refusal; per-file/aggregate/
count and differing-collision refusal; all real six Lua collision groups and base02
non-component pins preserved; preserved base01 assembly rejection; and
incomplete-final-component assembly refusal.
Provisioning/distribution still requires the master-owned license/source/notice
and adoption decisions. These private technical inputs are not redistribution
clearance, even though runtime bytes retain their original embedded notices.
