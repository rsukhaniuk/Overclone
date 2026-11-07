// Actual published Overclone UI: explicit in-place binary preview/Confirm/Cancel/
// one Undo, session refusal/shared read admission, then whole-browser cold offline.
// Root owns execution. ORIGINAL File/Blob APIs supply genuine selected bytes;
// fixture-specific delivery holds never fabricate buffers or call private product
// methods. No old harness IIFE, storage writes, native/decoder/provider activity.
// Required env: PUBLISHED_WWWROOT, RESULT_DIR (exclusive writable results).
const assert = require("node:assert/strict"), fs = require("node:fs"), os = require("node:os"), path = require("node:path"), http = require("node:http");
const { createHash } = require("node:crypto"), { inflateRawSync } = require("node:zlib");
const H = require("./review-ui-helpers.cjs"), { validate: validateCsp } = require("./test-csp.cjs"), { crc32 } = require("./zip-util.cjs");
const need = n => { assert.ok(process.env[n], `${n} required`); return process.env[n]; };
const sha = b => createHash("sha256").update(b).digest("hex"), identity = b => ({ bytes: b.length, sha256: sha(b) });
const OUT = path.resolve(need("RESULT_DIR")), RECEIPT = path.join(OUT, "binary-replacement-receipt.json");
assert.ok(fs.statSync(OUT).isDirectory()); fs.closeSync(fs.openSync(RECEIPT, "wx"));
const PORT = 8775, BUDGET = 16 * 1024 * 1024, NATIVE = /\/__oop\/native-worker\/(?:compile|pdf)\b/;
const TARGET = "figures/chart.bin", A_SENTINEL = "a-sentinel.txt", B_SENTINEL = "b-sentinel.txt";
const BOM = Buffer.from([239, 187, 191]);
const A_TEXT = Buffer.concat([BOM, Buffer.from("% Sentinel A Україна 😀\r\nKeep source unchanged.\r\n", "utf8")]);
const B_TEXT = Buffer.concat([BOM, Buffer.from("% Sentinel B Київ 😀\r\nIndependent saved source.\r\n", "utf8")]);
const OLD = { name: "initial.bin", bytes: Buffer.from([0, 255, 254, 128, 1, 2, 13, 10, 0, 73, 255, 17, 31]) };
const NEW = { name: "different-local-name.dat", bytes: Buffer.from([255, 0, 128, 4, 5, 13, 10, 42, 0, 254, 9, 10, 11, 12, 13, 14, 15]) };
const RETRY = { name: "retry-from-disk.bin", bytes: Buffer.from([254, 255, 0, 128, 21, 22, 23, 13, 10, 0, 7, 8, 9, 10, 11, 12, 13, 14, 15]) };
const CANCEL = { name: "cancel-this-selection.bin", bytes: Buffer.from([255, 128, 0, 70, 71, 72, 73, 74, 13, 10, 0]) };
const LATE = { name: "late-selected-file.bin", bytes: Buffer.from([255, 0, 129, 81, 82, 83, 84, 85, 86, 87, 0, 13, 10]) };
const STALE = { name: "never-confirm-this.bin", bytes: Buffer.from([254, 0, 128, 91, 92, 93, 94, 13, 10, 0, 7, 8]) };
const OFFLINE = { name: "offline-new-local-name.bin", bytes: Buffer.from([255, 254, 128, 0, 101, 102, 103, 104, 13, 10, 0, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25]) };
const SHARE = { name: "must-not-add-while-busy.txt", bytes: Buffer.from("Shared admission must refuse this competing real input.\n") };
const meta = f => ({ localName: f.name, ...identity(f.bytes) });
const record = { receiptSchemaVersion: 2, result: "FAIL", scope: "explicit-safe-in-place-binary-replacement-online-cold-offline", actualBrowserExecution: "started by caller",
  sdk: null, fixtures: [OLD, NEW, RETRY, CANCEL, LATE, STALE, OFFLINE, SHARE].map(meta), sentinels: { a: identity(A_TEXT), b: identity(B_TEXT) },
  online: {}, cold: {}, previews: [], actions: [], reads: [], navigation: [], refusedReads: [], history: [], artifacts: [], workers: [],
  pageErrors: [], violations: [], externalAttempts: [], externalSuccesses: [], failure: null, cleanup: null,
  limits: ["Observed genuine browser API delivery, not all managed noncooperative/disposal/overflow/writer/reentrancy/ABA cases.",
    "Binary bytes and immutable saved manifests only; no image decoder, engine, font, PDF, isolation or full compatibility claim.",
    "Browser Blob read counts are not private C# opener counts or global memory bounds."] };
let root, inputs, chromium, playwrightErrors, page = null, context = null, launching = null, server = null, profile = null, base = null;
let phase = "output-preflight", category = "PREFLIGHT", stopped = false, lastClosedSession = null;
const RECEIPT_LIMIT = 262144;
let receiptIssue = null, lastGoodReceipt = null;
const running = () => { if (stopped) throw new Error("Deadline/cleanup reached"); };
const stage = (p, c) => { running(); phase = p; category = c; };
function outputBytes() {
  let total = 0, entries = 0; const pending = [OUT];
  while (pending.length) { const dir = pending.pop(); for (const n of fs.readdirSync(dir)) {
    assert.ok(++entries <= 4096); const p = path.join(dir, n), s = fs.lstatSync(p); assert.ok(!s.isSymbolicLink());
    if (s.isDirectory()) pending.push(p); else { assert.ok(s.isFile()); total += s.size; }
  } } return total;
}
function smallEvidence(value, maximum) {
  try {
    const text = JSON.stringify(value);
    if (typeof text !== "string") return null;
    return Buffer.byteLength(text) <= maximum ? value : { truncated: true, preview: text.slice(0, Math.floor(maximum / 4)) };
  } catch { return { truncated: true, reason: "Evidence could not be serialized" }; }
}
function writeEmergencyReceipt() {
  // Failure-only diagnostic reduction: never convert a failed/over-budget case
  // to PASS and never skip UI assertions to make a successful receipt smaller.
  record.result = "FAIL"; process.exitCode = 1;
  if (!receiptIssue.preservationAttempted) {
    receiptIssue.preservationAttempted = true;
    if (lastGoodReceipt) {
      try {
        const backup = path.join(OUT, "last-full-receipt-before-fallback.json");
        const size = Buffer.byteLength(lastGoodReceipt);
        if (outputBytes() + size + 65536 <= BUDGET) {
          fs.writeFileSync(backup, lastGoodReceipt, { flag: "wx" });
          receiptIssue.lastFullReceipt = { artifact: path.basename(backup), bytes: size, sha256: sha(Buffer.from(lastGoodReceipt)) };
        } else receiptIssue.lastFullReceiptNotCopied = "Insufficient output-budget reserve; artifacts were not deleted.";
      } catch (e) { receiptIssue.lastFullReceiptNotCopied = String(e.message || e).slice(0, 512); }
    }
  }
  const first = record.failure || { phase, classification: "RECEIPT_REPORTING", message: "Receipt reporting failed", stack: null };
  const failure = { phase: first.phase, classification: first.classification, message: String(first.message).slice(0, 1024),
    stack: typeof first.stack === "string" ? first.stack.slice(0, 4096) : null, ui: smallEvidence(first.ui, 8192) };
  let emergency = { receiptSchemaVersion: 2, result: "FAIL", scope: record.scope, actualBrowserExecution: record.actualBrowserExecution,
    failure, receiptEvidence: { truncated: true, normalReceiptLimit: RECEIPT_LIMIT, issue: receiptIssue,
      qualification: "Case failed; bulk progress diagnostics omitted. Full assertions/artifacts were not waived." },
    sdk: smallEvidence(record.sdk, 4096), projects: smallEvidence(record.projects, 2048),
    target: smallEvidence(record.online.target ? { id: record.online.target.id, path: record.online.target.path, old: record.online.target.old } : null, 2048),
    completedActions: record.actions.slice(0, 32).map(a => ({ label: a.label, beforeHead: a.before?.head?.id, afterHead: a.after?.head?.id,
      session: a.after?.stamp?.session, generation: a.after?.stamp?.generation })),
    completedHistoryChecks: record.history.slice(0, 32).map(h => ({ label: h.label, revisionIds: h.revisionIds?.slice(0, 32) })),
    immutableRevisionIds: Object.keys(record.immutableRevisions || {}).slice(0, 32),
    artifacts: smallEvidence(record.artifacts.slice(0, 64), 8192), readCleanup: smallEvidence(record.readHookCleanup, 4096),
    cleanup: smallEvidence(record.cleanup, 4096), profileCleaned: record.profileCleaned === true,
    signalCounts: { pageErrors: record.pageErrors.length, csp: record.violations.length, externalAttempts: record.externalAttempts.length,
      externalSuccesses: record.externalSuccesses.length, nativeWorkers: record.workers.filter(w => NATIVE.test(w.url)).length } };
  let data = JSON.stringify(emergency, null, 2) + "\n";
  if (Buffer.byteLength(data) > 65536) {
    emergency = { receiptSchemaVersion: 2, result: "FAIL", scope: record.scope, failure,
      receiptEvidence: { truncated: true, issue: receiptIssue, emergencySummaryFurtherReduced: true },
      cleanup: smallEvidence(record.cleanup, 4096), readCleanup: smallEvidence(record.readHookCleanup, 4096), profileCleaned: record.profileCleaned === true };
    data = JSON.stringify(emergency, null, 2) + "\n";
  }
  try {
    const priorSize = fs.existsSync(RECEIPT) ? fs.statSync(RECEIPT).size : 0;
    if (Buffer.byteLength(data) > RECEIPT_LIMIT || outputBytes() - priorSize + Buffer.byteLength(data) > BUDGET)
      throw new Error("Emergency receipt cannot fit unchanged receipt/output budgets");
    fs.writeFileSync(RECEIPT, data);
  } catch (e) {
    // ENOSPC/IO failures cannot be repaired by dropping artifacts or increasing
    // budgets. Root captures bounded stderr; preserve the ORIGINAL first error.
    process.stderr.write(JSON.stringify({ ...emergency, receiptWriteError: String(e.message || e).slice(0, 512) }) + "\n");
  }
}
function save({ halt = true } = {}) {
  if (receiptIssue) { writeEmergencyReceipt(); return; }
  let measuredBytes = null;
  try {
    const data = JSON.stringify(record, null, 2) + "\n"; measuredBytes = Buffer.byteLength(data);
    if (measuredBytes > RECEIPT_LIMIT) {
      const e = new Error(`Receipt budget exceeded: ${measuredBytes} bytes > ${RECEIPT_LIMIT}`); e.receiptClassification = "RECEIPT_BUDGET"; throw e;
    }
    if (outputBytes() - fs.statSync(RECEIPT).size + measuredBytes > BUDGET) {
      const e = new Error("Output artifact/receipt budget exceeded16MiB"); e.receiptClassification = "OUTPUT_BUDGET"; throw e;
    }
    fs.writeFileSync(RECEIPT, data); lastGoodReceipt = data;
  } catch (e) {
    receiptIssue = { phase, classification: e.receiptClassification || "RECEIPT_WRITE_OR_SERIALIZATION", message: String(e.message || e).slice(0, 1024), measuredBytes };
    fail(e, receiptIssue.classification); process.exitCode = 1;
    writeEmergencyReceipt();
    process.stderr.write(JSON.stringify({ result: "FAIL", receiptIssue, firstFailure: { phase: record.failure.phase, classification: record.failure.classification, message: record.failure.message } }) + "\n");
    if (halt) throw e; // stop the case; catch/finally use halt:false and cannot recursively throw on the same overflow
  }
}
function fail(e, kind = category) { if (!record.failure) record.failure = { phase, classification: kind, message: String(e.message || e).slice(0, 1024), stack: typeof e.stack === "string" ? e.stack.slice(0, 4096) : null }; record.result = "FAIL"; }
function append(list, value, max = 128) { if (list.length < max) list.push(value); else record.observationOverflow = true; }
async function bounded(p, ms, message) { let timer; try { return await Promise.race([p, new Promise((_, reject) => { timer = setTimeout(() => reject(new Error(message)), ms); })]); } finally { clearTimeout(timer); } }
function cleanSignals() {
  assert.ok(!record.observationOverflow); assert.equal(record.workers.filter(w => NATIVE.test(w.url)).length, 0, "source-only workflow: no compiler/PDF workers");
  for (const n of ["pageErrors", "violations", "externalAttempts", "externalSuccesses"]) assert.deepEqual(record[n], [], n);
}
function preflight() {
  root = fs.realpathSync(need("PUBLISHED_WWWROOT")); assert.ok(outputBytes() <= BUDGET - 3 * 1024 * 1024);
  const headers = fs.readFileSync(path.join(__dirname, "../docs/examples/static-csp-headers.conf"), "utf8");
  validateCsp(fs.readFileSync(path.join(root, "index.html"), "utf8"), headers);
  const line = headers.split("\n").map(s => s.trim()).find(s => s.startsWith("Content-Security-Policy:")); assert.ok(line);
  const text = fs.readFileSync(path.join(root, "service-worker-assets.js"), "utf8"), match = text.match(/self\.assetsManifest\s*=\s*(\{[\s\S]*\})\s*;/); assert.ok(match);
  const manifest = JSON.parse(match[1]); assert.equal(typeof manifest.version, "string");
  assert.ok(Array.isArray(manifest.assets) && manifest.assets.length > 0 && manifest.assets.length <= 4096);
  const names = new Set();
  for (const a of manifest.assets) {
    assert.equal(typeof a.url, "string"); assert.ok(!path.isAbsolute(a.url) && !a.url.includes("\\") && !a.url.split("/").includes(".."));
    assert.ok(!names.has(a.url)); names.add(a.url); const f = fs.realpathSync(path.join(root, a.url)); assert.ok(f.startsWith(root + path.sep));
    assert.equal("sha256-" + createHash("sha256").update(fs.readFileSync(f)).digest("base64"), a.hash, a.url);
  }
  for (const n of ["index.html", "_framework/blazor.webassembly.js", "js/workspace-storage.js", "js/download.js", "js/offline-readiness.js"]) assert.ok(names.has(n), n);
  record.sdk = { version: manifest.version, observedAssets: manifest.assets.length, manifestSha256: sha(Buffer.from(text)), everySriVerified: true, strictCsp: true };
  return { headers, csp: line.slice("Content-Security-Policy:".length).trim() };
}
const TYPES = { ".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8", ".mjs": "text/javascript", ".css": "text/css", ".json": "application/json",
  ".wasm": "application/wasm", ".png": "image/png", ".svg": "image/svg+xml", ".ico": "image/x-icon", ".txt": "text/plain", ".dat": "application/octet-stream", ".blat": "application/octet-stream", ".pdb": "application/octet-stream" };
async function serve() {
  server = http.createServer((req, res) => {
    let name, f; try { name = decodeURIComponent(new URL(req.url, `http://127.0.0.1:${PORT}`).pathname); } catch { res.writeHead(400); res.end(); return; }
    if (name.includes("\\") || name.includes("\0") || name.split("/").includes("..")) { res.writeHead(400); res.end(); return; }
    try { f = fs.realpathSync(path.resolve(root, "." + (name === "/" ? "/index.html" : name))); } catch { res.writeHead(404); res.end(); return; }
    if (!f.startsWith(root + path.sep) || !fs.statSync(f).isFile()) { res.writeHead(404); res.end(); return; }
    res.writeHead(200, { "Content-Type": TYPES[path.extname(f).toLowerCase()] || "application/octet-stream", "Cache-Control": "no-store", "Content-Security-Policy": inputs.csp, "X-Content-Type-Options": "nosniff" });
    const s = fs.createReadStream(f); s.on("error", () => res.destroy()); s.pipe(res);
  });
  await new Promise((resolve, reject) => { server.once("error", reject); server.listen(PORT, "127.0.0.1", resolve); }); base = `http://127.0.0.1:${PORT}`;
}
function census() {
  const found = [];
  for (const pid of fs.readdirSync("/proc")) {
    if (!/^\d+$/.test(pid)) continue; let exe, stat; try { exe = path.basename(fs.readlinkSync(`/proc/${pid}/exe`)); } catch { continue; }
    if (!/chromium|chrome|headless_shell/i.test(exe)) continue;
    try { stat = fs.readFileSync(`/proc/${pid}/stat`, "utf8"); } catch (e) { if (["ENOENT", "ESRCH"].includes(e.code)) continue; throw e; }
    const startticks = stat.slice(stat.lastIndexOf(") ") + 2).split(" ")[19]; assert.match(startticks, /^\d+$/); found.push({ pid: Number(pid), startticks, exe });
  } return found.sort((a, b) => a.pid - b.pid);
}
async function reap() { let c; for (let i = 0; i < 40; i++) { c = census(); if (!c.length) return c; await new Promise(r => setTimeout(r, 100)); } assert.deepEqual(c, []); return c; }
async function stopOrigin() { if (server?.listening) await bounded(new Promise((resolve, reject) => { server.close(e => e ? reject(e) : resolve()); server.closeIdleConnections(); }), 5000, "origin close timeout"); }
async function refusal() { await new Promise((resolve, reject) => { const r = http.get(base, response => { response.resume(); reject(new Error("origin still alive")); }); r.on("error", e => e.code === "ECONNREFUSED" ? resolve() : reject(e)); r.setTimeout(2000, () => r.destroy(new Error("origin refusal unproven"))); }); }
async function launch(offline) {
  running(); launching = chromium.launchPersistentContext(profile, { headless: true, offline, acceptDownloads: true, timeout: 15000 }); context = await launching; launching = null; running();
  context.on("request", r => { const u = new URL(r.url()); if (/^https?:$/.test(u.protocol) && u.origin !== base) append(record.externalAttempts, { phase, origin: u.origin }); });
  await context.route("**/*", r => new URL(r.request().url()).origin === base ? r.continue() : r.abort());
  await context.addInitScript(() => { window.__binaryReplacementCsp = []; addEventListener("securitypolicyviolation", e => window.__binaryReplacementCsp.push(e.effectiveDirective)); });
  const watch = p => { p.on("worker", w => append(record.workers, { phase, url: w.url() })); p.on("pageerror", e => append(record.pageErrors, { phase, message: String(e.message).slice(0, 1024) })); };
  context.pages().forEach(watch); context.on("page", watch); context.on("response", r => { const u = new URL(r.url()); if (u.origin !== base && r.ok()) append(record.externalSuccesses, { phase, origin: u.origin }); });
  page = await context.newPage(); page.setDefaultTimeout(30000);
}
async function collectCsp() { if (context) for (const p of context.pages()) if (new URL(p.url()).origin === base) for (const directive of await p.evaluate(() => window.__binaryReplacementCsp || [])) append(record.violations, { phase, directive }); }
async function projectRecord(name) { return page.evaluate(async name => { const m = await import("./js/workspace-storage.js"), matches = (await m.listProjects()).filter(p => p.name === name); if (matches.length !== 1) throw new Error("Expected one controlled project"); const p = matches[0]; return { id: p.id, name: p.name, head: p.headRevisionId ?? null }; }, name); }
const canonical = s => String(s ?? "").replace(/-/g, "").toLowerCase();
async function config() { return page.evaluate(() => ({ main: document.querySelector('select[aria-label="Compile main file"]')?.value, engine: document.querySelector('select[aria-label="Compile engine"]')?.value,
  bibtex: (document.querySelector('[data-testid="bibliography-tool"]') ? document.querySelector('[data-testid="bibliography-tool"]').value === "Bibtex" : document.querySelector('[data-testid="use-bibtex"]')?.checked) ?? null, version: document.querySelector('[data-testid="compile-panel"]')?.dataset.selectionVersion,
  output: document.querySelector('[data-testid="compile-panel"]')?.dataset.outputHash ?? null })); }
async function snapshot(project) {
  return { project: await projectRecord(project), stamp: await H.stamp(page), head: await H.head(page, project), config: await config(),
    view: await page.evaluate(() => { const e = document.querySelector('[data-testid="editor"]'); return { label: document.querySelector('.editor-toolbar label[for="editor"]')?.textContent,
      activePath: document.querySelector('.file-tree .files li.active [data-testid="select-file"] .file-name')?.textContent,
      editorFile: e?.dataset.fileId ?? null, source: e?.value ?? null, start: e?.selectionStart ?? null, end: e?.selectionEnd ?? null,
      binary: !!document.querySelector('[data-testid="binary-view"]'), files: [...document.querySelectorAll('[data-testid="select-file"] .file-name')].map(e => e.textContent) }; }) };
}
async function settleConfiguration() { await H.reveal(page, H.$(page, "refresh-compile-tools")); await page.waitForFunction(() => document.querySelector('[data-testid="refresh-compile-tools"]')?.disabled === false, null, { timeout: 60000 }); }
async function waitDestination(name, file = null, previousSession = lastClosedSession) {
  stage("wait-destination:" + name, "PROJECT_NAVIGATION");
  await page.waitForFunction(name => document.querySelector(".workspace-header h1")?.textContent.trim() === name && !!document.querySelector("main.workbench") && !document.querySelector("main.project-dashboard"), name);
  const p = await projectRecord(name), head = p.head ? await H.head(page, name) : null, target = file === null ? null : head?.files.find(f => f.path === file);
  if (file !== null) assert.ok(target);
  await page.waitForFunction(({ name, head, file, previousSession }) => { const s = document.querySelector('[data-testid="analysis-panel"]'), id = x => String(x ?? "").replace(/-/g, "").toLowerCase();
    return document.querySelector(".workspace-header h1")?.textContent.trim() === name && !!s?.dataset.session
      && (!previousSession || id(s.dataset.session) !== id(previousSession)) && id(s.dataset.head) === id(head)
      && (file === null || [...document.querySelectorAll('[data-testid="select-file"] .file-name')].some(e => e.textContent.trim() === file));
  }, { name, head: p.head, file, previousSession });
  record.navigation.push({ action: "destination-loaded", project: p, stamp: await H.stamp(page), target: target ? { id: target.id, path: target.path } : null }); return target;
}
async function selectFile(file) {
  stage("select-file:" + file, "PROJECT_NAVIGATION"); const b = H.$(page, "select-file").filter({ hasText: file }); await b.waitFor({ state: "attached" }); assert.equal(await b.count(), 1); await b.click();
  await page.locator('.editor-toolbar label[for="editor"]', { hasText: file }).waitFor();
  assert.equal((await page.locator('.file-tree .files li.active [data-testid="select-file"] .file-name').textContent()).trim(), file);
}
async function openProject(name, file, { waitAdmission = true, previousSession = lastClosedSession } = {}) {
  stage("open-project:" + name, "PROJECT_NAVIGATION"); await H.open(page, name); const target = await waitDestination(name, file, previousSession); await selectFile(file);
  const revision = await readRevision(name, (await projectRecord(name)).head), typed = revision.files.find(f => canonical(f.id) === canonical(target.id)); assert.ok(typed);
  if (typed.kind === "binary") { await H.$(page, "binary-view").waitFor(); assert.equal(await H.$(page, "editor").count(), 0); assert.equal(await H.$(page, "binary-replacement-panel").count(), 1); }
  else { const text = Buffer.from(target.bytes).toString("utf8").replace(/^\uFEFF/, "").replace(/\r\n?/g, "\n"); await page.waitForFunction(({ id, text }) => { const e = document.querySelector('[data-testid="editor"]'); return e?.dataset.fileId?.replace(/-/g, "").toLowerCase() === String(id).replace(/-/g, "").toLowerCase() && e.value === text; }, { id: target.id, text }); }
  await settleConfiguration(); if (waitAdmission) await page.waitForFunction(() => document.querySelector('[data-testid="import-input"]')?.disabled === false);
}
async function closeProject() {
  const title = (await page.locator(".workspace-header h1").textContent()).trim(), stamp = await H.stamp(page); stage("close-project:" + title, "PROJECT_NAVIGATION");
  await page.locator('.workspace-header button[aria-label="Close"]').click(); await page.locator("main.project-dashboard").waitFor();
  await page.waitForFunction(() => !!document.querySelector("main.project-dashboard") && !document.querySelector(".workspace-header")); lastClosedSession = stamp.session;
  record.navigation.push({ action: "closed-to-dashboard", title, session: stamp.session });
}
async function settleSave(project) {
  const writable = async () => { assert.equal(await page.locator(".banner-readonly, .banner-recovery").count(), 0); assert.equal((await page.locator(".workspace-header h1").textContent()).trim(), project); };
  await writable(); const b = H.$(page, "save"); let mode = "already-saved";
  if (await b.isEnabled()) { try { await b.click({ timeout: 2000 }); mode = "explicit-click"; } catch (e) { if (!(e instanceof playwrightErrors.TimeoutError) || !(await b.isDisabled())) throw e; mode = "autosave-raced-click"; } }
  await page.waitForFunction(() => document.querySelector(".workspace-header .status")?.textContent === "Saved." && !document.querySelector(".banner-readonly, .banner-recovery")); await writable(); return { mode, status: "Saved." };
}
async function exactHead(project, files, expected = null) {
  const head = await H.head(page, project); if (expected) assert.deepEqual(head, expected); assert.deepEqual(head.files.map(f => f.path).sort(), Object.keys(files).sort());
  for (const [name, bytes] of Object.entries(files)) assert.deepEqual(Buffer.from(head.files.find(f => f.path === name).bytes), bytes, name); return head;
}
async function readRevision(project, id) {
  return page.evaluate(async ({ project, id }) => { const m = await import("./js/workspace-storage.js"), p = (await m.listProjects()).find(p => p.name === project); if (!p) throw new Error("Missing controlled project");
    const r = await m.getRevision(id); if (!r) throw new Error("Missing immutable saved manifest"); const checkpoints = await m.listCheckpoints(p.id);
    return { id: r.id, projectId: r.projectId, expectedProjectId: p.id, parent: r.parentRevisionId,
      checkpointRevisionIds: checkpoints.map(c => c.revisionId), files: await Promise.all(r.files.map(async f => ({ id: f.id, path: f.relativePath, kind: f.mediaKind, bytes: Array.from(await m.readContent(f.contentHash)) }))) };
  }, { project, id });
}
async function assertBinary(project, bytes, files, targetId, head = null) {
  const current = await exactHead(project, files, head), f = current.files.find(f => f.path === TARGET); assert.ok(f); assert.equal(canonical(f.id), canonical(targetId)); assert.deepEqual(Buffer.from(f.bytes), bytes);
  assert.equal((await page.locator('.editor-toolbar label[for="editor"]').textContent()).trim(), TARGET);
  assert.equal((await page.locator('.file-tree .files li.active [data-testid="select-file"] .file-name').textContent()).trim(), TARGET);
  assert.equal(await H.$(page, "editor").count(), 0); await H.$(page, "binary-view").waitFor(); assert.equal(await H.$(page, "binary-replacement-panel").count(), 1);
  const manifest = await readRevision(project, current.id), typed = manifest.files.find(f => f.path === TARGET); assert.equal(typed.kind, "binary"); assert.equal(canonical(typed.id), canonical(targetId)); return current;
}
async function createSentinel(name, filename, bytes) {
  stage("create-sentinel-project:" + name, "PROJECT_SETUP"); await H.reveal(page, page.locator("#new-project")); await page.locator("#new-project").fill(name); await page.getByRole("button", { name: "Create project", exact: true }).click(); await waitDestination(name);
  await H.$(page, "import-input").setInputFiles({ name: filename, mimeType: "text/plain", buffer: bytes }); await page.locator('.editor-toolbar label[for="editor"]', { hasText: filename }).waitFor();
  await page.waitForFunction(() => { const e = document.querySelector('[data-testid="single-file-import-status"]'); return e?.dataset.state === "imported" && e.dataset.busy === "false"; });
  const saved = await settleSave(name), head = await exactHead(name, { [filename]: bytes }); await settleConfiguration(); assert.equal(await H.$(page, "binary-replacement-panel").count(), 0); return { saved, head, project: await projectRecord(name) };
}
const panel = () => H.$(page, "binary-replacement-panel"), picker = () => panel().locator('[data-testid="binary-replacement-input"]');
const preview = () => panel().locator('[data-testid="binary-replacement-preview"]');
const confirm = () => panel().locator('[data-testid="binary-replacement-confirm"]'), cancel = () => panel().locator('[data-testid="binary-replacement-cancel"]');
async function status() { const s = H.$(page, "binary-replacement-status"); if (await s.count() === 0) return { present: false }; return { present: true, state: await s.getAttribute("data-state"), busy: await s.getAttribute("data-busy"), text: (await s.textContent()).slice(0, 1024) }; }
async function settled(states = null) {
  await page.waitForFunction(states => { const s = document.querySelector('[data-testid="binary-replacement-status"]'); return s?.dataset.busy === "false" && (states === null || states.includes(s.dataset.state)); }, states);
  const s = await status(); assert.ok(["idle", "reading", "preview", "applied", "undone", "stale", "cancelled", "busy", "failed", "disposed"].includes(s.state)); return s;
}
async function unavailable(id) { await page.waitForFunction(id => [...document.querySelectorAll(`[data-testid="${id}"]`)].every(b => b.disabled), id); assert.equal(await H.$(page, id).evaluateAll(bs => bs.filter(b => !b.disabled).length), 0); }
async function pick(f) {
  await H.reveal(page, picker()); await picker().waitFor({ state: "visible" });
  await page.waitForFunction(() => document.querySelector('[data-testid="binary-replacement-input"]')?.disabled === false);
  await picker().setInputFiles({ name: f.name, mimeType: "application/octet-stream", buffer: f.bytes });
}
async function assertPreview(oldBytes, fixture, targetId) {
  await settled(["preview"]); await preview().waitFor(); const expected = { "data-target-file-id": targetId, "data-target-path": TARGET, "data-old-bytes": String(oldBytes.length), "data-new-bytes": String(fixture.bytes.length), "data-old-sha256": sha(oldBytes), "data-new-sha256": sha(fixture.bytes) };
  const actual = {};
  for (const [key, value] of Object.entries(expected)) { actual[key] = await preview().getAttribute(key); if (key === "data-target-file-id") assert.equal(canonical(actual[key]), canonical(value)); else assert.equal(actual[key], value, key); }
  assert.notEqual(fixture.name, TARGET); assert.equal(await confirm().isEnabled(), true); await cancel().waitFor({ state: "visible" }); assert.equal(await cancel().isEnabled(), true); return actual;
}
async function stageReplacement(project, files, oldBytes, fixture, targetId, label) {
  stage(label, "PREVIEW_REPLACEMENT"); const before = await snapshot(project); await pick(fixture); const metadata = await assertPreview(oldBytes, fixture, targetId); const after = await snapshot(project); assert.deepEqual(after, before, "picking only stages; no source/head/selection/config mutation");
  record.previews.push({ label, fixture: meta(fixture), metadata, before, after }); cleanSignals(); save(); return metadata;
}
async function confirmSave(project, files, oldBytes, fixture, targetId, label) {
  const metadata = await stageReplacement(project, files, oldBytes, fixture, targetId, label); const before = await snapshot(project);
  stage(label + "-confirm-once", "EXPLICIT_CONFIRM"); await confirm().click(); const applied = await settled(["applied"]); await unavailable("binary-replacement-confirm");
  const saved = await settleSave(project); files[TARGET] = fixture.bytes; const head = await assertBinary(project, fixture.bytes, files, targetId); const after = await snapshot(project);
  assert.equal(before.project.id, after.project.id); assert.equal(before.stamp.session, after.stamp.session); assert.equal(Number(after.stamp.generation), Number(before.stamp.generation) + 1, "one actual ReplaceFileBytes generation");
  assert.equal(after.view.activePath, before.view.activePath); assert.deepEqual(after.config, before.config); assert.notEqual(head.id, before.head.id);
  record.actions.push({ label, metadata, applied, saved, before, after, exactTargetOnlyChange: true }); cleanSignals(); save(); return head;
}
async function downloadActual(trigger, label) {
  const target = path.join(OUT, label); assert.ok(!fs.existsSync(target)); const event = page.waitForEvent("download", { timeout: 30000 }); const [d] = await Promise.all([event, trigger.click()]);
  assert.equal(await bounded(d.failure(), 30000, "download completion timeout"), null); const staged = await bounded(d.path(), 5000, "download staging timeout"), size = fs.statSync(staged).size;
  assert.ok(size > 0 && size <= 1048576); assert.ok(outputBytes() + size + 262144 <= BUDGET); await bounded(d.saveAs(target), 30000, "saveAs timeout"); const bytes = fs.readFileSync(target); assert.equal(bytes.length, size);
  const info = { artifact: label, suggested: d.suggestedFilename(), ...identity(bytes), failure: null }; record.artifacts.push(info); return { bytes, info };
}
async function binaryDownload(project, expectedBytes, label) {
  // NEVER reselect the active target while a preview is staged: selection itself
  // is an authority change. Observe the existing binary view and click Download.
  assert.equal((await page.locator('.file-tree .files li.active [data-testid="select-file"] .file-name').textContent()).trim(), TARGET);
  const b = H.$(page, "download-binary"); await H.reveal(page, b); const before = await snapshot(project), previewBefore = await previewState(); const d = await downloadActual(b, label);
  assert.deepEqual(d.bytes, expectedBytes); assert.ok(!/[\\/\u0000-\u001f]/.test(d.info.suggested)); assert.match(d.info.suggested, /\.bin$/i);
  assert.deepEqual(await snapshot(project), before); assert.deepEqual(await previewState(), previewBefore); cleanSignals(); save(); return d.info;
}
async function previewState() { return H.$(page, "binary-replacement-preview").evaluateAll(es => es.map(e => Object.fromEntries([...e.attributes].filter(a => a.name.startsWith("data-")).map(a => [a.name, a.value])))); }
function unzip(bytes) {
  assert.ok(bytes.length >= 22 && bytes.length <= 1048576); let end = -1;
  for (let i = bytes.length - 22; i >= Math.max(0, bytes.length - 65557); i--) if (bytes.readUInt32LE(i) === 0x06054b50 && i + 22 + bytes.readUInt16LE(i + 20) === bytes.length) { end = i; break; }
  assert.ok(end >= 0); assert.equal(bytes.readUInt16LE(end + 4), 0); assert.equal(bytes.readUInt16LE(end + 6), 0); const n = bytes.readUInt16LE(end + 10), start = bytes.readUInt32LE(end + 16); assert.equal(bytes.readUInt16LE(end + 8), n); assert.ok(n > 0 && n <= 32); assert.equal(start + bytes.readUInt32LE(end + 12), end);
  const files = Object.create(null); let at = start, total = 0;
  for (let i = 0; i < n; i++) {
    assert.ok(at + 46 <= end); assert.equal(bytes.readUInt32LE(at), 0x02014b50); const flags = bytes.readUInt16LE(at + 8), method = bytes.readUInt16LE(at + 10), crc = bytes.readUInt32LE(at + 16), packed = bytes.readUInt32LE(at + 20), length = bytes.readUInt32LE(at + 24), nl = bytes.readUInt16LE(at + 28), ex = bytes.readUInt16LE(at + 30), comment = bytes.readUInt16LE(at + 32), local = bytes.readUInt32LE(at + 42);
    assert.ok(!(flags & 1) && [0, 8].includes(method) && nl > 0 && nl <= 200 && length <= 32768); total += length; assert.ok(total <= 32768); assert.ok(at + 46 + nl + ex + comment <= end);
    const name = bytes.subarray(at + 46, at + 46 + nl).toString("utf8"); assert.ok(name.split("/").every(s => /^[A-Za-z0-9._-]+$/.test(s) && ![".", ".."].includes(s))); assert.ok(!Object.hasOwn(files, name));
    assert.ok(local + 30 <= start); assert.equal(bytes.readUInt32LE(local), 0x04034b50); assert.equal(bytes.readUInt16LE(local + 8), method); const ln = bytes.readUInt16LE(local + 26), le = bytes.readUInt16LE(local + 28), data = local + 30 + ln + le;
    assert.equal(bytes.subarray(local + 30, local + 30 + ln).toString("utf8"), name); assert.ok(data + packed <= start); const compressed = bytes.subarray(data, data + packed), plain = method === 0 ? Buffer.from(compressed) : inflateRawSync(compressed, { maxOutputLength: 32768 });
    assert.equal(plain.length, length); assert.equal(crc32(plain), crc); files[name] = plain; at += 46 + nl + ex + comment;
  } assert.equal(at, end); return files;
}
async function zipDownload(project, expected, label) {
  const b = H.$(page, "export-zip"); await H.reveal(page, b); const before = await snapshot(project), previewBefore = await previewState(), d = await downloadActual(b, label); assert.match(d.info.suggested, /\.zip$/i);
  const files = unzip(d.bytes); assert.deepEqual(Object.keys(files).sort(), Object.keys(expected).sort()); for (const [n, bytes] of Object.entries(expected)) assert.deepEqual(files[n], bytes, n);
  assert.deepEqual(await snapshot(project), before); assert.deepEqual(await previewState(), previewBefore); d.info.entries = Object.fromEntries(Object.entries(files).map(([n, b]) => [n, identity(b)])); cleanSignals(); save(); return d.info;
}
async function undoSavedAfterCancelledStage(project, files, targetId, restoredBytes, label) {
  stage(label, "UNDO_RETENTION_REGRESSION");
  const before = await snapshot(project), control = H.$(page, "binary-replacement-undo");
  await H.reveal(page, control); await control.waitFor({ state: "visible" });
  assert.equal(await control.count(), 1); assert.equal(await control.isEnabled(), true);
  await control.click(); const undone = await settled(["undone"]); await unavailable("binary-replacement-undo");
  const saved = await settleSave(project); files[TARGET] = restoredBytes;
  const head = await assertBinary(project, restoredBytes, files, targetId), after = await snapshot(project);
  assert.equal(after.project.id, before.project.id); assert.equal(after.stamp.session, before.stamp.session);
  assert.equal(Number(after.stamp.generation), Number(before.stamp.generation) + 1);
  assert.notEqual(head.id, before.head.id); assert.deepEqual(after.view, before.view); assert.deepEqual(after.config, before.config);
  record.actions.push({ label, before, after, undone, saved, retainedPriorUndoUsedOnce: true });
  await binaryDownload(project, restoredBytes, label + ".bin"); await zipDownload(project, files, label + ".zip");
  cleanSignals(); save(); return head;
}
async function undoHiddenDuringPreview() {
  const controls = H.$(page, "binary-replacement-undo"); assert.ok(await controls.count() <= 1);
  for (const b of await controls.all()) assert.ok(!(await b.isVisible()) || await b.isDisabled(), "Undo must be hidden or disabled while preview is staged");
}
async function verifyHistory(project, expectations, targetId, label) {
  stage(label, "READONLY_HISTORY_ORACLE"); await H.$(page, "history-list").waitFor({ state: "attached" }); await H.reveal(page, H.$(page, "history-list")); assert.ok(await H.$(page, "select-history").count() > 0);
  const revisionIds = [], checkpointsObserved = new Set(); record.immutableRevisions ??= Object.create(null);
  assert.ok(expectations.length <= 32); assert.ok(record.history.length < 32);
  for (const { id, files } of expectations) {
    const r = await readRevision(project, id); assert.equal(canonical(r.id), canonical(id)); assert.equal(canonical(r.projectId), canonical(r.expectedProjectId)); assert.ok(r.checkpointRevisionIds.some(x => canonical(x) === canonical(id)), "actual checkpoint points at immutable manifest");
    assert.deepEqual(r.files.map(f => f.path).sort(), Object.keys(files).sort()); for (const [n, b] of Object.entries(files)) assert.deepEqual(Buffer.from(r.files.find(f => f.path === n).bytes), b);
    const target = r.files.find(f => f.path === TARGET); assert.equal(canonical(target.id), canonical(targetId)); assert.equal(target.kind, "binary");
    // EVERY raw comparison above still runs on EVERY readonly verification.
    // Store immutable metadata ONCE, not repeated raw arrays/checkpoint lists.
    const summary = { id: r.id, projectId: r.projectId, parent: r.parent,
      files: r.files.map(f => { const bytes = Buffer.from(f.bytes); return { id: f.id, path: f.path, kind: f.kind, length: bytes.length, sha256: sha(bytes) }; }) };
    assert.ok(Buffer.byteLength(JSON.stringify(summary)) <= 8192);
    const key = canonical(r.id);
    if (Object.hasOwn(record.immutableRevisions, key)) assert.deepEqual(record.immutableRevisions[key], summary, "repeated immutable revision metadata unchanged");
    else { assert.ok(Object.keys(record.immutableRevisions).length < 32); record.immutableRevisions[key] = summary; }
    revisionIds.push(r.id);
    for (const checkpointRevision of r.checkpointRevisionIds) checkpointsObserved.add(checkpointRevision);
    assert.ok(checkpointsObserved.size <= 128);
  }
  record.history.push({ label, revisionIds, checkpointRevisionIdsObserved: [...checkpointsObserved].sort(),
    everyRawFileComparedOnThisCall: true, usedOnlyReadonlyStorageAndHistoryDisclosure: true }); save();
}
// Minimal selected-fixture ORIGINAL Blob read hold. A second selected fixture is
// observed (never delayed/replaced) only for the shared-admission UI branch.
async function armRead(f) {
  const armed = await page.evaluate(({ held, shared }) => {
    if (window.__binaryReadGate && !window.__binaryReadGate.stats.restored) throw new Error("Prior read hook still owned");
    const sd = Object.getOwnPropertyDescriptor(Blob.prototype, "slice"), ad = Object.getOwnPropertyDescriptor(Blob.prototype, "arrayBuffer");
    const stats = { supported: typeof sd?.value === "function" && typeof ad?.value === "function", name: held.name, size: held.size, entered: false, released: false, restored: false, nativeResolved: false, nativeError: null, delivered: false, deliverySettled: false, starts: { [held.name]: 0, [shared.name]: 0 }, events: [] };
    if (!stats.supported) { window.__binaryReadGate = { stats }; return stats; }
    let release; const gate = new Promise(r => { release = r; }), tagged = new WeakMap(); const event = s => { if (stats.events.length < 24) stats.events.push(s); };
    const tag = b => b instanceof File && b.name === held.name && b.size === held.size ? held.name : b instanceof File && b.name === shared.name && b.size === shared.size ? shared.name : tagged.get(b);
    function slice(...args) { const value = sd.value.apply(this, args), kind = tag(this); if (kind) tagged.set(value, kind); return value; }
    function arrayBuffer(...args) {
      const kind = tag(this); if (!kind) return ad.value.apply(this, args); stats.starts[kind]++;
      if (kind !== held.name || stats.entered) return ad.value.apply(this, args);
      stats.entered = true; event("original-native-arrayBuffer-started"); const actual = ad.value.apply(this, args);
      return actual.then(async value => { stats.nativeResolved = true; stats.nativeLength = value.byteLength; stats.resolvedWhileHeld = !stats.released; event("original-native-buffer-resolved");
        stats.nativeSha256 = [...new Uint8Array(await crypto.subtle.digest("SHA-256", value))].map(x => x.toString(16).padStart(2, "0")).join(""); await gate; stats.delivered = true; event("original-unmodified-buffer-delivered"); return value;
      }, e => { stats.nativeError = { name: e.name, message: String(e.message).slice(0, 512) }; event("native-read-rejected"); throw e; }).finally(() => { stats.deliverySettled = true; });
    }
    Object.defineProperty(Blob.prototype, "slice", { ...sd, value: slice }); Object.defineProperty(Blob.prototype, "arrayBuffer", { ...ad, value: arrayBuffer });
    window.__binaryReadGate = { stats, release() { if (!stats.released) { stats.released = true; event("test-release"); release(); } }, restore() { if (stats.restored) return; if (Blob.prototype.slice !== slice || Blob.prototype.arrayBuffer !== arrayBuffer) throw new Error("Read hook ownership changed"); Object.defineProperty(Blob.prototype, "slice", sd); Object.defineProperty(Blob.prototype, "arrayBuffer", ad); stats.restored = true; event("original-APIs-restored"); } }; return stats;
  }, { held: { name: f.name, size: f.bytes.length }, shared: { name: SHARE.name, size: SHARE.bytes.length } });
  record.reads.push({ fixture: meta(f), armed }); save(); if (!armed.supported) { const e = new Error("Unsupported genuine Blob read boundary; no race PASS"); e.coverageGap = true; throw e; }
}
const readStats = () => page.evaluate(() => structuredClone(window.__binaryReadGate?.stats || null));
async function holdReplacement(f) {
  await armRead(f); await pick(f);
  try { await page.waitForFunction(() => window.__binaryReadGate?.stats.entered === true, null, { timeout: 15000 }); }
  catch (e) { if (!(e instanceof playwrightErrors.TimeoutError)) throw e; record.reads.at(-1).gap = await readStats(); save(); const gap = new Error("Selected replacement did not reach ORIGINAL Blob.arrayBuffer; coverage gap, not PASS"); gap.coverageGap = true; throw gap; }
  await page.waitForFunction(() => { const s = document.querySelector('[data-testid="binary-replacement-status"]'); return s?.dataset.state === "reading" && s.dataset.busy === "true"; });
  const stats = await readStats(); assert.equal(stats.released, false); record.reads.at(-1).held = { stats, ui: await status() }; save();
}
async function releaseRead() { await page.evaluate(() => window.__binaryReadGate.release()); await page.waitForFunction(() => window.__binaryReadGate?.stats.deliverySettled === true); }
async function restoreRead() { await page.evaluate(() => window.__binaryReadGate.restore()); const s = await readStats(); record.reads.at(-1).final = s; save(); return s; }
async function sharedAdmission(project) {
  const before = await snapshot(project), input = H.$(page, "import-input"); const disabled = await input.isDisabled(); const measured = { importInputDisabledDuringHeldReplacement: disabled, attemptedCompetingInput: false };
  if (!disabled) {
    measured.attemptedCompetingInput = true; await input.setInputFiles({ name: SHARE.name, mimeType: "text/plain", buffer: SHARE.bytes });
    await page.waitForFunction(() => { const s = document.querySelector('[data-testid="single-file-import-status"]'); return s && ["busy", "failed"].includes(s.dataset.state); });
    measured.actualImportStatus = await H.$(page, "single-file-import-status").evaluate(e => ({ state: e.dataset.state, busy: e.dataset.busy, text: e.textContent.slice(0, 512) }));
    assert.equal((await readStats()).starts[SHARE.name], 0, "competing selected Blob must not start native byte read");
  }
  assert.deepEqual(await snapshot(project), before); measured.nativeBlobStarts = (await readStats()).starts[SHARE.name]; record.online.sharedAdmission = measured; save();
}
async function staleRead(a, targetProject, targetFile, targetId, filesA, headA, headB, label) {
  stage(label, "SESSION_SWITCH_REAL_READ"); const original = await snapshot(a);
  assert.equal(canonical(original.head.files.find(f => f.path === TARGET).id), canonical(targetId));
  await holdReplacement(LATE); await closeProject();
  await openProject(targetProject, targetFile, { waitAdmission: false, previousSession: original.stamp.session }); const before = await snapshot(targetProject); assert.notEqual(before.stamp.session, original.stamp.session);
  if (a === targetProject) assert.equal(before.project.id, original.project.id); else assert.notEqual(before.project.id, original.project.id);
  const preRelease = { ui: await status(), read: await readStats(), additiveDisabled: await H.$(page, "import-input").isDisabled() }; await releaseRead();
  await page.waitForFunction(() => document.querySelector('[data-testid="import-input"]')?.disabled === false); if (await panel().count()) await settled();
  await unavailable("binary-replacement-confirm"); const read = await restoreRead(), after = await snapshot(targetProject); assert.deepEqual(after, before);
  await exactHead(a, filesA, headA); if (a !== targetProject) assert.deepEqual(await H.head(page, targetProject), headB);
  record.refusedReads.push({ label, original, before, after, preRelease, read, unchanged: true,
    qualification: "Actual browser delivery/unmount/refusal only; no original private controller completion/opener hook." }); cleanSignals(); save();
}
async function run() {
  inputs = preflight(); ({ chromium, errors: playwrightErrors } = require("playwright")); save(); assert.deepEqual(census(), []); profile = fs.mkdtempSync(path.join(os.tmpdir(), "binary-replacement-profile-")); await serve();
  stage("online-start", "UI_STARTUP"); await launch(false); record.online.processes = census(); assert.ok(record.online.processes.length); const response = await page.goto(base); assert.ok(response?.ok()); validateCsp(await response.text(), inputs.headers); assert.equal(response.headers()["content-security-policy"], inputs.csp); await H.ready(page);
  const a = `BinaryReplace-A-${Date.now()}`, b = `BinaryReplace-B-${Date.now()}`; record.projects = { a, b };
  record.online.a = await createSentinel(a, A_SENTINEL, A_TEXT); await closeProject(); record.online.b = await createSentinel(b, B_SENTINEL, B_TEXT); await closeProject();
  await openProject(a, A_SENTINEL); const bHead = record.online.b.head; let files = { [A_SENTINEL]: A_TEXT };
  stage("additive-original-binary-import", "PROJECT_SETUP"); await H.$(page, "import-input").setInputFiles({ name: OLD.name, mimeType: "application/octet-stream", buffer: OLD.bytes });
  await page.locator('.editor-toolbar label[for="editor"]', { hasText: OLD.name }).waitFor(); await page.waitForFunction(() => { const s = document.querySelector('[data-testid="single-file-import-status"]'); return s?.dataset.state === "imported" && s.dataset.busy === "false"; });
  await settleSave(a); let head = await exactHead(a, { ...files, [OLD.name]: OLD.bytes }); const targetId = head.files.find(f => f.path === OLD.name).id;
  stage("normal-nested-target-rename", "PROJECT_SETUP"); await H.reveal(page, H.$(page, "rename-input")); await H.$(page, "rename-input").fill(TARGET); await H.$(page, "rename").click();
  await page.locator('.editor-toolbar label[for="editor"]', { hasText: TARGET }).waitFor(); await settleSave(a); files[TARGET] = OLD.bytes; head = await assertBinary(a, OLD.bytes, files, targetId); const oldHead = head;
  record.online.target = { id: targetId, path: TARGET, kind: "binary", old: identity(OLD.bytes), head: oldHead }; cleanSignals();
  await stageReplacement(a, files, OLD.bytes, NEW, targetId, "preview-before-cancel");
  await binaryDownload(a, OLD.bytes, "preview-still-old.bin"); await zipDownload(a, files, "preview-still-old.zip"); const beforeCancel = await snapshot(a);
  stage("preview-cancel", "PREVIEW_CANCEL"); await cancel().click(); const cancelledPreview = await settled(["cancelled", "idle"]); await unavailable("binary-replacement-confirm"); assert.deepEqual(await snapshot(a), beforeCancel);
  record.online.previewCancel = { status: cancelledPreview, unchanged: true };
  const newHead = await confirmSave(a, files, OLD.bytes, NEW, targetId, "first-explicit-replacement"); await binaryDownload(a, NEW.bytes, "confirmed-new.bin"); await zipDownload(a, files, "confirmed-new.zip"); assert.deepEqual(await H.head(page, b), bHead);
  stage("actual-undo-after-normal-save", "EXPLICIT_UNDO"); const beforeUndo = await snapshot(a), undo = H.$(page, "binary-replacement-undo"); await H.reveal(page, undo); await undo.waitFor({ state: "visible" }); assert.equal(await undo.isEnabled(), true); await undo.click();
  const undone = await settled(["undone"]); await unavailable("binary-replacement-undo"); const undoSave = await settleSave(a); files[TARGET] = OLD.bytes; const undoneHead = await assertBinary(a, OLD.bytes, files, targetId); const afterUndo = await snapshot(a);
  assert.equal(afterUndo.stamp.session, beforeUndo.stamp.session); assert.equal(Number(afterUndo.stamp.generation), Number(beforeUndo.stamp.generation) + 1); assert.deepEqual(afterUndo.config, beforeUndo.config); assert.equal(afterUndo.view.activePath, TARGET);
  record.online.undo = { before: beforeUndo, after: afterUndo, undone, undoSave, actualUndoAfterSave: true };
  await binaryDownload(a, OLD.bytes, "undone-old.bin");
  const redoneHead = await confirmSave(a, files, OLD.bytes, NEW, targetId, "replace-again-after-undo");
  const history = [{ id: oldHead.id, files: { [A_SENTINEL]: A_TEXT, [TARGET]: OLD.bytes } }, { id: newHead.id, files: { [A_SENTINEL]: A_TEXT, [TARGET]: NEW.bytes } },
    { id: undoneHead.id, files: { [A_SENTINEL]: A_TEXT, [TARGET]: OLD.bytes } }, { id: redoneHead.id, files: { [A_SENTINEL]: A_TEXT, [TARGET]: NEW.bytes } }];
  await verifyHistory(a, history, targetId, "real-old-new-undo-redo-history");
  stage("valid-undo-before-nonmutating-preview", "UNDO_RETENTION_REGRESSION");
  const priorUndo = H.$(page, "binary-replacement-undo"); await H.reveal(page, priorUndo); await priorUndo.waitFor({ state: "visible" }); assert.equal(await priorUndo.isEnabled(), true);
  const beforeSecondPreview = await snapshot(a);
  await stageReplacement(a, files, NEW.bytes, STALE, targetId, "new-preview-must-not-discard-prior-undo");
  await undoHiddenDuringPreview();
  await cancel().click(); const retainedPreviewCancel = await settled(["cancelled", "idle"]); await unavailable("binary-replacement-confirm");
  assert.deepEqual(await snapshot(a), beforeSecondPreview, "cancelling new preview is nonmutating");
  record.online.priorUndoAfterPreviewCancel = { priorUndoEligible: true, hiddenWhilePreview: true, cancelled: retainedPreviewCancel, noMutation: true };
  const previewCancelUndoHead = await undoSavedAfterCancelledStage(a, files, targetId, OLD.bytes, "undo-survives-new-preview-cancel");
  assert.deepEqual(await H.head(page, b), bHead);
  history.push({ id: previewCancelUndoHead.id, files: { [A_SENTINEL]: A_TEXT, [TARGET]: OLD.bytes } });
  head = await confirmSave(a, files, OLD.bytes, NEW, targetId, "reapply-after-preview-cancel-undo");
  history.push({ id: head.id, files: { [A_SENTINEL]: A_TEXT, [TARGET]: NEW.bytes } });
  await verifyHistory(a, history, targetId, "history-with-preview-cancel-undo-reapply");
  stage("held-real-read-cancel-and-shared-admission", "REAL_READ_CANCEL"); const beforeReadCancel = await snapshot(a); await holdReplacement(CANCEL); await sharedAdmission(a);
  await cancel().waitFor({ state: "visible" }); assert.equal(await cancel().isEnabled(), true); await cancel().click();
  await page.waitForFunction(() => { const s = document.querySelector('[data-testid="binary-replacement-status"]'); return s?.dataset.state === "cancelled" && s.dataset.busy === "true"; });
  const beforeRelease = { ui: await status(), read: await readStats() }; await releaseRead(); const cancelledRead = await settled(["cancelled"]); await page.waitForFunction(() => document.querySelector('[data-testid="import-input"]')?.disabled === false);
  const read = await restoreRead(); assert.equal(read.starts[SHARE.name], 0); const afterReadCancel = await snapshot(a); assert.deepEqual(afterReadCancel, beforeReadCancel);
  record.online.readCancel = { beforeRelease, cancelledRead, read, before: beforeReadCancel, after: afterReadCancel, unchanged: true };
  const readCancelUndoHead = await undoSavedAfterCancelledStage(a, files, targetId, OLD.bytes, "undo-survives-held-read-cancel");
  assert.deepEqual(await H.head(page, b), bHead);
  history.push({ id: readCancelUndoHead.id, files: { [A_SENTINEL]: A_TEXT, [TARGET]: OLD.bytes } });
  head = await confirmSave(a, files, OLD.bytes, NEW, targetId, "reapply-after-held-read-cancel-undo");
  history.push({ id: head.id, files: { [A_SENTINEL]: A_TEXT, [TARGET]: NEW.bytes } });
  await verifyHistory(a, history, targetId, "history-with-read-cancel-undo-reapply");
  head = await confirmSave(a, files, NEW.bytes, RETRY, targetId, "fresh-retry-after-actual-cancel");
  await staleRead(a, b, B_SENTINEL, targetId, files, head, bHead, "held-replacement-A-to-B"); await closeProject(); await openProject(a, TARGET); await assertBinary(a, RETRY.bytes, files, targetId, head);
  await staleRead(a, a, TARGET, targetId, files, head, bHead, "held-replacement-A-close-A"); await assertBinary(a, RETRY.bytes, files, targetId, head);
  await stageReplacement(a, files, RETRY.bytes, STALE, targetId, "preview-before-user-source-change"); stage("source-selection-edit-invalidates-preview", "STAGE_INVALIDATION");
  await selectFile(A_SENTINEL); await H.$(page, "binary-replacement-panel").waitFor({ state: "detached" });
  assert.equal(await H.$(page, "binary-replacement-panel").count(), 0); await unavailable("binary-replacement-confirm");
  const sourceText = A_TEXT.subarray(3).toString("utf8").replace(/\r\n/g, "\n") + "% User source edit invalidates staged binary.\n";
  await H.$(page, "editor").fill(sourceText); await settleSave(a); files[A_SENTINEL] = Buffer.concat([BOM, Buffer.from(sourceText, "utf8")]); head = await exactHead(a, files);
  await selectFile(TARGET); await settled(); await unavailable("binary-replacement-confirm"); await unavailable("binary-replacement-undo"); await assertBinary(a, RETRY.bytes, files, targetId, head);
  record.online.stageInvalidation = { changedByUser: A_SENTINEL, exactNewSentinel: identity(files[A_SENTINEL]), staleProposedBytesNeverApplied: true, head };
  await binaryDownload(a, RETRY.bytes, "source-edit-target-unchanged.bin"); await zipDownload(a, files, "online-final.zip"); await verifyHistory(a, history, targetId, "history-preimages-still-immutable"); assert.deepEqual(await H.head(page, b), bHead);
  stage("wait-offline-cache", "COLD_OFFLINE"); await page.waitForFunction(() => window.__oopOfflineReady === true, null, { timeout: 90000 }); await collectCsp(); cleanSignals(); record.cold.oldProcesses = census(); record.cold.previousSession = (await H.stamp(page)).session;
  await bounded(context.close(), 10000, "online browser close timeout"); context = null; page = null; record.cold.censusZero = await reap(); await stopOrigin(); await refusal(); record.cold.originRefused = true; assert.deepEqual(census(), []);
  stage("cold-new-browser", "COLD_OFFLINE"); await launch(true); record.cold.offlineBeforeNavigation = true; record.cold.newProcesses = census(); assert.ok(record.cold.newProcesses.length); const oldIds = new Set(record.cold.oldProcesses.map(p => `${p.pid}:${p.startticks}`)); assert.ok(record.cold.newProcesses.every(p => !oldIds.has(`${p.pid}:${p.startticks}`)));
  await page.goto(base); await H.ready(page); await openProject(a, TARGET, { previousSession: record.cold.previousSession }); await assertBinary(a, RETRY.bytes, files, targetId, head); await unavailable("binary-replacement-confirm");
  await verifyHistory(a, history, targetId, "cold-old-new-history-survives"); assert.deepEqual(await H.head(page, b), bHead); record.cold.persisted = { head, targetId, targetPath: TARGET, bytes: identity(RETRY.bytes), priorHistorySurvives: true };
  const offlineHead = await confirmSave(a, files, RETRY.bytes, OFFLINE, targetId, "offline-explicit-replacement"); await binaryDownload(a, OFFLINE.bytes, "offline-new.bin"); await zipDownload(a, files, "offline-project.zip");
  await assertBinary(a, OFFLINE.bytes, files, targetId, offlineHead); assert.deepEqual(await H.head(page, b), bHead); await verifyHistory(a, [...history, { id: offlineHead.id, files: { ...files } }], targetId, "offline-new-plus-preimage-history");
  record.cold.finalState = "saved-explicit-offline-replacement/same-binary-ID-nested-path/real-download-and-ZIP/history-retained";
  await collectCsp(); cleanSignals(); record.result = "PASS";
}
async function failureEvidence() {
  if (!page || page.isClosed()) return;
  record.failure.ui = { replacement: await status(), read: await readStats(), preview: await previewState(), view: await page.evaluate(() => ({
    project: document.querySelector(".workspace-header h1")?.textContent, save: document.querySelector(".workspace-header .status")?.textContent,
    active: document.querySelector('.file-tree .files li.active [data-testid="select-file"] .file-name')?.textContent, label: document.querySelector('.editor-toolbar label[for="editor"]')?.textContent,
    files: [...document.querySelectorAll('[data-testid="select-file"] .file-name')].map(e => e.textContent), source: document.querySelector('[data-testid="editor"]')?.value?.slice(0, 2048),
    additive: document.querySelector('[data-testid="single-file-import-status"]')?.textContent?.slice(0, 1024), additiveBusy: document.querySelector('[data-testid="import-input"]')?.disabled })) };
}
save();
(async () => {
  let timer; const deadline = new Promise((_, reject) => { timer = setTimeout(() => { stopped = true; reject(new Error("harness deadline360s")); }, 360000); });
  try { await Promise.race([run(), deadline]); }
  catch (e) { fail(e, e.coverageGap ? "UNSUPPORTED_GENUINE_READ_BOUNDARY" : category); save({ halt: false }); try { await bounded(failureEvidence(), 5000, "failure observation timeout"); } catch {} process.exitCode = 1; }
  finally {
    stopped = true; clearTimeout(timer);
    if (page && !page.isClosed()) { try { record.readHookCleanup = await bounded(page.evaluate(() => { const g = window.__binaryReadGate; if (!g) return null; if (g.release) g.release(); if (g.restore) g.restore(); return structuredClone(g.stats); }), 5000, "read hook cleanup timeout"); } catch (e) { fail(e, "READ_HOOK_CLEANUP"); process.exitCode = 1; } }
    if (launching) { try { context = await bounded(launching, 20000, "late launch cleanup timeout"); } catch (e) { fail(e, "CLEANUP"); process.exitCode = 1; } }
    try { if (context) { try { await bounded(collectCsp(), 5000, "CSP cleanup timeout"); } finally { await bounded(context.close(), 10000, "browser cleanup timeout"); context = null; } } } catch (e) { fail(e, "CLEANUP"); process.exitCode = 1; }
    try { record.cleanup = { chromiumRemaining: await reap() }; } catch (e) { fail(e, "CLEANUP"); process.exitCode = 1; }
    try { await stopOrigin(); } catch (e) { fail(e, "CLEANUP"); process.exitCode = 1; }
    if (profile) { try { fs.rmSync(profile, { recursive: true, force: true }); record.profileCleaned = true; } catch (e) { fail(e, "CLEANUP"); process.exitCode = 1; } }
    try { cleanSignals(); } catch (e) { fail(e, "OBSERVED_FAILURE"); process.exitCode = 1; }
    save({ halt: false }); process.stdout.write(JSON.stringify({ result: record.result, failure: record.failure }) + "\n");
  }
})();
