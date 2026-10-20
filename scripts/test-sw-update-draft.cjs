// M2a-2b-2b update-DRAFT PRESERVATION regressions. Proves the app-scoped
// update guard never lets a waiting service worker activate / the page reload
// while unsaved work would be lost:
//   #1 type a draft and click "Reload to update" BEFORE autosave → refused,
//      draft retained + visible Save/retry guidance; explicit Save then retry
//      reload restores the durable draft.
//   #2 a failed save (persistent storage fault) with an unsaved draft → click
//      update refused (no reload, draft + head intact, visible failure); restore
//      the fault, explicit Save + retry reload succeeds.
//   #3 an edit landing during a HELD guard await (stale clean approval) → the
//      pre-reload recheck refuses, no draft loss; also a dirty workspace on
//      another route (/smoke) is refused (the guard is app-scoped, not page-local).
// Clean explicit acceptance + cache purge + durable-doc persistence are already
// covered by test-sw-update.cjs (re-run unchanged as regression #4).
//
// Uses the PUBLISHED static build served via a proxy (like test-sw-update.cjs)
// and a persistent browser profile so the SW registration survives reloads. A
// "new" worker is staged per scenario by bumping CACHE_VERSION through the proxy.

const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const http = require("node:http");
const { chromium } = require("playwright");
const { reveal } = require("./review-ui-helpers.cjs");

const UPSTREAM = process.env.APP_URL || "http://static-origin:8080";
const PROXY_PORT = 8097;
const BASE = `http://127.0.0.1:${PROXY_PORT}`;

function startProxy() {
  const state = { swOverride: null, swRequestCount: 0 };
  const server = http.createServer((cr, res) => {
    const u = new URL(cr.url, UPSTREAM);
    if (u.pathname === "/service-worker.js" && state.swOverride) {
      state.swRequestCount++;
      res.writeHead(200, { "content-type": "text/javascript" });
      res.end(state.swOverride);
      return;
    }
    const h = Object.assign({}, cr.headers, { host: u.host });
    const up = http.request(u, { method: cr.method, headers: h }, (r) => {
      res.writeHead(r.statusCode, r.headers);
      r.pipe(res);
    });
    up.on("error", () => res.destroy());
    cr.pipe(up);
  });
  server.state = state;
  return server;
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
async function openProject(page, name) {
  await page
    .locator("[data-testid=project-row]", { hasText: name })
    .locator("[data-testid=open-project]")
    .click();
  await page.locator("[data-testid=editor]").waitFor({ state: "visible" });
}

// Stage a NEW waiting worker by bumping CACHE_VERSION (through the proxy) and
// reloading + forcing registration.update() so the new worker installs as
// WAITING while the current one keeps controlling. Returns the bumped source.
async function stageWaiting(page, toVer) {
  const realSw = await page.evaluate(() =>
    fetch("/service-worker.js").then((r) => r.text()),
  );
  const nextSw = realSw.replace(
    /const CACHE_VERSION = "v[0-9]+"/,
    `const CACHE_VERSION = "${toVer}"`,
  );
  assert.notEqual(nextSw, realSw, `v${toVer} SW source must differ`);
  proxy.state.swOverride = nextSw;
  proxy.state.swRequestCount = 0;
  await page.reload();
  await waitForReady(page);
  await page.evaluate(async () => {
    try {
      const reg = await navigator.serviceWorker.getRegistration();
      if (reg) await reg.update();
    } catch {}
  });
  await page
    .locator("#update-banner")
    .waitFor({ state: "visible", timeout: 15000 });
  return nextSw;
}

// Assert the page did NOT reload since `marker` was set, the editor draft is
// retained, and visible guidance is shown.
async function assertNoReloadDraftRetained(page, marker, expectedDraft) {
  const alive = await page.evaluate((m) => window[m], marker);
  assert.equal(alive, "alive", `${marker}: page must NOT have reloaded`);
  const ed = await page.locator("[data-testid=editor]").inputValue();
  assert.equal(ed, expectedDraft, `${marker}: draft must be retained`);
  const guidance = await page.locator("#update-guidance").count();
  assert.ok(guidance > 0, `${marker}: visible guidance must be shown`);
  const gtxt = await page.locator("#update-guidance").innerText();
  assert.ok(
    /save/i.test(gtxt),
    `${marker}: guidance must mention Save (${gtxt})`,
  );
}

// Install a HELD guard proxy: the FIRST canActivateUpdate captures the real
// decision now (clean) and awaits a release gate, so an edit landing during the
// hold turns that decision stale. Subsequent calls delegate to the real C#
// guard (so the pre-reload recheck sees the edit → dirty → refuse).
async function installHeldGuard(page) {
  await page.evaluate(() => {
    const real = window.__oopUpdateGuard;
    if (!real || typeof real.invokeMethodAsync !== "function")
      throw new Error("guard not registered");
    let firstDone = false;
    let releaseResolve;
    window.__testHeldRelease = new Promise((r) => {
      releaseResolve = r;
    });
    window.__testHeldReleaseResolve = releaseResolve;
    window.__testHeldFirstDecision = null;
    window.__oopUpdateGuard = {
      async invokeMethodAsync(method, ...args) {
        if (!firstDone && method === "CanActivateUpdateAsync") {
          firstDone = true;
          const d = await real.invokeMethodAsync("CanActivateUpdateAsync");
          window.__testHeldFirstDecision = d;
          await window.__testHeldRelease;
          return d; // stale (clean) decision
        }
        return real.invokeMethodAsync(method, ...args); // delegate → sees the edit
      },
    };
  });
}
async function releaseHeldGuard(page) {
  await page.evaluate(() => window.__testHeldReleaseResolve());
}

let proxy;

(async () => {
  proxy = startProxy();
  await new Promise((r) => proxy.listen(PROXY_PORT, "127.0.0.1", r));
  const userDataDir = fs.mkdtempSync(path.join(os.tmpdir(), "oop-swdraft-"));
  let browser, page;
  const errors = [];
  try {
    browser = await chromium.launchPersistentContext(userDataDir, {
      headless: true,
    });
    page = await browser.newPage();
    page.on("pageerror", (e) => errors.push(String(e)));
    page.on("console", (m) => {
      if (m.type() === "error") errors.push("console: " + m.text());
    });

    // ---- Provision v3, a durable project with v1 content. ----
    await page.goto(BASE);
    await waitForReady(page);
    await page.evaluate(async () => {
      const m = await import("./js/workspace-storage.js");
      await m.clearAll();
    });
    await page.reload();
    await waitForReady(page);

    const name = unique("DraftUp");
    await reveal(page, page.locator("#new-project"));
    await page.locator("#new-project").fill(name);
    await page.getByRole("button", { name: "Create project" }).click();
    await page
      .locator("[data-testid=new-file-name]")
      .waitFor({ timeout: 20000 });
    await importSingleFile(page, "main.tex", enc("durable v1\n"));
    await saveAndWait(page);
    await page.waitForFunction(() => window.__oopOfflineReady === true, null, {
      timeout: 30000,
    });

    const realSw = await page.evaluate(() =>
      fetch("/service-worker.js").then((r) => r.text()),
    );
    const version = realSw.match(/const CACHE_VERSION = "v([0-9]+)"/);
    assert.ok(version, "published worker declares a numeric shell version");
    const shellVersions = Array.from(
      { length: 5 },
      (_, i) => `v${Number(version[1]) + i}`,
    );
    const shellCaches = shellVersions.map((v) => `oop-workspace-shell-${v}`);

    // ============================================================ Scenario #1
    // Stage the next worker; type v2 and click update BEFORE the 2s autosave.
    await stageWaiting(page, shellVersions[1]);
    await openProject(page, name);
    assert.equal(
      await page.locator("[data-testid=editor]").inputValue(),
      "durable v1\n",
      "#1 baseline editor content",
    );
    await page.evaluate(() => (window.__testAlive1 = "alive"));
    await page.locator("[data-testid=editor]").fill("draft v2\n");
    // Immediately click update — BEFORE autosave (well under 2s).
    await page.locator("#update-banner button").click();
    await page.waitForTimeout(900);
    await assertNoReloadDraftRetained(page, "__testAlive1", "draft v2\n");
    // No skipWaiting: v4 still WAITING, v3 still active.
    const reg1 = await page.evaluate(async () => {
      const r = await navigator.serviceWorker.getRegistration();
      return {
        waiting: r && r.waiting ? r.waiting.state : null,
        active: r && r.active ? r.active.state : null,
      };
    });
    assert.equal(reg1.waiting, "installed", "#1 waiting worker not activated");
    assert.equal(
      reg1.active,
      "activated",
      "#1 active worker unchanged (no reload)",
    );
    // Explicit Save makes the draft durable, then retry the update.
    const st1 = await saveAndWait(page);
    assert.ok(st1.startsWith("Saved"), `#1 save before retry: ${st1}`);
    await page.locator("#update-banner button").click();
    // Reload happens on controllerchange; poll for v4 active + v3 purged.
    let activated1 = false;
    for (let i = 0; i < 60; i++) {
      const s = await page
        .evaluate(async (shellCaches) => {
          const r = await navigator.serviceWorker.getRegistration();
          return {
            active: r && r.active ? r.active.state : null,
            v3: await caches.has(shellCaches[0]),
            v4: await caches.has(shellCaches[1]),
          };
        }, shellCaches)
        .catch(() => null);
      if (s && s.active === "activated" && s.v3 === false && s.v4 === true) {
        activated1 = true;
        break;
      }
      await page.waitForTimeout(300);
    }
    assert.ok(activated1, "#1 v4 activated and v3 purged after Save+retry");
    await waitForReady(page);
    await openProject(page, name);
    assert.equal(
      await page.locator("[data-testid=editor]").inputValue(),
      "draft v2\n",
      "#1 durable draft survives the update reload",
    );
    console.log(
      "PASS #1: draft-before-autosave refused, retained; Save+retry reload restores durable draft",
    );

    // ============================================================ Scenario #2
    // A failed save with an unsaved draft → click update refused (no reload,
    // draft + head intact, visible failure); restore fault, Save+retry reload.
    await stageWaiting(page, shellVersions[2]);
    await openProject(page, name);
    assert.equal(
      await page.locator("[data-testid=editor]").inputValue(),
      "draft v2\n",
      "#2 baseline editor content",
    );
    await page.locator("[data-testid=editor]").fill("draft v3\n");
    // One-shot persistent storage failure: make the next digest reject.
    await page.evaluate(() => {
      const realDigest = SubtleCrypto.prototype.digest;
      SubtleCrypto.prototype.digest = function () {
        SubtleCrypto.prototype.digest = realDigest;
        return Promise.reject(new Error("InjectedStorageFailure"));
      };
    });
    const failed = await saveAndWait(page);
    assert.ok(/Save failed/i.test(failed), `#2 save must fail: ${failed}`);
    // Now SavePhase=Failed, IsDirty=true → guard refuses update.
    await page.evaluate(() => (window.__testAlive2 = "alive"));
    await page.locator("#update-banner button").click();
    await page.waitForTimeout(900);
    await assertNoReloadDraftRetained(page, "__testAlive2", "draft v3\n");
    // Durable head unchanged (failed save must not advance it).
    const head2 = await page.evaluate(async () => {
      const m = await import("./js/workspace-storage.js");
      const ps = await m.listProjects();
      const p = ps[ps.length - 1];
      return p.headRevisionId;
    });
    // (Fault was one-shot and already restored.) Retry save succeeds.
    const st2 = await saveAndWait(page);
    assert.ok(st2.startsWith("Saved"), `#2 retry save: ${st2}`);
    const head2b = await page.evaluate(async () => {
      const m = await import("./js/workspace-storage.js");
      const ps = await m.listProjects();
      const p = ps[ps.length - 1];
      return p.headRevisionId;
    });
    assert.notEqual(
      head2b,
      head2,
      "#2 head advanced after successful retry save",
    );
    // Retry the update → reload → v5 active; durable draft v3 survives.
    await page.locator("#update-banner button").click();
    let activated2 = false;
    for (let i = 0; i < 60; i++) {
      const s = await page
        .evaluate(async (shellCaches) => {
          const r = await navigator.serviceWorker.getRegistration();
          return {
            active: r && r.active ? r.active.state : null,
            v4: await caches.has(shellCaches[1]),
            v5: await caches.has(shellCaches[2]),
          };
        }, shellCaches)
        .catch(() => null);
      if (s && s.active === "activated" && s.v4 === false && s.v5 === true) {
        activated2 = true;
        break;
      }
      await page.waitForTimeout(300);
    }
    assert.ok(activated2, "#2 v5 activated and v4 purged after Save+retry");
    await waitForReady(page);
    await openProject(page, name);
    assert.equal(
      await page.locator("[data-testid=editor]").inputValue(),
      "draft v3\n",
      "#2 durable draft survives the update reload after a recovered save failure",
    );
    console.log(
      "PASS #2: failed-save draft refused, retained; restore+Save+retry reload restores durable draft",
    );

    // ============================================================ Scenario #3a
    // Edit during a HELD guard await → stale clean approval must NOT cause
    // draft loss; the pre-reload (controllerchange) recheck refuses.
    await stageWaiting(page, shellVersions[3]);
    await openProject(page, name);
    assert.equal(
      await page.locator("[data-testid=editor]").inputValue(),
      "draft v3\n",
      "#3 baseline editor content",
    );
    await installHeldGuard(page);
    await page.evaluate(() => (window.__testAlive3 = "alive"));
    // Click update — the handler's first guard call is captured clean, then HELD.
    await page.locator("#update-banner button").click();
    // Wait until the first (clean) decision was captured.
    for (let i = 0; i < 40; i++) {
      const d = await page.evaluate(
        () =>
          window.__testHeldFirstDecision && window.__testHeldFirstDecision.safe,
      );
      if (d === true) break;
      await page.waitForTimeout(100);
    }
    const firstClean = await page.evaluate(
      () =>
        window.__testHeldFirstDecision && window.__testHeldFirstDecision.safe,
    );
    assert.equal(
      firstClean,
      true,
      "#3 first guard decision captured clean (before the edit)",
    );
    // Edit during the held await — this makes the captured decision stale.
    await page.locator("[data-testid=editor]").fill("draft v4\n");
    await releaseHeldGuard(page);
    // skipWaiting runs (stale clean), v6 activates → controllerchange → the
    // pre-reload recheck (delegated to the real guard) sees IsDirty → refuse.
    await page.waitForTimeout(1800);
    await assertNoReloadDraftRetained(page, "__testAlive3", "draft v4\n");
    // v6 DID activate (skipWaiting ran) — proving the recheck, not the gate,
    // prevented the loss (the gate already said clean).
    const reg3 = await page.evaluate(async () => {
      const r = await navigator.serviceWorker.getRegistration();
      return {
        active: r && r.active ? r.active.state : null,
        waiting: r && r.waiting ? r.waiting.state : null,
      };
    });
    assert.equal(
      reg3.active,
      "activated",
      "#3 new worker activated (skipWaiting ran)",
    );
    assert.ok(!reg3.waiting, "#3 no waiting worker left (activated)");
    // Save makes it durable, then retry the update (no waiting → else branch reload).
    const st3 = await saveAndWait(page);
    assert.ok(st3.startsWith("Saved"), `#3 save before retry: ${st3}`);
    await page.locator("#update-banner button").click();
    let activated3 = false;
    for (let i = 0; i < 60; i++) {
      const s = await page
        .evaluate(async (shellCaches) => {
          const r = await navigator.serviceWorker.getRegistration();
          return {
            active: r && r.active ? r.active.state : null,
            v5: await caches.has(shellCaches[2]),
            v6: await caches.has(shellCaches[3]),
          };
        }, shellCaches)
        .catch(() => null);
      if (s && s.active === "activated" && s.v5 === false && s.v6 === true) {
        activated3 = true;
        break;
      }
      await page.waitForTimeout(300);
    }
    assert.ok(activated3, "#3 v6 active and v5 purged after Save+retry");
    await waitForReady(page);
    await openProject(page, name);
    assert.equal(
      await page.locator("[data-testid=editor]").inputValue(),
      "draft v4\n",
      "#3 durable draft survives the update reload after a held-guard stale-approval",
    );
    console.log(
      "PASS #3a: edit during held guard await → pre-reload recheck refuses; no draft loss",
    );

    // ============================================================ Scenario #3b
    // A DIRTY workspace on ANOTHER route (/smoke) must still be refused — the
    // guard is app-scoped (App.razor), not page-local. Stage v7, dirty the
    // workspace, navigate to /smoke, click update there → refused, no reload,
    // draft retained.
    await stageWaiting(page, shellVersions[4]);
    await openProject(page, name);
    await page.locator("[data-testid=editor]").fill("draft v5\n");
    await page.evaluate(() => (window.__testAlive4 = "alive"));
    // Navigate to the worker-smoke route (client-side NavLink, preserves the
    // scoped coordinator + dirty working set).
    const link = page.locator(".project-menu a.project-smoke-link");
    await reveal(page, link);
    await link.click();
    await page
      .locator("text=Worker smoke test")
      .locator("xpath=ancestor::article")
      .waitFor({ state: "visible" });
    // The update banner is app-root (index.html), present on every route.
    await page.locator("#update-banner button").click();
    await page.waitForTimeout(900);
    const alive4 = await page.evaluate(() => window.__testAlive4);
    assert.equal(
      alive4,
      "alive",
      "#3b dirty workspace on /smoke must NOT reload",
    );
    const guidance4 = await page.locator("#update-guidance").count();
    assert.ok(
      guidance4 > 0,
      "#3b visible guidance shown on /smoke (app-scoped guard)",
    );
    // Navigate back to the workspace; the dirty draft is still there.
    await page.getByRole("link", { name: /^Workspace$/ }).click();
    await page.locator("[data-testid=editor]").waitFor({ state: "visible" });
    assert.equal(
      await page.locator("[data-testid=editor]").inputValue(),
      "draft v5\n",
      "#3b dirty draft retained across the /smoke round-trip",
    );
    console.log(
      "PASS #3b: dirty workspace on another route refused by the app-scoped guard",
    );

    // ============================================================ Scenario #4
    // Clean explicit acceptance on a clean workspace: click update → reload →
    // v7 active. (The draft v5 above is saved first to make the session clean.)
    const st4 = await saveAndWait(page);
    assert.ok(st4.startsWith("Saved"), `#4 save before clean accept: ${st4}`);
    await page.locator("#update-banner button").click();
    let activated4 = false;
    for (let i = 0; i < 60; i++) {
      const s = await page
        .evaluate(async (shellCaches) => {
          const r = await navigator.serviceWorker.getRegistration();
          return {
            active: r && r.active ? r.active.state : null,
            v6: await caches.has(shellCaches[3]),
            v7: await caches.has(shellCaches[4]),
          };
        }, shellCaches)
        .catch(() => null);
      if (s && s.active === "activated" && s.v6 === false && s.v7 === true) {
        activated4 = true;
        break;
      }
      await page.waitForTimeout(300);
    }
    assert.ok(activated4, "#4 clean accept activates v7 and purges v6");
    await waitForReady(page);
    await openProject(page, name);
    assert.equal(
      await page.locator("[data-testid=editor]").inputValue(),
      "draft v5\n",
      "#4 durable draft persists across the clean-accept reload",
    );

    assert.deepEqual(
      errors,
      [],
      "No uncaught page errors / console errors across all scenarios",
    );
    console.log(
      "PASS SW-update-draft: refused-before-autosave (#1), failed-save refusal (#2), held-guard stale-approval recheck (#3a), dirty-on-another-route (#3b), clean accept (#4)",
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
