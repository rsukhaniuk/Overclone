// Upgrade a real IndexedDB v2 workspace, then exercise the project-scoped report store.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { once } = require("node:events");
const { spawn } = require("node:child_process");
const { chromium } = require("playwright");
const H = require("./review-ui-helpers.cjs");

const { PREVIOUS_PUBLISHED_WWWROOT, PUBLISHED_WWWROOT } = process.env;
const origin = "http://127.0.0.1:8127";
const project = "Checks storage upgrade";
const source = Buffer.from("\ufeff\\documentclass{article}\r\n\\begin{document}\r\nУкраїна🙂\r\n\\end{document}\r\n");

(async () => {
    assert.ok(PREVIOUS_PUBLISHED_WWWROOT && PUBLISHED_WWWROOT);
    const profile = fs.mkdtempSync(path.join(os.tmpdir(), "overclone-checks-upgrade-"));
    let context, server, page;
    async function start(root) {
        server = spawn(process.execPath, [path.join(__dirname, "static-server.cjs"), root, "8127"], { stdio: ["ignore", "pipe", "inherit"] });
        await once(server.stdout, "data");
        context = await chromium.launchPersistentContext(profile, { headless: true, serviceWorkers: "block" });
        page = await context.newPage();
        await page.goto(origin);
        await H.ready(page);
    }
    async function stop() {
        if (context) { await context.close(); context = null; }
        if (server) {
            if (server.exitCode === null) {
                const exited = once(server, "exit");
                server.kill();
                await exited;
            }
            server = null;
        }
    }
    function storageVersion() {
        return page.evaluate(async () => (await indexedDB.databases()).find(db => db.name === "oop-workspace")?.version);
    }
    try {
        await start(PREVIOUS_PUBLISHED_WWWROOT);
        await H.reveal(page, page.locator("#new-project"));
        await page.locator("#new-project").fill(project);
        await page.getByRole("button", { name: "Create project", exact: true }).click();
        const input = H.$(page, "import-input");
        await input.setInputFiles({ name: "main.tex", mimeType: "text/plain", buffer: source });
        await page.locator(".source-tab").getByText("main.tex", { exact: true }).waitFor();
        await page.waitForFunction(() => document.querySelector(".workspace-header .status")?.textContent === "Saved.");
        const original = await H.assertBytes(page, project, source);
        assert.equal(await storageVersion(), 2);
        await stop();

        await start(PUBLISHED_WWWROOT);
        await H.open(page, project);
        assert.equal(await storageVersion(), 3);
        assert.deepEqual(await H.assertBytes(page, project, source), original);
        await H.analyze(page);
        await H.$(page, "analysis-check-storage").filter({ hasText: "Report saved in this browser." }).waitFor();
        await page.locator('[data-checks-tab="saved"]').click();
        await H.$(page, "saved-check").filter({ hasText: "Engine checks" }).waitFor();
        assert.deepEqual(await H.assertBytes(page, project, source), original);
        await page.getByRole("button", { name: "Close", exact: true }).click();
        await page.locator(".project-dashboard").waitFor();
        // Keep the live source report, then populate older fixture records with
        // the actual writer gate to exercise the archive's paging boundary.
        const added = await page.evaluate(async projectName => {
            const storage = await import("./js/workspace-storage.js");
            const projectRow = (await storage.listProjects()).find(row => row.name === projectName);
            const [first] = await storage.readCheckReports(projectRow.id, null, null, 20);
            const template = JSON.parse(first);
            const lock = await storage.tryAcquireLock("oop-project:" + projectRow.id);
            if (!lock) return 0;
            let count = 0;
            try {
                for (let index = 1; index <= 21; index++) {
                    const entry = { ...template, Id: "older-" + index, Created: template.Created - index, Title: "Older fixture " + index };
                    if (await storage.writeCheckReport(projectRow.id, lock, entry.Id, entry.Created, JSON.stringify(entry))) count++;
                }
                return count;
            } finally {
                storage.releaseLock(lock);
            }
        }, project);
        assert.equal(added, 21);
        await H.open(page, project);
        await H.$(page, "open-checks").click();
        await page.locator('[data-checks-tab="saved"]').click();
        await H.$(page, "saved-check").first().waitFor();
        assert.equal(await H.$(page, "saved-check").count(), 20);
        await page.getByRole("button", { name: "Load older reports", exact: true }).click();
        await H.$(page, "saved-check").filter({ hasText: "Older fixture 21" }).waitFor();
        assert.equal(await H.$(page, "saved-check").count(), 22);
        await page.getByRole("button", { name: "Close", exact: true }).click();
        await page.locator(".project-dashboard").waitFor();
        const isolated = await page.evaluate(async projectName => {
            const storage = await import("./js/workspace-storage.js");
            const projectRow = (await storage.listProjects()).find(row => row.name === projectName);
            const report = (await storage.readCheckReports(projectRow.id, null, null, 20))[0];
            const foreign = await storage.readCheckReports("other-project", null, null, 20);
            const unowned = await storage.removeCheckReport(projectRow.id, "unauthorized", JSON.parse(report).Id);
            await storage.deleteProject(projectRow.id);
            const left = await storage.readCheckReports(projectRow.id, null, null, 20);
            return { foreign: foreign.length, unowned, left: left.length };
        }, project);
        assert.deepEqual(isolated, { foreign: 0, unowned: false, left: 0 });
        console.log("PASS IndexedDB v2 upgrade, exact source/head, report persistence, paging, project isolation and deletion");
    } finally {
        await stop();
        fs.rmSync(profile, { recursive: true, force: true });
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
