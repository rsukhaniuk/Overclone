# Synthetic authoring-assistance contract vectors

These are small synthetic source-assistance cases, not real diploma projects,
compiler fixtures, native measurements or compliance evidence. Each directory
contains `metadata.json`, one exact `packed.utf8` buffer and an independently
authored `result.json`. File descriptors slice the single buffer (including
BOM/CRLF); file hashes are correlation data, not verified by Rust. JSON field
order/whitespace are insignificant. Source Unicode is never normalized.

`authoring::tests::vectors::canonical_metadata_packed_bytes_and_results` checks
all seven contexts plus unknown-catalog refusal. Expectations are specified
from source delimiters and the frozen wire contract, not recorded producer
output. `reference-unicode` covers Ukrainian, emoji, combining marks, BOM and
CRLF; `citation` includes a separate selected bibliography; `definition` is a
read-only project declaration target. `inputPath` deliberately has a missing
literal include (`chap.tex`): its captured `chapter.tex` suggestion does not
make the project complete or prove compilation. `command`/`definition` disclose
unknown macro execution even when a literal declaration/name is useful.

Query is the exact UTF-8 source prefix immediately before `caretByte`, without
its command backslash or argument delimiters. Command replacements include the
observed backslash; other replacements cover the exact prefix only. Citation
and reference contexts support the current comma-separated segment, preserving
ASCII whitespace outside the prefix. Definition uses an exact observed name/key
and returns exact captured declaration/key spans. Outline ignores query and
returns source-order literal headings with enclosing heading indexes.

`complete` means this **bounded literal extraction** had no additional observed
uncertainty; it never means complete TeX semantics, compiled numbering or resource
availability. Every successful result carries `sourceIntentOnly`,
`macroExpansionUnsupported` and `runtimeResourcesUnverified`. More uncertainty
codes make `complete=false`. Quota exhaustion instead returns a small failed
envelope without payload; no truncated completed prefix is returned. Path
candidates identify exact captured targets; `include` insertion omits `.tex`
because LaTeX appends it. Binary paths have no source-definition span.

Catalog1.0 signatures are authored concise descriptions from the existing
`LatexCommandCompletion` catalog, the Rust reference/formatting/geometry grammar
and fixtures, and these inspected project fixtures:

- `native/tex/package-compatibility/fixtures/{thesis-packages,biblatex-bibtex,local-override}.tex`
- `native/tex/suite-resources/fixtures/{amsmath,authoring}.tex`
- `native/tex/ukrainian-resources/fixtures/unicode.tex`

The bundled catalog records official package/class provenance URLs and engine
applicability, but performs no lookup. All runtime-resource states are explicitly
unverified. A literal package/class mention is not effective loading, compiler
support, license adoption, downloaded data or native evidence. Master verifies
current official references before integrated catalog promotion. No copied manual
corpus, downloaded sources/fonts or dependency changes are included.

The unit tests additionally cover local `.sty/.cls` declarations, conservative
simple `def`, declaration defaults/redefinitions, conditional/macro uncertainty,
selected-root closure/cycles/missing files, duplicate and dynamic bibliography,
malformed wire/Unicode/budgets, exact limits and a bounded malformed-source corpus.
Actual five-second worker termination, editor staleness/read-only guards,
source navigation authority and whole-browser cold-offline UI acceptance remain
master-owned and are not established by these vectors.
