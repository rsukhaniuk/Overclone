# Bounded additive package compatibility

Build/test tooling only. Product C# orchestration and the accepted C++/Rust
boundaries are unchanged. The Node files exist solely to call the supplied real
Emscripten ABIs and access their virtual filesystems. No host TeX executable,
network acquisition, backend, converter change or format regeneration is used.

Baseline: `b4ec43d1cac535408d44ac3d28a583f1c08b509f`, with exact source
preimages from `/control/baseline.json`. All 879 baseline files remained unchanged.
The supplied suite5 (`compiler-suite-runtime02`) is immutable. Root owns the new
profile6 assembly, replacement formats, application and browser/offline acceptance.

## Overlay and reproduction

`/control/package-overlay/files/` contains 14 exact installed runtime files,
770,899 bytes. `manifest.json` is the public `{schema:1,files:[{name,length,sha256}]}`
contract. `provenance.json` separately records original installed paths, relative
paths, unchanged embedded notices, sizes/hashes and the canonical closure. Neither
manifest silently replaces accepted data; **differing collisions: none**. There
are no replacement formats or native components. `pzdr.tfm` is the exact upstream
metric required by Xe hyperref's one-scaled-point link font, not a substituted
visible Unicode font and not a claim of full Zapf Dingbats glyph support.

Re-stage using the exact installed source paths listed in private provenance:

```sh
python3 native/tex/package-compatibility/overlay.py SOURCE1 SOURCE2 ...
# No arguments independently rechecks every existing overlay/source byte.
python3 native/tex/package-compatibility/overlay.py
python3 native/tex/package-compatibility/run.py --out /control/new-results
python3 native/tex/package-compatibility/pdf-evidence.py /control/new-results
```

Use a new results directory. `run.py` records every command/stdout/stderr, all
input pins, carried state, actual opened files, native statuses and output hashes.
Each pass uses a fresh module; cross-reference tests use three finite passes.
The combined package test includes genuine native BibTeX between passes 1 and 2.
A Node VM adapts the retained pdfTeX worker loader without rewriting its source;
the resolver's network fallback is disabled and accepted runtime files use the
same explicit `/tex` to `/work` aliases as the current application. All source
bytes are mounted unchanged. Quoted roots follow the existing selected-entry
contract. Lua ABI2's SDK hooks are not replaced; its native recorder supplies
opened-file evidence. Converters use the accepted unmodified WASM/ABI.

## Actual coverage

Final matching 14-file-overlay run: `/control/acceptance-results/` and
`/control/acceptance-run.log`. **36 real engine/BibTeX ABI invocations plus four
real DVI/XDV conversions passed their expected positive/negative assertions.**
Every root is `розділ з пробілом/головний.tex` (Lua's fixed output job is `result`).
There are no missing-glyph messages or unresolved package errors on positives;
final reference/citation rerun warnings are absent. Xe/Lua retain literal Ukrainian
with the accepted DejaVu regular/bold/italic fonts. Legacy engines intentionally
use the explicitly Latin branch; this does not establish legacy T2A coverage.

| Synthetic fixture / feature | pdfLaTeX | LaTeX/DVI old5 | XeLaTeX | LuaLaTeX |
| --- | --- | --- | --- | --- |
| hyperref, xcolor/colorlinks, TOC, href, resolved ref | PDF | DVI + converted PDF | XDV + converted PDF | PDF |
| bookmark, cleveref, enumitem, booktabs, setspace, listings/C, caption/subcaption, longtable, AMS/graphics package load, included chapter, plain/BibTeX | PDF | DVI + converted PDF; outline gap below | XDV + converted PDF | PDF |
| Genuine BibTeX, `reference2` BBL and resolved citation | pass | pass | pass | pass |
| Project-local `geometry.sty` wins while real hyperref loads | pass | pass | pass | pass |
| Deliberately missing package returns nonzero, exposes no PDF/DVI/XDV | pass | pass | pass | pass |

These fixtures do not include PNG decoding; root's supplied master browser
package fixture owns that actual image/rendering gate. They are synthetic, not
representative diploma-project acceptance. Existing original suite fixtures are
unchanged. No real source is rewritten to inject a backend selection.

### PDF link evidence is separate from compile status

`pdf-evidence.py` resolves ordinary and Flate-compressed object streams, checks
**page-referenced** Link annotations and the expected URI action, and traverses
catalog-referenced outline items. It is a bounded structural fixture checker,
not a general PDF parser, interactive viewer or raster/glyph validation.

On the combined fixture, all four actual PDFs have six page-referenced links,
one `https://example.invalid/` URI action and the expected output signatures.
pdfLaTeX, Xe and Lua have two reachable outline entries. **Old5 LaTeX/DVI has no
outline root/items**: its real log loads `hdvipdfm.def` correctly, but bookmark
independently loads `bkm-dvips.def`. The converter warns about absent
`головний.out.ps`, unsupported `config` and `pdf:majorversion` specials. Thus
old5 links exist, but default bookmark outlines are **not ready**. Both backend
data files are retained. Root's separate new6 format worker owns the trusted
initializer correction `PassOptionsToPackage{dvipdfmx}{bookmark}` and root must
rerun semantic/PDF/browser checks after assembly. This task does not claim that
new format passed, alter the converter, or qualify a source-edited fixture as
universal compatibility.

### Dependency trace and limits

The first real failure reproduced missing `hycolor.sty`; its exact installed
bytes exposed the next Xe dependency `pzdr.tfm`, then all engines exposed missing
`uniquecounter.sty`. Those **three files / 26,450 bytes** close hyperref compilation
on all four unchanged engines/formats. The companion fixture then exposed
`listings.sty`, `lstmisc.sty`, `bookmark.sty` and its backend data, `cleveref.sty`,
and the C language/default listings configuration. Each step's real failure is
retained under `/control/results/`; optional biblatex loading added logreq's two
runtime files. Manifest/source identity is rechecked in final runs.

`biblatex-bibtex.tex` explicitly requests `backend=bibtex`. Package loading passes
on Xe/Lua after adding `logreq.sty/.def`; it is **not an accepted bibliography
workflow**. Genuine BibTeX returned 2: `biblatex.bst` is not in the accepted suite
or this overlay (its supplied installed source is
`/inputs/host-texmf/bibtex/bst/biblatex/biblatex.bst`), and generated
`job-blx.bib` is not copied/admitted by the current selected-output/carry contract.
No replacement, fabricated BBL, source rewrite or silent Biber-to-BibTeX fallback
was used. Root must separately extend the generated-file contract and stage that
exact BST before claiming this explicitly limited backend works.

Biber remains a separate unavailable program. The exposed trees contain only its
installed PDF documentation. Named-member inventories of the retained source,
port, runtime and bootstrap archives found no Biber program/browser-port source;
archive paths/hashes and scope are in `/control/biber-input-inventory.json`.
Documentation and biblatex macro data are not a Biber implementation.

No full TeX Live, arbitrary listings languages, rendered Unicode extraction,
interactive links, browser execution/cold-offline, complete thesis compatibility
or redistribution/license clearance is inferred. Upstream runtime bytes and
embedded notices are preserved exactly; final source/notice/license release
materials remain root-owned.
