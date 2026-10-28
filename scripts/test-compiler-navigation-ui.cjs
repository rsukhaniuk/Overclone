// Actual published-app compiler diagnostic -> source LINE navigation acceptance.
// Root owns all execution/publication. This script drives ordinary UI only; it
// never runs test-local-authoring-ui.cjs as a library, injects results/storage,
// substitutes product navigation, or provisions a renderer. After failed-attempt
// cold navigation it repairs the controlled command, saves, and requires a real
// successful offline compile + exact accepted-PDF browser download. Source/PDF mapping
// and held foreign-IO races are NOT tested here (managed contracts cover races).
// Required: PUBLISHED_WWWROOT, AUTHORING_RUNTIME_DIR (497 pins.json + files/),
// RESULT_DIR (writable, exclusively owned by the caller). No PDF_RUNTIME_DIR.
// Run once in the cached isolated Playwright container; no retries or skips.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const http = require("node:http");
const { createHash } = require("node:crypto");
const { chromium } = require("playwright");
const H = require("./review-ui-helpers.cjs");
const { validate: validateCsp } = require("./test-csp.cjs");

const sha = b => createHash("sha256").update(b).digest("hex");
const need = name => { assert.ok(process.env[name], `${name} required`); return process.env[name]; };
const RESULT_DIR = path.resolve(need("RESULT_DIR"));
assert.ok(fs.statSync(RESULT_DIR).isDirectory(), "existing writable RESULT_DIR required");
const RECEIPT = path.join(RESULT_DIR, "compiler-navigation-receipt.json");
assert.ok(!fs.existsSync(RECEIPT), "refuse to overwrite an earlier run's receipt");
const WWWROOT = path.resolve(need("PUBLISHED_WWWROOT"));
const RUNTIME = path.resolve(need("AUTHORING_RUNTIME_DIR"));
const PORT = 8771;
const NATIVE = /\/__oop\/native-worker\/(compile|pdf)\b/;
const COMPILE = /\/__oop\/native-worker\/compile\b/;
const PDF = /\/__oop\/native-worker\/pdf\b/;
const PROFILE = "8e9071ff18e5f93203266e7a85a6c4bba0597f9fba2e692c5651f68997e31894";
// CRLF intentionally differs from textarea DOM normalization; Ukrainian/emoji
// are comments only, so this fixture needs no Cyrillic glyph/font closure.
const MAIN = Buffer.from("\\documentclass[10pt]{article}\r\n\\begin{document}\r\n\\input{chapter}\r\n\\end{document}\r\n", "utf8");
const CHAPTER = Buffer.from("% Україна 😀\r\n\\noindent Ordinary ASCII paragraph.\\par\r\n\\overcloneUndefinedCommand % Київ 😀\r\n", "utf8");
const ORIGINAL = { "main.tex": MAIN, "chapter.tex": CHAPTER };
const identity = bytes => ({ bytes: bytes.length, sha256: sha(bytes) });
const fixtureIdentities = files => Object.fromEntries(Object.entries(files).map(([n, b]) => [n, identity(b)]));
const workers = [], pageErrors = [], violations = [], externalAttempts = [], externalSuccesses = [];
const nativeCount = () => workers.filter(w => NATIVE.test(w.url)).length;
const record = {
  result: "FAIL", scope: "compiler-diagnostic-source-line-online-whole-browser-cold-offline",
  actualBrowserExecution: "started by caller", sdk: null, compiler: null,
  fixtures: { original: fixtureIdentities(ORIGINAL) }, online: {}, coldOffline: {},
  failure: null, workers, pageErrors, violations, externalAttempts, externalSuccesses,
  limitations: ["Line-level, not token/column or source/PDF synchronization.",
    "No renderer provisioned; failed-navigation proof precedes separate successful repair/compile/download proof.",
    "No delayed/foreign callback or held-IO authority race manufactured; managed tests cover those."],
};
let phase = "preflight", context = null, page = null, server = null, profile = null, base = null, launching = null;
let cancelled = false;
const ensureRunning = () => { if (cancelled) throw new Error("Harness deadline/cleanup reached"); };
function save() { fs.writeFileSync(RECEIPT, JSON.stringify(record, null, 2) + "\n"); }
async function bounded(promise, ms, label) {
  let timer;
  try { return await Promise.race([promise, new Promise((_, reject) => { timer = setTimeout(() => reject(new Error(label)), ms); })]); }
  finally { clearTimeout(timer); }
}
function fail(error) {
  if (!record.failure) record.failure = { phase, message: String(error.message || error).slice(0, 1024) };
  record.result = "FAIL";
}
const TYPES = { ".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8",
  ".mjs": "text/javascript; charset=utf-8", ".css": "text/css; charset=utf-8",
  ".json": "application/json; charset=utf-8", ".wasm": "application/wasm",
  ".png": "image/png", ".svg": "image/svg+xml", ".ico": "image/x-icon",
  ".txt": "text/plain; charset=utf-8", ".dat": "application/octet-stream",
  ".blat": "application/octet-stream", ".pdb": "application/octet-stream" };
function within(root, file) { return file.startsWith(root + path.sep); }
function preflight() {
  const headers = fs.readFileSync(path.join(__dirname, "../docs/examples/static-csp-headers.conf"), "utf8");
  const html = fs.readFileSync(path.join(WWWROOT, "index.html"), "utf8");
  validateCsp(html, headers);
  const header = headers.split("\n").map(s => s.trim()).find(s => s.startsWith("Content-Security-Policy:"));
  assert.ok(header, "strict CSP header required");
  const text = fs.readFileSync(path.join(WWWROOT, "service-worker-assets.js"), "utf8");
  const match = text.match(/self\.assetsManifest\s*=\s*(\{[\s\S]*\})\s*;/);
  assert.ok(match, "SDK manifest JSON envelope");
  const manifest = JSON.parse(match[1]);
  assert.equal(typeof manifest.version, "string");
  assert.ok(manifest.assets.length > 0 && manifest.assets.length <= 4096);
  const root = fs.realpathSync(WWWROOT), names = new Set();
  for (const asset of manifest.assets) {
    assert.equal(typeof asset.url, "string");
    assert.ok(!path.isAbsolute(asset.url) && !asset.url.includes("\\") && !asset.url.split("/").includes(".."));
    assert.ok(!names.has(asset.url), "no duplicate SDK asset"); names.add(asset.url);
    const file = fs.realpathSync(path.join(root, asset.url));
    assert.ok(within(root, file), "SDK asset stays inside publication");
    assert.equal("sha256-" + createHash("sha256").update(fs.readFileSync(file)).digest("base64"), asset.hash, `SDK hash ${asset.url}`);
  }
  record.sdk = { version: manifest.version, assets: manifest.assets.length, allAssetHashesVerified: true, cspValid: true };
  const pinsBytes = fs.readFileSync(path.join(RUNTIME, "pins.json"));
  const pins = JSON.parse(pinsBytes);
  assert.equal(pins.length, 497, "exact current authoring inventory");
  const unique = new Set(); let total = 0;
  for (const pin of pins) {
    assert.match(pin.name, /^[A-Za-z0-9._-]{1,200}$/); assert.ok(![".", ".."].includes(pin.name));
    assert.ok(!unique.has(pin.name)); unique.add(pin.name);
    assert.ok(Number.isSafeInteger(pin.length) && pin.length > 0 && pin.length <= 33554432);
    total += pin.length; assert.ok(total <= 67108864, "compiler closure <=64MiB");
    assert.match(pin.sha256, /^[0-9a-f]{64}$/);
    const file = path.join(RUNTIME, "files", pin.name);
    assert.equal(fs.statSync(file).size, pin.length, `compiler stat ${pin.name}`);
    assert.equal(sha(fs.readFileSync(file)), pin.sha256, `compiler pin ${pin.name}`);
  }
  const pin = name => { const p = pins.find(p => p.name === name); assert.ok(p); return p; };
  assert.equal(pin("engine.js").sha256, "39384532be2e32fd09dba99226db9da7572032c70da2bb2428fc9123c7fd163f");
  assert.equal(pin("engine.wasm").sha256, "7d239273b63b42d0c8f31d5e198a221c200c222f365760c20b9178408077048e");
  assert.equal(pin("pdflatex.fmt").sha256, "e2b5174b5aa6e552e03e9cf5dae7a7594e68d75962f48a6a30dc55da588b2595");
  record.compiler = { pins: pins.length, pinsSha256: sha(pinsBytes), allPinsVerified: true,
    engine: pin("engine.js"), wasm: pin("engine.wasm"), format: pin("pdflatex.fmt"), expectedProfile2Identity: PROFILE };
  return { headers, csp: header.slice("Content-Security-Policy:".length).trim(), root,
    pinsPath: path.join(RUNTIME, "pins.json"), files: pins.map(p => path.join(RUNTIME, "files", p.name)) };
}
function chromiumCensus() {
  const entries = [];
  for (const n of fs.readdirSync("/proc")) {
    if (!/^\d+$/.test(n)) continue;
    let exe; try { exe = path.basename(fs.readlinkSync(`/proc/${n}/exe`)); } catch { continue; }
    if (!/chromium|chrome|headless_shell/i.test(exe)) continue;
    // Startticks is field22; fields after the final ') ' start at field3.
    let stat;
    try { stat = fs.readFileSync(`/proc/${n}/stat`, "utf8"); }
    catch (error) { if (error.code === "ENOENT" || error.code === "ESRCH") continue; throw error; }
    const fields = stat.slice(stat.lastIndexOf(") ") + 2).split(" ");
    assert.match(fields[19], /^\d+$/);
    entries.push({ pid: Number(n), startticks: fields[19], exe });
  }
  return entries.sort((a, b) => a.pid - b.pid);
}
async function reap() {
  let census;
  for (let i = 0; i < 40; i++) {
    census = chromiumCensus(); if (census.length === 0) return census;
    await new Promise(r => setTimeout(r, 100));
  }
  assert.deepEqual(census, [], "ALL Chromium processes must close"); return census;
}
async function stopOrigin() {
  if (!server?.listening) return;
  await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error("Origin close timeout")), 5000);
    server.close(() => { clearTimeout(timer); resolve(); });
    server.closeIdleConnections();
  });
}
async function originRefused() {
  await new Promise((resolve, reject) => {
    const request = http.get(base, response => { response.resume(); reject(new Error("Origin still accepts connections")); });
    request.on("error", error => error.code === "ECONNREFUSED" ? resolve() : reject(error));
    request.setTimeout(2000, () => request.destroy(new Error("Origin refusal not proven")));
  });
}
async function launch(offline) {
  ensureRunning();
  launching = chromium.launchPersistentContext(profile, { headless: true, offline, timeout: 15000 });
  context = await launching; launching = null;
  ensureRunning();
  await context.route("**/*", route => {
    const url = new URL(route.request().url());
    if (url.origin === base) return route.continue();
    externalAttempts.push({ phase, origin: url.origin }); return route.abort();
  });
  // Observation only: no product state, navigation handler, storage or API override.
  await context.addInitScript(() => {
    window.__compilerNavCsp = [];
    addEventListener("securitypolicyviolation", e => window.__compilerNavCsp.push(e.effectiveDirective));
  });
  const watch = p => {
    p.on("worker", worker => workers.push({ phase, url: worker.url() }));
    p.on("pageerror", error => pageErrors.push({ phase, message: String(error.message).slice(0, 1024) }));
  };
  context.pages().forEach(watch); context.on("page", watch);
  context.on("response", response => {
    if (new URL(response.url()).origin !== base && response.ok()) externalSuccesses.push({ phase, url: response.url().slice(0, 256) });
  });
  page = await context.newPage(); page.setDefaultTimeout(30000);
  return page;
}
async function collectCsp() {
  if (!context) return;
  for (const p of context.pages()) {
    if (new URL(p.url()).origin === base)
      violations.push(...(await p.evaluate(() => window.__compilerNavCsp || [])).map(directive => ({ phase, directive })));
  }
}
async function resourceState(id, expected, timeout = 60000) {
  await page.waitForFunction(testid => document.querySelector(`[data-testid="${testid}"]`)?.dataset.busy === "false", id, { timeout });
  assert.equal(await H.$(page, id).getAttribute("data-state"), expected, `${id}: ${await H.$(page, id).textContent()}`);
}
async function waitTools() {
  await H.reveal(page, H.$(page, "refresh-compile-tools"));
  await page.waitForFunction(() => document.querySelector('[data-testid="refresh-compile-tools"]')?.disabled === false, null, { timeout: 60000 });
}
async function checkTools() {
  await waitTools(); await H.$(page, "refresh-compile-tools").click();
  await page.waitForFunction(() => document.querySelector('[data-testid="compiler-readiness"]')?.textContent.startsWith("Local compiler checked"), null, { timeout: 60000 });
}
async function noRenderer() {
  const count = nativeCount();
  await H.reveal(page, H.$(page, "pdf-runtime-recheck")); await H.$(page, "pdf-runtime-recheck").click();
  await resourceState("pdf-runtime-status", "Empty");
  assert.equal(nativeCount(), count, "PDF absence recheck starts no native worker");
  // ConfiguredPorts may contain a LocalPdfInspector even with an Empty store.
  // This text is informational, not evidence of installed renderer resources.
  (record.rendererRechecks ??= []).push({ phase, state: "Empty", nativeCount: count,
    hint: (await H.$(page, "renderer-readiness").textContent()).slice(0, 1024) });
}
async function selectFile(name) {
  await H.$(page, "select-file").filter({ hasText: name }).first().click();
  await page.locator('.editor-toolbar label[for="editor"]', { hasText: name }).waitFor();
  return H.$(page, "editor");
}
async function setupSelection() {
  await H.reveal(page, H.$(page, "refresh-compile-tools"));
  await selectFile("main.tex");
  await page.locator('select[aria-label="Compile main file"]').selectOption({ label: "main.tex" });
  await page.locator('select[aria-label="Compile engine"]').selectOption("local-authoring-v4");
  const bib = H.$(page, "use-bibtex"); await bib.waitFor();
  if (await bib.isChecked()) await bib.uncheck();
  assert.equal(await bib.isChecked(), false);
  await page.waitForFunction(() => document.querySelector('[data-testid="compile"]')?.disabled === false);
}
function actions() { return H.$(page, "compiler-go-to-source"); }
function targetAction() { return actions().filter({ hasText: "chapter.tex:3 (line)" }); }
async function assertStale() {
  await page.waitForFunction(() => {
    const buttons = [...document.querySelectorAll('[data-testid="compiler-go-to-source"]')];
    const text = document.querySelector('[data-testid="compiler-diagnostics"]')?.textContent || "";
    return buttons.every(b => b.disabled) && /previous attempt|previous or superseded attempt/.test(text);
  });
  // Availability comes from real diagnostic buttons, never PDF data-is-current.
  const enabled = await actions().evaluateAll(buttons => buttons.filter(b => !b.disabled).length);
  assert.equal(enabled, 0);
}
async function configuration() {
  return page.evaluate(() => ({
    main: document.querySelector('select[aria-label="Compile main file"]')?.value,
    engine: document.querySelector('select[aria-label="Compile engine"]')?.value,
    bibtex: document.querySelector('[data-testid="use-bibtex"]')?.checked,
    selectionVersion: document.querySelector('[data-testid="compile-panel"]')?.dataset.selectionVersion,
    compileStatus: document.querySelector('[data-testid="compile-status"]')?.textContent,
    pdfStatus: document.querySelector('[data-testid="compiled-pdf-status"]')?.textContent,
    compileDetails: document.querySelector('[data-testid="compile-details"]')?.textContent,
  }));
}
async function bridgeObservations() {
  return page.evaluate(async () => (await import("./js/compile-port-bridge.js")).observations());
}
async function waitCompileIdle() {
  // A settled native bridge does not alone imply the C# lifecycle/UI finished
  // cleanup. Baseline details only once active/pending/unsettled UI work is gone.
  await page.waitForFunction(() => document.querySelector('[data-testid="compile-work-status"]')?.textContent.trim() === "Idle", null, { timeout: 30000 });
}
async function failedCompile(label) {
  phase = label; ensureRunning();
  const before = workers.length, observationsBefore = await bridgeObservations();
  const event = page.waitForEvent("worker", { predicate: worker => COMPILE.test(worker.url()), timeout: 45000 });
  await Promise.all([event, H.$(page, "compile").click()]);
  await page.waitForFunction(() => /^(?:Compilation finished\.|No new PDF:)/.test(document.querySelector('[data-testid="compile-status"]')?.textContent || ""), null, { timeout: 45000 });
  assert.equal((await H.$(page, "compile-status").textContent()).trim(), "No new PDF: CompilerFailed.");
  await page.waitForFunction(async () => (await import("./js/compile-port-bridge.js")).observations().active === 0, null, { timeout: 30000 });
  await waitCompileIdle();
  const observations = await bridgeObservations();
  assert.ok(observations.started > observationsBefore.started, "fresh real dispatch");
  const trace = observations.trace.slice(observationsBefore.trace.length);
  assert.ok(trace.length > 0, "new real worker trace");
  assert.ok(trace.every(t => t.tag.version === 4 && t.tag.profile === 2 && t.tag.runtimeIdentity === PROFILE && t.tag.tool === "latex"), "exact authoring selection, no BibTeX");
  assert.ok(trace.some(t => t.entered && t.outcome === "native-return" && Number.isInteger(t.nativeReturn) && t.nativeReturn !== 0), "actual failed native entry, not synthetic/setup refusal");
  assert.ok(trace.every(t => t.terminated), "settled native workers");
  assert.ok(workers.slice(before).some(w => COMPILE.test(w.url)), "actual compiler worker boot");
  assert.ok(!workers.some(w => PDF.test(w.url)), "no PDF worker without renderer");
  assert.ok(!(await H.$(page, "compile-panel").getAttribute("data-output-hash")), "no displayed PDF hash");
  assert.equal((await H.$(page, "compiled-pdf-status").textContent()).trim(), "No compiled PDF");
  assert.equal(await H.$(page, "download-pdf").isDisabled(), true);
  await H.reveal(page, H.$(page, "compiler-log"));
  const log = await H.$(page, "compiler-log").textContent();
  assert.ok(log.length > 0 && log.length <= 131072, "bounded actual failed compile log");
  const diagnostic = (await H.$(page, "compiler-diagnostics").textContent()).slice(0, 8192);
  const result = { label, config: await configuration(), log: log.slice(0, 16384), logSha256: sha(Buffer.from(log)),
    logBytes: Buffer.byteLength(log), logTruncated: log.length > 16384, diagnostic, trace, workers: workers.slice(before),
    terminal: "CompilerFailed", noDisplayedPdf: true, downloadDisabled: true };
  record[label] = result; save(); // retain raw failure facts BEFORE demanding parser recovery
  assert.equal(await targetAction().count(), 1, "real parser must recover chapter.tex:3; no substitute report/fixture");
  assert.equal(await targetAction().isEnabled(), true);
  result.location = { path: "chapter.tex", line: 3, button: await targetAction().textContent() };
  fs.writeFileSync(path.join(RESULT_DIR, label + "-compiler.log"), log.slice(0, 16384));
  return result;
}
function assertSources(head, files) {
  assert.deepEqual(head.files.map(f => f.path).sort(), Object.keys(files).sort(), "exact controlled project file set");
  for (const [name, bytes] of Object.entries(files)) {
    const matches = head.files.filter(f => f.path === name); assert.equal(matches.length, 1);
    assert.deepEqual(Buffer.from(matches[0].bytes), bytes, `durable exact bytes ${name}`);
  }
  return fixtureIdentities(files);
}
async function domSelection() {
  return H.$(page, "editor").evaluate(editor => ({ value: editor.value,
    start: editor.selectionStart, end: editor.selectionEnd,
    selected: editor.value.slice(editor.selectionStart, editor.selectionEnd),
    focused: document.activeElement === editor,
    attributes: Object.fromEntries([...editor.attributes].filter(a => a.name.startsWith("data-")).map(a => [a.name, a.value])),
  }));
}
async function navigateAndAssert(project, head, files, label) {
  phase = label; ensureRunning();
  const config = await configuration(), workerCount = nativeCount(), bridge = await bridgeObservations();
  assert.deepEqual(await H.head(page, project), head);
  assertSources(head, files);
  assert.match((await page.locator('.editor-toolbar label[for="editor"]').textContent()).trim(), /main\.tex/);
  const chapter = head.files.find(f => f.path === "chapter.tex");
  const value = files["chapter.tex"].toString("utf8").replace(/\r\n?/g, "\n");
  const lines = value.split("\n"); const start = lines[0].length + 1 + lines[1].length + 1;
  const expected = { value, start, end: start + lines[2].length, selected: lines[2] };
  const evidence = [];
  for (const step of ["cross-file", "same-target", "cross-file-again"]) {
    if (step === "cross-file-again") await selectFile("main.tex");
    await targetAction().click();
    await page.waitForFunction(({ id, start, end, selected }) => {
      const editor = document.querySelector('[data-testid="editor"]');
      return document.querySelector('[data-testid="compiler-navigation-status"]')?.textContent === "Selected chapter.tex:3 (line-level precision)."
        && editor?.dataset.fileId === id && document.activeElement === editor
        && editor.selectionStart === start && editor.selectionEnd === end
        && editor.value.slice(start, end) === selected;
    }, { id: chapter.id, start: expected.start, end: expected.end, selected: expected.selected });
    await page.locator('.editor-toolbar label[for="editor"]', { hasText: "chapter.tex" }).waitFor();
    const dom = await domSelection();
    assert.equal(dom.attributes["data-file-id"], chapter.id, "real active file identity");
    assert.equal(dom.value, expected.value); assert.equal(dom.start, expected.start); assert.equal(dom.end, expected.end);
    assert.equal(dom.selected, expected.selected); assert.equal(dom.focused, true);
    assert.match(dom.selected, /Київ 😀$/); assert.ok(!dom.selected.includes("\r") && !dom.selected.includes("\n"));
    assert.deepEqual(await H.head(page, project), head, "navigation never changes durable head/bytes");
    assert.equal(nativeCount(), workerCount, "navigation starts no compile/render worker");
    assert.deepEqual(await bridgeObservations(), bridge, "navigation never dispatches native work");
    assert.deepEqual(await configuration(), config, "navigation preserves failure/log/compile selection");
    evidence.push({ step, ...dom, status: await H.$(page, "compiler-navigation-status").textContent(),
      headUnchanged: true, sourceBytesUnchanged: true, configUnchanged: true, workersUnchanged: true });
  }
  return { expectedDomUtf16: expected, observations: evidence, headId: head.id, workerCount };
}
async function recoverOffline(project, failedHead, files) {
  phase = "offline-repair-save"; ensureRunning();
  assert.deepEqual(await H.head(page, project), failedHead);
  assertSources(failedHead, files);
  const beforeText = files["chapter.tex"].toString("utf8");
  assert.equal(await H.$(page, "editor").inputValue(), beforeText);
  const broken = "\\overcloneUndefinedCommand", fixed = "\\noindent Repaired source.";
  assert.equal(beforeText.split(broken).length, 2, "exactly one deliberately undefined command");
  const repairedText = beforeText.replace(broken, fixed);
  assert.equal(repairedText.split("\n")[2], "\\noindent Repaired source. % Київ 😀", "only command on line3 replaced, comment preserved");
  const REPAIRED = { "main.tex": files["main.tex"], "chapter.tex": Buffer.from(repairedText, "utf8") };
  const beforeEditWorkers = nativeCount();
  await H.$(page, "editor").fill(repairedText); await assertStale();
  assert.equal(await H.$(page, "editor").inputValue(), repairedText);
  await H.save(page);
  const repairedHead = await H.head(page, project); assertSources(repairedHead, REPAIRED);
  assert.notEqual(repairedHead.id, failedHead.id, "ordinary repair Save creates a new durable head");
  await assertStale(); assert.equal(nativeCount(), beforeEditWorkers, "repair/save never compiles automatically");
  record.fixtures.repaired = fixtureIdentities(REPAIRED);
  fs.writeFileSync(path.join(RESULT_DIR, "fixture-chapter-repaired.tex"), REPAIRED["chapter.tex"], { flag: "wx" });
  const recovery = record.coldOffline.recovery = { failedNavigationHeadId: failedHead.id,
    repairedHead, exactRepairedBytes: record.fixtures.repaired, oldActionsUnavailable: true };
  save();

  // Renderer absence remains an explicit precondition, not something hidden by
  // provisioning a renderer or substituting a preview result.
  await noRenderer();
  phase = "offline-repaired-compile";
  const beforeWorkers = workers.length, beforeBridge = await bridgeObservations();
  const workerEvent = page.waitForEvent("worker", { predicate: worker => COMPILE.test(worker.url()), timeout: 45000 });
  await Promise.all([workerEvent, H.$(page, "compile").click()]);
  await page.waitForFunction(() => /^(?:Compilation finished\.|No new PDF:)/.test(document.querySelector('[data-testid="compile-status"]')?.textContent || ""), null, { timeout: 45000 });
  assert.equal((await H.$(page, "compile-status").textContent()).trim(), "Compilation finished.");
  await page.waitForFunction(async () => (await import("./js/compile-port-bridge.js")).observations().active === 0, null, { timeout: 30000 });
  await waitCompileIdle();
  const afterBridge = await bridgeObservations();
  assert.ok(afterBridge.started > beforeBridge.started, "repair produces a NEW real compile");
  const trace = afterBridge.trace.slice(beforeBridge.trace.length);
  assert.ok(trace.length > 0 && trace.every(t => t.entered && t.outcome === "native-return" && t.nativeReturn === 0 && t.terminated), "actual successful settled native phases");
  assert.ok(trace.every(t => t.tag.version === 4 && t.tag.profile === 2 && t.tag.runtimeIdentity === PROFILE && t.tag.tool === "latex"), "same actual authoring engine/profile, BibTeX off");
  assert.ok(workers.slice(beforeWorkers).some(w => COMPILE.test(w.url)));
  assert.ok(!workers.some(w => PDF.test(w.url)), "successful compile must not boot absent PDF renderer");
  assert.equal(await H.$(page, "pdf-runtime-status").getAttribute("data-state"), "Empty");
  await page.waitForFunction(() => document.querySelector('[data-testid="compile-panel"]')?.dataset.isCurrent === "true");
  const outputHash = await H.$(page, "compile-panel").getAttribute("data-output-hash");
  assert.match(outputHash, /^[0-9a-f]{64}$/);
  assert.equal((await H.$(page, "compiled-pdf-status").textContent()).trim(), "PDF up to date");
  assert.equal(await H.$(page, "download-pdf").isEnabled(), true);
  assert.deepEqual(await H.head(page, project), repairedHead, "repair compile does not save/change source");
  const log = await H.$(page, "compiler-log").textContent();
  assert.ok(log.length > 0 && log.length <= 131072);
  assert.match(log, /Output written on main\.pdf/);
  recovery.compile = { outcome: "Succeeded", currentPdf: true, outputHash, trace,
    workers: workers.slice(beforeWorkers), log: log.slice(0, 16384), logSha256: sha(Buffer.from(log)),
    logBytes: Buffer.byteLength(log), logTruncated: log.length > 16384,
    rendererResources: "Empty", pdfWorkers: 0 };
  fs.writeFileSync(path.join(RESULT_DIR, "offline-repaired-compiler.log"), log.slice(0, 16384));
  save();

  phase = "offline-repaired-download"; ensureRunning();
  const destination = path.join(RESULT_DIR, "offline-repaired.pdf");
  assert.ok(!fs.existsSync(destination), "never overwrite an earlier downloaded artifact");
  const exportBefore = { head: await H.head(page, project), config: await configuration(),
    nativeWorkerCount: nativeCount(), bridge: await bridgeObservations(),
    source: await H.$(page, "editor").inputValue(), outputHash };
  assertSources(exportBefore.head, REPAIRED); assert.equal(exportBefore.source, repairedText);
  const downloadEvent = page.waitForEvent("download", { timeout: 30000 });
  const [download] = await Promise.all([downloadEvent, H.$(page, "download-pdf").click()]);
  const suggested = download.suggestedFilename();
  assert.ok(suggested.length <= 84 && !suggested.startsWith("."));
  assert.equal(path.basename(suggested), suggested);
  assert.match(suggested, /^[\p{L}\p{N}\p{M} _-]+\.pdf$/u, "portable actual suggested PDF basename");
  assert.ok(!/^(?:CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])\.pdf$/i.test(suggested));
  await bounded(download.saveAs(destination), 30000, "PDF download saveAs timeout");
  assert.equal(await bounded(download.failure(), 5000, "PDF download failure observation timeout"), null);
  const stat = fs.statSync(destination); assert.ok(stat.size > 5 && stat.size <= 8388608, "actual positive bounded PDF payload");
  const bytes = fs.readFileSync(destination);
  assert.equal(bytes.subarray(0, 5).toString("ascii"), "%PDF-");
  assert.equal(sha(bytes), outputHash, "downloaded real bytes match displayed accepted PDF hash");
  await page.waitForFunction(() => document.querySelector('[data-testid="pdf-download-status"]')?.textContent === "PDF download started.");
  const exportAfter = { head: await H.head(page, project), config: await configuration(),
    nativeWorkerCount: nativeCount(), bridge: await bridgeObservations(),
    source: await H.$(page, "editor").inputValue(),
    outputHash: await H.$(page, "compile-panel").getAttribute("data-output-hash") };
  assert.deepEqual(exportAfter, exportBefore, "export preserves exact source/head/config/native work and accepted PDF");
  assertSources(exportAfter.head, REPAIRED);
  assert.equal(await H.$(page, "compile-panel").getAttribute("data-is-current"), "true");
  assert.equal(await H.$(page, "pdf-runtime-status").getAttribute("data-state"), "Empty");
  assert.ok(!workers.some(w => PDF.test(w.url)));
  recovery.download = { artifact: "offline-repaired.pdf", suggestedFilename: suggested,
    ...identity(bytes), header: "%PDF-", failure: null, before: exportBefore, after: exportAfter,
    sourceHeadConfigAndNativeWorkUnchanged: true };
  recovery.compile.payloadBytesVerifiedByActualDownload = bytes.length;
  record.coldOffline.finalState = "saved-repaired-source; current-successful-PDF; explicit-byte-verified-download; renderer-absent";
  save();
}
async function failureEvidence() {
  if (!page || page.isClosed()) return;
  try {
    record.failure.ui = await page.evaluate(() => {
      const text = (id, max) => (document.querySelector(`[data-testid="${id}"]`)?.textContent || "").slice(0, max);
      return { compile: text("compile-status", 512), log: text("compiler-log", 16384),
        diagnostics: text("compiler-diagnostics", 8192), navigation: text("compiler-navigation-status", 1024),
        runtime: text("runtime-status", 512), pdfRuntime: text("pdf-runtime-status", 512) };
    });
    record.failure.dom = await domSelection();
  } catch { /* Page startup/closing failure may prevent read-only observation. */ }
}
async function run() {
  const inputs = preflight(); save(); ensureRunning();
  fs.writeFileSync(path.join(RESULT_DIR, "fixture-main.tex"), MAIN, { flag: "wx" });
  fs.writeFileSync(path.join(RESULT_DIR, "fixture-chapter-original.tex"), CHAPTER, { flag: "wx" });
  assert.deepEqual(chromiumCensus(), [], "dedicated container must initially have no Chromium");
  profile = fs.mkdtempSync(path.join(os.tmpdir(), "compiler-nav-profile-"));
  server = http.createServer((request, response) => {
    let name;
    try { name = decodeURIComponent(new URL(request.url, `http://127.0.0.1:${PORT}`).pathname); }
    catch { response.writeHead(400); response.end(); return; }
    if (name.includes("\\") || name.split("/").includes("..") || name.includes("\0")) { response.writeHead(400); response.end(); return; }
    let file;
    try { file = fs.realpathSync(path.resolve(inputs.root, "." + (name === "/" ? "/index.html" : name))); }
    catch { response.writeHead(404); response.end(); return; }
    if (!within(inputs.root, file) || !fs.statSync(file).isFile()) { response.writeHead(404); response.end(); return; }
    response.writeHead(200, { "Content-Type": TYPES[path.extname(file).toLowerCase()] || "application/octet-stream",
      "Cache-Control": "no-store", "Content-Security-Policy": inputs.csp, "X-Content-Type-Options": "nosniff" });
    const stream = fs.createReadStream(file); stream.on("error", () => response.destroy()); stream.pipe(response);
  });
  await new Promise((resolve, reject) => { server.once("error", reject); server.listen(PORT, "127.0.0.1", resolve); });
  base = `http://127.0.0.1:${PORT}`;
  phase = "online-start"; await launch(false);
  record.online.chromium = chromiumCensus(); assert.ok(record.online.chromium.length > 0);
  const response = await page.goto(base); assert.ok(response?.ok());
  validateCsp(await response.text(), inputs.headers); assert.equal(response.headers()["content-security-policy"], inputs.csp);
  await H.ready(page);
  const project = `CompilerNav-${Date.now()}`; record.project = project;
  await H.reveal(page, page.locator("#new-project")); await page.locator("#new-project").fill(project);
  await page.getByRole("button", { name: "Create project", exact: true }).click();
  for (const [name, buffer] of Object.entries(ORIGINAL)) {
    await H.$(page, "import-input").setInputFiles({ name, mimeType: "text/plain", buffer });
    await page.locator('.editor-toolbar label[for="editor"]', { hasText: name }).waitFor();
  }
  await selectFile("main.tex"); await H.save(page);
  const originalHead = await H.head(page, project); assertSources(originalHead, ORIGINAL);
  record.online.originalHeadId = originalHead.id;
  phase = "online-install"; const beforeInstall = nativeCount();
  await waitTools(); await H.reveal(page, H.$(page, "runtime-recheck")); await H.$(page, "runtime-recheck").click();
  await resourceState("runtime-status", "Empty");
  await H.$(page, "runtime-pins").setInputFiles(inputs.pinsPath);
  await H.$(page, "runtime-files").setInputFiles(inputs.files);
  await H.$(page, "runtime-install").click(); await resourceState("runtime-status", "Installed", 180000);
  assert.equal((await H.$(page, "runtime-status").textContent()).trim(), "Compiler resources installed. Use Check tools to verify local compilation availability.");
  assert.equal(nativeCount(), beforeInstall, "compiler installation does not boot native workers");
  await noRenderer(); await checkTools();
  assert.equal(nativeCount(), beforeInstall, "resource/tool checks do not boot native workers");
  record.online.install = { installed: true, zeroNativeWorkerBoots: true, rendererAbsent: true,
    nativeCountBefore: beforeInstall, nativeCountAfter: nativeCount() };
  await setupSelection(); await failedCompile("online-first-failure");
  record.online.navigation = await navigateAndAssert(project, originalHead, ORIGINAL, "online-navigation");

  phase = "online-selection-staleness";
  const engine = page.locator('select[aria-label="Compile engine"]');
  const version = await H.$(page, "compile-panel").getAttribute("data-selection-version");
  const beforeAba = nativeCount();
  await engine.selectOption(""); await assertStale(); await engine.selectOption("local-authoring-v4"); await assertStale();
  assert.notEqual(await H.$(page, "compile-panel").getAttribute("data-selection-version"), version);
  assert.equal(nativeCount(), beforeAba, "A-B-A selection never compiles automatically");
  assert.deepEqual(await H.head(page, project), originalHead);
  record.online.selectionStaleness = { placeholderThenBack: true, oldActionsUnavailable: true, headUnchanged: true };
  await selectFile("main.tex"); await failedCompile("online-fresh-failure");
  record.online.freshNavigation = await navigateAndAssert(project, originalHead, ORIGINAL, "online-fresh-navigation");

  phase = "online-source-staleness";
  const editedText = CHAPTER.toString("utf8").replace(/\r\n?/g, "\n") + "% ordinary visible user edit\n";
  const EDITED = { "main.tex": MAIN, "chapter.tex": Buffer.from(editedText, "utf8") };
  const workerBeforeEdit = nativeCount(), configBeforeEdit = await configuration();
  await H.$(page, "editor").fill(editedText); await assertStale();
  assert.equal(await H.$(page, "editor").inputValue(), editedText);
  const domAfterEdit = await domSelection();
  assert.equal(domAfterEdit.attributes["data-file-id"], originalHead.files.find(f => f.path === "chapter.tex").id);
  assert.equal(domAfterEdit.start, editedText.length); assert.equal(domAfterEdit.end, editedText.length);
  assert.equal(domAfterEdit.selected, ""); assert.equal(domAfterEdit.focused, true);
  assert.equal(nativeCount(), workerBeforeEdit, "source edit does not compile");
  const configAfterEdit = await configuration();
  assert.equal(configAfterEdit.engine, configBeforeEdit.engine); assert.equal(configAfterEdit.main, configBeforeEdit.main);
  assert.equal(configAfterEdit.selectionVersion, configBeforeEdit.selectionVersion); assert.equal(configAfterEdit.bibtex, false);
  // Legitimate ordinary autosave may run after fill; explicitly Save BEFORE the
  // next durable baseline. No autosave is attributed to a navigation action.
  await H.save(page); const editedHead = await H.head(page, project); assertSources(editedHead, EDITED);
  const editedDom = await domSelection(); assert.equal(editedDom.attributes["data-file-id"], editedHead.files.find(f => f.path === "chapter.tex").id);
  assert.equal(editedDom.value, editedText);
  assert.equal(editedDom.start, domAfterEdit.start); assert.equal(editedDom.end, domAfterEdit.end);
  assert.equal(editedDom.selected, ""); await assertStale();
  assert.equal(nativeCount(), workerBeforeEdit);
  record.fixtures.edited = fixtureIdentities(EDITED);
  fs.writeFileSync(path.join(RESULT_DIR, "fixture-chapter-edited.tex"), EDITED["chapter.tex"], { flag: "wx" });
  record.online.sourceStaleness = { exactEditedBytes: true, headId: editedHead.id,
    oldActionsUnavailable: true, noCompile: true, noUnsolicitedFileOrCompileSelectionChange: true,
    noUnsolicitedCaretMovement: true, afterEdit: domAfterEdit, afterExplicitSave: editedDom };

  phase = "offline-ready";
  await page.waitForFunction(() => window.__oopOfflineReady === true, null, { timeout: 90000 });
  record.online.offlineReady = true; await collectCsp();
  record.coldOffline.beforeClose = chromiumCensus();
  await bounded(context.close(), 10000, "Online browser close timeout"); context = null; page = null;
  record.coldOffline.censusZero = await reap();
  phase = "origin-stop"; await stopOrigin(); await originRefused(); record.coldOffline.originRefused = true;
  assert.deepEqual(chromiumCensus(), []);
  phase = "cold-offline-start"; await launch(true);
  record.coldOffline.offlineBeforeNavigation = true;
  record.coldOffline.newProcesses = chromiumCensus(); assert.ok(record.coldOffline.newProcesses.length > 0);
  const oldProcesses = new Set(record.coldOffline.beforeClose.map(p => `${p.pid}:${p.startticks}`));
  assert.ok(record.coldOffline.newProcesses.every(p => !oldProcesses.has(`${p.pid}:${p.startticks}`)), "new browser process identities");
  await page.goto(base); await H.ready(page); await H.open(page, project); // UNCONDITIONAL real dashboard open
  assert.deepEqual(await H.head(page, project), editedHead, "same durable head and both source byte images survive cold restart");
  assertSources(await H.head(page, project), EDITED);
  phase = "cold-offline-recheck"; const beforeRecheck = nativeCount();
  await waitTools(); await H.reveal(page, H.$(page, "runtime-recheck")); await H.$(page, "runtime-recheck").click();
  await resourceState("runtime-status", "Installed"); await noRenderer(); await checkTools();
  assert.equal(nativeCount(), beforeRecheck, "offline installer rechecks and tool checks boot zero native workers");
  record.coldOffline.recheck = { installed: true, rendererAbsent: true, zeroNativeWorkerBoots: true,
    sourceBytesSurvive: true, nativeCountBefore: beforeRecheck, nativeCountAfter: nativeCount() };
  await setupSelection(); await failedCompile("offline-fresh-failure");
  record.coldOffline.navigation = await navigateAndAssert(project, editedHead, EDITED, "offline-navigation");
  // Preserve the failed-attempt navigation evidence before the natural recovery
  // phase changes source/head/outcome. This is not a rerun or fixture fallback.
  save();
  await recoverOffline(project, editedHead, EDITED);
  await collectCsp();
  assert.deepEqual(pageErrors, [], "zero page errors"); assert.deepEqual(violations, [], "zero CSP violations");
  assert.deepEqual(externalSuccesses, [], "no external successful requests");
  assert.equal(workers.filter(w => PDF.test(w.url)).length, 0, "no renderer workers anywhere");
  await page.screenshot({ path: path.join(RESULT_DIR, "compiler-navigation-offline.png"), fullPage: true });
  record.result = "PASS";
}

(async () => {
  let timer;
  const deadline = new Promise((_, reject) => { timer = setTimeout(() => { cancelled = true; reject(new Error("Harness deadline 360s")); }, 360000); });
  try { await Promise.race([run(), deadline]); }
  catch (error) {
    fail(error); save();
    try { await bounded(failureEvidence(), 5000, "Failure observation timeout"); } catch { /* First failure already retained. */ }
    process.exitCode = 1;
  }
  finally {
    cancelled = true; clearTimeout(timer);
    // A deadline during browser launch must still close the late owned context.
    if (launching) {
      try { context = await bounded(launching, 20000, "Pending browser launch cleanup timeout"); }
      catch (error) { fail(error); process.exitCode = 1; }
      launching = null;
    }
    try { if (context) {
      try { await bounded(collectCsp(), 5000, "CSP observation timeout"); }
      finally { await bounded(context.close(), 10000, "Final browser close timeout"); context = null; }
    } }
    catch (error) { fail(new Error(`Browser cleanup failed: ${String(error.message).slice(0, 256)}`)); process.exitCode = 1; }
    try { record.cleanup = { chromiumRemaining: await reap() }; }
    catch (error) { fail(error); process.exitCode = 1; }
    try { await stopOrigin(); }
    catch (error) { fail(error); process.exitCode = 1; }
    // Only the exclusively created synthetic profile is removed; no inputs/source/cache.
    if (profile) {
      try { fs.rmSync(profile, { recursive: true, force: true }); record.profileCleaned = true; }
      catch (error) { fail(new Error(`Owned profile cleanup failed: ${String(error.message).slice(0, 256)}`)); process.exitCode = 1; }
    }
    if (pageErrors.length || violations.length || externalSuccesses.length) {
      fail(new Error("Observed page/CSP/external-success failure")); process.exitCode = 1;
    }
    save(); process.stdout.write(JSON.stringify({ result: record.result, failure: record.failure }) + "\n");
  }
})();
