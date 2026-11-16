# Current-lineage origin candidates

These opt-in components compose the accepted native origin witnesses with the
P14 compiler lineage. They preserve P13/P14 and require a separately closed
profile. Product transport, publication and browser acceptance remain separate.

`compose-luahb.py` first relinks retained P14 objects and requires byte-identical
P14 loader/WASM output. It retains genuine HarfBuzz, corrected semantic kern
provenance, declaration256 and VFS policies. It changes only native pdfgen and
pdfpagetree archive members plus the geometry observer, recording the complete
ordered archive-member comparison, including duplicate upstream member names.
`build-current-converter.py` composes DVI font-semantics before the accepted
origin overlay, preserving real ActualText markers; Xe retains its graphics
lineage. Native source-path assertion strings use a canonical path.

| Candidate file | Bytes | SHA-256 |
| --- | ---: | --- |
| Lua luatex.js | 193626 | `98f6efae0509df0b3c5b739c3c60037b514df76eb07b43561ebb124d617c2c22` |
| Lua luatex.wasm | 6017681 | `54155196638dc8da2b8c5732d3f1c78da6e7147537d96d5fd1cc19725370af4b` |
| DVI oc-dvi.js | 13332 | `1d522a3a07be08a657463ab226cf54de5c40b13286e79e1881808c080aea371a` |
| DVI oc-dvi.wasm | 694247 | `e3c21bbe81011384d3600cd24690ae71b7f556c028f76d8aa98b0ba6ec99404c` |
| Xe oc-dvi.js | 13088 | `8775bd9fb0d15d7f5933518d0728d792ae07d040332b4b257c7ae0b5d3741591` |
| Xe oc-dvi.wasm | 692967 | `bb50c13a3c1cb2e6cb899ae63eb6bc904cdfddaf4de5435db7888bd7d9f4b546` |

All three native origin capabilities remain version1. Lua additionally requires
ABI5, graphics1, HarfBuzz1, fontClosure1, textSemantics1 and declaration256. DVI
requires ABI2, graphics1, origin1 and fontSemantics1; Xe requires ABI3, graphics1
and origin1. Existing formats remain unchanged. No native seal/witness authority
is exported. Matrices retain actual writer measurements; crop/rotation remain
viewer semantics and are applied once by the consumer.

Native evidence covers nine current-lineage four-page nonzero-origin scenarios
(Lua normal/view/table; DVI/Xe normal/view/mag1200) with independent actual PDF
and exact native root/include filename+line oracles; all54 existing malformed,
duplicate, out-of-range and unsupported-domain assertions; 13 additional
current-LuaHB inherited-root/reset/late-channel compiles with exact P14 legacy
PDF/log/map/geometry comparisons; and all21 P14 Lua semantic budget controls.
Independent fresh builds reproduce every candidate file byte for byte.

`run-current-project.cjs` is test tooling, reusing exact real native FFI calls
from the accepted fixture. It selects the actual semantic pdfTeX/DVI executable,
keeps original package sources and carries genuine generated BLX controls.
`check-corpus-navigation.cjs` chooses actual pinned E1 glyph boxes before map
queries and preserves every native reply. With the separate area64 query fix,
fresh package and biblatex pdfLaTeX root/include checks return exact expected
lines. The six DVI/Xe/Lua package cases still refuse geometry because their
unclassified native specials/literal/object channels remain unsafe. Origin
support alone does not close those cells. No matrix, coordinate or tolerance
is invented to label them successful. Geometry-neutral typed native channel
support requires its own derivative, tests and immutable pins.

The generic native pdfTeX adapter still uses its existing zero-origin mapping;
this source package adds explicit-origin witnesses only to DVI/Xe converters
and the current LuaHB producer.
