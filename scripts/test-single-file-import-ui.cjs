// Actual published-app single-file import: additive exact bytes and session/lifetime refusal.
// Root owns execution. No product/private coordinator/storage-write APIs, fabricated
// read bytes, native/provider calls or old harness IIFE. The ONLY controlled delay
// wraps selected-fixture Blob.slice/arrayBuffer: ORIGINAL native APIs supply the
// actual ArrayBuffer; delivery is held/released, never replaced. Unsupported read
// boundary is a coverage failure/gap, not a manufactured race PASS.
// Required env: PUBLISHED_WWWROOT, RESULT_DIR (exclusive writable results).
const assert = require("node:assert/strict"), fs = require("node:fs"), os = require("node:os"), path = require("node:path"), http = require("node:http");
const { createHash } = require("node:crypto"), { inflateRawSync } = require("node:zlib");
const H = require("./review-ui-helpers.cjs"), { validate: validateCsp } = require("./test-csp.cjs"), { crc32 } = require("./zip-util.cjs");
const need = n => { assert.ok(process.env[n], `${n} required`); return process.env[n]; };
const sha = b => createHash("sha256").update(b).digest("hex"), identity = b => ({ bytes: b.length, sha256: sha(b) });
const OUT = path.resolve(need("RESULT_DIR")), RECEIPT = path.join(OUT, "single-file-import-receipt.json");
assert.ok(fs.statSync(OUT).isDirectory()); fs.closeSync(fs.openSync(RECEIPT, "wx"));
const PORT = 8774, BUDGET = 16 * 1024 * 1024, NATIVE = /\/__oop\/native-worker\/(?:compile|pdf)\b/;
const TEXT = { name: "note-bom.tex", mimeType: "text/plain", bytes: Buffer.concat([Buffer.from([239, 187, 191]), Buffer.from("% Україна 😀\r\nExact imported source.\r\n", "utf8")]) };
const BINARY = { name: "arbitrary.bin", mimeType: "application/octet-stream", bytes: Buffer.from([0, 255, 254, 128, 1, 2, 13, 10, 0, 73, 255, 17, 31]) };
const LATE_B = { name: "late-from-a.tex", mimeType: "text/plain", bytes: Buffer.from("% Real held read A to B\r\nNever insert into B.\r\n") };
const LATE_A = { name: "late-old-session.tex", mimeType: "text/plain", bytes: Buffer.from("% Real held old A session\r\nNever insert into reopened A.\r\n") };
const FRESH = { name: "fresh-after-stale.txt", mimeType: "text/plain", bytes: Buffer.from("Fresh ordinary import after old operations settle.\r\n", "utf8") };
const CANCEL = { name: "cancelled-read.txt", mimeType: "text/plain", bytes: Buffer.from("Selected real bytes for optional cancellation.\n") };
const OFFLINE = { name: "offline.bin", mimeType: "application/octet-stream", bytes: Buffer.from([255, 0, 13, 10, 240, 159, 152, 128, 129, 42, 0, 7]) };
const sentA = Buffer.from("Saved sentinel A.\n"), sentB = Buffer.from("Saved sentinel B.\n");
const fixtureMeta = f => ({ name: f.name, mimeType: f.mimeType, ...identity(f.bytes) });
const record = { result: "FAIL", scope: "session-safe-additive-single-file-import-online-cold-offline", actualBrowserExecution: "started by caller",
  sdk: null, fixtures: [TEXT, BINARY, LATE_B, LATE_A, FRESH, CANCEL, OFFLINE].map(fixtureMeta), online: {}, cold: {},
  imports: [], reads: [], refusals: [], artifacts: [], workers: [], externalAttempts: [], externalSuccesses: [], pageErrors: [], violations: [],
  failure: null, cleanup: null,
  qualification: ["Only real browser File/Blob read observations; no product-private/noncooperative foreign callback is manufactured.",
    "InputFile unmount may cancel/refuse a real read. That is valid if no later source/session mutation; not proof of prior corruption.",
    "Managed tests own extra read-slot/disposal/noncooperative races. Arbitrary binary byte preservation is not image-decoder or compiler compatibility proof."] };
let root, inputs, chromium, playwrightErrors, page = null, context = null, launching = null, server = null, profile = null, base = null;
let phase = "output-preflight", category = "PREFLIGHT", stopped = false, lastClosedSession = null;
const running = () => { if (stopped) throw new Error("Deadline or cleanup reached"); };
const stage = (p, c) => { running(); phase = p; category = c; };
const nativeCount = () => record.workers.filter(w => NATIVE.test(w.url)).length;
function outputBytes() {
  let total = 0, n = 0; const pending = [OUT];
  while (pending.length) { const dir = pending.pop(); for (const name of fs.readdirSync(dir)) {
    assert.ok(++n <= 4096); const p = path.join(dir, name), s = fs.lstatSync(p); assert.ok(!s.isSymbolicLink());
    if (s.isDirectory()) pending.push(p); else { assert.ok(s.isFile()); total += s.size; }
  } } return total;
}
function save() {
  const data = JSON.stringify(record, null, 2) + "\n"; assert.ok(Buffer.byteLength(data) <= 262144);
  assert.ok(outputBytes() - fs.statSync(RECEIPT).size + Buffer.byteLength(data) <= BUDGET); fs.writeFileSync(RECEIPT, data);
}
function fail(e, kind = category) {
  if (!record.failure) record.failure = { phase, classification: kind, message: String(e.message || e).slice(0, 1024),
    stack: typeof e.stack === "string" ? e.stack.slice(0, 4096) : null };
  record.result = "FAIL";
}
function append(list, value, max = 128) { if (list.length < max) list.push(value); else record.observationOverflow = true; }
async function bounded(promise, ms, message) { let timer; try { return await Promise.race([promise, new Promise((_, reject) => { timer = setTimeout(() => reject(new Error(message)), ms); })]); } finally { clearTimeout(timer); } }
function cleanSignals() {
  assert.ok(!record.observationOverflow); assert.equal(nativeCount(), 0, "source-only workflow must never start compile/PDF workers");
  for (const n of ["externalAttempts", "externalSuccesses", "pageErrors", "violations"]) assert.deepEqual(record[n], [], n);
}
function preflight() {
  root = fs.realpathSync(need("PUBLISHED_WWWROOT")); assert.ok(outputBytes() <= BUDGET - 3 * 1024 * 1024);
  const headers = fs.readFileSync(path.join(__dirname, "../docs/examples/static-csp-headers.conf"), "utf8");
  validateCsp(fs.readFileSync(path.join(root, "index.html"), "utf8"), headers);
  const line = headers.split("\n").map(s => s.trim()).find(s => s.startsWith("Content-Security-Policy:")); assert.ok(line);
  const manifestText = fs.readFileSync(path.join(root, "service-worker-assets.js"), "utf8");
  const m = manifestText.match(/self\.assetsManifest\s*=\s*(\{[\s\S]*\})\s*;/); assert.ok(m);
  const manifest = JSON.parse(m[1]); assert.equal(typeof manifest.version, "string");
  assert.ok(Array.isArray(manifest.assets) && manifest.assets.length > 0 && manifest.assets.length <= 4096);
  const names = new Set();
  for (const a of manifest.assets) {
    assert.equal(typeof a.url, "string"); assert.ok(!path.isAbsolute(a.url) && !a.url.includes("\\") && !a.url.split("/").includes(".."));
    assert.ok(!names.has(a.url)); names.add(a.url); const p = fs.realpathSync(path.join(root, a.url)); assert.ok(p.startsWith(root + path.sep));
    assert.equal("sha256-" + createHash("sha256").update(fs.readFileSync(p)).digest("base64"), a.hash, a.url);
  }
  for (const n of ["index.html", "_framework/blazor.webassembly.js", "js/workspace-storage.js", "js/download.js", "js/offline-readiness.js"])
    assert.ok(names.has(n), "required source/PWA workflow asset: " + n);
  record.sdk = { version: manifest.version, observedAssets: manifest.assets.length, manifestSha256: sha(Buffer.from(manifestText)), everySriVerified: true, strictCsp: true };
  return { headers, csp: line.slice("Content-Security-Policy:".length).trim() };
}
const TYPES = { ".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8", ".mjs": "text/javascript", ".css": "text/css",
  ".json": "application/json", ".wasm": "application/wasm", ".png": "image/png", ".svg": "image/svg+xml", ".ico": "image/x-icon",
  ".txt": "text/plain", ".dat": "application/octet-stream", ".blat": "application/octet-stream", ".pdb": "application/octet-stream" };
async function serve() {
  server = http.createServer((req, res) => {
    let name, file; try { name = decodeURIComponent(new URL(req.url, `http://127.0.0.1:${PORT}`).pathname); } catch { res.writeHead(400); res.end(); return; }
    if (name.includes("\\") || name.includes("\0") || name.split("/").includes("..")) { res.writeHead(400); res.end(); return; }
    try { file = fs.realpathSync(path.resolve(root, "." + (name === "/" ? "/index.html" : name))); } catch { res.writeHead(404); res.end(); return; }
    if (!file.startsWith(root + path.sep) || !fs.statSync(file).isFile()) { res.writeHead(404); res.end(); return; }
    res.writeHead(200, { "Content-Type": TYPES[path.extname(file).toLowerCase()] || "application/octet-stream", "Cache-Control": "no-store", "Content-Security-Policy": inputs.csp, "X-Content-Type-Options": "nosniff" });
    const s = fs.createReadStream(file); s.on("error", () => res.destroy()); s.pipe(res);
  });
  await new Promise((resolve, reject) => { server.once("error", reject); server.listen(PORT, "127.0.0.1", resolve); }); base = `http://127.0.0.1:${PORT}`;
}
function census() {
  const found = [];
  for (const pid of fs.readdirSync("/proc")) {
    if (!/^\d+$/.test(pid)) continue; let exe, stat;
    try { exe = path.basename(fs.readlinkSync(`/proc/${pid}/exe`)); } catch { continue; }
    if (!/chromium|chrome|headless_shell/i.test(exe)) continue;
    try { stat = fs.readFileSync(`/proc/${pid}/stat`, "utf8"); } catch (e) { if (["ENOENT", "ESRCH"].includes(e.code)) continue; throw e; }
    const startticks = stat.slice(stat.lastIndexOf(") ") + 2).split(" ")[19]; assert.match(startticks, /^\d+$/); found.push({ pid: Number(pid), startticks, exe });
  } return found.sort((a, b) => a.pid - b.pid);
}
async function reap() { let c; for (let i = 0; i < 40; i++) { c = census(); if (!c.length) return c; await new Promise(r => setTimeout(r, 100)); } assert.deepEqual(c, []); return c; }
async function stopOrigin() { if (server?.listening) await bounded(new Promise((resolve, reject) => { server.close(e => e ? reject(e) : resolve()); server.closeIdleConnections(); }), 5000, "origin close timeout"); }
async function refusal() {
  await new Promise((resolve, reject) => { const r = http.get(base, response => { response.resume(); reject(new Error("origin still alive")); });
    r.on("error", e => e.code === "ECONNREFUSED" ? resolve() : reject(e)); r.setTimeout(2000, () => r.destroy(new Error("origin refusal unproven"))); });
}
async function launch(offline) {
  running(); launching = chromium.launchPersistentContext(profile, { headless: true, offline, acceptDownloads: true, timeout: 15000 }); context = await launching; launching = null; running();
  context.on("request", r => { const u = new URL(r.url()); if (/^https?:$/.test(u.protocol) && u.origin !== base) append(record.externalAttempts, { phase, origin: u.origin }); });
  await context.route("**/*", r => new URL(r.request().url()).origin === base ? r.continue() : r.abort());
  await context.addInitScript(() => { window.__importCsp = []; addEventListener("securitypolicyviolation", e => window.__importCsp.push(e.effectiveDirective)); });
  const watch = p => { p.on("worker", w => append(record.workers, { phase, url: w.url() })); p.on("pageerror", e => append(record.pageErrors, { phase, message: String(e.message).slice(0, 1024) })); };
  context.pages().forEach(watch); context.on("page", watch); context.on("response", r => { const u = new URL(r.url()); if (u.origin !== base && r.ok()) append(record.externalSuccesses, { phase, origin: u.origin }); });
  page = await context.newPage(); page.setDefaultTimeout(30000);
}
async function collectCsp() { if (context) for (const p of context.pages()) if (new URL(p.url()).origin === base) for (const directive of await p.evaluate(() => window.__importCsp || [])) append(record.violations, { phase, directive }); }
async function projectRecord(name) {
  return page.evaluate(async name => { const m = await import("./js/workspace-storage.js"); const matches = (await m.listProjects()).filter(p => p.name === name);
    if (matches.length !== 1) throw new Error("Expected one controlled project"); const p = matches[0]; return { id: p.id, name: p.name, head: p.headRevisionId ?? null }; }, name);
}
async function selection() { return page.evaluate(() => ({ main: document.querySelector('select[aria-label="Compile main file"]')?.value,
  engine: document.querySelector('select[aria-label="Compile engine"]')?.value, bibtex: (document.querySelector('[data-testid="bibliography-tool"]') ? document.querySelector('[data-testid="bibliography-tool"]').value === "Bibtex" : document.querySelector('[data-testid="use-bibtex"]')?.checked) ?? null,
  version: document.querySelector('[data-testid="compile-panel"]')?.dataset.selectionVersion, output: document.querySelector('[data-testid="compile-panel"]')?.dataset.outputHash ?? null })); }
async function snapshot(project) {
  return { project: await projectRecord(project), stamp: await H.stamp(page), head: await H.head(page, project), config: await selection(),
    view: await page.evaluate(() => { const e = document.querySelector('[data-testid="editor"]'); return { label: document.querySelector('.editor-toolbar label[for="editor"]')?.textContent,
      file: e?.dataset.fileId ?? null, source: e?.value ?? null, start: e?.selectionStart ?? null, end: e?.selectionEnd ?? null,
      binary: !!document.querySelector('[data-testid="binary-view"]'), files: [...document.querySelectorAll('[data-testid="select-file"] .file-name')].map(e => e.textContent) }; }) };
}
async function status() { const s = H.$(page, "single-file-import-status"); await s.waitFor({ state: "attached" }); return { state: await s.getAttribute("data-state"), busy: await s.getAttribute("data-busy"), text: (await s.textContent()).slice(0, 1024) }; }
async function settledImport(expected = null) {
  await page.waitForFunction(expected => { const s = document.querySelector('[data-testid="single-file-import-status"]'); return s?.dataset.busy === "false" && (expected === null || s.dataset.state === expected); }, expected, { timeout: 30000 });
  const s = await status(); assert.ok(["idle", "reading", "imported", "stale", "cancelled", "busy", "failed", "disposed"].includes(s.state)); assert.equal(s.busy, "false");
  assert.equal(await H.$(page, "import-input").isEnabled(), true); return s;
}
async function selectFile(name) {
  stage("select-file:" + name, "PROJECT_NAVIGATION");
  const b = H.$(page, "select-file").filter({ hasText: name }); await b.waitFor({ state: "attached" });
  assert.equal(await b.count(), 1); await b.click();
  await page.locator('.editor-toolbar label[for="editor"]', { hasText: name }).waitFor();
}
async function settleSave(project) {
  const writable = async () => { assert.equal(await page.locator(".banner-readonly, .banner-recovery").count(), 0); assert.equal((await page.locator(".workspace-header h1").textContent()).trim(), project); };
  await writable(); const b = H.$(page, "save"); let mode = "already-saved";
  if (await b.isEnabled()) { try { await b.click({ timeout: 2000 }); mode = "explicit-click"; } catch (e) { if (!(e instanceof playwrightErrors.TimeoutError) || !(await b.isDisabled())) throw e; mode = "autosave-raced-click"; } }
  await page.waitForFunction(() => document.querySelector(".workspace-header .status")?.textContent === "Saved." && !document.querySelector(".banner-readonly, .banner-recovery"), null, { timeout: 30000 });
  await writable(); return { mode, status: "Saved." }; // exactHead separately validates bytes/head
}
async function exactHead(project, files, expected = null) {
  const head = await H.head(page, project); if (expected) assert.deepEqual(head, expected);
  assert.deepEqual(head.files.map(f => f.path).sort(), Object.keys(files).sort());
  for (const [name, bytes] of Object.entries(files)) assert.deepEqual(Buffer.from(head.files.find(f => f.path === name).bytes), bytes, `exact saved bytes ${name}`);
  return head;
}
async function settleConfiguration() {
  // Observe normal initial tool configuration only, without Check tools or any
  // provisioning. Its legitimate startup must not race config invariance checks.
  await H.reveal(page, H.$(page, "refresh-compile-tools"));
  await page.waitForFunction(() => document.querySelector('[data-testid="refresh-compile-tools"]')?.disabled === false, null, { timeout: 60000 });
}
async function waitDestination(name, file = null, previousSession = lastClosedSession) {
  stage("wait-destination:" + name, "PROJECT_NAVIGATION");
  // H.open's analysis-profile wait can match the OLD workspace. Wait for the
  // actual destination title first, then bind its saved head/file identities.
  await page.waitForFunction(name => document.querySelector(".workspace-header h1")?.textContent.trim() === name
    && !!document.querySelector("main.workbench") && !document.querySelector("main.project-dashboard"), name, { timeout: 30000 });
  const expected = await projectRecord(name), head = expected.head ? await H.head(page, name) : null;
  const target = file === null ? null : head?.files.find(f => f.path === file);
  if (file !== null) assert.ok(target, "destination saved file must exist: " + file);
  await page.waitForFunction(({ name, head, file, previousSession }) => {
    const header = document.querySelector(".workspace-header h1"), state = document.querySelector('[data-testid="analysis-panel"]');
    const id = s => String(s ?? "").replace(/-/g, "").toLowerCase();
    return header?.textContent.trim() === name && !!document.querySelector("main.workbench")
      && !!state?.dataset.session && (!previousSession || id(state.dataset.session) !== id(previousSession))
      && id(state.dataset.head) === id(head)
      && (file === null || [...document.querySelectorAll('[data-testid="select-file"] .file-name')].some(e => e.textContent.trim() === file));
  }, { name, head: expected.head, file, previousSession }, { timeout: 30000 });
  const stamp = await H.stamp(page); assert.ok(stamp.session);
  const actualHeader = (await page.locator(".workspace-header h1").textContent()).trim(); assert.equal(actualHeader, name);
  record.navigation ??= [];
  append(record.navigation, { action: "destination-loaded", expectedProject: expected, actualHeader,
    stamp, previousSession, targetFile: target ? { path: target.path, id: target.id } : null,
    identityScope: "Exact UI title/session/head plus readonly destination project/head/file; no UI project-id attribute exists." });
  return target;
}
async function createProject(name, sentinelName, bytes) {
  stage("create-project:" + name, "PROJECT_SETUP");
  await H.reveal(page, page.locator("#new-project")); await page.locator("#new-project").fill(name); await page.getByRole("button", { name: "Create project", exact: true }).click();
  await waitDestination(name);
  stage("create-sentinel:" + sentinelName, "PROJECT_SETUP");
  await H.$(page, "new-file-name").fill(sentinelName); await H.$(page, "new-file").click(); await page.locator('.editor-toolbar label[for="editor"]', { hasText: sentinelName }).waitFor();
  stage("save-sentinel:" + name, "PROJECT_SETUP");
  await H.$(page, "editor").fill(bytes.toString("utf8")); const saved = await settleSave(name); const head = await exactHead(name, { [sentinelName]: bytes });
  await settledImport(); await settleConfiguration(); return { project: await projectRecord(name), stamp: await H.stamp(page), head, saved };
}
async function closeProject() {
  const title = (await page.locator(".workspace-header h1").textContent()).trim(), stamp = await H.stamp(page);
  stage("close-project:" + title, "PROJECT_NAVIGATION");
  await page.locator('.workspace-header button[aria-label="Close"]').click();
  await page.locator("main.project-dashboard").waitFor();
  await page.waitForFunction(() => !!document.querySelector("main.project-dashboard") && !document.querySelector(".workspace-header"), null, { timeout: 30000 });
  lastClosedSession = stamp.session;
  record.navigation ??= []; append(record.navigation, { action: "closed-to-dashboard", title, session: stamp.session });
}
async function openProject(name, file, { waitForImportIdle = true, previousSession = lastClosedSession } = {}) {
  stage("open-project:" + name, "PROJECT_NAVIGATION");
  await H.open(page, name); // ALWAYS a real row click, never a count()/isVisible skip
  const target = await waitDestination(name, file, previousSession);
  await selectFile(file);
  const text = Buffer.from(target.bytes).toString("utf8").replace(/^\uFEFF/, "").replace(/\r\n?/g, "\n");
  await page.waitForFunction(({ id, text }) => {
    const e = document.querySelector('[data-testid="editor"]');
    return e?.dataset.fileId?.replace(/-/g, "").toLowerCase() === String(id).replace(/-/g, "").toLowerCase() && e.value === text;
  }, { id: target.id, text }, { timeout: 30000 });
  await settleConfiguration();
  if (waitForImportIdle) await settledImport(); // held old reads release AFTER the new-view baseline
}
async function pick(fixture) { await H.$(page, "import-input").setInputFiles({ name: fixture.name, mimeType: fixture.mimeType, buffer: fixture.bytes }); }

// Fixture-specific browser API hold, not a provider replacement or product hook.
async function armRead(fixture) {
  stage(phase, "REAL_READ_INSTRUMENTATION");
  const info = await page.evaluate(({ name, size }) => {
    const previous = window.__singleImportReadGate;
    if (previous && !previous.stats.restored) throw new Error("Prior test read hook not restored");
    const sd = Object.getOwnPropertyDescriptor(Blob.prototype, "slice"), ad = Object.getOwnPropertyDescriptor(Blob.prototype, "arrayBuffer");
    const stats = { name, size, supported: !!sd?.value && typeof ad?.value === "function", entered: false, released: false, restored: false,
      nativeStarted: false, nativeResolved: false, nativeError: null, deliverySettled: false, delivered: false, events: [] };
    if (!stats.supported) { window.__singleImportReadGate = { stats }; return stats; }
    const slices = new WeakSet(); let release;
    const gate = new Promise(r => { release = r; });
    const mark = event => { if (stats.events.length < 24) stats.events.push(event); };
    const matches = b => b instanceof File && b.name === name && b.size === size;
    function slice(...args) {
      const value = sd.value.apply(this, args); // genuine browser Blob, unchanged
      if (matches(this) || slices.has(this)) { slices.add(value); stats.selectedFile = { name, size, type: this.type }; }
      return value;
    }
    function arrayBuffer(...args) {
      if ((!matches(this) && !slices.has(this)) || stats.entered) return ad.value.apply(this, args);
      stats.entered = true; stats.nativeStarted = true; stats.held = true; mark("native-arrayBuffer-started");
      // Invoke ORIGINAL native API BEFORE delaying delivery. Keep its actual
      // ArrayBuffer object; never substitute any buffer/content or mutate bytes.
      const actual = ad.value.apply(this, args);
      return actual.then(async value => {
        stats.nativeResolved = true; stats.nativeLength = value.byteLength; stats.resolvedWhileHeld = !stats.released; mark("native-arrayBuffer-resolved");
        stats.nativeSha256 = [...new Uint8Array(await crypto.subtle.digest("SHA-256", value))].map(b => b.toString(16).padStart(2, "0")).join("");
        await gate; stats.delivered = true; mark("original-buffer-delivered"); return value;
      }, error => { stats.nativeError = { name: error.name, message: String(error.message).slice(0, 512) }; mark("native-arrayBuffer-rejected"); throw error; })
        .finally(() => { stats.deliverySettled = true; stats.held = false; });
    }
    Object.defineProperty(Blob.prototype, "slice", { ...sd, value: slice });
    Object.defineProperty(Blob.prototype, "arrayBuffer", { ...ad, value: arrayBuffer });
    window.__singleImportReadGate = { stats, release() { if (!stats.released) { stats.released = true; mark("test-release"); release(); } },
      restore() {
        if (stats.restored) return;
        if (Blob.prototype.slice !== slice || Blob.prototype.arrayBuffer !== arrayBuffer) throw new Error("Read hook ownership changed; not clobbering it");
        Object.defineProperty(Blob.prototype, "slice", sd); Object.defineProperty(Blob.prototype, "arrayBuffer", ad); stats.restored = true; mark("original-APIs-restored");
      } };
    return stats;
  }, { name: fixture.name, size: fixture.bytes.length });
  record.reads.push({ fixture: fixtureMeta(fixture), arm: info }); save();
  if (!info.supported) { const e = new Error("Unsupported actual browser Blob read boundary; no race coverage claimed"); e.coverageGap = true; throw e; }
}
async function readStats() { return page.evaluate(() => structuredClone(window.__singleImportReadGate?.stats || null)); }
async function holdImport(fixture) {
  await armRead(fixture); await pick(fixture);
  try { await page.waitForFunction(() => window.__singleImportReadGate?.stats.entered === true, null, { timeout: 15000 }); }
  catch (e) { if (!(e instanceof playwrightErrors.TimeoutError)) throw e; record.reads.at(-1).unsupportedObservation = await readStats(); save(); const gap = new Error("Selected real import did not reach supported Blob.arrayBuffer boundary; race coverage gap, not PASS"); gap.coverageGap = true; throw gap; }
  await page.waitForFunction(() => { const s = document.querySelector('[data-testid="single-file-import-status"]'); return s?.dataset.state === "reading" && s.dataset.busy === "true"; });
  assert.equal(await H.$(page, "import-input").isDisabled(), true); const s = await readStats(); assert.equal(s.nativeStarted, true); assert.equal(s.released, false);
  record.reads.at(-1).held = { stats: s, ui: await status() }; save();
}
async function releaseRead() {
  await page.evaluate(() => window.__singleImportReadGate.release());
  await page.waitForFunction(() => window.__singleImportReadGate?.stats.deliverySettled === true, null, { timeout: 30000 });
}
async function restoreRead() { await page.evaluate(() => window.__singleImportReadGate.restore()); const stats = await readStats(); record.reads.at(-1).final = stats; save(); return stats; }
async function assertRefusal() {
  const state = await settledImport(); assert.ok(["idle", "stale", "cancelled", "failed", "disposed"].includes(state.state), "old-lifetime read must not report a new successful import");
  return state;
}
async function importValid(project, fixture, files) {
  stage("import-" + fixture.name, "VALID_IMPORT"); const configBefore = await selection(); await pick(fixture); const imported = await settledImport("imported");
  await page.locator('.editor-toolbar label[for="editor"]', { hasText: fixture.name }).waitFor(); assert.equal(await H.$(page, "select-file").filter({ hasText: fixture.name }).count(), 1);
  const saved = await settleSave(project); const head = await exactHead(project, files); assert.deepEqual(await selection(), configBefore);
  record.imports.push({ phase, fixture: fixtureMeta(fixture), status: imported, saved, head }); cleanSignals(); save(); return head;
}
async function downloadActual(trigger, label, maxBytes = 1048576) {
  const target = path.join(OUT, label); assert.ok(!fs.existsSync(target)); const event = page.waitForEvent("download", { timeout: 30000 });
  const [d] = await Promise.all([event, trigger.click()]); assert.equal(await bounded(d.failure(), 30000, "download completion timeout"), null);
  const staged = await bounded(d.path(), 5000, "download staging timeout"); const size = fs.statSync(staged).size;
  assert.ok(size > 0 && size <= maxBytes); assert.ok(outputBytes() + size + 262144 <= BUDGET); await bounded(d.saveAs(target), 30000, "saveAs timeout");
  const bytes = fs.readFileSync(target); assert.equal(bytes.length, size); const meta = { artifact: label, suggested: d.suggestedFilename(), ...identity(bytes), failure: null };
  record.artifacts.push(meta); return { bytes, meta };
}
async function fileDownload(project, fixture, label, binary) {
  await selectFile(fixture.name); const trigger = H.$(page, binary ? "download-binary" : "download"); await H.reveal(page, trigger);
  const before = await snapshot(project), d = await downloadActual(trigger, label); assert.equal(d.meta.suggested, fixture.name); assert.deepEqual(d.bytes, fixture.bytes);
  assert.deepEqual(await snapshot(project), before, "file download is byte-preserving and read-only"); cleanSignals(); save(); return d.meta;
}
// Read only the REAL downloaded ZIP. No test archive is injected into the app.
function unzip(bytes) {
  assert.ok(bytes.length >= 22 && bytes.length <= 1048576); let end = -1;
  for (let i = bytes.length - 22; i >= Math.max(0, bytes.length - 65557); i--) if (bytes.readUInt32LE(i) === 0x06054b50 && i + 22 + bytes.readUInt16LE(i + 20) === bytes.length) { end = i; break; }
  assert.ok(end >= 0); assert.equal(bytes.readUInt16LE(end + 4), 0); assert.equal(bytes.readUInt16LE(end + 6), 0);
  const n = bytes.readUInt16LE(end + 10), size = bytes.readUInt32LE(end + 12), start = bytes.readUInt32LE(end + 16);
  assert.equal(bytes.readUInt16LE(end + 8), n); assert.ok(n > 0 && n <= 32 && start + size === end);
  const files = Object.create(null); let at = start, total = 0;
  for (let i = 0; i < n; i++) {
    assert.ok(at + 46 <= end); assert.equal(bytes.readUInt32LE(at), 0x02014b50);
    const flags = bytes.readUInt16LE(at + 8), method = bytes.readUInt16LE(at + 10), crc = bytes.readUInt32LE(at + 16), packed = bytes.readUInt32LE(at + 20), length = bytes.readUInt32LE(at + 24);
    const nl = bytes.readUInt16LE(at + 28), ex = bytes.readUInt16LE(at + 30), comment = bytes.readUInt16LE(at + 32), local = bytes.readUInt32LE(at + 42);
    assert.ok(!(flags & 1) && [0, 8].includes(method) && nl > 0 && nl <= 200 && length <= 32768); total += length; assert.ok(total <= 32768);
    assert.ok(at + 46 + nl + ex + comment <= end); const name = bytes.subarray(at + 46, at + 46 + nl).toString("utf8");
    assert.match(name, /^[A-Za-z0-9._-]+$/); assert.ok(![".", ".."].includes(name) && !Object.hasOwn(files, name));
    assert.ok(local + 30 <= start); assert.equal(bytes.readUInt32LE(local), 0x04034b50); assert.equal(bytes.readUInt16LE(local + 8), method);
    const localName = bytes.readUInt16LE(local + 26), localExtra = bytes.readUInt16LE(local + 28), dataStart = local + 30 + localName + localExtra;
    assert.equal(bytes.subarray(local + 30, local + 30 + localName).toString("utf8"), name); assert.ok(dataStart + packed <= start);
    const packedData = bytes.subarray(dataStart, dataStart + packed); const data = method === 0 ? Buffer.from(packedData) : inflateRawSync(packedData, { maxOutputLength: 32768 });
    assert.equal(data.length, length); assert.equal(crc32(data), crc); files[name] = data; at += 46 + nl + ex + comment;
  }
  assert.equal(at, end); return files;
}
async function zipDownload(project, expected, label) {
  const trigger = H.$(page, "export-zip"); await H.reveal(page, trigger); const before = await snapshot(project), d = await downloadActual(trigger, label);
  assert.match(d.meta.suggested, /\.zip$/i); const files = unzip(d.bytes); assert.deepEqual(Object.keys(files).sort(), Object.keys(expected).sort());
  for (const [name, bytes] of Object.entries(expected)) assert.deepEqual(files[name], bytes, "ZIP exact entry " + name);
  assert.deepEqual(await snapshot(project), before, "export cannot alter source/head/config/generation/selection");
  d.meta.entries = Object.fromEntries(Object.entries(files).map(([n, b]) => [n, identity(b)])); cleanSignals(); save(); return d.meta;
}
async function staleCase(a, target, sentinel, fixture, label) {
  stage(label, "SESSION_SWITCH_REAL_READ"); const original = await snapshot(a); await holdImport(fixture); const oldSession = original.stamp.session;
  await closeProject(); await openProject(target, sentinel, { waitForImportIdle: false, previousSession: oldSession });
  const before = await snapshot(target); assert.notEqual(before.stamp.session, oldSession);
  if (a === target) assert.equal(before.project.id, original.project.id); else assert.notEqual(before.project.id, original.project.id);
  const beforeRelease = { ui: await status(), read: await readStats() }; await releaseRead(); const refused = await assertRefusal(); const read = await restoreRead();
  const after = await snapshot(target);
  record.refusals.push({ label, original, before, after, beforeRelease, refused, read,
    classification: read.nativeError ? "actual-native-read-error-with-refusal" : "old-session-read-refused-or-unmounted-cancelled",
    qualification: "No private original-controller completion hook. New-lifetime idle may reflect unmount; later exact heads/download/export also guard delayed mutation." }); save();
  assert.deepEqual(after, before, "released old read must not mutate target head/content/selection/generation/config");
  assert.equal(await H.$(page, "select-file").filter({ hasText: fixture.name }).count(), 0); cleanSignals();
}
async function run() {
  inputs = preflight(); ({ chromium, errors: playwrightErrors } = require("playwright")); save(); assert.deepEqual(census(), []);
  profile = fs.mkdtempSync(path.join(os.tmpdir(), "single-import-profile-")); await serve(); stage("online-start", "UI_STARTUP"); await launch(false);
  record.online.processes = census(); assert.ok(record.online.processes.length); const r = await page.goto(base); assert.ok(r?.ok()); validateCsp(await r.text(), inputs.headers);
  assert.equal(r.headers()["content-security-policy"], inputs.csp); await H.ready(page);
  const a = `SingleImport-A-${Date.now()}`, b = `SingleImport-B-${Date.now()}`; record.projects = { a, b };
  stage("create-sentinel-projects", "PROJECT_SETUP"); record.online.a = await createProject(a, "a-sentinel.txt", sentA); await closeProject();
  record.online.b = await createProject(b, "b-sentinel.txt", sentB); await closeProject(); await openProject(a, "a-sentinel.txt");
  const bOriginal = await H.head(page, b); assert.deepEqual(bOriginal, record.online.b.head);
  await exactHead(a, { "a-sentinel.txt": sentA }, record.online.a.head); let files = { "a-sentinel.txt": sentA };
  stage("same-session-real-import-with-user-edit-save", "VALID_IMPORT"); const old = await snapshot(a); await holdImport(TEXT);
  const editedSentinel = Buffer.from(sentA.toString("utf8") + "User edit while real import is pending.\n");
  await H.$(page, "editor").fill(editedSentinel.toString("utf8")); const duringSave = await settleSave(a); files["a-sentinel.txt"] = editedSentinel;
  const pendingHead = await exactHead(a, files); assert.equal((await H.stamp(page)).session, old.stamp.session);
  assert.equal((await status()).busy, "true"); assert.equal(await H.$(page, "import-input").isDisabled(), true);
  await releaseRead(); const imported = await settledImport("imported"); const read = await restoreRead();
  if (read.nativeLength === TEXT.bytes.length) assert.equal(read.nativeSha256, sha(TEXT.bytes));
  files[TEXT.name] = TEXT.bytes; const saved = await settleSave(a); let head = await exactHead(a, files);
  await page.locator('.editor-toolbar label[for="editor"]', { hasText: TEXT.name }).waitFor();
  assert.equal(await H.$(page, "editor").inputValue(), TEXT.bytes.subarray(3).toString("utf8").replace(/\r\n/g, "\n"));
  assert.deepEqual(await selection(), old.config); assert.deepEqual(await H.head(page, b), bOriginal);
  const observedText = Buffer.from(head.files.find(f => f.path === TEXT.name).bytes);
  assert.equal(observedText.subarray(0, 3).toString("hex"), "efbbbf");
  const observedDecoded = observedText.subarray(3).toString("utf8"); assert.ok(observedDecoded.includes("Україна 😀"));
  const crlfPairs = (observedDecoded.match(/\r\n/g) || []).length; assert.equal(crlfPairs, 2);
  record.online.sameSession = { old, duringSave, pendingHead, imported, saved, read, head, unrelatedUserEditPreserved: true,
    exactText: { ...identity(observedText), bomHex: observedText.subarray(0, 3).toString("hex"), crlfPairs,
      literalUnicodeComment: "Україна 😀", domNormalizedLf: true } }; save();
  await fileDownload(a, TEXT, "online-bom-source.tex", false);
  files[BINARY.name] = BINARY.bytes; head = await importValid(a, BINARY, files); await H.$(page, "binary-view").waitFor();
  await fileDownload(a, BINARY, "online-binary.bin", true); await zipDownload(a, files, "online-before-races.zip");
  const aBeforeRaces = await exactHead(a, files); await selectFile("a-sentinel.txt");
  await staleCase(a, b, "b-sentinel.txt", LATE_B, "held-A-to-B"); await exactHead(b, { "b-sentinel.txt": sentB }, bOriginal);
  assert.deepEqual(await H.head(page, a), aBeforeRaces); await closeProject(); await openProject(a, "a-sentinel.txt");
  await staleCase(a, a, "a-sentinel.txt", LATE_A, "held-A-close-reopen-A"); await exactHead(a, files, aBeforeRaces);
  files[FRESH.name] = FRESH.bytes; head = await importValid(a, FRESH, files);
  stage("duplicate-path-no-overwrite", "DUPLICATE_IMPORT"); const beforeDuplicate = await snapshot(a);
  await pick({ name: TEXT.name, mimeType: "text/plain", bytes: Buffer.from("Must never overwrite accepted bytes.\n") }); const duplicateStatus = await settledImport("failed");
  assert.deepEqual(await snapshot(a), beforeDuplicate); await exactHead(a, files, head); record.online.duplicate = { status: duplicateStatus, unchanged: true };
  stage("real-read-cancel", "CANCEL_REAL_READ"); const beforeCancel = await snapshot(a); await holdImport(CANCEL);
  // Actual product mounts Cancel only during Busy: observe it AFTER entering
  // the real held read, never infer absence from the idle DOM.
  const cancel = H.$(page, "single-file-import-cancel"); await cancel.waitFor({ state: "visible" });
  assert.equal(await cancel.count(), 1); assert.equal(await cancel.isEnabled(), true);
  await cancel.click(); const beforeRelease = { ui: await status(), read: await readStats() }; await releaseRead();
  const cancelled = await settledImport("cancelled"); const cancelledRead = await restoreRead(); const afterCancel = await snapshot(a);
  record.online.cancel = { actualControlPresent: true, exercised: true, before: beforeCancel, beforeRelease,
    cancelled, read: cancelledRead, after: afterCancel, unchanged: true };
  assert.deepEqual(afterCancel, beforeCancel, "cancelled actual read cannot mutate source/head/session/generation/caret/config");
  assert.equal(await H.$(page, "select-file").filter({ hasText: CANCEL.name }).count(), 0);
  const continuation = { ...FRESH, name: "after-cancel.txt" }; files[continuation.name] = continuation.bytes;
  head = await importValid(a, continuation, files);
  record.online.cancel.continuation = { fixture: fixtureMeta(continuation), importedOnce: true, head };
  await exactHead(a, files, head); assert.deepEqual(await H.head(page, b), bOriginal);
  await H.$(page, "history-list").waitFor({ state: "attached" }); await H.reveal(page, H.$(page, "history-list"));
  assert.ok(await H.$(page, "select-history").count() > 0); record.online.history = { visibleSavedCheckpoints: await H.$(page, "select-history").count(), restoredNothing: true };
  await zipDownload(a, files, "online-final.zip");
  stage("wait-offline-cache", "COLD_OFFLINE"); await page.waitForFunction(() => window.__oopOfflineReady === true, null, { timeout: 90000 }); await collectCsp(); cleanSignals();
  record.cold.previousWorkspaceSession = (await H.stamp(page)).session;
  record.cold.oldProcesses = census(); await bounded(context.close(), 10000, "online browser close timeout"); context = null; page = null;
  record.cold.censusZero = await reap(); await stopOrigin(); await refusal(); record.cold.originRefused = true; assert.deepEqual(census(), []);
  stage("cold-new-browser", "COLD_OFFLINE"); await launch(true); record.cold.offlineBeforeNavigation = true; record.cold.newProcesses = census(); assert.ok(record.cold.newProcesses.length);
  const oldIds = new Set(record.cold.oldProcesses.map(p => `${p.pid}:${p.startticks}`)); assert.ok(record.cold.newProcesses.every(p => !oldIds.has(`${p.pid}:${p.startticks}`)));
  await page.goto(base); await H.ready(page); await openProject(a, FRESH.name, { previousSession: record.cold.previousWorkspaceSession });
  await exactHead(a, files, head); assert.deepEqual(await H.head(page, b), bOriginal); record.cold.persisted = { head, fixtures: Object.fromEntries(Object.entries(files).map(([n, bytes]) => [n, identity(bytes)])), bHeadUnchanged: true };
  stage("offline-single-file-import", "OFFLINE_IMPORT"); files[OFFLINE.name] = OFFLINE.bytes; const offlineHead = await importValid(a, OFFLINE, files);
  assert.notEqual(offlineHead.id, head.id); record.cold.importedHead = offlineHead; await fileDownload(a, OFFLINE, "offline-binary.bin", true);
  await fileDownload(a, TEXT, "offline-bom-source.tex", false); await zipDownload(a, files, "offline-project.zip");
  await exactHead(a, files, offlineHead); assert.deepEqual(await H.head(page, b), bOriginal);
  record.cold.finalState = "saved-offline-import; exact bytes/downloads/export; foreign and old-session fixture paths absent";
  await collectCsp(); cleanSignals(); record.result = "PASS";
}
async function failureEvidence() {
  if (!page || page.isClosed()) return;
  record.failure.ui = { import: await status(), read: await readStats(), view: await page.evaluate(() => ({
    project: document.querySelector(".workspace-header h1")?.textContent, save: document.querySelector(".workspace-header .status")?.textContent,
    file: document.querySelector('.editor-toolbar label[for="editor"]')?.textContent, source: document.querySelector('[data-testid="editor"]')?.value?.slice(0, 2048),
    files: [...document.querySelectorAll('[data-testid="select-file"] .file-name')].map(e => e.textContent),
  })) };
}
save();
(async () => {
  let timer; const deadline = new Promise((_, reject) => { timer = setTimeout(() => { stopped = true; reject(new Error("harness deadline300s")); }, 300000); });
  try { await Promise.race([run(), deadline]); }
  catch (e) { fail(e, e.coverageGap ? "UNSUPPORTED_REAL_READ_BOUNDARY" : category); save(); try { await bounded(failureEvidence(), 5000, "failure observation timeout"); } catch {} process.exitCode = 1; }
  finally {
    stopped = true; clearTimeout(timer);
    if (page && !page.isClosed()) { try { record.readHookCleanup = await bounded(page.evaluate(() => { const g = window.__singleImportReadGate; if (!g) return null; if (g.release) g.release(); if (g.restore) g.restore(); return structuredClone(g.stats); }), 5000, "read hook cleanup timeout"); } catch (e) { fail(e, "READ_HOOK_CLEANUP"); process.exitCode = 1; } }
    if (launching) { try { context = await bounded(launching, 20000, "late launch cleanup timeout"); } catch (e) { fail(e, "CLEANUP"); process.exitCode = 1; } }
    try { if (context) { try { await bounded(collectCsp(), 5000, "CSP observation timeout"); } finally { await bounded(context.close(), 10000, "browser cleanup timeout"); context = null; } } } catch (e) { fail(e, "CLEANUP"); process.exitCode = 1; }
    try { record.cleanup = { chromiumRemaining: await reap() }; } catch (e) { fail(e, "CLEANUP"); process.exitCode = 1; }
    try { await stopOrigin(); } catch (e) { fail(e, "CLEANUP"); process.exitCode = 1; }
    if (profile) { try { fs.rmSync(profile, { recursive: true, force: true }); record.profileCleaned = true; } catch (e) { fail(e, "CLEANUP"); process.exitCode = 1; } }
    try { cleanSignals(); } catch (e) { fail(e, "OBSERVED_FAILURE"); process.exitCode = 1; }
    save(); process.stdout.write(JSON.stringify({ result: record.result, failure: record.failure }) + "\n");
  }
})();
