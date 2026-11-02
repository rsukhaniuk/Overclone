# Synthetic Lua same-pass map correctness fixtures

These fixtures are **synthetic**, not a representative diploma project or an
all-engine/navigation/PWA acceptance claim. They exercise a genuine supplied
LuaLaTeX format and actual compiled PDF/map bytes from the new Lua ABI4 candidate.
No fixture string or log-derived position is returned as a native map.

## Independent oracle

`project/розділ/my thesis.tex` retains its original UTF-8 BOM and16 CRLF line
endings. Root, included `.tex` and preamble-input `.sty` paths have Ukrainian
characters and internal spaces. The style creates a real box at its own source
line4; shipping a copy retains native package-node provenance. The included file
has text at line2. Quoted explicit input is intentional; the fixture does not
claim arbitrary space-containing names work through every LaTeX package loader.
Unicode/emoji source content is retained exactly. The `unicode` variant also
renders Ukrainian with the supplied DejaVu Serif and executes a literal emoji
through native Lua's UTF-8 decoder (`128512`); it does not claim emoji glyph
coverage in that font.

The fixture explicitly ships two pages with four isolated known text markers.
`oracle.py` independently reads the **actual PDF** text matrices and ToUnicode
mapping, checks those marker texts/page numbers, and compares acquired genuine
native SyncTeX forward results. Inverse clicks are chosen5bp inside each actual
PDF text origin, not copied from a query result. Root's line-only inverse result
is the enclosing shipped box's closing line12 (second page15), not an invented
exact character span. The style resolves to line4 and include to line2.
The changed fixture moves root text down5bp and changes a marker; new actual
PDF/map hashes must differ and all forward/inverse controls pass again.

The narrow PDF reader accepts only this uncompressed fixture structure; it is
not product code or a general PDF parser. The0.002bp oracle bound accounts for
three-decimal PDF matrix serialization plus native query float rounding. It is
**not** an invented thesis-compliance tolerance. The zero-origin fixture can
validate raw-map positions but cannot establish all writer transforms. Native
geometry remains explicitly `unsupported/writer-origin-unavailable`, with null
matrix/box and genuine writer page count2.

## Native controls

`test-runtime.cjs` creates a fresh real WASM module per case, verifies every
supplied runtime input against the immutable suite pins, seeds the genuine
pinned format, and preserves the exact source bytes before and after compilation.
It tests root/include/style map input bindings, canonical raw header/postamble,
complete-or-none map/geometry copy, wrong token, default/mode0 despite document
enablement, too-small cap and actual **postamble** overflow, repeated/invalid
configuration/run/getter revocation, old-pass AUX carry and forbidden map carry.
After-shipout controls use real native TeX failure, a real PDF-finalization Lua
callback failure, and actual native output-quota failure. The PDF stays usable
on optional-map overflow; failed native/PDF/output attempts publish no map.

`test-cancel.cjs` arms maps, waits until the actual infinite Lua loop after the
fixture's shipouts, physically terminates that worker, verifies no result was
published, then compiles a successful fresh-module PDF/map. It does not pretend
to call getters in a terminated module or report modeled cancellation.

`test-config.cjs` exercises the real WASM config/read boundary and checks that
no private begin/finish/stage/close/page hooks are exported. `test-compat.cjs`
compares all ten indexed outputs **byte-for-byte**, including log/AUX, between
the reviewed ABI3 pair and ABI4 with no map capability enabled. The synthetic
source explicitly suppresses optional PDF timestamps/IDs before compilation;
output bytes are never normalized for comparison.

`../../luatex-engine/synctex/regressions.sh` runs the existing ABI, Unicode-path,
bibliography/carry, output namespace, no-output, quota/lower-hook, confinement
and physical-cancellation tests against **ABI4**. Legacy `abi3-*` filenames in
that reused bibliography harness are labels only; each receipt records actual
candidate hashes. This is regression validation of new bytes, not a redo or
adoption of the accepted R1 milestone. All former default ABI3 assertions remain
available by leaving `LUA_ABI` unset.

Run `bash native/tex/synctex-fixtures/lua/run.sh` from `/work` in the supplied
cached tool container, with a fresh `RESULTS` directory and new candidate pair.
There are no downloads, host installations, application changes or publication.
The final private handoff records exact command/exit/count/skips, immutable source
and candidate identities and the retained raw PDF/map/query/sidecar evidence.
Root owns new profile/phase14 admission, transport/geometry composition and real
published/cold-offline workflows. Those gates remain open.
