# Controlled project-input budget derivative

This separate candidate changes **project input to 16 MiB** while retaining
**PDF output at 8 MiB**. It is not an adopted runtime/profile or product quota
change. Future profile 16 is reserved for integration; this recipe cannot make it
Ready. Do not replace any P13/P14/P15 source, recipe, asset or formatter.

`prepare.py` accepts only the exact retained current-P15 wrapper/header hashes
for Xe ABI3 or DVI ABI2. It splits the three uses of the former shared 8 MiB
constant into independent project-input and PDF-output constants, then adds
three read-only capability getters:

- `oc_dvi_input_budget_abi() == 1`
- `oc_dvi_project_input_limit() == 16777216`
- `oc_dvi_pdf_output_limit() == 8388608`

No private geometry/neutral proof functions are exported. The per-file 32 MiB,
resource 96 MiB, combined 4,096 files, 8,192 native handles, 256 KiB log, 512-page geometry,
131,072-byte geometry and 512 MiB maximum WASM-memory limits stay unchanged. These
are native ceilings; a future application may admit a narrower per-file/project
subset. Project files include the generated DVI/XDV intermediate.

Input refusal preserves the parent's accounting semantics: rejected bytes do
not spend or reset the accepted prefix; a small remaining file can still fit.
Output overflow remains sticky and clears the PDF/geometry when conversion
settles. There is no public reset; another attempt uses a fresh WASM instance.

`build.py` first verifies the pinned parent manifest, 75 object hashes, all
retained source/header hashes and SDK-library hashes. It recompiles/relinks the
parent and requires exact original object/JS/WASM reproduction **before** making
the derivative. Only `oc_dvi.o` changes; the 74 other objects, font semantics,
graphics/origin1/neutral1 witnesses, path rules and isolated virtual filesystem
remain exact. Parent source/build/notice closure is inherited and must still be
retained; this is not an unrelated rebuild or replacement compiler.

Run in the digest-pinned Emsdk 6.0.13 image from `toolchains.env`, with no network,
2 GiB RAM and two CPUs. Supply immutable retained parent, source and SDK-cache
mounts; use a fresh output directory. The standalone cache is frozen and has
no dependency downloads or shared application SDK volumes.

```sh
python3 native/tex/project-input-budget16/build.py --kind xe \
  --parent /parents/xe-neutral02 --source /inputs/dvi-source \
  --cache /cache --out /outputs/xe01
```

Repeat independently into `xe02` and compare candidate files byte-for-byte.
Repeat for `--kind dvi --parent /parents/dvi-neutral02`. Each output contains
`parent-reproduced`, `candidate`, compiler/link logs and a command/source/pin
receipt. Pin the final pair explicitly before any application integration.

Run the JavaScript checks in the existing browser-test Docker image:

```sh
node native/tex/project-input-budget16/test-admission.cjs \
  /outputs/xe01/parent-reproduced /outputs/xe01/candidate xe /outputs/admission.json
```

Admission checks exercise genuine parent/new WASM at 8 MiB and16MiB adjacent
boundaries, accounting retries, fresh instances, invalid paths/UTF-8, duplicate
names and unchanged resource/file/count ceilings. Native public exports must
be exactly the parent's set plus the three getters.

`test-output.py` builds a **separate test-only callback witness** from the exact
prepared wrapper. Its controlled stub producer checks 8 MiB output writes,
putc overflow, sticky flush/close failure, output clearing, one-use refusal and
16 MiB input-stream reads. It does not constitute a genuine PDF conversion or a
runtime candidate. Run `test-output.cjs` on that test binary.

`make-output-control.py` generates a deterministic valid 1673×1673 RGB PNG whose
incompressible raster exceeds 8 MiB. `replay.cjs` can run a retained native package
DVI/XDV with that image and require `output-limit`: admission succeeds below
16 MiB, actual PDF conversion returns resource-limit2, and no PDF or geometry
escapes. Do not raise output limits to make a corpus document pass.

`test-regressions.py` replays 36 retained genuine P15 converter cases against
parent and derivative:10package/BibLaTeX/origin cases plus 26 typed-special
positive/negative and sticky-unsafe controls. PDF, geometry and log bytes must
remain identical. It uses existing native intermediates and does not claim new
TeX/Biber/browser acceptance. `probe-project.cjs` separately verifies unchanged
private original-project admission without executing a compiler or converter.

A useful real corpus cell still requires its original dependency closure,
genuine Xe+Biber passes, resolved references, an actual PDF no larger than 8 MiB,
text/search/navigation and published-browser/offline acceptance. No source or
asset rewrites, aliases, recompression, proprietary fonts, backend, formatter or
engine changes are authorized by this candidate. Integration owns any future
application snapshot/worker/closure/trust/default changes.
