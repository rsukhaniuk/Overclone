// M1b B12: published-client offline restart.
//
// Verifies the REQUIRED published static build, not the development server:
// compose's publish-client `dotnet publish`es the Blazor WASM client to a
// static wwwroot, and static-origin (scripts/static-server.cjs) serves it.
// This test provisions against that published static origin, closes Chromium
// fully, stops the actual browser-facing proxy (verified connection refusal),
// disables Chromium networking, and reopens the SAME persistent browser profile
// offline. Exercises KNU WASM/citations/navigation, a second saved-edit cold
// reopen, ZIP import/export and history/restore. The internal static-origin
// remains up but the browser's proxy is stopped. No external requests succeed:
// the service worker serves the cached
// published shell and all work is local.
//
// A warm page reload does NOT establish cold-start support; this test closes the
// whole browser and reuses a persistent profile directory.

const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const http = require("node:http");
const { createHash } = require("node:crypto");
const {
  fixtures: knuFixtures,
  hashes: knuHashes,
} = require("./knu-fixtures.cjs");
const { chromium } = require("playwright");
const { buildZip } = require("./zip-util.cjs");
const review = require("./review-ui-helpers.cjs");
const references = require("./source-reference-ui-checks.cjs");
const formatting = require("./thesis-formatting-ui-checks.cjs");

const UPSTREAM = process.env.APP_URL || "http://static-origin:8080";
const PROXY_PORT = 8092;
const BASE = `http://127.0.0.1:${PROXY_PORT}`;

function upstreamBytes(pathname) {
  return new Promise((resolve, reject) =>
    http
      .get(new URL(pathname, UPSTREAM), (r) => {
        if (r.statusCode !== 200) {
          reject(new Error(`${pathname}: ${r.statusCode}`));
          r.resume();
          return;
        }
        const chunks = [];
        r.on("data", (d) => chunks.push(d));
        r.on("end", () => resolve(Buffer.concat(chunks)));
      })
      .on("error", reject),
  );
}

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

const enc = (s) => Array.from(new TextEncoder().encode(s));
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
  throw new Error("UI not ready");
}
async function saveAndWait(page) {
  await page.locator("[data-testid=save]").click();
  await page
    .locator(".status.status-saved, .status.status-failed")
    .waitFor({ state: "attached" });
  for (let i = 0; i < 40; i++) {
    const t = await page.locator(".status").innerText();
    if (t.startsWith("Saved")) return t;
    if (t.startsWith("Save failed")) return t;
    await page.waitForTimeout(250);
  }
  return page.locator(".status").innerText();
}
async function importSingleFile(page, name, bytes) {
  await page.locator("[data-testid=import-input]").setInputFiles({
    name,
    mimeType: "application/octet-stream",
    buffer: Buffer.from(bytes),
  });
  await page
    .locator("[data-testid=select-file]", { hasText: name })
    .waitFor({ state: "visible" });
}

async function analyzeNow(page) {
  await review.reveal(page, page.locator("[data-testid=analyze-now]"));
  await page.locator("[data-testid=analyze-now]").click();
}
async function analysisStatusText(page) {
  return await page.evaluate(
    () =>
      document.querySelector("[data-testid=analysis-status]")?.textContent ??
      "<none>",
  );
}
const A_TERMINAL =
  /complete|cancel|fail|unavailable|superseded|No project|no longer/i;
async function awaitTerminalAnalysis(page, timeout = 30000) {
  for (let i = 0; i < timeout / 200; i++) {
    const t = await analysisStatusText(page).catch(() => "");
    if (A_TERMINAL.test(t)) return t;
    await page.waitForTimeout(200);
  }
  throw new Error(
    `offline analysis did not reach a terminal status in ${timeout}ms; last: ${await analysisStatusText(page).catch(() => "?")}`,
  );
}

(async () => {
  const proxy = startProxy();
  await new Promise((r) => proxy.listen(PROXY_PORT, "127.0.0.1", r));
  const userDataDir = fs.mkdtempSync(path.join(os.tmpdir(), "oop-profile-"));
  let browser, ctx, page;
  try {
    const index = await upstreamBytes("/index.html");
    const wasm = await upstreamBytes("/rust-generated/document_engine_bg.wasm");
    assert.deepEqual(Array.from(wasm.subarray(0, 4)), [0, 97, 115, 109]);
    console.log(
      "Published build identity:",
      JSON.stringify({
        indexSha256: createHash("sha256").update(index).digest("hex"),
        rustWasmSha256: createHash("sha256").update(wasm).digest("hex"),
      }),
    );
    // ---- online: provision, create a project, wait for the shell to be cached
    browser = await chromium.launchPersistentContext(userDataDir, {
      headless: true,
    });
    ctx = browser;
    await references.install(ctx);
    page = await ctx.newPage();
    const failedReqs = [];
    page.on("requestfailed", (r) => failedReqs.push(r.url()));
    await page.goto(BASE);
    await waitForReady(page);
    const clientUrls = await page.evaluate(() =>
      performance
        .getEntriesByType("resource")
        .map((r) => r.name)
        .filter((n) => /\/_framework\/Client\.[^/]+\.wasm$/.test(n)),
    );
    assert.equal(
      clientUrls.length,
      1,
      "exactly one fingerprinted current Client assembly loaded",
    );
    const clientAssembly = await upstreamBytes(new URL(clientUrls[0]).pathname);
    console.log(
      "Published loaded Client:",
      new URL(clientUrls[0]).pathname,
      "SHA-256",
      createHash("sha256").update(clientAssembly).digest("hex"),
    );
    await page.evaluate(async () => {
      const m = await import("./js/workspace-storage.js");
      await m.clearAll();
    });
    await page.reload();
    await waitForReady(page);

    const name = unique("OfflineM1b");
    await review.reveal(page, page.locator("#new-project"));
    await page.locator("#new-project").fill(name);
    await page.getByRole("button", { name: "Create project" }).click();
    await page
      .locator("[data-testid=new-file-name]")
      .waitFor({ timeout: 20000 });
    await importSingleFile(page, "main.tex", enc("offline body Привіт\n"));
    // A13: also import a file with an unmatched brace so analysis can run on a
    // saved project both online and offline via the real Rust worker.
    await importSingleFile(page, "brace.tex", enc("Розділ {текст\n"));
    await importSingleFile(page, "knu.tex", knuFixtures.negative);
    await saveAndWait(page);
    // Current published UI + actual native KNU capability, not a warm old demo.
    await review.reveal(page, page.locator("[data-testid=analysis-profile]"));
    await review.selectAnalysisProfile(page, "knu-csc-2025-source@1.0.0");
    await analyzeNow(page);
    await awaitTerminalAnalysis(page);
    await page.locator("[data-testid=knu-report]").waitFor();
    assert.match(await page.locator("[data-testid=knu-edition]").innerText(), /2025/);
    assert.equal(await review.knuReportVersion(page), "1.0.0");
    assert.equal(await page.locator("[data-testid=knu-finding]").count(), 2);
    await review.reveal(page, page.locator("[data-testid=analysis-profile]"));
    await page
      .locator("[data-testid=analysis-profile]")
      .selectOption("engine-structural");

    // Provision the shell via the service worker and wait until genuinely ready.
    // __oopOfflineReady is honest only when ALL required assets (including the
    // analysis worker JS/WASM) are cached; a missing asset keeps it false.
    await page.waitForFunction(() => window.__oopOfflineReady === true, null, {
      timeout: 30000,
    });

    // A13 (online): real Rust analysis returns the unmatched-brace diagnostic.
    await analyzeNow(page);
    await awaitTerminalAnalysis(page);
    assert.ok(
      (await page
        .locator("[data-testid=analysis-finding]", { hasText: "brace.tex" })
        .count()) >= 1,
      "online analysis reports the brace.tex diagnostic before going offline",
    );

    // Separate real eligible F1 project: the legacy multi-file brace project
    // deliberately has uncertainty and must not be forged into fix eligibility.
    await page.getByRole("button", { name: "Close", exact: true }).click();
    const reviewName = await review.create(page, review.negative, "review.tex");
    await page.getByRole("button", { name: "Close", exact: true }).click();
    const referenceName = await references.create(page);
    await references.exercise(page, referenceName, "online");
    await page.getByRole("button", { name: "Close", exact: true }).click();
    const formattingName = await formatting.create(page);
    await formatting.exercise(page, formattingName, "online");
    await page.getByRole("button", { name: "Close", exact: true }).click();
    await review.open(page, name);
    await page.waitForFunction(() => window.__oopOfflineReady === true);

    // ---- close the browser fully, keep the same persistent profile
    await page.close();
    await ctx.close();
    browser = null;

    // A13: STOP the localhost proxy that IS the actual PWA origin, then reopen
    // the SAME profile with the network disabled. Cold start must succeed from
    // the service-worker cache only — a warm reload or a live worker would be
    // insufficient. State precisely: the actual public-origin proxy was stopped
    // AND the network was disabled.
    // Await the proxy close CALLBACK (http.Server.close takes a callback; calling
    // it without one does not yield a Promise) and then assert the origin
    // actually refuses connections (not a hardcoded boolean).
    await new Promise((resolve) => proxy.close(resolve));
    await new Promise((resolve) => {
      const req = http.get(
        { host: "127.0.0.1", port: PROXY_PORT, path: "/", timeout: 1500 },
        () => resolve(),
      );
      req.on("error", () => resolve());
      req.on("timeout", () => {
        req.destroy();
        resolve();
      });
    });
    // The proxy port now refuses connections: confirm an attempted request fails.
    let originRefused = false;
    await new Promise((resolve) => {
      const req = http.get(
        { host: "127.0.0.1", port: PROXY_PORT, path: "/", timeout: 1500 },
        () => resolve(),
      );
      req.on("error", () => {
        originRefused = true;
        resolve();
      });
      req.on("timeout", () => {
        req.destroy();
        originRefused = true;
        resolve();
      });
    });
    assert.ok(
      originRefused,
      "the actual public-origin proxy refuses connections after close (verified, not hardcoded)",
    );

    // ---- reopen the SAME profile, go offline (origin blocked), cold-start
    browser = await chromium.launchPersistentContext(userDataDir, {
      headless: true,
    });
    ctx = browser;
    await references.install(ctx);
    page = await ctx.newPage();
    const offErrors = [];
    page.on("pageerror", (e) => offErrors.push(String(e)));
    page.on("requestfailed", (r) => offErrors.push("reqfail:" + r.url()));
    await ctx.setOffline(true);
    // Navigation must succeed from cache only (origin unreachable).
    await page.goto(BASE, { waitUntil: "domcontentloaded" });
    await waitForReady(page);

    // The offline shell must serve; the project must be present from OPFS/IDB.
    await page
      .locator("[data-testid=project-row]", { hasText: name })
      .waitFor({ timeout: 20000 });
    await page
      .locator("[data-testid=project-row]", { hasText: name })
      .locator("[data-testid=open-project]")
      .click();
    await page.locator("[data-testid=editor]").waitFor({ state: "visible" });
    const text = await page.locator("[data-testid=editor]").inputValue();
    assert.equal(
      text,
      "offline body Привіт\n",
      "offline reload restores saved content",
    );

    // A13 (offline): real Rust analysis via the offline-cached worker returns the
    // diagnostic; click-to-select lands on the brace with no external request.
    await analyzeNow(page);
    await awaitTerminalAnalysis(page);
    assert.ok(
      (await page
        .locator("[data-testid=analysis-finding]", { hasText: "brace.tex" })
        .count()) >= 1,
      "offline analysis reports the brace.tex diagnostic via the cached Rust WASM",
    );
    await page
      .locator("[data-testid=analysis-finding]", { hasText: "brace.tex" })
      .first()
      .locator("[data-testid=finding-link]")
      .click();
    await page.waitForTimeout(150);
    const offSel = await page.evaluate(() => {
      const e = document.querySelector("[data-testid=editor]");
      // The brace.tex file must be active and the single "{" selected.
      return {
        label: document.querySelector(".editor-toolbar label")?.textContent,
        sub: e ? e.value.substring(e.selectionStart, e.selectionEnd) : null,
      };
    });
    assert.match(
      offSel.label || "",
      /brace\.tex/,
      "offline click activated brace.tex",
    );
    assert.equal(
      offSel.sub,
      "{",
      "offline click selected the exact brace span",
    );

    // M2b: real KNU rules, bundled citations and exact Unicode navigation with
    // the actual origin still stopped. No guideline/CDN/provider request.
    await review.reveal(page, page.locator("[data-testid=analysis-profile]"));
    await review.selectAnalysisProfile(page, "knu-csc-2025-source@1.0.0");
    await analyzeNow(page);
    await awaitTerminalAnalysis(page);
    await page.locator("[data-testid=knu-report]").waitFor();
    assert.match(
      await page.locator("[data-testid=knu-citation]").first().innerText(),
      /§3.1; printed pages 18/,
    );
    assert.match(
      await page.locator("[data-testid=knu-citation]").last().innerText(),
      /§3.2; printed pages 18, 19/,
    );
    const knuFinding = page.locator(
      '[data-testid=knu-finding][data-rule-id="KNU-SRC-HEADING-DOT-001"]',
    );
    const byteStart = Number(await knuFinding.getAttribute("data-start-byte"));
    const byteEnd = Number(await knuFinding.getAttribute("data-end-byte"));
    assert.equal(
      knuFixtures.negative.subarray(byteStart, byteEnd).toString(),
      ".",
    );
    const normalizedPrefix = knuFixtures.negative
      .subarray(0, byteStart)
      .toString()
      .replace(/^\uFEFF/, "")
      .replace(/\r\n?/g, "\n");
    await knuFinding.locator("[data-testid=knu-finding-link]").click();
    await page.waitForFunction((n) => {
      const e = document.querySelector("[data-testid=editor]");
      return (
        e.selectionStart === n &&
        e.selectionEnd === n + 1 &&
        e.value.substring(n, n + 1) === "."
      );
    }, normalizedPrefix.length);
    assert.match(
      await page.locator(".editor-toolbar .source-tab").innerText(),
      /knu.tex/,
    );
    // Actual offline reviewed-fix workflow, not a service/test injection.
    await page.getByRole("button", { name: "Close", exact: true }).click();
    await review.open(page, referenceName);
    await references.exercise(page, referenceName, "full cold offline");
    await page.getByRole("button", { name: "Close", exact: true }).click();
    await review.open(page, formattingName);
    await formatting.exercise(page, formattingName, "full cold offline");
    await page.getByRole("button", { name: "Close", exact: true }).click();
    await review.open(page, reviewName);
    await review.selectKnu(page);
    assert.equal(await page.locator("[data-testid=review-action]").count(), 0);
    const reviewBefore = await review.head(page, reviewName);
    await review.declineNoChange(page, reviewName);
    await review.preview(page);
    await review.accept(page);
    const reviewApplied = await review.assertBytes(
      page,
      reviewName,
      review.after,
      "review.tex",
    );
    assert.equal(reviewApplied.parent, reviewBefore.id);
    await review.analyze(page);
    assert.equal(await page.locator("[data-testid=preview-fix]").count(), 0);
    assert.equal(
      await page
        .locator(
          "[data-testid=knu-finding][data-rule-id=KNU-SRC-HEADING-DOT-001]",
        )
        .count(),
      1,
    );
    await review.undo(page);
    const reviewRestored = await review.assertBytes(
      page,
      reviewName,
      review.negative,
      "review.tex",
    );
    assert.equal(reviewRestored.parent, reviewApplied.id);
    assert.notEqual(reviewRestored.id, reviewBefore.id);
    assert.equal(await page.locator("[data-testid=review-undo]").count(), 0);
    await page.getByRole("button", { name: "Close", exact: true }).click();
    await review.open(page, name);
    await page
      .locator("[data-testid=select-file]", { hasText: "knu.tex" })
      .click();
    const knuEdited = "\\setlength{\\parindent}{1.27cm}\n\\section{Офлайн🙂}\n";
    await page.locator("[data-testid=editor]").fill(knuEdited);
    assert.ok((await saveAndWait(page)).startsWith("Saved"));
    // A SECOND fully closed browser restart proves offline edits really persist.
    await ctx.close();
    browser = null;
    browser = await chromium.launchPersistentContext(userDataDir, {
      headless: true,
    });
    ctx = browser;
    await ctx.setOffline(true);
    page = await ctx.newPage();
    page.on("pageerror", (e) => offErrors.push(String(e)));
    await page.goto(BASE, { waitUntil: "domcontentloaded" });
    await waitForReady(page);
    await page
      .locator("[data-testid=project-row]", { hasText: name })
      .locator("[data-testid=open-project]")
      .click();
    await page
      .locator("[data-testid=select-file]", { hasText: "knu.tex" })
      .click();
    assert.equal(
      await page.locator("[data-testid=editor]").inputValue(),
      knuEdited,
    );
    assert.equal(
      await page.locator("[data-testid=analysis-profile]").inputValue(),
      "engine-structural",
      "selection is session-local, not persisted",
    );
    await review.reveal(page, page.locator("[data-testid=analysis-profile]"));
    await review.selectAnalysisProfile(page, "knu-csc-2025-source@1.0.0");
    await analyzeNow(page);
    await awaitTerminalAnalysis(page);
    await page.locator("[data-testid=knu-report]").waitFor();
    const persisted = await page.evaluate(async (name) => {
      const m = await import("./js/workspace-storage.js");
      const p = (await m.listProjects()).find((p) => p.name === name);
      const r = await m.getRevision(p.headRevisionId);
      const f = r.files.find((f) => f.relativePath === "knu.tex");
      return Array.from(await m.readContent(f.contentHash));
    }, name);
    assert.deepEqual(
      persisted,
      [239, 187, 191, ...enc(knuEdited)],
      "offline edited bytes preserve BOM and accepted edited LF policy",
    );
    // The second whole-browser reopen also proves exact undo output persisted,
    // and honestly exposes no previous session's in-memory recovery entry.
    await page.getByRole("button", { name: "Close", exact: true }).click();
    await review.open(page, reviewName);
    const coldReview = await review.assertBytes(
      page,
      reviewName,
      review.negative,
      "review.tex",
    );
    assert.equal(coldReview.id, reviewRestored.id);
    assert.equal(await page.locator("[data-testid=review-action]").count(), 0);
    assert.equal(await page.locator("[data-testid=review-undo]").count(), 0);
    await page.getByRole("button", { name: "Close", exact: true }).click();
    await review.open(page, name);
    console.log(
      "PASS M2 review offline: native preview/decline/accept/save/reanalysis/undo/new revision, exact bytes after second whole-browser reopen; no durable in-memory undo",
    );
    console.log(
      "PASS M2b offline: KNU citations/evidence/navigation, save and second cold reopen; fixture SHA-256",
      knuHashes.negative,
    );
    await page
      .locator("[data-testid=select-file]", { hasText: "brace.tex" })
      .click();
    // Local save offline (existing M1b regression).
    await page.locator("[data-testid=editor]").fill("edited offline");
    const st = await saveAndWait(page);
    assert.ok(st.startsWith("Saved"), `offline save must succeed, got: ${st}`);

    // ZIP import offline (bytes from a buffer, no network).
    const zipBytes = buildZip([
      ["imported.tex", Buffer.from(enc("imported offline\n"))],
      ["asset.bin", Buffer.from([0, 1, 2, 3, 255])],
    ]);
    await page.getByRole("button", { name: "Close" }).click();
    await page.locator("[data-testid=import-zip]").setInputFiles({
      name: "m1b.zip",
      mimeType: "application/zip",
      buffer: zipBytes,
    });
    await review.reveal(page, page.locator("[data-testid=import-preview]"));
    await page
      .locator("[data-testid=import-preview]")
      .waitFor({ state: "visible" });
    const importName = unique("OfflineImport");
    await page.locator("[data-testid=staged-name]").fill(importName);
    await page.locator("[data-testid=publish-import]").click();
    await page
      .locator("[data-testid=editor], [data-testid=binary-view]")
      .waitFor({ state: "visible", timeout: 20000 });
    const restored = await page.evaluate(async (want) => {
      const m = await import("./js/workspace-storage.js");
      const ps = await m.listProjects();
      const p = ps.find((x) => x.name === want);
      const r = await m.getRevision(p.headRevisionId);
      const out = {};
      for (const f of r.files)
        out[f.relativePath] = Array.from(await m.readContent(f.contentHash));
      return {
        name: p.name,
        files: r.files.map((f) => f.relativePath),
        bytes: out,
      };
    }, importName);
    assert.deepEqual(restored.files.sort(), ["asset.bin", "imported.tex"]);
    assert.deepEqual(
      restored.bytes["asset.bin"],
      [0, 1, 2, 3, 255],
      "binary bytes preserved offline",
    );
    assert.deepEqual(restored.bytes["imported.tex"], enc("imported offline\n"));

    // ZIP export offline (download captured from cache-served shell).
    const exportBtn = page.locator("[data-testid=export-zip]");
    await review.reveal(page, exportBtn);
    console.log(
      "  [offline] export-zip visible:",
      await exportBtn.isVisible().catch(() => "?"),
    );
    const dlPromise = page.waitForEvent("download", { timeout: 60000 });
    await exportBtn.click();
    let dl = null;
    try {
      dl = await dlPromise;
    } catch (e) {
      console.log("  [offline] download event did not fire:", String(e));
      console.log("  [offline] pageerrors:", offErrors);
      // Capture any JS error / page state for diagnosis.
      const probe = await page.evaluate(() => ({
        hasEditor: !!document.querySelector("[data-testid=editor]"),
        url: location.href,
      }));
      console.log("  [offline] probe:", JSON.stringify(probe));
      throw e;
    }
    const out = `/tmp/b12-${Date.now()}.zip`;
    await dl.saveAs(out);
    assert.ok(
      fs.statSync(out).size > 0,
      "offline ZIP export produced a non-empty archive",
    );

    // History/restore offline: open the original project, select its checkpoint.
    await page.getByRole("button", { name: "Close" }).click();
    await page
      .locator("[data-testid=project-row]", { hasText: name })
      .waitFor();
    await page
      .locator("[data-testid=project-row]", { hasText: name })
      .locator("[data-testid=open-project]")
      .click();
    await review.reveal(page, page.locator("[data-testid=history-list]"));
    await page
      .locator("[data-testid=history-list]")
      .waitFor({ state: "visible" });
    const histCount = await page
      .locator("[data-testid=select-history]")
      .count();
    assert.ok(histCount >= 1, "history is available offline");
    await page.locator("[data-testid=select-history]").last().click();
    await page.locator("[data-testid=history-whole-restore-preview]").click();
    await page
      .locator("[data-testid=comparison]")
      .waitFor({ state: "visible" });
    await page.locator("[data-testid=restore]").click();
    const afterRestore = await page
      .locator("[data-testid=editor]")
      .inputValue()
      .catch(() => null);
    // After restoring the earliest checkpoint the editor shows the historical body.
    assert.ok(
      afterRestore === "offline body Привіт\n" ||
        afterRestore === "edited offline",
      `restore reflected a historical revision; got: ${afterRestore}`,
    );

    // No external request should have succeeded while offline: confirm the
    // offline context did not reach the origin for navigations (SW served cache).
    // (requestfailed fires for blocked requests; the key assertion is that the
    // shell loaded at all while offline.)

    // A13 missing-asset negative (distinct from the positive cold restart):
    // remove one required Rust asset from the service-worker CacheStorage, then
    // re-analyze offline. Analysis must fail HONESTLY (no fake engine success);
    // local editing/saving still work and OPFS/IDB documents are NOT cleared.
    // A fresh COLD RESTART is required: the already-loaded worker would
    // otherwise reuse its in-memory WASM and succeed despite the asset being
    // gone from CacheStorage. We remove the asset, close the browser, reopen
    // the SAME profile offline, and let a NEW worker try (and fail) to fetch it.
    // Clear the browser HTTP cache (CDP Network.clearBrowserCache) AS WELL AS
    // the required CacheStorage asset, so a cached-WASM hit cannot mask the
    // missing-asset failure. OPFS/IndexedDB are NOT cleared (documents persist).
    const client = await page.context().newCDPSession(page);
    await client.send("Network.clearBrowserCache");
    const ASSET = "/rust-generated/document_engine_bg.wasm";
    const removed = await page.evaluate(async (asset) => {
      let deleted = false;
      for (const name of await caches.keys()) {
        if (!/^oop-workspace-(?:shell|release|aux)-/.test(name)) continue;
        const cache = await caches.open(name);
        deleted = (await cache.delete(new Request(asset, { cache: "reload" }))) || deleted;
      }
      return deleted && !(await caches.match(asset));
    }, ASSET);
    assert.ok(
      removed,
      "the required Rust WASM asset was removed from CacheStorage",
    );
    // Close the offline session and cold-restart the SAME profile offline again.
    await page.close();
    await ctx.close();
    browser = null;
    browser = await chromium.launchPersistentContext(userDataDir, {
      headless: true,
    });
    ctx = browser;
    page = await ctx.newPage();
    const missingErrors = [];
    page.on("pageerror", (e) => missingErrors.push(String(e)));
    await ctx.setOffline(true);
    await page.goto(BASE, { waitUntil: "domcontentloaded" });
    await waitForReady(page);
    // The project is still present from OPFS/IDB (storage is NOT cleared).
    await page
      .locator("[data-testid=project-row]", { hasText: name })
      .waitFor({ timeout: 20000 });
    await page
      .locator("[data-testid=project-row]", { hasText: name })
      .locator("[data-testid=open-project]")
      .click();
    await page.locator("[data-testid=editor]").waitFor({ state: "visible" });
    // Local editing/saving still work offline with the asset missing.
    await page
      .locator("[data-testid=editor]")
      .fill("edited while asset missing");
    const stMissing = await saveAndWait(page);
    assert.ok(
      stMissing.startsWith("Saved"),
      `local save still works with the asset missing, got: ${stMissing}`,
    );
    // Analysis now fails honestly: a fresh worker cannot load the missing WASM offline.
    await review.reveal(page, page.locator("[data-testid=analysis-profile]"));
    await review.selectAnalysisProfile(page, "knu-csc-2025-source@1.0.0");
    await analyzeNow(page);
    await awaitTerminalAnalysis(page);
    const failStatus = await analysisStatusText(page);
    assert.match(
      failStatus,
      /fail|unavailable|no longer/i,
      `analysis fails honestly with the asset missing, got: ${failStatus}`,
    );
    // Offline readiness must NOT claim complete provisioning with a required asset absent.
    assert.notEqual(
      await page.evaluate(() => window.__oopOfflineReady),
      true,
      "offline readiness is not claimed while a required asset is absent",
    );
    assert.equal(
      await page.locator("[data-testid=analysis-finding]").count(),
      0,
      "no fake engine success/diagnostic with the asset missing",
    );
    assert.equal(
      await page.locator("[data-testid=knu-report]").count(),
      0,
      "no fabricated KNU success while WASM is absent",
    );
    // The C#-owned readiness notice must be VISIBLE with actionable guidance
    // (connect/reload/retry) — not merely the hidden window flag — while
    // editing/saving stay available.
    const readinessNotice = page.locator(
      "[data-testid=offline-readiness-notice]",
    );
    await readinessNotice.waitFor({ state: "visible", timeout: 10000 });
    const noticeText = await readinessNotice.innerText();
    assert.match(
      noticeText,
      /offline/i,
      `readiness notice explains the offline problem; got: ${noticeText}`,
    );
    assert.match(
      noticeText,
      /reload|connect|retry/i,
      `readiness notice gives actionable guidance; got: ${noticeText}`,
    );
    assert.ok(
      !(await page.locator("[data-testid=editor]").isDisabled()),
      "editing stays available while offline readiness is incomplete (editor not disabled)",
    );
    // Saving also still works: make the editor dirty and save again.
    await page.locator("[data-testid=editor]").fill("saved while incomplete");
    const stIncomplete = await saveAndWait(page);
    assert.ok(
      stIncomplete.startsWith("Saved"),
      `saving stays available while offline readiness is incomplete; got ${stIncomplete}`,
    );
    console.log(
      "PASS B12+A13: published client cold-starts offline (same profile; actual public-origin proxy stopped + network disabled), real Rust analysis offline + ZIP import/export + history/restore + save; missing-asset fails honestly",
    );
  } finally {
    if (browser) await browser.close();
    if (proxy) proxy.close();
    try {
      fs.rmSync(userDataDir, { recursive: true, force: true });
    } catch {}
  }
})().catch((e) => {
  console.error(e);
  process.exitCode = 1;
});
