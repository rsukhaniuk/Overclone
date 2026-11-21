// Actual registered P11 UI acceptance for one supplied make-project.py --view
// project. Test tooling only: no product hooks, fake native data, storage writes,
// source rewriting, direct compiler calls or source-derived geometry matrices.
// Required: ORIGIN_ENGINE=latex-dvi|xelatex|lualatex, ORIGIN_PROJECT_DIR,
// RESULT_DIR (new empty directory), PUBLISHED_WWWROOT, EXPECTED_SDK_VERSION,
// CANDIDATE_SOURCE_DIGEST (master's exact source/publication binding).
// Optional PREFLIGHT_ONLY=1 verifies frozen inputs/assets only, then exits before
// static-origin/browser creation; its result is explicitly not browser acceptance.
// Root generates the input and owns actual browser execution. No manual runtime
// provisioning is supported here: the exact automatically bundled P11 is required.
'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs'), path = require('node:path'), http = require('node:http');
const { createHash } = require('node:crypto');
const H = require('./review-ui-helpers.cjs');
const { buildZip } = require('./zip-util.cjs');
const { validate: validateCsp } = require('./test-csp.cjs');
const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const required = key => { assert.ok(process.env[key], 'Required environment: ' + key); return process.env[key]; };
const MAIN = 'тези/my thesis.tex', INCLUDED = 'глави/chapter one.tex';
const ENGINE = required('ORIGIN_ENGINE');
const PHASE = { 'latex-dvi': 18, xelatex: 19, lualatex: 20 }[ENGINE];
assert.ok(PHASE, 'ORIGIN_ENGINE must be latex-dvi, xelatex or lualatex');
const ROOT = fs.realpathSync(required('PUBLISHED_WWWROOT'));
const INPUT = fs.realpathSync(required('ORIGIN_PROJECT_DIR'));
const OUT = fs.realpathSync(required('RESULT_DIR'));
assert.equal(fs.readdirSync(OUT).length, 0, 'new empty exclusive RESULT_DIR required');
const SDK = required('EXPECTED_SDK_VERSION'), SOURCE = required('CANDIDATE_SOURCE_DIGEST');
assert.match(SOURCE, /^[0-9a-f]{64}$/, 'exact master source digest');
const P11 = { profile: 11, resources: 3709,
  closure: '573837b00b77dd477c4b4d4ba35384300f6c334c5f8e1cc5be23aeae8ce4a73c',
  zipLength: 193570746, zipSha256: 'd72b5ad4ac7a5ced045fa0d4027730691f3dc46722a8a2fb6eab49199d425989',
  phases: { pdftex: 6, 'latex-dvi': 18, xelatex: 19, lualatex: 20 },
  requiredNativeTuple: { dvi: { base: 2, graphics: 1, origin: 1 }, xe: { base: 3, graphics: 1, origin: 1 },
    lua: { base: 5, graphics: 1, origin: 1 } } };
const receipt = path.join(OUT, 'native-origin-receipt.json');
fs.writeFileSync(receipt, '', { flag: 'wx', mode: 0o600 });
const record = { schemaVersion: 1, result: 'FAIL', step: 'preflight', engine: ENGINE,
  sourceBinding: { sdk: SDK, sourceDigest: SOURCE, basis: 'master-supplied build/source binding; SDK assets independently verified below' },
  runtime: { node: process.version }, publication: null, inputs: null, online: {}, coldOffline: {},
  workers: [], pageErrors: [], externalRequests: [], cspViolations: [], downloads: [], failure: null,
  limitations: ['Synthetic original four-page origin fixture only; no general engine/package compatibility claim.',
    'Root independently verifies actual downloaded PDF MediaBox/CropBox/Rotate and native provenance with the accepted artifact oracle.',
    'UI glyph/raster navigation is not physical compliance, glyph ink, UserUnit or inherited-page-attribute proof.',
    'Repeated included source has no caller-selected forward occurrence in native query ABI1; its actual chosen occurrence is checked once.',
    'Fresh offline compilation uses unchanged source and a new real request/native execution; PDF bytes need not differ.'] };
let context, browser, page, server, origin, csp, completed = false;
const sockets = new Set();
const flush = () => fs.writeFileSync(receipt, JSON.stringify(record, null, 2) + '\n');
const step = text => { record.step = text; flush(); };
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
const deadline = setTimeout(() => {
  record.failure = { message: 'bounded harness deadline 600000ms', step: record.step }; flush();
  const force = setTimeout(() => process.exit(124), 5000);
  Promise.allSettled([context?.close(), stopServer()]).finally(() => { clearTimeout(force); process.exit(124); });
}, 600000);

function localFile(root, relative) {
  assert.ok(relative && !relative.startsWith('/') && !relative.includes('\\')
    && !relative.split('/').some(p => !p || p === '.' || p === '..'));
  const file = fs.realpathSync(path.resolve(root, relative));
  assert.ok(file.startsWith(root + path.sep) && fs.statSync(file).isFile(), 'bounded regular input');
  return file;
}

function publication() {
  const headers = fs.readFileSync(path.join(__dirname, '../docs/examples/static-csp-headers.conf'));
  validateCsp(fs.readFileSync(path.join(ROOT, 'index.html'), 'utf8'), headers.toString());
  csp = headers.toString().split('\n').map(s => s.trim()).find(s => s.startsWith('Content-Security-Policy:'))
    .slice('Content-Security-Policy:'.length).trim();
  const raw = fs.readFileSync(path.join(ROOT, 'service-worker-assets.js'));
  const manifest = JSON.parse(raw.toString().match(/self\.assetsManifest\s*=\s*(\{[\s\S]*\})\s*;/)[1]);
  record.publication = { sdk: manifest.version, manifestSha256: sha(raw), cspSha256: sha(headers), assetsVerified: 0 }; flush();
  assert.equal(manifest.version, SDK, 'actual immutable SDK identity');
  const seen = new Set();
  for (const asset of manifest.assets) {
    assert.ok(!seen.has(asset.url)); seen.add(asset.url);
    const bytes = fs.readFileSync(localFile(ROOT, asset.url));
    assert.equal('sha256-' + createHash('sha256').update(bytes).digest('base64'), asset.hash, asset.url);
    record.publication.assetsVerified++;
  }
  const zip = fs.readFileSync(localFile(ROOT, 'bundled-runtime/compiler-suite.zip'));
  record.publication.compilerZip = { length: zip.length, sha256: sha(zip) }; flush();
  assert.equal(zip.length, P11.zipLength, 'exact P11 application ZIP required; P10 is not an origin pass');
  assert.equal(sha(zip), P11.zipSha256);
  // The hash-bound existing application envelope is STORED, pins.json first.
  // Read that one bounded member; this is not a general ZIP import implementation.
  assert.equal(zip.readUInt32LE(0), 0x04034b50); assert.equal(zip.readUInt16LE(6), 0);
  assert.equal(zip.readUInt16LE(8), 0);
  const size = zip.readUInt32LE(22), nameLength = zip.readUInt16LE(26), extra = zip.readUInt16LE(28);
  assert.equal(zip.readUInt32LE(18), size); assert.ok(size > 0 && size <= 2 * 1024 * 1024);
  assert.equal(zip.subarray(30, 30 + nameLength).toString(), 'pins.json');
  const pinsRaw = zip.subarray(30 + nameLength + extra, 30 + nameLength + extra + size);
  assert.equal(pinsRaw.length, size);
  const pins = JSON.parse(pinsRaw); assert.equal(pins.length, P11.resources);
  const names = pins.map(p => p.name); assert.deepEqual(names, [...new Set(names)].sort());
  for (const pin of pins) {
    assert.match(pin.name, /^(?=.{1,200}$)[A-Za-z0-9._-]+(?:\/[A-Za-z0-9._-]+)*$/);
    assert.ok(!pin.name.split('/').some(p => p === '.' || p === '..'));
    assert.ok(Number.isSafeInteger(pin.length) && pin.length >= 0); assert.match(pin.sha256, /^[0-9a-f]{64}$/);
  }
  const closure = sha(Buffer.from(pins.map(p => `${p.name}\t${p.length}\t${p.sha256}`).join('\n')));
  record.publication.runtime = { ...P11, actualClosure: closure, pinsSha256: sha(pinsRaw),
    expandedBytes: pins.reduce((n, p) => n + p.length, 0),
    capabilityEvidence: 'exact reviewed bytes and guarded actual consumer phase traces, not direct ABI calls by this harness' }; flush();
  assert.equal(closure, P11.closure);
  record.publication.rendererAssets = manifest.assets.filter(a => /^bundled-runtime\/e1\.(?:js|wasm)$/.test(a.url));
  assert.equal(record.publication.rendererAssets.length, 2);
  fs.writeFileSync(path.join(OUT, 'verified-assets-manifest.json'), JSON.stringify(manifest, null, 2) + '\n');
}

function projectInput() {
  const expected = ['figure.png', 'figure.xbb', INCLUDED, MAIN].sort(), found = [];
  function visit(directory, prefix = '') {
    for (const item of fs.readdirSync(directory, { withFileTypes: true })) {
      assert.ok(!item.isSymbolicLink(), 'original fixture has no symlinks');
      const name = prefix + item.name;
      if (item.isDirectory()) visit(path.join(directory, item.name), name + '/');
      else { assert.ok(item.isFile()); found.push(name); }
    }
  }
  visit(INPUT); assert.deepEqual(found.sort(), expected, 'original finite four-file project only');
  const entries = expected.map(name => [name, fs.readFileSync(localFile(INPUT, name))]);
  const source = entries.find(([name]) => name === MAIN)[1].toString('utf8');
  const included = entries.find(([name]) => name === INCLUDED)[1].toString('utf8');
  const lines = source.split('\n'), marker = '\\hbox{\\kern40bp ROOTBEFORE PAGEWORD}\\kern10bp';
  const rootLines = lines.flatMap((text, index) => text === marker ? [index + 1] : []);
  record.inputs = { files: entries.map(([name, bytes]) => ({ name, length: bytes.length, sha256: sha(bytes) })),
    rootAnchors: rootLines.map((line, index) => ({ page: index + 1, file: MAIN, line, word: 'ROOTBEFORE' })),
    includedAnchor: { file: INCLUDED, line: 1, word: 'INCLUDEMARK', inversePages: [1, 2, 3, 4] },
    geometryExpectation: { basis: 'supplied make-project.py --view input; NOT observed PDF geometry',
      mediaBoxes: [[12, 18, 612, 818], [-24, -30, 576, 770], [-18, 24, 582, 824], [30, -18, 630, 782]], rotations: [0, 90, 180, 270] } }; flush();
  assert.equal(rootLines.length, 4); assert.equal(source.split('\\shipout\\vbox to 740bp{%').length - 1, 4);
  assert.equal(included, '\\hbox{\\kern40bp INCLUDEMARK X}\\kern12bp\n');
  assert.ok(!source.includes('\\mag=') && !source.includes('\\directlua'));
  assert.equal(source.includes('\\usepackage{fontspec}'), ENGINE !== 'latex-dvi');
  assert.equal(source.includes('\\pdfvariable pageattr{#1}'), ENGINE === 'lualatex');
  for (const attrs of [
    '/MediaBox [12 18 612 818] /CropBox [17.0 23.0 607.0 813.0] /Rotate 0',
    '/MediaBox [-24 -30 576 770] /CropBox [-19.0 -25.0 571.0 765.0] /Rotate 90',
    '/MediaBox [-18 24 582 824] /CropBox [-13.0 29.0 577.0 819.0] /Rotate 180',
    '/MediaBox [30 -18 630 782] /CropBox [35.0 -13.0 625.0 777.0] /Rotate 270',
  ]) assert.equal(source.split('\\OriginMedia{' + attrs + '}').length - 1, 1, 'original --view attributes');
  assert.deepEqual(entries.find(([name]) => name === 'figure.png')[1], fs.readFileSync(path.join(__dirname, '../src/Client/wwwroot/icons/icon-192.png')));
  assert.deepEqual(entries.find(([name]) => name === 'figure.xbb')[1], fs.readFileSync(path.join(__dirname, '../tests/CompilerSuiteFixtures/figure.xbb')));
  record.inputs.closureSha256 = sha(Buffer.from(record.inputs.files.map(f => `${f.name}\t${f.length}\t${f.sha256}`).join('\n')));
  record.tooling = Object.fromEntries(['test-native-origin-ui.cjs', 'review-ui-helpers.cjs', 'zip-util.cjs', 'test-csp.cjs']
    .map(name => [name, sha(fs.readFileSync(path.join(__dirname, name)))]));
  record.tooling.generator = sha(fs.readFileSync(path.join(__dirname, '../native/tex/page-origin-navigation/make-project.py')));
  record.tooling.nativePdfOracle = sha(fs.readFileSync(path.join(__dirname, '../native/tex/page-origin-navigation/pdf-query-oracle.py')));
  fs.writeFileSync(path.join(OUT, 'original-project.zip'), buildZip(entries)); flush();
  return { entries, source, included, rootLines };
}

async function startServer() {
  const types = { '.html': 'text/html', '.js': 'text/javascript', '.json': 'application/json',
    '.css': 'text/css', '.wasm': 'application/wasm', '.svg': 'image/svg+xml', '.png': 'image/png',
    '.webmanifest': 'application/manifest+json', '.woff': 'font/woff', '.woff2': 'font/woff2', '.ico': 'image/x-icon' };
  server = http.createServer((req, res) => {
    let file;
    try {
      const pathname = decodeURIComponent(new URL(req.url, 'http://127.0.0.1').pathname);
      file = localFile(ROOT, pathname === '/' ? 'index.html' : pathname.slice(1));
    } catch { res.writeHead(404).end(); return; }
    fs.readFile(file, (error, bytes) => {
      if (error) { res.writeHead(404).end(); return; }
      res.writeHead(200, { 'Content-Type': types[path.extname(file)] || 'application/octet-stream',
        'Content-Security-Policy': csp, 'X-Content-Type-Options': 'nosniff', 'Cache-Control': 'no-cache' }); res.end(bytes);
    });
  });
  server.on('connection', socket => { sockets.add(socket); socket.on('close', () => sockets.delete(socket)); });
  await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
  origin = 'http://127.0.0.1:' + server.address().port; record.publication.origin = origin; flush();
}
async function stopServer() {
  if (!server) return;
  const owned = server; server = null;
  for (const socket of sockets) socket.destroy();
  await new Promise((resolve, reject) => owned.close(error => error ? reject(error) : resolve()));
}
function browserCensus() {
  const found = [];
  for (const entry of fs.readdirSync('/proc')) {
    if (!/^\d+$/.test(entry)) continue;
    try {
      const executable = path.basename(fs.readlinkSync('/proc/' + entry + '/exe'));
      if (/chromium|chrome|headless_shell/i.test(executable)) found.push({ pid: Number(entry), executable,
        startTicks: fs.readFileSync('/proc/' + entry + '/stat', 'utf8').split(') ')[1].split(' ')[19] });
    } catch { /* Process exit during a read is an observation, not a kill. */ }
  }
  return found;
}
async function openBrowser(offline) {
  const { chromium } = require('playwright');
  record.runtime.playwright = require('playwright/package.json').version;
  assert.equal(record.runtime.playwright, '1.56.1', 'use the accepted cached browser toolchain');
  context = await chromium.launchPersistentContext(path.join(OUT, 'profile'), { headless: true, offline, acceptDownloads: true });
  browser = context.browser(); record.runtime.browser = browser?.version();
  await context.addInitScript(() => {
    window.__originCsp = [];
    addEventListener('securitypolicyviolation', event => window.__originCsp.push(event.effectiveDirective));
  });
  const external = raw => /^https?:/.test(raw) && new URL(raw).origin !== origin;
  context.on('request', request => { if (external(request.url())) record.externalRequests.push(new URL(request.url()).origin); });
  await context.route('**/*', route => external(route.request().url()) ? route.abort('blockedbyclient') : route.continue());
  context.on('page', item => {
    item.setDefaultTimeout(30000);
    item.on('pageerror', error => record.pageErrors.push(String(error).slice(0, 2000)));
    item.on('worker', worker => record.workers.push({ step: record.step, path: new URL(worker.url(), origin).pathname }));
  });
  page = await context.newPage(); step(offline ? 'cold offline BEFORE navigation' : 'online navigation');
  await page.goto(origin + '/'); await H.ready(page);
}
async function closeBrowser() {
  const pages = context.pages(), old = browser;
  await context.close(); context = null;
  if (old?.isConnected()) await old.close(); browser = null;
  assert.ok(pages.every(p => p.isClosed()) && (!old || !old.isConnected()));
  let census;
  for (let n = 0; n < 40; n++) { census = browserCensus(); if (!census.length) break; await pause(100); }
  record.restart = { tabsClosed: pages.length, browserDisconnected: true, censusBeforeRelaunch: census }; flush();
  assert.deepEqual(census, [], 'all browser processes must close before the offline relaunch');
}

function exactHead(head, fixture) {
  assert.equal(head.files.length, fixture.entries.length);
  for (const [name, bytes] of fixture.entries)
    assert.deepEqual(Buffer.from(head.files.find(f => f.path === name)?.bytes || []), bytes, 'exact saved original input: ' + name);
}
async function choose(file, fixture, head) {
  const saved = head.files.find(f => f.path === file); assert.ok(saved);
  const expected = fixture.entries.find(([name]) => name === file)[1].toString('utf8');
  const target = H.$(page, 'select-file').filter({ has: page.locator('.file-name', { hasText: file }) });
  assert.equal(await target.count(), 1); await target.click();
  await page.waitForFunction(({ id, text }) => {
    const editor = document.querySelector('[data-testid=editor]');
    return editor?.dataset.fileId === id && editor.value === text;
  }, { id: saved.id, text: expected });
  return expected;
}
async function readyProject(name) {
  await page.waitForFunction(expected => document.querySelector('.workspace-header h1')?.textContent === expected
    && !!document.querySelector('[data-testid=analysis-panel]')?.dataset.session
    && document.querySelectorAll('[data-testid=select-file]').length === 4, name);
  assert.equal(await H.$(page, 'recovery-panel').count(), 0);
  await page.waitForFunction(() => navigator.serviceWorker.controller !== null && window.__oopOfflineReady === true,
    null, { timeout: 240000 });
  await page.waitForFunction(() => document.querySelector('[data-testid=compiler-readiness]')?.textContent
    .startsWith('Local compiler checked'), null, { timeout: 240000 });
  assert.equal(await H.$(page, 'runtime-zip-input').count(), 0, 'automatic bundled runtime only');
  assert.equal(await H.$(page, 'pdf-runtime-manifest').count(), 0);
  await H.reveal(page, H.$(page, 'bundled-runtime-info'));
}
async function importProject(name, fixture) {
  await H.reveal(page, H.$(page, 'import-zip'));
  await H.$(page, 'import-zip').setInputFiles({ name: 'original-origin-project.zip', mimeType: 'application/zip', buffer: buildZip(fixture.entries) });
  await H.reveal(page, H.$(page, 'import-preview')); await H.$(page, 'staged-name').fill(name);
  await H.$(page, 'publish-import').click(); await readyProject(name);
}
async function bridge() {
  return page.evaluate(async () => (await import('./js/compile-port-bridge.js')).observations());
}
async function frame() {
  return H.$(page, 'pdf-pane').evaluate(async pane => {
    const canvas = pane.querySelector('[data-testid=pdf-canvas]'), rect = canvas?.getBoundingClientRect();
    const box = element => { const b = element?.getBoundingClientRect(); return b && rect
      ? { left: b.left - rect.left, top: b.top - rect.top, width: b.width, height: b.height } : null; };
    const glyph = Array.from(pane.querySelectorAll('[data-testid=glyph-highlight]')).find(element => {
      const b = element.getBoundingClientRect(); return b.width > 0 && b.height > 0;
    });
    let pixels = null, pixelBytes = null;
    if (canvas?.width && canvas?.height && canvas.width * canvas.height <= 4000000) {
      const bytes = canvas.getContext('2d').getImageData(0, 0, canvas.width, canvas.height).data;
      let nonwhite = 0; for (let i = 0; i < bytes.length; i += 4) if (bytes[i] < 250 || bytes[i + 1] < 250 || bytes[i + 2] < 250) nonwhite++;
      pixelBytes = bytes; pixels = { nonwhite, sha256Rgba: null };
    }
    const search = pane.querySelector('[data-testid=search-status]');
    const observed = { pdfHash: pane.dataset.document, generation: pane.dataset.generation,
      page: Number(pane.querySelector('[data-testid=page-label]')?.textContent.match(/Page (\d+) of/)?.[1] || 0),
      pageCount: Number(pane.querySelector('[data-testid=page-label]')?.textContent.match(/of (\d+)/)?.[1] || 0),
      status: pane.querySelector('[data-testid=preview-status]')?.dataset.previewStatus,
      drawStatus: pane.querySelector('[data-testid=canvas-status]')?.textContent,
      canvas: rect ? { cssWidth: rect.width, cssHeight: rect.height, width: canvas.width, height: canvas.height,
        pdfHash: canvas.dataset.pdfHash, page: Number(canvas.dataset.pdfPage), generation: canvas.dataset.drawGeneration, pixels } : null,
      sourceBox: box(pane.querySelector('[data-testid=source-pdf-highlight]')), glyphBox: box(glyph), glyphTitle: glyph?.title || null,
      search: { status: search?.dataset.searchStatus, count: Number(search?.dataset.matchCount || 0),
        scanned: Number(search?.dataset.scannedPages || 0), total: Number(search?.dataset.totalPages || 0),
        selected: pane.querySelector('[data-testid=selected-result]')?.textContent },
      mapStatus: document.querySelector('[data-testid=source-map-status]')?.textContent || '' };
    // Capture all DOM/frame fields synchronously with getImageData, before the
    // digest await can permit a later render to replace that canvas generation.
    if (pixelBytes) pixels.sha256Rgba = Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', pixelBytes)))
      .map(n => n.toString(16).padStart(2, '0')).join('');
    return observed;
  });
}
async function drawn(expectedPage = null) {
  await page.waitForFunction(expected => {
    const pane = document.querySelector('[data-testid=pdf-pane]'), canvas = pane?.querySelector('[data-testid=pdf-canvas]');
    const currentPage = Number(pane?.querySelector('[data-testid=page-label]')?.textContent.match(/Page (\d+) of/)?.[1] || 0);
    return pane?.querySelector('[data-testid=preview-status]')?.dataset.previewStatus === 'Available'
      && pane?.querySelector('[data-testid=canvas-status]')?.textContent === 'Drawn'
      && canvas?.dataset.drawGeneration === pane?.dataset.generation && canvas?.dataset.pdfHash === pane?.dataset.document
      && Number(canvas?.dataset.pdfPage) + 1 === currentPage && (expected === null || currentPage === expected);
  }, expectedPage, { timeout: 45000 });
}
function exactFrame(view, hash, expectedPage) {
  assert.equal(view.pdfHash, hash); assert.equal(view.pageCount, 4); assert.equal(view.status, 'Available');
  assert.equal(view.drawStatus, 'Drawn'); assert.equal(view.canvas.pdfHash, hash);
  assert.equal(view.canvas.generation, view.generation); assert.equal(view.canvas.page + 1, view.page);
  if (expectedPage !== null) assert.equal(view.page, expectedPage);
  assert.ok(view.canvas.width > 0 && view.canvas.height > 0 && view.canvas.cssWidth > 0 && view.canvas.cssHeight > 0);
  assert.ok(view.canvas.pixels.nonwhite > 0);
}
async function searchPoint(word, wantedPage, attempt, hash) {
  step('native PDF search ' + word + ' on page ' + wantedPage);
  const pane = H.$(page, 'pdf-pane');
  await pane.locator('input[aria-label="Search PDF"]').fill(word);
  attempt.searchWord = await pane.locator('input[aria-label="Search PDF"]').inputValue(); flush();
  assert.equal(attempt.searchWord, word);
  const old = await pane.getAttribute('data-generation');
  await pane.getByRole('button', { name: 'Search document', exact: true }).click();
  await page.waitForFunction(previous => {
    const p = document.querySelector('[data-testid=pdf-pane]');
    return p?.dataset.generation !== previous && ['Complete', 'Partial', 'Empty', 'Error', 'Cancelled']
      .includes(p?.querySelector('[data-testid=search-status]')?.dataset.searchStatus);
  }, old, { timeout: 45000 });
  await drawn(); attempt.search = await frame(); flush();
  assert.equal(attempt.search.search.status, 'Complete'); assert.equal(attempt.search.search.count, 4);
  assert.equal(attempt.search.search.scanned, 4); assert.equal(attempt.search.search.total, 4);
  attempt.selectedResults = [];
  for (let index = 1; index <= wantedPage; index++) {
    const previous = await pane.getAttribute('data-generation');
    await pane.getByRole('button', { name: 'Next result', exact: true }).click();
    await page.waitForFunction(({ previous, index }) => {
      const p = document.querySelector('[data-testid=pdf-pane]');
      return p?.dataset.generation !== previous
        && p?.querySelector('[data-testid=selected-result]')?.textContent === `Result ${index} / 4`;
    }, { previous, index });
    await drawn(); const selected = await frame(); attempt.selectedResults.push(selected); flush();
    exactFrame(selected, hash, index);
  }
  const view = attempt.glyph = await frame(); flush(); exactFrame(view, hash, wantedPage);
  assert.equal(view.glyphTitle, 'available native display glyph boxes', 'complete native glyph geometry for the non-whitespace anchor');
  assert.ok(view.glyphBox && view.glyphBox.width > 0 && view.glyphBox.height > 0);
  attempt.point = { x: view.glyphBox.left + view.glyphBox.width / 2, y: view.glyphBox.top + view.glyphBox.height / 2,
    basis: 'first actual nonempty native PDF search glyph rectangle; not the SyncTeX box' }; flush();
  return view;
}
function spatial(attempt) {
  const view = attempt.glyph, point = attempt.point, box = view.sourceBox;
  const sx = view.canvas.width / view.canvas.cssWidth, sy = view.canvas.height / view.canvas.cssHeight;
  attempt.spatial = { sourceBoxCss: box, pointCss: point, cssPerRasterPixel: { x: 1 / sx, y: 1 / sy },
    pointRaster: { x: point.x * sx, y: point.y * sy }, toleranceRasterPixels: 1,
    sourceBoxRaster: box ? { left: box.left * sx, top: box.top * sy,
      right: (box.left + box.width) * sx, bottom: (box.top + box.height) * sy } : null }; flush();
  assert.ok(box && box.width > 0 && box.height > 0, 'real forward highlight on the independently located anchor page');
  const b = attempt.spatial.sourceBoxRaster, p = attempt.spatial.pointRaster;
  assert.ok(p.x >= b.left - 1 && p.x <= b.right + 1 && p.y >= b.top - 1 && p.y <= b.bottom + 1,
    'native PDF glyph point inside forward highlight within one actual raster pixel');
}
async function inverse(file, line, attempt, fixture, head, hash) {
  const other = file === MAIN ? INCLUDED : MAIN; await choose(other, fixture, head);
  attempt.beforeInverse = await frame(); flush();
  exactFrame(attempt.beforeInverse, hash, attempt.glyph.page);
  assert.equal(attempt.beforeInverse.generation, attempt.glyph.generation);
  assert.deepEqual(attempt.beforeInverse.canvas, attempt.glyph.canvas, 'click stays on the same drawn raster');
  step('inverse actual glyph to ' + file + ':' + line);
  await H.$(page, 'pdf-canvas').dblclick({ position: { x: attempt.point.x, y: attempt.point.y } });
  await page.waitForFunction(({ file, id }) => {
    const status = document.querySelector('[data-testid=source-map-status]')?.textContent || '';
    const editor = document.querySelector('[data-testid=editor]');
    return status.startsWith('Selected ' + file + ':') && editor?.dataset.fileId === id
      || status.startsWith('No current source location is available for this PDF position');
  }, { file, id: head.files.find(f => f.path === file).id }, { timeout: 45000 });
  attempt.inverseStatus = await H.$(page, 'source-map-status').textContent();
  attempt.selection = await H.$(page, 'editor').evaluate(e => ({ fileId: e.dataset.fileId,
    start: e.selectionStart, end: e.selectionEnd, text: e.value.slice(e.selectionStart, e.selectionEnd), value: e.value })); flush();
  assert.ok(attempt.inverseStatus.startsWith('Selected ' + file + ':' + line + ' ')
    || attempt.inverseStatus.startsWith('Selected ' + file + ':' + line + '('), 'exact independently expected inverse filename/line: ' + attempt.inverseStatus);
  assert.ok(attempt.inverseStatus.includes('native line-level precision'));
  const source = fixture.entries.find(([name]) => name === file)[1].toString('utf8'), lines = source.split('\n');
  const start = lines.slice(0, line - 1).reduce((n, value) => n + value.length + 1, 0);
  assert.equal(attempt.selection.fileId, head.files.find(f => f.path === file).id);
  assert.equal(attempt.selection.value, source); assert.equal(attempt.selection.start, start);
  assert.equal(attempt.selection.end, start + lines[line - 1].length);
  assert.equal(await H.$(page, 'compile-panel').getAttribute('data-output-hash'), hash);
  assert.deepEqual(await H.head(page, record.projectName), head, 'navigation preserves the complete exact saved project');
  attempt.result = 'PASS'; flush();
}
async function navigation(mode, fixture, head, hash) {
  mode.navigation = []; flush();
  for (const anchor of [...record.inputs.rootAnchors, { file: INCLUDED, line: 1, word: 'INCLUDEMARK', page: null }]) {
    const attempt = { kind: 'forward-and-inverse', expected: { ...anchor, toleranceLines: 0 }, result: 'RUNNING' };
    mode.navigation.push(attempt); flush();
    const source = await choose(anchor.file, fixture, head), lines = source.split('\n');
    const lineStart = lines.slice(0, anchor.line - 1).reduce((n, value) => n + value.length + 1, 0);
    const caret = lineStart + lines[anchor.line - 1].indexOf(anchor.word);
    attempt.source = { file: anchor.file, line: anchor.line, text: lines[anchor.line - 1], caretUtf16: caret }; flush();
    assert.ok(caret >= lineStart);
    await H.$(page, 'editor').evaluate((e, at) => { e.focus(); e.setSelectionRange(at, at); }, caret);
    step('forward original ' + anchor.file + ':' + anchor.line); await H.$(page, 'source-go-to-pdf').click();
    await page.waitForFunction(() => {
      const status = document.querySelector('[data-testid=source-map-status]')?.textContent || '';
      return status.startsWith('PDF page ') || ['No current source location is available.', 'Source or PDF changed.',
        'Editor text no longer matches', 'Source/PDF navigation could not finish.'].some(prefix => status.startsWith(prefix));
    }, null, { timeout: 45000 });
    attempt.forwardStatus = await H.$(page, 'source-map-status').textContent();
    attempt.forwardFrame = await frame(); flush();
    const pageNumber = Number(attempt.forwardStatus.match(/^PDF page (\d+),/)?.[1] || 0);
    assert.ok(pageNumber >= 1 && pageNumber <= 4 && attempt.forwardStatus.includes(anchor.file + ':'), 'supported actual forward result: ' + attempt.forwardStatus);
    if (anchor.page !== null) assert.equal(pageNumber, anchor.page, 'root source line has an independent page occurrence');
    await drawn(pageNumber); attempt.forwardFrame = await frame(); flush(); exactFrame(attempt.forwardFrame, hash, pageNumber);
    await searchPoint(anchor.word, pageNumber, attempt, hash);
    assert.equal(attempt.glyph.mapStatus, attempt.forwardStatus, 'search retains the same source-map candidate');
    spatial(attempt); await inverse(anchor.file, anchor.line, attempt, fixture, head, hash);
  }
  for (let index = 1; index <= 4; index++) {
    const attempt = { kind: 'included-file-inverse', expected: { file: INCLUDED, line: 1, word: 'INCLUDEMARK', page: index, toleranceLines: 0 }, result: 'RUNNING' };
    mode.navigation.push(attempt); flush();
    await searchPoint('INCLUDEMARK', index, attempt, hash);
    await inverse(INCLUDED, 1, attempt, fixture, head, hash);
  }
}
async function download(mode, hash, head, name) {
  const before = await H.stamp(page); step(name + ' exact PDF download');
  await H.reveal(page, H.$(page, 'download-pdf'));
  const [result] = await Promise.all([page.waitForEvent('download'), H.$(page, 'download-pdf').click()]);
  const destination = path.join(OUT, name + '.pdf'); await result.saveAs(destination);
  const bytes = fs.readFileSync(destination), item = { file: path.basename(destination), length: bytes.length, sha256: sha(bytes),
    suggestedName: result.suggestedFilename(), failure: await result.failure() }; record.downloads.push(item); mode.download = item; flush();
  assert.equal(item.failure, null); assert.ok(bytes.subarray(0, 5).equals(Buffer.from('%PDF-'))); assert.equal(item.sha256, hash);
  assert.deepEqual(await H.head(page, record.projectName), head); assert.deepEqual(await H.stamp(page), before);
}
async function compile(mode, fixture, head, name) {
  await choose(MAIN, fixture, head);
  await page.locator('select[aria-label="Compile main file"]').selectOption({ label: MAIN });
  await page.locator('select[aria-label="Compile engine"]').selectOption(ENGINE);
  const bibliography = H.$(page, 'use-bibtex'); await bibliography.waitFor({ state: 'visible' });
  if (await bibliography.isChecked()) await bibliography.uncheck(); assert.equal(await bibliography.isChecked(), false);
  const previous = await bridge(); mode.phaseBaseline = { started: previous.started, settled: previous.settled }; flush();
  await page.waitForFunction(() => document.querySelector('[data-testid=compile]')?.disabled === false);
  step(name + ' fresh explicit compile'); await H.$(page, 'compile').click();
  await page.waitForFunction(() => /^(Compilation finished\.|No new PDF:)/.test(document.querySelector('[data-testid=compile-status]')?.textContent || ''),
    null, { timeout: 150000 });
  mode.status = await H.$(page, 'compile-status').textContent();
  mode.details = await H.$(page, 'compile-details').textContent(); mode.log = (await H.$(page, 'compiler-log').textContent()).slice(0, 65536);
  mode.nativePhases = await bridge(); flush();
  assert.ok(mode.status.startsWith('Compilation finished.'), mode.status);
  mode.request = mode.details.match(/Request: ([0-9a-f]{32});/)?.[1];
  mode.inputDigest = mode.details.match(/Input: ([0-9a-f]{64});/)?.[1];
  mode.pdfHash = await H.$(page, 'compile-panel').getAttribute('data-output-hash'); flush();
  assert.match(mode.request || '', /^[0-9a-f]{32}$/); assert.match(mode.pdfHash || '', /^[0-9a-f]{64}$/);
  assert.equal(await H.$(page, 'compile-panel').getAttribute('data-is-current'), 'true');
  const actual = mode.nativePhases.trace.filter(row => row.tag.request === mode.request);
  mode.executedPhases = actual; flush();
  assert.ok(actual.length > 0 && actual.length === mode.nativePhases.started - previous.started, 'all fresh actual phases are retained');
  assert.equal(mode.nativePhases.active, 0); assert.equal(mode.nativePhases.started, mode.nativePhases.settled);
  for (const row of actual) {
    assert.equal(row.tag.profile, 11); assert.equal(row.tag.version, PHASE); assert.equal(row.tag.engineId, ENGINE);
    assert.equal(row.tag.digest, mode.inputDigest); assert.equal(row.posted, true); assert.equal(row.entered, true);
    assert.equal(row.terminated, true); assert.equal(row.outcome, 'native-return'); assert.equal(row.nativeReturn, 0);
    assert.ok(['latex', 'dvipdfmx'].includes(row.tag.tool), 'no bibliography or fallback engine');
  }
  assert.equal(actual.filter(row => row.tag.tool === 'dvipdfmx').length, ENGINE === 'lualatex' ? 0 : 1);
  assert.ok(actual.some(row => row.tag.tool === 'latex'));
  await drawn(); mode.firstFrame = await frame(); flush(); exactFrame(mode.firstFrame, mode.pdfHash, null);
  assert.deepEqual(await H.head(page, record.projectName), head); exactHead(head, fixture);
  await download(mode, mode.pdfHash, head, name);
  await navigation(mode, fixture, head, mode.pdfHash);
  await page.screenshot({ path: path.join(OUT, name + '.png'), fullPage: true });
  record.cspViolations.push(...await page.evaluate(() => window.__originCsp || [])); flush();
}

(async () => {
  try {
    publication(); const fixture = projectInput();
    if (process.env.PREFLIGHT_ONLY === '1') {
      record.result = 'PREFLIGHT_ONLY'; record.step = 'frozen input/asset checks only; browser unrun'; flush(); return;
    }
    record.initialBrowserCensus = browserCensus(); flush(); assert.deepEqual(record.initialBrowserCensus, []);
    await startServer(); await openBrowser(false);
    record.projectName = 'Native origin ' + ENGINE + ' ' + Date.now(); flush();
    step('import original finite project through registered UI'); await importProject(record.projectName, fixture);
    const head = await H.head(page, record.projectName); exactHead(head, fixture);
    record.originalHead = { id: head.id, parent: head.parent,
      files: head.files.map(f => ({ id: f.id, path: f.path, length: f.bytes.length, sha256: sha(Buffer.from(f.bytes)) })) }; flush();
    await compile(record.online, fixture, head, 'online');
    step('close all browsers and stop owned origin'); await closeBrowser(); await stopServer();
    try { await fetch(origin + '/', { signal: AbortSignal.timeout(5000) }); assert.fail('stopped origin answered'); }
    catch (error) { assert.equal(error.cause?.code, 'ECONNREFUSED'); record.restart.originRefused = error.cause.code; }
    record.restart.hostStopped = true; record.restart.offlineBeforeNavigation = true; flush();
    await openBrowser(true);
    const row = H.$(page, 'project-row').filter({ hasText: record.projectName }); assert.equal(await row.count(), 1);
    await row.locator('[data-testid=open-project]').click(); await readyProject(record.projectName);
    assert.deepEqual(await H.head(page, record.projectName), head, 'same exact original head and all binary/source bytes after restart');
    await compile(record.coldOffline, fixture, head, 'cold-offline');
    assert.notEqual(record.coldOffline.request, record.online.request, 'new real compile identity, without source modification');
    assert.equal(record.coldOffline.inputDigest, record.online.inputDigest, 'identical original compile inputs');
    assert.deepEqual(await H.head(page, record.projectName), head);
    assert.deepEqual(record.pageErrors, []); assert.deepEqual(record.externalRequests, []); assert.deepEqual(record.cspViolations, []);
    completed = true;
  } catch (error) {
    record.failure = { step: record.step, message: String(error.stack || error).slice(0, 10000) }; flush(); process.exitCode = 1;
    if (page && !page.isClosed()) {
      const captures = (async () => {
        record.failure.frame = await frame().catch(() => null);
        record.failure.selection = await H.$(page, 'editor').evaluate(e => ({ fileId: e.dataset.fileId,
          start: e.selectionStart, end: e.selectionEnd, value: e.value })).catch(() => null);
        record.failure.nativePhases = await bridge().catch(() => null); flush();
        await page.screenshot({ path: path.join(OUT, 'failure.png'), fullPage: true });
      })();
      await Promise.race([captures.catch(error => { record.failure.captureError = String(error); }), pause(10000)]);
    }
  } finally {
    try { if (context) await context.close(); context = null; } catch (error) { record.cleanupFailure = String(error); process.exitCode = 1; }
    try { await stopServer(); } catch (error) { record.serverCleanupFailure = String(error); process.exitCode = 1; }
    if (completed && !record.cleanupFailure && !record.serverCleanupFailure) { record.result = 'PASS'; record.step = 'PASS original P11 UI navigation online/full cold offline'; }
    clearTimeout(deadline); flush();
    process.stdout.write(JSON.stringify({ result: record.result, receipt, failure: record.failure?.message || null }) + '\n');
  }
})();
