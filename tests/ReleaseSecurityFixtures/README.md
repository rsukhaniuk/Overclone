# R4 B1 — bounded synthetic security corpus

All inputs here are authored **synthetic canaries**, not real credentials,
projects, acquired engines or proof of isolation. Default execution of
`node scripts/test-release-isolation.cjs` only validates this inventory and hashes
its bytes. It never compiles TeX, runs Lua, starts a browser, resolves URI data,
reads any manifest-suggested virtual filename or contacts a sink.

## Intended later root-controlled captures

| Case | Input and independent safe oracle |
| --- | --- |
| `cpu-tex` | `runaway.tex`: actual expansion activity, then cancel; observe Worker destruction within2s, no promoted partial output, fresh benign recovery and editor/save still usable. |
| `cpu-lua` | `lua-runaway.tex` + `runaway.lua`: same oracle inside genuine Lua native execution. The marker immediately before a call does not establish in-call preemption. |
| `namespace` | `namespace-control.tex` plus manifest `wireNames`: names are **data only**. Traversal/absolute/reserved/NFC/case aliases must refuse before native entry. No path here identifies a real private file. |
| `generated-collision` | Reuse benign source with declared wire collision `result.pdf`/generated selected root. Refuse before seeding/entry; no partial accepted artifact. |
| `caught-quotas` | `caught-quotas.tex` + `caught-output-quota.lua`: at most129 ×64KiB writes to selected-job virtual `result.aux` (8MiB+64KiB), plus257 ×1KiB terminal flood. The script catches IO errors and continues. A **latched** quota failure cannot regain output getters or promote an apparent return0. Mere log truncation is not a fabricated quota-latch observation. |
| `display-data` | `display-data.tex`: markup/URI strings must display as text; no event execution, navigation or file grant. No URL is requested by this tooling. |
| `passive-uri` | `passive-uri.tex`: pdfTeX-only genuine PDF URI construction, for later passive action inspection. `javascript:` stays metadata, never an executable grant. Never follow/click/open that action. |

The manifest records file SHA-256/length, exact capture inputs, engine eligibility,
expected status families, walltime/heap/log/output ceilings and unproved evidence.
These are **limits/oracles, not measurements**. Byte/hash agreement is not legal,
security or runtime acceptance. Actual compilation/PDF/URI parsing remains unrun.
Only root may supply the final accepted profile/runtime/source/publication and
reserve a real browser/supervisor. Profile7/LuaABI3 integration is in progress;
profiles0–6 remain immutable. Biber10 and maps11–14 are not admitted by reservation.

One hostile job at a time, two CPU quota, outer process memory<=2GiB, explicit
per-case outer deadline<=30s (and an outer master cap<=5min), physical Worker
termination and a benign recovery after each group. Do not run these files in a
host TeX/Lua interpreter. Outer supervisor kill, missing native-entry evidence,
unavailable engine, stale source/pins or malformed receipt is **not a pass**.
No installed browser, Docker socket, external network or OS/process probe is
needed/authorized for B1. Any future network probe uses only an owned loopback
sink under root's capture reservation; its absence now is not zero-network proof.

## Tooling and evidence scope

`tests/browser/release-transport-bounds.test.cjs` loads the actual browser bridge,
admission and worker entry sources into controlled VM contexts. It instruments
bytes/Blob/URL/Worker/dispatch and stops at import/VFS canaries. It uses tiny
payloads/proxy/scalar sentinels, a64KiB allocation guard and explicitly labelled
private byte-ceiling scaling for PDF/query/worker-bound edge controls. It does
not execute a vendor loader or WASM. Product regressions remain failing tests;
they are not skipped or converted into assertions of vulnerable behavior.

The optional receipt oracle in `test-release-isolation.cjs --check-observation`
requires separately supplied exact source/runtime/publication hashes. It checks
supplied bounded observation data and fixture identity; it cannot authenticate
execution or create a browser trace. Pure oracle tests use an explicit synthetic
evidence kind and cannot become real CLI runtime receipts. A default preflight
success, fake Worker callback or synthetic oracle pass does **not** pass any full
`R4-RES/STALE/CAN/NET` acceptance. `--run-hostile` explicitly refuses: actual
runtime execution and its bounded supervisor belong to root, not this tool.
