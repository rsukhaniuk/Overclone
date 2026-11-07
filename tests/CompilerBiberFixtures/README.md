# Genuine Biber browser acceptance fixture

Synthetic source-only fixture, matched to the bundled biblatex 3.19 and Biber2.19.
The Latin/pdfTeX main and Unicode/Xe/Lua main explicitly request `backend=biber`.
Two captured BIB files provide three distinct Ukrainian/Latin citation keys.
The real UI imports these sources, an included chapter, and the existing portable
PNG/XBB assets. No BBL, BCF or generated compiler output is supplied by this fixture.
The layout deliberately uses the existing portable navigation markers and avoids
the separate broader-package graphics/navigation domain.

The pdfTeX/LaTeX main selects Ukrainian at bibliography start so the original
Cyrillic names and title use T2A. Babel's earlier switch back to English restores
its Latin encoding (OT1 here), even though T2A was loaded. The bibliography hook
runs after its English heading and normal-font setup; the body, heading and all
Latin markers remain unchanged. The hook shares the resource-declaration line
to preserve the existing source/navigation line numbers. BIB contents and the
separate Unicode/semantic fixtures are unchanged.

Run `CASE=biber` with each compiler choice in the existing published authoring
harness. Biber selection, genuine successful phase10 execution, at least three
successful TeX passes and absence of BibTeX substitution are required online and
after whole-browser shutdown, stopped/refused host and same-profile offline edit.
Final-pass warnings are checked separately from expected first-pass warnings.
The final PDF must contain all three bibliography markers, resolved citation
numbers/references, Ukrainian text, included equation, table and PNG. Every
captured input is checked byte-exact before/after compilation. Actual downloaded
PDFs retain the displayed compile hash and are inspected through the pinned E1
runtime; UI text/search is independently exercised.

The existing bounded production bridge exposes native phase identities/status,
not BBL bytes. Browser receipts distinguish Biber's genuine BBL-writing log plus
rendered bibliography content from the separately retained native direct BCF/BBL
producer proof. No hidden generated-file accessor is invented for this test.
`SOURCE_PDF_CHECKS=1` runs the existing strict portable root/include navigation
workflow; unsupported or wrong navigation is a failure, never an accepted skip.
The existing `CASE=biblatex` remains the separate explicit BibTeX backend case.
