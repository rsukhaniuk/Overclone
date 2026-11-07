// Actual published Overclone command completion, online then whole-browser cold offline.
// Strict captured-caret authority: ANY moved DOM caret must refuse insertion,
// even if the original prefix/range remains valid. Root corrected source review;
// no browser execution is claimed by this source handoff.
// Test tooling only. Root executes. No imported test IIFEs, private app calls,
// storage writes, fake product results, API patches, compilation or providers.
// Required env: PUBLISHED_WWWROOT, RESULT_DIR (exclusive owned output).
const assert = require("node:assert/strict"), fs = require("node:fs"), os = require("node:os"), path = require("node:path"), http = require("node:http");
const { createHash } = require("node:crypto"), { inflateRawSync } = require("node:zlib");
const H = require("./review-ui-helpers.cjs"), { validate: validateCsp } = require("./test-csp.cjs"), { crc32 } = require("./zip-util.cjs");
const need = n => { assert.ok(process.env[n], `${n} required`); return process.env[n]; };
const sha = b => createHash("sha256").update(b).digest("hex"), identity = b => ({ bytes: b.length, sha256: sha(b) });
const OUT = path.resolve(need("RESULT_DIR")), RECEIPT = path.join(OUT, "command-completion-receipt.json");
assert.ok(fs.statSync(OUT).isDirectory()); fs.closeSync(fs.openSync(RECEIPT, "wx"));
const PORT = 8776, BUDGET = 16 * 1024 * 1024, NATIVE = /\/__oop\/native-worker\/(?:compile|pdf)\b/;
const TARGET = "commands.tex", OTHER = "notes.tex", B_FILE = "b-sentinel.tex", BOM = Buffer.from([239,187,191]);
const A_BYTES = Buffer.concat([BOM, Buffer.from([
  "% Fixture A sentinel Україна 😀",
  "\\documentclass{article}", "\\begin{document}",
  "Український вступ 😀 before \\sec KEEP-SECTION-SUFFIX",
  "Інший текст 🙂 before \\text KEEP-TEXT-SUFFIX",
  "Captured target \\lab KEEP-LABEL-SUFFIX",
  "% Comment context \\sec KEEP-COMMENT",
  "\\begin{verbatim}", "Verbatim context \\sec KEEP-VERBATIM", "\\end{verbatim}",
  "Escaped context \\\\sec KEEP-ESCAPED",
  "Middle context \\section KEEP-MIDDLE", "Case context \\Sec KEEP-CASE",
  "\\end{document}", "% Immutable final suffix Київ 😀", ""
].join("\r\n"), "utf8")]);
const OTHER_BYTES = Buffer.concat([BOM,Buffer.from("% Other A source Київ 😀\r\nOther source must remain byte-identical.\r\n", "utf8")]);
const B_BYTES = Buffer.concat([BOM,Buffer.from("% Independent B Україна 😀\r\nNever complete or edit this project.\r\n", "utf8")]);
const FIXED = ["section","subsection","subsubsection","paragraph","textbf","textit","emph","label","ref","pageref","cite","footnote","caption","title","author","date"];
const SEC = "Український вступ 😀 before \\sec", TEXT = "Інший текст 🙂 before \\text", LABEL = "Captured target \\lab";
const record = { receiptSchemaVersion: 2, result: "FAIL", scope: "explicit-latex-command-completion-online-cold-offline", actualBrowserExecution: "started by caller",
  sdk: null, fixtures: { [TARGET]: identity(A_BYTES), [OTHER]: identity(OTHER_BYTES), [B_FILE]: identity(B_BYTES) }, fixedCatalog: FIXED,
  online: {}, cold: {}, previews: [], actions: [], navigation: [], history: [], artifacts: [], workers: [], immutableRevisions: Object.create(null),
  pageErrors: [], violations: [], externalAttempts: [], externalSuccesses: [], failure: null, cleanup: null,
  hooks: { productApiHooksInstalled: false, cspObserverLifetime: "owned browser contexts" },
  limits: ["Lexical completion only, not semantic/package/full authoring parity or source/PDF synchronization.",
    "No large128KiB/invalidUnicode/writer/noncooperative injection: those contracts remain managed-test scope.",
    "Normal textarea user edit deliberately serializes LF+BOM; completion edits must preserve their exact raw preimage's line endings.",
    "Cold proof is all Chromium closed/origin refused/new same-profile offline browser, not reload."] };
let root, inputs, chromium, playwrightErrors, page = null, context = null, launching = null, server = null, profile = null, base = null;
let phase = "output-preflight", category = "PREFLIGHT", stopped = false, lastClosedSession = null;
const RECEIPT_LIMIT = 262144;
let receiptIssue = null, lastGoodReceipt = null, ownedInputProbeToken = null;
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
    target: smallEvidence(record.online.target ? { id: record.online.target.id, path: record.online.target.path, source: record.online.target.source } : null, 2048),
    completedActions: record.actions.slice(0, 32).map(a => ({ label: a.label, beforeHead: a.before?.head?.id, afterHead: a.after?.head?.id,
      session: a.after?.stamp?.session, generation: a.after?.stamp?.generation })),
    completedHistoryChecks: record.history.slice(0, 32).map(h => ({ label: h.label, revisionIds: h.revisionIds?.slice(0, 32) })),
    immutableRevisionIds: Object.keys(record.immutableRevisions || {}).slice(0, 32),
    artifacts: smallEvidence(record.artifacts.slice(0, 64), 8192), readCleanup: smallEvidence(record.readHookCleanup, 4096),
    cleanup: smallEvidence(record.cleanup, 4096), profileCleaned: record.profileCleaned === true,
    userInputObservation: smallEvidence(record.userInputObservation, 8192), inputObserverCleanup: smallEvidence(record.inputObserverCleanup, 2048),
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
  for (const n of ["index.html", "_framework/blazor.webassembly.js", "js/workspace-storage.js", "js/download.js", "js/offline-readiness.js", "js/editor-select.js"]) assert.ok(names.has(n), n);
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
  await context.addInitScript(() => { window.__commandCompletionCsp = []; window.__commandCompletionCspOverflow = false; addEventListener("securitypolicyviolation", e => { if (window.__commandCompletionCsp.length < 64) window.__commandCompletionCsp.push(e.effectiveDirective); else window.__commandCompletionCspOverflow = true; }); });
  const watch = p => { p.on("worker", w => append(record.workers, { phase, url: w.url() })); p.on("pageerror", e => append(record.pageErrors, { phase, message: String(e.message).slice(0, 1024) })); };
  context.pages().forEach(watch); context.on("page", watch); context.on("response", r => { const u = new URL(r.url()); if (u.origin !== base && r.ok()) append(record.externalSuccesses, { phase, origin: u.origin }); });
  page = await context.newPage(); page.setDefaultTimeout(30000);
}
async function collectCsp() { if (context) for (const p of context.pages()) if (new URL(p.url()).origin === base) { const c = await p.evaluate(() => ({ events: window.__commandCompletionCsp || [], overflow: window.__commandCompletionCspOverflow === true })); if(c.overflow) record.observationOverflow = true; for(const directive of c.events) append(record.violations, {phase,directive}); } }
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
  stage("open-project:" + name, "PROJECT_NAVIGATION");
  const requested = await projectRecord(name);
  await page.waitForFunction(({ name, id }) => {
    const canonical = s => String(s ?? "").replace(/-/g, "").toLowerCase();
    const rows = [...document.querySelectorAll('[data-testid="project-row"]')].filter(r => r.dataset.name === name);
    return rows.length === 1 && canonical(rows[0].dataset.projectId) === canonical(id) && !!rows[0].dataset.projectId;
  }, { name, id: requested.id });
  const actualRows = await H.$(page, "project-row").evaluateAll((rows, { name, id }) => {
    const canonical = s => String(s ?? "").replace(/-/g, "").toLowerCase();
    return rows.filter(r => r.dataset.name === name && canonical(r.dataset.projectId) === canonical(id))
      .map(r => ({ name: r.dataset.name, id: r.dataset.projectId }));
  }, { name, id: requested.id });
  assert.equal(actualRows.length, 1); const actualRow = actualRows[0];
  assert.equal(actualRow.name, name); assert.ok(actualRow.id); assert.equal(canonical(actualRow.id), canonical(requested.id));
  // Re-resolving this locator still requires BOTH identities. Reordered/keyed
  // catalog rows cannot silently turn this click into another project's Open.
  const row = page.locator(`[data-testid="project-row"][data-name=${JSON.stringify(name)}][data-project-id=${JSON.stringify(actualRow.id)}]`);
  await row.waitFor({ state: "attached" }); assert.equal(await row.count(), 1);
  assert.deepEqual(await row.evaluate(r => ({ name: r.dataset.name, id: r.dataset.projectId })), actualRow);
  const button = row.locator('[data-testid="open-project"]'); await button.waitFor({ state: "attached" }); assert.equal(await button.count(), 1);
  const opening = { action: "open-exact-identity-row", requested, actualRowBeforeClick: actualRow };
  record.navigation.push(opening); save(); await button.click();
  const target = await waitDestination(name, file, previousSession);
  await H.reveal(page, H.$(page, "analysis-profile")); await selectFile(file);
  const revision = await readRevision(name, (await projectRecord(name)).head), typed = revision.files.find(f => canonical(f.id) === canonical(target.id)); assert.ok(typed);
  assert.equal(typed.kind, "text");
  const text = dom(Buffer.from(target.bytes));
  await page.waitForFunction(({ id, text }) => { const e = document.querySelector('[data-testid="editor"]'); return e?.dataset.fileId?.replace(/-/g, "").toLowerCase() === String(id).replace(/-/g, "").toLowerCase() && e.value === text; }, { id: target.id, text });
  await settleConfiguration(); if (waitAdmission) await page.waitForFunction(() => document.querySelector('[data-testid="import-input"]')?.disabled === false);
  opening.destination = { project: await projectRecord(name), stamp: await H.stamp(page) };
  assert.equal(canonical(opening.destination.project.id), canonical(requested.id)); save();
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
async function downloadActual(trigger, label) {
  const target = path.join(OUT, label); assert.ok(!fs.existsSync(target)); const event = page.waitForEvent("download", { timeout: 30000 }); const [d] = await Promise.all([event, trigger.click()]);
  assert.equal(await bounded(d.failure(), 30000, "download completion timeout"), null); const staged = await bounded(d.path(), 5000, "download staging timeout"), size = fs.statSync(staged).size;
  assert.ok(size > 0 && size <= 1048576); assert.ok(outputBytes() + size + 262144 <= BUDGET); await bounded(d.saveAs(target), 30000, "saveAs timeout"); const bytes = fs.readFileSync(target); assert.equal(bytes.length, size);
  const info = { artifact: label, suggested: d.suggestedFilename(), ...identity(bytes), failure: null }; record.artifacts.push(info); return { bytes, info };
}
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

function rawText(bytes) { assert.ok(bytes.subarray(0,3).equals(BOM)); return bytes.subarray(3).toString("utf8"); }
function lf(s) { return s.replace(/\r\n?/g,"\n"); }
function dom(bytes) { return lf(rawText(bytes)); }
function compactSnapshot(s) {
  const { source, ...view } = s.view;
  return { project:s.project, stamp:s.stamp, head:{ id:s.head.id, parent:s.head.parent }, config:s.config,
    view:{ ...view, source:{ utf16:source?.length ?? null, ...(source === null ? {} : identity(Buffer.from(source,"utf8"))) } } };
}
function sameAuthority(after,before) {
  assert.equal(after.project.id,before.project.id); assert.equal(after.stamp.session,before.stamp.session);
  assert.deepEqual(after.config,before.config); assert.deepEqual(after.view.files,before.view.files);
}
function planPrefix(bytes,anchor,prefix,command) {
  assert.ok(FIXED.includes(command)); assert.ok(command.startsWith(prefix));
  const raw=rawText(bytes), at=raw.indexOf(anchor); assert.ok(at>=0 && raw.indexOf(anchor,at+1)===-1); assert.ok(anchor.endsWith("\\"+prefix));
  const end=at+anchor.length, start=end-prefix.length-1; assert.equal(raw.slice(start,end),"\\"+prefix);
  const insertion="\\"+command+"{}", next=raw.slice(0,start)+insertion+raw.slice(end);
  const expected=Buffer.concat([BOM,Buffer.from(next,"utf8")]);
  return { expected, oldRange:{ start:lf(raw.slice(0,start)).length, end:lf(raw.slice(0,end)).length },
    caret:lf(next.slice(0,start+insertion.length-1)).length, rawStartBytes:3+Buffer.byteLength(raw.slice(0,start),"utf8"),
    prefix,command };
}
function point(bytes,anchor,delta=0) {
  const raw=rawText(bytes), at=raw.indexOf(anchor); assert.ok(at>=0 && raw.indexOf(anchor,at+1)===-1);
  return lf(raw.slice(0,at+anchor.length)).length+delta;
}
async function caret(start,end=start) {
  const e=H.$(page,"editor"); assert.equal(await e.count(),1); assert.equal(await e.isEnabled(),true);
  await e.evaluate((e,{start,end})=>{e.focus();e.setSelectionRange(start,end);},{start,end});
  const actual=await e.evaluate(e=>({ start:e.selectionStart,end:e.selectionEnd,focused:document.activeElement===e }));
  assert.deepEqual(actual,{start,end,focused:true}); return actual;
}
const tools=()=>H.$(page,"source-tools"), showButton=()=>tools().locator('[data-testid="source-completion-show"]');
const candidates=()=>tools().locator('[data-testid="source-completion-insert"]');
// Attribute-scoped locator avoids display-text ambiguity and never selects first().
const choice=id=>tools().locator(`[data-testid="source-completion-insert"][data-command-id="${id}"]`);
async function status() {
  const s=tools().locator('[data-testid="source-completion-status"]'); assert.equal(await s.count(),1);
  return { state:await s.getAttribute("data-state"),text:(await s.textContent()).slice(0,512) };
}
async function settled(allowed=null) {
  await page.waitForFunction(allowed=>{const s=document.querySelector('[data-testid="source-completion-status"]');return s && s.dataset.state!=="reading" && (!allowed || allowed.includes(s.dataset.state));},allowed);
  const s=await status(); assert.ok(["idle","ready","empty","unavailable","stale","applied"].includes(s.state)); return s;
}
async function noEligible() {
  await page.waitForFunction(()=>[...document.querySelectorAll('[data-testid="source-completion-insert"]')].every(e=>e.disabled || !e.getClientRects().length));
  for(const b of await candidates().all()) assert.ok(!(await b.isVisible()) || await b.isDisabled());
}
async function proposalState() { return { status:await status(),buttons:await candidates().evaluateAll(es=>es.map(e=>({id:e.dataset.commandId,disabled:e.disabled,visible:!!e.getClientRects().length}))) }; }
async function showAt(project,position,expected,label,end=position) {
  stage(label,"SHOW_COMPLETION"); await H.reveal(page,showButton()); await caret(position,end);
  const before=await snapshot(project); assert.equal(await showButton().isEnabled(),true); await showButton().click();
  const state=await settled(expected.length?["ready"]:["empty","unavailable"]);
  const ids=await candidates().evaluateAll(es=>es.map(e=>e.dataset.commandId));
  assert.deepEqual(ids,expected); assert.deepEqual(ids,[...ids].sort()); assert.equal(new Set(ids).size,ids.length);
  assert.ok(ids.every(id=>FIXED.includes(id)));
  if(expected.length){assert.equal(await tools().locator('[data-testid="source-completion-results"]').count(),1);for(const id of ids){assert.equal(await choice(id).count(),1);assert.equal(await choice(id).isEnabled(),true);}}
  else await noEligible();
  const after=await snapshot(project); assert.deepEqual(after,before,"Show changes no source/head/gen/config/selection");
  assert.ok(record.previews.length<32);record.previews.push({label,position,end,ids,state,before:compactSnapshot(before),after:compactSnapshot(after)});cleanSignals();save();return before;
}
async function dismissUnchanged(project,label) {
  stage(label,"DISMISS_COMPLETION");const before=await snapshot(project),b=tools().locator('[data-testid="source-completion-dismiss"]');
  await H.reveal(page,b);assert.equal(await b.count(),1);assert.equal(await b.isEnabled(),true);await b.click();
  const state=await settled(["idle","empty","stale","unavailable"]);await noEligible();assert.deepEqual(await snapshot(project),before);
  record.actions.push({label,state,before:compactSnapshot(before),after:compactSnapshot(await snapshot(project)),noMutation:true});cleanSignals();save();
}
async function verifyRevision(project,id,files,ids) {
  const r=await readRevision(project,id);assert.equal(canonical(r.id),canonical(id));assert.equal(canonical(r.projectId),canonical(r.expectedProjectId));
  assert.ok(r.checkpointRevisionIds.some(x=>canonical(x)===canonical(id)));assert.deepEqual(r.files.map(f=>f.path).sort(),Object.keys(files).sort());
  for(const [name,bytes] of Object.entries(files)) {const f=r.files.find(f=>f.path===name);assert.deepEqual(Buffer.from(f.bytes),bytes,name);assert.equal(f.kind,"text");if(ids?.[name])assert.equal(canonical(f.id),canonical(ids[name]));}
  const meta={id:r.id,projectId:r.projectId,parent:r.parent,files:r.files.map(f=>({id:f.id,path:f.path,kind:f.kind,length:f.bytes.length,sha256:sha(Buffer.from(f.bytes))}))};
  assert.ok(Buffer.byteLength(JSON.stringify(meta))<=8192);const key=canonical(r.id);
  if(Object.hasOwn(record.immutableRevisions,key))assert.deepEqual(record.immutableRevisions[key],meta);else{assert.ok(Object.keys(record.immutableRevisions).length<32);record.immutableRevisions[key]=meta;}
  return r;
}
async function assertSource(project,files,ids,expectedHead=null,selected=TARGET) {
  const head=await exactHead(project,files,expectedHead);await verifyRevision(project,head.id,files,ids);
  const f=head.files.find(f=>f.path===selected);assert.ok(f);
  assert.equal((await page.locator('.editor-toolbar label[for="editor"]').textContent()).trim(),selected);
  assert.equal((await page.locator('.file-tree .files li.active [data-testid="select-file"] .file-name').textContent()).trim(),selected);
  const e=H.$(page,"editor");assert.equal(await e.count(),1);assert.equal(canonical(await e.getAttribute("data-file-id")),canonical(f.id));assert.equal(await e.inputValue(),dom(files[selected]));
  assert.equal(await e.isEnabled(),true);assert.equal(await H.$(page,"binary-view").count(),0);return head;
}
async function createProject(name,fixtures) {
  stage("create-source-project:"+name,"PROJECT_SETUP");await H.reveal(page,page.locator("#new-project"));await page.locator("#new-project").fill(name);
  await page.getByRole("button",{name:"Create project",exact:true}).click();await waitDestination(name);
  for(const [filename,bytes] of Object.entries(fixtures)) {
    await H.$(page,"import-input").setInputFiles({name:filename,mimeType:"text/plain",buffer:bytes});
    await page.locator('.editor-toolbar label[for="editor"]',{hasText:filename}).waitFor();
    await page.waitForFunction(()=>{const s=document.querySelector('[data-testid="single-file-import-status"]');return s?.dataset.state==="imported"&&s.dataset.busy==="false";});await settleSave(name);
  }
  const head=await exactHead(name,fixtures),ids=Object.fromEntries(head.files.map(f=>[f.path,f.id]));await verifyRevision(name,head.id,fixtures,ids);await settleConfiguration();
  return {head,ids};
}
async function history(project,checks,ids,label) {
  stage(label,"IMMUTABLE_HISTORY");await H.$(page,"history-list").waitFor({state:"attached"});await H.reveal(page,H.$(page,"history-list"));assert.ok(await H.$(page,"select-history").count()>0);
  assert.ok(checks.length<=32 && record.history.length<32);const checkpointIds=new Set(),revisionIds=[];
  for(const check of checks){const r=await verifyRevision(project,check.id,check.files,ids);revisionIds.push(r.id);for(const id of r.checkpointRevisionIds)checkpointIds.add(id);assert.ok(checkpointIds.size<=128);}
  record.history.push({label,revisionIds,checkpointRevisionIdsObserved:[...checkpointIds].sort(),everyRawFileComparedOnThisCall:true});save();
}
async function consume(project,files,ids,plan,label) {
  stage(label,"INSERT_COMPLETION");const before=await snapshot(project);assert.equal(before.view.start,plan.oldRange.end);assert.equal(before.view.end,plan.oldRange.end);
  const button=choice(plan.command);assert.equal(await button.count(),1);assert.equal(await button.isEnabled(),true);await button.click();
  const applied=await settled(["applied"]);await noEligible();
  const expectedDom=dom(plan.expected);await page.waitForFunction(({text,caret})=>{const e=document.querySelector('[data-testid="editor"]');return e?.value===text&&e.selectionStart===caret&&e.selectionEnd===caret&&document.activeElement===e;},{text:expectedDom,caret:plan.caret});
  const inMemory=await snapshot(project);sameAuthority(inMemory,before);assert.equal(Number(inMemory.stamp.generation),Number(before.stamp.generation)+1);
  assert.equal(inMemory.view.editorFile,before.view.editorFile);assert.equal(inMemory.view.activePath,before.view.activePath);assert.equal(inMemory.view.label,before.view.label);assert.equal(inMemory.view.source,expectedDom);
  const saved=await settleSave(project);files[TARGET]=plan.expected;const head=await assertSource(project,files,ids);const after=await snapshot(project);sameAuthority(after,before);
  assert.equal(Number(after.stamp.generation),Number(before.stamp.generation)+1);assert.notEqual(head.id,before.head.id);assert.equal(after.view.start,plan.caret);assert.equal(after.view.end,plan.caret);
  record.actions.push({label,command:plan.command,oldRange:plan.oldRange,caret:plan.caret,rawStartBytes:plan.rawStartBytes,applied,saved,before:compactSnapshot(before),after:compactSnapshot(after),oneOrdinaryEdit:true});
  cleanSignals();save();return head;
}
async function rejectOld(project,label) {
  stage(label,"STALE_PROPOSAL");await H.reveal(page,showButton());const before=await snapshot(project);let clicked=false;
  const buttons=await candidates().all();assert.ok(buttons.length<=16);
  for(const b of buttons)if(await b.isVisible()&&await b.isEnabled()){await b.click();clicked=true;break;}
  const state=await settled();assert.notEqual(state.state,"applied","old captured proposal cannot apply");await noEligible();
  assert.deepEqual(await snapshot(project),before,"old candidate cannot introduce another mutation");
  record.actions.push({label,clickedRefusedCandidate:clicked,state,before:compactSnapshot(before),after:compactSnapshot(await snapshot(project))});cleanSignals();save();
}
async function sourceDownload(project,expected,label) {
  // Never reselect the same file while a proposal is pending.
  const b=H.$(page,"download");await H.reveal(page,b);const before=await snapshot(project),proposal=await proposalState();
  const d=await downloadActual(b,label);assert.deepEqual(d.bytes,expected);assert.ok(!/[\\/\u0000-\u001f]/.test(d.info.suggested));assert.match(d.info.suggested,/\.tex$/i);
  assert.deepEqual(await snapshot(project),before);assert.deepEqual(await proposalState(),proposal);cleanSignals();save();
}
async function zipDownload(project,expected,label) {
  const b=H.$(page,"export-zip");await H.reveal(page,b);const before=await snapshot(project),proposal=await proposalState(),d=await downloadActual(b,label);assert.match(d.info.suggested,/\.zip$/i);
  const files=unzip(d.bytes);assert.deepEqual(Object.keys(files).sort(),Object.keys(expected).sort());for(const [name,bytes]of Object.entries(expected))assert.deepEqual(files[name],bytes,name);
  d.info.entries=Object.fromEntries(Object.entries(files).map(([n,b])=>[n,identity(b)]));assert.deepEqual(await snapshot(project),before);assert.deepEqual(await proposalState(),proposal);cleanSignals();save();
}
async function caretBinding(project,files) {
  const plan=planPrefix(files[TARGET],LABEL,"lab","label");await showAt(project,plan.oldRange.end,["label"],"show-captured-label-before-caret-move");
  const movedCaret=point(files[TARGET],TEXT);assert.notEqual(movedCaret,plan.oldRange.end);
  await caret(movedCaret);const before=await snapshot(project),b=choice("label");
  assert.equal(before.view.start,movedCaret);assert.equal(before.view.end,movedCaret);
  assert.ok(await b.count()<=1);let clickedCapturedLabel=false;
  if(await b.count()===1 && await b.isVisible() && await b.isEnabled()){
    await b.click();clickedCapturedLabel=true;
  }
  const state=await settled();
  assert.ok(["idle","stale","empty","unavailable"].includes(state.state),"moved caret MUST refuse; original-range application is forbidden");
  await noEligible();const after=await snapshot(project);
  assert.deepEqual(after,before,"moved-caret refusal preserves entire source/gen/head/config/session/selection snapshot");
  assert.equal(after.view.start,movedCaret);assert.equal(after.view.end,movedCaret);
  record.actions.push({label:"strict-moved-caret-refusal",clickedCapturedLabel,state,originalRange:plan.oldRange,movedCaret,before:compactSnapshot(before),after:compactSnapshot(after),noMutation:true});
  cleanSignals();save();
  await showAt(project,movedCaret,["textbf","textit"],"fresh-show-at-user-moved-text-caret");
  await dismissUnchanged(project,"dismiss-fresh-text-proposal-without-consuming-cold-prefix");
}
async function armUserInputObserver() {
  assert.equal(ownedInputProbeToken,null);
  const token=`completion-input-${Date.now()}-${Math.random().toString(36).slice(2)}`;
  await page.evaluate(token=>{
    const key="__oopCompletionInputObserver",e=document.querySelector('[data-testid="editor"]');
    if(window[key] || !e || e.disabled || e.readOnly)throw new Error("Cannot arm owned writable-editor input observer");
    const g={token,element:e,fileId:e.dataset.fileId,events:[],overflow:false};
    g.listener=event=>{
      if(g.events.length>=128){g.overflow=true;return;}
      const value=e.value;
      if(value.length>8192 || String(event.inputType||"").length>128)g.overflow=true;
      g.events.push({inputType:String(event.inputType||"").slice(0,128),isTrusted:event.isTrusted,
        fileId:e.dataset.fileId,sameTarget:event.target===e,writable:!e.disabled&&!e.readOnly,
        value:value.length<=8192?value:null,utf16Length:value.length});
    };
    // Passive DOM observation ONLY: no handler/API replacement or event dispatch.
    e.addEventListener("input",g.listener,{capture:true,passive:true});window[key]=g;
  },token);
  ownedInputProbeToken=token;
}
async function stopUserInputObserver() {
  if(ownedInputProbeToken===null)return null;
  const token=ownedInputProbeToken;
  const captured=await page.evaluate(token=>{
    const key="__oopCompletionInputObserver",g=window[key];
    if(!g || g.token!==token)throw new Error("Owned input observer identity missing/changed");
    g.element.removeEventListener("input",g.listener,true);
    const live=document.querySelector('[data-testid="editor"]');
    const captured={originalFileId:g.fileId,liveFileId:live?.dataset.fileId ?? null,
      sameLiveElement:live===g.element,events:g.events,overflow:g.overflow,listenerRemoved:true};
    delete window[key];return captured;
  },token);
  ownedInputProbeToken=null;return captured;
}
async function observedUserFill(project,edited,before) {
  let captured=null,after=null,saved=null,operationError=null,cleanupError=null,measuredStamp=null;
  await armUserInputObserver();
  try{
    await H.$(page,"editor").fill(edited);
    await page.waitForFunction(({generation,text})=>{
      const s=document.querySelector('[data-testid="analysis-panel"]'),e=document.querySelector('[data-testid="editor"]');
      return Number(s?.dataset.generation)>generation && e?.value===text;
    },{generation:Number(before.stamp.generation),text:edited});
    saved=await settleSave(project);after=await snapshot(project);
  }catch(e){operationError=e;throw e;}
  finally{
    try{captured=await bounded(stopUserInputObserver(),5000,"owned input observer removal timeout");record.inputObserverCleanup={listenerRemoved:captured.listenerRemoved};}
    catch(e){cleanupError=e;record.inputObserverCleanup={listenerRemoved:false,error:String(e.message||e).slice(0,1024)};}
    try{measuredStamp=await bounded(H.stamp(page),5000,"post-input actual model stamp timeout");}
    catch(e){if(!cleanupError)cleanupError=e;}
    const values=Object.create(null),events=(captured?.events||[]).map(event=>{
      const {value,...meta}=event;
      if(value===null)return {...meta,valueHash:null,controlledValue:false};
      const bytes=Buffer.from(value,"utf8"),hash=sha(bytes);values[hash]={utf16:value.length,...identity(bytes)};
      return {...meta,valueHash:hash,controlledValue:value===""||value===before.view.source||value===edited};
    });
    const delta=measuredStamp?Number(measuredStamp.generation)-Number(before.stamp.generation):null;
    record.userInputObservation={instrumentation:"owned passive capture input listener; NOT a product API hook",beforeStamp:before.stamp,
      afterStamp:measuredStamp,generationDelta:delta,deliveredInputCount:events.length,events,values,
      originalFileId:captured?.originalFileId,liveFileId:captured?.liveFileId,sameLiveElement:captured?.sameLiveElement,
      overflow:captured?.overflow ?? null,listenerRemoved:captured?.listenerRemoved===true,
      outcome:operationError?"operation-failed-with-measurements":"captured-pending-exact-accounting"};
    if(cleanupError){if(operationError)record.userInputObservation.cleanupError=String(cleanupError.message||cleanupError).slice(0,1024);else throw cleanupError;}
  }
  const m=record.userInputObservation;
  try{
  assert.equal(m.listenerRemoved,true);assert.equal(m.overflow,false,"real input observation overflow");
  assert.ok(m.deliveredInputCount>=1&&m.deliveredInputCount<=128,"expected1..128 actual DOM input deliveries");
  assert.equal(canonical(m.originalFileId),canonical(before.view.editorFile));assert.equal(canonical(m.liveFileId),canonical(before.view.editorFile));
  for(const event of m.events){assert.equal(canonical(event.fileId),canonical(before.view.editorFile));assert.equal(event.sameTarget,true);assert.equal(event.writable,true);assert.equal(event.controlledValue,true,"input delivery value outside controlled old/empty/final source set");}
  assert.equal(captured.events[captured.events.length-1].value,edited,"final actual delivered DOM value must equal intended normal user edit");
  assert.deepEqual(after.stamp,m.afterStamp,"model stamp stable through observer settlement");sameAuthority(after,before);
  assert.equal(after.view.editorFile,before.view.editorFile);assert.equal(after.view.source,edited);
  assert.equal(m.generationDelta,m.deliveredInputCount,`UNATTRIBUTED_EDIT: model generation delta${m.generationDelta} vs actual writable same-file inputs${m.deliveredInputCount}`);
  m.outcome="exact-input-count-accounted";return {saved,after};
  }catch(e){m.outcome="input-accounting-failed";m.accountingError=String(e.message||e).slice(0,1024);throw e;}
}
async function run() {
  inputs=preflight();({chromium,errors:playwrightErrors}=require("playwright"));save();assert.deepEqual(census(),[]);
  profile=fs.mkdtempSync(path.join(os.tmpdir(),"command-completion-profile-"));await serve();
  stage("online-start","UI_STARTUP");await launch(false);record.online.processes=census();assert.ok(record.online.processes.length);
  const response=await page.goto(base);assert.ok(response?.ok());validateCsp(await response.text(),inputs.headers);assert.equal(response.headers()["content-security-policy"],inputs.csp);await H.ready(page);
  const a=`Completion-A-${Date.now()}`,b=`Completion-B-${Date.now()}`;record.projects={a,b};
  const files={[TARGET]:A_BYTES,[OTHER]:OTHER_BYTES},bFiles={[B_FILE]:B_BYTES};
  const initialA=await createProject(a,files);await closeProject();const initialB=await createProject(b,bFiles);await closeProject();
  await openProject(a,TARGET);const ids=initialA.ids,bHead=initialB.head;await assertSource(a,files,ids,initialA.head);
  record.online.target={id:ids[TARGET],path:TARGET,source:identity(A_BYTES)};
  const checks=[{id:initialA.head.id,files:{...files}}];const section=planPrefix(files[TARGET],SEC,"sec","section");
  assert.notEqual(section.rawStartBytes,section.oldRange.start,"Unicode/emoji/CRLF fixture separates UTF8 raw bytes from DOM UTF16");
  await showAt(a,section.oldRange.end,["section"],"show-section-with-Unicode-before-prefix");
  await dismissUnchanged(a,"dismiss-section-unchanged");await showAt(a,section.oldRange.end,["section"],"fresh-show-section-before-one-insert");
  const sectionHead=await consume(a,files,ids,section,"explicit-section-insert-once");checks.push({id:sectionHead.id,files:{...files}});
  await noEligible();await sourceDownload(a,files[TARGET],"online-section.tex");await zipDownload(a,files,"online-section.zip");
  assert.deepEqual(await H.head(page,b),bHead);await verifyRevision(b,bHead.id,bFiles,initialB.ids);await history(a,checks,ids,"old-raw-CRLF-preimage-after-section");
  await showAt(a,point(files[TARGET],TEXT),["textbf","textit"],"ordinal-text-prefix-candidates");await dismissUnchanged(a,"dismiss-text-unchanged");
  await caretBinding(a,files);assert.deepEqual(await H.head(page,b),bHead);
  await showAt(a,point(files[TARGET],TEXT),["textbf","textit"],"show-before-actual-user-edit");stage("actual-user-source-edit","USER_SOURCE_INVALIDATION");
  const beforeEdit=await snapshot(a),edited=dom(files[TARGET])+"% Actual user edit invalidates the captured completion.\n";
  const {saved:userSaved,after:afterEdit}=await observedUserFill(a,edited,beforeEdit);
  // Ordinary textarea editing intentionally serializes LF, unlike completion's
  // raw-preserving literal edit. Account for that legitimate user change exactly.
  files[TARGET]=Buffer.concat([BOM,Buffer.from(edited,"utf8")]);const editedHead=await assertSource(a,files,ids);
  record.actions.push({label:"actual-user-source-edit",saved:userSaved,before:compactSnapshot(beforeEdit),after:compactSnapshot(afterEdit),
    generationDelta:record.userInputObservation.generationDelta,actualInputCount:record.userInputObservation.deliveredInputCount,intentionalTextareaLfSerialization:true});checks.push({id:editedHead.id,files:{...files}});
  await rejectOld(a,"old-text-proposal-refused-after-source-edit");
  // Actual file A->other->A invalidates authority even with unchanged bytes.
  await showAt(a,point(files[TARGET],TEXT),["textbf","textit"],"show-before-file-A-other-A");const fileBaseline=await snapshot(a);
  await selectFile(OTHER);await assertSource(a,files,ids,null,OTHER);await noEligible();await selectFile(TARGET);await assertSource(a,files,ids,editedHead);
  await rejectOld(a,"file-A-other-A-old-proposal-not-revived");const afterFile=await snapshot(a);assert.deepEqual(afterFile.head,fileBaseline.head);assert.deepEqual(afterFile.config,fileBaseline.config);assert.deepEqual(afterFile.stamp,fileBaseline.stamp);
  // Project transitions wait exact destination title/session/head/attached target.
  await showAt(a,point(files[TARGET],TEXT),["textbf","textit"],"show-before-project-A-B");const oldSession=(await H.stamp(page)).session;
  await closeProject();await openProject(b,B_FILE,{previousSession:oldSession});await assertSource(b,bFiles,initialB.ids,bHead,B_FILE);await rejectOld(b,"A-to-B-captured-proposal-discarded");
  await closeProject();await openProject(a,TARGET);await assertSource(a,files,ids,editedHead);await rejectOld(a,"A-B-A-old-proposal-not-revived");
  await showAt(a,point(files[TARGET],TEXT),["textbf","textit"],"fresh-request-after-A-B-A");const beforeClose=await H.stamp(page);
  await closeProject();await openProject(a,TARGET,{previousSession:beforeClose.session});await assertSource(a,files,ids,editedHead);await rejectOld(a,"A-close-A-old-proposal-discarded");
  await showAt(a,point(files[TARGET],TEXT),["textbf","textit"],"fresh-request-after-A-close-A");await dismissUnchanged(a,"dismiss-fresh-request-after-session-change");
  for(const [label,anchor,offset] of [["comment","% Comment context \\sec",0],["verbatim","Verbatim context \\sec",0],["escaped-control-symbol","Escaped context \\\\sec",0],["middle-token","Middle context \\section",-4],["case-sensitive","Case context \\Sec",0]]){
    await showAt(a,point(files[TARGET],anchor,offset),[],"no-suggestions-"+label);
  }
  const noncollapsed=point(files[TARGET],TEXT);await showAt(a,noncollapsed-2,[],"no-suggestions-noncollapsed",noncollapsed);
  await assertSource(a,files,ids,editedHead);await sourceDownload(a,files[TARGET],"online-after-user-edit.tex");await zipDownload(a,files,"online-final.zip");await history(a,checks,ids,"all-old-preimages-before-cold");
  assert.deepEqual(await H.head(page,b),bHead);await verifyRevision(b,bHead.id,bFiles,initialB.ids);
  stage("wait-offline-readiness","COLD_OFFLINE");await page.waitForFunction(()=>window.__oopOfflineReady===true,null,{timeout:90000});await collectCsp();cleanSignals();
  record.cold.oldProcesses=census();record.cold.previousSession=(await H.stamp(page)).session;
  await bounded(context.close(),10000,"online Chromium close timeout");context=null;page=null;record.cold.censusZero=await reap();await stopOrigin();await refusal();record.cold.originRefused=true;assert.deepEqual(census(),[]);
  stage("cold-new-offline-browser","COLD_OFFLINE");await launch(true);record.cold.offlineBeforeNavigation=true;record.cold.newProcesses=census();assert.ok(record.cold.newProcesses.length);
  const oldIds=new Set(record.cold.oldProcesses.map(p=>`${p.pid}:${p.startticks}`));assert.ok(record.cold.newProcesses.every(p=>!oldIds.has(`${p.pid}:${p.startticks}`)));
  await page.goto(base);await H.ready(page);await openProject(a,TARGET,{previousSession:record.cold.previousSession});await assertSource(a,files,ids,editedHead);await rejectOld(a,"cold-no-old-proposal-authority");
  await history(a,checks,ids,"cold-actual-preimages-still-immutable");assert.deepEqual(await H.head(page,b),bHead);await verifyRevision(b,bHead.id,bFiles,initialB.ids);
  const bold=planPrefix(files[TARGET],TEXT,"text","textbf");await showAt(a,bold.oldRange.end,["textbf","textit"],"offline-fresh-text-prefix");
  const offlineHead=await consume(a,files,ids,bold,"offline-explicit-textbf-insert-once");checks.push({id:offlineHead.id,files:{...files}});
  await sourceDownload(a,files[TARGET],"offline-textbf.tex");await zipDownload(a,files,"offline-final.zip");await history(a,checks,ids,"offline-new-and-all-preimages");
  await assertSource(a,files,ids,offlineHead);assert.deepEqual(await H.head(page,b),bHead);await verifyRevision(b,bHead.id,bFiles,initialB.ids);
  record.cold.final={head:offlineHead.id,targetId:ids[TARGET],path:TARGET,source:identity(files[TARGET]),exactOldRevisionsRechecked:true};
  await collectCsp();cleanSignals();record.result="PASS";
}
async function failureEvidence() {
  if(!page||page.isClosed())return;
  record.failure.ui={stamp:await page.evaluate(()=>{
    const s=document.querySelector('[data-testid="analysis-panel"]');
    return {generation:s?.dataset.generation ?? null,session:s?.dataset.session ?? null,head:s?.dataset.head ?? null};
  })};
  record.failure.ui.completion=await status();record.failure.ui.proposal=await proposalState();
  record.failure.ui.view=await page.evaluate(()=>({
    project:document.querySelector(".workspace-header h1")?.textContent,save:document.querySelector(".workspace-header .status")?.textContent,
    file:document.querySelector('[data-testid="editor"]')?.dataset.fileId,start:document.querySelector('[data-testid="editor"]')?.selectionStart,
    end:document.querySelector('[data-testid="editor"]')?.selectionEnd,sourcePreview:document.querySelector('[data-testid="editor"]')?.value?.slice(0,1024)}));
}
save();
(async()=>{
  let timer;const deadline=new Promise((_,reject)=>{timer=setTimeout(()=>{stopped=true;reject(new Error("harness deadline360s"));},360000);});
  try{await Promise.race([run(),deadline]);}
  catch(e){fail(e);save({halt:false});try{await bounded(failureEvidence(),5000,"failure observation timeout");}catch{}process.exitCode=1;}
  finally{
    stopped=true;clearTimeout(timer);
    if(ownedInputProbeToken!==null && page && !page.isClosed()){
      try{const c=await bounded(stopUserInputObserver(),5000,"final input observer cleanup timeout");record.inputObserverCleanup={listenerRemoved:c?.listenerRemoved===true,finalCleanup:true};}
      catch(e){record.inputObserverCleanup={listenerRemoved:false,error:String(e.message||e).slice(0,1024)};fail(e,"INPUT_OBSERVER_CLEANUP");process.exitCode=1;}
    }
    if(launching){try{context=await bounded(launching,20000,"late launch cleanup timeout");}catch(e){fail(e,"CLEANUP");process.exitCode=1;}}
    try{if(context){try{await bounded(collectCsp(),5000,"CSP cleanup timeout");}finally{await bounded(context.close(),10000,"browser cleanup timeout");context=null;}}}catch(e){fail(e,"CLEANUP");process.exitCode=1;}
    try{record.cleanup={chromiumRemaining:await reap(),productApiHooksInstalled:false};}catch(e){fail(e,"CLEANUP");process.exitCode=1;}
    try{await stopOrigin();}catch(e){fail(e,"CLEANUP");process.exitCode=1;}
    if(profile){try{fs.rmSync(profile,{recursive:true,force:true});record.profileCleaned=true;}catch(e){fail(e,"CLEANUP");process.exitCode=1;}}
    try{cleanSignals();}catch(e){fail(e,"OBSERVED_FAILURE");process.exitCode=1;}
    save({halt:false});process.stdout.write(JSON.stringify({result:record.result,failure:record.failure})+"\n");
  }
})();
