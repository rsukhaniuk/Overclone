// Focused registered-app proof; ROOT owns publication, locks and execution.
// Required: APP_URL (loopback published upstream), SOURCE_ROOT (matching frozen
// checkout with box contracts), RESULT_DIR (fresh run directory),
// AUTHORING_RUNTIME_DIR (unchanged pins.json + files/), PDF_RUNTIME_DIR (E1
// manifest.json + files/). NODE_PATH may select existing cached Playwright.
// Optional EXPECTED_{PDF_LOADER,PDF_WASM,RUST_JS,RUST_WASM}_SHA256.
// An owned loopback forwarding proxy supplies the browser origin. It preserves
// upstream headers/bytes and is stopped for cold proof; APP_URL is never stopped.
// No external harness IIFE imports, storage seeding, injected providers/results,
// security flags, force clicks, page mutations, native-policy changes or retries.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const http = require('node:http');
const { createHash } = require('node:crypto');
const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const $ = (page, id) => page.locator(`[data-testid="${id}"]`);
const record = { result: 'FAIL', scope: 'four compiler box warnings, not PDF layout/compliance', phases: {}, cleanup: {} };
let config, proxy, profile, active, base, phase = 'configuration', usedBytes = 0, screenshots = 0;
const LIMIT = 16 * 1024 * 1024;
function artifact(name, bytes) {
  assert.match(name, /^[a-z0-9.-]+$/);
  bytes = Buffer.isBuffer(bytes) ? bytes : Buffer.from(bytes);
  const file = path.join(config.result, name), previous = fs.existsSync(file) ? fs.statSync(file).size : 0;
  assert.ok(usedBytes - previous + bytes.length <= LIMIT, '16MiB retained-output budget');
  fs.writeFileSync(file, bytes); usedBytes += bytes.length - previous;
  return { file: name, bytes: bytes.length, sha256: sha(bytes) };
}
function receipt() { if (config?.result) artifact('receipt.json', JSON.stringify(record, null, 2) + '\n'); }
const errorInfo = error => ({ name: error.name, message: error.message, stack: error.stack });
async function reveal(page, target) {
  // Copied narrowly from review-ui-helpers; opens ancestors, NEVER closes them.
  await target.waitFor({ state: 'attached' });
  for (const detail of await target.locator('xpath=ancestor::details').all())
    if (await detail.getAttribute('open') === null) await detail.locator(':scope > summary').click();
}
async function closeProject(page) {
  const menu = page.locator('details.project-menu');
  if (await menu.getAttribute('open') !== null) await menu.locator(':scope > summary').click();
  assert.equal(await menu.getAttribute('open'), null, 'Project overlay closed by normal summary interaction');
}
async function save(page) {
  const saved = () => {
    const status = document.querySelector('.workspace-header .status');
    if (document.querySelector('.banner-readonly, .banner-recovery')
      || status?.classList.contains('status-readonly') || status?.classList.contains('status-failed'))
      throw new Error('Cannot establish Saved state: read-only, recovery or save failure');
    return status?.textContent === 'Saved.' && status.classList.contains('status-saved');
  };
  await page.evaluate(saved); // Reject unsafe states BEFORE considering a click.
  // Clean source correctly disables Save. Disabled alone is NOT persistence proof.
  if (await $(page, 'save').isEnabled()) await $(page, 'save').click();
  await page.waitForFunction(saved); // Exact real status required on both paths.
}
async function head(page, name) {
  // Existing readonly H.head pattern; no product storage writes/private C# calls.
  return page.evaluate(async name => {
    const storage = await import('./js/workspace-storage.js');
    const projects = (await storage.listProjects()).filter(p => p.name === name);
    if (projects.length !== 1) throw new Error('Expected exact synthetic project');
    const project = projects[0], revision = await storage.getRevision(project.headRevisionId);
    return { projectId: project.id, id: revision.id, parent: revision.parentRevisionId,
      files: await Promise.all(revision.files.map(async f => ({ id: f.id, path: f.relativePath,
        bytes: Array.from(await storage.readContent(f.contentHash)) }))) };
  }, name);
}
async function stamp(page) {
  return $(page, 'analysis-panel').evaluate(el => ({ generation: el.dataset.generation, session: el.dataset.session, head: el.dataset.head }));
}
function sourceAt(revision, expected) {
  assert.equal(revision.files.length, 1);
  assert.equal(revision.files[0].path, 'main.tex');
  assert.deepEqual(Buffer.from(revision.files[0].bytes), expected, 'actual saved source bytes');
}
function fixture(marker) {
  return Buffer.from(String.raw`\documentclass{article}
\hbadness=100 \vbadness=100 \hfuzz=0pt \vfuzz=0pt
\begin{document}
\noindent ${marker}\par
\hbox to 1pt{Wide box text}
\hbox to 200pt{A B}
\vbox to 1pt{\hbox{Too tall}}
\vbox to 200pt{\hbox{Tall empty}}
\end{document}
`, 'utf8');
}
function census() {
  // Executable links, not mutable Chromium argv titles (existing authoring proof).
  const found = [];
  for (const pid of fs.readdirSync('/proc').filter(n => /^\d+$/.test(n))) {
    try {
      const exe = path.basename(fs.readlinkSync(`/proc/${pid}/exe`));
      if (/chromium|chrome|headless_shell/i.test(exe)) found.push({ pid, exe,
        startticks: fs.readFileSync(`/proc/${pid}/stat`, 'utf8').split(') ')[1].split(' ')[19] });
    } catch (error) { if (!['ENOENT', 'EACCES', 'ESRCH'].includes(error.code)) throw error; }
  }
  return found;
}
async function noChromium() {
  const until = Date.now() + 10000;
  while (census().length && Date.now() < until) await new Promise(resolve => setTimeout(resolve, 50));
  const remaining = census(); assert.deepEqual(remaining, [], 'every Chromium process closed'); return remaining;
}
async function state(page, id, expected, timeout = 120000) {
  await page.waitForFunction(({ id, expected }) => {
    const el = document.querySelector(`[data-testid="${id}"]`);
    return el?.dataset.busy === 'false' && el.dataset.state === expected;
  }, { id, expected }, { timeout });
}
function readConfig() {
  const required = name => { assert.ok(process.env[name], `${name} must be supplied`); return process.env[name]; };
  const result = path.resolve(required('RESULT_DIR'));
  fs.mkdirSync(result, { recursive: true }); assert.deepEqual(fs.readdirSync(result), [], 'fresh RESULT_DIR required');
  config = { result }; // Even subsequent configuration failures get a FAIL receipt.
  config.upstream = new URL(required('APP_URL'));
  assert.ok(config.upstream.protocol === 'http:' && ['127.0.0.1', 'localhost', '[::1]'].includes(config.upstream.hostname), 'APP_URL must be a root-supplied loopback HTTP published site');
  assert.equal(config.upstream.pathname, '/');
  assert.ok(!config.upstream.username && !config.upstream.password && !config.upstream.search && !config.upstream.hash);
  config.source = path.resolve(required('SOURCE_ROOT'));
  config.authoring = path.resolve(required('AUTHORING_RUNTIME_DIR'));
  config.pdf = path.resolve(required('PDF_RUNTIME_DIR'));
  for (const dir of [config.source, config.authoring, config.pdf]) assert.ok(fs.statSync(dir).isDirectory());
  config.expected = {};
  for (const role of ['PDF_LOADER', 'PDF_WASM', 'RUST_JS', 'RUST_WASM']) {
    const pin = process.env[`EXPECTED_${role}_SHA256`];
    if (pin !== undefined) { assert.match(pin, /^[a-f0-9]{64}$/); config.expected[role] = pin; }
  }
  const contract = fs.readFileSync(path.join(config.source, 'src/Client/Compilation/Diagnostics/CompilerLogContracts.cs'), 'utf8');
  config.warnings = ['OverfullHBox', 'UnderfullHBox', 'OverfullVBox', 'UnderfullVBox'].map(kind => {
    const values = [...contract.matchAll(new RegExp(`CompilerDiagnosticCode\\.${kind}\\s*=>\\s*"([^"\\r\\n]+)"`, 'g'))].map(m => m[1]);
    assert.equal(values.length, 2, `frozen SOURCE_ROOT must contain code + fixed message for ${kind}`);
    assert.match(values[0], /^TEX\d{3}$/); assert.match(values[1], /^The compiler reported an (?:overfull|underfull) (?:horizontal|vertical) box\.$/);
    return { kind, code: values[0], message: values[1] };
  });
  const parser = fs.readFileSync(path.join(config.source, 'src/Client/Compilation/Diagnostics/CompilerLogParser.cs'));
  record.sourceContract = { sha256: sha(Buffer.from(contract)), parserSha256: sha(parser), warnings: config.warnings };
  config.pinsPath = path.join(config.authoring, 'pins.json');
  const pinsBytes = fs.readFileSync(config.pinsPath), pins = JSON.parse(pinsBytes);
  assert.equal(pins.length, 497); assert.equal(new Set(pins.map(p => p.name)).size, 497);
  config.compilerFiles = pins.map(pin => {
    assert.match(pin.name, /^[A-Za-z0-9._-]{1,200}$/); assert.ok(!['.', '..'].includes(pin.name));
    assert.ok(Number.isSafeInteger(pin.length) && pin.length >= 0); assert.match(pin.sha256, /^[a-f0-9]{64}$/);
    const file = path.join(config.authoring, 'files', pin.name), bytes = fs.readFileSync(file);
    assert.equal(bytes.length, pin.length); assert.equal(sha(bytes), pin.sha256, pin.name); return file;
  });
  const closure = sha(Buffer.from([...pins].sort((a, b) => a.name < b.name ? -1 : a.name > b.name ? 1 : 0)
    .map(p => `${p.name}\t${p.length}\t${p.sha256}`).join('\n')));
  assert.equal(closure, '0f6b7b885ff15fb3bcd11a49140d9f486406775fe0222f5925709eb5c9680a2e', 'unchanged accepted 497 fixture');
  config.pdfManifestPath = path.join(config.pdf, 'manifest.json');
  const pdfBytes = fs.readFileSync(config.pdfManifestPath), pdf = JSON.parse(pdfBytes);
  assert.equal(pdf.version, 1); assert.equal(pdf.resources.length, 2);
  assert.deepEqual(pdf.resources.map(r => r.role).sort(), ['loader', 'wasm']);
  config.pdfFiles = pdf.resources.map(pin => {
    assert.match(pin.name, /^[A-Za-z0-9._-]{1,200}$/); assert.ok(!['.', '..'].includes(pin.name));
    assert.ok(Number.isSafeInteger(pin.length) && pin.length > 0); assert.match(pin.sha256, /^[a-f0-9]{64}$/);
    const file = path.join(config.pdf, 'files', pin.name), bytes = fs.readFileSync(file);
    assert.equal(bytes.length, pin.length); assert.equal(sha(bytes), pin.sha256);
    const expected = config.expected[`PDF_${pin.role.toUpperCase()}`]; if (expected) assert.equal(pin.sha256, expected);
    return file;
  });
  record.resources = { compilerCount: pins.length, compilerBytes: pins.reduce((n, p) => n + p.length, 0), closure,
    pinsSha256: sha(pinsBytes), pdfManifestSha256: sha(pdfBytes), pdfResources: pdf.resources };
}
async function manifest() {
  const response = await fetch(new URL('service-worker-assets.js', config.upstream), { signal: AbortSignal.timeout(15000), redirect: 'error' });
  assert.equal(response.status, 200);
  const bytes = Buffer.from(await response.arrayBuffer()); assert.ok(bytes.length <= 1024 * 1024);
  const match = bytes.toString('utf8').match(/self\.assetsManifest\s*=\s*(\{[\s\S]*\})\s*;/);
  assert.ok(match, 'actual generated service-worker-assets manifest, never eval');
  const value = JSON.parse(match[1]); assert.ok(value.version && Array.isArray(value.assets) && value.assets.length > 0);
  config.assets = new Map();
  for (const asset of value.assets) {
    assert.ok(typeof asset.url === 'string' && !asset.url.startsWith('/') && !asset.url.includes('..'));
    const url = new URL(asset.url, 'http://manifest.invalid/'); assert.equal(url.origin, 'http://manifest.invalid');
    assert.match(asset.hash, /^sha256-[A-Za-z0-9+/]{43}=$/); assert.ok(!config.assets.has(url.pathname));
    config.assets.set(url.pathname, asset.hash);
  }
  config.assets.set('/service-worker-assets.js', 'sha256-' + createHash('sha256').update(bytes).digest('base64'));
  record.publication = { version: value.version, manifestSha256: sha(bytes), assets: value.assets.length };
}
async function startProxy() {
  proxy = http.createServer((request, response) => {
    const target = new URL(request.url, config.upstream); if (target.origin !== config.upstream.origin) { response.writeHead(400); response.end(); return; }
    const forwarded = http.request(target, { method: request.method, headers: { ...request.headers, host: target.host } }, upstream => {
      response.writeHead(upstream.statusCode, upstream.headers); upstream.pipe(response);
    });
    const deadline = setTimeout(() => forwarded.destroy(new Error('upstream deadline')), 15000);
    forwarded.on('error', () => { if (!response.headersSent) response.writeHead(502); response.end(); });
    response.on('close', () => { clearTimeout(deadline); forwarded.destroy(); }); request.pipe(forwarded);
  });
  await new Promise((resolve, reject) => { proxy.once('error', reject); proxy.listen(0, '127.0.0.1', resolve); });
  base = `http://127.0.0.1:${proxy.address().port}`;
}
async function stopProxy() {
  if (proxy?.listening) await new Promise((resolve, reject) => { proxy.close(error => error ? reject(error) : resolve()); proxy.closeIdleConnections(); });
}
async function refused() {
  await new Promise((resolve, reject) => {
    const req = http.get(base, response => { response.resume(); reject(new Error('owned origin still reachable')); });
    req.on('error', error => error.code === 'ECONNREFUSED' ? resolve() : reject(error));
    req.setTimeout(2000, () => req.destroy(new Error('ECONNREFUSED not proven')));
  });
}
async function launch(offline) {
  const { chromium } = require('playwright'); // Cached dependency only; config/fixtures validated first.
  const context = await chromium.launchPersistentContext(profile, { headless: true, offline, viewport: { width: 1280, height: 900 } });
  const captured = { offlineBeforeNavigation: offline, loaded: [], workers: [], errors: [], consoleErrors: [], cspConsoleErrors: [], violations: [], externalAttempts: 0, externalSuccesses: 0, captureErrors: [] };
  const pending = new Set();
  active = { context, captured, pending, page: null, stop: null };
  record.phases[phase] = captured;
  await context.route('**/*', route => new URL(route.request().url()).origin === base ? route.continue() : route.abort());
  await context.addInitScript(() => {
    window.__boxCspEvidence = [];
    addEventListener('securitypolicyviolation', event => window.__boxCspEvidence.push(event.effectiveDirective));
  });
  const onRequest = request => { const url = new URL(request.url()); if (/^https?:$/.test(url.protocol) && url.origin !== base) captured.externalAttempts++; };
  const onResponse = response => {
    const url = new URL(response.url());
    if (/^https?:$/.test(url.protocol) && url.origin !== base && response.ok()) captured.externalSuccesses++;
    if (url.origin !== base) return;
    const key = url.pathname === '/' ? '/index.html' : url.pathname, expected = config.assets.get(key);
    if (!expected || response.status() !== 200) return;
    const work = (async () => {
      const bytes = await response.body(), digest = sha(bytes);
      assert.equal('sha256-' + createHash('sha256').update(bytes).digest('base64'), expected, `loaded asset ${key}`);
      for (const [role, suffix] of [['RUST_JS', '/document_engine.js'], ['RUST_WASM', '/document_engine_bg.wasm']])
        if (config.expected[role] && key.endsWith(suffix)) assert.equal(digest, config.expected[role], role);
      assert.ok(captured.loaded.length < 2048, 'bounded loaded response records');
      captured.loaded.push({ path: key, sha256: digest, bytes: bytes.length, fromServiceWorker: response.fromServiceWorker() });
    })();
    pending.add(work); work.catch(error => captured.captureErrors.push(errorInfo(error))).finally(() => pending.delete(work));
  };
  const pageHandlers = [];
  const onPage = page => {
    const onError = error => captured.errors.push(errorInfo(error));
    const onConsole = message => {
      if (message.type() !== 'error') return;
      const text = message.text(); captured.consoleErrors.push(text);
      if (/content security policy|violates? .*?(?:script-src|worker-src|connect-src)|refused to .*?because/i.test(text)) captured.cspConsoleErrors.push(text);
    }; // Retain ordinary native stderr; it is not automatically a page exception.
    const onWorker = worker => captured.workers.push(worker.url());
    page.on('pageerror', onError); page.on('console', onConsole); page.on('worker', onWorker);
    pageHandlers.push({ page, onError, onConsole, onWorker });
  };
  context.on('request', onRequest); context.on('response', onResponse); context.on('page', onPage);
  active.stop = async () => {
    context.off('request', onRequest); context.off('response', onResponse); context.off('page', onPage);
    await Promise.allSettled([...pending]); // Stop admission BEFORE actual body drain, never swallow into PASS.
    captured.responseObserverDetached = true; captured.responseBodyDrain = pending.size;
    for (const { page, onError, onConsole, onWorker } of pageHandlers) {
      captured.violations.push(...await page.evaluate(() => window.__boxCspEvidence || []));
      page.off('pageerror', onError); page.off('console', onConsole); page.off('worker', onWorker);
    }
    captured.pageObserversDetached = true;
    assert.equal(pending.size, 0); assert.deepEqual(captured.captureErrors, []);
  };
  const page = await context.newPage(); active.page = page; page.setDefaultTimeout(30000);
  await page.goto(base, { waitUntil: 'domcontentloaded', timeout: 60000 });
  await page.locator('.project-dashboard').waitFor({ timeout: 60000 });
  await page.waitForFunction(() => navigator.serviceWorker.controller?.state === 'activated', null, { timeout: 60000 });
  return page;
}
async function shutdownContext() {
  if (!active) return;
  const current = active;
  try { if (current.stop) await current.stop(); }
  finally { await current.context.close(); active = null; }
}
async function screenshot(page, label) {
  if (screenshots < 2) { screenshots++; return artifact(`${label}.png`, await page.screenshot({ fullPage: false, timeout: 10000 })); }
}
async function download(page, id, label) {
  await reveal(page, $(page, id)); await closeProject(page);
  const event = page.waitForEvent('download', { timeout: 30000 });
  const observed = event.then(value => ({ value }), error => ({ error }));
  try {
    // Shorter click deadline keeps its actual Error; event timeout cannot mask it.
    await $(page, id).click({ timeout: 10000 });
    const outcome = await observed; if (outcome.error) throw outcome.error;
    const item = outcome.value; assert.equal(await item.failure(), null);
    const file = await item.path(); assert.ok(file, 'actual browser download file');
    return artifact(label, fs.readFileSync(file));
  } catch (error) { record.downloadFailure = { phase, control: id, ...errorInfo(error) }; receipt(); throw error; }
  finally { await observed; }
}
async function tools(page, recheck = false) {
  await reveal(page, $(page, 'refresh-compile-tools'));
  await page.waitForFunction(() => document.querySelector('[data-testid=refresh-compile-tools]')?.disabled === false, null, { timeout: 60000 });
  if (recheck) for (const id of ['runtime', 'pdf-runtime']) {
    await reveal(page, $(page, `${id}-recheck`)); await $(page, `${id}-recheck`).click();
    await state(page, `${id}-status`, 'Installed');
  }
  await $(page, 'refresh-compile-tools').click();
  await page.waitForFunction(() => document.querySelector('[data-testid=compiler-readiness]')?.textContent.startsWith('Local compiler checked'), null, { timeout: 60000 });
}
async function exercise(page, name, bytes, marker) {
  const evidence = record.phases[phase];
  await page.locator('[data-testid=select-file]', { hasText: 'main.tex' }).click();
  await reveal(page, $(page, 'refresh-compile-tools'));
  await page.locator('select[aria-label="Compile main file"]').selectOption({ label: 'main.tex' });
  await page.locator('select[aria-label="Compile engine"]').selectOption('local-authoring-v4');
  await require("./review-ui-helpers.cjs").bibliography(page, "None");
  await save(page);
  const before = await head(page, name), authority = await stamp(page); sourceAt(before, bytes);
  assert.ok(authority.session && authority.head && /^\d+$/.test(authority.generation), 'visible source authority');
  assert.equal(authority.head.replaceAll('-', '').toLowerCase(), before.id.replaceAll('-', '').toLowerCase());
  evidence.source = artifact(`${phase}.tex`, bytes); evidence.savedHead = before; evidence.authority = authority;
  await reveal(page, $(page, 'analyze-now')); const analysisStart = Date.now(); await $(page, 'analyze-now').click();
  await page.waitForFunction(() => document.querySelector('[data-testid=analysis-status]')?.textContent === 'Analysis complete.');
  evidence.analysisElapsedMs = Date.now() - analysisStart;
  assert.deepEqual(await head(page, name), before); assert.deepEqual(await stamp(page), authority);
  await closeProject(page); const start = Date.now(); await $(page, 'compile').click();
  await page.waitForFunction(() => /^(Compilation finished\.|No new PDF:)/.test(document.querySelector('[data-testid=compile-status]')?.textContent || ''), null, { timeout: 120000 });
  evidence.compileElapsedMs = Date.now() - start;
  evidence.compileStatus = await $(page, 'compile-status').textContent();
  evidence.log = artifact(`${phase}.log`, await $(page, 'compiler-log').textContent()); // Preserve real log BEFORE any grammar assertion.
  const details = await $(page, 'compile-details').textContent(); evidence.details = details;
  assert.match(evidence.compileStatus, /^Compilation finished\./); assert.match(details, /Outcome: Succeeded/);
  const outputHash = await $(page, 'compile-panel').getAttribute('data-output-hash'); assert.match(outputHash, /^[a-f0-9]{64}$/);
  evidence.outputHash = outputHash; evidence.request = details.match(/Request: ([a-f0-9]{32});/)?.[1];
  evidence.inputDigest = details.match(/Input: ([a-f0-9]{64});/)?.[1]; evidence.epoch = details.match(/epoch: (\d+)/)?.[1];
  assert.ok(evidence.request && evidence.inputDigest && evidence.epoch); assert.ok(details.includes(`PDF: ${outputHash}`));
  assert.equal(await $(page, 'compile-panel').getAttribute('data-is-current'), 'true');
  const log = fs.readFileSync(path.join(config.result, evidence.log.file), 'utf8');
  evidence.warningFindings = [];
  for (const warning of config.warnings) {
    const horizontal = warning.kind.endsWith('HBox'), over = warning.kind.startsWith('Overfull');
    assert.ok(log.includes(`${over ? 'Overfull' : 'Underfull'} \\${horizontal ? 'hbox' : 'vbox'}`), `real native log ${warning.kind}`);
    const rows = $(page, 'compiler-finding').filter({ hasText: warning.message }); const count = await rows.count();
    assert.ok(count > 0, `required actual UI warning ${warning.kind}; see retained native log`);
    for (const row of await rows.all()) {
      const text = (await row.textContent()).trim(); assert.ok(text.startsWith(`Warning: ${warning.message}`));
      assert.equal(await row.locator('button').count(), 0, 'box warning offers NO source action');
      evidence.warningFindings.push({ ...warning, uiText: text, severity: 'Warning', sourceActions: 0 });
    }
  }
  await page.waitForFunction(() => document.querySelector('[data-testid=preview-status]')?.dataset.previewStatus === 'Available'
    && /Drawn/.test(document.querySelector('[data-testid=canvas-status]')?.textContent || '')
    && document.querySelector('[data-testid=pdf-pane]')?.dataset.document, null, { timeout: 120000 });
  evidence.pageLabel = await $(page, 'page-label').textContent(); assert.match(evidence.pageLabel, /Page 1 of 1/);
  evidence.text = await $(page, 'page-text').textContent(); assert.ok(evidence.text.includes(marker), 'real rendered PDF marker text');
  evidence.inkPixels = await page.evaluate(() => {
    const canvas = document.querySelector('[data-testid=pdf-canvas]'); if (!canvas?.width || !canvas.height) return 0;
    const data = canvas.getContext('2d').getImageData(0, 0, canvas.width, canvas.height).data; let ink = 0;
    for (let i = 0; i < data.length; i += 4) if (data[i] < 250 || data[i + 1] < 250 || data[i + 2] < 250) ink++;
    return ink;
  });
  assert.ok(evidence.inkPixels > 0, 'nonblank actual renderer canvas, not physical layout measurement');
  evidence.screenshot = await screenshot(page, phase);
  evidence.sourceDownload = await download(page, 'download', `${phase}.download.tex`);
  assert.equal(evidence.sourceDownload.sha256, sha(bytes)); assert.equal(evidence.sourceDownload.bytes, bytes.length);
  // Restore the disclosure used for source export; no claim it caused run01.
  const fileMenu = page.locator('.editor-toolbar details.file-menu');
  if (await fileMenu.getAttribute('open') !== null) await fileMenu.locator(':scope > summary').click();
  assert.equal(await fileMenu.getAttribute('open'), null, 'File actions closed normally before toolbar PDF download');
  evidence.pdfDownload = await download(page, 'download-pdf', `${phase}.pdf`);
  assert.equal(evidence.pdfDownload.sha256, outputHash);
  assert.equal(fs.readFileSync(path.join(config.result, evidence.pdfDownload.file)).subarray(0, 5).toString(), '%PDF-');
  assert.equal(await $(page, 'compile-panel').getAttribute('data-output-hash'), outputHash);
  assert.equal(await $(page, 'compile-panel').getAttribute('data-is-current'), 'true');
  assert.equal(await $(page, 'editor').inputValue(), bytes.toString());
  assert.deepEqual(await head(page, name), before, 'checks/compile/preview/download do not edit/save source');
  assert.deepEqual(await stamp(page), authority);
  assert.ok(evidence.workers.some(url => /\/__oop\/native-worker\/compile\b/.test(url)), 'actual registered compile worker');
  assert.ok(evidence.workers.some(url => /\/__oop\/native-worker\/pdf\b/.test(url)), 'actual registered PDF worker');
  receipt(); return evidence;
}
function verifyPhase(evidence) {
  assert.deepEqual(evidence.captureErrors, []); assert.deepEqual(evidence.errors, []);
  assert.deepEqual(evidence.violations, []); assert.deepEqual(evidence.cspConsoleErrors, []);
  assert.equal(evidence.externalSuccesses, 0); assert.equal(evidence.externalAttempts, 0);
  assert.ok(evidence.loaded.some(row => /\/.*\.wasm$/.test(row.path)), 'actual loaded WASM hashed against current manifest');
  for (const [role, suffix] of [['RUST_JS', '/document_engine.js'], ['RUST_WASM', '/document_engine_bg.wasm']])
    if (config.expected[role]) assert.ok(evidence.loaded.some(row => row.path.endsWith(suffix) && row.sha256 === config.expected[role]), `loaded ${role} pin`);
}
async function main() {
  readConfig(); receipt(); await manifest(); assert.deepEqual(census(), [], 'root must coordinate exclusive browser slot');
  profile = fs.mkdtempSync(path.join(os.tmpdir(), 'compiler-box-synthetic-'));
  await startProxy(); phase = 'online'; let page = await launch(false);
  const name = `CompilerBox-${Date.now()}`; record.projectName = name;
  await reveal(page, page.locator('#new-project')); await page.locator('#new-project').fill(name);
  await page.getByRole('button', { name: 'Create project', exact: true }).click();
  const onlineSource = fixture('BOXONLINE42');
  await $(page, 'import-input').setInputFiles({ name: 'main.tex', mimeType: 'text/plain', buffer: onlineSource });
  await page.locator('.editor-toolbar label', { hasText: 'main.tex' }).waitFor(); await save(page);
  for (const [id, manifestId, manifestPath, filesId, files] of [
    ['runtime', 'runtime-pins', config.pinsPath, 'runtime-files', config.compilerFiles],
    ['pdf-runtime', 'pdf-runtime-manifest', config.pdfManifestPath, 'pdf-runtime-files', config.pdfFiles],
  ]) {
    await reveal(page, $(page, `${id}-recheck`)); await $(page, `${id}-recheck`).click(); await state(page, `${id}-status`, 'Empty');
    await $(page, manifestId).setInputFiles(manifestPath); await $(page, filesId).setInputFiles(files);
    await $(page, `${id}-install`).click(); await state(page, `${id}-status`, 'Installed', 180000);
    assert.equal(record.phases.online.workers.filter(url => /\/__oop\/native-worker\//.test(url)).length, 0, 'resource install does not execute native workers');
  }
  await tools(page); const online = await exercise(page, name, onlineSource, 'BOXONLINE42');
  record.cold = { beforeClose: census() }; assert.ok(record.cold.beforeClose.length > 0);
  await shutdownContext(); verifyPhase(online); record.cold.afterClose = await noChromium();
  await stopProxy(); await refused(); record.cold.originECONNREFUSED = true;
  phase = 'cold-offline'; page = await launch(true); record.cold.afterRelaunch = census();
  const row = $(page, 'project-row').filter({ hasText: name }); await row.waitFor();
  assert.equal(await row.getAttribute('data-name'), name);
  assert.equal((await row.getAttribute('data-project-id')).replaceAll('-', '').toLowerCase(), online.savedHead.projectId.replaceAll('-', '').toLowerCase());
  await row.locator('[data-testid=open-project]').click();
  await page.locator('.editor-toolbar label', { hasText: 'main.tex' }).waitFor();
  assert.deepEqual(await head(page, name), online.savedHead, 'whole-browser cold persisted source/head');
  assert.equal(await $(page, 'editor').inputValue(), onlineSource.toString());
  const coldSource = fixture('BOXOFFLINE84'); await $(page, 'editor').fill(coldSource.toString()); await save(page);
  await tools(page, true); const cold = await exercise(page, name, coldSource, 'BOXOFFLINE84');
  assert.notEqual(cold.source.sha256, online.source.sha256); assert.notEqual(cold.savedHead.id, online.savedHead.id);
  assert.notEqual(cold.authority.session, online.authority.session); assert.notEqual(cold.request, online.request);
  assert.notEqual(cold.inputDigest, online.inputDigest); assert.notEqual(cold.outputHash, online.outputHash, 'changed visible marker yields genuinely fresh PDF');
  assert.ok(!cold.text.includes('BOXONLINE42')); await shutdownContext(); verifyPhase(cold);
  record.cleanup.finalChromium = await noChromium(); record.result = 'PASS';
}
(async () => {
  try { await main(); }
  catch (error) {
    record.result = 'FAIL'; record.failure = { phase, ...errorInfo(error) };
    if (active?.page) {
      try {
        const page = active.page;
        record.failure.ui = { url: page.url(), compile: await $(page, 'compile-status').textContent({ timeout: 2000 }),
          download: await $(page, 'pdf-download-status').textContent({ timeout: 2000 }) };
        record.failure.log = artifact('failure.log', await $(page, 'compiler-log').textContent({ timeout: 2000 }));
        record.failure.screenshot = await screenshot(page, 'failure');
      } catch (captureError) { record.failure.captureError = errorInfo(captureError); }
    }
  } finally {
    for (const [key, cleanup] of [['browser', shutdownContext], ['proxy', stopProxy]]) {
      try { await cleanup(); record.cleanup[key] = true; }
      catch (error) { record.cleanup[key] = errorInfo(error); record.result = 'FAIL'; }
    }
    if (profile) {
      try { record.cleanup.finalChromium = await noChromium(); fs.rmSync(profile, { recursive: true }); record.cleanup.syntheticProfileRemoved = true; }
      catch (error) { record.cleanup.profileError = errorInfo(error); record.result = 'FAIL'; }
    }
    try { receipt(); } catch (error) { console.error('Receipt write failed:', error.message); record.result = 'FAIL'; }
    console.log(JSON.stringify({ result: record.result, phase, failure: record.failure?.message || null }));
    process.exitCode = record.result === 'PASS' ? 0 : 1;
  }
})();
