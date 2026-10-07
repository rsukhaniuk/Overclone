// M2a-2b-2b (Area 4): service-worker update path. Proves the explicit
// update button targets the WAITING registration (not the active controller),
// reloads only after the actual controllerchange, and durable documents in
// OPFS/IndexedDB persist across the update (the new cache version does not
// delete them). No force-reload before explicit acceptance; active editing is
// not interrupted by the install.
//
// This is a coherent deployment of the new cache version (v3 here), not a new
// feature milestone. It uses the PUBLISHED static build served by static-origin
// (like test-offline-restart.cjs) and a persistent browser profile so the SW
// registration survives reloads. A "new" worker is staged by routing the SW
// script to a copy with a bumped CACHE_VERSION (v4), so the browser detects a
// byte-difference and installs it as WAITING while the v3 worker keeps
// controlling the page.

const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const http = require("node:http");
const { chromium } = require("playwright");

const UPSTREAM = process.env.APP_URL || "http://static-origin:8080";
const PROXY_PORT = 8096;
const BASE = `http://127.0.0.1:${PROXY_PORT}`;

function startProxy() {
  // The proxy is the actual PWA origin the browser sees. The browser's
  // service-worker update check fetches /service-worker.js THROUGH this proxy
  // (Playwright page.route does NOT intercept the SW script fetch, which is
  // performed by the browser's SW infrastructure outside the page network
  // layer). So staging a new worker is done by having the PROXY serve a
  // modified SW on demand: `swOverride` is null during provisioning (forward
  // to upstream), then set to the v4 SW source so the next reload's update
  // check installs the new worker as WAITING.
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
          () => !!document.querySelector("#new-project, [data-testid=editor]"),
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

async function cacheHas(page, name) {
  return await page.evaluate(async (n) => await caches.has(n), name);
}

(async () => {
  const proxy = startProxy();
  await new Promise((r) => proxy.listen(PROXY_PORT, "127.0.0.1", r));
  const userDataDir = fs.mkdtempSync(path.join(os.tmpdir(), "oop-sw-"));
  let browser, ctx, page;
  try {
    browser = await chromium.launchPersistentContext(userDataDir, {
      headless: true,
    });
    ctx = browser;
    page = await ctx.newPage();
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

    // 1. Create a durable project and wait until v3 is fully provisioned.
    const name = unique("SWUp");
    await page.locator("#new-project").fill(name);
    await page.getByRole("button", { name: "Create project" }).click();
    await page
      .locator("[data-testid=new-file-name]")
      .waitFor({ timeout: 20000 });
    await importSingleFile(page, "main.tex", enc("sw body\n"));
    await saveAndWait(page);
    await page.waitForFunction(() => window.__oopOfflineReady === true, null, {
      timeout: 30000,
    });
    assert.ok(
      await cacheHas(page, "oop-workspace-shell-v3"),
      "v3 cache provisioned",
    );
    assert.ok(
      !(await cacheHas(page, "oop-workspace-shell-v4")),
      "v4 cache not yet present",
    );

    // 2. Stage a NEW worker (bumped CACHE_VERSION v3 -> v4). The browser's SW
    // update check fetches /service-worker.js THROUGH the proxy (Playwright
    // page.route does not intercept the SW script fetch), so the PROXY serves
    // the modified v4 SW on demand: the next reload's update check installs v4
    // as WAITING while v3 keeps controlling the page.
    const realSw = await page.evaluate(() =>
      fetch("/service-worker.js").then((r) => r.text()),
    );
    const v4Sw = realSw.replace(
      'const CACHE_VERSION = "v3"',
      'const CACHE_VERSION = "v4"',
    );
    assert.notEqual(
      v4Sw,
      realSw,
      "the v4 SW source differs from v3 (update detectable)",
    );
    proxy.state.swOverride = v4Sw;
    const seenPaths = [];
    proxy.on("request", (req) => seenPaths.push(req.url || ""));

    // 3. Reload to trigger the SW update check. The new v4 installs (WAITING);
    // v3 stays active and shows the update banner. NO force-reload.
    await page.reload();
    await waitForReady(page);
    // The automatic update check on navigation can be throttled; force it with
    // registration.update() so the proxy-served v4 SW is fetched and installed.
    await page.evaluate(async () => {
      try {
        const reg = await navigator.serviceWorker.getRegistration();
        if (reg) await reg.update();
      } catch {
        /* update() may throw if a check is already running */
      }
    });
    // Confirm the proxy served the v4 SW for the update check.
    assert.ok(
      proxy.state.swRequestCount > 0,
      `the proxy served the modified v4 SW for the update check (saw ${proxy.state.swRequestCount}); paths: ${JSON.stringify(seenPaths.filter((p) => p.includes("service-worker")))}`,
    );
    await page
      .locator("#update-banner")
      .waitFor({ state: "visible", timeout: 15000 });

    // The v3 worker is STILL the active controller (explicit acceptance only):
    // the durable project still opens and saves fine under v3.
    await page
      .locator("[data-testid=project-row]", { hasText: name })
      .waitFor({ timeout: 20000 });
    await page
      .locator("[data-testid=project-row]", { hasText: name })
      .locator("[data-testid=open-project]")
      .click();
    await page.locator("[data-testid=editor]").waitFor({ state: "visible" });
    await page.locator("[data-testid=editor]").fill("second durable draft\n");
    const st = await saveAndWait(page);
    assert.ok(
      st.startsWith("Saved"),
      `active editing/save works under v3 while v4 waits; got ${st}`,
    );
    assert.ok(
      await cacheHas(page, "oop-workspace-shell-v3"),
      "v3 cache still active (not yet activated v4)",
    );
    assert.ok(
      await cacheHas(page, "oop-workspace-shell-v4"),
      "v4 cache precached during install (waiting)",
    );

    // 4. EXPLICIT acceptance: click "Reload to update". The button posts
    // skipWaiting to the WAITING registration and reloads after the actual
    // controllerchange. Documents are never deleted.
    const preClick = await page.evaluate(async () => {
      const reg = await navigator.serviceWorker.getRegistration();
      return {
        active: reg?.active?.state,
        waiting: reg?.waiting?.state,
        controller: !!navigator.serviceWorker.controller,
      };
    });
    console.log("  [sw] pre-click regState:", JSON.stringify(preClick));
    await page.locator("#update-banner button").click();
    // Wait for the controllerchange reload to land the v4-controlled page.
    // The button calls location.reload() on controllerchange, which detaches
    // the old frame; a single page.waitForFunction does not survive that
    // reload, so poll with FRESH page.evaluate calls (which re-bind to the
    // new document after the reload) until v4 is active and v3 purged.
    let activated = false;
    for (let i = 0; i < 60; i++) {
      const s = await page
        .evaluate(async () => {
          const reg = await navigator.serviceWorker.getRegistration();
          return {
            active: reg?.active?.state,
            v3: await caches.has("oop-workspace-shell-v3"),
            v4: await caches.has("oop-workspace-shell-v4"),
          };
        })
        .catch(() => null);
      if (s && s.active === "activated" && s.v3 === false && s.v4 === true) {
        activated = true;
        break;
      }
      await page.waitForTimeout(300);
    }
    assert.ok(
      activated,
      "v4 activated and purged the v3 cache after explicit accept",
    );
    await waitForReady(page);
    assert.ok(
      !(await cacheHas(page, "oop-workspace-shell-v3")),
      "v3 cache purged after v4 activation",
    );
    assert.ok(
      await cacheHas(page, "oop-workspace-shell-v4"),
      "v4 cache is now active",
    );

    // 5. Durable documents persist across the update (OPFS/IDB not cleared).
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
      "second durable draft\n",
      "the durable draft persisted across the SW update reload",
    );
    // The app still works under v4: make the editor dirty and save again.
    await page.locator("[data-testid=editor]").fill("edited under v4\n");
    const st2 = await saveAndWait(page);
    assert.ok(
      st2.startsWith("Saved"),
      `save works under the activated v4 worker; got ${st2}`,
    );
    assert.deepEqual(errors, [], "No uncaught page errors");
    console.log(
      "PASS SW-update: waiting worker activates only on explicit accept; controllerchange reload; durable docs persist",
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
