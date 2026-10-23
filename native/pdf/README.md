# Optional project C++ PDF ABI

Not enabled by the root native build. Not a bundled/selected production PDFium
package. `lineage.json` pins the owned corrected E1 ancestor. `e1.cpp` is that
implementation plus an include of `actions.inc`; the legacy ABI version1,
36-byte budget, numeric fields, statuses, ownership and geometry algorithms stay
unchanged. The header corrects the documentation: native coordinates are **raw
PDF default-user-space units**, not universally physical 1/72-inch points.
Physical measurements are indeterminate; UserUnit gaps remain. No KNU claim.

The page-budget maximum in `valid_budget` is512 (raised from32); same ABI1,
36-byte budget struct, per-page/total bytes/raster/handles/text/search caps.
Explicit32-budget calls still reject33-page documents; a new512-budget call can
open up to512 pages. On-demand page rendering; no eager512-page scan.

Standalone build, inside pinned Emscripten6.0.13 only:

```sh
sh native/pdf/build.sh EXPLICIT_PDFIUM_INCLUDE_DIR EXPLICIT_LIBPDFIUM_A OUTPUT_DIR
```

The caller supplies already provisioned cached headers/library and an existing
owned output directory. It compiles a real object and links new loader/WASM with
C++17, `-Wall -Wextra -Werror`; failures propagate. No download, install, root CMake
change or product enablement. Native producer, dependency source, notices/license
closure and adoption remain separate unresolved gates.

## Additive optional v1 operations

Old runtimes lack `_e1_actions`; callers must discover the symbol, never synthesize
support. New exports `e1_capabilities(page)` and `e1_actions(page)` require an open
page. Both publish immutable JSON through the existing result/copy/release API.
No native pointer or borrowed string crosses that boundary. Capabilities name
raw coordinate space, indeterminate physical measurement, action-data availability,
hard caps and absence of action execution. They use the caller's output budget.
Legacy payload schemas are untouched, including misleading historical field names.

Action JSON schemaVersion1: raw coordinate/physical status, count, ordered links.
Each link has index; rectangle status/raw coordinates; action availability/native
type/type-status; URI and filePath status/native byte count/value; local destination
status/accessor/pageIndex. Strings are untrusted **data**, not HTML, safe URLs,
permissions or instructions. No URI opening, file access, action execution,
navigation, JavaScript/XFA callback or other document load is implemented.

- At most128 links (a 129th observation returns LIMIT), fixed stack storage.
- Native string cap4096 bytes **including terminating NUL**. Length is checked
  before fill/scan; no truncation. Empty native string is distinct from unavailable.
- Strict UTF8 excludes overlong sequences, surrogates and values above U+10FFFF;
  invalid data returns `invalid-utf8`/null. The explicit native byte count remains.
- JSON quotes/backslashes/control characters are escaped, genuine Unicode retained.
- Two bounded serialization passes count exact bytes before dynamic JSON allocation,
  obey caller output/live/aggregate budgets and publish only a complete result.
  Cap/budget failure is E1_LIMIT3 with result0/no partial output; handles stay valid.
- Native action type0 remains unknown, including the accepted authored JavaScript
  case. There is no guessed JavaScript/GoTo classification from source scanning.
- Destination locality is gated by action kind BEFORE interpreting any index.
  RemoteGoTo2/EmbeddedGoTo5 are always `not-local`, null index/source; no target
  getter is invoked by the adapter. Other/unknown actions are `not-applicable`,
  null index/source, even when incidental target data exist.
- LocalGoTo1 uses `FPDFAction_GetDest` (`action-api`) and a real page-count check;
  absent/invalid targets stay unavailable/invalid. Only an absent action may use
  `FPDFLink_GetDest` (`link-api`) for local/direct destinations. Labels identify
  actual accessors, not dictionary syntax. Raw getter numeric agreement is not
  proof of semantic locality. No remote/embedded target is opened.
- Rectangles are native raw units with finite/order status, never physical geometry
  or native-to-source navigation proof.

Test-only harnesses live under `tests/*BrowserHarness`. This adapter does not
establish general password/OCR/Unicode/shaping support, physical geometry,
source navigation, production runtime adoption or dependency license clearance.
