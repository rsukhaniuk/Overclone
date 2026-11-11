# R1 compiler compatibility corpus

`manifest.json` pins the existing browser harness, its relative helpers, fixture
sources, toolchain and expected runtime. All available entries are clearly labelled
synthetic controls. The `supplied-real-thesis` entry remains unavailable until a
representative project is supplied and approved for local testing; passing this
matrix does not replace it or establish full TeX Live compatibility.

The recipient runner wraps `scripts/test-local-authoring-ui.cjs`; it does not
implement another compiler, inject generated outputs or build/publish the app.
It verifies every static publication asset and runtime resource before execution,
copies only the pinned public source graph, resolves an already cached browser
image derived from the pinned base and executes with networking disabled,
read-only inputs and a fresh result directory. It never pulls images or downloads
packages. Chrome's test profile and ordinary runtime/application caches live in a
bounded temporary filesystem. The app runs through its real registered UI/workers.

Use explicit paths to an accepted published `wwwroot`, compiler `pins.json` and
`files/`, trusted renderer `manifest.json` and `files/`, and a fresh result path.
No private project paths are built into these commands:

```sh
python3 scripts/run-compiler-acceptance.py --help
python3 scripts/run-compiler-acceptance.py --preflight \
  --publication "$PUBLICATION" --publication-id "$SDK_VERSION" \
  --authoring-runtime "$COMPILER_RUNTIME" --pdf-runtime "$PDF_RUNTIME" \
  --lua-harfbuzz-source "$APPROVED_HARFBUZZ_SOURCE" --results "$PREFLIGHT_RESULTS"
python3 scripts/run-compiler-acceptance.py \
  --fixture biber --engine pdflatex --backend biber \
  --selected-root 'тези/my thesis.tex' \
  --publication "$PUBLICATION" --publication-id "$SDK_VERSION" \
  --authoring-runtime "$COMPILER_RUNTIME" --pdf-runtime "$PDF_RUNTIME" \
  --results "$BROWSER_RESULTS"
```

The HarfBuzz source input is needed only for its fixture, or an all-fixture
preflight; it contains the approved original `fonts/amiri-regular.ttf` with the
manifest's exact hash. Available engines are `pdflatex`, `latex-dvi`, `xelatex`
and `lualatex`; backends are explicit `none`, `bibtex` or `biber`. Incompatible
fixture/engine/backend choices refuse before starting a browser. Environment
variables `AUTHORING_RUNTIME_DIR`, `PDF_RUNTIME_DIR`, `RESULT_DIR` and
`LUAHARFBUZZ_SOURCE_DIR` can provide the corresponding explicit paths.

Preflight performs static identity/admission checks only. `acceptance-handoff.json`
records the exact command, tool/runtime/publication identities, selected scope,
exit state and artifact hashes. A run passes only when the actual browser receipt
also matches those inputs and proves the stopped-origin cold-offline restart.
Missing fixtures, mismatched hashes, old results and unexecuted cells never pass.

`packages` and `biblatex` retain strict native navigation assertions. The separate
`packages-compiler` / `biblatex-compiler` entries test the identical package/input
bytes, actual selected backend, final text, exact downloads and fresh offline
compilation; their receipts explicitly mark native navigation **not assessed**
pending R2. They do not waive those original tests or close R2 map precision.

Inspect retained package PDFs independently with:

```sh
python3 native/tex/package-compatibility/pdf-evidence.py \
  --pdf "$ONLINE_PDF" --expected-uri https://example.org/ \
  --expected-outline 'Compiler suite' --expected-outline 'Included chapter' \
  --receipt "$ONLINE_FEATURE_RECEIPT"
```

The offline fixture deliberately adds a section; include another
`--expected-outline 'Edited offline'` for that PDF. This bounded structural tool
checks page-referenced URI actions and reachable outline titles in the actual
exported bytes. It is not a general PDF security parser or preview link UI.
