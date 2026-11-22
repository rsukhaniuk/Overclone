// Published app with real browser storage and a second integrity-checked shell
// version on the same origin. No manual save or update action is used.
const assert = require("node:assert/strict");
const { createHash } = require("node:crypto");
const fs = require("node:fs");
const http = require("node:http");
const os = require("node:os");
const path = require("node:path");
const { setTimeout: delay } = require("node:timers/promises");
const { chromium } = require("playwright");
const H = require("./review-ui-helpers.cjs");
const { $ } = H;

const root = fs.realpathSync(process.env.PUBLISHED_WWWROOT);
const project = "Autosave and quiet updates";
const profile = fs.mkdtempSync(path.join(os.tmpdir(), "overclone-autosave-"));
const read = name => fs.readFileSync(path.join(root, name), "utf8");
const worker = read("service-worker.js");
const manifest = JSON.parse(read("service-worker-assets.js").match(/self\.assetsManifest\s*=\s*(\{[\s\S]*\})\s*;/)[1]);
const successor = manifest.version + "-autosave-test";
const marker = '<meta name="autosave-test-release" content="successor" />';
const html = read("index.html").replace("</head>", marker + "\n</head>");
const nextManifest = {
    ...manifest, version: successor,
    assets: manifest.assets.map(asset => asset.url === "index.html"
        ? { ...asset, hash: "sha256-" + createHash("sha256").update(html).digest("base64") }
        : asset)
};
const mime = { ".html": "text/html", ".js": "text/javascript", ".mjs": "text/javascript",
    ".css": "text/css", ".wasm": "application/wasm", ".json": "application/json" };
let serveSuccessor = false;
let archiveRequests = 0;
const server = http.createServer((request, response) => {
    const route = new URL(request.url, "http://127.0.0.1").pathname;
    const name = route === "/" ? "index.html" : route.slice(1);
    if (name === "bundled-runtime/compiler-suite.zip") archiveRequests++;
    const headers = { "Content-Type": mime[path.extname(name)] || "application/octet-stream", "Cache-Control": "no-cache" };
    const revised = serveSuccessor && {
        "service-worker.js": worker.replace(manifest.version, successor),
        "service-worker-assets.js": "self.assetsManifest = " + JSON.stringify(nextManifest) + ";",
        "index.html": html,
    }[name];
    if (revised) { response.writeHead(200, headers).end(revised); return; }
    const stream = fs.createReadStream(path.join(root, name));
    stream.on("open", () => { response.writeHead(200, headers); stream.pipe(response); });
    stream.on("error", () => { if (!response.headersSent) response.writeHead(404).end(); });
});
const launch = { headless: true, viewport: { width: 1440, height: 900 } };

async function saved(page) {
    await page.waitForFunction(() => document.querySelector(".workspace-header .status")?.textContent === "Saved."
        && !document.querySelector('[data-testid="save"]')?.disabled);
}
async function guard(page) {
    return page.evaluate(() => {
        const event = new Event("beforeunload", { cancelable: true });
        window.dispatchEvent(event);
        return event.defaultPrevented;
    });
}
async function waitForWorker(check, message) {
    const deadline = Date.now() + 90000;
    while (Date.now() < deadline) {
        if (await check()) return;
        await delay(200);
    }
    assert.fail(message);
}

(async () => {
    await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
    const url = "http://127.0.0.1:" + server.address().port;
    let context;
    try {
        context = await chromium.launchPersistentContext(profile, launch);
        const page = await context.newPage();
        let navigations = 0;
        page.on("framenavigated", frame => {
            if (frame === page.mainFrame()) navigations++;
        });
        const reads = [];
        page.on("request", request => {
            if (request.url().endsWith("/bundled-runtime/compiler-suite.zip")) reads.push(request.url());
        });
        await page.goto(url);
        await H.ready(page);
        await H.reveal(page, page.locator("#new-project"));
        await page.locator("#new-project").fill(project);
        await page.getByRole("button", { name: "Create project", exact: true }).click();
        await $(page, "starter-article").click();
        const editor = $(page, "editor");
        const original = await editor.inputValue();
        await saved(page);
        await H.assertBytes(page, project, Buffer.from(original));
        const savedHeader = await H.head(page, project);
        await page.getByRole("button", { name: "Save source", exact: true }).click();
        await page.getByText("Source saved", { exact: true }).waitFor();
        assert.deepEqual(await H.head(page, project), savedHeader, "saving an already saved source must not create a revision");

        const manual = original.replace("Write your document here.", "Ручне збереження 🙂.");
        await editor.fill(manual);
        await page.getByRole("button", { name: "Save source", exact: true }).click();
        await page.getByText("Source saved", { exact: true }).waitFor();
        await H.assertBytes(page, project, Buffer.from(manual));
        await page.waitForFunction(() => navigator.serviceWorker.controller?.state === "activated", null, { timeout: 90000 });
        assert.ok(archiveRequests > 0, "service worker caches the compiler archive");
        assert.deepEqual(reads, [], "project open and editing do not load the archive into the page");

        const changed = original.replace("Write your document here.", "Київ 🙂, automatically saved.");
        await editor.fill(changed);
        assert.equal(await guard(page), true, "dirty writes protect browser close");
        await saved(page);
        await H.assertBytes(page, project, Buffer.from(changed));
        assert.equal(await guard(page), false, "durable save releases the guard");

        await H.reveal(page, $(page, "new-file-name"));
        await $(page, "new-file-name").fill("scratch.tex");
        await $(page, "new-file").click();
        await saved(page);
        await H.assertBytes(page, project, Buffer.alloc(0), "scratch.tex");
        await H.reveal(page, $(page, "rename-input"));
        await $(page, "rename-input").fill("renamed.tex");
        await $(page, "rename").click();
        await saved(page);
        await H.assertBytes(page, project, Buffer.alloc(0), "renamed.tex");
        await page.getByRole("button", { name: "Delete renamed.tex", exact: true }).click();
        await saved(page);
        assert.deepEqual((await H.head(page, project)).files.map(f => f.path), ["main.tex"]);

        const imported = Buffer.from("\ufeffКиїв 🙂\r\n% preserve bytes\r");
        await $(page, "import-input").setInputFiles({ name: "notes.tex", mimeType: "text/plain", buffer: imported });
        await page.locator(".source-tab", { hasText: "notes.tex" }).waitFor();
        await saved(page);
        await H.assertBytes(page, project, imported, "notes.tex");

        // The candidate installs while this editor is open. Keep writing in A.
        serveSuccessor = true;
        const navigationsBeforeUpdate = navigations;
        await page.evaluate(async () => { await (await navigator.serviceWorker.getRegistration()).update(); });
        await waitForWorker(() => page.evaluate(async () =>
            (await navigator.serviceWorker.getRegistration())?.waiting?.state === "installed"), "successor should wait while the editor is open");
        assert.equal(await page.evaluate(async version => {
            const snapshot = await caches.open("oop-workspace-release-" + version);
            return (await (await snapshot.match("/index.html")).text()).includes('name="autosave-test-release"');
        }, successor), true, "candidate cache contains the successor HTML");
        assert.equal(await page.locator("#update-banner").count(), 0);
        assert.equal(await page.getByRole("button", { name: "Reload to update" }).count(), 0);
        await $(page, "select-file").filter({ hasText: "main.tex" }).click();
        const finalText = changed.replace("automatically saved", "saved with an update waiting");
        await editor.fill(finalText);
        await saved(page);
        await H.assertBytes(page, project, Buffer.from(finalText));
        assert.equal(navigations, navigationsBeforeUpdate);
        assert.equal(await page.locator('meta[name="autosave-test-release"]').count(), 0);

        await context.close();
        context = null;
        context = await chromium.launchPersistentContext(profile, launch);
        const reopened = await context.newPage();
        const navigation = await reopened.goto(url);
        await H.ready(reopened);
        await waitForWorker(() => reopened.evaluate(async version => {
            const registration = await navigator.serviceWorker.getRegistration();
            const releases = (await caches.keys()).filter(key => key.startsWith("oop-workspace-release-"));
            return !registration?.waiting && navigator.serviceWorker.controller?.state === "activated"
                && releases.length === 1 && releases[0] === "oop-workspace-release-" + version;
        }, successor), "successor should activate after the browser closes");
        assert.ok((await navigation.text()).includes(marker), "reopened navigation uses the successor snapshot");
        assert.equal(await reopened.locator('meta[name="autosave-test-release"]').getAttribute("content"), "successor");
        await H.open(reopened, project);
        await H.assertBytes(reopened, project, Buffer.from(finalText));
        await H.assertBytes(reopened, project, imported, "notes.tex");

        await context.close();
        context = null;
        const stopped = new Promise(resolve => server.close(resolve));
        server.closeAllConnections();
        await stopped;
        context = await chromium.launchPersistentContext(profile, launch);
        await context.setOffline(true);
        const offline = await context.newPage();
        await offline.goto(url);
        await H.ready(offline);
        await H.open(offline, project);
        await H.assertBytes(offline, project, Buffer.from(finalText));
        await H.assertBytes(offline, project, imported, "notes.tex");
        console.log("PASS automatic article, text, create, rename, delete and import persistence; quiet waiting update; exact offline reopen");
    } finally {
        if (context) await context.close();
        server.close();
        server.closeAllConnections();
        fs.rmSync(profile, { recursive: true, force: true });
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
