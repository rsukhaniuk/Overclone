// Real published-app acceptance: explicit total-PDF usual-extent observation.
// Root executes this standalone harness. No source/native/Rust result/provider
// injection, worker interception, API replacement or old harness IIFE execution.
// Required env: PUBLISHED_WWWROOT, AUTHORING_RUNTIME_DIR (497 pins + files/),
// PDF_RUNTIME_DIR (trusted512 E1 manifest + files/), exclusive writable RESULT_DIR.
// Assertions below are acceptance expectations, NOT another threshold evaluator.
const assert = require("node:assert/strict"), fs = require("node:fs"), os = require("node:os");
const path = require("node:path"), http = require("node:http"), { createHash } = require("node:crypto");
const H = require("./review-ui-helpers.cjs"), { validate: validateCsp } = require("./test-csp.cjs");
const need = n => { assert.ok(process.env[n], `${n} required`); return process.env[n]; };
const sha = b => createHash("sha256").update(b).digest("hex"), identity = b => ({ bytes: b.length, sha256: sha(b) });
const OUT = path.resolve(need("RESULT_DIR")), RECEIPT = path.join(OUT, "rendered-extent-receipt.json");
assert.ok(fs.statSync(OUT).isDirectory()); fs.closeSync(fs.openSync(RECEIPT, "wx")); // no overwriting first receipt
const SOURCE = Buffer.from("\\documentclass[10pt]{article}\n\\begin{document}\n\\noindent Write your document here.\n\\end{document}\n", "utf8");
const PROFILE = "8e9071ff18e5f93203266e7a85a6c4bba0597f9fba2e692c5651f68997e31894";
const E1 = { loader: { bytes: 13562, sha256: "040bdfac50656b3993f622614b73feaaba31d8253d077ff50f925f316c2f5f6b" },
  wasm: { bytes: 3434939, sha256: "1637c1a7798cab443334e559045d88f33864d89e0ff02eeb57b3da4f08f37c20" } };
const COMPILE = /\/__oop\/native-worker\/compile\b/, PDF = /\/__oop\/native-worker\/pdf\b/;
const RUST = /\/js\/rendered-extent-worker\.js(?:[?#]|$)/, BUDGET = 16 * 1024 * 1024, PORT = 8773;
const CITATION = "https://csc.knu.ua/uk/filer/canonical/1740300611/2680/";
const COVERAGE = "Total PDF pages only. Appendix and main-body pages are not identified. Overall compliance is not assessed.";
const record = { result: "FAIL", scope: "current-rendered-total-PDF-usual-extent-online-cold-offline",
  actualBrowserExecution: "started by caller", source: identity(SOURCE), sdk: null, compiler: null, renderer: null,
  online: {}, cold: {}, checks: [], changes: [], workers: [], pageErrors: [], violations: [], externalAttempts: [], externalSuccesses: [],
  observability: { pdfPerCallTrace: "Unavailable: baseline PDF bridge has no public observations API; none added.",
    claim: "Worker lifetimes + trusted published bytes/export metadata + actual hash-bound result; not a per-call WASM entry trace." },
  failure: null, cleanup: null,
  limits: ["Total PDF count only, not identified non-appendix/main-body counts or overall compliance.",
    "No physical units, geometry, fonts, fixes, general guideline compliance or source/PDF synchronization claim.",
    "No held foreign-IO/cancel/race cases manufactured; those belong to managed tests."] };
let phase = "output-preflight", category = "PREFLIGHT", root, inputs, chromium, playwrightErrors;
let context = null, launching = null, page = null, server = null, profile = null, base = null, stopped = false, browserEpoch = 0;
const running = () => { if (stopped) throw new Error("Deadline/cleanup reached"); };
const stage = (p, c) => { running(); phase = p; category = c; };
const count = re => record.workers.filter(w => re.test(w.url)).length;
const activePdf = () => record.workers.filter(w => PDF.test(w.url) && !w.closed).length;
const normalize = s => s.replace(/\s+/gu, " ").trim();
function outputBytes() {
  let size = 0, entries = 0; const pending = [OUT];
  while (pending.length) { const dir = pending.pop(); for (const n of fs.readdirSync(dir)) {
    assert.ok(++entries <= 4096); const p = path.join(dir, n), s = fs.lstatSync(p); assert.ok(!s.isSymbolicLink());
    if (s.isDirectory()) pending.push(p); else { assert.ok(s.isFile()); size += s.size; }
  } } return size;
}
function save() {
  const data = JSON.stringify(record, null, 2) + "\n"; assert.ok(Buffer.byteLength(data) <= 262144, "receipt <=256KiB");
  assert.ok(outputBytes() - fs.statSync(RECEIPT).size + Buffer.byteLength(data) <= BUDGET); fs.writeFileSync(RECEIPT, data);
}
function fail(e, kind = category) {
  if (!record.failure) record.failure = { phase, classification: kind, message: String(e.message || e).slice(0, 1024) };
  record.result = "FAIL";
}
function append(list, value, max = 128) { if (list.length < max) list.push(value); else record.observationOverflow = true; }
async function bounded(promise, ms, message) {
  let timer;
  try { return await Promise.race([promise, new Promise((_, reject) => { timer = setTimeout(() => reject(new Error(message)), ms); })]); }
  finally { clearTimeout(timer); }
}
async function until(predicate, ms, message) {
  const end = Date.now() + ms;
  while (!predicate()) { running(); assert.ok(Date.now() < end, message); await new Promise(r => setTimeout(r, 50)); }
}
function cleanSignals() {
  assert.ok(!record.observationOverflow);
  for (const n of ["pageErrors", "violations", "externalAttempts", "externalSuccesses"]) assert.deepEqual(record[n], [], n);
}
const TYPES = { ".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8", ".mjs": "text/javascript",
  ".css": "text/css", ".json": "application/json", ".wasm": "application/wasm", ".png": "image/png", ".svg": "image/svg+xml",
  ".ico": "image/x-icon", ".txt": "text/plain", ".dat": "application/octet-stream", ".blat": "application/octet-stream", ".pdb": "application/octet-stream" };
const inside = (r, f) => f.startsWith(r + path.sep);
function preflight() {
  root = fs.realpathSync(need("PUBLISHED_WWWROOT")); const runtime = path.resolve(need("AUTHORING_RUNTIME_DIR")), pdfDir = path.resolve(need("PDF_RUNTIME_DIR"));
  assert.ok(outputBytes() <= BUDGET - 3 * 1024 * 1024);
  const headers = fs.readFileSync(path.join(__dirname, "../docs/examples/static-csp-headers.conf"), "utf8");
  validateCsp(fs.readFileSync(path.join(root, "index.html"), "utf8"), headers);
  const cspLine = headers.split("\n").map(s => s.trim()).find(s => s.startsWith("Content-Security-Policy:")); assert.ok(cspLine);
  const text = fs.readFileSync(path.join(root, "service-worker-assets.js"), "utf8");
  const m = text.match(/self\.assetsManifest\s*=\s*(\{[\s\S]*\})\s*;/); assert.ok(m);
  const manifest = JSON.parse(m[1]); assert.equal(typeof manifest.version, "string");
  assert.ok(Array.isArray(manifest.assets) && manifest.assets.length > 0 && manifest.assets.length <= 4096,
    "positive finite observed manifest inventory; no universal237/239 count rule");
  const names = new Set();
  for (const a of manifest.assets) {
    assert.equal(typeof a.url, "string"); assert.ok(!path.isAbsolute(a.url) && !a.url.includes("\\") && !a.url.split("/").includes(".."));
    assert.ok(!names.has(a.url)); names.add(a.url); const file = fs.realpathSync(path.join(root, a.url)); assert.ok(inside(root, file));
    assert.equal("sha256-" + createHash("sha256").update(fs.readFileSync(file)).digest("base64"), a.hash, a.url);
  }
  record.sdk = { version: manifest.version, assets: manifest.assets.length, observedCurrentAssets: manifest.assets.length, everyAssetSriVerified: true, strictCsp: true };
  stage("published-rust-assets-export", "RUST_INIT");
  for (const name of ["js/rendered-extent-worker.js", "js/rendered-extent-bridge.js", "rust-generated/document_engine.js", "rust-generated/document_engine_bg.wasm"])
    assert.ok(names.has(name), "required new published asset: " + name);
  const generated = fs.readFileSync(path.join(root, "rust-generated/document_engine.js"), "utf8");
  const wasm = fs.readFileSync(path.join(root, "rust-generated/document_engine_bg.wasm"));
  assert.ok(wasm.length > 8 && wasm.length <= 16777216);
  assert.match(generated, /\bevaluate_rendered_extent\b/, "new generated module export required, not baseline old WASM");
  // Static metadata ONLY at root-run preflight; no Rust function is invoked by
  // this harness. The actual app worker performs the later policy evaluations.
  const exports = WebAssembly.Module.exports(new WebAssembly.Module(wasm)).map(e => e.name);
  assert.ok(exports.includes("evaluate_rendered_extent"), "actual published WASM export metadata");
  const bridgeCode = fs.readFileSync(path.join(root, "js/rendered-extent-bridge.js"), "utf8");
  const workerCode = fs.readFileSync(path.join(root, "js/rendered-extent-worker.js"), "utf8");
  assert.match(workerCode, /evaluate_rendered_extent/);
  const observationExport = /export\s+(?:async\s+)?function\s+observations\b|export\s+(?:const|let)\s+observations\b/.test(bridgeCode);
  record.rust = { module: identity(Buffer.from(generated)), wasm: identity(wasm), staticExportPresent: true,
    worker: identity(Buffer.from(workerCode)), bridge: identity(Buffer.from(bridgeCode)), optionalModuleObservationsExport: observationExport };
  stage("compiler-pins", "PREFLIGHT");
  const pinsPath = path.join(runtime, "pins.json"), bytes = fs.readFileSync(pinsPath), pins = JSON.parse(bytes);
  assert.equal(pins.length, 497); const unique = new Set(); let total = 0;
  for (const p of pins) {
    assert.match(p.name, /^[A-Za-z0-9._-]{1,200}$/); assert.ok(![".", ".."].includes(p.name) && !unique.has(p.name)); unique.add(p.name);
    assert.ok(Number.isSafeInteger(p.length) && p.length > 0 && p.length <= 33554432); total += p.length; assert.ok(total <= 67108864);
    assert.match(p.sha256, /^[0-9a-f]{64}$/); const f = path.join(runtime, "files", p.name);
    assert.equal(fs.statSync(f).size, p.length); assert.equal(sha(fs.readFileSync(f)), p.sha256);
  }
  const pin = n => { const p = pins.find(p => p.name === n); assert.ok(p); return p; };
  assert.equal(pin("engine.js").sha256, "39384532be2e32fd09dba99226db9da7572032c70da2bb2428fc9123c7fd163f");
  assert.equal(pin("engine.wasm").sha256, "7d239273b63b42d0c8f31d5e198a221c200c222f365760c20b9178408077048e");
  assert.equal(pin("pdflatex.fmt").sha256, "e2b5174b5aa6e552e03e9cf5dae7a7594e68d75962f48a6a30dc55da588b2595");
  record.compiler = { count: 497, totalBytes: total, pinsSha256: sha(bytes), everyPinVerified: true, loader: pin("engine.js"),
    wasm: pin("engine.wasm"), format: pin("pdflatex.fmt"), expectedProfile2Identity: PROFILE };
  const pdfManifestPath = path.join(pdfDir, "manifest.json"), manifestBytes = fs.readFileSync(pdfManifestPath), pdf = JSON.parse(manifestBytes);
  assert.equal(pdf.version, 1); assert.equal(pdf.resources.length, 2); const roles = new Set();
  const pdfFiles = pdf.resources.map(r => {
    assert.ok(["loader", "wasm"].includes(r.role) && !roles.has(r.role)); roles.add(r.role);
    assert.match(r.name, /^[A-Za-z0-9._-]{1,200}$/); assert.ok(![".", ".."].includes(r.name));
    assert.equal(r.length, E1[r.role].bytes); assert.equal(r.sha256, E1[r.role].sha256);
    const f = path.join(pdfDir, "files", r.name); assert.equal(fs.statSync(f).size, r.length); assert.equal(sha(fs.readFileSync(f)), r.sha256); return f;
  });
  record.renderer = { manifestSha256: sha(manifestBytes), trusted512Pair: E1, everyResourceVerified: true };
  return { headers, csp: cspLine.slice("Content-Security-Policy:".length).trim(), pinsPath,
    files: pins.map(p => path.join(runtime, "files", p.name)), pdfManifestPath, pdfFiles, observationExport };
}
function census() {
  const out = [];
  for (const pid of fs.readdirSync("/proc")) {
    if (!/^\d+$/.test(pid)) continue; let exe, stat;
    try { exe = path.basename(fs.readlinkSync(`/proc/${pid}/exe`)); } catch { continue; }
    if (!/chromium|chrome|headless_shell/i.test(exe)) continue;
    try { stat = fs.readFileSync(`/proc/${pid}/stat`, "utf8"); } catch (e) { if (["ENOENT", "ESRCH"].includes(e.code)) continue; throw e; }
    const startticks = stat.slice(stat.lastIndexOf(") ") + 2).split(" ")[19]; assert.match(startticks, /^\d+$/); out.push({ pid: Number(pid), startticks, exe });
  } return out.sort((a, b) => a.pid - b.pid);
}
async function reap() {
  let c;
  for (let i = 0; i < 40; i++) { c = census(); if (!c.length) return c; await new Promise(r => setTimeout(r, 100)); }
  assert.deepEqual(c, [], "whole browser census must reach zero"); return c;
}
async function stopOrigin() {
  if (!server?.listening) return;
  await bounded(new Promise((resolve, reject) => { server.close(e => e ? reject(e) : resolve()); server.closeIdleConnections(); }), 5000, "origin close timeout");
}
async function refusal() {
  await new Promise((resolve, reject) => {
    const r = http.get(base, response => { response.resume(); reject(new Error("origin still accepts connections")); });
    r.on("error", e => e.code === "ECONNREFUSED" ? resolve() : reject(e)); r.setTimeout(2000, () => r.destroy(new Error("refusal unproven")));
  });
}
async function serve() {
  server = http.createServer((req, res) => {
    let name, file;
    try { name = decodeURIComponent(new URL(req.url, `http://127.0.0.1:${PORT}`).pathname); } catch { res.writeHead(400); res.end(); return; }
    if (name.includes("\\") || name.includes("\0") || name.split("/").includes("..")) { res.writeHead(400); res.end(); return; }
    try { file = fs.realpathSync(path.resolve(root, "." + (name === "/" ? "/index.html" : name))); } catch { res.writeHead(404); res.end(); return; }
    if (!inside(root, file) || !fs.statSync(file).isFile()) { res.writeHead(404); res.end(); return; }
    res.writeHead(200, { "Content-Type": TYPES[path.extname(file).toLowerCase()] || "application/octet-stream", "Cache-Control": "no-store",
      "Content-Security-Policy": inputs.csp, "X-Content-Type-Options": "nosniff" });
    const stream = fs.createReadStream(file); stream.on("error", () => res.destroy()); stream.pipe(res);
  });
  await new Promise((resolve, reject) => { server.once("error", reject); server.listen(PORT, "127.0.0.1", resolve); }); base = `http://127.0.0.1:${PORT}`;
}
async function launch(offline) {
  running(); launching = chromium.launchPersistentContext(profile, { headless: true, offline, acceptDownloads: true, timeout: 15000 });
  context = await launching; launching = null; running(); const epoch = ++browserEpoch;
  context.on("request", req => { const u = new URL(req.url()); if (/^https?:$/.test(u.protocol) && u.origin !== base) append(record.externalAttempts, { phase, origin: u.origin }); });
  await context.route("**/*", r => new URL(r.request().url()).origin === base ? r.continue() : r.abort());
  await context.addInitScript(() => { window.__extentCsp = []; addEventListener("securitypolicyviolation", e => window.__extentCsp.push(e.effectiveDirective)); });
  const watch = p => {
    p.on("worker", worker => { const item = { phase, browserEpoch: epoch, url: worker.url(), closed: false }; append(record.workers, item); worker.on("close", () => { item.closed = true; }); });
    p.on("pageerror", e => append(record.pageErrors, { phase, message: String(e.message).slice(0, 1024) }));
  };
  context.pages().forEach(watch); context.on("page", watch);
  context.on("response", r => { const u = new URL(r.url()); if (u.origin !== base && r.ok()) append(record.externalSuccesses, { phase, origin: u.origin }); });
  page = await context.newPage(); page.setDefaultTimeout(30000);
}
async function collectCsp() {
  if (!context) return;
  for (const p of context.pages()) if (new URL(p.url()).origin === base)
    for (const directive of await p.evaluate(() => window.__extentCsp || [])) append(record.violations, { phase, directive });
}
async function settleNormalSave() {
  const writable = async () => { assert.equal(await page.locator(".banner-readonly, .banner-recovery").count(), 0); assert.equal(await H.$(page, "editor").isDisabled(), false); };
  await writable(); const button = H.$(page, "save"); let mode = "already-saved";
  if (await button.isEnabled()) {
    try { await button.click({ timeout: 2000 }); mode = "explicit-click"; }
    catch (e) { if (!(e instanceof playwrightErrors.TimeoutError) || !(await button.isDisabled())) throw e; mode = "autosave-raced-click"; }
  }
  await page.waitForFunction(() => document.querySelector(".workspace-header .status")?.textContent === "Saved."
    && !document.querySelector(".banner-readonly, .banner-recovery") && document.querySelector('[data-testid="editor"]')?.disabled === false, null, { timeout: 30000 });
  await writable(); return { mode, status: "Saved.", writable: true }; // callers still verify exact durable bytes/head
}
async function selectMain() {
  const file = H.$(page, "select-file").filter({ hasText: "main.tex" }); assert.equal(await file.count(), 1); await file.click();
  await page.locator('.editor-toolbar label[for="editor"]', { hasText: "main.tex" }).waitFor();
}
async function exactSource(project, bytes, head = null) {
  const actual = await H.head(page, project); if (head) assert.deepEqual(actual, head); assert.equal(actual.files.length, 1);
  const f = actual.files[0]; assert.equal(f.path, "main.tex"); assert.deepEqual(Buffer.from(f.bytes), bytes);
  assert.equal(await H.$(page, "editor").inputValue(), bytes.toString("utf8"));
  assert.equal(await H.$(page, "editor").getAttribute("data-file-id"), f.id); return actual;
}
async function resources(id, expected, timeout = 60000) {
  await page.waitForFunction(id => document.querySelector(`[data-testid="${id}"]`)?.dataset.busy === "false", id, { timeout });
  assert.equal(await H.$(page, id).getAttribute("data-state"), expected, await H.$(page, id).textContent());
}
async function waitTools() {
  await H.reveal(page, H.$(page, "refresh-compile-tools"));
  await page.waitForFunction(() => document.querySelector('[data-testid="refresh-compile-tools"]')?.disabled === false, null, { timeout: 60000 });
}
async function checkTools() {
  await waitTools(); await H.$(page, "refresh-compile-tools").click();
  await page.waitForFunction(() => document.querySelector('[data-testid="compiler-readiness"]')?.textContent.startsWith("Local compiler checked"), null, { timeout: 60000 });
}
async function configure() {
  await selectMain(); await H.reveal(page, H.$(page, "refresh-compile-tools"));
  await page.locator('select[aria-label="Compile main file"]').selectOption({ label: "main.tex" });
  await page.locator('select[aria-label="Compile engine"]').selectOption("local-authoring-v4");
  await H.bibliography(page, "None");
  await page.waitForFunction(() => document.querySelector('[data-testid="compile"]')?.disabled === false);
}
const compileBridge = () => page.evaluate(async () => (await import("./js/compile-port-bridge.js")).observations());
async function optionalRustObservations() {
  // Called only AFTER the app has actually used this canonical module. No port
  // or worker is constructed by the test, and no observations API is demanded.
  if (!inputs.observationExport) return { available: false, reason: "No detected exported module observations(); no test API added." };
  return page.evaluate(async () => {
    const m = await import("./js/rendered-extent-bridge.js");
    if (typeof m.observations !== "function") return { available: false };
    const value = await m.observations(); const encoded = JSON.stringify(value);
    if (typeof encoded !== "string") return { available: false, reason: "Optional observations returned no serializable evidence." };
    if (encoded.length > 8192) throw new Error("Rust observations exceed bounded evidence budget");
    return { available: true, value };
  });
}
async function waitPreviewSettled(hash, pages) {
  await page.waitForFunction(({ hash, pages }) => {
    const status = document.querySelector('[data-testid="preview-status"]'), pane = document.querySelector('[data-testid="pdf-pane"]');
    return status?.dataset.previewStatus === "Available" && /Page ready/.test(status.textContent || "")
      && /Drawn/.test(document.querySelector('[data-testid="canvas-status"]')?.textContent || "")
      && pane?.dataset.document === hash
      && document.querySelector('[data-testid="page-label"]')?.textContent.trim() === `Page 1 of ${pages}`
      && document.querySelector('button[aria-label="Retry preview"]')?.disabled === false;
  }, { hash, pages }, { timeout: 60000 });
  await until(() => activePdf() === 0, 30000, "actual PDF workers must close before baselining");
  assert.equal((await compileBridge()).active, 0);
  await page.waitForFunction(() => document.querySelector('[data-testid="compile-work-status"]')?.textContent.trim() === "Idle", null, { timeout: 30000 });
}
async function compile(project, bytes, head, label, expectedPages) {
  stage(label, "NATIVE_COMPILE"); const before = await compileBridge(), offset = record.workers.length;
  const event = page.waitForEvent("worker", { predicate: w => COMPILE.test(w.url()), timeout: 45000 });
  await Promise.all([event, H.$(page, "compile").click()]);
  await page.waitForFunction(() => /^(?:Compilation finished\.|No new PDF:)/.test(document.querySelector('[data-testid="compile-status"]')?.textContent || ""), null, { timeout: 45000 });
  const terminal = normalize(await H.$(page, "compile-status").textContent()), log = await H.$(page, "compiler-log").textContent() || "";
  record[label] = { terminal, log: log.slice(0, 16384), logSha256: sha(Buffer.from(log)), logBytes: Buffer.byteLength(log), logTruncated: log.length > 16384 }; save();
  assert.equal(terminal, "Compilation finished.");
  await page.waitForFunction(async () => (await import("./js/compile-port-bridge.js")).observations().active === 0, null, { timeout: 30000 });
  await page.waitForFunction(() => document.querySelector('[data-testid="compile-work-status"]')?.textContent.trim() === "Idle", null, { timeout: 30000 });
  const bridge = await compileBridge(), trace = bridge.trace.slice(before.trace.length);
  assert.ok(bridge.started > before.started && trace.length);
  assert.ok(trace.every(t => t.entered && t.terminated && t.outcome === "native-return" && t.nativeReturn === 0));
  assert.ok(trace.every(t => t.tag.version === 4 && t.tag.profile === 2 && t.tag.runtimeIdentity === PROFILE && t.tag.tool === "latex"));
  const hash = await H.$(page, "compile-panel").getAttribute("data-output-hash"); assert.match(hash, /^[0-9a-f]{64}$/);
  assert.equal(await H.$(page, "compile-panel").getAttribute("data-is-current"), "true");
  stage(label + "-preview-settle", "NATIVE_PREVIEW"); await waitPreviewSettled(hash, expectedPages);
  assert.ok(record.workers.slice(offset).some(w => PDF.test(w.url)), "actual renderer worker for preview");
  await exactSource(project, bytes, head); cleanSignals();
  Object.assign(record[label], { hash, current: true, trace, previewPages: expectedPages, previewAvailable: true,
    previewPdfWorkersClosed: true, workers: record.workers.slice(offset), rendererStore: "Installed" });
  fs.writeFileSync(path.join(OUT, label + ".log"), log.slice(0, 16384), { flag: "wx" }); save(); return hash;
}
const panel = () => H.$(page, "rendered-extent-panel");
const result = () => panel().locator('[data-testid="rendered-extent-result"]');
const workType = () => panel().locator('[data-testid="rendered-extent-work-type"]');
const checkButton = () => panel().locator('[data-testid="rendered-extent-check"]');
async function noCurrentResult() {
  await page.waitForFunction(() => !document.querySelector('[data-testid="rendered-extent-panel"] [data-testid="rendered-extent-result"][data-current="true"]'));
}
async function snapshot(project, bytes, head) {
  await exactSource(project, bytes, head);
  return { head: await H.head(page, project), editor: await H.$(page, "editor").evaluate(e => ({ value: e.value,
    file: e.dataset.fileId, start: e.selectionStart, end: e.selectionEnd, selected: e.value.slice(e.selectionStart, e.selectionEnd) })),
    config: await page.evaluate(() => ({ main: document.querySelector('select[aria-label="Compile main file"]')?.value,
      engine: document.querySelector('select[aria-label="Compile engine"]')?.value, bibtex: (document.querySelector('[data-testid="bibliography-tool"]') ? document.querySelector('[data-testid="bibliography-tool"]').value === "Bibtex" : document.querySelector('[data-testid="use-bibtex"]')?.checked),
      version: document.querySelector('[data-testid="compile-panel"]')?.dataset.selectionVersion,
      outcome: document.querySelector('[data-testid="compile-status"]')?.textContent, details: document.querySelector('[data-testid="compile-details"]')?.textContent,
      hash: document.querySelector('[data-testid="compile-panel"]')?.dataset.outputHash, current: document.querySelector('[data-testid="compile-panel"]')?.dataset.isCurrent })),
    compileBridge: await compileBridge(), compileWorkerCount: count(COMPILE) };
}
async function choose(type, label, project, bytes, head) {
  stage(label, "WORK_TYPE_INVALIDATION");
  const beforeUi = await snapshot(project, bytes, head);
  const before = { pdf: count(PDF), compile: count(COMPILE), bridge: await compileBridge() };
  await workType().selectOption(type); assert.equal(await workType().inputValue(), type); await noCurrentResult();
  assert.equal(count(PDF), before.pdf, "work type must not automatically inspect PDF");
  assert.equal(count(COMPILE), before.compile, "work type must not compile"); assert.deepEqual(await compileBridge(), before.bridge);
  const afterUi = await snapshot(project, bytes, head); assert.deepEqual(afterUi, beforeUi);
  await noCurrentResult(); assert.equal(count(PDF), before.pdf); assert.equal(count(COMPILE), before.compile);
  record.changes.push({ phase, type, currentReportInvalidated: true, noNativeJob: true, before: beforeUi, after: afterUi });
}
async function scopedText(id) { const e = result().locator(`[data-testid="${id}"]`); assert.equal(await e.count(), 1); return normalize(await e.textContent()); }
async function explicitCheck(project, bytes, head, hash, expected, label) {
  stage(label, "RENDERED_CHECK"); await H.reveal(page, checkButton());
  await waitPreviewSettled(hash, expected.pages);
  const before = await snapshot(project, bytes, head), pdfBefore = count(PDF), offset = record.workers.length;
  assert.equal(await checkButton().isEnabled(), true);
  const event = page.waitForEvent("worker", { predicate: w => PDF.test(w.url()), timeout: 30000 });
  await Promise.all([event, checkButton().click()]);
  await page.waitForFunction(expected => {
    const e = document.querySelector('[data-testid="rendered-extent-panel"] [data-testid="rendered-extent-result"]');
    return e?.dataset.current === "true" && e.dataset.pdfSha256 === expected.hash
      && e.dataset.pageCount === String(expected.pages) && e.dataset.workType === expected.type
      && e.dataset.status === expected.status && e.dataset.profileVersion === "1.0.0";
  }, { ...expected, hash }, { timeout: 60000 });
  await until(() => activePdf() === 0, 30000, "extent PDF inspection worker closes");
  await page.waitForFunction(() => document.querySelector('[data-testid="rendered-extent-check"]')?.disabled === false);
  assert.ok(count(PDF) > pdfBefore, "explicit check actually starts PDF inspection");
  const rustBoots = record.workers.filter(w => RUST.test(w.url) && w.browserEpoch === browserEpoch);
  assert.ok(rustBoots.length > 0, "actual dedicated Rust worker observed in this browser lifetime (reused or one-shot)");
  assert.equal(await result().count(), 1);
  const attrs = { "data-status": expected.status, "data-current": "true", "data-pdf-sha256": hash, "data-page-count": String(expected.pages),
    "data-work-type": expected.type, "data-profile-version": "1.0.0", "data-reason": expected.reason,
    "data-usual-minimum": expected.min === null ? "none" : String(expected.min), "data-usual-maximum": expected.max === null ? "none" : String(expected.max) };
  for (const [name, value] of Object.entries(attrs)) assert.equal(await result().getAttribute(name), value, name);
  const reason = await scopedText("rendered-extent-reason"); assert.equal(reason, expected.reasonText);
  const range = await scopedText("rendered-extent-range"); assert.equal(range, expected.rangeText);
  const coverage = await scopedText("rendered-extent-coverage"); assert.equal(coverage, COVERAGE);
  const citation = result().locator('a[data-testid="rendered-extent-citation"]'); assert.equal(await citation.count(), 1);
  assert.equal(normalize(await citation.textContent()), "KNU CSC 2025, §2, p. 11"); assert.equal(await citation.getAttribute("href"), CITATION);
  if (await citation.getAttribute("target") === "_blank") {
    const rel = (await citation.getAttribute("rel") || "").split(/\s+/); assert.ok(rel.includes("noopener") && rel.includes("noreferrer"));
  }
  // Native worker/real result are evidence; no particular PDF/WASM entry call
  // or raster flag is inferred from a boot in the absence of per-call tracing.
  const after = await snapshot(project, bytes, head); assert.deepEqual(after, before, "check preserves source/head/file/caret/config/compile work");
  const observation = { label, attrs, reason, range, coverage, citation: { text: "KNU CSC 2025, §2, p. 11", href: CITATION },
    statusText: normalize(await panel().locator('[data-testid="rendered-extent-status"]').textContent()),
    before, after, workers: record.workers.slice(offset), rustWorkerBootsInThisBrowser: rustBoots,
    rustObservations: await optionalRustObservations(),
    noMutationOrCompile: true, perCallNativeTrace: "not exposed; no new API added" };
  record.checks.push(observation); cleanSignals(); save(); return observation;
}
// Fixed acceptance examples from the approved wire contract. No calculation of
// thresholds/boundaries or policy implementation is performed by this test.
const UNSPECIFIED = { type: "unspecified", pages: 1, status: "indeterminate", reason: "workTypeRequired", min: null, max: null,
  reasonText: "Choose a work type to compare the usual length.", rangeText: "Usual non-appendix length: select a work type." };
const BACHELOR = { type: "bachelorQualification", pages: 1, status: "belowUsualMinimum", reason: "totalBelowUsualNonAppendixMinimum", min: 40, max: 60,
  reasonText: "Total PDF pages are below the usual minimum.", rangeText: "Usual non-appendix length: 40–60 pages." };
const MASTER = { type: "masterQualification", pages: 2, status: "belowUsualMinimum", reason: "totalBelowUsualNonAppendixMinimum", min: 50, max: 70,
  reasonText: "Total PDF pages are below the usual minimum.", rangeText: "Usual non-appendix length: 50–70 pages." };
async function download(project, bytes, head, hash) {
  stage("offline-download", "UI_DOWNLOAD_ORACLE"); const before = await snapshot(project, bytes, head), pdfBefore = count(PDF);
  const extentBefore = await result().evaluate(e => ({ attrs: Object.fromEntries([...e.attributes].filter(a => a.name.startsWith("data-")).map(a => [a.name, a.value])), text: e.textContent }));
  const target = path.join(OUT, "offline-two-page.pdf"); assert.ok(!fs.existsSync(target));
  const event = page.waitForEvent("download", { timeout: 30000 }); const [d] = await Promise.all([event, H.$(page, "download-pdf").click()]);
  const suggested = d.suggestedFilename(); assert.ok(suggested.length <= 84 && !suggested.startsWith(".")); assert.equal(path.basename(suggested), suggested);
  assert.match(suggested, /^[\p{L}\p{N}\p{M} _-]+\.pdf$/u); assert.ok(!/^(?:CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])\.pdf$/i.test(suggested));
  assert.equal(await bounded(d.failure(), 30000, "download completion timeout"), null);
  const staged = await bounded(d.path(), 5000, "download staging timeout"), size = fs.statSync(staged).size;
  assert.ok(size > 5 && size <= 1048576); assert.ok(outputBytes() + size + 262144 <= BUDGET);
  await bounded(d.saveAs(target), 30000, "download saveAs timeout"); const pdf = fs.readFileSync(target);
  assert.equal(pdf.length, size); assert.equal(pdf.subarray(0, 5).toString("ascii"), "%PDF-"); assert.equal(sha(pdf), hash);
  await page.waitForFunction(() => document.querySelector('[data-testid="pdf-download-status"]')?.textContent === "PDF download started.");
  const after = await snapshot(project, bytes, head); assert.deepEqual(after, before); assert.equal(count(PDF), pdfBefore);
  assert.deepEqual(await result().evaluate(e => ({ attrs: Object.fromEntries([...e.attributes].filter(a => a.name.startsWith("data-")).map(a => [a.name, a.value])), text: e.textContent })), extentBefore);
  record.cold.download = { artifact: "offline-two-page.pdf", suggested, ...identity(pdf), failure: null, before, after,
    reportUnchanged: true, sourceHeadConfigAndNativeCountsUnchanged: true, mime: "not measured in this case" }; save();
}
async function run() {
  inputs = preflight(); ({ chromium, errors: playwrightErrors } = require("playwright")); save();
  assert.deepEqual(census(), []); profile = fs.mkdtempSync(path.join(os.tmpdir(), "rendered-extent-profile-")); await serve();
  stage("online-start", "UI_STARTUP"); await launch(false); record.online.processes = census(); assert.ok(record.online.processes.length);
  const response = await page.goto(base); assert.ok(response?.ok()); validateCsp(await response.text(), inputs.headers); assert.equal(response.headers()["content-security-policy"], inputs.csp);
  await H.ready(page); const project = `RenderedExtent-${Date.now()}`; record.project = project;
  stage("online-source-save", "SOURCE_UI"); await H.reveal(page, page.locator("#new-project")); await page.locator("#new-project").fill(project);
  await page.getByRole("button", { name: "Create project", exact: true }).click();
  await H.$(page, "starter-article").click(); await page.locator('.editor-toolbar label[for="editor"]', { hasText: "main.tex" }).waitFor();
  assert.equal(await H.$(page, "editor").inputValue(), SOURCE.toString("utf8")); record.online.save = await settleNormalSave();
  const head = await exactSource(project, SOURCE); record.online.head = head;
  stage("online-install-compiler", "RESOURCE_INSTALL"); const c0 = count(COMPILE), p0 = count(PDF);
  await waitTools(); await H.reveal(page, H.$(page, "runtime-recheck")); await H.$(page, "runtime-recheck").click(); await resources("runtime-status", "Empty");
  await H.$(page, "runtime-pins").setInputFiles(inputs.pinsPath); await H.$(page, "runtime-files").setInputFiles(inputs.files);
  await H.$(page, "runtime-install").click(); await resources("runtime-status", "Installed", 180000);
  assert.equal(count(COMPILE), c0); assert.equal(count(PDF), p0);
  stage("online-install-pdf", "RESOURCE_INSTALL"); await H.reveal(page, H.$(page, "pdf-runtime-recheck")); await H.$(page, "pdf-runtime-recheck").click();
  await resources("pdf-runtime-status", "Empty"); await H.$(page, "pdf-runtime-manifest").setInputFiles(inputs.pdfManifestPath);
  await H.$(page, "pdf-runtime-files").setInputFiles(inputs.pdfFiles); await H.$(page, "pdf-runtime-install").click(); await resources("pdf-runtime-status", "Installed", 120000);
  await checkTools(); assert.equal(count(COMPILE), c0); assert.equal(count(PDF), p0);
  record.online.install = { compiler: "Installed", pdf: "Installed", zeroNativeBoots: true }; await configure();
  const hash = await compile(project, SOURCE, head, "online-compile", 1);
  await H.reveal(page, checkButton()); assert.equal(await workType().inputValue(), "unspecified");
  await explicitCheck(project, SOURCE, head, hash, UNSPECIFIED, "online-unspecified");
  await choose("bachelorQualification", "online-degree-bachelor", project, SOURCE, head);
  await explicitCheck(project, SOURCE, head, hash, BACHELOR, "online-bachelor");
  await choose("masterQualification", "online-degree-away", project, SOURCE, head);
  await choose("bachelorQualification", "online-degree-back", project, SOURCE, head);
  await noCurrentResult(); // old result must not become current merely by A-B-A
  await exactSource(project, SOURCE, head);
  stage("wait-offline-cache", "COLD_OFFLINE"); await page.waitForFunction(() => window.__oopOfflineReady === true, null, { timeout: 90000 });
  record.online.offlineReady = true; await collectCsp(); cleanSignals(); record.cold.oldProcesses = census();
  await bounded(context.close(), 10000, "online browser close timeout"); context = null; page = null; record.cold.censusZero = await reap();
  await stopOrigin(); await refusal(); record.cold.originRefused = true; assert.deepEqual(census(), []);
  stage("cold-offline-start", "COLD_OFFLINE"); const coldC = count(COMPILE), coldP = count(PDF); await launch(true);
  record.cold.offlineBeforeNavigation = true; record.cold.newProcesses = census(); assert.ok(record.cold.newProcesses.length);
  const oldIds = new Set(record.cold.oldProcesses.map(p => `${p.pid}:${p.startticks}`)); assert.ok(record.cold.newProcesses.every(p => !oldIds.has(`${p.pid}:${p.startticks}`)));
  await page.goto(base); await H.ready(page); await H.open(page, project); await selectMain(); await exactSource(project, SOURCE, head);
  stage("cold-resource-recheck", "RESOURCE_RECHECK"); await waitTools(); await H.reveal(page, H.$(page, "runtime-recheck")); await H.$(page, "runtime-recheck").click();
  await resources("runtime-status", "Installed"); await H.reveal(page, H.$(page, "pdf-runtime-recheck")); await H.$(page, "pdf-runtime-recheck").click();
  await resources("pdf-runtime-status", "Installed"); await checkTools(); assert.equal(count(COMPILE), coldC); assert.equal(count(PDF), coldP);
  record.cold.recheck = { compiler: "Installed", pdf: "Installed", zeroNativeBoots: true, exactHeadSurvived: head.id };
  await configure(); const coldHash = await compile(project, SOURCE, head, "cold-one-page-compile", 1);
  await H.reveal(page, checkButton()); assert.equal(await workType().inputValue(), "unspecified", "new app session default, no persisted report authority assumed");
  await choose("bachelorQualification", "cold-one-page-degree", project, SOURCE, head);
  await explicitCheck(project, SOURCE, head, coldHash, BACHELOR, "cold-one-page-report");
  stage("cold-visible-two-page-edit", "SOURCE_STALENESS");
  const token = "\\end{document}", text = SOURCE.toString("utf8"); assert.equal(text.split(token).length, 2);
  const edited = Buffer.from(text.replace(token, "\\newpage\n\\noindent Second offline page.\n" + token), "utf8");
  const compileBeforeEdit = count(COMPILE);
  await H.$(page, "editor").fill(edited.toString("utf8")); await noCurrentResult();
  assert.equal(await H.$(page, "compile-panel").getAttribute("data-is-current"), "false");
  assert.equal(normalize(await H.$(page, "compiled-pdf-status").textContent()), "Last successful PDF — out of date");
  assert.equal(await checkButton().isDisabled(), true);
  record.cold.save = await settleNormalSave(); const editedHead = await exactSource(project, edited); assert.notEqual(editedHead.id, head.id);
  assert.deepEqual(Buffer.from(head.files[0].bytes), SOURCE); record.cold.edit = { oldHead: head, newHead: editedHead, bytes: identity(edited),
    insertionBeforeEndDocument: "\\newpage\n\\noindent Second offline page.\n", previousReportStale: true };
  // Source freshness relabeling may legitimately refresh the OLD PDF preview.
  // Let that finish; it is not an explicit extent inspection or new compile.
  await waitPreviewSettled(coldHash, 1);
  assert.equal(count(COMPILE), compileBeforeEdit, "edit/save cannot implicitly compile");
  const gated = { compile: count(COMPILE), pdf: count(PDF), bridge: await compileBridge() };
  await noCurrentResult(); assert.equal(await checkButton().isDisabled(), true);
  assert.deepEqual({ compile: count(COMPILE), pdf: count(PDF), bridge: await compileBridge() }, gated);
  record.cold.staleCheckGate = { disabled: true, noCheckDispatched: true, afterLegitimatePreviewSettlement: gated };
  const twoHash = await compile(project, edited, editedHead, "cold-two-page-compile", 2);
  await choose("masterQualification", "cold-master-degree", project, edited, editedHead);
  await explicitCheck(project, edited, editedHead, twoHash, MASTER, "cold-master-two-page");
  await download(project, edited, editedHead, twoHash);
  record.cold.finalState = "saved-two-page-source/current-PDF/current-limited-Master-observation/not-compliance";
  await collectCsp(); cleanSignals(); record.result = "PASS";
}
async function failureEvidence() {
  if (!page || page.isClosed()) return;
  record.failure.ui = await page.evaluate(() => {
    const text = (id, n) => (document.querySelector(`[data-testid="${id}"]`)?.textContent || "").slice(0, n);
    const r = document.querySelector('[data-testid="rendered-extent-result"]');
    return { compiler: text("compile-status", 512), log: text("compiler-log", 16384), preview: text("preview-status", 512),
      canvas: text("canvas-status", 512), runtime: text("runtime-status", 512), pdfRuntime: text("pdf-runtime-status", 512),
      extentStatus: text("rendered-extent-status", 1024), extentText: text("rendered-extent-result", 8192),
      extentAttributes: r ? Object.fromEntries([...r.attributes].filter(a => a.name.startsWith("data-")).map(a => [a.name, a.value])) : null };
  });
}
save();
(async () => {
  let timer; const deadline = new Promise((_, reject) => { timer = setTimeout(() => { stopped = true; reject(new Error("harness deadline360s")); }, 360000); });
  try { await Promise.race([run(), deadline]); }
  catch (e) { fail(e); save(); try { await bounded(failureEvidence(), 5000, "failure observation timeout"); } catch {} process.exitCode = 1; }
  finally {
    stopped = true; clearTimeout(timer);
    if (launching) { try { context = await bounded(launching, 20000, "late launch cleanup timeout"); } catch (e) { fail(e, "CLEANUP"); process.exitCode = 1; } }
    try { if (context) { try { await bounded(collectCsp(), 5000, "CSP cleanup timeout"); } finally { await bounded(context.close(), 10000, "browser cleanup timeout"); context = null; } } }
    catch (e) { fail(e, "CLEANUP"); process.exitCode = 1; }
    try { record.cleanup = { remainingChromium: await reap() }; } catch (e) { fail(e, "CLEANUP"); process.exitCode = 1; }
    try { await stopOrigin(); } catch (e) { fail(e, "CLEANUP"); process.exitCode = 1; }
    if (profile) { try { fs.rmSync(profile, { recursive: true, force: true }); record.profileCleaned = true; } catch (e) { fail(e, "CLEANUP"); process.exitCode = 1; } }
    try { cleanSignals(); } catch (e) { fail(e, "OBSERVED_FAILURE"); process.exitCode = 1; }
    save(); process.stdout.write(JSON.stringify({ result: record.result, failure: record.failure }) + "\n");
  }
})();
