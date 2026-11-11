# Lua semantic collector node/depth controls

`tests/browser/compiler-lua-collection-bounds.test.cjs` tests the unchanged P13
LuaHB worker and its exact installed Luaotfload semantic collector. It does not
copy, patch or replace collector/producer functions or inject worker results.

The node matrix uses a genuine TeX-authored Arabic glyph plus zero-width kerns.
Each kern occupies its own commented source line, keeping the native line buffer
below200,000 bytes and each project source below1MiB. A read-only physical-line
observer records exactly65,535 /65,536 /65,537 nodes before collection; the actual
native collector succeeds below/at and reports its real unsupported-provenance
error above. The fixture has one literal Arabic character, so its text content
is far below the independent16,384-unit limit. Source-specific fixed node overhead
is checked by the actual observation, not accepted from a guessed formula.

The depth matrix is explicitly a QA collector-unit vector, not original-font
end-to-end coverage. It reads existing Lua closures using `debug.getupvalue` to
obtain the registered finalizer's actual `semantic_spans` function and the Harf
plugin's actual `semantic_cluster` producer; it does not alter either function,
the callback table or their registration. It constructs real LuaTeX discretionary
replacement nodes with a visible top-level RTL control and one nested glyph,
calls the actual producer/collector, and observes depths31 /32 /33 independently
of node/text limits. Below/at emits the exact expected ActualText; above gives the
actual native error and no PDF/maps. These controlled nodes are labelled QA data.

A known edge remains separate: when the only RTL glyph is entirely beyond the
recursion guard, the collector cannot observe RTL and returns unchanged before
its unsupported-error branch. An earlier QA fixture expected an emitted literal
and correctly failed; this does not establish original-document behavior for
that domain. The successful depth matrix requires visible RTL in the collected
scope and must not be described as universal nested-discretionary coverage.

Original unchanged Amiri Arabic controls run before/after the matrix, retaining
exact native source-map and geometry hashes. All eight accepted calls are real
frozen-worker invocations, with every input checked against its resource hash.
Above-limit vectors release no PDF/source map/geometry. This is not browser,
shaping parity, general isolation or additional-script acceptance.

Provide `COMPILER_SEMANTICS_BOUNDS_RUNTIME_DIR` (P13 pins/files),
`COMPILER_LUA_COLLECTION_BOUNDS_RESULTS` (fresh results directory), and
`COMPILER_SEMANTICS_BOUNDS_AMIRI` (the original approved548,016-byte font,
SHA256 `6988222efdde090e0926cabcaf695c5f551269efbf751b29b097eeb3dbf27e39`).
Run inside the cached browser tooling container with networking disabled,
read-only source/runtime/font mounts and an explicit writable results mount:

```sh
node --expose-gc tests/browser/compiler-lua-collection-bounds.test.cjs
```

No runtime rebuild or generated artifact is committed. Missing runtime reports a
skip rather than a native result. The separate `compiler-semantics-bounds` test
retains its converter and UTF16 matrices unchanged.
