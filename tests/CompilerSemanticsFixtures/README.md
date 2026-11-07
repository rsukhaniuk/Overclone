# Original-source browser semantic checks

The manifest pins existing unchanged repository source fixtures. The harness
imports their exact bytes through the real file picker; no repair macros or source
rewriting are inserted before the original online compilation.

- `CASE=cyrillic-semantics`, pdfLaTeX or LaTeX/DVI only:
  `CYRILLIC_FIXTURE=mixed-ligature.tex` (default) checks regular/bold original
  `fі`, `ffі`, `fіlm`, Cyrillic `Іі` and Latin `fi/ffi/ff` controls.
  `CYRILLIC_FIXTURE=mixed-semantic.tex` checks original Cyrillic `Іі` separately
  from the exact ASCII `I i` controls, including regular/bold and OT1 text.
  Both variants are needed to cover every control without altering either source.
- `CASE=harfbuzz`, LuaLaTeX only, imports the original genuine fontspec
  `Renderer=HarfBuzz` source. `LUAHARFBUZZ_SOURCE_DIR` supplies the already approved
  original Amiri TTF, checked against its exact byte length/SHA256. The real UI
  renames that imported font to `fonts/amiri-regular.ttf`; the source is unchanged.
  The exact Arabic phrase `مرحبا بالعالم`, including its space, must extract and
  search literally. No query reversal or Unicode normalization is performed.

All cases require actual current PDF rendering/download, exact captured project
bytes and production native phase traces. The downloaded PDF is independently
opened with the pinned E1 ABI for literal extraction/search. The same profile is
then reopened after closing the entire browser and stopping/refusing the origin,
with network disabled before navigation. A deliberate visible source edit is
saved and freshly compiled offline, with original semantic controls retained.
The changed source and new PDF hashes must differ; an old cached PDF cannot pass.
Stale export behavior retains the existing authoring harness checks.

Published browser execution and independent visual/raster comparison remain
master-owned. These tooling preflight/syntax checks do not claim product success.
