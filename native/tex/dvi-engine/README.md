# Offline DVI/XDV converter — ABI 1

Actual retained SwiftLaTeX/Tectonic `(x)dvipdfmx`, compiled as C
libraries behind `oc_dvi.cpp` (C++17). The checked build overlay writes each
page's resolved MediaBox explicitly, preserving the native inherited or per-page
dimensions for the current renderer's box API. Its input/output hashes are in
the build manifest. This is not a PDF generator substitute,
CLI subprocess adapter, backend or package downloader. JavaScript files here
are test tooling only; generated Emscripten loading glue stays in private build
outputs. Master owns application worker/C# integration and provisioning.

## Application boundary

Authoritative declarations: [`oc_dvi.h`](oc_dvi.h). Factory:
`createOcDvi({wasmBinary: verifiedWasmBytes})`. Exported heap: `HEAPU8`.
All C names below have the normal leading `_` in generated JS exports.

1. Create a fresh module in an isolated disposable worker. Supply verified WASM
   bytes; do not accept a caller URL or enable arbitrary loader configuration.
2. Use `malloc`/`free` for temporary transfer buffers. Encode path strings as
   NUL-terminated UTF-8. `oc_dvi_add_file(path, bytes, length, resource)` copies
   input, returning 0 on admission; free the temporary buffers afterward.
3. Resource flag 0 means project data; 1 means provisioned resources. Resource
   names must be flat basenames. Project names are exact relative paths in fixed
   virtual `/project`; no current-directory or process argument is accepted.
4. `oc_dvi_run(entry)` reads that exact project entry. DVI/XDV is detected by the
   actual converter. Output is always internal `/output/result.pdf`, separate
   from project data, even if a project contains a preexisting `result.pdf`.
5. On return 0, copy `oc_dvi_pdf_size()` bytes from `oc_dvi_pdf_data()` immediately.
   Validate with the existing independent PDF renderer before application use.
   Getters return module-owned bytes; caller must not free them. `malloc` and
   `free` are only for caller transfer buffers. Native logs are untrusted plain
   text, never HTML. `oc_dvi_error()` is fixed safe text, not document content.
6. Exactly one attempt per module, including invalid first-run input. A repeated
   run returns **and latches status 3**, clears PDF access (size 0, null pointer),
   and preserves the earlier native return/log. Destroy the worker for reuse,
   cancellation or deadline expiry. There is deliberately no reset/cancel export.

`oc_dvi_abi_version()` is 1. `oc_dvi_engine_version()` is
`oc-dvi-1/swiftlatex-tectonic-dvipdfmx`. ABI status codes:

| Code | Meaning |
| --- | --- |
| 0 | Success (or admission ready) |
| 1 | Invalid input/path/duplicate/missing entry |
| 2 | Resource limit latched |
| 3 | Instance already used |
| 4 | Nonzero native return other than bridge abort |
| 5 | Retained bridge abort, native return 99 |
| 6 | Native return 0 without a nonempty PDF header |

`oc_dvi_native_return()` starts at -1. Native return 99 is the retained bridge's
`setjmp`/`longjmp` error result, **not** a WASM runtime abort. A WASM trap, OOM,
assertion or destroyed worker does not produce a native return: host reports
WorkerCrashed/Cancelled/Timeout as appropriate. Native return 0 cannot override a
latched wrapper limit. Failed attempts expose no partial PDF.

## Virtual resources, limits and unsupported inputs

The sole absolute lookup spelling is **`/tex/<flat-basename>`**, an explicit
resource-only alias agreed with XeTeX. It never falls back to project bytes.
Other absolute paths, URLs, traversal, empty segments, backslashes, control bytes,
colons, pipes and malformed UTF-8 are rejected. Names are <=249 UTF-8 bytes,
case-sensitive and not normalized or renamed. Spaces, Ukrainian names and shell
metacharacters such as `;$` are passed as data. No shell command is constructed.
Relative lookups use exact project names first, then exact resource names; common
font suffixes may be added when absent. No host-font discovery or network resolver
is linked; `-sFILESYSTEM=0` prevents host/Emscripten filesystem access.

* Project total: 8 MiB. Provisioned-resource total: 96 MiB, separate counter.
* Per-file: 32 MiB. Combined project/resource count: 4,096 (empty files count).
* PDF: 8 MiB. Log: 256 KiB, truncation reported separately, not a PDF failure.
* Maximum WASM memory: 512 MiB; initial 32 MiB, stack 2 MiB.
* Successful input opens: 8,192 per attempt. Host must enforce a deadline and
  destroy the worker; synchronous native code cannot process a cancel message.

Admission refuses oversized bytes/counts before copying and does not consume the
counter on refusal. These technical bounds are **not** compatibility/parity
claims. Missing fonts/TFM/encodings/CMaps/images, unsupported DVI/XDV versions,
compressed resource input, bitmap-font generation, external PostScript conversion
and arbitrary OS paths are not silently acquired or substituted. Native special
handling can warn and continue; retain logs and do not equate exit 0 with complete
rendering fidelity. In particular, select a legitimate `dvipdfmx` LaTeX backend
rather than sending dvips header specials to this converter.

`oc_dvi_missing_file()` reports the **first unsuccessful lookup**, which can be
an optional probe, not necessarily a fatal dependency. Maps `pdftex.map`,
`kanjix.map`, `ckx.map` are probed by retained source defaults, not invented config.
`glyphlist.txt`/`pdfglyphlist.txt` provide actual ToUnicode mappings. Optional
`texglyphlist.txt` is currently absent from the tested resource set and causes no
warning for these fixtures. CJK coverage is not established by loading maps alone.

## Exact offline build

Inputs are the retained `dvipdfm.wasm` **source directory**, not an old WASM file.
`build.py` compiles all `dpx-*.c`, `core-bridge.c`, `core-memory.c`, `md5.c` and this
wrapper. It excludes network-enabled `dvipdfmx-wasm.c`, `library.js` and `pre.js`.
Retained C files are not edited. Real cached libpng/zlib are linked; compression
uses the library's `compress2` feature. No Makefile that masks failures is run.

```sh
# Once, copy the approved SDK cache into private storage; never modify inputs.
mkdir -p /control/build/cache
cp -a /inputs/sdk-cache/. /control/build/cache/
# Pick a NEW output directory for each build. Mandatory compiler guards remain.
timeout 480 bash -c 'ulimit -v 3145728; python3 native/tex/dvi-engine/build.py \
  --source /inputs/browser-ports/source/dvipdfm.wasm \
  --out /control/build/dvi-final --cache /control/build/cache'
```

At most two compiler commands run concurrently. Each command has checked status,
a 180-second timeout and its own log. `manifest.json` records source/header,
wrapper/build-script, object, link-map, SDK-library and final loader/WASM hashes,
exact commands, toolchain version and limits. Keep all generated C/object/WASM,
SDK caches and vendor sources outside the repository. Retained engine headers
state GPL-2.0-or-later; bridge headers carry Tectonic MIT notices. Private technical
build evidence is not redistribution/license clearance; master owns release policy.

## Reproduced evidence and test commands

`example.tex` and `cap-example.tex` are synthetic **source-owned TeX inputs**.
`make-fixture.cjs` uses the existing genuine provisioned pdfTeX, with its explicit
`swiftlatexpdftex.fmt` -> `pdflatex.fmt` alias and offline seeded resources. It does
not hand-author DVI or execute a host TeX installation. The source explicitly
chooses the real LaTeX `dvipdfmx` backend. Test harness network calls throw.

```sh
node native/tex/dvi-engine/make-fixture.cjs /inputs/resources/files \
  native/tex/dvi-engine/example.tex /control/results/example.dvi
node native/tex/dvi-engine/test.cjs CANDIDATE FLAT_CM_RESOURCES \
  /control/results/example.dvi /control/results/focused
python3 native/tex/dvi-engine/check-pdf.py /control/results/focused/example.pdf
node native/tex/dvi-engine/make-fixture.cjs /inputs/resources/files \
  native/tex/dvi-engine/cap-example.tex /control/results/cap.dvi
node native/tex/dvi-engine/test-output-cap.cjs CANDIDATE FLAT_CM_RESOURCES \
  /control/results/cap.dvi /control/results/output-cap.json
node native/tex/dvi-engine/test-cancel.cjs CANDIDATE FLAT_CM_RESOURCES \
  /control/results/cap.dvi /control/results/cancel.json
node native/tex/dvi-engine/test-xdv.cjs CANDIDATE FLAT_XDV_RESOURCES \
  /inputs/browser-ports/source/dvipdfm.wasm/test.xdv /control/results/xdv-suite
```

Normal CM fixture: genuine 612-byte, two-page DVI -> 7,193-byte PDF. The exact
11-file set is `pdftex.map`, `kanjix.map`, `ckx.map`, `glyphlist.txt`,
`pdfglyphlist.txt`, plus TFM/PFB pairs for `cmr10`, `cmr7`, `cmmi10`.
Receipts include each actual file hash/length; no fabricated standard font map is
used. Byte-level checks prove two pages, embedded CMR10/CMMI10/CMR7 CFF streams,
real ligature ToUnicode mapping and decoded expected text. Master independently
validated the earlier identical PDF with Poppler.

The retained genuine upstream `test.xdv` produced a 20,548-byte one-page PDF with
six supplied LM OTFs (`lmroman17-regular`, `lmroman12-regular`, `lmroman9-bold`,
`lmroman9-regular`, `lmroman12-bold`, `lmroman10-regular`) plus the provisioned CM
font closure. Its suite rejects project fallback for `/tex/` and arbitrary
absolute-path substitution. Master independently parsed its fonts/math/text.
This is **not** fresh-current-XeTeX-worker acceptance; that genuine output remains
an integration dependency. No claim of complete XDV version/package/font coverage.

The genuine 60,000-page, 5,629,100-byte DVI hit the PDF cap: native returned 0,
wrapper returned 2, PDF getters exposed nothing, and log stopped at 262,144 bytes.
Final resource-set run grew the heap to 83,689,472 bytes. Worker destruction also
interrupted that actual conversion. Application revision/stale/cold-offline,
browser rendering/navigation and cancellation acceptance remain master's scope.
