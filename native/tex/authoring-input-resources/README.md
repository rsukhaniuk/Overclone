# Historic authoring package and font resources

`resources.lock.json` pins 29 original TeX Live 2023 runtime, source and
publication archives. It stages 104 additive resources (1,140,849 bytes) for
algorithms, algorithmicx, bbm-macros, changepage, doublestroke, import, stmaryrd,
totcount, biblatex-gost, XITS and XeTeX's TeX-ligature mapping. The original BBM
font source/metric archive is retained in the verified source closure; this
recipe does not generate missing outlines or substitute another font. The full
source/documentation archives and original notices accompany release materials.
Their total archive size is 4,285,024 bytes; decoded regular members total
6,928,348 bytes. Acquisition limits remain 64 MiB compressed and 128 MiB decoded.

The four XITS text fonts retain their actual family and styles (Regular, Bold,
Italic, Bold Italic), with the original OFL notice. No proprietary font, project
source or project asset is included. Fonts outside this explicit closure remain
separate compatibility requirements.

XeTeX's existing WebAssembly font manager reads its explicit catalogue. The
existing C++/FreeType metadata extractor in `../suite-resources/font-catalog.cpp`
regenerates all 77 parent entries byte for byte and appends the four actual XITS
faces. The resulting catalogue is the only parent resource replacement. No font
alias, filename substitution, engine patch or format rebuild is used.
`catalog.metadata.json` retains the exact extracted names, style bits and optical
metadata, including the retained reader's duplicated subFamilyID ABI fields.

Prepare the extractor with the pinned Emscripten 6.0.13 image and the retained
FreeType library whose digest appears in the lock. Supply the matching original
FreeType public headers and configured headers from its accepted build:

```sh
em++ native/tex/suite-resources/font-catalog.cpp /inputs/freetype.a \
  -I/inputs/freetype-config -I/inputs/freetype/include \
  -O2 -fexceptions -sMODULARIZE=1 -sINVOKE_RUN=0 -sFORCE_FILESYSTEM=1 \
  -sEXPORTED_RUNTIME_METHODS=FS,callMain -sENVIRONMENT=node \
  -o /tool/font-catalog.js
```

Then run in a network-disabled container using the separately acquired, exact
archives, immutable P15 resource inventory and verified extractor:

```sh
python3 native/tex/authoring-input-resources/prepare.py \
  --archives /inputs/archives --base /inputs/profile15 \
  --catalog-tool /tool/font-catalog.js --out /outputs/authoring-resources
```

The command validates SHA256 and historic-inventory SHA512, budgets, declared
regular members, parent bytes and the metadata extractor before staging a fresh
output. It refuses resource collisions, modified inputs, missing members and a
changed parent catalogue. `pins.json` contains 105 resources: the 104 additions
and one explicitly identified replacement. The output is an assembly input;
this recipe does not select a compiler profile, change input/output quotas,
fetch dependencies or establish published-browser acceptance.
