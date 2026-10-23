# Offline XeTeX/XeLaTeX module

Owned recipe and runtime seams only. Generated engine C, dependencies, JS/WASM,
formats and test artifacts stay outside the repository. No sources are downloaded.
This does not change pdfLaTeX, application registration, converters or SyncTeX.

## Build and real tests

With the task's explicit read-only `/inputs` mounts, frozen-cache compiler wrappers
on PATH and the master-supplied `/control/provided-texmf` copy:

```sh
python3 native/tex/xetex-engine/build.py --stage all
python3 native/tex/xetex-engine/provision-test.py
timeout 60 node native/tex/xetex-engine/test.cjs format
timeout 60 node native/tex/xetex-engine/test.cjs pass1
timeout 60 node native/tex/xetex-engine/test.cjs pass2
python3 native/tex/xetex-engine/freeze.py
```

`build.py` accepts `--inputs` and `--build`; defaults are `/inputs` and
`/control/build/xetex`. Its per-command logs and checked exits are in the private
build directory. Jobs are bounded to two, compiler address space to 3 GiB, and
individual commands have timeouts. SDK cache is copied privately and remains
frozen. The old Makefile is not used: it hides failures and requests absent SDK
FreeType/ICU ports. Approved TeX Live source supplies genuine FreeType 2.10.4,
ICU 68.2, HarfBuzz 2.7.4, Graphite and Xpdf instead. HarfBuzz uses XeTeX's own
FreeType callbacks, not its unused optional `hb-ft` adapter. Dependency config and
version headers are generated normally; generated XeTeX C is never modified.

The engine identifies itself as **XeTeX 3.14159265-2.6-0.999992**, retained port
**XeTeX WebAssembly 0.3.0**. Its real INITEX consumes the supplied legitimate
`xelatex.ini` and **LaTeX 2023-11-01 patch 1** kernel. It does not use a pdfTeX format.
The review candidate is `/control/candidate/xetex-v3`; `manifest.json` binds
its exact bytes, and `required-resources.json` gives observed resource hashes and
source paths. The first candidate (`xetex-v1`) is retained as evidence but is
superseded: v2 removed stale pre-existing outputs; v3 corrects combined input
admission to accommodate 64 captured files plus up to 128 carried state files.
V3 reuses the exact genuine v2 format, whose ABI and generating inputs are unchanged.
The format exceeds 32 MiB; consult the candidate manifest for its exact size/hash.
A second, independently regenerated reduced-fixture format also compiles the test;
it is not relabelled as the frozen format.

## Master integration ABI

Load `xetex.js` as modularized factory **`createXeTeX`**, supplying its verified
`wasmBinary` locally. The build supports browser/window, Worker and Node testing.
Exported runtime helpers: `FS`, `ccall`, `cwrap`.

1. Create **fresh module/worker per attempt**. Mount only explicit bytes in MEMFS:
   runtime `/tex/<basename>`, projects `/work/<relative-path>`, CWD `/work`.
   No NODEFS/host mounts, network provisioning or host font discovery.
2. Format generation: call `compileFormat()`; on status 0 read
   **`/work/xelatex.fmt`**. Provision that exact format as `/tex/xelatex.fmt` in
   subsequent fresh compile modules.
3. Compilation: call `setMainEntry(UTF-8 relative filename)` and require status 0,
   then `compileLaTeX()`. The filename is validated/quoted as filename data and
   decoded to Unicode scalars at the handwritten top-input seam. Nested Ukrainian
   BMP names/spaces are verified. TeX metacharacters/unsafe paths are rejected.
   The retained generated scanner does not support non-BMP main filenames;
   `setMainEntry` explicitly rejects them rather than truncating a code point.
4. Status 0 means the actual engine returned success; require the expected new
   nonempty XDV as well. Nonzero, abort, cancellation or timeout means **no artifact
   promotion**. Results must retain the master's revision/request identity.
5. Outputs are under `/work`, using the main filename's **basename**, not its
   directory: the fixture `розділ з пробілом/головний.tex` produces
   `/work/головний.xdv`, `.aux`, `.log`. Actual XDV is version 7 (`f7 07`).
   Nativefont references use **`/tex/DejaVuSerif.ttf`** and
   **`/tex/DejaVuSerif-Bold.ttf`** with normal
   `\setmainfont{DejaVuSerif.ttf}[Path=/tex/,BoldFont=DejaVuSerif-Bold.ttf]`.
6. Carry AUX bytes explicitly into the next fresh module to resolve references.
   Export `compileBibtex()` is retained but not accepted by these tests; use a
   separately verified bibliography path until it has actual acceptance.

Additional statuses: `-1` missing main, `-2` invalid/unsupported `setMainEntry`,
`-4` input budget failure, **`-5` missing/invalid fresh output**,
**`-10` already attempted**. Do not restore/reuse an engine heap.
Cancel by destroying the worker; never call an engine method after cancellation.
`runsystem` is a nonexecuting retained stub; shell escape flags are disabled and
converter pipe launch is removed. This module produces XDV, never launches PDF
conversion. Master owns XDV-to-PDF and actual app/PDF/offline acceptance.

## Implemented bounds and focused evidence

- WASM memory: initial 32 MiB, maximum **512 MiB**, 5 MiB stack. A 600 MiB
  allocation is rejected in the real module.
- Combined `/work` admission: **192 files** (64 captured plus up to 128 carried
  AUX/state files), 8 MiB/file, 32 MiB total, depth at most 16. C# separately
  enforces the **64 captured / 128 state** split; this is not 192-file project
  capture capacity. Runtime admission: 8192 files, 64 MiB/file, 160 MiB total. Symlinks/unsafe paths
  reject. Apply stricter trusted resource inventory admission before these calls.
- During execution, the small C++ `EM_JS` MEMFS seam limits ordinary output files
  to **32 MiB**, format files to **64 MiB**, cumulative output growth to **96 MiB**,
  and stdout/stderr to **2 MiB**. It permits data writes only below `/work`.
  The JavaScript is only MEMFS enforcement, not compiler workflow logic.
- The master must enforce the job deadline, convert thrown/aborted native failures
  into safe fixed errors, discard stale results and reject partial outputs.

`test.cjs` exercises genuine format generation, two-pass fontspec/TTF Ukrainian
multi-file XDV, unsafe main names, missing font, foreign pdfTeX format, real TeX
failure, host-path denial, disabled shell escape, single-use rejection, file-count/
size admission, stale-output rejection, non-BMP main rejection and actual MEMFS
write/memory bounds. Run a named case with
`timeout 60 node native/tex/xetex-engine/test.cjs <case>`; cases include `failure`,
`confinement`, `shell`, `input-limit`, `count-limit`, `foreign-format`,
`missing-font`, `write-limits`, `memory-limit`, `stale-output`, `emoji` (explicit
unsupported-main rejection, not successful non-BMP authoring), `capacity-pass1`,
`capacity-pass2` (64 captured files plus real prior AUX), `capacity-max-state`
(64 captured plus 128 state), and `count-limit` (193 combined files refuse).
Test receipts stay in
`/control/results`. `test-cancel.cjs` terminates a Node Worker executing an infinite
TeX loop, then proves a fresh Worker compiles successfully; this is not claimed as
a published-browser cancellation test.

Not claimed: browser/PDF integration, broad package/font coverage, named host-font
lookup, Unicode hyphenation, SyncTeX, full TeX Live compatibility or redistribution
clearance. The tested format retains standard optional-file warnings for
`TS1lmr.fd` and `latex2e-first-aid-for-external-files.ltx`; no replacements are
fabricated. Existing pdfLaTeX navigation is untouched.
