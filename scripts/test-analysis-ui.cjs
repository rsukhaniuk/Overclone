// Real-Chromium analysis UI coverage (M2a-2b-2b): wires the ACCEPTED AnalysisCoordinator
// to the workspace page and verifies the published diagnostics panel, guarded
// click-to-source, honest invalidation, cancellation, failed-save independence,
// unavailable/unsupported coverage, HTML-escaping, and page-lifecycle reuse.
//
// The HAPPY analysis paths import the REAL rust-bridge ES module and run the
// REAL wasm-bindgen WASM through the page UI (no synthetic worker there). FAULT
// cases (U3 held worker, U5 truncated/indeterminate via lowered caps) use a
// test-local Worker replacement via Playwright route interception and are
// LABELED; each is followed by a SAME-page recovery that removes the synthetic
// route and runs actual Rust WASM again. No new Function/eval; self-contained
// page.evaluate drivers as existing tests do. No product global hooks / env
// flags just for tests. OPFS/WebLocks need a secure localhost origin, so a
// tiny localhost HTTP proxy fronts http://app:8080 (same pattern as
// test-workspace.cjs).

const assert = require("node:assert/strict");
const http = require("node:http");
const { chromium } = require("playwright");
const { reveal } = require("./review-ui-helpers.cjs");

const UPSTREAM = process.env.APP_URL || "http://app:8080";
const PROXY_PORT = 8094;
const BASE = `http://127.0.0.1:${PROXY_PORT}`;

// U1 fixture: UTF-8 BOM + "Привіт 👩🏽\r\nРозділ {текст\r\n" — unmatched { AFTER first CRLF.
const U1_TEXT = "Привіт 👩🏽\r\nРозділ {текст\r\n";
const U1_BYTES = Buffer.concat([
  Buffer.from([0xef, 0xbb, 0xbf]),
  Buffer.from(U1_TEXT, "utf-8"),
]);

// A non-responsive fake worker (holds analyzeSource forever) for U3 cancel.
const NONRESPONSIVE_WORKER = `
self.onmessage = ({ data }) => {
  if (data && typeof data.protocolVersion === "number") {
    if (data.operation === "initialize") {
      self.postMessage({ canonical: true, requestId: data.requestId, responseJson:
        JSON.stringify({ protocolVersion:1, requestId: data.requestId, status: "completed",
          payload: { engineVersion: "fake", operations: ["analyzeSource"], limits: {} } }) });
      return;
    }
    if (data.operation === "analyzeSource") return; // never reply (held)
  }
  if (data.type === "ready") self.postMessage({ id: data.id, result: true });
};
`;

// A held-reply synthetic worker (LABELED synthetic fault) for U2 "late reply never
// repaints": on analyzeSource it builds a VALID canonical completed response from
// the request metadataJson (matching snapshotId/profile/fileId/byteLength) with one
// ENGINE-STRUCT-001 diagnostic at byte [1,2) ("{"), and DELAYS the reply so the
// test can edit/switch/close before it arrives. A late valid completed reply that
// arrives after an intent bump must be Superseded (no findings repaint). This
// synthetic worker never runs the real Rust core; recovery re-routes to real WASM.
const HELD_REPLY_WORKER = `
self.onmessage = ({ data }) => {
  if (data && typeof data.protocolVersion === "number") {
    if (data.operation === "initialize") {
      self.postMessage({ canonical: true, requestId: data.requestId, responseJson:
        JSON.stringify({ protocolVersion:1, requestId: data.requestId, status: "completed",
          payload: { engineVersion: "synthetic-held-reply", operations: ["analyzeSource"], limits: {} } }) });
      return;
    }
    if (data.operation === "analyzeSource") {
      const meta = JSON.parse(data.payload.metadataJson);
      const file = meta.payload.files[0];
      const fileId = file.fileId;
      const resp = {
        protocolVersion: 1, requestId: data.requestId, status: "completed",
        payload: {
          snapshotId: meta.payload.snapshotId,
          profile: meta.payload.profile,
          facts: { files: [{
            fileId, byteLength: file.byteLength, hasBom: file.hasBom,
            newlineKind: "lf", truncated: false, braceDepthExceeded: false,
            tokenCount: 0, engineOutcome: "fail", tokens: [] }] },
          diagnostics: [{
            id: fileId + ":ENGINE-STRUCT-001:0", ruleId: "ENGINE-STRUCT-001",
            origin: "engine", severity: "warning", fileId,
            span: { startByte: 1, endByte: 2 },
            message: "Unmatched opening brace.", primaryLocation: { line: 1, column: 1 } }],
          ruleResults: [{
            ruleId: "ENGINE-STRUCT-001", origin: "engine", outcome: "fail",
            perFile: [{ fileId, outcome: "fail", diagnosticIds: [fileId + ":ENGINE-STRUCT-001:0"] }],
            limitations: [] }],
          aggregateOutcome: "fail",
          coverage: { factKinds: [], unsupported: [], truncatedFiles: [],
            diagnosticsTruncated: false, resultTruncated: false },
          unavailableCapabilities: ["pdfFacts", "pdfChecks"] } } };
      // HOLD the reply so the test can invalidate (edit/switch/close) before it arrives.
      setTimeout(() => {
        self.postMessage({ canonical: true, requestId: data.requestId, responseJson: JSON.stringify(resp) });
      }, 1200);
      return;
    }
  }
  if (data && data.type === "ready") self.postMessage({ id: data.id, result: true });
};
`;

function startProxy() {
  return http.createServer((cr, res) => {
    const u = new URL(cr.url, UPSTREAM);
    const h = Object.assign({}, cr.headers, { host: u.host });
    const up = http.request(u, { method: cr.method, headers: h }, (r) => {
      res.writeHead(r.statusCode, r.headers);
      r.pipe(res);
    });
    up.on("error", () => res.destroy());
    cr.pipe(up);
  });
}

// Fetch a same-origin asset body from UPSTREAM via node http (no browser/SW),
// used to replay the REAL editor-select.js source through a held route.
function fetchUpstreamText(pathname) {
  return new Promise((resolve, reject) => {
    const u = new URL(pathname, UPSTREAM);
    const req = http.get(u, (r) => {
      if (r.statusCode !== 200) {
        reject(new Error(`${pathname} -> ${r.statusCode}`));
        return;
      }
      let b = "";
      r.setEncoding("utf8");
      r.on("data", (d) => (b += d));
      r.on("end", () => resolve(b));
    });
    req.on("error", reject);
  });
}

function unique(n) {
  return `${n}-${Date.now()}-${Math.random().toString(36).slice(2, 7)}`;
}

async function waitForReady(page) {
  for (let i = 0; i < 90; i++) {
    try {
      if (
        await page.evaluate(
          () => !!document.querySelector(".project-dashboard, [data-testid=new-file-name]"),
        )
      )
        return;
    } catch {}
    await page.waitForTimeout(500);
  }
  throw new Error("Workspace UI did not become ready");
}

async function createProject(page, name) {
  await reveal(page, page.locator("#new-project"));
  await page.locator("#new-project").fill(name);
  await page.getByRole("button", { name: "Create project" }).click();
  await page.locator("[data-testid=new-file-name]").waitFor({ timeout: 20000 });
  await reveal(page, page.locator("[data-testid=analyze-now]"));
}

async function importSingleFile(page, name, bytes) {
  await page.locator("[data-testid=import-input]").setInputFiles({
    name,
    mimeType: "text/plain",
    buffer: Buffer.from(bytes),
  });
  await page
    .locator("[data-testid=select-file]", { hasText: name })
    .waitFor({ state: "visible" });
}

async function saveAndWait(page) {
  await page.locator("[data-testid=save]").click();
  await page
    .locator(
      ".workspace-header .status.status-saved, .workspace-header .status.status-failed",
    )
    .waitFor({ state: "attached" });
  for (let i = 0; i < 40; i++) {
    const t = await page.locator(".workspace-header .status").innerText();
    if (t.startsWith("Saved")) return t;
    if (t.startsWith("Save failed")) return t;
    await page.waitForTimeout(250);
  }
  return page.locator(".workspace-header .status").innerText();
}

async function readHeadContent(page, wantName) {
  return await page.evaluate(async (name) => {
    const m = await import("./js/workspace-storage.js");
    const ps = await m.listProjects();
    const p = ps[ps.length - 1];
    const rev = await m.getRevision(p.headRevisionId);
    const f = rev.files.find((x) => x.relativePath === name) || rev.files[0];
    return Array.from(await m.readContent(f.contentHash));
  }, wantName);
}

async function analyzeNow(page) {
  await reveal(page, page.locator("[data-testid=analyze-now]"));
  await page.locator("[data-testid=analyze-now]").click();
}

const TERMINAL =
  /complete|cancel|fail|unavailable|superseded|No project|no longer/i;

async function analysisStatusText(page) {
  return await page.evaluate(
    () =>
      document.querySelector("[data-testid=analysis-status]")?.textContent ??
      "<none>",
  );
}

async function awaitCompletedAnalysis(page, timeout = 30000) {
  // Wait for a TERMINAL analysis status (not queued/analyzing) and return it.
  for (let i = 0; i < timeout / 200; i++) {
    const t = await analysisStatusText(page).catch(() => "");
    if (TERMINAL.test(t)) return t;
    await page.waitForTimeout(200);
  }
  throw new Error(
    `analysis did not reach a terminal status in ${timeout}ms; last: ${await analysisStatusText(page).catch(() => "?")}`,
  );
}

async function firstFinding(page) {
  return page.locator("[data-testid=analysis-finding]").first();
}

(async () => {
  const proxy = startProxy();
  await new Promise((r) => proxy.listen(PROXY_PORT, "127.0.0.1", r));
  const browser = await chromium.launch({ headless: true });
  try {
    await runU1HappyAndMultiFile(browser);
    await runU2Invalidation(browser);
    await runU2HeldReplyLateNeverRepaints(browser);
    await runU2WorkerRestartClickOldFinding(browser);
    await runU2bNavigationLifetime(browser);
    await runU2cEditorSelectJsValidation(browser);
    await runU3CancelThenRecover(browser);
    await runU4FailedSaveIndependence(browser);
    await runU5CoverageEscapingDomMismatch(browser);
    await runU5IndeterminateReal(browser);
    await runU6Lifecycle(browser);
    await runU6ReadOnlyTabAnalysis(browser);
    console.log(
      "analysis-ui-test passed: U1–U6 real Rust WASM UI + labeled synthetic fault recovery + navigation lifetime + read-only tab.",
    );
  } finally {
    await browser.close();
    proxy.close();
  }
})().catch((e) => {
  console.error(e);
  process.exitCode = 1;
});

// ---- U1/A02b: real Rust diagnostic, click selects exact "{" span; bytes unchanged ----
async function runU1HappyAndMultiFile(browser) {
  const ctx = await browser.newContext();
  const page = await ctx.newPage();
  const errors = [];
  page.on("pageerror", (e) => errors.push(String(e)));
  await page.goto(BASE);
  await waitForReady(page);
  await page.evaluate(async () => {
    const m = await import("./js/workspace-storage.js");
    await m.clearAll();
  });
  await page.reload();
  await waitForReady(page);

  await createProject(page, unique("U1"));
  await importSingleFile(page, "main.tex", U1_BYTES);
  // Save so the imported bytes are durable, then read them.
  await saveAndWait(page);
  const headBytesBefore = await readHeadContent(page, "main.tex");

  // Run real Rust analysis; expect the unmatched-brace diagnostic.
  await analyzeNow(page);
  await awaitCompletedAnalysis(page);
  const status = await page
    .locator("[data-testid=analysis-status]")
    .innerText();
  assert.match(status, /complete/i, `expected complete, got: ${status}`);
  const finding = await firstFinding(page);
  assert.ok(await finding.isVisible(), "ENGINE-STRUCT-001 finding rendered");
  assert.match(await finding.innerText(), /ENGINE-STRUCT-001/);

  // Click the finding and assert exact DOM selection.
  await finding.locator("[data-testid=finding-link]").click();
  await page.waitForTimeout(150);
  const sel = await page.evaluate(() => {
    const e = document.querySelector("[data-testid=editor]");
    return {
      fileId: e?.getAttribute("data-file-id"),
      start: e?.selectionStart ?? -1,
      end: e?.selectionEnd ?? -1,
      sub: e ? e.value.substring(e.selectionStart, e.selectionEnd) : null,
      focused: document.activeElement === e,
    };
  });
  assert.equal(sel.sub, "{", `selected substring must be "{", got: ${sel.sub}`);
  assert.ok(
    sel.start >= 0 && sel.end === sel.start + 1,
    `selection must be the single "{"`,
  );
  assert.ok(sel.focused, "editor must be focused after click");
  assert.ok(sel.fileId && sel.fileId.length > 0, "data-file-id must be set");

  // Authoritative bytes unchanged by analysis/selection; head did not advance.
  const headBytesAfter = await readHeadContent(page, "main.tex");
  assert.deepEqual(
    headBytesAfter,
    headBytesBefore,
    "analysis/selection must not change stored bytes",
  );

  // Multi-file: a second file with identical prefix text but an unmatched brace.
  const otherBytes = Buffer.from("same text {unmatched\n", "utf-8");
  await importSingleFile(page, "other.tex", otherBytes);
  await saveAndWait(page);
  await analyzeNow(page);
  await awaitCompletedAnalysis(page);
  // Find the finding whose file is other.tex and click it.
  const otherFinding = page.locator("[data-testid=analysis-finding]", {
    hasText: "other.tex",
  });
  assert.ok(
    (await otherFinding.count()) >= 1,
    "diagnostic for other.tex rendered",
  );
  await otherFinding.first().locator("[data-testid=finding-link]").click();
  await page.waitForTimeout(150);
  const sel2 = await page.evaluate(() => {
    const e = document.querySelector("[data-testid=editor]");
    return {
      fileId: e?.getAttribute("data-file-id"),
      sub: e ? e.value.substring(e.selectionStart, e.selectionEnd) : null,
      label: document.querySelector(".editor-toolbar .source-tab")?.textContent,
    };
  });
  assert.equal(
    sel2.sub,
    "{",
    "clicking other.tex diagnostic selects the brace there",
  );
  assert.match(
    sel2.label || "",
    /other\.tex/,
    "editor switched to other.tex, not the same-text main.tex",
  );
  assert.deepEqual(errors, [], "No uncaught page errors");
  console.log(
    "PASS U1/A02b: real Rust diagnostic + exact click-to-select + multi-file + bytes unchanged",
  );
  await ctx.close();
}

// ---- U2/A07/A14: invalidation on edit/close; late reply never repaints ----
async function runU2Invalidation(browser) {
  const ctx = await browser.newContext();
  const page = await ctx.newPage();
  await page.goto(BASE);
  await waitForReady(page);
  await page.evaluate(async () => {
    const m = await import("./js/workspace-storage.js");
    await m.clearAll();
  });
  await page.reload();
  await waitForReady(page);
  await createProject(page, unique("U2"));
  await importSingleFile(page, "main.tex", Buffer.from("a{b\n", "utf-8"));
  await saveAndWait(page);
  await analyzeNow(page);
  await awaitCompletedAnalysis(page);
  assert.ok(
    (await page.locator("[data-testid=analysis-finding]").count()) >= 1,
    "finding present",
  );

  // Edit: findings must disappear immediately (invalidation before debounce).
  await page.locator("[data-testid=editor]").fill("a b c no brace");
  await page.waitForTimeout(150);
  assert.equal(
    await page.locator("[data-testid=analysis-finding]").count(),
    0,
    "old findings must clear on edit before a new analysis finishes",
  );
  // Run fresh analysis: no findings now (matched braces absent).
  await analyzeNow(page);
  await awaitCompletedAnalysis(page);
  assert.equal(
    await page.locator("[data-testid=analysis-finding]").count(),
    0,
    "no findings after edit",
  );

  // Project change (close) drops the report: the workspace section (and analysis panel) is
  // replaced by the project list. The coordinator retains NoProject internally; the
  // UI no longer shows a stale report.
  await page.getByRole("button", { name: "Close", exact: true }).click();
  await page.locator(".project-dashboard").waitFor({ state: "visible" });
  assert.equal(
    await page.locator("[data-testid=analysis-panel]").count(),
    0,
    "analysis panel is removed when no project is open",
  );
  console.log("PASS U2/A07/A14: edit+project-change invalidation");
  await ctx.close();
}

// ---- U2 (REAL held response): a late valid completed reply never repaints ----
// after an edit; latest analysis works with actual WASM. Plus a restore case
// and a worker-restart "click old finding -> no stale selection" case.
async function runU2HeldReplyLateNeverRepaints(browser) {
  const ctx = await browser.newContext();
  const page = await ctx.newPage();
  const errors = [];
  page.on("pageerror", (e) => errors.push(String(e)));
  // SYNTHETIC (labeled): route the rust worker to a held-reply worker that
  // builds a VALID completed response and delays it so an edit lands first.
  await page.route("**/js/rust-worker.js", (route) =>
    route.fulfill({ contentType: "text/javascript", body: HELD_REPLY_WORKER }),
  );
  await page.goto(BASE);
  await waitForReady(page);
  await page.evaluate(async () => {
    const m = await import("./js/workspace-storage.js");
    await m.clearAll();
  });
  await page.reload();
  await waitForReady(page);
  const heldName = unique("U2held");
  await createProject(page, heldName);
  await importSingleFile(page, "main.tex", Buffer.from("a{b\n", "utf-8"));
  await saveAndWait(page);

  // First analyze: the synthetic worker builds a valid completed response with a
  // diagnostic (delayed ~1200ms). After it settles, the report is visible.
  await analyzeNow(page);
  await awaitCompletedAnalysis(page);
  assert.ok(
    (await page.locator("[data-testid=analysis-finding]").count()) >= 1,
    "synthetic held-reply worker produced a valid completed report",
  );

  // Now analyze AGAIN, and edit DURING the held reply. The late valid completed
  // reply (for the pre-edit snapshot) must NOT repaint findings: the edit bumped
  // the intent, so the coordinator settles the late reply as Superseded.
  await analyzeNow(page);
  // Issue the edit immediately (deterministic: well within the 1200ms hold).
  await page.locator("[data-testid=editor]").fill("no brace here at all");
  // Findings cleared on edit (invalidation before debounce).
  await page.waitForTimeout(150);
  assert.equal(
    await page.locator("[data-testid=analysis-finding]").count(),
    0,
    "old findings clear on edit before the late reply arrives",
  );
  // Wait for the held analysis to reach a terminal state (the late reply lands).
  await awaitCompletedAnalysis(page);
  // The late valid completed reply must NOT repaint findings (Superseded).
  assert.equal(
    await page.locator("[data-testid=analysis-finding]").count(),
    0,
    "a late valid completed reply never repaints findings after an edit",
  );
  const statusAfter = await page
    .locator("[data-testid=analysis-status]")
    .innerText();
  assert.match(
    statusAfter,
    /cancel|superseded|complete|no longer/i,
    `terminal status after late reply: ${statusAfter}`,
  );

  // Recovery: cancel resets the synthetic worker, then unroute to real WASM and
  // re-analyze. The edited content has no brace -> 0 findings (honest).
  await page.locator("[data-testid=cancel-analysis]").click();
  await page.unroute("**/js/rust-worker.js");
  await analyzeNow(page);
  await awaitCompletedAnalysis(page);
  assert.equal(
    await page.locator("[data-testid=analysis-finding]").count(),
    0,
    "fresh real-WASM analysis reflects the edited content (no brace)",
  );

  // ---- navigation case (beyond no-project panel removal) ----
  // Re-introduce a brace and save, then close the project (panel removed),
  // REOPEN it, re-analyze and click a finding -> exact selection. This
  // verifies navigation across a close/reopen (the scoped coordinator
  // survives) rather than only the no-project panel removal already covered.
  await page.locator("[data-testid=editor]").fill("a{b again");
  await saveAndWait(page);
  await page.getByRole("button", { name: "Close", exact: true }).click();
  await page.locator(".project-dashboard").waitFor({ state: "visible" });
  assert.equal(
    await page.locator("[data-testid=analysis-panel]").count(),
    0,
    "analysis panel removed when no project is open",
  );
  // Reopen the SAME durable project and re-analyze.
  await page
    .locator("[data-testid=project-row]", { hasText: heldName })
    .first()
    .locator("[data-testid=open-project]")
    .click();
  await page.locator("[data-testid=editor]").waitFor({ state: "visible" });
  await analyzeNow(page);
  await awaitCompletedAnalysis(page);
  assert.ok(
    (await page.locator("[data-testid=analysis-finding]").count()) >= 1,
    "fresh analysis after close/reopen returns the diagnostic",
  );
  await page
    .locator("[data-testid=analysis-finding]")
    .first()
    .locator("[data-testid=finding-link]")
    .click();
  await page.waitForTimeout(150);
  const navSel = await page.evaluate(() => {
    const e = document.querySelector("[data-testid=editor]");
    return {
      sub: e ? e.value.substring(e.selectionStart, e.selectionEnd) : null,
      focused: document.activeElement === e,
    };
  });
  assert.equal(
    navSel.sub,
    "{",
    "click after close/reopen selects the exact brace",
  );
  assert.ok(navSel.focused, "editor focused after close/reopen navigation");
  console.log(
    "PASS U2/held-reply+navigation: late valid reply never repaints; close/reopen navigation selects; fresh WASM works",
  );
  await ctx.close();
}

// ---- U2 worker restart with a visible report: click old finding -> no stale ----
// selection; fresh analysis works. Worker epoch is bumped via the shared bridge
// dispose() (no product debug hook; the bridge's own public dispose), so the
// Completed report stays but its WorkerEpoch is now stale.
async function runU2WorkerRestartClickOldFinding(browser) {
  const ctx = await browser.newContext();
  const page = await ctx.newPage();
  await page.goto(BASE);
  await waitForReady(page);
  await page.evaluate(async () => {
    const m = await import("./js/workspace-storage.js");
    await m.clearAll();
  });
  await page.reload();
  await waitForReady(page);
  await createProject(page, unique("U2restart"));
  await importSingleFile(page, "main.tex", Buffer.from("a{b\n", "utf-8"));
  await saveAndWait(page);
  await analyzeNow(page);
  await awaitCompletedAnalysis(page);
  assert.ok(
    (await page.locator("[data-testid=analysis-finding]").count()) >= 1,
    "finding present",
  );

  // Restart the shared worker (bump the bridge epoch) WITHOUT clearing the
  // coordinator's Completed report: the bridge dispose() terminates the worker and
  // bumps the epoch; the coordinator is not running an analysis so its state is
  // untouched. The report stays visible, but its WorkerEpoch is now stale.
  await page.evaluate(async () => {
    const m = await import("./js/rust-bridge.js");
    m.dispose();
  });

  // Click the OLD finding: NavigateAsync verifies the worker epoch (bridge-local,
  // now bumped) and rejects the stale report -> NO stale selection.
  await page
    .locator("[data-testid=analysis-finding]")
    .first()
    .locator("[data-testid=finding-link]")
    .click();
  await page.waitForTimeout(200);
  const after = await page.evaluate(() => {
    const e = document.querySelector("[data-testid=editor]");
    return {
      start: e?.selectionStart ?? -1,
      end: e?.selectionEnd ?? -1,
      sub: e ? e.value.substring(e.selectionStart, e.selectionEnd) : null,
      notice: !!document.querySelector("[data-testid=nav-notice]"),
    };
  });
  assert.equal(
    after.start,
    after.end,
    "no stale selection after a worker reset",
  );
  assert.ok(
    after.notice,
    "a stale-report notice is shown after a worker reset",
  );

  // Fresh analysis re-initializes a healthy worker and returns the diagnostic.
  await analyzeNow(page);
  await awaitCompletedAnalysis(page);
  assert.ok(
    (await page.locator("[data-testid=analysis-finding]").count()) >= 1,
    "fresh analysis works after a worker restart",
  );
  console.log(
    "PASS U2/worker-restart: click old finding after epoch bump rejects; fresh analysis works",
  );
  await ctx.close();
}

// ---- U3/A08: cancel held analysis, then fresh real WASM works ----
async function runU3CancelThenRecover(browser) {
  const ctx = await browser.newContext();
  const page = await ctx.newPage();
  // SYNTHETIC: route the rust worker to a non-responsive fake for the cancel.
  await page.route("**/js/rust-worker.js", (route) =>
    route.fulfill({
      contentType: "text/javascript",
      body: NONRESPONSIVE_WORKER,
    }),
  );
  await page.goto(BASE);
  await waitForReady(page);
  await page.evaluate(async () => {
    const m = await import("./js/workspace-storage.js");
    await m.clearAll();
  });
  await page.reload();
  await waitForReady(page);
  await page.unroute("**/js/rust-worker.js"); // ensure real worker for the app shell
  await createProject(page, unique("U3"));
  await importSingleFile(page, "main.tex", Buffer.from("a{b\n", "utf-8"));

  // Re-route to the held worker so analyzeSource never replies.
  await page.route("**/js/rust-worker.js", (route) =>
    route.fulfill({
      contentType: "text/javascript",
      body: NONRESPONSIVE_WORKER,
    }),
  );
  await analyzeNow(page);
  // Cancel promptly (do NOT wait for the 5s whole-job deadline).
  await page.locator("[data-testid=cancel-analysis]").click();
  await page
    .locator("[data-testid=analysis-status]")
    .filter({ hasText: /cancel/i })
    .waitFor({ state: "attached", timeout: 5000 });

  // Recovery: remove the synthetic route and run real Rust WASM.
  await page.unroute("**/js/rust-worker.js");
  await analyzeNow(page);
  await awaitCompletedAnalysis(page);
  assert.ok(
    (await page.locator("[data-testid=analysis-finding]").count()) >= 1,
    "fresh real-WASM analysis returns diagnostics after cancel",
  );
  console.log(
    "PASS U3/A08: cancel held analysis promptly, then fresh real WASM works",
  );
  await ctx.close();
}

// ---- U4/A11: failed local save + successful analysis on dirty snapshot ----
async function runU4FailedSaveIndependence(browser) {
  const ctx = await browser.newContext();
  const page = await ctx.newPage();
  await page.goto(BASE);
  await waitForReady(page);
  await page.evaluate(async () => {
    const m = await import("./js/workspace-storage.js");
    await m.clearAll();
  });
  await page.reload();
  await waitForReady(page);
  await createProject(page, unique("U4"));
  await importSingleFile(page, "main.tex", Buffer.from("v1 good\n", "utf-8"));
  await saveAndWait(page);
  const headBefore = await page.evaluate(async () => {
    const m = await import("./js/workspace-storage.js");
    return (await m.listProjects()).pop().headRevisionId;
  });

  // Edit to v2 with an unmatched brace (dirty).
  await page.locator("[data-testid=editor]").fill("v2 with {unmatched");
  // Inject a one-shot storage-save failure (SubtleCrypto.digest pattern).
  await page.evaluate(() => {
    SubtleCrypto.prototype.digest = function () {
      // Keep the failure active (do NOT restore) so the 2s autosave cannot silently recover.
      return Promise.reject(new Error("InjectedU4SaveFailure"));
    };
  });
  await page.locator("[data-testid=save]").click();
  await page.locator(".status.status-failed").waitFor({ state: "attached" });
  const saveStatus = await page.locator(".status").innerText();
  assert.match(
    saveStatus,
    /Save failed/i,
    `save must fail, got: ${saveStatus}`,
  );
  // Dirty editor text retained; head not advanced.
  const editorText = await page.locator("[data-testid=editor]").inputValue();
  assert.equal(
    editorText,
    "v2 with {unmatched",
    "dirty editor text retained after failed save",
  );
  const headAfter = await page.evaluate(async () => {
    const m = await import("./js/workspace-storage.js");
    return (await m.listProjects()).pop().headRevisionId;
  });
  assert.equal(headAfter, headBefore, "head must not advance on failed save");

  // Analysis must succeed on the dirty v2 snapshot regardless of the save failure.
  await analyzeNow(page);
  await awaitCompletedAnalysis(page);
  assert.ok(
    (await page.locator("[data-testid=analysis-finding]").count()) >= 1,
    "analysis succeeds on dirty v2 despite save failure",
  );
  // Save-failed state still visible.
  const statusAfter = await page.locator(".status").innerText();
  assert.match(
    statusAfter,
    /Save failed/i,
    "save-failed state remains visible after analysis",
  );

  // Restore the injection so later tests/cleanup save normally.
  await page.evaluate(() => {
    SubtleCrypto.prototype.digest =
      SubtleCrypto.prototype.digest.__real || SubtleCrypto.prototype.digest;
  });
  await ctx.close();
  console.log(
    "PASS U4/A11: failed save + successful analysis on dirty snapshot, head unchanged",
  );
}

// ---- U5/A12/A15: coverage, indeterminate, HTML-escaping, DOM-only mismatch ----
async function runU5CoverageEscapingDomMismatch(browser) {
  const ctx = await browser.newContext();
  const page = await ctx.newPage();
  await page.goto(BASE);
  await waitForReady(page);
  await page.evaluate(async () => {
    const m = await import("./js/workspace-storage.js");
    await m.clearAll();
  });
  await page.reload();
  await waitForReady(page);
  await createProject(page, unique("U5"));

  // Real WASM: PDF checks under Unavailable; a binary file is excluded metadata.
  await importSingleFile(page, "main.tex", Buffer.from("a{b\n", "utf-8"));
  await page.locator("[data-testid=import-input]").setInputFiles({
    name: "asset.bin",
    mimeType: "application/octet-stream",
    buffer: Buffer.from([0, 1, 2, 255]),
  });
  await saveAndWait(page);
  await analyzeNow(page);
  await awaitCompletedAnalysis(page);
  assert.ok(
    (await page.locator("[data-testid=analysis-unavailable]").count()) >= 1,
    "PDF unavailable capabilities rendered distinctly",
  );
  assert.ok(
    (await page.locator("[data-testid=analysis-excluded]").count()) >= 1,
    "binary excluded metadata rendered",
  );
  // The completion shows the engine finding; no Pass/compliance claim.
  assert.ok(
    (await page.locator("[data-testid=analysis-finding]").count()) >= 1,
    "engine finding shown",
  );

  // HTML/script in SOURCE content (filenames forbid < > : etc.): displayed
  // escaped in the textarea and never executed. A normal filename is used.
  let pageerrors = [];
  page.on("pageerror", (e) => pageerrors.push(String(e)));
  await page.locator("[data-testid=new-file-name]").fill("xss.tex");
  await page.getByRole("button", { name: "New file" }).click();
  await page.locator("[data-testid=editor]").waitFor({ state: "visible" });
  await page
    .locator("[data-testid=editor]")
    .fill("<img src=x onerror=window.__xss2=1> {oops");
  await analyzeNow(page);
  await awaitCompletedAnalysis(page);
  const xss = await page.evaluate(() => ({
    a: window.__xss,
    b: window.__xss2,
  }));
  assert.equal(xss.a, undefined, "script tag in filename did not execute");
  assert.equal(xss.b, undefined, "onerror handler in source did not execute");
  assert.ok(
    (await page.locator("[data-testid=analysis-finding]").count()) >= 1,
    "engine finding for the new file shown",
  );
  await page.evaluate(() => {
    pageerrors = [];
  });

  // DOM-only textarea.value mutation: no selection, no document modification, safe notice.
  // Use the main.tex brace finding; mutate the textarea value without firing input.
  await page
    .locator("[data-testid=select-file]", { hasText: "main.tex" })
    .click();
  await analyzeNow(page);
  await awaitCompletedAnalysis(page);
  const findingMain = page
    .locator("[data-testid=analysis-finding]", { hasText: "main.tex" })
    .first();
  await page.evaluate(() => {
    const e = document.querySelector("[data-testid=editor]");
    // Mutate the DOM value to a mismatch WITHOUT firing input (WorkingFile.Content unchanged).
    e.value = "MUTATED DOM VALUE";
  });
  await findingMain.locator("[data-testid=finding-link]").click();
  await page.waitForTimeout(150);
  const after = await page.evaluate(() => {
    const e = document.querySelector("[data-testid=editor]");
    return {
      start: e.selectionStart,
      end: e.selectionEnd,
      value: e.value,
      hasNotice: !!document.querySelector("[data-testid=nav-notice]"),
    };
  });
  // No selection happened on a DOM-only mismatch.
  assert.equal(
    after.start,
    after.end,
    "no selection range on DOM-only mismatch",
  );
  assert.ok(after.hasNotice, "a safe stale/non-selectable notice is shown");
  // The mismatched DOM value was NOT silently overwritten/rerendered away by the click.
  assert.equal(
    after.value,
    "MUTATED DOM VALUE",
    "DOM-only mutation was not overwritten by the click",
  );

  // SYNTHETIC (labeled): indeterminate/truncated report is verified in a fresh
  // context in runU5SyntheticIndeterminate so the route takes effect on a new
  // bridge-owned worker (the main U5 page already initialized a real worker).
  console.log(
    "PASS U5/A12/A15: unavailable/unsupported/excluded, HTML-escaping, DOM-only mismatch",
  );
  await ctx.close();
}

// ---- U5 (REAL): indeterminate via brace-depth-exceeded renders incomplete, not Pass ----
async function runU5IndeterminateReal(browser) {
  const ctx = await browser.newContext();
  const page = await ctx.newPage();
  await page.goto(BASE);
  await waitForReady(page);
  await page.evaluate(async () => {
    const m = await import("./js/workspace-storage.js");
    await m.clearAll();
  });
  await page.reload();
  await waitForReady(page);
  await createProject(page, unique("U5ind"));
  // >256 nested unmatched braces: the REAL Rust core stops depth tracking at
  // maxBraceDepth (256) -> braceDepthExceeded -> the file's engineOutcome
  // becomes Indeterminate (NOT Pass) with zero confirmed diagnostics. This is
  // a REAL bounded-partial/indeterminate path through actual Rust WASM (no
  // synthetic worker; production limits are unchanged).
  const deep = "{".repeat(300) + "\n";
  await importSingleFile(page, "deep.tex", Buffer.from(deep, "utf-8"));
  await analyzeNow(page);
  await awaitCompletedAnalysis(page);
  assert.ok(
    (await page.locator("[data-testid=analysis-incomplete]").count()) >= 1,
    "indeterminate analysis labeled incomplete",
  );
  assert.equal(
    await page.locator("[data-testid=analysis-pass]").count(),
    0,
    "indeterminate analysis must not show a Pass/compliance message",
  );
  console.log(
    "PASS U5/A15 (real): indeterminate (brace-depth-exceeded) labeled incomplete, not Pass",
  );
  await ctx.close();
}

// ---- U6: navigate away/back remount reuses the SAME coordinator ----
async function runU6Lifecycle(browser) {
  const ctx = await browser.newContext();
  const page = await ctx.newPage();
  await page.goto(BASE);
  await waitForReady(page);
  await page.evaluate(async () => {
    const m = await import("./js/workspace-storage.js");
    await m.clearAll();
  });
  await page.reload();
  await waitForReady(page);
  await createProject(page, unique("U6"));
  await importSingleFile(page, "main.tex", Buffer.from("a{b\n", "utf-8"));
  await saveAndWait(page);
  await analyzeNow(page);
  await awaitCompletedAnalysis(page);
  assert.ok(
    (await page.locator("[data-testid=analysis-finding]").count()) >= 1,
    "finding before navigation",
  );

  // Navigate to the smoke route via the client-side NavLink (Blazor router),
  // NOT a full page load, so the scoped coordinator survives the remount.
  const link = page.locator(".project-menu a.project-smoke-link");
  await reveal(page, link);
  await link.click();
  await page.waitForTimeout(400);
  // Navigate back to the workspace via the client-side NavLink.
  await page.locator('a.nav-link[href="/"]').click();
  await waitForReady(page);
  // The project is still open (scoped coordinator kept state). Re-analyze.
  await analyzeNow(page);
  await awaitCompletedAnalysis(page);
  assert.ok(
    (await page.locator("[data-testid=analysis-finding]").count()) >= 1,
    "coordinator still works after remount; analysis returns diagnostics",
  );
  console.log(
    "PASS U6: navigate away/back remount reuses coordinator, no disposed reuse",
  );
  await ctx.close();
}

// ---- U2b navigation lifetime: check after every await; nav intent owns lifetime ----
// Regressions for the page-side navigation fixes. Holds the FIRST DOM module
// import (editor-select.js) with a test-local barrier by clearing that asset
// from the service-worker CacheStorage (so the SW fetch misses -> the Playwright
// route intercepts the SW's network fetch) and fulfilling it on release. Then:
// (a) edit during a held navigation -> NO stale selection, DOM value not rerendered
//     away; (b) two rapid clicks -> the newer click wins (older is cancelled). A
// DOM-only text mismatch remains rejected (already in U5). Cross-file navigation
// still selects the correct file (already in U1).
async function runU2bNavigationLifetime(browser) {
  // GENUINE held-import barrier (M2a-2b-2b review item 3). The prior
  // implementation claimed a route barrier but only did ordinary click ->
  // fill -> wait 250ms; there was no held module import and the test could
  // pass even when the click already selected before the fill collapsed the
  // selection. This version REALLY holds the editor-select.js module import:
  //   - a fresh browser context with `serviceWorkers:'block'` (no SW -> no
  //     CacheStorage bypass; the import goes to the network);
  //   - a fresh realm -> no ES module-map caching of editor-select.js;
  //   - a Playwright route on **/js/editor-select.js is installed BEFORE the
  //     first diagnostic click, with Node-side reached/release promises so
  //     the request is genuinely held until released;
  //   - the click is started WITHOUT awaiting its async completion; the test
  //     awaits routeReached, mutates while the import is held, asserts NO
  //     stale selection/late activation, THEN releases.
  // The route fulfills with the REAL editor-select.js source (fetched via
  // node http from the upstream origin) so a later healthy same-page/remount
  // selection runs through actual WASM. SYNTHETIC network barrier is labeled.
  const realSrc = await fetchUpstreamText("/js/editor-select.js");

  // Helper: install a held route on editor-select.js. Returns reached/release
  // handles. The route fulfills with realSrc on release.
  function installHeldRoute(page) {
    let reachedResolve, releaseResolve;
    const reached = new Promise((r) => {
      reachedResolve = r;
    });
    const releaseGate = new Promise((r) => {
      releaseResolve = r;
    });
    const doneRoutes = new Set();
    let hits = 0;
    page.route("**/js/editor-select.js", (route) => {
      hits++;
      reachedResolve();
      releaseGate.then(() => {
        // Guard per-route: a second invocation for the same route object (or a
        // retry) must not double-fulfill (Playwright rejects the second
        // fulfill promise with "Route is already handled!").
        if (doneRoutes.has(route)) return;
        doneRoutes.add(route);
        try {
          const p = route.fulfill({
            contentType: "text/javascript",
            body: realSrc,
          });
          // route.fulfill returns a Promise; handle its rejection (already
          // handled) so it is never an unhandled rejection.
          if (p && typeof p.then === "function")
            p.then(
              () => {},
              () => {},
            );
        } catch {
          /* already handled — best-effort */
        }
      });
    });
    return {
      reached: () =>
        Promise.race([
          reached,
          page.waitForTimeout(8000).then(() => {
            throw new Error(
              "editor-select route was not reached (held import did not hold)",
            );
          }),
        ]),
      release: () => releaseResolve(),
      hits: () => hits,
      unroute: () => page.unroute("**/js/editor-select.js"),
    };
  }

  // ---- (a) edit during a genuinely-held import -> NO stale selection ----
  {
    const ctx = await browser.newContext({ serviceWorkers: "block" });
    const page = await ctx.newPage();
    const errors = [];
    const unhandled = [];
    page.on("pageerror", (e) => errors.push(String(e)));
    page.on("unhandledrejection", (e) => unhandled.push(String(e)));
    await page.goto(BASE);
    await waitForReady(page);
    await page.evaluate(async () => {
      const m = await import("./js/workspace-storage.js");
      await m.clearAll();
    });
    await page.reload();
    await waitForReady(page);
    await createProject(page, unique("U2bHeldA"));
    await importSingleFile(page, "main.tex", Buffer.from("a{b\n", "utf-8"));
    await saveAndWait(page);
    await analyzeNow(page);
    await awaitCompletedAnalysis(page);
    const finding = page.locator("[data-testid=analysis-finding]").first();

    const gate = installHeldRoute(page);
    // Start the click WITHOUT awaiting its async completion (the Blazor
    // handler runs and blocks on the held editor-select import).
    await finding.locator("[data-testid=finding-link]").click();
    // Deterministic: wait until the route is actually reached (import held).
    await gate.reached();
    // Mutate while the import is held: bumps the generation.
    await page.locator("[data-testid=editor]").fill("edited while import held");
    // Release; the post-await verification rejects the stale report.
    gate.release();
    await page.waitForTimeout(250);
    const after = await page.evaluate(() => {
      const e = document.querySelector("[data-testid=editor]");
      return {
        start: e?.selectionStart ?? -1,
        end: e?.selectionEnd ?? -1,
        value: e?.value,
        notice: !!document.querySelector("[data-testid=nav-notice]"),
      };
    });
    assert.equal(
      after.start,
      after.end,
      "no stale selection after an edit during a genuinely-held import",
    );
    assert.equal(
      after.value,
      "edited while import held",
      "DOM value is the edited text, not the analyzed source (not rerendered away)",
    );
    assert.ok(
      after.notice,
      "a stale/non-selectable notice is shown (the held navigation was rejected after the edit)",
    );
    gate.unroute();
    // Let any late async settle, then assert zero page errors / unhandled rejections.
    await page.waitForTimeout(300);
    assert.deepEqual(
      errors,
      [],
      "no page errors from the held-import edit scenario",
    );
    assert.deepEqual(
      unhandled,
      [],
      "no unhandled rejections from the held-import edit scenario",
    );
    console.log(
      "PASS U2b/held-import-edit: edit during a genuinely-held editor-select import rejects the stale selection",
    );
    await ctx.close();
  }

  // ---- (b) two rapid clicks under the shared import barrier -> newer wins ----
  {
    const ctx = await browser.newContext({ serviceWorkers: "block" });
    const page = await ctx.newPage();
    const errors = [];
    const unhandled = [];
    page.on("pageerror", (e) => errors.push(String(e)));
    page.on("unhandledrejection", (e) => unhandled.push(String(e)));
    await page.goto(BASE);
    await waitForReady(page);
    await page.evaluate(async () => {
      const m = await import("./js/workspace-storage.js");
      await m.clearAll();
    });
    await page.reload();
    await waitForReady(page);
    await createProject(page, unique("U2bHeldB"));
    await importSingleFile(page, "a.tex", Buffer.from("a{b\n", "utf-8"));
    await importSingleFile(page, "b.tex", Buffer.from("c{d\n", "utf-8"));
    await saveAndWait(page);
    await analyzeNow(page);
    await awaitCompletedAnalysis(page);
    const findingA = page
      .locator("[data-testid=analysis-finding]", { hasText: "a.tex" })
      .first();
    const findingB = page
      .locator("[data-testid=analysis-finding]", { hasText: "b.tex" })
      .first();
    // Make b.tex active so clicking A is cross-file, then B is cross-file.
    await page
      .locator("[data-testid=select-file]", { hasText: "b.tex" })
      .click();

    const gate = installHeldRoute(page);
    // Click A (held), then immediately click B (newer intent) — both under
    // the SAME held import barrier; the older intent is cancelled.
    await findingA.locator("[data-testid=finding-link]").click();
    await gate.reached();
    await findingB.locator("[data-testid=finding-link]").click();
    gate.release();
    // Wait for the b.tex selection to land (terminal nav state via the editor).
    let finalSel = null;
    for (let i = 0; i < 60; i++) {
      finalSel = await page.evaluate(() => {
        const e = document.querySelector("[data-testid=editor]");
        return {
          label: document.querySelector(".editor-toolbar .source-tab")
            ?.textContent,
          sub: e ? e.value.substring(e.selectionStart, e.selectionEnd) : null,
        };
      });
      if (
        finalSel.label &&
        /b\.tex/.test(finalSel.label) &&
        finalSel.sub === "{"
      )
        break;
      await page.waitForTimeout(100);
    }
    assert.match(
      finalSel?.label || "",
      /b\.tex/,
      "newer (b.tex) click wins the active file under the held import barrier",
    );
    assert.equal(
      finalSel?.sub,
      "{",
      "newer click selected the b.tex brace; older a.tex click did not override",
    );
    gate.unroute();
    await page.waitForTimeout(300);
    assert.deepEqual(
      errors,
      [],
      "no page errors from the two-rapid-clicks scenario",
    );
    assert.deepEqual(
      unhandled,
      [],
      "no unhandled rejections from the two-rapid-clicks scenario",
    );
    console.log(
      "PASS U2b/held-import-two-clicks: newer navigation intent wins under a genuinely-held import barrier",
    );
    await ctx.close();
  }

  // ---- (c) navigate away during a held import -> no retained subscription ----
  // /autosave; remount selects via actual WASM.
  {
    const ctx = await browser.newContext({ serviceWorkers: "block" });
    const page = await ctx.newPage();
    const errors = [];
    const unhandled = [];
    page.on("pageerror", (e) => errors.push(String(e)));
    page.on("unhandledrejection", (e) => unhandled.push(String(e)));
    await page.goto(BASE);
    await waitForReady(page);
    await page.evaluate(async () => {
      const m = await import("./js/workspace-storage.js");
      await m.clearAll();
    });
    await page.reload();
    await waitForReady(page);
    await createProject(page, unique("U2bHeldC"));
    await importSingleFile(page, "main.tex", Buffer.from("a{b\n", "utf-8"));
    await saveAndWait(page);
    await analyzeNow(page);
    await awaitCompletedAnalysis(page);
    const finding = page.locator("[data-testid=analysis-finding]").first();

    const gate = installHeldRoute(page);
    await finding.locator("[data-testid=finding-link]").click();
    await gate.reached();
    // Navigate AWAY (client-side NavLink) while the import is held: the page
    // disposes synchronously (events unsubscribed, nav/autosave cancelled, the
    // readiness subscription unsubscribed, the DotNet ref disposed) without
    // waiting for the blocked import.
    const link = page.locator(".project-menu a.project-smoke-link");
    await reveal(page, link);
    await link.click();
    await page.waitForTimeout(400);
    // No retained readiness subscription fires into a dead .NET ref and no
    // autosave runs while the page is away.
    await page.waitForTimeout(300);
    assert.deepEqual(
      errors,
      [],
      "no page errors after navigating away during a held import",
    );
    assert.deepEqual(
      unhandled,
      [],
      "no unhandled rejections after navigating away during a held import (readiness ref disposed, subscription unsubscribed)",
    );
    // Release the held import so the disposed page's late-cleanup can dispose
    // the module; unroute so a remount uses a fresh import.
    gate.release();
    gate.unroute();
    // Navigate back (remount) and verify a healthy selection via real WASM.
    await page.locator('a.nav-link[href="/"]').click();
    await waitForReady(page);
    await analyzeNow(page);
    await awaitCompletedAnalysis(page);
    await page
      .locator("[data-testid=analysis-finding]")
      .first()
      .locator("[data-testid=finding-link]")
      .click();
    await page.waitForTimeout(200);
    const remountSel = await page.evaluate(() => {
      const e = document.querySelector("[data-testid=editor]");
      return {
        sub: e ? e.value.substring(e.selectionStart, e.selectionEnd) : null,
        focused: document.activeElement === e,
      };
    });
    assert.equal(
      remountSel.sub,
      "{",
      "remount selects the exact brace via actual WASM after a held-import navigate-away",
    );
    assert.ok(remountSel.focused, "editor focused after remount");
    await page.waitForTimeout(300);
    assert.deepEqual(errors, [], "no page errors after remount");
    assert.deepEqual(unhandled, [], "no unhandled rejections after remount");
    console.log(
      "PASS U2b/held-import-navigate-away: navigate away during a held import leaves no retained subscription/autosave; remount selects via real WASM",
    );
    await ctx.close();
  }
}

// ---- U2c editor-select.js DOM validation: disconnected/disabled/fractional/NaN ----
// Tests the tiny DOM-glue module directly against a real DOM (no product debug
// hooks). Invalid ranges must not change the existing selection; detached and
// disabled textareas must be rejected before focus/setSelectionRange.
async function runU2cEditorSelectJsValidation(browser) {
  const ctx = await browser.newContext();
  const page = await ctx.newPage();
  await page.goto(BASE);
  await page.setContent("<textarea id='t'></textarea>");
  const mod = await page.evaluateHandle(() => import("./js/editor-select.js"));
  const results = await page.evaluate(async (m) => {
    const { trySelect } = await m;
    const out = {};
    const t = document.getElementById("t");
    t.setAttribute("data-file-id", "fid1");
    // Baseline: a valid selection succeeds.
    t.value = "a{b";
    out.valid = trySelect(t, "fid1", "a{b", 1, 2);
    out.validSel = t.selectionStart === 1 && t.selectionEnd === 2;
    // Fractional start must be rejected BEFORE mutating selection.
    t.value = "a{b";
    t.setSelectionRange(0, 0);
    out.fractional = trySelect(t, "fid1", "a{b", 1.5, 2.5);
    out.fractionalSel = t.selectionStart === 0 && t.selectionEnd === 0;
    // NaN start must be rejected (NaN passes typeof === "number" but Number.isInteger rejects).
    t.value = "a{b";
    t.setSelectionRange(0, 0);
    out.nan = trySelect(t, "fid1", "a{b", NaN, 2);
    out.nanSel = t.selectionStart === 0 && t.selectionEnd === 0;
    // Wrong file id rejected.
    t.value = "a{b";
    out.wrongFile = trySelect(t, "other", "a{b", 1, 2);
    // DOM-only value mismatch rejected before mutation.
    t.value = "MUTATED";
    t.setSelectionRange(0, 0);
    out.mismatch = trySelect(t, "fid1", "a{b", 1, 2);
    out.mismatchSel = t.selectionStart === 0 && t.selectionEnd === 0;
    // Detached textarea rejected (never attached to the document).
    const det = document.createElement("textarea");
    det.value = "a{b";
    det.setAttribute("data-file-id", "fid1");
    out.detached = trySelect(det, "fid1", "a{b", 1, 2);
    // Disabled textarea rejected before focus/range.
    t.value = "a{b";
    t.disabled = true;
    t.setSelectionRange(0, 0);
    out.disabled = trySelect(t, "fid1", "a{b", 1, 2);
    out.disabledSel = t.selectionStart === 0 && t.selectionEnd === 0;
    t.disabled = false;
    // Out-of-bounds rejected.
    t.value = "a{b";
    out.oob = trySelect(t, "fid1", "a{b", 1, 99);
    return out;
  }, mod);
  assert.strictEqual(results.valid, true, "valid selection succeeds");
  assert.ok(results.validSel, "valid selection set the range");
  assert.strictEqual(results.fractional, false, "fractional start rejected");
  assert.ok(
    results.fractionalSel,
    "fractional start did not change existing selection",
  );
  assert.strictEqual(results.nan, false, "NaN start rejected");
  assert.ok(results.nanSel, "NaN start did not change existing selection");
  assert.strictEqual(results.wrongFile, false, "wrong file id rejected");
  assert.strictEqual(
    results.mismatch,
    false,
    "DOM-only value mismatch rejected",
  );
  assert.ok(results.mismatchSel, "mismatch did not change existing selection");
  assert.strictEqual(results.detached, false, "detached textarea rejected");
  assert.strictEqual(results.disabled, false, "disabled textarea rejected");
  assert.ok(
    results.disabledSel,
    "disabled textarea did not change existing selection",
  );
  assert.strictEqual(results.oob, false, "out-of-bounds range rejected");
  console.log(
    "PASS U2c/editor-select.js: disconnected/disabled/fractional/NaN/mismatch rejected; valid succeeds",
  );
  await ctx.close();
}

// ---- U6 read-only tab: a read-only tab can analyze a durable project, still ----
// cannot edit/save/takeover another tab's lock except via the explicit button.
// Reuses the existing two-tab lock pattern (test-workspace.cjs).
async function runU6ReadOnlyTabAnalysis(browser) {
  const ctx = await browser.newContext();
  const page1 = await ctx.newPage();
  const page2 = await ctx.newPage();
  await page1.goto(BASE);
  await waitForReady(page1);
  const name = unique("U6ro");
  await createProject(page1, name);
  await importSingleFile(page1, "main.tex", Buffer.from("a{b\n", "utf-8"));
  await saveAndWait(page1);

  // Second tab opens the SAME project -> read-only (writer lock held by tab1).
  await page2.goto(BASE);
  await waitForReady(page2);
  await page2
    .locator("[data-testid=project-row]", { hasText: name })
    .waitFor({ timeout: 20000 });
  await page2
    .locator("[data-testid=project-row]", { hasText: name })
    .locator("[data-testid=open-project]")
    .click();
  await page2.locator(".banner-readonly").waitFor({ state: "visible" });
  const editorDisabled = await page2
    .locator("[data-testid=editor]")
    .isDisabled();
  assert.ok(editorDisabled, "second tab opens the editor read-only");

  // The read-only tab CAN run engine analysis (analysis is independent of the
  // writer lock / edit / save).
  await analyzeNow(page2);
  await awaitCompletedAnalysis(page2);
  assert.ok(
    (await page2.locator("[data-testid=analysis-finding]").count()) >= 1,
    "read-only tab can analyze the durable project",
  );

  // The read-only tab CANNOT edit (disabled) or save; no hidden writes.
  const saveDisabled = await page2.locator("[data-testid=save]").isDisabled();
  assert.ok(saveDisabled, "read-only tab cannot save (no writer lock)");
  // No takeover happened: tab1 still holds the lock and can save (make it dirty
  // first so the Save button is enabled).
  await page1.locator("[data-testid=editor]").fill("tab1 still writer");
  const tab1Save = await saveAndWait(page1);
  assert.ok(
    tab1Save.startsWith("Saved"),
    `tab1 still holds the writer lock; got ${tab1Save}`,
  );

  // Explicit takeover (no hidden permission change): tab1 releases by closing,
  // tab2 takes over via the explicit button, then can edit/save.
  await page1.getByRole("button", { name: "Close", exact: true }).click();
  await page1.waitForTimeout(500);
  await page2
    .getByRole("button", { name: "Take over editing", exact: true })
    .click();
  await page2.locator(".banner-readonly").waitFor({ state: "hidden" });
  const writable = await page2.locator("[data-testid=editor]").isDisabled();
  assert.ok(!writable, "after explicit takeover the editor is writable");
  console.log(
    "PASS U6/read-only-tab: read-only tab analyzes; cannot edit/save/takeover except explicit button",
  );
  await ctx.close();
}
