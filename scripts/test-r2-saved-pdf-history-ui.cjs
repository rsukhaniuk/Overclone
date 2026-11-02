// Genuine registered-PWA history acceptance. Tooling only; ROOT executes.
// No application/provider/worker injection, storage writes, fake PDFs or retries.
'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const { createHash } = require('node:crypto');
const H = require('./review-ui-helpers.cjs');
const { buildZip } = require('./zip-util.cjs');
const { validate: validateCsp } = require('./test-csp.cjs');
const sha = b => createHash('sha256').update(b).digest('hex');
const need = k => { assert(process.env[k], 'Required environment: ' + k); return process.env[k]; };
const publishedInput = fs.realpathSync(need('PUBLISHED_WWWROOT'));
const out = fs.realpathSync(need('RESULT_DIR'));
assert(out !== publishedInput && !out.startsWith(publishedInput + path.sep), 'RESULT_DIR must not write inside immutable publication');
assert.equal(fs.readdirSync(out).length, 0, 'RESULT_DIR must exist, be empty and exclusive (including failed runs)');
fs.writeFileSync(path.join(out, 'exclusive.json'), JSON.stringify({ pid: process.pid, started: new Date().toISOString() }), { flag: 'wx' });
const receipt = path.join(out, 'saved-pdf-history-receipt.json');
const record = { schemaVersion: 1, workPackage: 'R2-U-saved-pdf-association', phase: 'actual-browser-acceptance',
  result: 'FAIL', step: 'initializing', harnessSha256: sha(fs.readFileSync(__filename)),
  runtime: { node: process.version, platform: process.platform }, publication: null, fixture: null,
  actions: [], workers: [], pageErrors: [], externalRequests: [], tabs: [], dirtyProbe: null,
  online: null, reader: null, restart: null, coldOffline: null, failure: null,
  notClaimed: ['Held native/read/disposal/late-callback races: deterministic managed coverage only unless separately observed.',
    'Compile-setting equality, dirty-working-text equality, historical map/ticket/currentness, native execution authentication.',
    'Full R2/MVP, physical PDF compliance, broader browser/device and distribution/security compatibility.'] };
let root, origin, server, context, browser, active, ownedServer = false, workflowComplete = false;
const sockets = new Set(), tabIds = new WeakMap(), observed = new WeakSet();
const TIME = { action: 30000, compile: 150000, provision: 300000, close: 15000 };
const flush = () => fs.writeFileSync(receipt, JSON.stringify(record, null, 2) + '\n');
function step(label, p = active) { if (p) active = p; record.step = label; flush(); }
async function bounded(p, ms, label) {
  let timer; try { return await Promise.race([p, new Promise((_, reject) => { timer = setTimeout(() => reject(Error(label)), ms); })]); }
  finally { clearTimeout(timer); }
}
const $ = H.$;
const canonical = v => typeof v === 'string' ? v.replace(/-/g, '').toLowerCase()
  : v && typeof v === 'object' ? canonical(v.value ?? v.Value) : v == null ? null : assert.fail('Unexpected identity');
const hex = v => typeof v === 'string' ? v : v?.hex ?? v?.Hex;
const id = b => ({ length: b.length, sha256: sha(b) });
const MAIN = 'main.tex', PART = 'розділ/part.tex', BINARY = 'untouched.bin';
const BOM = Buffer.from([239, 187, 191]);
const alpha = '\\documentclass{article}\r\n% Україна 🙂 comments only; no Unicode font requirement\r\n\\begin{document}\r\nHistory Alpha saved PDF.\\par\r\n\\input{розділ/part.tex}\r\n\\end{document}\r\n';
const part = '% Київ 😀 included comments preserved\r\nIncluded Alpha preserved source.\\par\r\n';
const fixture = [[MAIN, Buffer.concat([BOM, Buffer.from(alpha)])], [PART, Buffer.concat([BOM, Buffer.from(part)])],
  [BINARY, Buffer.from([0, 255, 13, 10, 128, 239, 187, 191, 42])]];
const fixtureZip = buildZip(fixture);
const original = () => new Map(fixture.map(([n, b]) => [n, Buffer.from(b)]));
const editorValue = b => b.toString('utf8').replace(/^\uFEFF/, '').replace(/\r\n?/g, '\n');
const betaText = editorValue(fixture[0][1]).replace('History Alpha saved PDF.\\par',
  'History Beta current PDF.\\par\n\\newpage\nSecond Beta page.\\par\n\\newpage\nThird Beta page.\\par');
const betaBytes = Buffer.concat([BOM, Buffer.from(betaText)]); // ordinary edit encoding: LF + retained BOM
const changed = () => { const m = original(); m.set(MAIN, betaBytes); return m; };
const projectName = 'Saved PDF synthetic ' + Date.now() + '-' + process.pid;
function history(p) { return $(p, 'compile-history-panel'); }
function hp(p, testId) { return history(p).locator(`[data-testid="${testId}"]`); }
function historicalPane(p) { return hp(p, 'pdf-pane'); }
function currentPane(p) { return $(p, 'compile-panel').locator(':scope > section[data-testid="pdf-pane"]'); }
function attach(p) {
  if (observed.has(p)) return; observed.add(p); const key = 'tab-' + (record.tabs.length + 1); tabIds.set(p, key);
  record.tabs.push({ id: key, closed: false }); p.setDefaultTimeout(TIME.action);
  p.on('close', () => { record.tabs.find(t => t.id === key).closed = true; });
  p.on('pageerror', e => record.pageErrors.push({ tab: key, message: String(e).slice(0, 2000) }));
  p.on('worker', w => { const entry = { tab: key, url: w.url(), closed: false }; record.workers.push(entry); w.on('close', () => { entry.closed = true; }); });
}
function external(url) { try { const u = new URL(url); return /^https?:$/.test(u.protocol) && u.origin !== origin; } catch { return false; } }
function census() {
  const found = [];
  for (const pid of fs.readdirSync('/proc')) {
    if (!/^\d+$/.test(pid)) continue;
    let exe, stat; try { exe = path.basename(fs.readlinkSync(`/proc/${pid}/exe`)); if (!/chromium|chrome|headless_shell/i.test(exe)) continue; stat = fs.readFileSync(`/proc/${pid}/stat`, 'utf8'); }
    catch (e) { if (['ENOENT', 'ESRCH'].includes(e.code)) continue; throw e; }
    const startticks = stat.slice(stat.lastIndexOf(') ') + 2).split(' ')[19]; assert.match(startticks, /^\d+$/); found.push({ pid: Number(pid), exe, startticks });
  }
  return found.sort((a, b) => a.pid - b.pid);
}
async function noChromium() {
  const end = Date.now() + 5000; let left;
  do { left = census(); if (!left.length) return left; await new Promise(resolve => setTimeout(resolve, 100)); } while (Date.now() < end);
  assert.deepEqual(left, [], 'ALL Chromium processes must exit; never kill unrelated processes');
}
async function serve() {
  const types = { '.html': 'text/html', '.js': 'text/javascript', '.json': 'application/json', '.css': 'text/css', '.wasm': 'application/wasm', '.svg': 'image/svg+xml' };
  server = http.createServer((req, res) => {
    let file;
    try { const pathname = decodeURIComponent(new URL(req.url, origin).pathname); file = fs.realpathSync(path.resolve(root, '.' + (pathname === '/' ? '/index.html' : pathname)));
      if (!file.startsWith(root + path.sep) || !fs.statSync(file).isFile()) { res.writeHead(403).end(); return; } }
    catch { res.writeHead(404).end(); return; }
    fs.readFile(file, (error, bytes) => {
      if (error) { res.writeHead(404).end(); return; }
      res.writeHead(200, { 'Content-Type': types[path.extname(file)] || 'application/octet-stream',
        'Content-Security-Policy': record.publication.csp, 'Cache-Control': 'no-cache', 'X-Content-Type-Options': 'nosniff' }); res.end(bytes);
    });
  });
  server.on('connection', s => { sockets.add(s); s.on('close', () => sockets.delete(s)); });
  await bounded(new Promise((resolve, reject) => { server.once('error', reject); server.listen(Number(new URL(origin).port), '127.0.0.1', () => { ownedServer = true; resolve(); }); }), 5000, 'owned origin startup deadline');
}
async function stopOrigin() {
  if (!server || !ownedServer) return;
  const own = server; for (const s of sockets) s.destroy();
  await bounded(new Promise((resolve, reject) => own.close(e => e ? reject(e) : resolve())), 5000, 'owned origin close deadline');
  ownedServer = false; server = null;
}
async function refusal() {
  await bounded(new Promise((resolve, reject) => {
    const req = http.get(origin + '/', r => { r.resume(); reject(Error('Owned origin still answers')); });
    req.on('error', e => e.code === 'ECONNREFUSED' ? resolve() : reject(e)); req.setTimeout(2000, () => req.destroy(Error('Refusal not proved')));
  }), 5000, 'origin refusal deadline');
}
async function launch(offline) {
  const { chromium } = require('playwright');
  context = await chromium.launchPersistentContext(path.join(out, 'profile'), { headless: true, offline, acceptDownloads: true, timeout: TIME.close });
  browser = context.browser(); record.runtime.browser = browser?.version() || 'persistent Chromium';
  context.pages().forEach(attach); context.on('page', attach);
  context.on('request', req => { if (external(req.url())) { const u = new URL(req.url()); record.externalRequests.push({ origin: u.origin, path: u.pathname, type: req.resourceType(), method: req.method() }); } });
  await context.route('**/*', route => external(route.request().url()) ? route.abort('blockedbyclient') : route.continue());
  const p = await context.newPage(); attach(p); active = p;
  await p.goto(origin + '/', { timeout: TIME.provision }); await H.ready(p); return p;
}
async function closeBrowser() {
  if (!context) return null;
  const old = context, pages = old.pages(), b = browser;
  await bounded(old.close(), TIME.close, 'persistent context close deadline');
  if (b?.isConnected()) await bounded(b.close(), TIME.close, 'browser close deadline');
  assert(pages.every(p => p.isClosed()), 'All tabs closed'); assert(!b || !b.isConnected(), 'Browser disconnected');
  context = null; browser = null;
  const remaining = await noChromium(); return { tabsClosed: pages.length, browserDisconnected: true, chromiumProcesses: remaining };
}
function preflight() {
  root = publishedInput; assert(out !== root && !out.startsWith(root + path.sep), 'Results must not alter publication');
  const expected = need('EXPECTED_SDK_VERSION'), sourceDigest = need('CANDIDATE_SOURCE_DIGEST'); assert.match(sourceDigest, /^[0-9a-f]{64}$/);
  const port = Number(process.env.TEST_PORT || 8792); assert(Number.isInteger(port) && port > 1024 && port < 65536); origin = 'http://127.0.0.1:' + port;
  const headerBytes = fs.readFileSync(path.join(__dirname, '../docs/examples/static-csp-headers.conf'));
  validateCsp(fs.readFileSync(path.join(root, 'index.html'), 'utf8'), headerBytes.toString());
  const csp = headerBytes.toString().split('\n').map(s => s.trim()).find(s => s.startsWith('Content-Security-Policy:')).slice('Content-Security-Policy:'.length).trim();
  const bytes = fs.readFileSync(path.join(root, 'service-worker-assets.js')), match = bytes.toString().match(/self\.assetsManifest\s*=\s*(\{[\s\S]*\})\s*;/); assert(match);
  const manifest = JSON.parse(match[1]); assert.equal(manifest.version, expected, 'Require actual requested SDK, never hardcode the live version');
  const names = new Set();
  for (const asset of manifest.assets) {
    assert(typeof asset.url === 'string' && !path.isAbsolute(asset.url) && !asset.url.includes('\\') && !asset.url.split('/').includes('..') && !names.has(asset.url)); names.add(asset.url);
    const file = fs.realpathSync(path.join(root, asset.url)); assert(file.startsWith(root + path.sep));
    assert.equal('sha256-' + createHash('sha256').update(fs.readFileSync(file)).digest('base64'), asset.hash, 'SDK asset SRI ' + asset.url);
  }
  record.publication = { sdkVersion: expected, candidateSourceDigest: sourceDigest, sourceDigestProvenance: 'root-supplied source identity, not derived from binaries',
    manifestSha256: sha(bytes), assetCount: manifest.assets.length, allManifestHashesVerified: true, csp, cspSourceSha256: sha(headerBytes), origin };
  record.helpers = Object.fromEntries(['review-ui-helpers.cjs', 'zip-util.cjs', 'test-csp.cjs'].map(f => [f, sha(fs.readFileSync(path.join(__dirname, f)))]));
  record.fixture = { synthetic: true, zipSha256: sha(fixtureZip), filesA: fixture.map(([name, b]) => ({ name, ...id(b) })), mainB: id(betaBytes),
    mainBEncoding: 'ordinary textarea edit: LF with original BOM preserved; include BOM/CRLF and binary untouched' };
}
function verifyBytes(head, expected) {
  assert.equal(head.files.length, expected.size);
  for (const [file, b] of expected) { const actual = head.files.find(f => f.path === file); assert(actual, file); assert.deepEqual(Buffer.from(actual.bytes), b, 'Exact persisted bytes ' + file); }
}
function summary(head) { return { id: head.id, parent: head.parent, files: head.files.map(f => ({ id: f.id, path: f.path, ...id(Buffer.from(f.bytes)) })) }; }
async function exactHead(p, expected) { const head = await H.head(p, projectName); verifyBytes(head, expected); return head; }
async function headId(p) { return p.evaluate(async name => { const m = await import('./js/workspace-storage.js'); const item = (await m.listProjects()).find(x => x.name === name); if (!item) throw Error('Missing synthetic project'); return item.headRevisionId; }, projectName); }
async function chooseMain(p, expected) {
  const before = await H.head(p, projectName), file = before.files.find(f => f.path === MAIN); assert(file);
  const target = $(p, 'select-file').filter({ has: p.locator('.file-name', { hasText: /^main\.tex$/ }) }); assert.equal(await target.count(), 1); await target.click();
  await p.waitForFunction(({ fileId, text }) => { const e = document.querySelector('[data-testid=editor]'); return e?.dataset.fileId === fileId && e.value === text && e.dataset.journalPath === 'main.tex'; }, { fileId: file.id, text: editorValue(expected) });
  return file.id;
}
async function setupHistory(p) { await H.reveal(p, hp(p, 'refresh-pdf-history')); await history(p).waitFor(); }
async function snapshot(p) {
  return p.evaluate(() => {
    const h = document.querySelector('[data-testid=compile-history-panel]'), c = document.querySelector('[data-testid=compile-panel]');
    const text = (root, n, limit = 2048) => root?.querySelector(`[data-testid="${n}"]`)?.textContent?.trim().slice(0, limit) ?? null;
    const panel = root => root ? { hash: text(root, 'pdf-hash'), preview: text(root, 'preview-status'), previewStatus: root.querySelector('[data-testid=preview-status]')?.dataset.previewStatus,
      pages: text(root, 'page-label'), provenance: text(root, 'pdf-provenance'), text: text(root, 'page-text', 6000), canvas: text(root, 'canvas-status'),
      sourceButtons: [...root.querySelectorAll('button[aria-label="Go to source"]')].map(b => ({ disabled: b.disabled })), sourceHighlights: root.querySelectorAll('[data-testid=source-pdf-highlight]').length } : null;
    return { source: { ...document.querySelector('[data-testid=analysis-panel]')?.dataset },
      editor: (() => { const e = document.querySelector('[data-testid=editor]'); return e ? { file: e.dataset.fileId, generation: e.dataset.highlightRevision, path: e.dataset.journalPath, value: e.value, disabled: e.disabled, readonly: e.readOnly } : null; })(),
      history: { status: text(h, 'history-status'), record: text(h, 'history-record-identity'), capturedHead: text(h, 'history-captured-head'), root: text(h, 'history-captured-root'), engine: text(h, 'history-captured-engine'),
        input: text(h, 'history-captured-input'), pdfHash: text(h, 'history-pdf-sha256'), pdfSize: text(h, 'history-pdf-size'), association: { ...h?.querySelector('[data-testid=history-source-association]')?.dataset },
        associationText: text(h, 'history-source-association'), dirty: h?.querySelector('[data-testid=history-working-source-disclosure]')?.dataset.dirtyWorkingSource,
        preview: panel(h?.querySelector('[data-testid=pdf-pane]')) },
      current: { hash: c?.dataset.outputHash, isCurrent: c?.dataset.isCurrent, status: text(c, 'compile-status'), preview: panel(c?.querySelector(':scope > [data-testid=pdf-pane]')) } };
  });
}
async function bridge(p) { return p.evaluate(async () => (await import('./js/compile-port-bridge.js')).observations()); }
async function state(p) { return { head: await headId(p), stamp: await H.stamp(p), bridgeStarted: (await bridge(p)).started }; }
async function neutralAction(p, label, action) {
  step(label, p); const before = await state(p); await action(); const after = await state(p);
  record.actions.push({ label, tab: tabIds.get(p), before, after }); flush(); assert.deepEqual(after, before, label + ' must not save/edit/recompile');
}
async function waitAssociation(p, status, revision, stale = false) {
  await p.waitForFunction(({ status, revision, stale }) => {
    const a = document.querySelector('[data-testid=compile-history-panel] [data-testid=history-source-association]');
    return a?.dataset.associationStatus === status && a.dataset.associationScope === 'RevisionFileManifestOnly' && a.dataset.associationReadStatus === 'Ready'
      && a.dataset.comparedRevision.replace(/-/g, '').toLowerCase() === revision && a.dataset.comparisonRequested === 'true' && a.dataset.comparisonPending === 'false' && a.dataset.comparisonStale === String(stale);
  }, { status, revision: canonical(revision), stale });
}
async function compare(p, status, revision, label) {
  await neutralAction(p, label, async () => { await hp(p, 'compare-saved-pdf-source').click(); await waitAssociation(p, status, revision); });
  return (await snapshot(p)).history;
}
async function raster(p, pane, expectedHash, expectedText, pages, label) {
  await H.reveal(p, pane.locator('[data-testid=page-text]'));
  await pane.locator('[data-testid=preview-status][data-preview-status=Available]').waitFor({ timeout: TIME.compile });
  await p.waitForFunction(({ historical, hash, expected, pages }) => {
    const root = historical ? document.querySelector('[data-testid=compile-history-panel] [data-testid=pdf-pane]') : document.querySelector('[data-testid=compile-panel] > [data-testid=pdf-pane]');
    return root?.dataset.document === hash && root.querySelector('[data-testid=canvas-status]')?.textContent === 'Drawn'
      && root.querySelector('[data-testid=page-text]')?.textContent.includes(expected) && root.querySelector('[data-testid=page-label]')?.textContent === `Page 1 of ${pages}`;
  }, { historical: label.includes('historical'), hash: expectedHash, expected: expectedText, pages }, { timeout: TIME.compile });
  assert.equal(await pane.count(), 1); const canvas = pane.locator('[data-testid=pdf-canvas]');
  const proof = await canvas.evaluate(c => { const pixels = c.getContext('2d').getImageData(0, 0, c.width, c.height).data; let ink = 0; for (let i = 0; i < pixels.length; i += 4) if (pixels[i] < 245 || pixels[i + 1] < 245 || pixels[i + 2] < 245) ink++; return { width: c.width, height: c.height, inkPixels: ink }; });
  assert(proof.width > 0 && proof.height > 0 && proof.inkPixels > 50, 'Actual native raster must contain ink');
  return { label, hash: expectedHash, pages, expectedText, ...proof };
}
async function assertHistorical(p, a, pages = 1) {
  assert.equal((await hp(p, 'history-record-identity').textContent()).trim(), a.record);
  assert.equal((await hp(p, 'history-pdf-sha256').textContent()).trim(), a.hash); assert.equal(Number(await hp(p, 'history-pdf-size').textContent()), a.length);
  assert.equal(canonical((await hp(p, 'history-captured-head').textContent()).trim()), canonical(a.head));
  assert.equal((await hp(p, 'history-captured-root').textContent()).trim(), MAIN); assert.match(await hp(p, 'history-captured-engine').textContent(), /xelatex\s*\/\s*maps-abi1/);
  assert.match(await history(p).innerText(), /Provenance: HistoricalOnly/); assert.match(await history(p).innerText(), /provenance only/);
  assert.equal(await historicalPane(p).getByRole('button', { name: 'Go to source', exact: true }).isDisabled(), true);
  assert.equal(await hp(p, 'source-pdf-highlight').count(), 0); assert.match(await hp(p, 'pdf-provenance').textContent(), /CallerSaysHistorical/);
  return raster(p, historicalPane(p), a.hash, 'History Alpha', pages, 'historical actual page');
}
async function download(p, saved, expected, label) {
  let result; await neutralAction(p, label, async () => {
    const button = saved ? hp(p, 'download-saved-pdf') : $(p, 'compile-panel').locator('[data-testid=download-pdf]');
    const [d] = await Promise.all([p.waitForEvent('download'), button.click()]);
    assert.equal(await bounded(d.failure(), TIME.action, 'download completion deadline'), null);
    const staged = await bounded(d.path(), 5000, 'download path deadline'); assert(staged); const size = fs.statSync(staged).size;
    assert(size > 5 && size <= 8388608); const target = path.join(out, label + '.pdf'); assert(!fs.existsSync(target));
    await bounded(d.saveAs(target), TIME.action, 'download saveAs deadline'); const bytes = fs.readFileSync(target); assert.equal(bytes.length, size); assert.equal(bytes.subarray(0, 5).toString(), '%PDF-');
    if (expected) { assert.equal(sha(bytes), expected.hash); assert.equal(bytes.length, expected.length); if (expected.file) assert.deepEqual(bytes, fs.readFileSync(path.join(out, expected.file))); }
    result = { file: path.basename(target), hash: sha(bytes), length: bytes.length, suggestedFilename: d.suggestedFilename() }; assert.equal(path.basename(result.suggestedFilename), result.suggestedFilename);
    await p.waitForFunction(saved => document.querySelector(saved ? '[data-testid=compile-history-panel] [data-testid=history-status]' : '[data-testid=pdf-download-status]')?.textContent.trim() === (saved ? 'Saved PDF download started.' : 'PDF download started.'), saved);
  }); return result;
}
async function compile(p, engine, expected, pages, label) {
  step(label, p); await H.reveal(p, $(p, 'refresh-compile-tools'));
  await p.waitForFunction(() => document.querySelector('[data-testid=compiler-readiness]')?.textContent.includes('compilation available.'), null, { timeout: TIME.provision });
  await p.locator('select[aria-label="Compile main file"]').selectOption({ label: MAIN }); await p.locator('select[aria-label="Compile engine"]').selectOption(engine);
  const bib = $(p, 'use-bibtex'); if (await bib.count() && await bib.isChecked()) await bib.uncheck();
  const before = await bridge(p), beforeHead = await H.head(p, projectName), oldHash = await $(p, 'compile-panel').getAttribute('data-output-hash');
  const fresh = p.waitForEvent('worker', { predicate: w => /\/__oop\/native-worker\/(?:compile|xetex)\?/.test(w.url()), timeout: TIME.compile });
  await Promise.all([fresh, $(p, 'compile').click()]);
  await p.waitForFunction(old => { const c = document.querySelector('[data-testid=compile-panel]'); return document.querySelector('[data-testid=compile-status]')?.textContent === 'Compilation finished.' && document.querySelector('[data-testid=compile-work-status]')?.textContent.trim() === 'Idle' && c?.dataset.outputHash && c.dataset.outputHash !== old && c.dataset.isCurrent === 'true'; }, oldHash, { timeout: TIME.compile });
  const after = await bridge(p), trace = after.trace.slice(before.trace.length); assert(after.started > before.started && trace.length > 0 && after.active === 0);
  assert(trace.every(t => t.entered && t.terminated && t.outcome === 'native-return' && t.nativeReturn === 0 && t.tag.profile === 9));
  assert(trace.every(t => t.tag.version === (engine === 'xelatex' ? 13 : 6)));
  if (engine === 'xelatex') { assert(trace.some(t => t.tag.tool === 'dvipdfmx')); assert(trace.filter(t => t.tag.tool === 'latex').length >= 2); }
  assert.deepEqual(await H.head(p, projectName), beforeHead, 'Native compilation does not save/mutate project');
  const hash = await $(p, 'compile-panel').getAttribute('data-output-hash'); assert.match(hash, /^[0-9a-f]{64}$/);
  const proof = await raster(p, currentPane(p), hash, expected, pages, 'current ' + label); record.actions.push({ label, trace, beforeHead: summary(beforeHead), raster: proof }); flush(); return hash;
}
// Read-only forensic witness of the ACTUAL persisted metadata. No new storage
// adapter/provider is instantiated and no object store/directory is created.
async function storedMetadata(p, recordId) {
  return p.evaluate(async recordId => {
    const dbName = 'overclone-compile-artifacts-v1'; if (!(await indexedDB.databases()).some(d => d.name === dbName)) throw Error('Artifact DB not created by real UI save');
    const db = await new Promise((resolve, reject) => { const r = indexedDB.open(dbName); r.onupgradeneeded = () => { r.transaction.abort(); reject(Error('Read-only witness refused schema creation')); }; r.onsuccess = () => resolve(r.result); r.onerror = () => reject(r.error); });
    let text; try { text = await new Promise((resolve, reject) => { const tx = db.transaction('catalog', 'readonly'), r = tx.objectStore('catalog').get(recordId); let v; r.onsuccess = () => { v = r.result; }; tx.oncomplete = () => resolve(v); tx.onabort = () => reject(tx.error); tx.onerror = () => reject(tx.error); }); } finally { db.close(); }
    if (typeof text !== 'string' || new TextEncoder().encode(text).length > 1048576) throw Error('Missing/bounded catalog');
    const c = JSON.parse(text), i = c.assets.findIndex(a => a.relativePath === 'history/metadata.json'); if (i < 0 || c.assets.length !== 2 || c.installationId !== recordId) throw Error('Historical catalog shape');
    const d = await (await (await navigator.storage.getDirectory()).getDirectoryHandle(dbName)).getDirectoryHandle(recordId);
    const f = await (await d.getFileHandle(String(i))).getFile(); if (f.size < 1 || f.size > 262144 || f.size !== c.assets[i].declaredLength) throw Error('Metadata bounds');
    const bytes = new Uint8Array(await f.arrayBuffer()), digest = [...new Uint8Array(await crypto.subtle.digest('SHA-256', bytes))].map(x => x.toString(16).padStart(2, '0')).join('');
    if (digest !== c.assets[i].hash) throw Error('Stored metadata hash');
    return { metadata: JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes)), metadataSha256: digest, storageInventoryDigest: c.inventoryDigest };
  }, recordId);
}
async function openHistory(p, key = null) {
  await setupHistory(p); await hp(p, 'refresh-pdf-history').click(); await hp(p, 'open-saved-pdf').first().waitFor();
  const buttons = hp(p, 'open-saved-pdf'); assert.equal(await buttons.count(), 1, 'Fresh synthetic project has one real saved PDF'); await buttons.first().click();
  await hp(p, 'history-record-identity').waitFor({ state: 'attached' });
  await H.reveal(p, hp(p, 'history-record-identity'));
  await hp(p, 'history-record-identity').waitFor({ state: 'visible' }); await hp(p, 'compare-saved-pdf-source').waitFor({ state: 'visible' });
  if (key) assert.equal((await hp(p, 'history-record-identity').textContent()).trim(), key);
  const a = await hp(p, 'history-source-association').evaluate(e => ({ ...e.dataset }));
  assert.equal(a.associationStatus, 'Unassociated'); assert.equal(a.comparisonRequested, 'false'); assert.equal(a.comparisonPending, 'false');
}
async function earlierRevision(p, revision, expected) {
  const data = await p.evaluate(async revision => {
    const m = await import('./js/workspace-storage.js'), r = await m.getRevision(revision); if (!r || r.id !== revision) throw Error('Original saved revision unavailable');
    return { id: r.id, parent: r.parentRevisionId, files: await Promise.all(r.files.map(async f => ({ id: f.id, path: f.relativePath, bytes: Array.from(await m.readContent(f.contentHash)) }))) };
  }, revision); verifyBytes(data, expected);
  await H.reveal(p, $(p, 'history-left'));
  const listed = await $(p, 'history-left').locator('option').evaluateAll(options => options.map(o => o.value));
  assert(listed.some(value => canonical(value) === canonical(revision)), 'Original source A remains discoverable as a saved revision, not merely an orphaned object');
  return { ...summary(data), discoverableInSourceHistory: true };
}
async function readerFlow(writer, a, bHead) {
  const reader = await context.newPage(); attach(reader); step('genuine second readonly tab before any close', reader);
  try {
    await reader.goto(origin + '/'); await H.ready(reader); await H.open(reader, projectName); await chooseMain(reader, betaBytes);
    await reader.waitForFunction(() => { const e = document.querySelector('[data-testid=editor]'); return e && (e.disabled || e.readOnly); });
    const before = await state(reader); assert.equal(await $(reader, 'save').isDisabled(), true); assert.equal(await $(reader, 'compile').isDisabled(), true);
    const replace = $(reader, 'project-replace-preview'); if (await replace.count()) assert.equal(await replace.isDisabled(), true);
    await openHistory(reader, a.record); const result = await compare(reader, 'Mismatch', bHead.id, 'reader-manifest-comparison');
    const rasterProof = await assertHistorical(reader, a); const exported = await download(reader, true, a, 'reader-historical-A');
    assert.deepEqual(await state(reader), before); assert.deepEqual(await exactHead(reader, changed()), bHead);
    assert.equal((await $(writer, 'editor').evaluate(e => e.disabled || e.readOnly)), false, 'Writer lock not taken over');
    return { genuineReadonly: true, before, association: result, raster: rasterProof, download: exported, noTakeover: true };
  } catch (error) { await captureFailure(); throw error; } // offending reader captured BEFORE close
  finally { await reader.close(); active = writer; }
}
async function online(p) {
  step('import genuine synthetic BOM/CRLF two-TeX/binary ZIP', p);
  record.online = { project: projectName }; flush();
  const close = p.getByRole('button', { name: 'Close', exact: true }); if (await close.count()) await close.click();
  await H.reveal(p, $(p, 'import-zip')); await $(p, 'import-zip').setInputFiles({ name: 'saved-pdf-synthetic.zip', mimeType: 'application/zip', buffer: fixtureZip });
  await $(p, 'import-preview').waitFor({ timeout: TIME.provision }); await H.reveal(p, $(p, 'staged-name')); await $(p, 'staged-name').fill(projectName); await $(p, 'publish-import').click();
  await $(p, 'select-file').first().waitFor({ timeout: TIME.provision }); await chooseMain(p, fixture[0][1]);
  if (await $(p, 'save').isEnabled()) { await H.save(p); record.online.initialSourceSave = 'Explicit Save'; }
  else { assert.equal(await p.locator('.workspace-header .status').textContent(), 'Saved.'); record.online.initialSourceSave = 'Explicit ZIP publication already saved; Save correctly disabled'; }
  flush();
  const aHead = await exactHead(p, original()); const alphaHash = await compile(p, 'xelatex', 'History Alpha', 1, 'online-Xe-A');
  const a = await download(p, false, null, 'current-Xe-A'); assert.equal(a.hash, alphaHash); a.head = aHead.id;
  Object.assign(record.online, { aHead: summary(aHead), a }); flush();
  await setupHistory(p); await neutralAction(p, 'save compiled A to artifact history without saving source', async () => { await hp(p, 'save-pdf-history').click(); await p.waitForFunction(() => document.querySelector('[data-testid=compile-history-panel] [data-testid=history-status]')?.textContent.startsWith('PDF saved to history.')); });
  await openHistory(p); a.record = (await hp(p, 'history-record-identity').textContent()).trim(); assert.match(a.record, /^[0-9a-f]{32}$/);
  const metadata = await storedMetadata(p, a.record), m = metadata.metadata;
  assert.equal(m.classification, 'HistoricalOnly'); assert.equal(m.version, 2); assert.equal(m.artifactVersion, 2); assert.equal(m.adapter.id, 'native-xetex'); assert.equal(m.adapter.version, 'geometry-abi3');
  assert.equal(hex(m.adapter.hash), '10031d2748be3f6fa6ba5bc99fd4ef597f06f66208c5b9a5aeba60d5dc3aecac');
  assert.equal(canonical(m.originalSnapshot.baseHeadRevisionId), canonical(aHead.id)); assert.equal(hex(m.pdfHash), a.hash); assert.equal(m.pdfLength, a.length);
  assert(m.sourceMapGeometry && hex(m.sourceMapGeometry.hash)); assert(!('pages' in m.sourceMapGeometry)); assert(!('sourceMapPayload' in m) && !('sourceMapGeometryPayload' in m) && !('context' in m) && !('ticket' in m));
  Object.assign(record.online, { storedMetadata: metadata }); flush();
  const opened = await assertHistorical(p, a), verified = await compare(p, 'VerifiedFileManifest', aHead.id, 'explicit saved-A comparison');
  Object.assign(record.online, { opened, verified }); flush();
  const savedDownload = await download(p, true, a, 'historical-A-before-edit');
  // Prepare EVERY locator/control and full-value fence before the ONE fill.
  await chooseMain(p, fixture[0][1]); await setupHistory(p); const editor = $(p, 'editor'), compareButton = hp(p, 'compare-saved-pdf-source');
  assert.equal(await compareButton.isEnabled(), true); const beforeStamp = await H.stamp(p), beforeHead = await headId(p);
  assert.equal(canonical(beforeHead), canonical(aHead.id)); assert.equal(await editor.inputValue(), editorValue(fixture[0][1]));
  record.dirtyProbe = { attemptedOnce: true, headBeforeFill: beforeHead, stampBeforeFill: beforeStamp, expectedMainAfterOrdinaryEdit: id(betaBytes) }; flush();
  const started = Date.now(); await editor.fill(betaText);
  await p.waitForFunction(({ text, generation }) => { const e = document.querySelector('[data-testid=editor]'), a = document.querySelector('[data-testid=analysis-panel]'), h = document.querySelector('[data-testid=compile-history-panel]'); return e?.value === text && Number(e.dataset.highlightRevision) > Number(generation) && e.dataset.highlightRevision === a?.dataset.generation && h?.querySelector('[data-testid=history-working-source-disclosure]')?.dataset.dirtyWorkingSource === 'true' && h?.querySelector('[data-testid=history-source-association]')?.dataset.comparisonStale === 'true'; }, { text: betaText, generation: beforeStamp.generation }, { timeout: 1500 });
  const atClick = await p.evaluate(async name => { const m = await import('./js/workspace-storage.js'), project = (await m.listProjects()).find(p => p.name === name), h = document.querySelector('[data-testid=compile-history-panel]'); return { head: project?.headRevisionId, dirty: h?.querySelector('[data-testid=history-working-source-disclosure]')?.dataset.dirtyWorkingSource, association: { ...h?.querySelector('[data-testid=history-source-association]')?.dataset } }; }, projectName);
  record.dirtyProbe.beforeCompare = atClick; record.dirtyProbe.msBeforeCompare = Date.now() - started; flush();
  await compareButton.click(); // one immediate action; no retry/fill/clock/storage workaround
  let dirtyCompletionError = null;
  try {
    await p.waitForFunction(revision => { const a = document.querySelector('[data-testid=compile-history-panel] [data-testid=history-source-association]')?.dataset;
      return a?.comparisonRequested === 'true' && a.comparisonPending === 'false' && (a.comparisonStale === 'false' || a.associationReadStatus !== 'Ready' || a.associationStatus !== 'VerifiedFileManifest' || a.comparedRevision.replace(/-/g, '').toLowerCase() !== revision);
    }, canonical(aHead.id), { timeout: 5000 });
  } catch (error) { dirtyCompletionError = String(error); }
  const afterDirty = await snapshot(p), afterHead = await headId(p);
  Object.assign(record.dirtyProbe, { after: afterDirty, headAfterCompare: afterHead, completionError: dirtyCompletionError, elapsedMs: Date.now() - started }); flush();
  assert.equal(canonical(atClick.head), canonical(aHead.id), 'Autosave won before compare: strict dirty probe FAIL; do not relabel as saved-B proof');
  assert.equal(canonical(afterHead), canonical(aHead.id), 'Autosave won during compare: strict dirty probe FAIL'); assert.equal(dirtyCompletionError, null, 'New dirty comparison result was not established; do not reuse prior stale UI');
  assert.equal(afterDirty.history.association.associationStatus, 'VerifiedFileManifest'); assert.equal(afterDirty.history.association.associationReadStatus, 'Ready');
  assert.equal(afterDirty.history.association.associationScope, 'RevisionFileManifestOnly'); assert.equal(canonical(afterDirty.history.association.comparedRevision), canonical(aHead.id));
  assert.equal(afterDirty.history.association.comparisonStale, 'false'); assert.equal(atClick.dirty, 'true'); assert.equal(afterDirty.history.dirty, 'true');
  assert.equal(atClick.association.comparisonStale, 'true'); record.dirtyProbe.result = 'PASS'; flush();
  // Save explicitly, OR prove a completed ordinary autosave without retrying fill.
  step('save genuine B; old comparison must stale', p);
  if (canonical(await headId(p)) === canonical(aHead.id)) {
    if (await $(p, 'save').isEnabled()) await H.save(p);
    else await p.waitForFunction(() => document.querySelector('.workspace-header .status')?.textContent === 'Saved.');
  }
  const bHead = await exactHead(p, changed()); assert.notEqual(canonical(bHead.id), canonical(aHead.id));
  record.online.bHead = summary(bHead); flush();
  await p.waitForFunction(() => document.querySelector('[data-testid=compile-history-panel] [data-testid=history-source-association]')?.dataset.comparisonStale === 'true');
  const mismatch = await compare(p, 'Mismatch', bHead.id, 'explicit saved-B mismatch');
  const betaHash = await compile(p, 'local-authoring-v4', 'History Beta', 3, 'online-pdfLaTeX-B'); assert.notEqual(betaHash, a.hash);
  const b = await download(p, false, null, 'current-pdfLaTeX-B'); assert.equal(b.hash, betaHash);
  Object.assign(record.online, { b, mismatch }); flush();
  const preserved = await assertHistorical(p, a); const savedAfterB = await download(p, true, a, 'historical-A-while-current-B');
  await openHistory(p, a.record); await compare(p, 'Mismatch', bHead.id, 'reopened historical-A saved-B mismatch');
  assert.deepEqual(await exactHead(p, changed()), bHead); const revisionA = await earlierRevision(p, aHead.id, original());
  record.reader = await readerFlow(p, a, bHead); assert.deepEqual(await exactHead(p, changed()), bHead);
  return { project: projectName, aHead: summary(aHead), bHead: summary(bHead), a, b, storedMetadata: metadata, opened, verified, savedDownload, mismatch, preserved, savedAfterB, revisionA };
}
async function cold(p) {
  step('fresh offline project/session opens B and historical A', p); await H.open(p, projectName); await chooseMain(p, betaBytes);
  const before = await exactHead(p, changed()), stamp = await H.stamp(p); assert.deepEqual(summary(before), record.online.bHead);
  assert.notEqual(canonical(stamp.session), canonical(record.dirtyProbe.stampBeforeFill.session), 'Fresh project session, not retained UI result');
  await openHistory(p, record.online.a.record); const association = await compare(p, 'Mismatch', before.id, 'cold historical-A compare against saved-B');
  const rasterProof = await assertHistorical(p, record.online.a), exported = await download(p, true, record.online.a, 'cold-historical-A');
  const revisionA = await earlierRevision(p, record.online.aHead.id, original()); assert.deepEqual(await exactHead(p, changed()), before);
  return { session: stamp, savedB: summary(before), association, raster: rasterProof, download: exported, revisionA, exactBAndBinaryPersist: true };
}
async function captureFailure() {
  record.failureTabs = [];
  for (const p of context?.pages() || []) {
    if (p.isClosed()) continue; const key = tabIds.get(p) || 'unobserved';
    const details = await snapshot(p).catch(e => ({ unavailable: String(e) }));
    const body = await p.locator('body').innerText({ timeout: 5000 }).catch(e => String(e));
    record.failureTabs.push({ tab: key, active: p === active, url: p.url(), body: body.slice(0, 45000), state: details });
    await p.screenshot({ path: path.join(out, 'failure-' + key + '.png'), fullPage: true, timeout: 5000 }).catch(e => { record.failureTabs.at(-1).screenshotError = String(e); });
  }
  flush();
}
(async () => {
  flush();
  try {
    preflight(); assert.deepEqual(census(), [], 'Run in dedicated browser environment; do not share or kill other Chromium'); flush();
    await serve(); const p = await launch(false); step('online real workflow', p); record.online = await online(p); flush();
    step('wait genuine SW/controller/offline-ready then ALL-browser shutdown', p);
    await p.evaluate(() => navigator.serviceWorker.ready);
    await p.waitForFunction(() => navigator.serviceWorker.controller !== null && window.__oopOfflineReady === true, null, { timeout: TIME.provision });
    const closed = await closeBrowser(); await stopOrigin(); await refusal();
    record.restart = { ...closed, ownedOriginStopped: true, originRefused: 'ECONNREFUSED', offlineBeforeNavigation: true, sameProfile: 'profile' }; flush();
    const offline = await launch(true); record.coldOffline = await cold(offline);
    assert.deepEqual(record.externalRequests, []); assert.deepEqual(record.pageErrors, []); workflowComplete = true; step('workflow complete; cleanup still required', offline);
  } catch (error) { record.failure = String(error.stack || error); process.exitCode = 1; await captureFailure().catch(e => { record.failureCaptureError = String(e); }); }
  finally {
    try { if (context) record.finalBrowserCleanup = await closeBrowser(); }
    catch (e) { record.cleanupFailure = String(e.stack || e); process.exitCode = 1; await captureFailure().catch(() => {}); }
    try { await stopOrigin(); } catch (e) { record.serverCleanupFailure = String(e.stack || e); process.exitCode = 1; }
    if (workflowComplete && !record.cleanupFailure && !record.serverCleanupFailure && !record.failure) { record.result = 'PASS'; record.step = 'PASS: genuine registered saved-PDF association/download online, readonly and whole-browser host-refused cold offline'; }
    flush(); console.log(JSON.stringify({ result: record.result, receipt, step: record.step, failure: record.failure }));
  }
})();
