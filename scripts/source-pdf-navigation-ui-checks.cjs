// Real published-app checks. Only user actions and DOM/storage observations;
// no fabricated map, native reply, provider, renderer or application authority.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const H = require('./review-ui-helpers.cjs');

function navigationPairs(mainPath, fixture) {
  if (['portable', 'packages', 'biblatex'].includes(fixture))
    return [[mainPath, 'Portable compiler fixture.'], ['chapter.tex', 'Included file, reference']];
  return fixture
    ? [[mainPath, 'ASCII context: Kyiv 123.'], ['chapter.tex', 'Chapter cites article']]
    : [[mainPath, 'Modern CSharp pipeline']];
}

// Fixed synthetic-fixture oracles, not inferred from compiler logs or source.
// Collapse extraction whitespace only; never fold Unicode or accept unresolved
// counters merely because the surrounding title/author happens to be present.
function assertPackageText(text, fixture, compilerChoice) {
  assert.ok(['packages', 'biblatex'].includes(fixture), 'known package fixture');
  assert.ok(['local-authoring-v4', 'latex-dvi', 'xelatex', 'lualatex'].includes(compilerChoice), 'known compiler');
  const normalized = text.replace(/\s+/g, ' ').trim();
  const literals = [
    'Portable compiler fixture. Section 2 is resolved.',
    'Included file, reference to Section 1.',
    'See section 2. Book citation [1].',
    '[1] Zoe Zimmer. Portable Compiler Reference. Offline Fixture Press, 2025.',
    'Hyperlink fixture', 'Listing marker', 'Package compatibility entry.',
  ];
  for (const literal of literals) assert.ok(normalized.includes(literal), `actual ${fixture} PDF text: ${literal}`);
  assert.doesNotMatch(normalized, /\?\?|\[book\]/, 'no unresolved reference or literal citation key');
  if (['xelatex', 'lualatex'].includes(compilerChoice)) {
    assert.ok(normalized.includes('Привіт, Київ!'), 'actual Unicode fixture text');
  }
  return { literals, whitespaceNormalizationOnly: true, bibliographyBackend: 'BibTeX',
    citationStyle: fixture === 'biblatex' ? 'biblatex numeric' : 'classic plain' };
}

// Read actual displayed rectangles relative to the actual canvas. PDF search
// glyphs are an independent point oracle; SyncTeX's enclosing box is not one.
async function packageView(page) {
  return H.$(page, 'pdf-pane').evaluate(pane => {
    const canvas = pane.querySelector('[data-testid=pdf-canvas]');
    const canvasBox = canvas?.getBoundingClientRect();
    const box = element => {
      const value = element?.getBoundingClientRect();
      return value && canvasBox ? { left: value.left - canvasBox.left, top: value.top - canvasBox.top,
        width: value.width, height: value.height } : null;
    };
    const glyph = Array.from(pane.querySelectorAll('[data-testid=glyph-highlight]'))
      .find(element => element.title === 'available native display glyph boxes'
        && element.getBoundingClientRect().width > 0 && element.getBoundingClientRect().height > 0);
    const search = pane.querySelector('[data-testid=search-status]');
    return { document: pane.dataset.document, generation: pane.dataset.generation,
      drawGeneration: canvas?.dataset.drawGeneration || null, canvasPdfHash: canvas?.dataset.pdfHash || null,
      canvasPage: canvas?.dataset.pdfPage === undefined ? null : Number(canvas.dataset.pdfPage),
      page: Number(pane.querySelector('[data-testid=page-label]')?.textContent.match(/Page (\d+) of/)?.[1] || 0),
      preview: pane.querySelector('[data-testid=preview-status]')?.dataset.previewStatus,
      drawn: pane.querySelector('[data-testid=canvas-status]')?.textContent,
      canvas: canvasBox ? { width: canvasBox.width, height: canvasBox.height,
        rasterWidth: canvas.width, rasterHeight: canvas.height } : null,
      sourceBoxCss: box(pane.querySelector('[data-testid=source-pdf-highlight]')),
      glyphBoxCss: box(glyph), glyphTitle: glyph?.title || null,
      search: { status: search?.dataset.searchStatus, count: Number(search?.dataset.matchCount || 0),
        scanned: Number(search?.dataset.scannedPages || 0), total: Number(search?.dataset.totalPages || 0),
        selected: pane.querySelector('[data-testid=selected-result]')?.textContent },
      sourceMapStatus: document.querySelector('[data-testid=source-map-status]')?.textContent || '' };
  });
}

function rasterBox(view, box) {
  if (!view.canvas || !box || view.canvas.width <= 0 || view.canvas.height <= 0) return null;
  const sx = view.canvas.rasterWidth / view.canvas.width, sy = view.canvas.rasterHeight / view.canvas.height;
  return { left: box.left * sx, top: box.top * sy, right: (box.left + box.width) * sx, bottom: (box.top + box.height) * sy };
}

async function verifyPackage(page, project, mainPath, fixture, label) {
  assert.match(label, /^[a-z0-9-]+$/);
  assert.ok(process.env.RESULT_DIR, 'package point evidence needs the existing result directory');
  const evidenceFile = path.join(process.env.RESULT_DIR, label + '-evidence.json');
  fs.closeSync(fs.openSync(evidenceFile, 'wx', 0o600));
  const evidence = { label, fixture, result: 'RUNNING', records: [], evidenceArtifact: path.basename(evidenceFile),
    actualBrowserActions: true, unchangedProject: false };
  const retain = () => fs.writeFileSync(evidenceFile, JSON.stringify(evidence, null, 2) + '\n');
  retain();
  let candidate;
  try {
    const before = await H.head(page, project);
    const pdfHash = await H.$(page, 'compile-panel').getAttribute('data-output-hash');
    evidence.pdfHash = pdfHash; retain();
    for (const [file, needle] of navigationPairs(mainPath, fixture)) {
      candidate = { file, marker: needle, stage: 'select-source' };
      evidence.records.push(candidate); retain();
      await page.locator('[data-testid="select-file"]').filter({ hasText: file }).first().click();
      const expectedId = before.files.find(f => f.path === file)?.id;
      await page.waitForFunction(({ id, needle }) => {
        const el = document.querySelector('[data-testid=editor]');
        return el?.dataset.fileId === id && el.value.includes(needle);
      }, { id: expectedId, needle });
      const editor = H.$(page, 'editor'), source = await editor.inputValue();
      const caret = source.indexOf(needle), expectedLine = source.slice(0, caret).split('\n').length;
      candidate.source = { fileId: expectedId, caretUtf16: caret, expectedMarkerLine: expectedLine,
        markerLineText: source.split('\n')[expectedLine - 1], utf16Units: source.length };
      candidate.stage = 'forward'; retain();
      assert.ok(caret >= 0, 'known actual source marker');
      await editor.evaluate((element, at) => { element.focus(); element.setSelectionRange(at, at); }, caret);
      await H.$(page, 'source-go-to-pdf').click();
      await page.waitForFunction(() => {
        const text = document.querySelector('[data-testid=source-map-status]')?.textContent || '';
        return text.length > 0 && !text.startsWith('Locating');
      }, null, { timeout: 45000 });
      candidate.forward = await H.$(page, 'source-map-status').textContent();
      candidate.forwardPage = Number(candidate.forward.match(/^PDF page (\d+)/)?.[1] || 0);
      candidate.forwardView = await packageView(page); retain();
      assert.match(candidate.forward, /^PDF page /, 'actual forward navigation status: ' + candidate.forward);
      assert.ok(candidate.forward.includes(file + ':'), 'forward match identifies the captured file');
      await page.waitForFunction(() => {
        const pane = document.querySelector('[data-testid=pdf-pane]'), canvas = pane?.querySelector('[data-testid=pdf-canvas]');
        return pane?.querySelector('[data-testid=preview-status]')?.dataset.previewStatus === 'Available'
          && pane?.querySelector('[data-testid=canvas-status]')?.textContent === 'Drawn'
          && pane?.querySelector('[data-testid=source-pdf-highlight]')
          && canvas?.dataset.drawGeneration === pane?.dataset.generation
          && canvas?.dataset.pdfHash === pane?.dataset.document;
      }, null, { timeout: 45000 });
      candidate.forwardView = await packageView(page); candidate.stage = 'search-marker'; retain();
      const pane = H.$(page, 'pdf-pane');
      // Spaces can have no glyph rectangle. These unique, non-whitespace words
      // are on the same captured fixture lines and provide measured ink points.
      const searchNeedle = file === mainPath ? 'resolved' : 'file';
      candidate.searchNeedle = searchNeedle; retain();
      await pane.locator('input[aria-label="Search PDF"]').fill(searchNeedle);
      const searchGeneration = await pane.getAttribute('data-generation');
      await pane.getByRole('button', { name: 'Search document', exact: true }).click();
      await page.waitForFunction(old => {
        const pane = document.querySelector('[data-testid=pdf-pane]');
        return pane?.dataset.generation !== old && ['Complete', 'Partial', 'Error', 'Cancelled', 'Empty']
          .includes(pane?.querySelector('[data-testid=search-status]')?.dataset.searchStatus);
      }, searchGeneration, { timeout: 45000 });
      candidate.searchView = await packageView(page); retain();
      assert.equal(candidate.searchView.search.status, 'Complete', 'exact marker search checks the entire PDF');
      assert.equal(candidate.searchView.search.count, 1, 'exact marker has one real native PDF hit');
      assert.equal(candidate.searchView.search.scanned, candidate.searchView.search.total);
      // Select the hit explicitly; a complete search does not select a result.
      candidate.stage = 'select-search-hit'; retain();
      const hitGeneration = await pane.getAttribute('data-generation');
      await pane.getByRole('button', { name: 'Next result', exact: true }).click();
      await page.waitForFunction(old => {
        const pane = document.querySelector('[data-testid=pdf-pane]'), canvas = pane?.querySelector('[data-testid=pdf-canvas]');
        return pane?.dataset.generation !== old
          && pane?.querySelector('[data-testid=selected-result]')?.textContent === 'Result 1 / 1'
          && pane?.querySelector('[data-testid=preview-status]')?.dataset.previewStatus === 'Available'
          && pane?.querySelector('[data-testid=canvas-status]')?.textContent === 'Drawn'
          && canvas?.dataset.drawGeneration === pane?.dataset.generation
          && canvas?.dataset.pdfHash === pane?.dataset.document;
      }, hitGeneration, { timeout: 45000 });
      candidate.glyphView = await packageView(page); candidate.stage = 'compare-forward-glyph'; retain();
      // Search selection redraws. Reacquire BOTH rectangles from that same
      // actual frame; the earlier forward snapshot remains independent evidence.
      const forwardBox = rasterBox(candidate.glyphView, candidate.glyphView.sourceBoxCss);
      const glyphBox = rasterBox(candidate.glyphView, candidate.glyphView.glyphBoxCss);
      const point = glyphBox ? { x: (glyphBox.left + glyphBox.right) / 2, y: (glyphBox.top + glyphBox.bottom) / 2 } : null;
      candidate.spatial = { forwardBoxRaster: forwardBox, glyphBoxRaster: glyphBox, toleranceRasterPixels: 1,
        glyphMidpointRaster: point, comparisonDrawGeneration: candidate.glyphView.drawGeneration,
        overlapX: forwardBox && glyphBox ? Math.min(forwardBox.right, glyphBox.right) - Math.max(forwardBox.left, glyphBox.left) : null,
        overlapY: forwardBox && glyphBox ? Math.min(forwardBox.bottom, glyphBox.bottom) - Math.max(forwardBox.top, glyphBox.top) : null };
      retain();
      assert.equal(candidate.forwardView.document, pdfHash); assert.equal(candidate.glyphView.document, pdfHash);
      assert.equal(candidate.forwardPage, candidate.forwardView.page, 'forward status agrees with the actual drawn page');
      assert.equal(candidate.forwardView.page, candidate.glyphView.page, 'forward match is on the independently searched marker page');
      assert.equal(candidate.glyphView.sourceMapStatus, candidate.forward, 'search did not replace the source-map candidate');
      assert.equal(candidate.glyphView.drawGeneration, candidate.glyphView.generation);
      assert.equal(candidate.glyphView.canvasPdfHash, pdfHash);
      assert.equal(candidate.glyphView.canvasPage + 1, candidate.glyphView.page);
      assert.equal(candidate.glyphView.glyphTitle, 'available native display glyph boxes');
      assert.ok(forwardBox && glyphBox, 'actual source and native display glyph rectangles are available');
      assert.ok(candidate.glyphView.sourceBoxCss.width > 0 && candidate.glyphView.sourceBoxCss.height > 0);
      assert.ok(candidate.glyphView.canvas.rasterWidth > 0 && candidate.glyphView.canvas.rasterHeight > 0);
      assert.deepEqual(candidate.forwardView.canvas, candidate.glyphView.canvas, 'search retains the actual raster dimensions and CSS scale');
      assert.ok(candidate.spatial.overlapX >= -1 && candidate.spatial.overlapY >= -1, 'forward box overlaps actual marker glyph within one raster pixel');
      assert.ok(point.x >= forwardBox.left - 1 && point.x <= forwardBox.right + 1
        && point.y >= forwardBox.top - 1 && point.y <= forwardBox.bottom + 1,
      'independent glyph midpoint is inside the forward highlight within one raster pixel');
      const glyph = candidate.glyphView.glyphBoxCss;
      candidate.click = { x: glyph.left + glyph.width / 2, y: glyph.top + glyph.height / 2 };
      candidate.stage = 'inverse'; retain();
      const other = file === mainPath ? 'chapter.tex' : mainPath;
      await page.locator('[data-testid="select-file"]').filter({ hasText: other }).first().click();
      await page.waitForFunction(id => document.querySelector('[data-testid=editor]')?.dataset.fileId === id,
        before.files.find(f => f.path === other).id);
      candidate.inverseView = await packageView(page); retain();
      assert.equal(candidate.inverseView.generation, candidate.glyphView.generation, 'glyph click remains bound to the drawn PDF generation');
      assert.equal(candidate.inverseView.document, pdfHash);
      assert.equal(candidate.inverseView.drawGeneration, candidate.glyphView.drawGeneration);
      assert.equal(candidate.inverseView.canvasPdfHash, pdfHash);
      assert.equal(candidate.inverseView.canvasPage, candidate.glyphView.canvasPage);
      assert.equal(candidate.inverseView.preview, 'Available'); assert.equal(candidate.inverseView.drawn, 'Drawn');
      assert.deepEqual(candidate.inverseView.canvas, candidate.glyphView.canvas);
      await H.$(page, 'pdf-canvas').dblclick({ position: candidate.click });
      await page.waitForFunction(file => {
        const text = document.querySelector('[data-testid=source-map-status]')?.textContent || '';
        return text.startsWith('Selected ' + file + ':') && text.includes('native line-level precision');
      }, file, { timeout: 45000 });
      candidate.inverse = await H.$(page, 'source-map-status').textContent();
      const line = Number(candidate.inverse.slice(('Selected ' + file + ':').length).match(/^\d+/)?.[0]);
      const selected = await editor.evaluate(el => ({ file: el.dataset.fileId, value: el.value, start: el.selectionStart, end: el.selectionEnd }));
      candidate.nativeLine = line; candidate.expectedMarkerLine = expectedLine; candidate.markerLineTolerance = 1;
      candidate.selected = { file: selected.file, start: selected.start, end: selected.end,
        text: selected.value.slice(selected.start, selected.end), utf16Units: selected.value.length };
      retain();
      assert.ok(line > 0);
      // Keep the strict package marker neighbourhood independently of both boxes.
      assert.ok(Math.abs(line - expectedLine) <= 1, `inverse ${file}:${line} near fixture marker line ${expectedLine}`);
      assert.equal(selected.file, expectedId); assert.equal(selected.value, source);
      const lines = source.split('\n'); assert.ok(line <= lines.length);
      const start = lines.slice(0, line - 1).reduce((n, value) => n + value.length + 1, 0);
      assert.equal(selected.start, start); assert.equal(selected.end, start + lines[line - 1].length);
      assert.equal(await H.$(page, 'compile-panel').getAttribute('data-output-hash'), pdfHash);
      assert.deepEqual(await H.head(page, project), before, 'navigation/search do not edit or save any project file');
      candidate.stage = 'PASS'; retain();
    }
    await page.locator('[data-testid="select-file"]').filter({ hasText: mainPath }).first().click();
    evidence.unchangedProject = true; evidence.result = 'PASS'; retain();
    return evidence;
  } catch (error) {
    if (candidate) {
      candidate.failureView = await packageView(page).catch(() => null);
      candidate.failureSelection = await H.$(page, 'editor').evaluate(el => ({ file: el.dataset.fileId,
        start: el.selectionStart, end: el.selectionEnd, text: el.value.slice(el.selectionStart, el.selectionEnd),
        utf16Units: el.value.length })).catch(() => null);
    }
    evidence.result = 'FAIL'; evidence.failure = String(error.message).slice(0, 1024); retain();
    throw error;
  }
}

async function verify(page, project, mainPath, fixture, label) {
  if (['packages', 'biblatex'].includes(fixture)) return verifyPackage(page, project, mainPath, fixture, label);
  const before = await H.head(page, project);
  const pdfHash = await H.$(page, 'compile-panel').getAttribute('data-output-hash');
  const records = [];
  const pairs = navigationPairs(mainPath, fixture);
  for (const [file, needle] of pairs) {
    await page.locator('[data-testid="select-file"]').filter({ hasText: file }).first().click();
    const expectedId = before.files.find(f => f.path === file).id;
    await page.waitForFunction(({ id, needle }) => {
      const el = document.querySelector('[data-testid=editor]');
      return el?.dataset.fileId === id && el.value.includes(needle);
    }, { id: expectedId, needle });
    const editor = H.$(page, 'editor'); const source = await editor.inputValue();
    const caret = source.indexOf(needle); assert(caret >= 0, 'known actual source marker');
    await editor.evaluate((element, at) => { element.focus(); element.setSelectionRange(at, at); }, caret);
    await H.$(page, 'source-go-to-pdf').click();
    await page.waitForFunction(() => {
      const text = document.querySelector('[data-testid=source-map-status]')?.textContent || '';
      return text.length > 0 && !text.startsWith('Locating');
    }, null, { timeout: 45000 });
    const forward = await H.$(page, 'source-map-status').textContent();
    assert.match(forward, /^PDF page /, 'actual forward navigation status: ' + forward);
    assert(forward.includes(file + ':'), 'forward match identifies the actual captured file');
    await page.waitForFunction(() => document.querySelector('[data-testid=pdf-pane]')?.querySelector('[data-testid=preview-status]')?.dataset.previewStatus === 'Available'
      && document.querySelector('[data-testid=canvas-status]')?.textContent === 'Drawn'
      && document.querySelector('[data-testid=source-pdf-highlight]'), null, { timeout: 45000 });
    const highlight = await H.$(page, 'source-pdf-highlight').boundingBox();
    const canvas = H.$(page, 'pdf-canvas'); const pixels = await canvas.boundingBox();
    assert(highlight && pixels && highlight.width > 0 && highlight.height > 0);
    const click = { x: highlight.x + highlight.width / 2 - pixels.x, y: highlight.y + highlight.height / 2 - pixels.y };
    // Cross-file inverse navigation must actually select the captured other file.
    if (pairs.length > 1) {
      const other = file === mainPath ? 'chapter.tex' : mainPath;
      await page.locator('[data-testid="select-file"]').filter({ hasText: other }).first().click();
      await page.waitForFunction(id => document.querySelector('[data-testid=editor]')?.dataset.fileId === id,
        before.files.find(f => f.path === other).id);
    }
    await canvas.dblclick({ position: click });
    await page.waitForFunction(file => {
      const text = document.querySelector('[data-testid=source-map-status]')?.textContent || '';
      return text.startsWith('Selected ' + file + ':') && text.includes('native line-level precision');
    }, file, { timeout: 45000 });
    const inverse = await H.$(page, 'source-map-status').textContent();
    const line = Number(inverse.slice(('Selected ' + file + ':').length).match(/^\d+/)?.[0]); assert(line > 0);
    const expectedLine = source.slice(0, caret).split('\n').length;
    // A source line may share a TeX paragraph with the adjacent fixture line.
    // Bound the new package cases to that neighbourhood, independently of the hit.
    if (['packages', 'biblatex'].includes(fixture))
      assert.ok(Math.abs(line - expectedLine) <= 1, `inverse ${file}:${line} near fixture marker line ${expectedLine}`);
    const selected = await editor.evaluate(el => ({ file: el.dataset.fileId, value: el.value, start: el.selectionStart, end: el.selectionEnd }));
    assert.equal(selected.file, expectedId); assert.equal(selected.value, source);
    const lines = source.split('\n'); assert(line <= lines.length);
    const start = lines.slice(0, line - 1).reduce((n, value) => n + value.length + 1, 0);
    assert.equal(selected.start, start); assert.equal(selected.end, start + lines[line - 1].length);
    assert.equal(await H.$(page, 'compile-panel').getAttribute('data-output-hash'), pdfHash);
    assert.deepEqual(await H.head(page, project), before, 'navigation does not edit or save any project file');
    records.push({ file, forward, inverse, nativeLine: line, expectedMarkerLine: expectedLine,
      markerLineTolerance: ['packages', 'biblatex'].includes(fixture) ? 1 : null,
      selectedUtf16: [selected.start, selected.end], click });
  }
  await page.locator('[data-testid="select-file"]').filter({ hasText: mainPath }).first().click();
  return { label, pdfHash, records, unchangedProject: true, actualBrowserActions: true };
}
module.exports = { verify, navigationPairs, assertPackageText };
