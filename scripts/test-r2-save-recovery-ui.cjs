// R2 A09: real quota + normal cooperative handover + LABELED native revocation fault.
// Root execution only. No product API patching, fake failures or storage writes.
"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const http = require("node:http");
const { createHash } = require("node:crypto");
const H = require("./review-ui-helpers.cjs");
const { buildZip } = require("./zip-util.cjs");
const { validate: validateCsp } = require("./test-csp.cjs");
const sha = b => createHash("sha256").update(b).digest("hex");
const required = key => { assert(process.env[key], "Required environment: " + key); return process.env[key]; };
const out = fs.realpathSync(required("RESULT_DIR"));
assert.equal(fs.readdirSync(out).length, 0, "RESULT_DIR must exist, be new/empty and exclusive");
fs.writeFileSync(path.join(out, "exclusive.json"), JSON.stringify({ pid: process.pid, started: new Date().toISOString() }), { flag: "wx" });
const receiptPath = path.join(out, "save-recovery-receipt.json");
const record = { schemaVersion: 1, workPackage: "R2-U-save-recovery", result: "FAIL", step: "setup", activeTab: null,
  harnessSha256: sha(fs.readFileSync(__filename)), runtime: { node: process.version, platform: process.platform },
  publication: null, quota: [], phases: {}, attempts: [], downloads: [], tabs: [], pageErrors: [], externalRequests: [], failure: null,
  limitations: ["CONTROLLED REAL BROWSER QUOTA override in a NEW synthetic profile; NOT actual disk exhaustion.",
    "No RAM-only unsaved text is claimed to survive browser death. Export/retry occurs BEFORE closing tabs.",
    "Held I/O, disposal, permission denial, eviction, corrupt-head recovery, other browsers/devices and full A09/R2 remain separate.",
    "Native steal is browser FAULT INJECTION in a test-owned text page only, never product takeover or normal-exclusivity proof.",
    "Prior saved objects are read for verification only; all working-byte backup proof comes from real Download controls."] };
let root, origin, port, server, context, browser, active, nativeFaultPage, workflowComplete = false, ownedServer = false;
const sockets = new Set(), ids = new WeakMap(), quotaSessions = new Map();
function flush() { fs.writeFileSync(receiptPath, JSON.stringify(record, null, 2) + "\n"); }
function step(text, page = active) { active = page; record.step = text; record.activeTab = page ? ids.get(page) : null; flush(); }
function attach(page) {
  if (ids.has(page)) return;
  const id = "tab-" + (record.tabs.length + 1); ids.set(page, id); record.tabs.push({ id, closed: false }); page.setDefaultTimeout(30000);
  page.on("close", () => { record.tabs.find(t => t.id === id).closed = true; });
  page.on("pageerror", e => { record.pageErrors.push({ tab: id, message: String(e).slice(0, 2000) }); });
}
function url(raw) { try { return new URL(raw); } catch { return null; } }
function external(raw) { const u = url(raw); return /^https?:/.test(raw) && (!u || u.origin !== origin); }
async function start() {
  const mime = { ".html": "text/html", ".js": "text/javascript", ".json": "application/json", ".css": "text/css", ".wasm": "application/wasm", ".svg": "image/svg+xml" };
  server = http.createServer((req, res) => {
    let file;
    try {
      const pathname = decodeURIComponent(new URL(req.url, origin).pathname);
      file = fs.realpathSync(path.resolve(root, "." + (pathname === "/" ? "/index.html" : pathname)));
      if (!file.startsWith(root + path.sep) || !fs.statSync(file).isFile()) { res.writeHead(403).end(); return; }
    } catch { res.writeHead(404).end(); return; }
    fs.readFile(file, (error, bytes) => {
      if (error) { res.writeHead(404).end(); return; }
      res.writeHead(200, { "Content-Type": mime[path.extname(file)] || "application/octet-stream", "Content-Security-Policy": record.publication.csp, "X-Content-Type-Options": "nosniff", "Cache-Control": "no-cache" }); res.end(bytes);
    });
  });
  server.on("connection", s => { sockets.add(s); s.on("close", () => sockets.delete(s)); });
  await new Promise((resolve, reject) => { server.once("error", reject); server.listen(port, "127.0.0.1", () => { ownedServer = true; resolve(); }); });
}
async function stop() {
  if (!ownedServer) return; ownedServer = false; const old = server; server = null;
  for (const socket of sockets) socket.destroy(); await new Promise((resolve, reject) => old.close(e => e ? reject(e) : resolve()));
}
async function open(offline) {
  const { chromium } = require("playwright");
  context = await chromium.launchPersistentContext(path.join(out, "profile"), { headless: true, offline, acceptDownloads: true });
  browser = context.browser(); record.runtime.browser = browser?.version();
  try { record.runtime.playwright = require("playwright/package.json").version; } catch { record.runtime.playwright = "version unavailable"; }
  assert.equal(record.runtime.playwright, "1.56.1", "Root must use the accepted cached Playwright1.56.1");
  context.on("page", attach); for (const page of context.pages()) attach(page);
  context.on("request", r => { if (external(r.url())) { const u = url(r.url()); record.externalRequests.push({ url: u ? u.origin + u.pathname : "invalid URL", method: r.method() }); } });
  await context.route("**/*", route => external(route.request().url()) ? route.abort("blockedbyclient") : route.continue());
  const page = await context.newPage(); attach(page); step(offline ? "cold offline BEFORE navigation" : "online navigation", page);
  await page.goto(origin + "/"); await H.ready(page); return page;
}
async function closeBrowser() {
  const pages = context.pages(), b = browser; await context.close(); context = null;
  if (b?.isConnected()) await b.close(); assert(pages.every(p => p.isClosed())); assert(!b || !b.isConnected()); browser = null; quotaSessions.clear();
  return { tabsClosed: pages.length, browserDisconnected: true };
}
const $ = H.$, BOM = Buffer.from([239, 187, 191]), main = "нотатки/джерело.tex", asset = "untouched.bin";
const original = Buffer.concat([BOM, Buffer.from("% Україна 🙂 $1 \\literal\r\n\\documentclass{article}\r\n\\begin{document}\r\nSaved source A.\r\n\\end{document}\r\n")]);
const binary = Buffer.from([255, 0, 13, 10, 128, 239, 187, 191, 73]);
const normalize = text => text.replace(/\r\n?/g, "\n");
const displayed = bytes => normalize(bytes.toString("utf8").replace(/^\uFEFF/, ""));
const editedBytes = text => Buffer.concat([BOM, Buffer.from(normalize(text))]);
const expansion = Array.from({ length: 4096 }, (_, i) => "% " + sha(Buffer.from("synthetic-quota-expansion-" + i)) + "\n").join("");
const bigText = displayed(original) + "% quota working copy Україна 🙂 $1 \\literal\n" + expansion;
const bigBytes = editedBytes(bigText);
const writerText = displayed(original) + "% distinct new writer revision C — Київ 🙂\n";
const writerBytes = editedBytes(writerText);
const entries = [[main, original], [asset, binary]];
function brief(snapshot) { return { project: snapshot.project, revision: snapshot.revision, files: snapshot.files.map(f => ({ id: f.id, path: f.path, mediaKind: f.mediaKind, hasBom: f.hasBom, length: f.bytes.length, sha256: sha(Buffer.from(f.bytes)) })) }; }
// READ ONLY durable verification, using the same registered storage module as
// existing acceptance helpers. Never use this as the unsaved-text backup.
async function snapshot(page, name, revision = null) {
  return page.evaluate(async ({ name, revision }) => {
    const m = await import("./js/workspace-storage.js"), matches = (await m.listProjects()).filter(p => p.name === name);
    if (matches.length !== 1) throw new Error("Expected one synthetic project: " + name);
    const p = matches[0], r = await m.getRevision(revision || p.headRevisionId);
    return { project: { id: p.id, name: p.name, head: p.headRevisionId, schemaVersion: p.schemaVersion }, revision: { id: r.id, parent: r.parentRevisionId, projectId: r.projectId }, files: await Promise.all(r.files.map(async f => ({ id: f.id, path: f.relativePath, mediaKind: f.mediaKind, hasBom: f.hasBom, bytes: Array.from(await m.readContent(f.contentHash)) }))) };
  }, { name, revision });
}
function exact(s, textBytes) {
  assert.equal(s.files.length, 2); assert.deepEqual(Buffer.from(s.files.find(f => f.path === main)?.bytes || []), textBytes); assert.deepEqual(Buffer.from(s.files.find(f => f.path === asset)?.bytes || []), binary);
}
async function state(page) {
  return page.evaluate(() => {
    const e = document.querySelector("[data-testid=editor]"), a = document.querySelector("[data-testid=analysis-panel]"), save = document.querySelector("[data-testid=save]");
    return { status: document.querySelector(".workspace-header .status")?.textContent, banner: document.querySelector(".banner-readonly")?.textContent, file: e?.dataset.fileId, value: e?.value, readonly: !!e && (e.disabled || e.readOnly), saveDisabled: save?.disabled, generation: a?.dataset.generation, session: a?.dataset.session, head: a?.dataset.head };
  });
}
function stateBrief(s) { const { value, ...rest } = s; return { ...rest, textareaUtf16Units: value?.length, textareaUtf8Sha256: value === undefined ? null : sha(Buffer.from(value)) }; }
async function choose(page, name, expected) {
  active = page; const s = await snapshot(page, name), id = s.files.find(f => f.path === main)?.id; assert(id);
  const target = $(page, "select-file").filter({ has: page.locator(".file-name", { hasText: main }) }); assert.equal(await target.count(), 1); await target.click();
  await page.waitForFunction(({ id, text }) => { const e = document.querySelector("[data-testid=editor]"); return e?.dataset.fileId === id && e.value === text; }, { id, text: displayed(expected) }); return id;
}
async function settled(page) {
  await page.waitForFunction(() => navigator.serviceWorker.controller !== null && window.__oopOfflineReady === true, null, { timeout: 240000 });
  await page.waitForFunction(() => document.querySelector("[data-testid=compiler-readiness]")?.textContent.includes("compilation available."), null, { timeout: 240000 });
  await page.waitForLoadState("networkidle"); // genuine publication/provisioning activity, not a model-edit fence
}
async function importProject(page, name) {
  const close = page.getByRole("button", { name: "Close", exact: true });
  if (await close.count()) { await close.click(); await page.locator(".project-dashboard").waitFor(); }
  await H.reveal(page, $(page, "import-zip")); await $(page, "import-zip").setInputFiles({ name: "synthetic-quota.zip", mimeType: "application/zip", buffer: buildZip(entries) });
  await H.reveal(page, $(page, "import-preview")); await $(page, "staged-name").fill(name); await $(page, "publish-import").click(); await $(page, "select-file").first().waitFor({ timeout: 180000 });
  await settled(page); await choose(page, name, original); const s = await snapshot(page, name); exact(s, original); assert.equal((await state(page)).status, "Saved."); return s;
}
async function cdp(page) {
  if (!quotaSessions.has(page)) quotaSessions.set(page, await context.newCDPSession(page)); return quotaSessions.get(page);
}
async function quota(page, label, size) {
  const session = await cdp(page), input = size === undefined ? { origin } : { origin, quotaSize: size };
  const item = { label, tab: ids.get(page), command: "Storage.overrideQuotaForOrigin", input, before: await session.send("Storage.getUsageAndQuota", { origin }) }; record.quota.push(item); flush();
  await session.send("Storage.overrideQuotaForOrigin", input); item.after = await session.send("Storage.getUsageAndQuota", { origin }); flush();
  assert.equal(item.after.overrideActive, size !== undefined, "Native quota override/reset was not observed"); return item;
}
async function quotaLimit(page, label) {
  await settled(page); const session = await cdp(page), observed = await session.send("Storage.getUsageAndQuota", { origin });
  assert(Number.isFinite(observed.usage) && observed.usage >= 0);
  const limit = await quota(page, label, Math.ceil(observed.usage) + 1024);
  limit.calculationUsage = observed; limit.additionalBudgetBytes = 1024; flush(); return limit;
}
async function fillOnce(page, name, expectedBefore, text, label) {
  const id = await choose(page, name, expectedBefore), before = await state(page); assert.equal(before.readonly, false);
  const attempt = { label, tab: ids.get(page), before: stateBrief(before), intendedUtf16Units: text.length, intendedSavedSha256: sha(editedBytes(text)), inputCount: 1 }; record.attempts.push(attempt); flush();
  await $(page, "editor").fill(text); // ONE input, after exact file/full-value render fence
  await page.waitForFunction(({ id, text, generation, session }) => {
    const e = document.querySelector("[data-testid=editor]"), a = document.querySelector("[data-testid=analysis-panel]");
    return e?.dataset.fileId === id && e.value === text && a?.dataset.session === session && Number(a?.dataset.generation) > Number(generation);
  }, { id, text, generation: before.generation, session: before.session });
  attempt.modelAcknowledged = stateBrief(await state(page)); flush(); return attempt;
}
async function failedSave(page, name, A, label) {
  const initial = await state(page);
  const failureAttempt = { label, tab: ids.get(page), before: stateBrief(initial), explicitSaveClicks: 0 };
  record.attempts.push(failureAttempt); flush();
  if (initial.status === "Unsaved changes." && !initial.saveDisabled) {
    // Genuine explicit Save cancels the queued 2s autosave in Workspace.SaveAsync.
    // If that autosave already started/failed, observe it; NEVER repeat the failure.
    failureAttempt.explicitSaveClicks = 1; failureAttempt.kind = "explicit Save (cancels deferred autosave)"; flush();
    await $(page, "save").click();
  } else failureAttempt.kind = "observe already-started/completed native autosave, no extra Save";
  await page.waitForFunction(() => {
    const status = document.querySelector(".workspace-header .status")?.textContent || "", save = document.querySelector("[data-testid=save]");
    return status.startsWith("Save failed:") || status === "Saved." && save?.disabled;
  }, null, { timeout: 30000 }); // actual Save/autosave result, no synthetic rejection
  const s = await state(page), durable = await snapshot(page, name);
  if (s.status === "Saved." || durable.revision.id !== A.revision.id) {
    const e = new Error("QUOTA_OVERRIDE_NOT_ENFORCED_FOR_STORAGE_CLIENT: source unexpectedly saved; no quota-failure PASS is possible"); e.code = "QUOTA_OVERRIDE_NOT_ENFORCED"; throw e;
  }
  assert.match(s.status, /^Save failed:.*(storage is full|quota)/i, "Require actual quota-specific native failure, not a fabricated/other error");
  exact(durable, original); assert.equal(s.value, bigText); assert.equal(s.readonly, false); assert.equal(s.saveDisabled, false); assert.equal(s.head, A.revision.id);
  const usage = await (await cdp(page)).send("Storage.getUsageAndQuota", { origin }); assert.equal(usage.overrideActive, true);
  const result = { label, saveAttempt: failureAttempt, state: stateBrief(s), durable: brief(durable), nativeUsage: usage, actualQuotaFailure: true, noHeadAdvance: true, retainedDirtyText: true }; flush(); return result;
}
async function download(page, expected, label) {
  active = page; await H.reveal(page, $(page, "download"));
  const [d] = await Promise.all([page.waitForEvent("download"), $(page, "download").click()]);
  const file = path.join(out, label + ".tex"); await d.saveAs(file); assert.equal(await d.failure(), null); const bytes = fs.readFileSync(file); assert.deepEqual(bytes, expected, "Real Download must contain exact current working bytes, not old saved content");
  const result = { label, tab: ids.get(page), suggestedFilename: d.suggestedFilename(), file: path.basename(file), length: bytes.length, sha256: sha(bytes), exact: true }; record.downloads.push(result); flush(); return result;
}
async function explicitSave(page, name, expected, parent, label) {
  const before = await snapshot(page, name), s = await state(page); assert.equal(before.revision.id, parent);
  assert.equal(s.saveDisabled, false, "No automatic success may be relabeled an explicit retry");
  const attempt = { label, explicitSaveClicks: 1, before: stateBrief(s), parent, clickedAt: new Date().toISOString() }; record.attempts.push(attempt); flush();
  await $(page, "save").click();
  await page.waitForFunction(() => document.querySelector(".workspace-header .status")?.textContent === "Saved." && document.querySelector("[data-testid=save]")?.disabled === true);
  const after = await snapshot(page, name); exact(after, expected); assert.notEqual(after.revision.id, parent); assert.equal(after.revision.parent, parent); assert.equal(after.project.id, before.project.id); assert.equal(after.project.name, name);
  const current = await state(page); assert.equal(current.value, displayed(expected)); assert.equal(current.generation, s.generation); assert.equal(current.head, after.revision.id);
  attempt.after = brief(after); attempt.state = stateBrief(current); flush(); return after;
}
async function quotaRecovery(page) {
  const name = "A09 quota retry " + Date.now(); step("quota: import/save exact A and binary", page); const A = await importProject(page, name);
  record.phases.quota = { project: name, A: brief(A) }; flush(); step("quota: native usage+1KiB cap, ONE expanded edit, actual Save/autosave failure", page);
  record.phases.quota.limit = await quotaLimit(page, "ordinary-edit-quota"); await fillOnce(page, name, original, bigText, "ordinary-edit-under-native-quota");
  record.phases.quota.failure = await failedSave(page, name, A, "ordinary-save-or-autosave");
  await page.screenshot({ path: path.join(out, "ordinary-quota-failure.png"), fullPage: true });
  step("quota: real unsaved source Download without saving", page); record.phases.quota.backup = await download(page, bigBytes, "quota-unsaved-B");
  assert.deepEqual(brief(await snapshot(page, name)), brief(A)); assert.match((await state(page)).status, /^Save failed:/);
  step("quota: reset native quota, ONE explicit Save retry", page); record.phases.quota.reset = await quota(page, "reset-before-explicit-retry");
  assert.deepEqual(brief(await snapshot(page, name)), brief(A)); assert.match((await state(page)).status, /^Save failed:/, "Reset must not silently retry");
  const B = await explicitSave(page, name, bigBytes, A.revision.id, "one-explicit-quota-retry");
  const old = await snapshot(page, name, A.revision.id); exact(old, original); assert.equal(old.revision.id, A.revision.id);
  record.phases.quota.B = brief(B); record.phases.quota.AStillReadable = brief(old); await download(page, bigBytes, "quota-saved-B"); flush(); return { name, A, B };
}
async function lockSnapshot(page) { return page.evaluate(() => navigator.locks.query()); }
async function waitAcquired(page, prior, expected) {
  await page.waitForFunction(({ file, session, text }) => { const e = document.querySelector('[data-testid=editor]'), a = document.querySelector('[data-testid=analysis-panel]'); return e?.dataset.fileId === file && e.value === text && !e.disabled && !e.readOnly && a?.dataset.session !== session; }, { file: prior.file, session: prior.session, text: displayed(expected) });
}
async function cooperativeTakeover(page, q) {
  step('cooperative takeover: clean exported/retried/saved writer holds; visible reader action MUST wait', page);
  const reader = await context.newPage(); attach(reader); await reader.goto(origin + '/'); await H.ready(reader); await H.open(reader, q.name); await choose(reader, q.name, bigBytes);
  const prior = await state(reader), lockName = 'oop-project:' + q.B.project.id; assert.equal(prior.readonly, true);
  const before = await lockSnapshot(reader); assert.equal(before.held.filter(l => l.name === lockName).length, 1);
  const evidence = { scope: 'NORMAL COOPERATIVE PRODUCT TAKEOVER, not revocation', lockName, before, readerBefore: stateBrief(prior) }; record.phases.cooperativeTakeover = evidence; flush();
  await $(reader, 'takeover').click();
  await reader.waitForFunction(async name => (await navigator.locks.query()).pending.some(l => l.name === name && l.mode === 'exclusive'), lockName);
  evidence.waiting = await lockSnapshot(reader); assert.equal((await state(reader)).readonly, true); assert.equal((await state(page)).readonly, false);
  assert.deepEqual(brief(await snapshot(reader, q.name)), brief(q.B));
  // Saved/exported writer releases through the real clean Close control ONLY.
  await page.getByRole('button', { name: 'Close', exact: true }).click(); await page.locator('.project-dashboard').waitFor();
  await waitAcquired(reader, prior, bigBytes); evidence.granted = await lockSnapshot(reader);
  const held = evidence.granted.held.filter(l => l.name === lockName); assert.equal(held.length, 1);
  const pendingClient = evidence.waiting.pending.find(l => l.name === lockName)?.clientId;
  assert(pendingClient); assert.equal(held[0].clientId, pendingClient); assert.notEqual(held[0].clientId, before.held.find(l => l.name === lockName).clientId);
  assert.deepEqual(brief(await snapshot(reader, q.name)), brief(q.B)); await download(reader, bigBytes, 'cooperative-new-owner-saved-B');
  await reader.getByRole('button', { name: 'Close', exact: true }).click(); await reader.locator('.project-dashboard').waitFor();
  evidence.released = await lockSnapshot(reader); assert(!evidence.released.held.some(l => l.name === lockName)); evidence.pass = true; flush(); await reader.close(); active = page;
}
async function releaseNativeFault() {
  if (!nativeFaultPage || nativeFaultPage.isClosed()) return;
  const released = await nativeFaultPage.evaluate(async () => {
    const s = window.__a09NativeFault; if (!s) return null;
    if (!s.observedGrant) return { name: s.name, released: false, neverGranted: true, error: s.error || null, after: await navigator.locks.query() };
    s.release(); await s.completion; return { name: s.name, released: true, after: await navigator.locks.query() };
  });
  if (record.phases.writerLoss) record.phases.writerLoss.nativeRelease = released;
  if (released?.released) assert(!released.after.held.some(l => l.name === released.name)); flush();
  await nativeFaultPage.close(); nativeFaultPage = null;
}
async function writerLoss(page) {
  const name = 'A09 writer loss fault ' + Date.now(); step('native revocation FAULT: separate saved project, real settled quota failure', page); const A = await importProject(page, name);
  record.phases.writerLoss = { scope: 'CONTROLLED REAL NATIVE OWNERSHIP-REVOCATION FAULT INJECTION; NOT product takeover semantics or normal-exclusivity proof', project: name, A: brief(A), noInFlightRepositoryCommit: true }; flush(); await quotaLimit(page, 'writer-loss-quota');
  await fillOnce(page, name, original, bigText, 'old-writer-under-native-quota'); record.phases.writerLoss.failure = await failedSave(page, name, A, 'old-writer-save-or-autosave');
  await page.screenshot({ path: path.join(out, 'writer-quota-failure.png'), fullPage: true }); await download(page, bigBytes, 'old-writer-before-revocation');
  const lockName = 'oop-project:' + A.project.id, oldState = await state(page); record.phases.writerLoss.beforeNativeFault = await lockSnapshot(page);
  assert.equal(record.phases.writerLoss.beforeNativeFault.held.filter(l => l.name === lockName).length, 1);
  nativeFaultPage = await context.newPage(); attach(nativeFaultPage); step('test-owned native lease revokes old ownership AFTER settled save failure; original RAM/tab stays alive', nativeFaultPage);
  // Same-origin text document from PUBLISHED_WWWROOT, NOT an app/project tab.
  await nativeFaultPage.goto(origin + '/service-worker-assets.js');
  await nativeFaultPage.evaluate(name => {
    let release; const held = new Promise(r => { release = r; });
    const s = { name, input: { mode: 'exclusive', steal: true }, release, completion: null }; window.__a09NativeFault = s;
    s.completion = navigator.locks.request(name, s.input, async lock => { s.observedGrant = { name: lock.name, mode: lock.mode, snapshot: await navigator.locks.query() }; await held; });
    s.completion.catch(error => { s.error = String(error); });
  }, lockName);
  await nativeFaultPage.waitForFunction(() => window.__a09NativeFault?.observedGrant || window.__a09NativeFault?.error, null, { timeout: 15000 });
  const grant = await nativeFaultPage.evaluate(() => { const s = window.__a09NativeFault; if (s.error) throw new Error(s.error); return { input: s.input, grant: s.observedGrant }; });
  record.phases.writerLoss.nativeGrant = grant; const oldClient = record.phases.writerLoss.beforeNativeFault.held.find(l => l.name === lockName).clientId;
  const faultClient = grant.grant.snapshot.held.find(l => l.name === lockName)?.clientId; assert(faultClient); assert.notEqual(faultClient, oldClient); flush();
  step('revoked dirty writer: ONE Save ownership check, no source discard; dirty takeover must be disabled', page);
  assert.equal((await state(page)).value, bigText); await $(page, 'save').click();
  await page.waitForFunction(() => { const e = document.querySelector('[data-testid=editor]'); return e && (e.disabled || e.readOnly) && document.querySelector('[data-testid=save]')?.disabled; });
  const lost = await state(page); assert.equal(lost.value, bigText); assert.equal(lost.generation, oldState.generation); assert.equal(lost.session, oldState.session); assert.equal(lost.head, A.revision.id);
  assert.match(lost.banner || '', /unsaved|working|download/i); assert(await $(page, 'takeover').isDisabled(), 'Dirty readonly takeover must not reload/drop retained RAM');
  exact(await snapshot(page, name), original); await download(page, bigBytes, 'old-writer-revoked-retained-B'); record.phases.writerLoss.oldWriterAfter = stateBrief(lost);
  // Clean real reader opens while the TEST lease is held, so its normal
  // takeover action is available but NOT invoked until native fault release.
  const reader = await context.newPage(); attach(reader); await reader.goto(origin + '/'); await H.ready(reader); await H.open(reader, name); await choose(reader, name, original);
  const prior = await state(reader); assert.equal(prior.readonly, true); record.phases.writerLoss.readerBefore = stateBrief(prior);
  await releaseNativeFault(); step('test lease RELEASED; clean app tab acquires via ordinary visible cooperative Take over editing', reader);
  await $(reader, 'takeover').click(); await waitAcquired(reader, prior, original); record.phases.writerLoss.realWriterGrant = await lockSnapshot(reader);
  assert.equal(record.phases.writerLoss.realWriterGrant.held.filter(l => l.name === lockName).length, 1); await quota(reader, 'reset-before-new-writer-save');
  await fillOnce(reader, name, original, writerText, 'new-writer-distinct-C'); const C = await explicitSave(reader, name, writerBytes, A.revision.id, 'new-writer-save-C');
  step('old revoked writer cannot overwrite new C; exact dirty B still downloadable, never reload old writer', page);
  const finalOld = await state(page); assert.equal(finalOld.value, bigText); assert.equal(finalOld.readonly, true); assert.equal(finalOld.saveDisabled, true); assert(await $(page, 'takeover').isDisabled());
  assert.deepEqual(brief(await snapshot(page, name)), brief(C)); await download(page, bigBytes, 'old-writer-after-new-C-retained-B');
  assert.deepEqual(brief(await snapshot(reader, name)), brief(C)); exact(await snapshot(reader, name, A.revision.id), original);
  record.phases.writerLoss.C = brief(C); record.phases.writerLoss.unsavedBIsDownloadOnly = true; flush(); return { name, A, C, reader };
}
async function captureFailure() {
  record.failureTabs ||= [];
  for (const page of context?.pages() || []) {
    if (page.isClosed()) continue; const id = ids.get(page); const s = await state(page).catch(e => ({ unavailable: String(e) }));
    if (s.value !== undefined) fs.writeFileSync(path.join(out, "failure-" + id + "-textarea.txt"), s.value);
    const body = await page.locator("body").innerText().catch(() => "unavailable"); record.failureTabs.push({ id, active: page === active, url: page.url(), state: stateBrief(s), body: body.slice(0, 45000), exactTextareaFile: "failure-" + id + "-textarea.txt" });
    await page.screenshot({ path: path.join(out, "failure-" + id + ".png"), fullPage: true }).catch(() => {});
  }
}
(async () => {
  flush();
  try {
    root = fs.realpathSync(required("PUBLISHED_WWWROOT")); assert(root !== out && !out.startsWith(root + path.sep));
    const expectedSdk = required("EXPECTED_SDK_VERSION"), source = required("CANDIDATE_SOURCE_DIGEST"); assert.match(source, /^[0-9a-f]{64}$/);
    port = Number(process.env.TEST_PORT || 8790); assert(Number.isInteger(port) && port > 1024 && port < 65536); origin = "http://127.0.0.1:" + port;
    const headers = fs.readFileSync(path.join(__dirname, "../docs/examples/static-csp-headers.conf"), "utf8"); validateCsp(fs.readFileSync(path.join(root, "index.html"), "utf8"), headers);
    const csp = headers.split("\n").map(x => x.trim()).find(x => x.startsWith("Content-Security-Policy:")).slice("Content-Security-Policy:".length).trim();
    const raw = fs.readFileSync(path.join(root, "service-worker-assets.js")), manifest = JSON.parse(raw.toString().match(/self\.assetsManifest\s*=\s*(\{[\s\S]*\})\s*;/)[1]); assert.equal(manifest.version, expectedSdk);
    const seen = new Set(); for (const a of manifest.assets) { assert(!path.isAbsolute(a.url) && !a.url.split("/").includes("..") && !seen.has(a.url)); seen.add(a.url); const file = fs.realpathSync(path.resolve(root, a.url)); assert(file.startsWith(root + path.sep)); assert.equal("sha256-" + createHash("sha256").update(fs.readFileSync(file)).digest("base64"), a.hash, a.url); }
    fs.writeFileSync(path.join(out, "verified-assets-manifest.json"), JSON.stringify(manifest, null, 2) + "\n");
    fs.writeFileSync(path.join(out, "synthetic-fixture.zip"), buildZip(entries));
    record.publication = { verifiedManifestFile: "verified-assets-manifest.json", sdkVersion: manifest.version, manifestSha256: sha(raw), candidateSourceDigest: source, assetsVerified: manifest.assets.length, csp, cspSha256: sha(headers), origin };
    record.tooling = Object.fromEntries(["review-ui-helpers.cjs", "zip-util.cjs", "test-csp.cjs"].map(f => [f, sha(fs.readFileSync(path.join(__dirname, f)))]));
    record.fixture = { originalSha256: sha(original), binarySha256: sha(binary), expandedEditedSha256: sha(bigBytes), expandedEditedLength: bigBytes.length, newWriterSha256: sha(writerBytes), zipSha256: sha(buildZip(entries)), nativeQuotaAdditionalBudget: 1024, ordinaryAutosaveSeconds: 2 }; flush();
    await start(); let page = await open(false); const q = await quotaRecovery(page); await cooperativeTakeover(page, q); const w = await writerLoss(page);
    step("all recoverable RAM copies downloaded; whole-browser close then owned-origin stop/refusal", page);
    const closed = await closeBrowser(); await stop(); let refused;
    try { await fetch(origin + "/", { signal: AbortSignal.timeout(5000) }); assert.fail("Owned origin still answered"); }
    catch (e) { assert.equal(e.cause?.code, "ECONNREFUSED"); refused = e.cause.code; }
    record.restart = { ...closed, ownedServerStopped: true, nodeRefusal: refused, offlineAtContextLaunchBeforeNavigation: true }; flush();
    page = await open(true); step("cold offline: exact saved B, original A, binary, metadata and writer C", page);
    const B = await snapshot(page, q.name); exact(B, bigBytes); assert.deepEqual(brief(B), brief(q.B)); exact(await snapshot(page, q.name, q.A.revision.id), original);
    const C = await snapshot(page, w.name); exact(C, writerBytes); assert.deepEqual(brief(C), brief(w.C)); exact(await snapshot(page, w.name, w.A.revision.id), original);
    await H.open(page, q.name); await choose(page, q.name, bigBytes);
    const offlineText = displayed(bigBytes) + "% genuinely offline normal edit Київ 🙂\n", offlineBytes = editedBytes(offlineText);
    await fillOnce(page, q.name, bigBytes, offlineText, "cold-offline-normal-edit"); const D = await explicitSave(page, q.name, offlineBytes, q.B.revision.id, "cold-offline-explicit-save");
    await download(page, offlineBytes, "cold-offline-saved-D"); exact(await snapshot(page, q.name, q.A.revision.id), original); exact(await snapshot(page, q.name, q.B.revision.id), bigBytes); assert.deepEqual(brief(await snapshot(page, w.name)), brief(w.C));
    record.phases.coldOffline = { verifiedA: brief(q.A), verifiedB: brief(q.B), D: brief(D), unrelatedWriterCUnchanged: true, offlineNormalEditSaveDownload: true };
    await page.screenshot({ path: path.join(out, "cold-offline-success.png"), fullPage: true }); assert.deepEqual(record.externalRequests, []); assert.deepEqual(record.pageErrors, []); workflowComplete = true;
  } catch (error) {
    record.failure = { code: error.code || "ASSERTION_OR_OPERATION_FAILED", message: String(error.stack || error), cause: String(error.cause?.stack || error.cause || "") }; process.exitCode = 1; await captureFailure().catch(e => { record.failureCaptureError = String(e); });
  } finally {
    // Only the test-owned native lease is released here, never a product lock.
    try { await releaseNativeFault(); } catch (e) { record.nativeLeaseCleanupFailure = String(e); process.exitCode = 1; }
    // Reset native override even after a real failure; never patch storage APIs.
    for (const [page, session] of quotaSessions) if (!page.isClosed()) { try { const input = { origin }; await session.send("Storage.overrideQuotaForOrigin", input); record.quota.push({ label: "cleanup-reset-omitted-quotaSize", input, after: await session.send("Storage.getUsageAndQuota", { origin }) }); } catch (e) { record.quotaCleanupFailure = String(e); process.exitCode = 1; } }
    if (context) { try { await closeBrowser(); } catch (e) { record.browserCleanupFailure = String(e); process.exitCode = 1; } }
    try { await stop(); } catch (e) { record.serverCleanupFailure = String(e); process.exitCode = 1; }
    if (workflowComplete && !record.nativeLeaseCleanupFailure && !record.quotaCleanupFailure && !record.browserCleanupFailure && !record.serverCleanupFailure) { record.result = "PASS"; record.step = "PASS: bounded quota/retry, cooperative handover, native-revocation FAULT recovery and cold-offline"; }
    flush(); process.stdout.write(JSON.stringify({ result: record.result, step: record.step, receipt: receiptPath, failure: record.failure }) + "\n");
  }
})();
