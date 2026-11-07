// Actual published Overclone UI: empty project -> explicit blank article ->
// normal save/compile/download, then whole-browser cold offline edit and repeat.
// Root owns execution. No starter source import/fill, product/worker mocks, E1,
// provider/Drive action, storage writes, CDP or security switches. Only the cold
// phase's ordinary USER edit uses editor.fill. No full compatibility/render claim.
// Required env: PUBLISHED_WWWROOT, AUTHORING_RUNTIME_DIR (497 pins.json + files/),
// RESULT_DIR (existing writable exclusively owned directory). No PDF_RUNTIME_DIR.
const assert = require("node:assert/strict"), fs = require("node:fs"), os = require("node:os");
const path = require("node:path"), http = require("node:http"), { createHash } = require("node:crypto");
const H = require("./review-ui-helpers.cjs"), { validate: validateCsp } = require("./test-csp.cjs");
const need = n => { assert.ok(process.env[n], `${n} required`); return process.env[n]; };
const sha = b => createHash("sha256").update(b).digest("hex");
const identity = b => ({ bytes: b.length, sha256: sha(b) });
const OUT = path.resolve(need("RESULT_DIR")), RECEIPT = path.join(OUT, "article-starter-receipt.json");
assert.ok(fs.statSync(OUT).isDirectory(), "existing writable RESULT_DIR required");
// Exclusive creation refuses even a prior failed receipt. Never retry to green.
fs.closeSync(fs.openSync(RECEIPT, "wx"));
const EXPECTED = Buffer.from("\\documentclass[10pt]{article}\n\\begin{document}\n\\noindent Write your document here.\n\\end{document}\n", "utf8");
const PROFILE = "8e9071ff18e5f93203266e7a85a6c4bba0597f9fba2e692c5651f68997e31894";
const NATIVE = /\/__oop\/native-worker\/(compile|pdf)\b/, COMPILE = /\/__oop\/native-worker\/compile\b/, PDF = /\/__oop\/native-worker\/pdf\b/;
const BUDGET = 16 * 1024 * 1024, PORT = 8772;
const record = { result: "FAIL", scope: "explicit-article-starter-online-cold-offline-native-download",
  actualBrowserExecution: "started by caller", expectedSource: identity(EXPECTED), sdk: null, compiler: null,
  online: {}, cold: {}, workers: [], pageErrors: [], cspViolations: [], externalAttempts: [], externalSuccesses: [],
  rendererRechecks: [], failure: null, cleanup: null,
  limits: ["Observed UI contract only, not synthetic/foreign callback race coverage.",
    "No renderer installed; no preview/physical/font/KNU/general compatibility proof.",
    "Static PWA asset traffic is observed, not attributed to starter domain activity."] };
let phase = "output-preflight", category = "PREFLIGHT", root, runtime, inputs, chromium, playwrightErrors;
let context = null, launching = null, page = null, server = null, profile = null, base = null, stopped = false;
const nativeCount = () => record.workers.filter(w => NATIVE.test(w.url)).length;
const running = () => { if (stopped) throw new Error("Deadline or cleanup reached"); };
const stage = (p, c) => { running(); phase = p; category = c; };
function outputBytes() {
  let total = 0, entries = 0; const pending = [OUT];
  // Metadata may already occupy subdirectories; never delete/overwrite it.
  while (pending.length) {
    const dir = pending.pop();
    for (const n of fs.readdirSync(dir)) {
      entries++; assert.ok(entries <= 4096, "bounded owned results inventory");
      const file = path.join(dir, n), s = fs.lstatSync(file);
      assert.ok(!s.isSymbolicLink(), "no output symlinks");
      if (s.isDirectory()) pending.push(file); else { assert.ok(s.isFile()); total += s.size; }
    }
  }
  return total;
}
function save() {
  const data = JSON.stringify(record, null, 2) + "\n";
  assert.ok(Buffer.byteLength(data) <= 131072, "bounded receipt <=128KiB");
  assert.ok(outputBytes() - fs.statSync(RECEIPT).size + Buffer.byteLength(data) <= BUDGET, "results <=16MiB");
  fs.writeFileSync(RECEIPT, data);
}
function fail(error, kind = category) {
  if (!record.failure) record.failure = { phase, classification: kind, message: String(error.message || error).slice(0, 1024) };
  record.result = "FAIL";
}
async function bounded(promise, ms, message) {
  let timer;
  try { return await Promise.race([promise, new Promise((_, reject) => { timer = setTimeout(() => reject(new Error(message)), ms); })]); }
  finally { clearTimeout(timer); }
}
function append(list, entry, max = 128) {
  if (list.length < max) list.push(entry); else record.observationOverflow = true;
}
function cleanSignals() {
  assert.ok(!record.observationOverflow, "observation budget not exceeded");
  for (const key of ["pageErrors", "cspViolations", "externalAttempts", "externalSuccesses"])
    assert.deepEqual(record[key], [], key + " must remain zero");
  assert.ok(!record.workers.some(w => PDF.test(w.url)), "zero PDF workers: renderer absent");
}
const TYPES = { ".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8", ".mjs": "text/javascript",
  ".css": "text/css; charset=utf-8", ".json": "application/json", ".wasm": "application/wasm", ".png": "image/png",
  ".svg": "image/svg+xml", ".ico": "image/x-icon", ".txt": "text/plain", ".dat": "application/octet-stream",
  ".blat": "application/octet-stream", ".pdb": "application/octet-stream" };
const inside = (r, f) => f.startsWith(r + path.sep);
function preflight() {
  root = fs.realpathSync(need("PUBLISHED_WWWROOT")); runtime = path.resolve(need("AUTHORING_RUNTIME_DIR"));
  assert.ok(outputBytes() <= BUDGET - 3 * 1024 * 1024, "reserve result capacity before producing artifacts");
  const headers = fs.readFileSync(path.join(__dirname, "../docs/examples/static-csp-headers.conf"), "utf8");
  const html = fs.readFileSync(path.join(root, "index.html"), "utf8"); validateCsp(html, headers);
  const cspLine = headers.split("\n").map(s => s.trim()).find(s => s.startsWith("Content-Security-Policy:")); assert.ok(cspLine);
  const text = fs.readFileSync(path.join(root, "service-worker-assets.js"), "utf8");
  const match = text.match(/self\.assetsManifest\s*=\s*(\{[\s\S]*\})\s*;/); assert.ok(match, "generated SDK manifest JSON");
  const manifest = JSON.parse(match[1]); assert.equal(typeof manifest.version, "string");
  assert.equal(manifest.assets.length, 237, "current SDK237 asset inventory");
  const names = new Set();
  for (const asset of manifest.assets) {
    assert.equal(typeof asset.url, "string");
    assert.ok(!path.isAbsolute(asset.url) && !asset.url.includes("\\") && !asset.url.split("/").includes(".."));
    assert.ok(!names.has(asset.url)); names.add(asset.url);
    const file = fs.realpathSync(path.join(root, asset.url)); assert.ok(inside(root, file));
    assert.equal("sha256-" + createHash("sha256").update(fs.readFileSync(file)).digest("base64"), asset.hash, `SDK SRI ${asset.url}`);
  }
  record.sdk = { version: manifest.version, assets: manifest.assets.length, everyAssetHashVerified: true, strictCspVerified: true };
  const pinsPath = path.join(runtime, "pins.json"), pinBytes = fs.readFileSync(pinsPath), pins = JSON.parse(pinBytes);
  assert.equal(pins.length, 497); const unique = new Set(); let total = 0;
  for (const p of pins) {
    assert.match(p.name, /^[A-Za-z0-9._-]{1,200}$/); assert.ok(![".", ".."].includes(p.name));
    assert.ok(!unique.has(p.name)); unique.add(p.name);
    assert.ok(Number.isSafeInteger(p.length) && p.length > 0 && p.length <= 33554432);
    total += p.length; assert.ok(total <= 67108864); assert.match(p.sha256, /^[0-9a-f]{64}$/);
    const f = path.join(runtime, "files", p.name); assert.equal(fs.statSync(f).size, p.length); assert.equal(sha(fs.readFileSync(f)), p.sha256, p.name);
  }
  const pin = n => { const p = pins.find(p => p.name === n); assert.ok(p); return p; };
  assert.equal(pin("engine.js").sha256, "39384532be2e32fd09dba99226db9da7572032c70da2bb2428fc9123c7fd163f");
  assert.equal(pin("engine.wasm").sha256, "7d239273b63b42d0c8f31d5e198a221c200c222f365760c20b9178408077048e");
  assert.equal(pin("pdflatex.fmt").sha256, "e2b5174b5aa6e552e03e9cf5dae7a7594e68d75962f48a6a30dc55da588b2595");
  record.compiler = { resources: 497, totalBytes: total, pinsSha256: sha(pinBytes), everyPinVerified: true,
    loader: pin("engine.js"), wasm: pin("engine.wasm"), format: pin("pdflatex.fmt"), expectedProfile2Identity: PROFILE };
  return { headers, csp: cspLine.slice("Content-Security-Policy:".length).trim(), pinsPath,
    files: pins.map(p => path.join(runtime, "files", p.name)) };
}
function census() {
  const found = [];
  for (const pid of fs.readdirSync("/proc")) {
    if (!/^\d+$/.test(pid)) continue; let exe, stat;
    try { exe = path.basename(fs.readlinkSync(`/proc/${pid}/exe`)); } catch { continue; }
    if (!/chromium|chrome|headless_shell/i.test(exe)) continue;
    try { stat = fs.readFileSync(`/proc/${pid}/stat`, "utf8"); }
    catch (e) { if (["ENOENT", "ESRCH"].includes(e.code)) continue; throw e; }
    const startticks = stat.slice(stat.lastIndexOf(") ") + 2).split(" ")[19]; assert.match(startticks, /^\d+$/);
    found.push({ pid: Number(pid), startticks, exe });
  }
  return found.sort((a, b) => a.pid - b.pid);
}
async function reap() {
  let remaining;
  for (let i = 0; i < 40; i++) { remaining = census(); if (!remaining.length) return remaining; await new Promise(r => setTimeout(r, 100)); }
  assert.deepEqual(remaining, [], "all browser processes must exit"); return remaining;
}
async function stopOrigin() {
  if (!server?.listening) return;
  await bounded(new Promise((resolve, reject) => { server.close(e => e ? reject(e) : resolve()); server.closeIdleConnections(); }), 5000, "origin close timeout");
}
async function refusal() {
  await new Promise((resolve, reject) => {
    const r = http.get(base, response => { response.resume(); reject(new Error("origin still accepts connections")); });
    r.on("error", e => e.code === "ECONNREFUSED" ? resolve() : reject(e));
    r.setTimeout(2000, () => r.destroy(new Error("origin refusal not proven")));
  });
}
async function launch(offline) {
  running(); launching = chromium.launchPersistentContext(profile, { headless: true, offline, acceptDownloads: true, timeout: 15000 });
  context = await launching; launching = null; running();
  context.on("request", request => {
    const url = new URL(request.url());
    if (/^https?:$/.test(url.protocol) && url.origin !== base) append(record.externalAttempts, { phase, origin: url.origin });
  });
  await context.route("**/*", route => {
    if (new URL(route.request().url()).origin === base) return route.continue();
    return route.abort();
  });
  await context.addInitScript(() => {
    window.__starterCsp = [];
    addEventListener("securitypolicyviolation", e => window.__starterCsp.push(e.effectiveDirective));
  }); // observation only, never product/source/storage injection
  const watch = p => {
    p.on("worker", w => append(record.workers, { phase, url: w.url() }));
    p.on("pageerror", e => append(record.pageErrors, { phase, message: String(e.message).slice(0, 1024) }));
  };
  context.pages().forEach(watch); context.on("page", watch);
  context.on("response", r => { if (new URL(r.url()).origin !== base && r.ok()) append(record.externalSuccesses, { phase, origin: new URL(r.url()).origin }); });
  page = await context.newPage(); page.setDefaultTimeout(30000);
}
async function collectCsp() {
  if (!context) return;
  for (const p of context.pages()) if (new URL(p.url()).origin === base)
    for (const directive of await p.evaluate(() => window.__starterCsp || [])) append(record.cspViolations, { phase, directive });
}
async function serve() {
  server = http.createServer((req, res) => {
    let name, file;
    try { name = decodeURIComponent(new URL(req.url, `http://127.0.0.1:${PORT}`).pathname); }
    catch { res.writeHead(400); res.end(); return; }
    if (name.includes("\\") || name.includes("\0") || name.split("/").includes("..")) { res.writeHead(400); res.end(); return; }
    try { file = fs.realpathSync(path.resolve(root, "." + (name === "/" ? "/index.html" : name))); }
    catch { res.writeHead(404); res.end(); return; }
    if (!inside(root, file) || !fs.statSync(file).isFile()) { res.writeHead(404); res.end(); return; }
    res.writeHead(200, { "Content-Type": TYPES[path.extname(file).toLowerCase()] || "application/octet-stream",
      "Cache-Control": "no-store", "Content-Security-Policy": inputs.csp, "X-Content-Type-Options": "nosniff" });
    const stream = fs.createReadStream(file); stream.on("error", () => res.destroy()); stream.pipe(res);
  });
  await new Promise((resolve, reject) => { server.once("error", reject); server.listen(PORT, "127.0.0.1", resolve); });
  base = `http://127.0.0.1:${PORT}`;
}
async function storedProject(name) {
  return page.evaluate(async name => {
    const m = await import("./js/workspace-storage.js");
    const project = (await m.listProjects()).find(p => p.name === name);
    if (!project) return null;
    return { id: project.id, name: project.name, head: project.headRevisionId ?? null };
  }, name); // read-only; head() cannot be used until an empty project's first save
}
async function compilerSelection() {
  return page.evaluate(() => ({
    main: document.querySelector('select[aria-label="Compile main file"]')?.value,
    engine: document.querySelector('select[aria-label="Compile engine"]')?.value,
    bibtex: (document.querySelector('[data-testid="bibliography-tool"]') ? document.querySelector('[data-testid="bibliography-tool"]').value === "Bibtex" : document.querySelector('[data-testid="use-bibtex"]')?.checked) ?? null,
    version: document.querySelector('[data-testid="compile-panel"]')?.dataset.selectionVersion,
  }));
}
async function config() {
  return { selection: await compilerSelection(), ui: await page.evaluate(() => ({
    outcome: document.querySelector('[data-testid="compile-status"]')?.textContent,
    details: document.querySelector('[data-testid="compile-details"]')?.textContent,
    pdfLabel: document.querySelector('[data-testid="compiled-pdf-status"]')?.textContent,
    outputHash: document.querySelector('[data-testid="compile-panel"]')?.dataset.outputHash,
    current: document.querySelector('[data-testid="compile-panel"]')?.dataset.isCurrent,
    work: document.querySelector('[data-testid="compile-work-status"]')?.textContent.trim(),
  })) };
}
async function starterUnavailable() {
  await page.waitForFunction(() => [...document.querySelectorAll('[data-testid="starter-article"]')].every(b => b.disabled));
  const buttons = H.$(page, "starter-article");
  assert.equal(await buttons.evaluateAll(list => list.filter(b => !b.disabled).length), 0);
}
async function selectMain() {
  const entry = H.$(page, "select-file").filter({ hasText: "main.tex" }); assert.equal(await entry.count(), 1); await entry.click();
  await page.locator('.editor-toolbar label[for="editor"]', { hasText: "main.tex" }).waitFor();
}
async function settleNormalSave() {
  const writable = async () => {
    assert.equal(await page.locator(".banner-readonly, .banner-recovery").count(), 0, "writable project with no recovery banner required");
    assert.equal(await H.$(page, "editor").isDisabled(), false, "actual editor remains writable");
  };
  await writable();
  const button = H.$(page, "save"); let mode = "already-saved";
  if (await button.isEnabled()) {
    try { await button.click({ timeout: 2000 }); mode = "explicit-click"; }
    catch (error) {
      // Only autosave disabling Save during the ordinary click may take this
      // path. Other errors, or a timeout with Save still enabled, remain failures.
      if (!(error instanceof playwrightErrors.TimeoutError) || !(await button.isDisabled())) throw error;
      mode = "autosave-raced-click";
    }
  }
  await page.waitForFunction(() => {
    const status = document.querySelector(".workspace-header .status");
    return status?.textContent === "Saved."
      && !document.querySelector(".banner-readonly, .banner-recovery")
      && document.querySelector('[data-testid="editor"]')?.disabled === false;
  }, null, { timeout: 30000 });
  await writable();
  // Saved is not byte/head proof: both callers must still run exactSource/H.head.
  return { mode, status: "Saved.", writable: true, recovery: false };
}
async function exactSource(project, bytes, expectedHead = null) {
  const head = await H.head(page, project); if (expectedHead) assert.deepEqual(head, expectedHead);
  assert.equal(head.files.length, 1); const f = head.files[0]; assert.equal(f.path, "main.tex");
  assert.deepEqual(Buffer.from(f.bytes), bytes, "durable exact UTF8 bytes (including LF/noBOM)");
  const editor = H.$(page, "editor"); assert.equal(await editor.inputValue(), bytes.toString("utf8"));
  assert.equal(await editor.getAttribute("data-file-id"), f.id, "actual active source identity");
  assert.equal(await page.locator('.editor-toolbar label[for="editor"]').textContent(), "main.tex");
  assert.equal(await H.$(page, "select-file").count(), 1, "exact one-file tree");
  await starterUnavailable(); return head;
}
async function waitTools() {
  await H.reveal(page, H.$(page, "refresh-compile-tools"));
  await page.waitForFunction(() => document.querySelector('[data-testid="refresh-compile-tools"]')?.disabled === false, null, { timeout: 60000 });
}
async function resources(id, state, timeout = 60000) {
  await page.waitForFunction(id => document.querySelector(`[data-testid="${id}"]`)?.dataset.busy === "false", id, { timeout });
  assert.equal(await H.$(page, id).getAttribute("data-state"), state, await H.$(page, id).textContent());
}
async function noRenderer() {
  const n = nativeCount(); await H.reveal(page, H.$(page, "pdf-runtime-recheck")); await H.$(page, "pdf-runtime-recheck").click();
  await resources("pdf-runtime-status", "Empty"); assert.equal(nativeCount(), n);
  record.rendererRechecks.push({ phase, store: "Empty", newNativeBoots: 0,
    informationalHint: (await H.$(page, "renderer-readiness").textContent()).slice(0, 512) });
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
const bridge = () => page.evaluate(async () => (await import("./js/compile-port-bridge.js")).observations());
async function compile(project, bytes, head, label) {
  stage(label, "NATIVE_COMPILE"); const before = await bridge(), workerOffset = record.workers.length;
  const workerEvent = page.waitForEvent("worker", { predicate: w => COMPILE.test(w.url()), timeout: 45000 });
  await Promise.all([workerEvent, H.$(page, "compile").click()]);
  await page.waitForFunction(() => /^(?:Compilation finished\.|No new PDF:)/.test(document.querySelector('[data-testid="compile-status"]')?.textContent || ""), null, { timeout: 45000 });
  const terminal = (await H.$(page, "compile-status").textContent()).trim();
  const log = await H.$(page, "compiler-log").textContent() || "";
  record[label] = { terminal, log: log.slice(0, 16384), logBytes: Buffer.byteLength(log), logSha256: sha(Buffer.from(log)), logTruncated: log.length > 16384 }; save();
  assert.equal(terminal, "Compilation finished.");
  await page.waitForFunction(async () => (await import("./js/compile-port-bridge.js")).observations().active === 0, null, { timeout: 30000 });
  await page.waitForFunction(() => document.querySelector('[data-testid="compile-work-status"]')?.textContent.trim() === "Idle", null, { timeout: 30000 });
  const observed = await bridge(), trace = observed.trace.slice(before.trace.length);
  assert.ok(observed.started > before.started && trace.length > 0, "fresh actual native compilation");
  assert.ok(trace.every(t => t.entered && t.terminated && t.outcome === "native-return" && t.nativeReturn === 0));
  assert.ok(trace.every(t => t.tag.version === 4 && t.tag.profile === 2 && t.tag.runtimeIdentity === PROFILE && t.tag.tool === "latex"));
  assert.ok(record.workers.slice(workerOffset).some(w => COMPILE.test(w.url)));
  assert.equal(await H.$(page, "compile-panel").getAttribute("data-is-current"), "true");
  const hash = await H.$(page, "compile-panel").getAttribute("data-output-hash"); assert.match(hash, /^[0-9a-f]{64}$/);
  assert.equal((await H.$(page, "compiled-pdf-status").textContent()).trim(), "PDF up to date");
  assert.equal(await H.$(page, "pdf-runtime-status").getAttribute("data-state"), "Empty");
  assert.ok(log.length > 0 && log.length <= 131072); assert.match(log, /Output written on main\.pdf/);
  await exactSource(project, bytes, head); cleanSignals();
  Object.assign(record[label], { trace, outputHash: hash, current: true, uiIdle: true, bridgeActive: observed.active,
    actualWorkerBoots: record.workers.slice(workerOffset), rendererStore: "Empty", pdfWorkers: 0 });
  fs.writeFileSync(path.join(OUT, label + ".log"), log.slice(0, 16384), { flag: "wx" }); save(); return hash;
}
async function download(project, bytes, head, outputHash, label) {
  stage(label, "UI_DOWNLOAD_ORACLE");
  const before = { head: await exactSource(project, bytes, head), config: await config(), bridge: await bridge(), nativeCount: nativeCount() };
  assert.equal(await H.$(page, "download-pdf").isEnabled(), true);
  const target = path.join(OUT, label + ".pdf"); assert.ok(!fs.existsSync(target));
  const event = page.waitForEvent("download", { timeout: 30000 });
  const [d] = await Promise.all([event, H.$(page, "download-pdf").click()]);
  const suggested = d.suggestedFilename(); assert.ok(suggested.length <= 84 && !suggested.startsWith("."));
  assert.equal(path.basename(suggested), suggested); assert.match(suggested, /^[\p{L}\p{N}\p{M} _-]+\.pdf$/u);
  assert.ok(!/^(?:CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])\.pdf$/i.test(suggested));
  assert.equal(await bounded(d.failure(), 30000, "download completion observation timeout"), null);
  // Preflight the ACTUAL completed browser download before copying into results;
  // no Blob URL fetch, regenerated PDF, or synthetic payload is used.
  const staged = await bounded(d.path(), 5000, "download staging observation timeout"); assert.ok(staged);
  const size = fs.statSync(staged).size; assert.ok(size > 5 && size <= 1048576, "positive bounded controlled-article PDF");
  assert.ok(outputBytes() + size + 131072 <= BUDGET, "reserve artifact/receipt budget before saveAs");
  await bounded(d.saveAs(target), 30000, "download saveAs timeout");
  assert.equal(fs.statSync(target).size, size);
  const pdf = fs.readFileSync(target); assert.equal(pdf.subarray(0, 5).toString("ascii"), "%PDF-"); assert.equal(sha(pdf), outputHash);
  await page.waitForFunction(() => document.querySelector('[data-testid="pdf-download-status"]')?.textContent === "PDF download started.");
  const after = { head: await exactSource(project, bytes, head), config: await config(), bridge: await bridge(), nativeCount: nativeCount() };
  assert.deepEqual(after, before, "export cannot edit/save/recompile/change config or native work");
  cleanSignals(); assert.ok(outputBytes() <= BUDGET - 131072);
  const result = { artifact: label + ".pdf", suggestedFilename: suggested, ...identity(pdf), header: "%PDF-", failure: null,
    before, after, unchangedSourceHeadConfigAndNativeWork: true, mimeClaim: "not measured here; separate accepted download suite" };
  record[label] = result; save(); return result;
}
async function run() {
  stage("static-input-preflight", "PREFLIGHT"); inputs = preflight(); ({ chromium, errors: playwrightErrors } = require("playwright")); save();
  assert.deepEqual(census(), [], "dedicated container initially has no Chromium");
  profile = fs.mkdtempSync(path.join(os.tmpdir(), "article-starter-profile-")); await serve();
  stage("online-startup", "UI_STARTUP"); await launch(false); record.online.processes = census(); assert.ok(record.online.processes.length);
  const response = await page.goto(base); assert.ok(response?.ok()); validateCsp(await response.text(), inputs.headers);
  assert.equal(response.headers()["content-security-policy"], inputs.csp); await H.ready(page);
  const project = `ArticleStarter-${Date.now()}`; record.project = project;
  stage("online-empty-project", "STARTER_UI_CONTRACT");
  await H.reveal(page, page.locator("#new-project")); await page.locator("#new-project").fill(project);
  await page.getByRole("button", { name: "Create project", exact: true }).click();
  const starter = H.$(page, "starter-article"); await starter.waitFor();
  assert.equal((await starter.textContent()).trim(), "Start a blank article"); assert.equal(await starter.isEnabled(), true);
  await waitTools(); const emptyProject = await storedProject(project); assert.ok(emptyProject); assert.equal(emptyProject.head, null);
  assert.equal(await H.$(page, "select-file").count(), 0); assert.equal(await H.$(page, "editor").count(), 0);
  const stamp = await H.stamp(page); assert.equal(Number(stamp.generation), 0); assert.ok(stamp.session);
  const beforeStarter = await compilerSelection(); assert.equal(nativeCount(), 0); cleanSignals();
  record.online.empty = { project: emptyProject, stamp, noEditor: true, noTreeFiles: true, nativeWorkers: 0, selection: beforeStarter };
  stage("online-starter-click", "STARTER_UI_CONTRACT"); await starter.click();
  await page.locator('.editor-toolbar label[for="editor"]', { hasText: "main.tex" }).waitFor();
  assert.equal(await H.$(page, "editor").inputValue(), EXPECTED.toString("utf8"));
  const activeId = await H.$(page, "editor").getAttribute("data-file-id"); assert.ok(activeId);
  assert.equal(await H.$(page, "select-file").count(), 1); await starterUnavailable();
  assert.equal(nativeCount(), 0); assert.deepEqual(await compilerSelection(), beforeStarter); cleanSignals();
  // Settle an ordinary click OR completed autosave before exact head invariants.
  record.online.save = await settleNormalSave(); const savedHead = await exactSource(project, EXPECTED);
  assert.equal(savedHead.files[0].id, activeId); assert.equal(nativeCount(), 0);
  record.online.starter = { producedSource: identity(Buffer.from(savedHead.files[0].bytes)), activeId,
    savedHead, exactlyOneMain: true, repeatActionUnavailable: true, noNativeBoots: true, selectionUnchanged: true };
  fs.writeFileSync(path.join(OUT, "actual-starter-main.tex"), Buffer.from(savedHead.files[0].bytes), { flag: "wx" }); save();

  stage("online-resource-install", "RESOURCE_INSTALL"); const beforeInstall = nativeCount();
  await H.reveal(page, H.$(page, "runtime-recheck")); await H.$(page, "runtime-recheck").click(); await resources("runtime-status", "Empty");
  await H.$(page, "runtime-pins").setInputFiles(inputs.pinsPath); await H.$(page, "runtime-files").setInputFiles(inputs.files);
  await H.$(page, "runtime-install").click(); await resources("runtime-status", "Installed", 180000);
  assert.equal((await H.$(page, "runtime-status").textContent()).trim(), "Compiler resources installed. Use Check tools to verify local compilation availability.");
  await noRenderer(); await checkTools(); assert.equal(nativeCount(), beforeInstall);
  record.online.install = { state: "Installed", zeroNativeBoots: true, rendererStore: "Empty" };
  await configure(); const onlineHash = await compile(project, EXPECTED, savedHead, "online-compile");
  record.online.download = await download(project, EXPECTED, savedHead, onlineHash, "online-starter");

  stage("wait-offline-cache", "COLD_OFFLINE");
  await page.waitForFunction(() => window.__oopOfflineReady === true, null, { timeout: 90000 });
  record.online.offlineReady = true; await collectCsp(); cleanSignals();
  record.cold.oldProcesses = census(); await bounded(context.close(), 10000, "online browser close timeout"); context = null; page = null;
  record.cold.censusZero = await reap(); await stopOrigin(); await refusal(); record.cold.originRefused = true; assert.deepEqual(census(), []);
  const nativeBeforeCold = nativeCount(); stage("cold-offline-startup", "COLD_OFFLINE"); await launch(true);
  record.cold.offlineBeforeNavigation = true; record.cold.newProcesses = census(); assert.ok(record.cold.newProcesses.length);
  const oldIds = new Set(record.cold.oldProcesses.map(p => `${p.pid}:${p.startticks}`));
  assert.ok(record.cold.newProcesses.every(p => !oldIds.has(`${p.pid}:${p.startticks}`)));
  await page.goto(base); await H.ready(page); await H.open(page, project); // unconditional async dashboard Open
  await selectMain(); await exactSource(project, EXPECTED, savedHead); assert.equal(nativeCount(), nativeBeforeCold);
  stage("cold-resource-recheck", "RESOURCE_RECHECK"); await waitTools();
  await H.reveal(page, H.$(page, "runtime-recheck")); await H.$(page, "runtime-recheck").click(); await resources("runtime-status", "Installed");
  await noRenderer(); await checkTools(); assert.equal(nativeCount(), nativeBeforeCold);
  record.cold.recheck = { installed: true, zeroNewNativeBoots: true, rendererStore: "Empty", survivedHeadId: savedHead.id,
    exactSource: identity(EXPECTED), starterUnavailable: true };
  stage("cold-visible-edit-save", "OFFLINE_USER_EDIT");
  const edited = Buffer.from(EXPECTED.toString("utf8") + "% Offline edit\n", "utf8");
  await H.$(page, "editor").fill(edited.toString("utf8")); record.cold.save = await settleNormalSave();
  const editedHead = await exactSource(project, edited); assert.notEqual(editedHead.id, savedHead.id);
  assert.deepEqual(Buffer.from(savedHead.files[0].bytes), EXPECTED, "retained immutable preimage");
  assert.equal(nativeCount(), nativeBeforeCold, "ordinary edit/save never implicitly compiles");
  record.cold.edit = { oldHead: savedHead, newHead: editedHead, source: identity(edited), exactAppend: "% Offline edit\n" };
  fs.writeFileSync(path.join(OUT, "actual-offline-main.tex"), Buffer.from(editedHead.files[0].bytes), { flag: "wx" });
  await configure(); const offlineHash = await compile(project, edited, editedHead, "offline-compile");
  record.cold.download = await download(project, edited, editedHead, offlineHash, "offline-starter");
  record.cold.finalState = "saved-user-edited-source/current-successful-PDF/actual-download/renderer-Empty";
  // A trailing post-document comment need not change PDF bytes; fresh native
  // entry + new source head/current accepted result establish this new compile.
  await collectCsp(); cleanSignals(); record.result = "PASS";
}
async function failureEvidence() {
  if (!page || page.isClosed()) return;
  record.failure.ui = await page.evaluate(() => {
    const text = (id, n) => (document.querySelector(`[data-testid="${id}"]`)?.textContent || "").slice(0, n);
    return { compiler: text("compile-status", 512), work: text("compile-work-status", 512), log: text("compiler-log", 16384),
      runtime: text("runtime-status", 512), pdfRuntime: text("pdf-runtime-status", 512),
      sourceLabel: document.querySelector('.editor-toolbar label[for="editor"]')?.textContent,
      source: document.querySelector('[data-testid="editor"]')?.value?.slice(0, 2048),
      starter: text("starter-article", 128), download: text("pdf-download-status", 512) };
  });
}
save();
(async () => {
  let timer;
  const deadline = new Promise((_, reject) => { timer = setTimeout(() => { stopped = true; reject(new Error("harness deadline360s")); }, 360000); });
  try { await Promise.race([run(), deadline]); }
  catch (e) { fail(e); save(); try { await bounded(failureEvidence(), 5000, "failure observation timeout"); } catch {} process.exitCode = 1; }
  finally {
    stopped = true; clearTimeout(timer);
    if (launching) { try { context = await bounded(launching, 20000, "late launch cleanup timeout"); } catch (e) { fail(e, "CLEANUP"); process.exitCode = 1; } }
    try { if (context) {
      try { await bounded(collectCsp(), 5000, "CSP observation cleanup timeout"); }
      finally { await bounded(context.close(), 10000, "browser cleanup timeout"); context = null; }
    } } catch (e) { fail(e, "CLEANUP"); process.exitCode = 1; }
    try { record.cleanup = { browserCensus: await reap() }; } catch (e) { fail(e, "CLEANUP"); process.exitCode = 1; }
    try { await stopOrigin(); } catch (e) { fail(e, "CLEANUP"); process.exitCode = 1; }
    // Only the mkdtemp-created synthetic profile; never inputs/source/shared caches.
    if (profile) { try { fs.rmSync(profile, { recursive: true, force: true }); record.profileCleaned = true; } catch (e) { fail(e, "CLEANUP"); process.exitCode = 1; } }
    try { cleanSignals(); } catch (e) { fail(e, "OBSERVED_SECURITY_OR_WORKER_FAILURE"); process.exitCode = 1; }
    save(); process.stdout.write(JSON.stringify({ result: record.result, failure: record.failure }) + "\n");
  }
})();
