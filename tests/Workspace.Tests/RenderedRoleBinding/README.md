# Literal source and native text binding

The controller supports optional issued-proof integration; this package does
not wire the application UI or DI. The service consumes canonical existing Rust event indexes, the current analysis report,
the accepted successful compile ticket and owned capture, the existing native
SyncTeX policy, and actual PDF character facts. It implements no TeX grammar,
normative thresholds, source edits, compiler or analysis pipeline.

`BindAsync` requires matching project/session/generation/head, explicit work
type and selected root, all captured text bytes/paths/BOMs/hashes, current live
source and imported-byte preimages, and current analyzer epoch. It requires a
unique canonical literal event and matching typed observation, exact literal
PDF characters, actual box-center containment in the default native forward
box, and exact filename/line inverse queries at both character-range ends.
The Rust span remains the source-span authority. Query lines/columns never
establish token precision. There is no whitespace/case normalization, candidate
picking, coordinate tolerance, basename fallback or source/font substitution.

An issued proof is scoped to the owner, exact report/compile ticket and original
cancellation token. Call `VerifyCurrentAsync` and then `IsCurrent` immediately
before publishing/consuming it. A copied object cannot mint authority. The proof
retains the actual selected native characters and inspection/runtime identities.

The supported roles are typed figure/table captions and literal named
introduction/conclusions/references/appendix headings. `headingText` describes
only heading characters: it never establishes a whole section's page extent or
body role. Generic heading hierarchy is unavailable in existing events and is
refused. `captionText` likewise describes only the matched caption title.
`GeometryMethod` is `native-character-box-map-correlation` and
`GlyphInkMeasured` is false: native character boxes are not glyph-ink metrics.
No normative result follows from a binding alone. Author labels cannot supply
binding input. Global macro/package/include uncertainty refuses certification;
`missingMarginSides` alone is unrelated to heading/caption classification.

## Evidence and focused checks

`Fixtures/*.tex` are explicitly synthetic. The native fixture uses their exact
UTF-8 bytes with every LF converted to CRLF. Ukrainian text and an emoji precede
the source spans in comments; the rendered caption titles are ASCII. Both files
are captured, but this positive fixture does **not** execute `chapter.tex`.
There is no rendered-Cyrillic or included-caption positive claim.

The separate native run uses pinned P14 pdfLaTeX plus the accepted E1 renderer
and final source-owned area64 query. `record-native.cjs` records actual full-page
characters and the forward/inverse calls. The private native fixture, original
PDF/map, Rust request/packed bytes/responses, tool/source pins and receipts are
required via `RENDERED_ROLE_BINDING_FIXTURE`. Tests skip explicitly if absent.
No generated PDF, WASM or acquired external work is committed here.

Managed tests replay those exact recorded native facts through real source
analysis admission, workspace capture, compile admission/lifecycle, wire parser
and navigation policy. This verifies managed proof guards; it is not a fresh
native execution, UI capture, offline acceptance or rendered compliance claim.
Adversarial Rust source variants are genuine public-entry responses; their
compiler ports are deliberately modeled and do not claim those variants compile.

Run the focused class using the existing Docker .NET workflow with an isolated
artifact directory and the supplied fixture environment variable. The master
owns registration/controller integration and real browser acceptance.

## Smallest remaining Rust producer contract

Current `AnalysisThesisEventDto` loses the original heading level, while the KNU
caption observer is per-file and does not inherit an included file's document/
float context. The resolved thesis traversal knows its existing command and
environment but does not expose typed role detail. Preamble packages also remain
explicitly uncertain. These limitations must not be erased in C#.

A future explicitly versioned existing source-analysis response can attach a
bounded detail to its existing canonical event: heading level or figure/table
environment, the existing exact command/declaration/value spans, and a
role-relevant context status plus limitation codes from the resolved traversal.
Populate included-file caption context in Rust's existing traversal, not a C#
parser or an invented native span. Preserve old profile/wire behavior and caps.
Unknown package/macro execution remains uncertain: correlation may be displayed
with that uncertainty, but must not enable confirmed roles or normative Pass/
Fail. Do not infer package execution safety from a package name. Genuine Unicode
heading and included-caption positives require that independently reviewed
producer contract and actual matching native fixtures.
