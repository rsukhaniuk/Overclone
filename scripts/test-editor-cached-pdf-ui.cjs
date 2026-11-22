// Published Chromium regression. PREVIOUS_WWWROOT optionally tests an actual
// older release with the legacy update button on the same browser origin.
const assert = require("node:assert/strict");
const { spawn } = require("node:child_process");
const { createHash } = require("node:crypto");
const { once } = require("node:events");
const { setTimeout: delay } = require("node:timers/promises");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { chromium } = require("playwright");
const H = require("./review-ui-helpers.cjs");
const { $ } = H;

const origin = "http://127.0.0.1:8098";
const project = "Cached compilation";
const profile = fs.mkdtempSync(path.join(os.tmpdir(), "overclone-pdf-cache-"));
const source = "\\documentclass{article}\n\\begin{document}\nExact cached PDF text.\n\\end{document}\n";
const edited = source.replace("Exact cached PDF text.", "Revised source after saving the PDF.");
const sha = bytes => createHash("sha256").update(bytes).digest("hex");

async function until(check, message) {
    const deadline = Date.now() + 90000;
    while (Date.now() < deadline) {
        const value = await check();
        if (value) return value;
        await delay(150);
    }
    assert.fail(message);
}

(async () => {
    const site = process.env.PUBLISHED_WWWROOT;
    const previous = process.env.PREVIOUS_WWWROOT;
    assert.ok(site, "set PUBLISHED_WWWROOT");
    let server, browser, page, compilerActivity;
    async function serve(root) {
        server = spawn(process.execPath, [path.join(__dirname, "static-server.cjs"), root, "8098"], { stdio: ["ignore", "pipe", "inherit"] });
        await once(server.stdout, "data");
    }
    async function stopOrigin() {
        if (!server) return;
        const stopped = once(server, "exit");
        server.kill();
        await stopped;
        server = null;
    }
    async function launch(offline = false) {
        browser = await chromium.launchPersistentContext(profile, { headless: true, acceptDownloads: true, viewport: { width: 1440, height: 900 } });
        if (offline) await browser.setOffline(true);
        page = await browser.newPage();
        compilerActivity = [];
        page.on("request", r => {
            if (r.url().endsWith("/bundled-runtime/compiler-suite.zip")) compilerActivity.push(r.url());
        });
        page.on("worker", w => {
            if (/\/__oop\/native-worker\/(?:compile|dvi|xetex|luatex|biber)\b/.test(w.url())) compilerActivity.push(w.url());
        });
        await page.goto(origin);
        await H.ready(page);
    }
    async function close() {
        await browser.close();
        browser = null;
    }
    async function create() {
        await H.reveal(page, page.locator("#new-project"));
        await page.locator("#new-project").fill(project);
        await page.getByRole("button", { name: "Create project", exact: true }).click();
        await $(page, "starter-article").click();
        await $(page, "editor").fill(source);
        await saved(source);
    }
    async function saved(text) {
        await page.waitForFunction(() => document.querySelector(".workspace-header .status")?.textContent === "Saved.");
        await H.assertBytes(page, project, Buffer.from(text));
    }
    async function rendered(phrase) {
        await page.waitForFunction(expected =>
            document.querySelector('[data-testid="canvas-status"]')?.textContent === "Drawn"
            && document.querySelector('[data-testid="page-text"]')?.textContent.includes(expected),
        phrase, { timeout: 90000 });
        return $(page, "pdf-pane").getAttribute("data-document");
    }
    async function storedHash() {
        return page.evaluate(async name => {
            const storage = await import("/js/workspace-storage.js");
            const project = (await storage.listProjects()).find(p => p.name === name);
            const row = await storage.readLatestCompiledPdf(project.id);
            if (!row) return null;
            const digest = await crypto.subtle.digest("SHA-256", row.pdf);
            return Array.from(new Uint8Array(digest), b => b.toString(16).padStart(2, "0")).join("");
        }, project);
    }

    try {
        await serve(previous || site);
        await launch();
        await create();
        if (previous) {
            await page.waitForFunction(() => navigator.serviceWorker.controller?.state === "activated", null, { timeout: 90000 });
            await stopOrigin();
            await serve(site);
            await page.evaluate(async () => { await (await navigator.serviceWorker.getRegistration()).update(); });
            await until(() => page.evaluate(async () =>
                (await navigator.serviceWorker.getRegistration())?.waiting?.state === "installed"), "new worker should wait for the older tab");
            await page.getByRole("button", { name: "Reload to update", exact: true }).waitFor();
            await close();
            await launch();
            assert.equal(await page.getByRole("button", { name: "Reload to update", exact: true }).count(), 0);
            await H.open(page, project);
            await H.assertBytes(page, project, Buffer.from(source));
            assert.deepEqual(compilerActivity, []);
            console.log("Legacy update button replaced on first reopen, with exact v1 project bytes intact");
        }

        await $(page, "compile").click();
        const pdfHash = await rendered("Exact cached PDF text.");
        assert.match(pdfHash, /^[a-f0-9]{64}$/);
        await until(async () => await storedHash() === pdfHash, "accepted PDF should be cached in the browser");
        await close();
        await stopOrigin();

        await launch(true);
        await H.open(page, project);
        assert.equal(await rendered("Exact cached PDF text."), pdfHash);
        assert.equal(await $(page, "compiled-pdf-status").isVisible(), false);
        assert.deepEqual(compilerActivity, [], "cached preview should not load TeX resources or start a compiler worker");
        const [download] = await Promise.all([
            page.waitForEvent("download"),
            $(page, "download-pdf").click(),
        ]);
        assert.equal(sha(fs.readFileSync(await download.path())), pdfHash);
        const line = page.locator(".pdf-text-layer span", { hasText: "Exact cached PDF text." });
        const box = await line.boundingBox();
        await page.mouse.move(box.x + 0.5, box.y + box.height / 2);
        await page.mouse.down();
        await page.mouse.move(box.x + box.width - 0.5, box.y + box.height / 2, { steps: 15 });
        await page.mouse.up();
        assert.match(await page.evaluate(() => getSelection().toString()), /Exact cached PDF text/);
        await $(page, "editor").fill(edited);
        await saved(edited);
        await $(page, "compiled-pdf-status").waitFor();
        await close();

        await launch(true);
        await H.open(page, project);
        assert.equal(await rendered("Exact cached PDF text."), pdfHash);
        assert.match(await $(page, "compiled-pdf-status").innerText(), /out of date/);
        await H.assertBytes(page, project, Buffer.from(edited));
        assert.deepEqual(compilerActivity, []);

        const broken = edited.replace("\\end{document}", "\\undefinedCachedPdfTest % intentional error\n\\end{document}");
        await $(page, "editor").fill(broken);
        await saved(broken);
        await $(page, "compile").click();
        await page.waitForFunction(() => document.querySelector('[data-testid="compile-details"]')?.textContent.includes("Outcome: CompilerFailed;"),
            null, { timeout: 90000 });
        assert.equal(await rendered("Exact cached PDF text."), pdfHash);
        assert.equal(await storedHash(), pdfHash);

        await $(page, "editor").fill(edited);
        await $(page, "compile").click();
        const nextHash = await rendered("Revised source after saving the PDF.");
        assert.notEqual(nextHash, pdfHash);
        await until(async () => await storedHash() === nextHash, "new successful compile should replace the previous cache");
        assert.ok(compilerActivity.some(url => url.includes("/__oop/native-worker/compile")));
        assert.equal(await $(page, "compiled-pdf-status").isVisible(), false);
        console.log("PASS cold offline cached PDF, exact download, text selection, stale source, explicit offline compile and latest cache replacement");
    } finally {
        if (browser) await close();
        await stopOrigin();
        fs.rmSync(profile, { recursive: true, force: true });
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
