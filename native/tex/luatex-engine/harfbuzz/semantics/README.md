# Original HarfBuzz text semantics: candidate producer

This source package is a private candidate boundary. It does not adopt a runtime,
change a published profile, or establish browser/offline acceptance.

The original unchanged `../fixtures/harfbuzz.tex` now extracts and searches the
literal phrase `مرحبا بالعالم`, including its space, in the pinned E1 renderer.
Independent Poppler `-bbox` extraction reports the same original phrase. Plain
Poppler text output applies bidi display ordering; its retained raw output is
reported separately and is never reversed or normalized by these tests.

`overlay.py` verifies the exact accepted Luaotfload 3.26 resource preimage and
builds a new resource. `rtl-spans.lua` consumes genuine shaped logical cluster
strings and actual space glue. After line breaking, a completed line containing
RTL clusters receives one logical ActualText span. No document source, shaping
input, glyph ID, glyph position, spacing width, discretionary decision or line
break is rewritten. Wrapping the whole completed line avoids the PDF rounding
changes observed when page-mode literals split mixed-direction text mid-line.
The original Luaotfload module header, copyright/authors and GPL v2.0 notice
remain intact in the generated resource. This is a modification of that resource,
not a separately licensed replacement for the upstream module.

Collection is bounded to 16,384 UTF-16 units, 65,536 visited nodes and depth32.
It follows the actual retained discretionary replacement. It refuses incomplete
provenance on a detected RTL line. Nested boxes, other scripts and mixed math
are not established by this corpus; in particular, a line whose RTL text exists
only inside an inner box is outside this completed-line test scope. No general
bidi or all-font correctness claim follows from these four controls. Boundary
constants are source-inspected here; full below/at/above semantic-size execution
and arbitrary nested-box coverage remain follow-up checks.

The source-owned native correction is independently necessary:
`fix-kern-provenance.py` changes exactly four metadata accessor arguments in
upstream `synctex_record_node_kern()` from `glue` to `kern`. LuaTeX's actual kern
allocation is five words; the glue allocation is seven. The old writer therefore
read unrelated adjacent memory as kern source metadata. Actual node API probes
show tag/line0/0 on the original paragraph kerns. Genuine subsequent
`copy_node()` assigns the copied kern its actual current input/line; the corrected
writer reads those real fields (observed tag1/line15 in the mixed fixture).
No tag or line is invented, forced to zero, filtered, or normalized by this patch.
The upstream SyncTeX source and existing prepared-source notices remain intact.

`build.py` derives a new native component from the immutable fonts04 build:
only the genuine SyncTeX compilation unit is rebuilt and replaced in a private
copy of the core archive. Every other ordered archive member, including existing
duplicate member names, must remain byte-identical. Existing linked input hashes,
headers, heap, VFS and input/output limits remain checked. The small linked
`capability.cpp` exports `oc_lua_text_semantics_abi()==1`; it does not substitute
for loading the pinned resource or passing actual PDF tests. No original engine,
VFS, graphics, build-recipe or profile file is edited.

Run inside the cached SDK container with the same read-only input mounts as the
fonts04 build, plus fresh private writable cache/build/output directories:

```sh
python3 semantics/build.py \
  --parent-build /task/build-fonts04 \
  --parent-candidate /task/candidate-fonts04 \
  --graphics-build /graphics-base --cache /build/sdk-cache \
  --work /build/work --out /build/candidate
python3 semantics/overlay.py ORIGINAL_LUAOTFLOAD_HARF_PLUG NEW_RESOURCE_OVERLAY
```

The host test driver orchestrates cached network-disabled containers, each limited
to two CPUs and 1GiB, and uses the already available read-only Poppler/Mutool QA
programs. Inputs are explicit paths; no binary/font/source download occurs:

```sh
python3 semantics/run.py P10_RUNTIME CANDIDATE_COMPONENT ACTUAL_FORMAT \
  LUAHARFBUZZ_SOURCE E1_RUNTIME_FILES RESOURCE_OVERLAY NEW_RESULTS
```

Each baseline and candidate compile uses identical original source bytes and the
same corrected native component. The driver checks full raw SyncTeX bytes,
native geometry status, page count/facts, E1 raster bytes, independent Poppler
raster bytes, and Mutool's actual glyph IDs/Unicode/positions/advances. It also
checks literal E1 extraction/search and independent Poppler bounding-box text.
Every failed equality remains a hard overall failure. `HB_CASE` selects one of
`original`, `mixed`, `lines-pages` or `latin` for focused diagnosis.

Accepted native evidence, 2026-11-18: all four pairs pass. The original fixed-clock
LuaHB format is consumed unchanged. A second fresh native build reproduces both
component hashes exactly. Old fonts04 versus the corrected baseline preserves
all four E1 rasters. Native RTL geometry remains its existing explicit unsupported
domain; the LTR geometry control remains available. No renderer geometry or
source-map refusal is bypassed. Browser/source-navigation hit precision and final
profile/package adoption remain master-owned.

Preserved failures include page-mode mid-run marker rounding/raster drift;
`direct_raw` backend state refusal on mixed text; the original out-of-node kern
writer read; and the too-large map-capacity test invocation. Generated PDFs,
formats, compiled components, archives and font data stay outside the repository.
