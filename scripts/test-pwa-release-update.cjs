// M4 offline-release-update correctness. Real published-browser flow run by
// lead against TWO actual static publications (A and B) with distinct C# Client
// content and distinct generated service-worker-assets.js versions. No compiler
// proof or complete authoring/parity claim is made here.
//
// Usage (lead): APP_A=<dirA> APP_B=<dirB> [BINARY_PATH=<pinned chromium>] \
//   node scripts/test-pwa-release-update.cjs
// Each dir is a published wwwroot (dotnet publish site/wwwroot). The lead builds
// B from a PRIVATE TEMPORARY clone with a C# rendered build-identity marker
// (NEVER committed; see docs/offline-release-updates.md for the exact patch).
//
// Proves on actual published bytes:
//  - Provision A, save a UTF-8/BOM/CRLF fixture, wait for genuine release readiness.
//  - Candidate B with exactly one required runtime/application asset 503, and
//    separately integrity-corrupted: the new install MUST reject (installing ->
//    redundant), A stays the active controller, no waiting worker / no banner,
//    no ready/accepted claim; offline cold restart (host STOPPED, offline before
//    navigation) restores the saved fixture's exact BOM/CRLF bytes from storage
//    and A's identity.
//  - Restore complete B: registration.update -> waiting banner -> save-aware
//    explicit update -> DOM shows B's managed rendered identity and manifest
//    version B; close/reopen offline with host STOPPED -> B identity + exact
//    saved bytes.
//  - An unrelated sentinel Cache API entry survives every cleanup; private and
//    authenticated requests are actually issued and never cached.

const assert = require("node:assert/strict");
const fs = require("node:fs");
const http = require("node:http");
const os = require("node:os");
const path = require("node:path");
const crypto = require("node:crypto");
const { chromium } = require("playwright");
const H = require("./review-ui-helpers.cjs");

const APP_A = process.env.APP_A;
const APP_B = process.env.APP_B;
if (!APP_A || !APP_B) {
    process.stderr.write("APP_A and APP_B must point to two published wwwroot dirs\n");
    process.exit(2);
}
const PORT = 8104;
const BASE = `http://127.0.0.1:${PORT}`;
const launch = { headless: true, args: ["--no-sandbox"] };
const EXPECTED_B_ID = process.env.BUILD_ID_B || "BUILD-B-PWA-SNAPSHOT-TEST";
const BOM = Buffer.from([239, 187, 191]);
const FIXTURE_TEXT = "перше🙂\r\nціль\r\nостаннє\r\n";
const fixtureBytes = Buffer.concat([BOM, Buffer.from(FIXTURE_TEXT, "utf8")]);

// --- A/B identity from the published files (content + manifest version) ---
function manifestOf(dir) {
    const t = fs.readFileSync(path.join(dir, "service-worker-assets.js"), "utf8");
    const m = t.match(/"version":\s*"([^"]*)"/);
    if (!m) throw new Error(`no manifest version in ${dir}`);
    return { version: m[1], text: t };
}
function clientWasmHash(dir) {
    const f = fs.readdirSync(path.join(dir, "_framework")).find((n) =>
        /^Client\.[A-Za-z0-9_-]+\.wasm$/.test(n),
    );
    if (!f) throw new Error(`no Client.*.wasm in ${dir}/_framework`);
    return { name: f, sha256: crypto.createHash("sha256").update(fs.readFileSync(path.join(dir, "_framework", f))).digest("hex") };
}
const identA = { manifest: manifestOf(APP_A), client: clientWasmHash(APP_A) };
const identB = { manifest: manifestOf(APP_B), client: clientWasmHash(APP_B) };
assert.notEqual(identA.manifest.version, identB.manifest.version, "A/B manifest versions differ");
assert.notEqual(identA.client.sha256, identB.client.sha256, "A/B Client wasm content differs");
// The SDK prepends a generated manifest-version comment. The owned worker
// implementation, apart from that producer comment, must stay byte-identical:
// this tests a content-only Client update, not a changed worker implementation.
const workerBody = root => fs.readFileSync(path.join(root, "service-worker.js"), "utf8")
    .replace(/^\/\* Manifest version: [^\r\n]*\*\/\r?\n/, "");
assert.equal(workerBody(APP_A), workerBody(APP_B), "A/B worker implementation differs beyond SDK comment");

// --- static switching server (chosen release ONLY; no B->A fallback) ---
function mimeOf(p) {
    if (p.endsWith(".js") || p.endsWith(".mjs")) return "text/javascript";
    if (p.endsWith(".wasm")) return "application/wasm";
    if (p.endsWith(".json")) return "application/json";
    if (p.endsWith(".css")) return "text/css";
    if (p.endsWith(".html")) return "text/html";
    return "application/octet-stream";
}
function startServer() {
    const state = { serve: "A", faultPath: null, faultKind: null, faultRequests: 0 };
    const server = http.createServer((cr, res) => {
        const url = new URL(cr.url, BASE);
        const root = state.serve === "B" ? APP_B : APP_A;
        // Reject traversal; serve the chosen release only.
        let rel;
        try { rel = decodeURIComponent(url.pathname).replace(/^\/+/, ""); }
        catch { res.writeHead(400); res.end(); return; }
        if (rel.includes("..")) { res.writeHead(403); res.end(); return; }
        let file = path.join(root, rel);
        // SPA fallback: a directory or missing non-asset path serves index.html.
        let body;
        if (fs.existsSync(file) && fs.statSync(file).isFile()) body = fs.readFileSync(file);
        else if (url.pathname === "/" || !path.extname(rel)) {
            body = fs.readFileSync(path.join(root, "index.html"));
            res.writeHead(200, { "content-type": "text/html" });
            res.end(body);
            return;
        } else { res.writeHead(404); res.end(); return; }
        if (state.faultPath && "/" + rel === state.faultPath) {
            state.faultRequests++;
            if (state.faultKind === "503") { res.writeHead(503); res.end(); return; }
            if (state.faultKind === "corrupt") {
                const corrupted = Buffer.from(body);
                if (corrupted.length > 0) corrupted[0] ^= 0x01;
                res.writeHead(200, { "content-type": mimeOf(rel) });
                res.end(corrupted);
                return;
            }
        }
        res.writeHead(200, { "content-type": mimeOf(rel) });
        res.end(body);
    });
    server.state = state;
    return server;
}

async function ready(page) {
    for (let i = 0; i < 120; i++) {
        if (await page.evaluate(() => !!document.querySelector(".project-dashboard,[data-testid=new-file-name]"))) return;
        await page.waitForTimeout(500);
    }
    throw new Error("UI not ready");
}
async function releaseReady(page) {
    await page.waitForFunction(() => window.__oopOfflineReady === true, null, { timeout: 90000 });
}
async function domIdentity(page) {
    return page.evaluate(() => {
        const el = document.querySelector("[data-testid=build-identity]");
        return el ? el.textContent : null;
    });
}
async function regState(page) {
    return page.evaluate(async () => {
        const reg = await navigator.serviceWorker.getRegistration();
        return {
            active: reg?.active?.state,
            waiting: reg?.waiting?.state,
            installing: reg?.installing?.state,
            controller: !!navigator.serviceWorker.controller,
        };
    });
}
// Retain the installing ServiceWorker object: registration.installing is
// cleared when it becomes redundant, so polling that property loses failures.
// Subscribe BEFORE update() and keep the worker's actual statechange evidence.
async function assertInstallRejected(page, faultUrl) {
    const states = await page.evaluate(async () => {
        const registration = await navigator.serviceWorker.getRegistration();
        if (!registration || registration.waiting) throw new Error("expected active release with no waiting candidate");
        return new Promise((resolve, reject) => {
            let candidate;
            const observed = [];
            const timer = setTimeout(() => finish(new Error("candidate did not become redundant")), 30000);
            function finish(error) {
                clearTimeout(timer);
                registration.removeEventListener("updatefound", found);
                candidate?.removeEventListener("statechange", changed);
                if (error) reject(error); else resolve(observed);
            }
            function changed() {
                observed.push(candidate.state);
                if (candidate.state === "redundant") finish();
                else if (candidate.state === "installed" || candidate.state === "activated")
                    finish(new Error("faulted candidate unexpectedly installed"));
            }
            function found() {
                if (candidate || !registration.installing) return;
                candidate = registration.installing;
                candidate.addEventListener("statechange", changed);
                changed();
            }
            registration.addEventListener("updatefound", found);
            found();
            registration.update().catch(finish);
        });
    });
    assert.ok(states.includes("installing") && states.includes("redundant"), `install rejected: ${faultUrl}`);
    const s = await regState(page);
    assert.ok(!s.waiting, "no waiting worker from a rejected install");
    assert.equal(s.active, "activated", "A remains the active controller");
}
async function assertReleaseIdentity(page, identity, managedId) {
    // The SDK manifest is a classic SW import (not an ES-module export, and
    // deliberately not page-cached). Check the release cache plus the actual
    // Client assembly resource loaded by Blazor and the managed DOM marker.
    assert.equal(await page.evaluate(version => caches.has(`oop-workspace-release-${version}`),
        identity.manifest.version), true, "expected release cache remains present");
    const resources = await page.evaluate(() => performance.getEntriesByType("resource")
        .flatMap(entry => { try { return [new URL(entry.name).pathname]; } catch { return []; } }));
    assert.ok(resources.includes(`/_framework/${identity.client.name}`), "actual Blazor Client resource identity");
    assert.equal(await domIdentity(page), managedId, "managed rendered release identity");
}

(async () => {
    const proxy = startServer();
    await new Promise((r) => proxy.listen(PORT, "127.0.0.1", r));
    const profile = fs.mkdtempSync(path.join(os.tmpdir(), "oop-pwa-release-"));
    let browser;
    try {
        // 1. Provision A.
        browser = await chromium.launchPersistentContext(profile, launch);
        const page = await browser.newPage();
        const errors = [];
        page.on("pageerror", (e) => errors.push(String(e)));
        await page.goto(BASE, { waitUntil: "domcontentloaded" });
        await ready(page);
        await page.evaluate(async () => { const m = await import("./js/workspace-storage.js"); await m.clearAll(); });
        await page.reload();
        await ready(page);
        const nameA = `PWA-A-${Date.now()}`;
        await H.reveal(page, page.locator("#new-project"));
        await page.locator("#new-project").fill(nameA);
        await page.getByRole("button", { name: "Create project", exact: true }).click();
        await page.locator("[data-testid=new-file-name]").waitFor({ timeout: 20000 });
        await page.locator("[data-testid=import-input]").setInputFiles({ name: "main.tex", mimeType: "text/plain", buffer: fixtureBytes });
        await page.locator(".source-tab", { hasText: "main.tex" }).waitFor();
        await H.save(page);
        await releaseReady(page);
        const identityA = await domIdentity(page);
        assert.equal(identityA, null, "A is the unmodified managed build without the private B marker");
        await assertReleaseIdentity(page, identA, identityA);
        // Inject an unrelated sentinel cache that must survive every cleanup.
        await page.evaluate(async () => { const c = await caches.open("sentinel-cache"); await c.put(new Request("/sentinel"), new Response("keep")); });
        // A real release snapshot is cached.
        assert.ok(await page.evaluate(async () => (await caches.keys()).some((k) => k.startsWith("oop-workspace-release-"))), "release A produced an immutable release cache");

        // 2. Interrupted B: one required asset 503. Observe install rejected (not a sleep).
        const manifestB = JSON.parse(identB.manifest.text.replace(/^self\.assetsManifest\s*=\s*/, "").replace(/;?\s*$/, ""));
        const requiredAsset = manifestB.assets.find((a) => /_framework\/dotnet\.(?:runtime|native)\.[A-Za-z0-9_-]+\.js$/.test(a.url));
        assert.ok(requiredAsset, "candidate B has a fingerprinted runtime/native JS asset");
        const faultPath = "/" + requiredAsset.url;
        proxy.state.serve = "B";
        proxy.state.faultPath = faultPath;
        proxy.state.faultKind = "503";
        const requestsBefore503 = proxy.state.faultRequests;
        await assertInstallRejected(page, faultPath + " (503)");
        assert.ok(proxy.state.faultRequests > requestsBefore503, "real 503 request reached candidate runtime asset");
        // No update banner for a rejected install.
        assert.equal(await page.locator("#update-banner").count(), 0, "no update banner for a rejected B install");
        // A identity unchanged (DOM + manifest version).
        assert.equal(await domIdentity(page), identityA, "A managed identity unchanged after rejected B");
        await assertReleaseIdentity(page, identA, identityA);
        // Actually issue a private/authenticated request and assert it is never cached.
        await page.evaluate(async () => {
            await fetch("/api/private?token=secret", { headers: { authorization: "Bearer x" } }).catch(() => {});
            await fetch("/_framework/dotnet.runtime.unknown.js?v=v10&token=x").catch(() => {});
        });
        await page.waitForTimeout(500);
        const privateCached = await page.evaluate(async () => !!(await caches.match("/api/private?token=secret")) || !!(await caches.match("/_framework/dotnet.runtime.unknown.js?v=v10&token=x")));
        assert.equal(privateCached, false, "private/authenticated/API requests never cached");
        // Sentinel survives the rejected install.
        assert.ok(await page.evaluate(async () => !!(await caches.match("/sentinel"))), "unrelated sentinel cache survives a rejected install");

        // 3. Cold offline restart (host STOPPED, offline BEFORE navigation) restores A + exact BOM/CRLF bytes.
        await page.close();
        await browser.close();
        browser = null;
        await new Promise((r) => proxy.close(r));
        browser = await chromium.launchPersistentContext(profile, launch);
        const coldA = await browser.newPage();
        await browser.setOffline(true);
        await coldA.goto(BASE, { waitUntil: "domcontentloaded" });
        await ready(coldA);
        await coldA.locator("[data-testid=project-row]", { hasText: nameA }).waitFor({ timeout: 20000 });
        await coldA.locator("[data-testid=project-row]", { hasText: nameA }).locator("[data-testid=open-project]").click();
        await coldA.locator("[data-testid=editor]").waitFor({ state: "visible" });
        // Exact saved BOM/CRLF bytes from durable storage (real head read, not a C# call).
        await H.assertBytes(coldA, nameA, fixtureBytes);
        assert.equal(await domIdentity(coldA), identityA, "cold A shows A identity");
        await assertReleaseIdentity(coldA, identA, identityA);
        await coldA.close();
        await browser.close();
        browser = null;

        // 4. Integrity-corrupted B: same proof. Restart serving A first so a
        // navigation's automatic update check cannot race the failure observer.
        proxy.state.serve = "A";
        await new Promise((r) => proxy.listen(PORT, "127.0.0.1", r));
        browser = await chromium.launchPersistentContext(profile, launch);
        const page2 = await browser.newPage();
        page2.on("pageerror", (e) => errors.push(String(e)));
        await page2.goto(BASE, { waitUntil: "domcontentloaded" });
        await ready(page2);
        proxy.state.serve = "B";
        proxy.state.faultKind = "corrupt";
        const requestsBeforeCorrupt = proxy.state.faultRequests;
        await assertInstallRejected(page2, faultPath + " (corrupt)");
        assert.ok(proxy.state.faultRequests > requestsBeforeCorrupt, "real corrupted runtime response reached install");
        await assertReleaseIdentity(page2, identA, identityA);
        assert.equal(await page2.locator("#update-banner").count(), 0, "no update banner for a corrupted B install");
        // Cold offline after corrupt install: A still restores exact bytes.
        await page2.close();
        await browser.close();
        browser = null;
        await new Promise((r) => proxy.close(r));
        browser = await chromium.launchPersistentContext(profile, launch);
        const coldA2 = await browser.newPage();
        await browser.setOffline(true);
        await coldA2.goto(BASE, { waitUntil: "domcontentloaded" });
        await ready(coldA2);
        await coldA2.locator("[data-testid=project-row]", { hasText: nameA }).waitFor({ timeout: 20000 });
        await coldA2.locator("[data-testid=project-row]", { hasText: nameA }).locator("[data-testid=open-project]").click();
        await coldA2.locator("[data-testid=editor]").waitFor({ state: "visible" });
        await H.assertBytes(coldA2, nameA, fixtureBytes);
        await assertReleaseIdentity(coldA2, identA, identityA);
        assert.ok(await coldA2.evaluate(async () => !!(await caches.match("/sentinel"))), "sentinel retained after corrupt install");
        await coldA2.close();
        await browser.close();
        browser = null;

        // 5. Complete B -> save-aware explicit update.
        proxy.state.serve = "A";
        await new Promise((r) => proxy.listen(PORT, "127.0.0.1", r));
        browser = await chromium.launchPersistentContext(profile, launch);
        const page3 = await browser.newPage();
        page3.on("pageerror", (e) => errors.push(String(e)));
        await page3.goto(BASE, { waitUntil: "domcontentloaded" });
        await ready(page3);
        proxy.state.serve = "B";
        proxy.state.faultPath = null;
        proxy.state.faultKind = null;
        await page3.reload({ waitUntil: "domcontentloaded" });
        await ready(page3);
        await page3.evaluate(async () => { try { const reg = await navigator.serviceWorker.getRegistration(); if (reg) await reg.update(); } catch {} });
        await page3.locator("#update-banner").waitFor({ state: "visible", timeout: 20000 });
        const beforeAccept = await regState(page3);
        assert.ok(beforeAccept.waiting, "complete B is waiting for explicit acceptance");
        // Save-aware update: the existing C# UpdateGuard owns the reload.
        await page3.locator("#update-banner button").click();
        let bActive = false;
        for (let i = 0; i < 80; i++) {
            const s = await page3.evaluate(async () => {
                const reg = await navigator.serviceWorker.getRegistration();
                return { active: reg?.active?.state, waiting: reg?.waiting?.state,
                    releases: (await caches.keys()).filter(name => name.startsWith("oop-workspace-release-")) };
            }).catch(() => null);
            if (s && s.active === "activated" && !s.waiting &&
                s.releases.includes(`oop-workspace-release-${identB.manifest.version}`) &&
                !s.releases.includes(`oop-workspace-release-${identA.manifest.version}`)) { bActive = true; break; }
            await page3.waitForTimeout(300);
        }
        assert.ok(bActive, "complete B activated only after explicit save-aware accept, manifest version B");
        await page3.waitForFunction(expected => document.querySelector("[data-testid=build-identity]")?.textContent === expected,
            EXPECTED_B_ID, { timeout: 60000 });
        await ready(page3);
        // B managed rendered identity is now in the DOM and differs from A.
        const identityB = await domIdentity(page3);
        assert.ok(identityB, "candidate B exposes its managed rendered identity");
        assert.notEqual(identityB, identityA, "B managed identity differs from A");
        await assertReleaseIdentity(page3, identB, EXPECTED_B_ID);

        // 6. Close/reopen offline (host STOPPED) -> B identity + exact saved bytes + sentinel.
        await page3.close();
        await browser.close();
        browser = null;
        await new Promise((r) => proxy.close(r));
        browser = await chromium.launchPersistentContext(profile, launch);
        const coldB = await browser.newPage();
        await browser.setOffline(true);
        await coldB.goto(BASE, { waitUntil: "domcontentloaded" });
        await ready(coldB);
        await coldB.locator("[data-testid=project-row]", { hasText: nameA }).waitFor({ timeout: 20000 });
        await coldB.locator("[data-testid=project-row]", { hasText: nameA }).locator("[data-testid=open-project]").click();
        await coldB.locator("[data-testid=editor]").waitFor({ state: "visible" });
        await H.assertBytes(coldB, nameA, fixtureBytes);
        assert.equal(await domIdentity(coldB), identityB, "cold B shows B managed identity");
        await assertReleaseIdentity(coldB, identB, EXPECTED_B_ID);
        assert.ok(await coldB.evaluate(async () => !!(await caches.match("/sentinel"))), "unrelated sentinel cache survives the B activation cleanup");
        assert.deepEqual(errors, [], "No uncaught page errors");
        process.stdout.write(
            "PASS PWA release update: interrupted(503)+corrupt B rejected, A retained offline with exact BOM/CRLF bytes; complete B save-aware update; B identity + exact bytes offline; sentinel survives; private never cached\n",
        );
    } finally {
        if (browser) await browser.close();
        if (proxy.listening) await new Promise((r) => proxy.close(r));
        try { fs.rmSync(profile, { recursive: true, force: true }); } catch {}
    }
})().catch((error) => {
    process.stderr.write(`${error.stack || error}\n`);
    process.exitCode = 1;
});
