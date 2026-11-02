// Real published-app checks. Only user actions and DOM/storage observations;
// no fabricated map, native reply, provider, renderer or application authority.
const assert = require('node:assert/strict');
const H = require('./review-ui-helpers.cjs');

async function verify(page, project, mainPath, fixture, label) {
  const before = await H.head(page, project);
  const pdfHash = await H.$(page, 'compile-panel').getAttribute('data-output-hash');
  const records = [];
  const pairs = fixture === 'portable'
    ? [[mainPath, 'Portable compiler fixture.'], ['chapter.tex', 'Included file, reference']]
    : fixture
    ? [[mainPath, 'ASCII context: Kyiv 123.'], ['chapter.tex', 'Chapter cites article']]
    : [[mainPath, 'Modern CSharp pipeline']];
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
    const selected = await editor.evaluate(el => ({ file: el.dataset.fileId, value: el.value, start: el.selectionStart, end: el.selectionEnd }));
    assert.equal(selected.file, expectedId); assert.equal(selected.value, source);
    const lines = source.split('\n'); assert(line <= lines.length);
    const start = lines.slice(0, line - 1).reduce((n, value) => n + value.length + 1, 0);
    assert.equal(selected.start, start); assert.equal(selected.end, start + lines[line - 1].length);
    assert.equal(await H.$(page, 'compile-panel').getAttribute('data-output-hash'), pdfHash);
    assert.deepEqual(await H.head(page, project), before, 'navigation does not edit or save any project file');
    records.push({ file, forward, inverse, nativeLine: line, selectedUtf16: [selected.start, selected.end], click });
  }
  await page.locator('[data-testid="select-file"]').filter({ hasText: mainPath }).first().click();
  return { label, pdfHash, records, unchangedProject: true, actualBrowserActions: true };
}
module.exports = { verify };
