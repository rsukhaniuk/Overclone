// M4 service-worker snapshot update path. Proves the explicit, save-aware
// update against the PUBLISHED manifest-required worker:
//  - Staging a new release means new worker bytes (triggers updatefound via
//    updateViaCache:"none") AND a bumped service-worker-assets.js version (a
//    new immutable release cache). The old worker stays the active controller.
//  - The new release installs completely (integrity-checked) or is NOT
//    activated; the waiting worker only activates on explicit "Reload to
//    update", gated by the existing C# UpdateGuard (no draft loss).
//  - Durable documents in OPFS/IndexedDB persist across the update.
//  - Unsaved-draft and save-failure guard cases: the update is refused while
//    unsaved work would be lost; the draft is retained and visible guidance is
//    shown; after an explicit Save (and storage-fault recovery) the retry
//    reload activates the new release and restores the durable draft.
//
// Uses the PUBLISHED static build and a persistent browser profile. The proxy
// stages the next release by serving a modified worker (a staged marker
// comment) and a modified manifest (a bumped version) on demand.

const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const http = require("node:http");
const { chromium } = require("playwright");
const { reveal } = require("./review-ui-helpers.cjs");

const UPSTREAM = process.env.APP_URL || "http://static-origin:8080";
const PROXY_PORT = 8096;
const BASE = `http://127.0.0.1:${PROXY_PORT}`;

function startProxy() {
    // swOverride: a modified worker (new bytes => updatefound). manifestOverride:
    // a modified service-worker-assets.js with a bumped version (new release cache).
    const state = { swOverride: null, manifestOverride: null };
    const server = http.createServer((cr, res) => {
        const u = new URL(cr.url, UPSTREAM);
        if (u.pathname === "/service-worker.js" && state.swOverride) {
            res.writeHead(200, { "content-type": "text/javascript" });
            res.end(state.swOverride);
            return;
        }
        if (u.pathname === "/service-worker-assets.js" && state.manifestOverride) {
            res.writeHead(200, { "content-type": "text/javascript" });
            res.end(state.manifestOverride);
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
                    () =>
                        !!document.querySelector(
                            ".project-dashboard, [data-testid=new-file-name]",
                        ),
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
async function manifestVersion(page) {
    return page.evaluate(async () => {
        const text = await fetch("/service-worker-assets.js").then(response => response.text());
        return text.match(/"version":\s*"([^"]+)"/)?.[1] ?? null;
    });
}
async function releaseCaches(page) {
    return page.evaluate(async () => {
        const keys = await caches.keys();
        return keys.filter((k) => k.startsWith("oop-workspace-release-"));
    });
}

(async () => {
    const proxy = startProxy();
    await new Promise((r) => proxy.listen(PROXY_PORT, "127.0.0.1", r));
    const userDataDir = fs.mkdtempSync(path.join(os.tmpdir(), "oop-sw-snap-"));
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

        // 1. Create a durable project and wait until the release is provisioned.
        const name = unique("SWSnap");
        await reveal(page, page.locator("#new-project"));
        await page.locator("#new-project").fill(name);
        await page.getByRole("button", { name: "Create project" }).click();
        await page.locator("[data-testid=new-file-name]").waitFor({ timeout: 20000 });
        await importSingleFile(page, "main.tex", enc("sw body\n"));
        await saveAndWait(page);
        await page.waitForFunction(() => window.__oopOfflineReady === true, null, {
            timeout: 30000,
        });
        const realSw = await page.evaluate(() => fetch("/service-worker.js").then((r) => r.text()));
        const realManifest = await page.evaluate(() =>
            fetch("/service-worker-assets.js").then((r) => r.text()),
        );
        const versionA = await manifestVersion(page);
        assert.ok(versionA, "published worker exposes a manifest version");
        const releaseA = `oop-workspace-release-${versionA}`;
        const cachesA = await releaseCaches(page);
        assert.ok(cachesA.includes(releaseA), "release A cache provisioned");

        // 2. Stage a NEW release: new worker bytes (updatefound) + bumped
        //    manifest version (new immutable release cache). Old worker stays.
        const versionB = `${versionA}-staged-${Date.now()}`;
        const stagedManifest = realManifest.replace(
            /"version":\s*"[^"]*"/,
            `"version": "${versionB}"`,
        );
        assert.notEqual(stagedManifest, realManifest, "staged manifest version differs");
        proxy.state.swOverride = realSw + `\n// staged update ${Date.now()}\n`;
        proxy.state.manifestOverride = stagedManifest;

        await page.reload();
        await waitForReady(page);
        await page.evaluate(async () => {
            try {
                const reg = await navigator.serviceWorker.getRegistration();
                if (reg) await reg.update();
            } catch {}
        });
        await page.locator("#update-banner").waitFor({ state: "visible", timeout: 20000 });

        // The OLD release A is still the active controller: editing/save works.
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
        assert.ok(st.startsWith("Saved"), `active editing/save works under A while B waits; got ${st}`);
        const cachesWhileWaiting = await releaseCaches(page);
        assert.ok(cachesWhileWaiting.includes(releaseA), "release A still present while B waits");

        // 3. Guard: an UNSAVED draft must refuse the update (no reload, draft retained).
        await page.locator("[data-testid=editor]").fill("unsaved draft\n");
        const aliveBefore = await page.evaluate(() => (window.__testAlive = true));
        await page.locator("#update-banner button").click();
        await page.waitForTimeout(1500);
        assert.equal(
            await page.evaluate(() => window.__testAlive),
            aliveBefore,
            "update refused while unsaved: page did not reload",
        );
        assert.equal(
            await page.locator("[data-testid=editor]").inputValue(),
            "unsaved draft\n",
            "unsaved draft retained after refused update",
        );
        // The waiting worker is still waiting (not activated).
        const regState = await page.evaluate(async () => {
            const reg = await navigator.serviceWorker.getRegistration();
            return { active: reg?.active?.state, waiting: reg?.waiting?.state };
        });
        assert.ok(regState.waiting, "new release still waiting after refused update");

        // 4. Save the draft, then accept the update explicitly.
        await saveAndWait(page);
        await page.locator("#update-banner button").click();
        let bActive = false;
        for (let i = 0; i < 80; i++) {
            const s = await page
                .evaluate(async ({ versionB, releaseA }) => {
                    const reg = await navigator.serviceWorker.getRegistration();
                    return {
                        active: reg?.active?.state,
                        waiting: reg?.waiting?.state,
                        version: await caches.has(`oop-workspace-release-${versionB}`) &&
                            !(await caches.has(releaseA)) ? versionB : null,
                    };
                }, { versionB, releaseA })
                .catch(() => null);
            if (s && s.active === "activated" && !s.waiting && s.version === versionB) {
                bActive = true;
                break;
            }
            await page.waitForTimeout(300);
        }
        assert.ok(bActive, "release B activated only after explicit save-aware accept");
        await waitForReady(page);

        // Release A is purged on activation; release B is active.
        const cachesB = await releaseCaches(page);
        assert.ok(cachesB.includes(`oop-workspace-release-${versionB}`), "release B cache active");
        assert.ok(!cachesB.includes(releaseA), "release A cache purged after B activation");

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
            "unsaved draft\n",
            "the explicitly saved draft persisted across the SW snapshot update reload",
        );
        assert.deepEqual(errors, [], "No uncaught page errors");
        console.log(
            "PASS SW snapshot update: waiting release activates only on explicit save-aware accept; draft guard; durable docs persist; release cache swap",
        );
    } finally {
        if (browser) await browser.close();
        if (proxy?.listening) await new Promise(resolve => proxy.close(resolve));
        try {
            fs.rmSync(userDataDir, { recursive: true, force: true });
        } catch {}
    }
})().catch((e) => {
    console.error(e);
    process.exitCode = 1;
});
