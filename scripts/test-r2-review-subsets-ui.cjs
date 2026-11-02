// Real registered PWA review subsets. Test tooling ONLY; root executes this.
// No provider/worker/storage writes, held I/O, application-state injection or retries.
"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const http = require("node:http");
const { createHash } = require("node:crypto");
const H = require("./review-ui-helpers.cjs");
const { buildZip } = require("./zip-util.cjs");
const { validate: validateCsp } = require("./test-csp.cjs");
const sha = (b) => createHash("sha256").update(b).digest("hex");
const required = (key) => {
  assert(process.env[key], "Required environment: " + key);
  return process.env[key];
};
// Refuse sharing a result directory, even with a previous failed capture.
const out = fs.realpathSync(required("RESULT_DIR"));
assert.equal(
  fs.readdirSync(out).length,
  0,
  "RESULT_DIR must exist and be empty/exclusive",
);
fs.writeFileSync(
  path.join(out, "exclusive.json"),
  JSON.stringify({ pid: process.pid, started: new Date().toISOString() }),
  { flag: "wx" },
);
const receiptPath = path.join(out, "review-subsets-receipt.json");
const record = {
  schemaVersion: 1,
  workPackage: "R2-U-reviewed-subsets",
  result: "FAIL",
  execution: "root actual browser run",
  harnessSha256: sha(fs.readFileSync(__filename)),
  runtime: { node: process.version, platform: process.platform },
  step: "initializing",
  activeTab: null,
  publication: null,
  online: null,
  coldOffline: null,
  restart: null,
  externalRequests: [],
  pageErrors: [],
  workers: [],
  tabs: [],
  failure: null,
  notPhysicallyTested: [
    "Quota/save-failure recovery, held I/O and disposal races (managed ports only).",
    "AI/provider access and Ai-origin live data (separate R3); historical/large-budget selections.",
    "Full R2/native map/physical PDF compliance and all other browser/device targets.",
    "Direct forged service calls/old authorization tokens: no debug product API is injected. Old UI controls and canonical route are checked.",
  ],
};
let root, origin, port, context, browser, server, active, manifest;
let networkSequence = 0,
  ownedServer = false,
  workflowComplete = false;
const sockets = new Set(),
  tabIds = new WeakMap(),
  observed = new WeakSet();
function flush() {
  fs.writeFileSync(receiptPath, JSON.stringify(record, null, 2) + "\n");
}
function step(label, p = active) {
  if (p) active = p;
  record.step = label;
  record.activeTab = p ? tabIds.get(p) : null;
  flush();
}
function attach(p) {
  if (observed.has(p)) return;
  observed.add(p);
  const id = "tab-" + (record.tabs.length + 1);
  tabIds.set(p, id);
  record.tabs.push({ id, closed: false });
  p.setDefaultTimeout(30000);
  p.on("close", () => {
    record.tabs.find((t) => t.id === id).closed = true;
  });
  p.on("pageerror", (e) =>
    record.pageErrors.push({ tab: id, message: String(e).slice(0, 2000) }),
  );
  p.on("worker", (w) => {
    const v = { tab: id, url: w.url(), closed: false };
    record.workers.push(v);
    w.on("close", () => {
      v.closed = true;
    });
  });
}
function parsedUrl(url) {
  try {
    return new URL(url);
  } catch {
    return null;
  }
}
function isExternal(url) {
  const u = parsedUrl(url);
  return /^https?:/.test(url) && (!u || u.origin !== origin);
}
async function start() {
  assert(!server);
  const types = {
    ".html": "text/html",
    ".js": "text/javascript",
    ".json": "application/json",
    ".css": "text/css",
    ".wasm": "application/wasm",
    ".svg": "image/svg+xml",
  };
  server = http.createServer((req, res) => {
    let file;
    try {
      const pathname = decodeURIComponent(new URL(req.url, origin).pathname);
      file = fs.realpathSync(
        path.resolve(root, "." + (pathname === "/" ? "/index.html" : pathname)),
      );
      if (!file.startsWith(root + path.sep) || !fs.statSync(file).isFile()) {
        res.writeHead(403).end();
        return;
      }
    } catch {
      res.writeHead(404).end();
      return;
    }
    fs.readFile(file, (error, bytes) => {
      if (error) {
        res.writeHead(404).end();
        return;
      }
      res.writeHead(200, {
        "Content-Type": types[path.extname(file)] || "application/octet-stream",
        "Content-Security-Policy": record.publication.csp,
        "Cache-Control": "no-cache",
        "X-Content-Type-Options": "nosniff",
      });
      res.end(bytes);
    });
  });
  server.on("connection", (s) => {
    sockets.add(s);
    s.on("close", () => sockets.delete(s));
  });
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(port, "127.0.0.1", () => {
      ownedServer = true;
      resolve();
    });
  });
}
async function stop() {
  if (!server || !ownedServer) return;
  const old = server;
  server = null;
  ownedServer = false;
  for (const s of sockets) s.destroy();
  await new Promise((resolve, reject) =>
    old.close((e) => (e ? reject(e) : resolve())),
  );
}
async function open(offline) {
  const { chromium } = require("playwright");
  context = await chromium.launchPersistentContext(path.join(out, "profile"), {
    headless: true,
    offline,
    acceptDownloads: true,
  });
  browser = context.browser();
  record.runtime.browser = browser?.version() || "persistent Chromium";
  context.on("page", attach);
  for (const p of context.pages()) attach(p);
  context.on("request", (r) => {
    networkSequence++;
    if (isExternal(r.url())) {
      const u = parsedUrl(r.url());
      record.externalRequests.push({
        url: u ? u.origin + u.pathname : "invalid HTTP URL",
        method: r.method(),
        type: r.resourceType(),
      });
    }
  });
  // External endpoints are never selected in this workflow. Fail AND block if attempted.
  await context.route("**/*", (route) =>
    isExternal(route.request().url())
      ? route.abort("blockedbyclient")
      : route.continue(),
  );
  const p = await context.newPage();
  attach(p);
  step(offline ? "cold offline launch BEFORE navigation" : "online launch", p);
  await p.goto(origin + "/");
  await H.ready(p);
  return p;
}
async function closeWholeBrowser() {
  const pages = context.pages(),
    b = browser;
  await context.close();
  context = null;
  if (b?.isConnected()) await b.close();
  assert(
    pages.every((p) => p.isClosed()),
    "Every tab must close before cold restart",
  );
  assert(!b || !b.isConnected(), "Persistent browser must disconnect");
  browser = null;
  return { tabsClosed: pages.length, browserDisconnected: true };
}
const $ = H.$,
  BOM = Buffer.from([239, 187, 191]);
const main = "main.tex",
  notes = "розділ/нотатки.tex",
  binary = "untouched.bin",
  token = "SUBSET_TOKEN";
const replacement = "НОВЕ🙂$1\\literal";
const fixture = [
  [
    main,
    Buffer.concat([
      BOM,
      Buffer.from(
        "\\documentclass{article}\r\n\\setlength{\\parindent}{ 10 mm }\r\n\\begin{document}\r\n% Україна 😀 $1 \\ unchanged\r\n% SUBSET_TOKEN first\r\n% SUBSET_TOKEN keep\r\n% SUBSET_TOKEN third\r\n\\end{document}\r\n",
      ),
    ]),
  ],
  [
    notes,
    Buffer.concat([
      BOM,
      Buffer.from(
        "% Київ 🙂 literal $1 \\ unchanged\r\n% SUBSET_TOKEN keep\r\n% SUBSET_TOKEN second\r\n",
      ),
    ]),
  ],
  [binary, Buffer.from([0, 255, 13, 10, 128, 239, 187, 191, 42])],
];
const original = () => new Map(fixture.map(([f, b]) => [f, Buffer.from(b)]));
const normalize = (s) => s.replace(/\r\n?/g, "\n");
const editorText = (b) => normalize(b.toString("utf8").replace(/^\uFEFF/, ""));
const visible = (s) =>
  s
    .replace(/\uFEFF/g, "⟦BOM⟧")
    .replace(/\r/g, "⟦CR⟧")
    .replace(/\n/g, "⟦LF⟧\n");
const reEscape = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
// Search attributes are UTF-16 in EXACT decoded source (BOM excluded),
// unlike review-edit byte offsets. Only textarea presentation collapses CRLF.
// Derive independently from fixture prefixes; never read the actual selection
// to decide its expected range or reinterpret these indexes as UTF-8 bytes.
function expectedSearchRange(bytes, sourceStart, sourceEnd, expectedToken) {
  const source = bytes.toString("utf8").replace(/^\uFEFF/, "");
  assert(Number.isSafeInteger(sourceStart) && Number.isSafeInteger(sourceEnd));
  assert(sourceStart >= 0 && sourceEnd >= sourceStart && sourceEnd <= source.length);
  for (const offset of [sourceStart, sourceEnd]) {
    assert(!(source[offset - 1] === "\r" && source[offset] === "\n"), "Range splits CRLF");
    const before = source.charCodeAt(offset - 1), after = source.charCodeAt(offset);
    assert(!(before >= 0xd800 && before <= 0xdbff && after >= 0xdc00 && after <= 0xdfff), "Range splits surrogate pair");
  }
  assert.equal(source.slice(sourceStart, sourceEnd), expectedToken, "Exact-source UTF-16 span must select the fixture token");
  const start = normalize(source.slice(0, sourceStart)).length;
  const end = normalize(source.slice(0, sourceEnd)).length;
  assert.equal(editorText(bytes).slice(start, end), expectedToken);
  return { start, end, sourceStart, sourceEnd, sourceUnit: "UTF-16 exact source without BOM", domUnit: "UTF-16 LF-normalized textarea" };
}
function editList(bytes) {
  const edits = [];
  let start = 0;
  while ((start = bytes.indexOf(Buffer.from(token), start)) >= 0) {
    edits.push({
      start,
      end: start + Buffer.byteLength(token),
      expected: token,
      replacement,
    });
    start += Buffer.byteLength(token);
  }
  return edits;
}
function selectedMap() {
  return new Map([
    [main, [0, 2]],
    [notes, [1]],
  ]);
}
function desired(subset) {
  const result = original();
  for (const file of [main, notes]) {
    let bytes = result.get(file);
    const edits = editList(bytes),
      ids = subset ? selectedMap().get(file) : edits.map((_, i) => i);
    for (const i of [...ids].reverse()) {
      const e = edits[i];
      bytes = Buffer.concat([
        bytes.subarray(0, e.start),
        Buffer.from(replacement),
        bytes.subarray(e.end),
      ]);
    }
    result.set(file, bytes);
  }
  return result;
}
function verifyBytes(head, expected) {
  assert.equal(head.files.length, expected.size);
  for (const [file, b] of expected) {
    const f = head.files.find((f) => f.path === file);
    assert(f, "Missing " + file);
    assert.deepEqual(Buffer.from(f.bytes), b, "Exact saved bytes: " + file);
  }
}
function summarize(head) {
  return {
    id: head.id,
    parent: head.parent,
    files: head.files.map((f) => ({
      id: f.id,
      path: f.path,
      length: f.bytes.length,
      sha256: sha(Buffer.from(f.bytes)),
    })),
  };
}
async function head(p, name, expected) {
  const h = await H.head(p, name);
  if (expected) verifyBytes(h, expected);
  return h;
}
async function choose(p, name, file, expected) {
  active = p;
  const h = await head(p, name),
    id = h.files.find((f) => f.path === file)?.id;
  assert(id, file);
  const target = $(p, "select-file").filter({
    has: p.locator(".file-name", {
      hasText: new RegExp("^" + reEscape(file) + "$"),
    }),
  });
  assert.equal(await target.count(), 1);
  await target.click();
  await p.waitForFunction(
    ({ id, value }) => {
      const e = document.querySelector("[data-testid=editor]");
      return e?.dataset.fileId === id && e.value === value;
    },
    { id, value: editorText(expected) },
  );
  return id;
}
async function working(p, name, expected) {
  const before = await H.stamp(p),
    values = [];
  for (const [f, b] of expected)
    if (f !== binary) {
      await choose(p, name, f, b);
      values.push(
        await $(p, "editor").evaluate((e) => ({
          file: e.dataset.fileId,
          value: e.value,
          readonly: e.readOnly || e.disabled,
        })),
      );
    }
  assert.deepEqual(
    await H.stamp(p),
    before,
    "Inspecting files must not mutate generation/head",
  );
  return values;
}
async function reviewIdle(p) {
  await H.idle(p);
  await p.waitForFunction(
    () =>
      document.querySelector("[data-testid=project-search-panel]")?.dataset
        .busy === "false",
  );
}
async function actionId(p) {
  const a = $(p, "review-action");
  return (await a.count()) ? a.getAttribute("data-action-id") : null;
}
async function ids(p) {
  const q = $(p, "review-preview");
  if (!(await q.count())) return null;
  const id = await q.getAttribute("data-preview-id"),
    proposal = await q.getAttribute("data-proposal-id");
  assert.match(id || "", /^[0-9a-f-]{36}$/i);
  assert.match(
    proposal || "",
    /^[0-9a-f-]{36}$/i,
    "Missing proposal-ID UI seam: apply the bounded ReviewPanel observability proposal before publication",
  );
  return { preview: id, proposal, origin: await q.getAttribute("data-origin") };
}
async function newPreview(p, old) {
  await p.waitForFunction(
    (old) => {
      const panel = document.querySelector("[data-testid=review-panel]"),
        q = document.querySelector("[data-testid=review-preview]");
      return (
        panel?.dataset.busy === "false" &&
        q?.dataset.previewId &&
        q.dataset.previewId !== old?.preview
      );
    },
    old,
    { timeout: 60000 },
  );
  await reviewIdle(p);
  const fresh = await ids(p);
  if (old)
    assert.notEqual(
      fresh.proposal,
      old.proposal,
      "Fresh preview must have a NEW proposal ID",
    );
  return fresh;
}
async function search(p) {
  await H.reveal(p, $(p, "project-search-query"));
  // Explicitly retire a prior complete result through the real control. A
  // retained Complete status must not satisfy this NEW search operation.
  if (await $(p, "project-search-cancel").isEnabled()) {
    await $(p, "project-search-cancel").click();
    await p.waitForFunction(
      () =>
        document.querySelector("[data-testid=project-search-status]")?.dataset
          .searchStatus === "Cancelled" &&
        document.querySelectorAll("[data-testid=project-search-result]")
          .length === 0 &&
        document.querySelector("[data-testid=project-search-panel]")?.dataset
          .busy === "false",
    );
  }
  await $(p, "project-search-query").fill(token);
  assert.equal(await $(p, "project-search-query").inputValue(), token);
  await $(p, "project-search-submit").click();
  await p.waitForFunction(
    () =>
      document.querySelector("[data-testid=project-search-status]")?.dataset
        .searchStatus === "Complete" &&
      document.querySelector("[data-testid=project-search-panel]")?.dataset
        .busy === "false",
  );
  assert.equal(await $(p, "project-search-result").count(), 5);
}
async function fullProposal(p, name) {
  const old = await ids(p),
    before = await head(p, name, original()),
    stamp = await H.stamp(p);
  await search(p);
  await $(p, "project-replace-text").fill(replacement);
  await $(p, "project-replace-all").click();
  await p.waitForFunction(() =>
    document
      .querySelector("[data-testid=project-replace-selection]")
      ?.textContent.includes("5 match(es) in 2 file(s) selected;"),
  );
  assert.equal(
    await $(p, "project-replace-match").evaluateAll(
      (es) => es.filter((e) => e.checked).length,
    ),
    5,
  );
  await H.reveal(p, $(p, "project-replace-preview"));
  await $(p, "project-replace-preview").click();
  const identity = await newPreview(p, old);
  assert.equal(identity.origin, "User");
  await verifyReview(p, false);
  assert.deepEqual(await head(p, name, original()), before);
  assert.deepEqual(await H.stamp(p), stamp);
  return identity;
}
async function verifyReview(p, subset) {
  const expected = desired(subset),
    evidence = [];
  assert.equal(await $(p, "review-file").count(), 2);
  for (const file of [main, notes]) {
    const f = $(p, "review-file").filter({
      has: p.locator("[data-testid=review-path]", {
        hasText: new RegExp("^" + reEscape(file) + "$"),
      }),
    });
    assert.equal(await f.count(), 1);
    const edits = editList(original().get(file)),
      chosen = subset ? selectedMap().get(file) : edits.map((_, i) => i),
      shown = f.locator("[data-testid=review-edit]");
    assert.equal(await shown.count(), chosen.length);
    for (let i = 0; i < chosen.length; i++) {
      const e = edits[chosen[i]],
        row = shown.nth(i);
      assert.equal(await row.getAttribute("data-start-byte"), String(e.start));
      assert.equal(await row.getAttribute("data-end-byte"), String(e.end));
      assert.equal(
        await row.locator("[data-testid=review-expected]").textContent(),
        visible(e.expected),
      );
      assert.equal(
        await row.locator("[data-testid=review-replacement]").textContent(),
        visible(replacement),
      );
    }
    const before = await f.locator("[data-testid=review-before]").textContent(),
      after = await f.locator("[data-testid=review-after]").textContent();
    assert.equal(before, visible(original().get(file).toString("utf8")));
    assert.equal(after, visible(expected.get(file).toString("utf8")));
    assert.equal(
      await f.locator("[data-testid=review-diff-summary]").count(),
      0,
    );
    assert.match(
      await f.locator("[data-testid=review-line-diff]").innerText(),
      /НОВЕ🙂\$1/,
    );
    evidence.push({
      file,
      originalEditIndexes: chosen,
      edits: chosen.map((i) => edits[i]),
      beforeSha256: sha(original().get(file)),
      afterSha256: sha(expected.get(file)),
    });
  }
  return evidence;
}
async function observations(p) {
  return {
    stamp: await H.stamp(p),
    ui: await p.evaluate(() => {
      const text = (id) =>
        document.querySelector('[data-testid="' + id + '"]')?.textContent;
      return {
        analysis: text("analysis-status"),
        compile: text("compile-status"),
        log: text("compiler-log"),
        review: text("review-persistence"),
      };
    }),
    networkSequence,
  };
}
async function selectSubset(p, name, identity) {
  const before = await head(p, name, original()),
    obs = await observations(p),
    action = await actionId(p);
  assert.equal(await $(p, "subset-choice").count(), 5);
  assert.equal(
    await $(p, "subset-choice").evaluateAll(
      (es) => es.filter((e) => e.checked).length,
    ),
    0,
  );
  for (const [file, indexes] of [...selectedMap()].reverse())
    for (const index of [...indexes].reverse()) {
      const id = before.files.find((f) => f.path === file).id;
      const row = p.locator(
        `[data-testid=subset-edit][data-file-id="${id}"][data-edit-index="${index}"]`,
      );
      assert.equal(await row.count(), 1);
      await row.locator("[data-testid=subset-choice]").check();
    }
  assert.equal(
    await $(p, "subset-choice").evaluateAll(
      (es) => es.filter((e) => e.checked).length,
    ),
    3,
  );
  // DOM checked flags alone do not acknowledge the C# choice model.
  await p.waitForFunction(
    () =>
      document
        .querySelector("[data-testid=subset-scope]")
        ?.textContent.includes("3 selected of 5 exact original edits") &&
      document.querySelector("[data-testid=subset-preview]")?.disabled ===
        false,
  );
  assert.match(
    await $(p, "subset-scope").innerText(),
    /3 selected of 5 exact original edits/,
  );
  assert.deepEqual(await ids(p), identity);
  assert.deepEqual(await head(p, name, original()), before);
  assert.deepEqual(
    await observations(p),
    obs,
    "Checkboxes must not change generation/head/analysis/compiler/recovery/network state",
  );
  await $(p, "subset-preview").click();
  const fresh = await newPreview(p, identity);
  assert.notEqual(fresh.preview, identity.preview);
  assert.notEqual(fresh.proposal, identity.proposal);
  assert.equal(fresh.origin, "User");
  const exact = await verifyReview(p, true);
  assert.deepEqual(await head(p, name, original()), before);
  assert.deepEqual(await H.stamp(p), obs.stamp);
  assert.equal(await actionId(p), action, "Preview must not silently apply");
  return {
    original: identity,
    subset: fresh,
    exact,
    selectionOnlyUnchanged: true,
  };
}
async function decline(p, name) {
  const before = await head(p, name, original()),
    stamp = await H.stamp(p);
  await $(p, "review-decline").click();
  await $(p, "review-preview").waitFor({ state: "detached" });
  await reviewIdle(p);
  assert.deepEqual(await head(p, name, original()), before);
  assert.deepEqual(await H.stamp(p), stamp);
}
async function acceptUndo(p, name, expected) {
  const before = await head(p, name, original()),
    previous = await actionId(p);
  await $(p, "review-accept").click();
  await p.waitForFunction(
    (previous) => {
      const a = document.querySelector("[data-testid=review-action]");
      return (
        a &&
        a.dataset.actionId !== previous &&
        a.dataset.persisted === "true" &&
        a.dataset.undone === "false" &&
        document.querySelector("[data-testid=review-panel]")?.dataset.busy ===
          "false"
      );
    },
    previous,
    { timeout: 60000 },
  );
  await $(p, "review-preview").waitFor({ state: "detached" });
  const action = await $(p, "review-action").getAttribute("data-action-id"),
    after = await head(p, name, expected);
  assert.notEqual(after.id, before.id);
  assert.equal(after.parent, before.id);
  await working(p, name, expected);
  await $(p, "review-undo").click();
  await p.waitForFunction(
    (id) => {
      const a = document.querySelector("[data-testid=review-action]");
      return (
        a?.dataset.actionId === id &&
        a.dataset.persisted === "true" &&
        a.dataset.undone === "true" &&
        document.querySelector("[data-testid=review-panel]")?.dataset.busy ===
          "false"
      );
    },
    action,
    { timeout: 60000 },
  );
  const undone = await head(p, name, original());
  assert.notEqual(undone.id, after.id);
  assert.notEqual(undone.id, before.id);
  assert.equal(undone.parent, after.id);
  assert.equal(await $(p, "review-undo").count(), 0);
  await working(p, name, original());
  return {
    action,
    accepted: summarize(after),
    undone: summarize(undone),
    exactSavedBytes: true,
    untouchedBinary: true,
    newDurableHeads: true,
  };
}
async function closeProject(p) {
  const close = p.getByRole("button", { name: "Close", exact: true });
  if (await close.count()) {
    await close.click();
    await p.locator(".project-dashboard").waitFor();
  }
}
async function importProject(p, name) {
  await closeProject(p);
  await H.reveal(p, $(p, "import-zip"));
  await $(p, "import-zip").setInputFiles({
    name: "synthetic-subsets.zip",
    mimeType: "application/zip",
    buffer: buildZip(fixture),
  });
  await H.reveal(p, $(p, "import-preview"));
  await $(p, "import-preview").waitFor({ timeout: 180000 });
  await $(p, "staged-name").fill(name);
  await $(p, "publish-import").click();
  await $(p, "select-file").first().waitFor({ timeout: 180000 });
  await p.waitForFunction(
    () =>
      document
        .querySelector("[data-testid=compiler-readiness]")
        ?.textContent.includes("compilation available."),
    null,
    { timeout: 240000 },
  );
  // Finish genuine provisioning/cache work before attributing any new request
  // to checkbox actions. No synthetic network/worker instrumentation is used.
  await p.waitForFunction(
    () =>
      navigator.serviceWorker.controller !== null &&
      window.__oopOfflineReady === true,
    null,
    { timeout: 240000 },
  );
  await H.reveal(p, $(p, "analysis-profile"));
  await $(p, "analysis-profile").selectOption("engine-structural");
  await H.analyze(p);
  const before = await head(p, name, original());
  await working(p, name, original());
  return before;
}
async function staleSelection(p, name) {
  const identity = await fullProposal(p, name);
  const oldButton = await $(p, "subset-preview").elementHandle();
  // Select one real old choice, then change analysis selection A -> B -> A.
  await $(p, "subset-choice").first().check();
  const before = await head(p, name, original()),
    stamp = await H.stamp(p);
  await $(p, "analysis-profile").selectOption("knu-csc-2025-source@1.0.0");
  await $(p, "analysis-profile").selectOption("engine-structural");
  await H.analyze(p);
  await $(p, "review-preview").waitFor({ state: "detached" });
  await p.waitForFunction(
    () => document.querySelectorAll("[data-testid=subset-choice]").length === 0,
  );
  assert(
    await oldButton.evaluate((e) => !e.isConnected || e.disabled),
    "Old subset control must retire, not rebase onto new analysis",
  );
  assert.deepEqual(await head(p, name, original()), before);
  assert.deepEqual(await H.stamp(p), stamp);
  return {
    retired: identity,
    analysisSelectionAba: true,
    oldControlRetired: true,
    unchangedSourceHeadGeneration: true,
  };
}
async function canonical(p, name, readOnly = false) {
  const before = await head(p, name, original());
  await H.selectKnu(p);
  // Stable canonical ID, not display text or a caller-supplied descriptor.
  const fix = p.locator(
    '[data-testid=preview-fix][data-fix-id="KNU-FIX-PARINDENT-LITERAL-001"]',
  );
  assert.equal(await fix.count(), 1);
  const old = await ids(p);
  await H.reveal(p, fix);
  await fix.click();
  const identity = await newPreview(p, old);
  assert.equal(identity.origin, "Rule");
  await $(p, "subset-rule-indivisible").waitFor();
  assert.equal(await $(p, "subset-choice").count(), 0);
  assert.equal(await $(p, "subset-preview").count(), 0);
  assert.equal((await $(p, "review-expected").textContent()).trim(), "10 mm");
  assert.equal(
    (await $(p, "review-replacement").textContent()).trim(),
    "1.27cm",
  );
  assert.match(
    await $(p, "review-citation").innerText(),
    /KNU-SRC-PARINDENT-001/,
  );
  assert.deepEqual(await head(p, name, original()), before);
  if (readOnly) {
    assert(await $(p, "review-accept").isDisabled());
    await decline(p, name);
    return { indivisible: true, readOnlyAcceptDisabled: true, identity };
  }
  const expected = original();
  expected.set(
    main,
    Buffer.from(expected.get(main).toString("utf8").replace("10 mm", "1.27cm")),
  );
  const result = await acceptUndo(p, name, expected);
  return { identity, indivisible: true, fullCanonicalAcceptUndo: result };
}
async function readonly(p, name) {
  const before = await head(p, name, original()),
    reader = await context.newPage();
  attach(reader);
  step(
    "genuine read-only tab: inspect/search/canonical preview, no takeover/write",
    reader,
  );
  try {
    await reader.goto(origin + "/");
    await H.ready(reader);
    await H.open(reader, name);
    await choose(reader, name, main, original().get(main));
    await reader.waitForFunction(() => {
      const e = document.querySelector("[data-testid=editor]");
      return e && (e.disabled || e.readOnly);
    });
    const stamp = await H.stamp(reader);
    await search(reader);
    assert(await $(reader, "project-replace-all").isDisabled());
    assert(await $(reader, "project-replace-preview").isDisabled());
    assert(
      await $(reader, "project-replace-match").evaluateAll(
        (es) => es.length === 5 && es.every((e) => e.disabled),
      ),
    );
    assert.equal(await $(reader, "subset-choice").count(), 0);
    const result = $(reader, "project-search-result").first(),
      targetPath = await result.getAttribute("data-source-path");
    const targetId = before.files.find((f) => f.path === targetPath)?.id;
    assert(targetId);
    const sourceStart = Number(await result.getAttribute("data-source-start")),
      sourceEnd = Number(await result.getAttribute("data-source-end"));
    const expectedRange = expectedSearchRange(original().get(targetPath), sourceStart, sourceEnd, token);
    const { start, end } = expectedRange;
    await result.click();
    await reader.waitForFunction(
      ({ id, text, start, end, token }) => {
        const e = document.querySelector("[data-testid=editor]");
        return (
          e?.dataset.fileId === id &&
          e.value === text &&
          e.selectionStart === start &&
          e.selectionEnd === end &&
          e.value.slice(start, end) === token
        );
      },
      {
        id: targetId,
        text: editorText(original().get(targetPath)),
        start,
        end,
        token,
      },
    );
    const selection = await $(reader, "editor").evaluate((e) => ({
      file: e.dataset.fileId,
      start: e.selectionStart,
      end: e.selectionEnd,
      text: e.value.slice(e.selectionStart, e.selectionEnd),
      readonly: e.readOnly || e.disabled,
    }));
    assert.equal(selection.file, targetId);
    assert.equal(selection.text, token);
    assert.equal(selection.readonly, true);
    assert.deepEqual([selection.start, selection.end], [start, end]);
    const rule = await canonical(reader, name, true);
    assert.deepEqual(await head(reader, name, original()), before);
    assert.deepEqual(await H.stamp(reader), stamp);
    return {
      genuineSecondTab: true,
      searchSelection: selection,
      expectedSearchRange: expectedRange,
      rule,
      writerNeverTakenOver: true,
      headUnchanged: true,
    };
  } catch (error) {
    await captureFailure();
    throw error;
  } finally {
    await reader.close();
    active = p;
  }
}
async function staleEdit(p, label) {
  const name = "Subset stale edit " + label + " " + Date.now();
  await importProject(p, name);
  const old = await fullProposal(p, name);
  await $(p, "subset-choice").first().check();
  const control = await $(p, "subset-preview").elementHandle();
  const expected = original(),
    id = await choose(p, name, notes, expected.get(notes)),
    stamp = await H.stamp(p);
  const text =
    editorText(expected.get(notes)) +
    "% deliberate intervening source edit Україна 🙂\n";
  await $(p, "editor").fill(text); // ONE fill only, after exact target file/value render fence.
  await p.waitForFunction(
    ({ id, text, generation }) => {
      const e = document.querySelector("[data-testid=editor]"),
        s = document.querySelector("[data-testid=analysis-panel]");
      return (
        e?.dataset.fileId === id &&
        e.value === text &&
        Number(s?.dataset.generation) > Number(generation)
      );
    },
    { id, text, generation: stamp.generation },
  );
  await $(p, "review-preview").waitFor({ state: "detached" });
  assert(await control.evaluate((e) => !e.isConnected || e.disabled));
  assert.equal(await $(p, "subset-choice").count(), 0);
  const clicked = await $(p, "save").isEnabled();
  if (clicked) await $(p, "save").click();
  await p.waitForFunction(
    () =>
      document.querySelector(".workspace-header .status")?.textContent ===
      "Saved.",
  );
  expected.set(notes, Buffer.concat([BOM, Buffer.from(text)]));
  const final = await head(p, name, expected);
  assert.notEqual(final.id, stamp.head);
  await choose(p, name, notes, expected.get(notes));
  return {
    project: name,
    retired: old,
    oneFencedFill: true,
    explicitSaveClicked: clicked,
    oldControlsRetired: true,
    finalHead: final,
  };
}
async function phase(p, label) {
  const name = "Review subsets " + label + " " + Date.now();
  step(label + ": import exact synthetic two-text/binary project", p);
  const initial = await importProject(p, name);
  step(
    label +
      ": full real literal proposal, noncontiguous subset, NEW exact preview, decline",
    p,
  );
  const declined = await selectSubset(p, name, await fullProposal(p, name));
  await decline(p, name);
  step(
    label +
      ": freshly generated full proposal/subset, explicit accept/save and exact one-action Undo",
    p,
  );
  const accepted = await selectSubset(p, name, await fullProposal(p, name));
  const roundTrip = await acceptUndo(p, name, desired(true));
  step(
    label + ": old original controls retired by analysis selection A-B-A",
    p,
  );
  const stale = await staleSelection(p, name);
  step(
    label + ": canonical indivisible rule, independent full Accept/Save/Undo",
    p,
  );
  const rule = await canonical(p, name);
  const reader = await readonly(p, name);
  step(label + ": final exact original project proof", p);
  const final = await head(p, name, original());
  await working(p, name, original());
  await p.screenshot({
    path: path.join(out, label + "-primary.png"),
    fullPage: true,
  });
  step(
    label +
      ": separate genuine intervening-edit project (do not normalize the primary fixture)",
    p,
  );
  const edit = await staleEdit(p, label);
  assert.deepEqual(
    await head(p, name, original()),
    final,
    "Stale-edit project must not mutate old primary",
  );
  return {
    project: name,
    initial: summarize(initial),
    declined,
    accepted,
    roundTrip,
    stale,
    rule,
    reader,
    finalHead: final,
    staleEdit: edit,
  };
}
async function captureFailure() {
  record.failureTabs ||= [];
  for (const p of context?.pages() || []) {
    if (p.isClosed()) continue;
    const id = tabIds.get(p);
    const state = await p
      .evaluate(() => ({
        url: location.href,
        body: document.body.innerText.slice(0, 45000),
        editor: (() => {
          const e = document.querySelector("[data-testid=editor]");
          return e
            ? {
                file: e.dataset.fileId,
                value: e.value,
                disabled: e.disabled,
                readonly: e.readOnly,
                start: e.selectionStart,
                end: e.selectionEnd,
              }
            : null;
        })(),
        analysis: document
          .querySelector("[data-testid=analysis-panel]")
          ?.outerHTML.slice(0, 5000),
        review: document
          .querySelector("[data-testid=review-panel]")
          ?.outerHTML.slice(0, 22000),
        subsets: document
          .querySelector("[data-testid=review-subsets]")
          ?.outerHTML.slice(0, 22000),
        choices: [
          ...document.querySelectorAll("[data-testid=subset-choice]"),
        ].map((e) => ({
          checked: e.checked,
          disabled: e.disabled,
          file: e.closest("[data-file-id]")?.dataset.fileId,
          index: e.closest("[data-edit-index]")?.dataset.editIndex,
        })),
      }))
      .catch((e) => ({ unavailable: String(e) }));
    record.failureTabs.push({ id, active: p === active, state });
    await p
      .screenshot({
        path: path.join(out, "failure-" + id + ".png"),
        fullPage: true,
      })
      .catch(() => {});
  }
}
(async () => {
  flush();
  try {
    root = fs.realpathSync(required("PUBLISHED_WWWROOT"));
    assert(
      !out.startsWith(root + path.sep) && out !== root,
      "Results/profile must not be inside publication",
    );
    port = Number(process.env.TEST_PORT || 8789);
    assert(Number.isInteger(port) && port > 1024 && port < 65536);
    origin = "http://127.0.0.1:" + port;
    const expectedSdk = required("EXPECTED_SDK_VERSION"),
      sourceDigest = required("CANDIDATE_SOURCE_DIGEST");
    assert.match(sourceDigest, /^[0-9a-f]{64}$/);
    const cspBytes = fs.readFileSync(
      path.join(__dirname, "../docs/examples/static-csp-headers.conf"),
      "utf8",
    );
    validateCsp(
      fs.readFileSync(path.join(root, "index.html"), "utf8"),
      cspBytes,
    );
    const header = cspBytes
      .split("\n")
      .map((s) => s.trim())
      .find((s) => s.startsWith("Content-Security-Policy:"));
    assert(header);
    const csp = header.slice("Content-Security-Policy:".length).trim();
    const manifestBytes = fs.readFileSync(
      path.join(root, "service-worker-assets.js"),
    );
    manifest = JSON.parse(
      manifestBytes
        .toString()
        .match(/self\.assetsManifest\s*=\s*(\{[\s\S]*\})\s*;/)[1],
    );
    assert.equal(
      manifest.version,
      expectedSdk,
      "Wrong publication (do not test old live diagnostics build)",
    );
    const seen = new Set();
    for (const a of manifest.assets) {
      assert(
        !path.isAbsolute(a.url) &&
          !a.url.split("/").includes("..") &&
          !seen.has(a.url),
      );
      seen.add(a.url);
      const file = fs.realpathSync(path.resolve(root, a.url));
      assert(file.startsWith(root + path.sep));
      assert.equal(
        "sha256-" +
          createHash("sha256").update(fs.readFileSync(file)).digest("base64"),
        a.hash,
        "Manifest mismatch " + a.url,
      );
    }
    record.inputs = Object.fromEntries(
      ["review-ui-helpers.cjs", "zip-util.cjs", "test-csp.cjs"].map((f) => [
        f,
        sha(fs.readFileSync(path.join(__dirname, f))),
      ]),
    );
    record.publication = {
      sdkVersion: manifest.version,
      assetCount: manifest.assets.length,
      manifestSha256: sha(manifestBytes),
      candidateSourceDigest: sourceDigest,
      origin,
      csp,
      cspSourceSha256: sha(cspBytes),
      allManifestAssetHashesVerified: true,
    };
    record.fixture = {
      zipSha256: sha(buildZip(fixture)),
      files: fixture.map(([name, b]) => ({
        name,
        length: b.length,
        sha256: sha(b),
      })),
      token,
      replacement,
    };
    flush();
    await start();
    let p = await open(false);
    record.online = await phase(p, "online");
    step("wait genuine PWA offline readiness before whole browser shutdown", p);
    await p.evaluate(() => navigator.serviceWorker.ready);
    await p.waitForFunction(
      () =>
        navigator.serviceWorker.controller !== null &&
        window.__oopOfflineReady === true,
      null,
      { timeout: 240000 },
    );
    const closed = await closeWholeBrowser();
    await stop();
    let refusal;
    try {
      await fetch(origin + "/", { signal: AbortSignal.timeout(5000) });
      assert.fail("Origin still answered after owned server stopped");
    } catch (e) {
      assert.equal(
        e.cause?.code,
        "ECONNREFUSED",
        "Require actual connection-refusal proof, not timeout/other fetch failure",
      );
      refusal = e.cause.code;
    }
    record.restart = {
      ...closed,
      ownedServerStopped: true,
      nodeConnectionRefused: refusal,
      offlineAppliedAtPersistentContextLaunch: true,
    };
    flush();
    p = await open(true);
    assert.deepEqual(
      await head(p, record.online.project, original()),
      record.online.finalHead,
    );
    assert.deepEqual(
      await head(p, record.online.staleEdit.project),
      record.online.staleEdit.finalHead,
    );
    record.coldOffline = await phase(p, "cold-offline");
    assert.deepEqual(
      await head(p, record.online.project, original()),
      record.online.finalHead,
      "Old online project must remain exact through offline work",
    );
    assert.deepEqual(
      await head(p, record.online.staleEdit.project),
      record.online.staleEdit.finalHead,
    );
    assert.deepEqual(record.pageErrors, []);
    assert.deepEqual(record.externalRequests, []);
    workflowComplete = true;
    step(
      "Workflow checks passed; closing whole browser and owned server before final PASS",
      p,
    );
  } catch (e) {
    record.failure = String(e.stack || e);
    process.exitCode = 1;
    await captureFailure().catch((error) => {
      record.failureCaptureError = String(error);
    });
  } finally {
    if (context) {
      try {
        await closeWholeBrowser();
      } catch (e) {
        record.result = "FAIL";
        record.cleanupFailure = String(e);
        process.exitCode = 1;
        await captureFailure().catch(() => {});
      }
    }
    try {
      await stop();
    } catch (e) {
      record.result = "FAIL";
      record.serverCleanupFailure = String(e);
      process.exitCode = 1;
    }
    // Synthetic persistent profile intentionally retained for root forensics.
    if (
      workflowComplete &&
      !record.cleanupFailure &&
      !record.serverCleanupFailure
    ) {
      record.result = "PASS";
      record.step =
        "PASS: bounded real subset/decline/accept/save/Undo online and full host-stopped cold-offline";
    }
    flush();
    process.stdout.write(
      JSON.stringify({
        result: record.result,
        step: record.step,
        receipt: receiptPath,
        failure: record.failure,
      }) + "\n",
    );
  }
})();
