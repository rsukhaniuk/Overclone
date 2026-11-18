This is an explicitly synthetic Ukrainian root and **actually executed** included
file fixture. The original target uses UTF-8 CRLF, Ukrainian `І/і` in both literal
caption titles, and an emoji in each preceding comment. It is separate from the
older ASCII-caption fixture whose chapter was intentionally not executed.

The private first witness is `/tmp/oop-r2-role-binding-unicode-20261118`. Its
unchanged 284-byte root and 159-byte chapter compile successfully with the exact
assembled P16 resources. Final E1v2 native character ranges contain the original
titles. Genuine default forward queries contain every nonspace character center;
first/last inverse queries return the exact original filename and line (root7,
chapter3). These are text/nominal-box/map correlations, not glyph-ink or normative
proof. The recorder never changes native functions, inputs, fonts or maps.

Source profile 1.6 refuses role issuance because it lacks a typed semantic
context for `fontenc` and `babel`. The independent formatting scans do not inherit
include context. The legacy regression preserves that refusal and the separate
ASCII control isolates include context from package uncertainty.

Source profile 1.7 obtains typed caption-title observations from its project
traversal. This fixture uses the pinned P16 pdfTeX article context with
`fontenc[T2A]` followed by `babel[ukrainian]`, an all-`.tex` capture, and no
local resource shadows. The managed replay binds both original Ukrainian titles
to their native character ranges and checks that source edits, worker replacement,
and a same-PDF recompile retire issued proof. Wrong compiler or capability pins
refuse evidence. Caption numbers, glyph ink and illustration association need
separate evidence.

Prepare an empty private destination:

```sh
python3 tests/Workspace.Tests/RenderedRoleBindingUnicode/prepare-fixture.py /tmp/fresh-unicode-role-fixture
```

Invoke `document_engine::analyze_source` on each generated `request.json` and
`packed.bin` to create `response.json`. For the target only, copy its request
to `request-v1_7.json` with `payload.profile.version` set to `1.7.0`. Use the
same packed bytes to produce `response-v1_7.json`. The handoff records the exact
producer identity. Compile the target
with the existing source-owned `page-origin-navigation/run-current-project.cjs`
using `PROJECT=<fixture>/project` and the exact P16 runtime. Run the new recorder
with the target, exact final E1v2 directory and exact area64 query directory.
No source edits are needed between these operations.

The managed replay uses the real adapter/coordinator/binding with separately
executed native/Rust evidence; browser IO/compile ports remain modeled:

```sh
RENDERED_ROLE_BINDING_UNICODE_FIXTURE=/unicode-fixture dotnet test tests/Workspace.Tests/Workspace.Tests.csproj --no-restore --filter FullyQualifiedName~Workspace.Tests.RenderedRoleBindingUnicode
```

Use the existing cached Docker toolchains with network disabled and private build
artifacts. The exact native commands, source/runtime pins and managed result are
in the private handoff. This fixture does not perform browser/offline acceptance.
