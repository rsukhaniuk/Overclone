# Separate 256-file native candidate

`prepare.py --work-files 256` raises only the combined `/work` input-file
ceiling from 192 to 256 and adds `oc_xe_work_file_limit()==256`. The default
remains 192. Existing per-file, combined-byte, runtime, heap and output guards
remain unchanged. Browser capture and generated-carry limits remain separate.

The default preparation/build reproduced the accepted P13 loader and WASM
byte for byte. The new source-built candidate has these identities:

| File | Bytes | SHA-256 |
| --- | ---: | --- |
| engine.js | 70018 | `3969fa53aa5b3cb42ca3b8cea1adb6e841d83c897f41d2d0b66f352d7d1142f7` |
| engine.wasm | 3302304 | `d2886ff867abc73386b733839f5e0a9b9855723cc3b19aa8686649fd0c3139c8` |

Run the existing `run.py` native matrix against the candidate with the exact
accepted Xe format. Its `/inputs/suite` projection must exclude unrelated
`biber.data`: copying every engine's data plus the Xe format exceeds the
unchanged native runtime-byte ceiling. Resource bytes themselves are unchanged.

`test-capacity.cjs TOTAL [LIMIT]` uses that same native fixture loader. With
`ENGINE`, `ENGINE_FORMAT`, `RESULTS`, and the pinned `/inputs/suite` supplied,
run candidate totals 255, 256 and 257 with limit 256. Each contains exactly
128 captured files, all actually opened, plus 127, 128 or 129 carry files;
the root AUX is consumed by real cross-reference resolution. The first two
compile, and 257 refuses with native -4 and no map or XDV. Run immutable
P13 totals 192 and 193 with limit 192 as controls. Use the existing
`synctex-fixtures/xe/test-compat.cjs` for exact BMP map/output compatibility.

These are native checks. Profile adoption, actual application capture policy,
conversion, browser navigation and cold offline verification are master-owned.
