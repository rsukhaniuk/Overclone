// Actual published KNU 1.1 worker/UI + cold-offline acceptance. Run by lead
// against the combined publication. No success-response synthesis or TeX engine
// model: observations below retain and forward the original Worker MessageEvent.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const http = require("node:http");
const https = require("node:https");
const { createHash } = require("node:crypto");
const { chromium } = require("playwright");
const H = require("./review-ui-helpers.cjs");
const { $, reveal } = H;
function parseUrl(value, base) {
  try { return new URL(value, base); }
  catch (error) { throw new Error("Invalid static-test URL", { cause: error }); }
}
function parseJson(raw) {
  try { return JSON.parse(raw); }
  catch (error) { throw new Error("Invalid actual protocol or trusted metadata JSON", { cause: error }); }
}
const upstream = parseUrl(process.env.APP_URL || "http://static-origin:8080");
assert.ok(["http:", "https:"].includes(upstream.protocol) && !upstream.username && !upstream.password);
const output = process.env.RESULT_DIR || fs.mkdtempSync(path.join(os.tmpdir(), "knu-margins-results-"));
fs.mkdirSync(output, { recursive: true });
const profile = fs.mkdtempSync(path.join(os.tmpdir(), "knu-margins-profile-"));
const sha = bytes => createHash("sha256").update(bytes).digest("hex");
const ID = "knu-csc-2025-source", LEGACY = ID + "@1.0.0";
const INDENT = "KNU-SRC-PARINDENT-001", HEADING = "KNU-SRC-HEADING-DOT-001", MARGINS = "KNU-SRC-MARGINS-001";
const normalize = bytes => bytes.toString("utf8").replace(/^\uFEFF/, "").replace(/\r\n?/g, "\n");
const fixture = geometry => Buffer.from("\uFEFFУкраїна🙂\r\n\\setlength{\\parindent}{1.27cm}\r\n\\section{Методи}\r\n" + geometry + "\r\n");
const positive = fixture("\\geometry{left=2.5cm,right=10mm,top=20mm,bottom=22mm}");
const negative = fixture("\\geometry{left= 24.999 mm ,right=10mm,top=20mm,bottom=20mm}");
const unknown = fixture("\\geometry{left=1mm,right=10mm,top=20mm,bottom=20mm,unknown=20mm}");
const macro = fixture("\\geometry{left=\\dimexpr25mm\\relax,right=10mm,top=20mm,bottom=20mm}");
const missing = fixture("\\geometry{left=25mm}");
const fixBytes = fixture("\\geometry{left=1mm,unknown=20mm}").toString("utf8").replace("{1.27cm}", "{ 10 mm }");
const fix = Buffer.from(fixBytes), fixed = Buffer.from(fixBytes.replace("10 mm", "1.27cm"));
const result = {
  status: "running", phases: [], failures: [],
  source: { scriptSha256: sha(fs.readFileSync(__filename)), fixtureSha256: Object.fromEntries(
    Object.entries({ positive, negative, unknown, macro, missing, fix }).map(([name, bytes]) => [name, sha(bytes)])) },
  errors: [], externalRequests: [], coldNetworkSuccesses: [], loadedWasm: [],
};
let base, context, cold = false, latestMetadata, legacyMetadata, activePhase;
const bodyTasks = new Set();
function boundedPush(array, value, max = 32) { if (array.length < max) array.push(value); }
function writeResult() {
  const json = JSON.stringify(result, null, 2);
  assert.ok(Buffer.byteLength(json) <= 128 * 1024, "bounded result JSON");
  fs.writeFileSync(path.join(output, "knu-margins-result.json"), json + "\n");
}
async function phase(name, action) {
  activePhase = { name, status: "running", startedAt: new Date().toISOString() };
  result.phases.push(activePhase);
  await action();
  activePhase.status = "passed";
}
function requestBytes(url) {
  return new Promise((resolve, reject) => {
    const req = (url.protocol === "https:" ? https : http).get(url, response => {
      if (response.statusCode !== 200) { response.resume(); reject(new Error(`HTTP ${response.statusCode}: ${url.pathname}`)); return; }
      const chunks = []; let size = 0;
      response.on("data", chunk => {
        size += chunk.length;
        if (size > 8 * 1024 * 1024) req.destroy(new Error("static asset byte cap"));
        else chunks.push(chunk);
      });
      response.on("end", () => resolve(Buffer.concat(chunks)));
      response.on("error", reject);
    });
    req.setTimeout(15000, () => req.destroy(new Error("static asset deadline")));
    req.on("error", reject);
  });
}
const proxy = http.createServer((request, response) => {
  const url = parseUrl(request.url, upstream);
  if (url.origin !== upstream.origin) { response.writeHead(400); response.end(); return; }
  const remote = (url.protocol === "https:" ? https : http).request(url,
    { method: request.method, headers: { ...request.headers, host: url.host } }, reply => {
      response.writeHead(reply.statusCode, reply.headers); reply.pipe(response);
    });
  remote.setTimeout(15000, () => remote.destroy(new Error("proxy deadline")));
  remote.on("error", () => response.destroy()); request.pipe(remote);
});
proxy.on("clientError", (_error, socket) => socket.destroy());
async function stopProxy() {
  if (!proxy.listening) return;
  proxy.closeAllConnections();
  await new Promise((resolve, reject) => proxy.close(error => error ? reject(error) : resolve()));
}
function observeOriginalResponses() {
  const Native = window.Worker;
  const state = window.__knuMarginsObserved = { total: 0, seen: [], failures: [] };
  window.Worker = class extends Native {
    constructor(url, options) { super(url, options); this.knuRustWorker = String(url).includes("rust-worker.js"); }
    set onmessage(handler) {
      if (typeof handler !== "function") { super.onmessage = handler; return; }
      super.onmessage = event => {
        if (this.knuRustWorker && event.data?.canonical && typeof event.data.responseJson === "string") {
          try {
            const value = JSON.parse(event.data.responseJson);
            if (value.status === "completed" && value.payload?.profile?.id === "knu-csc-2025-source") {
              state.total++;
              state.seen.push({ snapshotId: value.payload.snapshotId, raw: event.data.responseJson });
              if (state.seen.length > 4) state.seen.shift();
            } else if (value.status === "failed" && state.failures.length < 8) state.failures.push(value.error?.code);
          } catch { /* Do not repair malformed protocol data; product handles it. */ }
        }
        handler.call(this, event); // ORIGINAL event and responseJson, unchanged.
      };
    }
  };
}
async function launch(offline) {
  context = await chromium.launchPersistentContext(profile, { headless: true, offline, args: ["--no-sandbox"], viewport: { width: 1440, height: 1000 } });
  // A new Chromium process, with offline set BEFORE its first app navigation.
  await context.setOffline(offline);
  await context.addInitScript(observeOriginalResponses);
  context.on("response", response => {
    const url = response.url(), local = parseUrl(url).origin === base;
    if (cold && local && response.status() < 400 && !response.fromServiceWorker())
      boundedPush(result.coldNetworkSuccesses, { path: parseUrl(url).pathname, status: response.status() });
    if (local && /\/rust-generated\/document_engine_bg\.wasm(?:\?|$)/.test(url)) {
      const task = response.body().then(bytes => {
        const digest = sha(bytes);
        if (digest !== result.source.publishedWasmSha256) boundedPush(result.failures, "Browser-loaded Rust WASM differs from publication identity");
        if (result.loadedWasm.length === 8) result.loadedWasm.shift();
        result.loadedWasm.push({ phase: cold ? "cold" : "online", sha256: digest, bytes: bytes.length, fromServiceWorker: response.fromServiceWorker() });
      }).catch(error => boundedPush(result.failures, "WASM identity observation: " + String(error).slice(0, 500)))
        .finally(() => bodyTasks.delete(task));
      bodyTasks.add(task);
    }
  });
  context.on("request", request => {
    const url = request.url();
    if (!/^(data:|blob:|about:)/.test(url) && parseUrl(url).origin !== base)
      boundedPush(result.externalRequests, url.slice(0, 500));
  });
  const page = await context.newPage();
  page.setDefaultTimeout(15000); page.setDefaultNavigationTimeout(45000);
  page.on("pageerror", error => boundedPush(result.errors, String(error).slice(0, 1500)));
  await page.goto(base, { waitUntil: "domcontentloaded" }); await H.ready(page);
  return page;
}
async function create(page, bytes) {
  const name = "KNU-Margins-" + Date.now() + "-" + result.phases.length;
  await reveal(page, page.locator("#new-project")); await page.locator("#new-project").fill(name);
  await page.getByRole("button", { name: "Create project", exact: true }).click();
  await $(page, "import-input").setInputFiles({ name: "main.tex", mimeType: "text/plain", buffer: bytes });
  await page.locator(".editor-toolbar .source-tab", { hasText: "main.tex" }).waitFor();
  await H.save(page); await H.assertBytes(page, name, bytes);
  return name;
}
async function closeProject(page) { await page.getByRole("button", { name: "Close", exact: true }).click(); await page.locator(".project-dashboard").waitFor(); }
async function select(page, value = ID) { await reveal(page, $(page, "analysis-profile")); await $(page, "analysis-profile").selectOption(value); }
async function analyze(page, version = "1.1.0") {
  const before = await page.evaluate(() => ({ count: window.__knuMarginsObserved.total,
    snapshotId: document.querySelector('[data-testid="knu-report"]')?.dataset.snapshotId }));
  await reveal(page, $(page, "analyze-now")); await $(page, "analyze-now").click();
  await page.waitForFunction(before => {
    const report = document.querySelector('[data-testid="knu-report"]');
    const state = window.__knuMarginsObserved;
    return document.querySelector('[data-testid="analysis-status"]')?.textContent === "Analysis complete." && state.total > before.count && report?.dataset.snapshotId !== before.snapshotId &&
      state.seen.some(entry => entry.snapshotId === report?.dataset.snapshotId);
  }, before, { timeout: 30000 });
  const raw = await page.evaluate(() => {
    const id = document.querySelector('[data-testid="knu-report"]').dataset.snapshotId;
    return window.__knuMarginsObserved.seen.findLast(entry => entry.snapshotId === id).raw;
  });
  const payload = parseJson(raw).payload;
  assert.deepEqual(payload.profile, { id: ID, version });
  assert.deepEqual(payload.knu.metadata, version === "1.1.0" ? latestMetadata : legacyMetadata);
  assert.deepEqual(payload.ruleResults.map(rule => rule.ruleId), version === "1.1.0" ? ["ENGINE-STRUCT-001", INDENT, HEADING, MARGINS] : ["ENGINE-STRUCT-001", INDENT, HEADING]);
  assert.equal(await $(page, "knu-rule").count(), version === "1.1.0" ? 3 : 2);
  assert.match(await $(page, "knu-edition").innerText(), new RegExp(version.replaceAll(".", "\\.")));
  return payload;
}
function marginRule(payload) { return payload.ruleResults.find(rule => rule.ruleId === MARGINS); }
async function legible(page) {
  await page.waitForFunction(() => {
    const editor = document.querySelector('[data-testid="editor"]');
    if (!editor) return false;
    const style = getComputedStyle(editor), box = editor.getBoundingClientRect();
    const transparent = style.color === "transparent" || /,\s*0\s*\)$/.test(style.color);
    if (box.width < 200 || box.height < 100 || parseFloat(style.fontSize) < 11 || style.visibility !== "visible") return false;
    if (!transparent) return true; // Supported native/plain-text fallback.
    const layer = editor.parentElement.querySelector('[data-testid="source-highlight-layer"]');
    const text = layer?.querySelector(".source-highlight-text");
    return layer?.dataset.ready === "true" && layer.dataset.aligned === "true" && layer.dataset.fileId === editor.dataset.fileId &&
      layer.dataset.revision === editor.dataset.highlightRevision && text?.textContent === editor.value &&
      getComputedStyle(text).visibility === "visible" && getComputedStyle(text).opacity !== "0";
  }, null, { timeout: 30000 });
  const display = await $(page, "editor").evaluate(editor => ({
    color: getComputedStyle(editor).color, caretColor: getComputedStyle(editor).caretColor,
    fontSize: getComputedStyle(editor).fontSize,
    highlightingReady: editor.parentElement.querySelector('[data-testid="source-highlight-layer"]')?.dataset.ready === "true",
  }));
  result.legibility ??= []; boundedPush(result.legibility, { phase: cold ? "cold" : "desktop", ...display }, 4);
}
async function finding(page, bytes) {
  const row = page.locator(`[data-testid="knu-finding"][data-rule-id="${MARGINS}"]`).first();
  await reveal(page, row); await row.waitFor();
  const start = Number(await row.getAttribute("data-start-byte")), end = Number(await row.getAttribute("data-end-byte"));
  const expected = "24.999 mm";
  assert.equal(start, bytes.indexOf(Buffer.from(expected))); assert.equal(end, start + Buffer.byteLength(expected));
  assert.equal(bytes.subarray(start, end).toString("utf8"), expected);
  const prefix = normalize(bytes.subarray(0, start));
  await row.locator('[data-testid="knu-finding-link"]').click();
  await page.waitForFunction(({ start, end, text }) => {
    const editor = document.querySelector('[data-testid="editor"]');
    return editor?.selectionStart === start && editor.selectionEnd === end && editor.value.slice(start, end) === text && document.activeElement === editor;
  }, { start: prefix.length, end: prefix.length + expected.length, text: expected });
  assert.notEqual(start, prefix.length, "byte and UTF16 offsets really differ for BOM/Ukrainian/emoji/CRLF");
  await legible(page);
}
async function negativeEvidence(page, payload) {
  assert.equal(marginRule(payload).outcome, "fail");
  const observations = payload.knu.files.flatMap(file => file.observations).filter(o => o.ruleId === MARGINS);
  const left = observations.find(o => o.value.side === "left");
  assert.deepEqual(left.value, { kind: "marginDeclaration", side: "left", length: { numeratorMm: 24999, denominator: 1000, unit: "mm" } });
  assert.equal(left.agrees, false);
  const rule = page.locator(`[data-testid="knu-rule"][data-rule-id="${MARGINS}"]`);
  assert.match(await rule.locator('[data-testid="knu-actual"]').first().innerText(), /Observed left margin: 24999\/1000 mm \(original unit mm\)/);
  assert.equal(await rule.locator('[data-testid="preview-fix"]').count(), 0);
  assert.equal(await $(page, "preview-fix").count(), 0);
}
async function screenshot(page, name) {
  const bytes = await page.screenshot({ fullPage: true, timeout: 15000 });
  assert.ok(bytes.length <= 4 * 1024 * 1024, "bounded screenshot"); fs.writeFileSync(path.join(output, name), bytes);
}
async function run() {
  let page, coldName, coldHead;
  await phase("published-identity-and-fresh-profile", async () => {
    latestMetadata = parseJson(fs.readFileSync(path.join(__dirname, "../src/Client/Analysis/knu-profile-v1_1-metadata.json")));
    legacyMetadata = parseJson(fs.readFileSync(path.join(__dirname, "../src/Client/Analysis/knu-profile-metadata.json")));
    result.source.metadataSha256 = sha(fs.readFileSync(path.join(__dirname, "../src/Client/Analysis/knu-profile-v1_1-metadata.json")));
    const wasm = await requestBytes(parseUrl("/rust-generated/document_engine_bg.wasm", upstream));
    assert.deepEqual([...wasm.subarray(0, 4)], [0, 97, 115, 109]);
    result.source.publishedWasmSha256 = sha(wasm);
    if (process.env.EXPECTED_RUST_WASM_SHA256) assert.equal(sha(wasm), process.env.EXPECTED_RUST_WASM_SHA256);
    const sw = await requestBytes(parseUrl("/service-worker.js", upstream));
    assert.match(sw.toString("utf8"), /service-worker-assets\.js/, "published SDK service worker, not a dev no-op");
    result.source.indexSha256 = sha(await requestBytes(parseUrl("/index.html", upstream)));
    result.source.serviceWorkerSha256 = sha(sw);
    result.source.assetManifestSha256 = sha(await requestBytes(parseUrl("/service-worker-assets.js", upstream)));
    await new Promise(resolve => proxy.listen(0, "127.0.0.1", resolve)); base = `http://127.0.0.1:${proxy.address().port}`;
    page = await launch(false);
  });
  await phase("primary-latest-worktype-positive-four-sides", async () => {
    const name = await create(page, positive);
    assert.equal(await $(page, "analysis-profile").inputValue(), "engine-structural", "engine default unchanged");
    const before = await H.head(page, name);
    await select(page);
    assert.equal(await $(page, "analysis-work-type").inputValue(), "unspecified");
    let payload = await analyze(page);
    assert.equal(marginRule(payload).outcome, "pass");
    assert.equal(payload.knu.files[0].observations.filter(o => o.ruleId === MARGINS && o.agrees === true).length, 4);
    assert.equal(await $(page, "knu-finding").count(), 0);
    for (const work of ["coursework", "bachelorQualification", "masterQualification", "unspecified"]) {
      await $(page, "analysis-work-type").selectOption(work); payload = await analyze(page);
      assert.equal(payload.configuration.workType, work); assert.equal(marginRule(payload).outcome, "pass");
      assert.equal(await $(page, "analysis-profile").inputValue(), ID);
    }
    // Adversarial INPUT only; no worker response or agreement is synthesized.
    await $(page, "analysis-work-type").evaluate(select => {
      select.add(new Option("Invalid test work type", "__invalid__")); select.value = "__invalid__";
      select.dispatchEvent(new Event("change", { bubbles: true }));
    });
    await page.waitForFunction(() => document.querySelector('[data-testid="analysis-status"]')?.textContent.startsWith("Analysis rejected the input:"));
    assert.equal(await $(page, "analysis-profile").inputValue(), ID, "invalid worktype cannot silently downgrade latest");
    assert.equal(await $(page, "knu-report").count(), 0);
    await $(page, "analysis-work-type").selectOption("coursework");
    payload = await analyze(page); assert.equal(payload.configuration.workType, "coursework");
    assert.deepEqual(await H.head(page, name), before, "analysis/worktype never edits or saves source bytes/head");
    await closeProject(page);
  });
  await phase("below-minimum-evidence-unicode-navigation-desktop", async () => {
    coldName = await create(page, negative); coldHead = await H.head(page, coldName);
    await select(page); const payload = await analyze(page);
    await negativeEvidence(page, payload); await finding(page, negative);
    assert.deepEqual(await H.head(page, coldName), coldHead);
    await screenshot(page, "knu-margins-desktop.png"); await closeProject(page);
  });
  for (const [name, bytes, required] of [["unknown-options", unknown, "unsupportedGeometry"], ["macro", macro, "unsupportedArgument"], ["missing-side", missing, "missingMarginSides"]]) {
    await phase(name + "-indeterminate-without-guesses", async () => {
      const project = await create(page, bytes), before = await H.head(page, project);
      await select(page); const payload = await analyze(page);
      assert.equal(marginRule(payload).outcome, "indeterminate");
      assert.ok(payload.knu.files[0].limitations.includes(required));
      if (name === "macro") assert.equal(payload.knu.files[0].observations.filter(o => o.ruleId === MARGINS).length, 0);
      if (name === "missing-side") assert.equal(payload.knu.files[0].observations.filter(o => o.ruleId === MARGINS).length, 1);
      assert.equal(await $(page, "preview-fix").count(), 0);
      assert.deepEqual(await H.head(page, project), before); await closeProject(page);
    });
  }
  await phase("existing-indent-preview-accept-undo-and-version-stale-clearing", async () => {
    const project = await create(page, fix), before = await H.head(page, project);
    await select(page); let payload = await analyze(page);
    assert.equal(marginRule(payload).outcome, "indeterminate");
    assert.equal(payload.knu.fixDescriptors.length, 1); assert.equal(payload.knu.fixDescriptors[0].ruleId, INDENT);
    assert.equal(await page.locator(`[data-rule-id="${MARGINS}"] [data-testid="preview-fix"]`).count(), 0);
    await H.preview(page);
    assert.equal((await $(page, "review-expected").innerText()).trim(), "10 mm");
    assert.equal((await $(page, "review-replacement").innerText()).trim(), "1.27cm");
    assert.deepEqual(await H.head(page, project), before, "preview is inert");
    await H.accept(page); await H.assertBytes(page, project, fixed);
    await H.undo(page); await H.assertBytes(page, project, fix);
    payload = await analyze(page); const staleSnapshot = payload.snapshotId, previewId = await H.preview(page);
    await select(page, LEGACY);
    await page.locator(`[data-testid="review-preview"][data-preview-id="${previewId}"]`).waitFor({ state: "detached" });
    await page.waitForFunction(id => !document.querySelector(`[data-testid="knu-report"][data-snapshot-id="${id}"]`), staleSnapshot);
    assert.equal(await $(page, "nav-notice").count(), 0);
    assert.equal(await $(page, "analysis-profile").inputValue(), LEGACY);
    await analyze(page, "1.0.0");
    await $(page, "analysis-work-type").selectOption("masterQualification");
    const legacy = await analyze(page, "1.0.0"); assert.equal(legacy.configuration.workType, "masterQualification");
    assert.equal(await $(page, "analysis-profile").inputValue(), LEGACY, "legacy remains visibly selected after worktype change");
    await select(page, "engine-structural"); await $(page, "knu-report").waitFor({ state: "detached" });
    assert.equal(await $(page, "review-preview").count(), 0);
    await select(page); await analyze(page); await H.assertBytes(page, project, fix);
    await closeProject(page);
  });
  await phase("provision-published-shell-and-close-entire-Chromium", async () => {
    await page.waitForFunction(() => window.__oopOfflineReady === true && !!navigator.serviceWorker.controller, null, { timeout: 45000 });
    await Promise.all(bodyTasks);
    assert.ok(result.loadedWasm.some(entry => entry.phase === "online"), "observe actual browser-loaded Rust WASM identity");
    assert.ok(result.loadedWasm.every(entry => entry.sha256 === result.source.publishedWasmSha256));
    assert.deepEqual(result.externalRequests, []); assert.deepEqual(result.errors, []);
    await context.close(); context = null;
    await stopProxy();
    const refused = await new Promise((resolve, reject) => {
      const req = http.get(base, response => { response.resume(); reject(new Error("stopped facing proxy still accepts connections")); });
      req.setTimeout(1500, () => req.destroy(new Error("refusal check timed out")));
      req.on("error", error => error.code === "ECONNREFUSED" ? resolve(error.code) : reject(error));
    });
    result.coldTeardown = { entirePersistentBrowserClosed: true, actualFacingProxyRefusal: refused };
  });
  await phase("cold-offline-same-profile-new-worker-evidence-and-navigation", async () => {
    cold = true; page = await launch(true);
    await H.open(page, coldName);
    assert.equal(await $(page, "editor").inputValue(), normalize(negative));
    const before = await H.head(page, coldName); assert.deepEqual(before, coldHead);
    await select(page); const payload = await analyze(page);
    await negativeEvidence(page, payload); await finding(page, negative);
    assert.deepEqual(await H.head(page, coldName), before, "cold analysis/navigation preserve exact BOM/CRLF bytes and head");
    await H.assertBytes(page, coldName, negative);
    await screenshot(page, "knu-margins-cold.png");
    await Promise.all(bodyTasks);
    assert.ok(result.loadedWasm.some(entry => entry.phase === "cold" && entry.fromServiceWorker), "fresh offline worker loads WASM from provisioned service worker");
    assert.ok(result.loadedWasm.every(entry => entry.sha256 === result.source.publishedWasmSha256));
    assert.deepEqual(result.coldNetworkSuccesses, []); assert.deepEqual(result.externalRequests, []); assert.deepEqual(result.errors, []); assert.deepEqual(result.failures, []);
    result.coldFinding = { profile: payload.profile, snapshotId: payload.snapshotId, ruleId: MARGINS, wasmSha256: result.source.publishedWasmSha256 };
  });
}
// Finite fail-closed watchdog, not a sleep or success condition. All ordinary
// waits above use actual UI/worker/service-worker state, not elapsed delays.
const watchdog = setTimeout(() => {
  result.status = "failed"; boundedPush(result.failures, "whole-script 300s deadline"); writeResult();
  proxy.closeAllConnections(); context?.close().catch(() => {});
  setTimeout(() => process.exit(1), 1000);
}, 300000);
(async () => {
  try { await run(); result.status = "passed"; }
  catch (error) {
    result.status = "failed"; if (activePhase) activePhase.status = "failed";
    boundedPush(result.failures, String(error.stack || error).slice(0, 4000)); process.exitCode = 1;
  } finally {
    if (context) await context.close().catch(error => boundedPush(result.failures, "browser close: " + String(error).slice(0, 500)));
    await stopProxy().catch(error => boundedPush(result.failures, "proxy close: " + String(error).slice(0, 500)));
    // Only this script's freshly created synthetic browser profile is removed.
    fs.rmSync(profile, { recursive: true, force: true });
    if (result.failures.length) { result.status = "failed"; process.exitCode = 1; }
    writeResult(); clearTimeout(watchdog);
    console.log(JSON.stringify({ status: result.status, phases: result.phases.map(p => ({ name: p.name, status: p.status })), result: path.join(output, "knu-margins-result.json") }));
  }
})();
