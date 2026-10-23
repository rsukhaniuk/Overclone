# Bounded upstream SyncTeX query adapter (ABI/schema 1)

Root-review candidate, not accepted browser navigation. Uses Jérôme Laurens's
actual pinned TeX Live `22d3aa7aded98382a59b1f3dc0e7142a810bdeca`
`synctex_parser.h` scanner/display_query/edit_query/next_result APIs. No parser,
source-text heuristic, PDF reader or copied upstream sources are included.
Upstream's weak line/name matching is retained: requesting line 100 in the actual
`footest` fixture returns native line 99, not a manufactured exact match. Root must
bind returned filenames to exact captured sources; even absolute names are data.

## ABI

See `synctex_query.h` for signatures, statuses and bounds. Single-threaded module:
`load → forward/inverse → result_length/result_copy → close`. Only the first load
**attempt** is allowed, including failed attempts. Close is terminal/idempotent.
A refused reload preserves the original scanner but clears results and returns
`SQ_STATE`; it never installs the new map. Multiple queries are allowed.
Version/status/error-message/length accessors do not invalidate a result.
Every load/query attempt, failed copy and close erases internal result bytes;
empty queries return `SQ_EMPTY` and length zero, not an empty JSON document.
Successful copy copies exactly length bytes without a NUL. Invalid copy leaves
caller memory untouched. Previously copied caller memory is caller-owned.

Map transport accepts canonical LF `SyncTeX Version:1\n` prefix, no embedded NUL,
1..1,048,575 bytes. This exact magic gate excludes gzip/unsupported versions;
all remaining parsing belongs to upstream. No claim of whole-format canonical
validation is made. Forward filenames are strict UTF-8 project-relative paths,
1..1024 bytes; no empty/dot/parent segments, colon, backslash, NUL or controls.
Source lines are positive int32; column 0 means unspecified; page hint -1 means
unspecified, otherwise 1..512. Input inverse page is 1..512, h/v finite within
±1,000,000 visible big points; double input is converted to upstream float.

Result is UTF-8 JSON, at most 32,768 bytes:

```json
{"schema":1,"direction":"forward","coordinateSpace":"synctex-visible-bp-top-left","retainedCount":1,"truncated":false,"matches":[{"file":"main.tex","tag":1,"line":10,"column":null,"page":1,"point":{"h":10,"v":20},"box":{"h":10,"v":20,"width":10,"height":10,"depth":0}}]}
```

The illustration is synthetic, not measured output. Each value comes directly
from the corresponding upstream getter. Column 0 (header documentation) or -1
(pinned implementation `SYNCTEX_DFLT_COLUMN`, parser.c:1378) becomes null. No mean
line/token span is substituted. Visible box v is its **baseline**, not top edge;
upstream's top edge is v-height and bottom edge v+depth. The wrapper does not
transform coordinates for PDF crop, rotation, physical mm or DOM pixels. Native
file names must be valid UTF-8/control-free and <=1024 bytes, but need not be
project-relative; quotes/backslashes are JSON escaped. C0/C1/DEL and U+2028/2029
are refused. Tag/line must be positive, page 1..512, column >=-1; all coordinates
finite within ±1,000,000, dimensions nonnegative <=1,000,000. Unsupported values
fail the entire query, with no partial result. Over-budget strings/aggregate
serialization return fixed `SQ_BUDGET`; native messages are not returned as errors.
Only the 33rd result handle is inspected to set truncated, never a total count.

## Filesystem and integration limits

A fixed `.synctex-query-private` directory is exclusively created (0700), then
fixed `map.synctex` with O_EXCL/O_NOFOLLOW (0600). Preexisting file/directory/link
is refused without removal. The scanner parses fixed `map.pdf` (which need not
exist) with parse=1; the owned sidecar is removed after parsing. Cleanup checks
owned inode/device/mode and never recursively deletes anything. The caller must
provide an exclusively owned, stable CWD/VFS with no concurrent filesystem actor
or chdir; this is not an adversarial-host race-proof filesystem sandbox. Native
tests use unique temporary CWDs; production WASM must have only private virtual
FS, no host mounts. No project/PDF/source file is read by query calls.

The upstream parser may allocate, consume CPU and write native diagnostics to
stdout/stderr. Root must use an isolated worker, memory/deadline/cancellation
limits, and avoid forwarding untrusted native logs as trusted HTML or secret-safe
messages. Input bounds alone are not a parser security audit or heap/CPU bound.
Use the C numeric locale; serialization refuses non-JSON locale formatting.
Map/PDF/source freshness, renderer geometry and accepted-output authority are
entirely caller-owned. No producer same-pass, WASM or browser acceptance is claimed.
Dependency notices/release licensing remain root-owned; proposed AGPL-compatible
release direction is not clearance.

## Standalone native build and tests (inside the assigned offline Docker image)

No host tools, downloads, shared build volumes or source copies required:

```sh
S=/inputs/texlive/texk/web2c/synctexdir
G=/inputs/generators
Q=/work/native/tex/synctex-query
O=/control/candidate
c++ -std=c++17 -Wall -Wextra -Werror -O2 -I "$Q" -I "$S" \
  "$Q/synctex_query.cpp" "$Q/tests/query_driver.cpp" \
  "$G/texk/web2c/libsynctex.a" "$G/libs/zlib/libz.a" -lm -o "$O/query-driver"
python3 "$Q/tests/test_query.py" "$O/query-driver" /inputs/texlive \
  "$G/texk/web2c/synctex" "$O/test-output"
```

Verified: native warning-as-error build and **35 isolated real-parser cases**, plus
in-process lifecycle/invalid-input/no-stale-result controls. Actual upstream
`footest` forward/inverse results are compared to independently executed upstream
CLI output (including native box baseline versus CLI bottom edge). Small synthetic
canonical maps test spaces/Ukrainian/emoji/escaping, 32/33/34 results, page/buffer
bounds, malformed/native-data/budget cases and preexisting filesystem controls.
Synthetic fixtures are not engine-produced PDFs/maps. The test CLI receives an
empty placeholder PDF filename only; neither it nor the adapter parses PDF bytes.
The tests remove SYNCTEX_EDITOR/VIEWER from the oracle environment and never run
editor/viewer commands. No sanitizer run or product WASM build was performed.

## Root-only pinned Emscripten build/link recipe (NOT executed here)

Run only in the existing pinned Emscripten Docker image, using explicit cached
sources/config headers below. Do not link native `.a` archives into WASM. Keep
all objects and generated artifacts in a new private output directory. This is
an integration recipe to verify, not a claimed passing build or runtime pin.

```sh
S=/inputs/texlive/texk/web2c/synctexdir
Z=/inputs/texlive/libs/zlib/zlib-src
ZH=/inputs/generators/libs/zlib
Q=/work/native/tex/synctex-query
O=/control/candidate/synctex-wasm
mkdir "$O"
for unit in adler32 crc32 deflate infback inffast inflate inftrees trees \
            zutil compress uncompr gzclose gzlib gzread gzwrite; do
  emcc -O2 -DHAVE_UNISTD_H -I "$ZH" -I "$Z" -c "$Z/$unit.c" -o "$O/$unit.o"
done
for unit in synctex_parser synctex_parser_utils; do
  emcc -O2 -DSYNCTEX_NO_UPDATER -I "$S" -I "$ZH" -I "$Z" \
    -c "$S/$unit.c" -o "$O/$unit.o"
done
# Wrapper warnings are checked separately from upstream source warnings.
em++ -std=c++17 -Wall -Wextra -Werror -O2 -I "$S" -I "$Q" \
  -c "$Q/synctex_query.cpp" -o "$O/query.o"
em++ "$O"/*.o -O2 --no-entry -sMODULARIZE=1 -sEXPORT_NAME=createSyncTeXQuery \
  -sENVIRONMENT=worker -sFILESYSTEM=1 -sALLOW_MEMORY_GROWTH=1 \
  -sMAXIMUM_MEMORY=134217728 -sSTACK_SIZE=1048576 \
  -sEXPORTED_FUNCTIONS='["_malloc","_free","_sq_version","_sq_status_code","_sq_error_message","_sq_load","_sq_forward","_sq_inverse","_sq_result_length","_sq_result_copy","_sq_close"]' \
  -o "$O/synctex-query.js"
```

Root must validate cached generated zconf.h compatibility, choose final memory
limits/module glue, verify actual exported calls and add bridge/orchestration;
no generated WASM/C/upstream sources belong in this subtree.
