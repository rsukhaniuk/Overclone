// Published Chromium workflow for the compiled PDF reader. Requires the
// bundled compiler and renderer, and sends requests only to the test origin.
const assert = require("node:assert/strict");
const { spawn } = require("node:child_process");
const { once } = require("node:events");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { chromium } = require("playwright");
const H = require("./review-ui-helpers.cjs");
const { $ } = H;

const root = process.env.PUBLISHED_WWWROOT;
const origin = "http://127.0.0.1:8097";
const profile = fs.mkdtempSync(path.join(os.tmpdir(), "overclone-pdf-reader-"));
const project = "PDF reader interactions";
const source = String.raw`\documentclass{article}
\usepackage{hyperref}
\begin{document}
Selectable sentence here.

\begin{tabular}{|l|l|l|l|}\hline
Characteristic & Store-and-forward & Cut-through & L1 Crosspoint\\\hline
Primary use case & Enterprise & Low-latency DC & HFT data fan-out\\\hline
\end{tabular}

\hyperlink{target-page}{Jump to the third page}

\href{http://127.0.0.1:8097/manifest.json}{Open web link}

\href{file:///unsupported.pdf}{Local file action}
\newpage
Second page text.
\newpage
\hypertarget{target-page}{Third page target.}
\newpage
Page 4 text.
\newpage
Page 5 text.
\newpage
Page 6 text.
\newpage
Page 7 text.
\newpage
Page 8 text.
\newpage
Page 9 text.
\newpage
Page 10 text.
\newpage
Page 11 text.
\newpage
Page 12 text.
\end{document}
`;

(async () => {
    assert.ok(root, "set PUBLISHED_WWWROOT");
    const server = spawn(process.execPath, [path.join(__dirname, "static-server.cjs"), root, "8097"], { stdio: ["ignore", "pipe", "inherit"] });
    let browser;
    try {
        await once(server.stdout, "data");
        browser = await chromium.launchPersistentContext(profile, { headless: true, viewport: { width: 2040, height: 1090 }, deviceScaleFactor: 1.5 });
        await browser.grantPermissions(["clipboard-read", "clipboard-write"], { origin });
        let page = await browser.newPage();
        const errors = [];
        page.on("pageerror", error => errors.push(error.message));
        let viewport = page.locator("#workspace-output .pdf-reader");
        const sheet = n => viewport.locator('[data-page-number="' + n + '"]');
        const zoom = page.getByRole("combobox", { name: "Zoom", exact: true });
        const scroll = () => viewport.evaluate(element => element.scrollTop);

        async function rendered(number, phrase) {
            await page.waitForFunction(({ number, phrase }) =>
                document.querySelector('[data-testid="pdf-canvas"]')?.dataset.pdfPage === String(number - 1) &&
                document.querySelector('[data-testid="canvas-status"]')?.textContent.includes("Drawn") &&
                document.querySelector('[data-testid="page-text"]')?.textContent.includes(phrase),
            { number, phrase }, { timeout: 90000 });
        }

        await page.goto(origin);
        await H.ready(page);
        await H.reveal(page, page.locator("#new-project"));
        await page.locator("#new-project").fill(project);
        await page.getByRole("button", { name: "Create project", exact: true }).click();
        await $(page, "starter-article").click();
        const editor = $(page, "editor");
        await editor.fill(source);
        const firstCompile = Date.now();
        await $(page, "compile").click();
        await rendered(1, "Selectable sentence here.");
        await page.waitForFunction(() => {
            const canvas = document.querySelector('[data-testid="pdf-canvas"]');
            return canvas?.width >= canvas.clientWidth * devicePixelRatio;
        });
        console.log("First compile to rendered text (ms):", Date.now() - firstCompile);
        assert.equal(await sheet(3).count(), 1);

        const neighborsStarted = Date.now();
        await sheet(2).locator("canvas").waitFor({state:"attached",timeout:15000});
        await sheet(3).locator("canvas").waitFor({state:"attached",timeout:15000});
        console.log("Neighbor pages ready before scrolling (ms):", Date.now() - neighborsStarted);
        const cell = await sheet(1).locator(".pdf-text-line").filter({hasText:/^Primary use case/}).evaluate(row => {
            let length = 0, first, last;
            for (const child of row.childNodes) {
                if (length >= "Primary use case".length) break;
                length += child.textContent.length;
                if (child.nodeType === Node.ELEMENT_NODE) { first ??= child.getBoundingClientRect(); last = child.getBoundingClientRect(); }
            }
            return {x:first.left-0.5, end:last.right+0.5, y:first.top+first.height/2};
        });
        await page.mouse.move(cell.x,cell.y); await page.mouse.down();
        await page.mouse.move(cell.end,cell.y,{steps:15}); await page.mouse.up();
        await page.keyboard.press("Control+c");
        assert.equal(await page.evaluate(() => navigator.clipboard.readText()), "Primary use case");
        await page.mouse.dblclick(cell.x+10,cell.y);
        assert.equal(await page.evaluate(() => getSelection().toString()), "Primary");
        // A neighboring page must already have pixels while wheel events continue.
        await viewport.evaluate(el => {
            window.readerScrollStart = performance.now();
            for(let i=0;i<20;i++) setTimeout(() => {el.scrollTop = el.querySelector('[data-page-number="2"]').offsetTop + i%2*2;},i*25);
        });
        await page.waitForTimeout(250);
        assert.equal(await sheet(2).locator("canvas").count(), 1);
        await page.waitForTimeout(300);
        await rendered(2, "Second page text.");
        await page.getByRole("button", {name:"Previous page",exact:true}).click();
        await rendered(1,"Selectable sentence here.");

        const flingStarted = Date.now();
        await viewport.evaluate(el => {
            for(let i=0;i<20;i++) setTimeout(() => {el.scrollTop = el.querySelector('[data-page-number="12"]').offsetTop + i%2*2;},i*25);
        });
        await page.waitForTimeout(250);
        assert.equal(await page.getByRole("spinbutton", {name:"Go to page",exact:true}).inputValue(), "12");
        await rendered(12,"Page 12 text.");
        console.log("Uncached last page during continuous scroll (ms):",Date.now()-flingStarted);
        await page.waitForTimeout(300);
        await viewport.evaluate(el => {el.scrollTop=0;});
        await rendered(1,"Selectable sentence here.");

        const line = await sheet(1).locator(".pdf-text-layer span", { hasText: "Selectable sentence here." }).boundingBox();
        await page.mouse.move(line.x - 45, line.y + line.height / 2);
        await page.mouse.down();
        await page.mouse.move(line.x + line.width + 42, line.y + line.height / 2, { steps: 15 });
        await page.mouse.up();
        assert.match(await page.evaluate(() => getSelection().toString().replace(/\s+/g, " ")), /Selectable sentence here/);
        await page.keyboard.press("Control+c");
        assert.match(await page.evaluate(async () => (await navigator.clipboard.readText()).replace(/\s+/g, " ")), /Selectable sentence here/);
        await viewport.focus();
        await page.keyboard.press("Control+a");
        assert.match(await page.evaluate(() => getSelection().toString()), /Jump to the third page/);
        assert.doesNotMatch(await page.evaluate(() => getSelection().toString()), /Overclone/);
        const selectPageText = page.getByRole("button", { name: "Select page text", exact: true });
        await page.locator(".pdf-more-option > summary").click();
        await selectPageText.click();
        assert.match(await page.evaluate(() => getSelection().toString()), /Selectable sentence here/);
        assert.equal(await selectPageText.isVisible(), false);

        const caret = source.indexOf("Selectable sentence");
        await editor.evaluate((input, at) => { input.focus(); input.setSelectionRange(at, at); }, caret);
        await $(page, "source-go-to-pdf").click();
        await $(page, "source-pdf-highlight").waitFor({ timeout: 15000 }).catch(async error => {
            console.error("Source navigation status:", await $(page, "source-map-status").textContent());
            throw error;
        });
        await page.mouse.click(line.x + line.width / 2, line.y + line.height / 2);
        await page.getByRole("button", { name: "Go to source", exact: true }).click();
        await page.waitForFunction(() => document.querySelector('[data-testid="source-map-status"]')?.textContent.startsWith("Selected main.tex:"));

        const raster = await $(page, "pdf-canvas").evaluate(canvas => ({ width: canvas.width, css: canvas.clientWidth }));
        const textLayer = await sheet(1).locator(".pdf-text-layer").elementHandle();
        await zoom.selectOption("300");
        await page.waitForFunction(width => document.querySelector('[data-testid="pdf-canvas"]').clientWidth > width * 2.9, raster.css);
        assert.equal(await $(page, "preview-status").isVisible(), false);
        await page.waitForFunction(width => document.querySelector('[data-testid="pdf-canvas"]').width > width, raster.width);
        assert.ok(await textLayer.evaluate(layer => layer.isConnected), "a sharper bitmap keeps the selectable text layer");
        assert.ok(await $(page, "pdf-canvas").evaluate(canvas => canvas.width * canvas.height <= 4_000_000));
        await zoom.selectOption("100");

        const link = sheet(1).getByRole("link", { name: "Go to PDF page 3", exact: true });
        await link.click();
        await rendered(3, "Third page target.");
        assert.ok(await scroll() > 500);
        await page.getByRole("button", { name: "Previous page", exact: true }).click();
        await rendered(2, "Second page text.");
        await page.getByRole("button", { name: "Previous page", exact: true }).click();
        await rendered(1, "Selectable sentence here.");

        const [web] = await Promise.all([
            page.waitForEvent("popup"),
            sheet(1).getByRole("link", { name: /Open http:\/\/127\.0\.0\.1:8097\/manifest\.json/ }).click(),
        ]);
        await web.waitForURL(origin + "/manifest.json");
        assert.equal(await web.evaluate(() => window.opener), null);
        await web.close();
        assert.equal(await sheet(1).locator('a[href^="file:"]').count(), 0);

        await viewport.hover({ position: { x: 40, y: 40 } });
        await page.mouse.wheel(0, await sheet(1).evaluate(el => el.offsetHeight + 16));
        await rendered(2, "Second page text.");
        const position = await scroll();
        const previousHash = await $(page, "pdf-pane").getAttribute("data-document");
        const changed = source.replace("Second page text.", "Revised second page text.");
        await editor.fill(changed);
        await page.waitForFunction(() => document.querySelector(".workspace-header .status")?.textContent === "Saved.");
        assert.equal(await scroll(), position);
        assert.equal(await $(page, "pdf-pane").getAttribute("data-document"), previousHash);
        await H.assertBytes(page, project, Buffer.from(changed));
        const recompile = Date.now();
        await $(page, "compile").click();
        await rendered(2, "Revised second page text.");
        console.log("Recompile to revised text (ms):", Date.now() - recompile);
        assert.notEqual(await $(page, "pdf-pane").getAttribute("data-document"), previousHash);
        assert.ok(Math.abs(await scroll() - position) <= 5);

        await page.setViewportSize({ width: 390, height: 840 });
        const files = page.locator(".files-switch");
        if (await files.getAttribute("open") === null) await files.locator("summary").click();
        await page.getByRole("button", { name: "PDF", exact: true }).click();
        await page.waitForFunction(() => !document.querySelector(".sidebar-switch[open]"));
        await rendered(2, "Revised second page text.");
        await page.getByRole("button", { name: "Next page", exact: true }).click();
        await rendered(3, "Third page target.");
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth), 390);
        assert.deepEqual(errors, []);
        console.log("PASS real compile, table and word selection, continuous scrolling, 300% zoom, links, revised PDF position, phone navigation");
        await browser.close();
        const originStopped = once(server,"exit"); server.kill(); await originStopped;
        browser = await chromium.launchPersistentContext(profile, {headless:true,viewport:{width:2040,height:1090},deviceScaleFactor:1.5});
        await browser.setOffline(true);
        page = await browser.newPage();
        page.on("pageerror",error => errors.push(error.message));
        viewport = page.locator("#workspace-output .pdf-reader");
        await page.goto(origin);
        await H.ready(page); await H.open(page,project);
        await rendered(1,"Selectable sentence here.");
        await sheet(2).locator("canvas").waitFor({state:"attached",timeout:15000});
        await page.getByRole("button",{name:"Next page",exact:true}).click();
        await rendered(2,"Revised second page text.");
        const offlineSource = changed.replace("Revised second page text.","Offline revised second page text.");
        await $(page,"editor").fill(offlineSource);
        await page.waitForFunction(() => document.querySelector(".workspace-header .status")?.textContent === "Saved.");
        await $(page,"compile").click();
        await rendered(2,"Offline revised second page text.");
        await H.assertBytes(page,project,Buffer.from(offlineSource));
        assert.deepEqual(errors,[]);
        console.log("PASS whole-browser cold offline reopen, cached 12-page PDF, neighbor rendering and fresh offline compile");
    } finally {
        if (browser) await browser.close();
        server.kill();
        fs.rmSync(profile, { recursive: true, force: true });
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
