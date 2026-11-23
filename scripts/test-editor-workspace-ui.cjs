// Published workspace acceptance. Run this in the pinned browser image with
// PUBLISHED_WWWROOT aimed at the clean static publication.
const assert = require("node:assert/strict");
const { spawn } = require("node:child_process");
const { once } = require("node:events");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { chromium } = require("playwright");
const H = require("./review-ui-helpers.cjs");
const { $ } = H;

const site = process.env.PUBLISHED_WWWROOT;
const output = process.env.RESULT_DIR || fs.mkdtempSync(path.join(os.tmpdir(), "editor-workspace-"));
const profile = fs.mkdtempSync(path.join(os.tmpdir(), "editor-profile-"));
const origin = "http://127.0.0.1:8096";
const name = "Editor workspace acceptance";
const normalize = text => text.replaceAll("\r\n", "\n").replaceAll("\r", "\n");
const fileBytes = text => Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), Buffer.from(text)]);
const launchOptions = { headless: true, viewport: { width: 1440, height: 900 } };

async function compile(page, outcome) {
    const details = $(page, "compile-details");
    const before = await details.textContent();
    await $(page, "compile").click({ timeout: 150000 });
    await page.waitForFunction(({ prior, outcome }) => {
        const detail = document.querySelector("[data-testid=compile-details]")?.textContent;
        return detail && detail !== prior && detail.includes(`Outcome: ${outcome};`) &&
            document.querySelector("[data-testid=compile-work-status]")?.textContent.trim() === "Idle";
    }, { prior: before, outcome }, { timeout: 90000 });
    assert.match(await details.textContent(), new RegExp("Outcome: " + outcome + ";"));
}

async function rendered(page, phrase) {
    await page.waitForFunction(expected =>
        document.querySelector("[data-testid=canvas-status]")?.textContent.includes("Drawn") &&
        document.querySelector("[data-testid=page-text]")?.textContent.includes(expected),
    phrase, { timeout: 45000 });
    const hash = await $(page, "pdf-pane").getAttribute("data-document");
    assert.match(hash, /^[0-9a-f]{64}$/);
    return hash;
}

async function writingPosition(page) {
    return $(page, "editor").evaluate(input => ({
        value: input.value,
        start: input.selectionStart,
        end: input.selectionEnd,
        direction: input.selectionDirection,
        top: input.scrollTop,
        left: input.scrollLeft,
        fileId: input.dataset.fileId,
    }));
}

async function withoutOverflow(page) {
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth), page.viewportSize().width);
}

async function save(page) {
    if (await $(page, "save").isEnabled())
        await $(page, "save").click({ timeout: 1000 }).catch(error => { if (error.name !== "TimeoutError") throw error; });
    await page.waitForFunction(() => document.querySelector(".workspace-header .status")?.textContent === "Saved.");
}

async function highlight(page) {
    const editor = $(page, "editor");
    await page.waitForFunction(() => {
        const source = document.querySelector("#editor");
        const layer = document.querySelector("[data-testid=source-highlight-layer]");
        return layer?.dataset.ready === "true" && layer.dataset.fileId === source.dataset.fileId &&
            layer.dataset.revision === source.dataset.highlightRevision;
    });
    assert.equal(await $(page, "source-highlight-text").textContent(), await editor.inputValue());
    return editor.getAttribute("data-file-id");
}

(async () => {
    assert.ok(site, "set PUBLISHED_WWWROOT");
    fs.mkdirSync(output, { recursive: true });
    const server = spawn(process.execPath, [path.join(__dirname, "static-server.cjs"), site, "8096"], { stdio: ["ignore", "pipe", "inherit"] });
    const errors = [];
    let context, page;
    function observe(newPage) {
        newPage.setDefaultTimeout(15000);
        newPage.on("pageerror", error => errors.push(error.message));
    }

    try {
        await once(server.stdout, "data");
        context = await chromium.launchPersistentContext(profile, launchOptions);
        page = await context.newPage();
        observe(page);
        await page.goto(origin);
        await H.ready(page);
        await H.reveal(page, page.locator("#new-project"));
        await page.locator("#new-project").fill(name);
        await page.getByRole("button", { name: "Create project", exact: true }).click();
        await page.getByText("Import a source file…", { exact: true }).waitFor();
        await $(page, "starter-article").click();
        const editor = $(page, "editor");
        await page.waitForFunction(() => document.activeElement?.id === "editor");
        const original = (await editor.inputValue()).replace("Write your document here.", "Hello from the built-in compiler.");
        await editor.fill(original);
        assert.ok(await $(page, "compile").isEnabled(), "a known root can queue compilation while bundled tools prepare");
        await save(page);
        await H.assertBytes(page, name, Buffer.from(original));
        await compile(page, "Succeeded");
        const firstPdf = await rendered(page, "Hello from the built-in compiler.");
        assert.match(await $(page, "compiler-log").textContent(), /pdfTeX/);
        await withoutOverflow(page);
        assert.equal(await page.locator('.workspace-tools, [data-testid="source-history-disclosure"]').count(), 0);
        assert.equal(await $(page, "checks-page").isVisible(), false);
        assert.ok(await page.locator('.pdf-search-option').evaluate(panel => !panel.open));
        const fit = await page.locator('#workspace-output .pdf-scroll').evaluate(scroll => ({
            pane: scroll.clientWidth,
            page: scroll.querySelector('canvas').getBoundingClientRect().width,
            raster: scroll.querySelector('canvas').width,
        }));
        assert.ok(fit.pane - fit.page <= 26 && fit.raster >= fit.page, 'the initial PDF fills its pane at readable resolution');
        await page.screenshot({ path: path.join(output, "editor-1440.png") });

        const prefix = "% authoring workspace diagnostic\n".repeat(110);
        const invalidLine = "\\undefinedOvercloneCommand % jump to this line";
        const broken = prefix + original.replace("\\end{document}", invalidLine + "\n\\end{document}");
        await editor.fill(broken);
        await compile(page, "CompilerFailed");
        assert.equal(await $(page, "pdf-pane").getAttribute("data-document"), firstPdf);
        assert.match(await $(page, "compiled-pdf-status").innerText(), /out of date/);
        await page.locator('[data-testid=compile-details] > summary').click();
        const failureLog = await $(page, "compiler-log").innerText();
        fs.writeFileSync(path.join(output, "compiler-failure.log"), failureLog);
        assert.match(failureLog, /Undefined control sequence/);
        await page.locator('[data-testid=compile-details] > summary').click();
        await $(page, "compile-error-jump").click();
        await page.waitForFunction(() => document.activeElement?.id === "editor");
        const selected = await writingPosition(page);
        assert.equal(selected.value.slice(selected.start, selected.end), invalidLine);
        assert.ok(selected.top > 500, "diagnostic scrolled to the distant source line");
        await page.keyboard.insertText("\\par Recompiled after the fix.");
        await page.getByRole("button", { name: "Hide PDF", exact: true }).click();
        await compile(page, "Succeeded");
        await page.getByRole("button", { name: "Show PDF", exact: true }).click();
        assert.notEqual(await rendered(page, "Recompiled after the fix."), firstPdf);
        assert.equal(await $(page, "compile-error").count(), 0);
        console.log("Blank article, native compile/render, error line jump, old PDF and corrected compile passed.");

        const mixed = "\\section{Вступ🙂}\r\n% retained CRLF\r% retained CR\n" +
            ("% long source " + "x".repeat(200) + "\r\n").repeat(100);
        const updated = mixed.replace("Вступ🙂", "Україна🙂");
        for (const filename of ["notes.tex", "identical.tex"]) {
            await $(page, "import-input").setInputFiles({ name: filename, mimeType: "text/plain", buffer: fileBytes(mixed) });
            await page.locator(".source-tab", { hasText: filename }).waitFor();
            await save(page);
        }
        const identicalId = await highlight(page);
        await $(page, "select-file").filter({ has: page.getByText("notes.tex", { exact: true }) }).click();
        assert.notEqual(await highlight(page), identicalId);
        await H.assertBytes(page, name, fileBytes(mixed), "notes.tex");
        await editor.focus();
        await editor.evaluate(input => input.setSelectionRange(9, 16));
        assert.equal((await writingPosition(page)).value.slice(9, 16), "Вступ🙂");
        await page.keyboard.insertText("Україна🙂");
        await highlight(page);
        assert.equal(await page.locator(".workspace-header .status").textContent(), "Unsaved changes.");

        for (const width of [1440, 768, 390]) {
            await page.setViewportSize({ width, height: 900 });
            await editor.evaluate(input => {
                const start = input.value.indexOf("% long source", 700);
                input.setSelectionRange(start, start + 12, "backward");
                input.scrollTop = 150;
                input.scrollLeft = 64;
            });
            const before = await writingPosition(page);
            assert.ok(before.top > 0 && before.left > 0 && before.start !== before.end);
            await H.reveal(page, $(page, "ai-panel"));
            assert.ok(await editor.isVisible());
            assert.ok(await $(page, "ai-panel").isVisible());
            await page.screenshot({ path: path.join(output, "checks-ai-" + width + ".png") });
            await withoutOverflow(page);
            await H.reveal(page, $(page, "analyze-now"));
            await page.screenshot({ path: path.join(output, "checks-source-" + width + ".png") });
            await withoutOverflow(page);
            await $(page, "open-editor").click();
            assert.deepEqual(await writingPosition(page), before);
            await page.screenshot({ path: path.join(output, "source-" + width + ".png") });
            if (width > 800) {
                await page.getByRole("button", { name: "Hide PDF", exact: true }).click();
                assert.ok(await $(page, "compile").isVisible());
                await page.getByRole("button", { name: "Show PDF", exact: true }).click();
            } else {
                await page.getByRole("button", { name: "PDF", exact: true }).click();
                assert.equal(await editor.isVisible(), false);
                assert.equal(await $(page, "source-highlight-text").isVisible(), false,
                    "the inactive syntax layer must not bleed through the PDF view");
                assert.equal(await page.locator("#workspace-source").evaluate(pane => getComputedStyle(pane).opacity), "0",
                    "highlight overlays must not paint over the PDF");
                assert.ok((await $(page, "pdf-pane").boundingBox()).width >= width - 2);
                await withoutOverflow(page);
                await page.screenshot({ path: path.join(output, "pdf-" + width + ".png") });
                await page.getByRole("button", { name: "Source", exact: true }).click();
                assert.ok((await editor.boundingBox()).width >= width - 48);
            }
            assert.deepEqual(await writingPosition(page), before);
            await withoutOverflow(page);
        }
        await save(page);
        await H.assertBytes(page, name, fileBytes(normalize(updated)), "notes.tex");
        await $(page, "source-undo").click();
        assert.equal(await editor.inputValue(), normalize(mixed));
        await save(page);
        await H.assertBytes(page, name, fileBytes(normalize(mixed)), "notes.tex");
        await $(page, "source-redo").click();
        assert.equal(await editor.inputValue(), normalize(updated));
        await save(page);
        await H.assertBytes(page, name, fileBytes(normalize(updated)), "notes.tex");
        console.log("Responsive panes preserved cursor, selection and scroll. Exact import bytes and edited LF+BOM bytes through undo/redo passed.");

        await page.waitForFunction(() => navigator.serviceWorker.controller?.state === "activated" &&
            window.__oopOfflineReady === true, null, { timeout: 150000 });
        await context.close();
        context = null;
        const stopped = once(server, "exit");
        server.kill();
        await stopped;
        await assert.rejects(fetch(origin));
        context = await chromium.launchPersistentContext(profile, launchOptions);
        await context.setOffline(true);
        page = await context.newPage();
        observe(page);
        await page.goto(origin);
        await H.ready(page);
        await H.open(page, name);
        const notes = $(page, "select-file").filter({ has: page.getByText("notes.tex", { exact: true }) });
        await H.reveal(page, notes);
        await notes.click();
        await highlight(page);
        await H.assertBytes(page, name, fileBytes(normalize(updated)), "notes.tex");
        await compile(page, "Succeeded");
        await rendered(page, "Recompiled after the fix.");
        console.log("Closed Chromium and stopped origin, reopened offline with exact bytes, highlighted source and real compile/render.");

        await page.getByRole("button", { name: "Close", exact: true }).click();
        await page.setViewportSize({ width: 390, height: 900 });
        await H.reveal(page, page.locator("#new-project"));
        await page.locator("#new-project").fill("Ambiguous root selection");
        await page.getByRole("button", { name: "Create project", exact: true }).click();
        await $(page, "starter-article").waitFor();
        await page.waitForFunction(() => !document.querySelector(".workspace-navigation.sidebar-expanded"));
        const chooser = page.waitForEvent("filechooser");
        await page.getByText("Import a source file…", { exact: true }).click();
        await (await chooser).setFiles({
            name: "chapter.tex", mimeType: "text/plain", buffer: Buffer.from(original),
        });
        await page.locator(".source-tab", { hasText: "chapter.tex" }).waitFor();
        await $(page, "import-input").setInputFiles({
            name: "paper.tex", mimeType: "text/plain",
            buffer: Buffer.from(original.replace("Hello from the built-in compiler.", "The explicit root was compiled.")),
        });
        await page.locator(".source-tab", { hasText: "paper.tex" }).waitFor();
        assert.ok(await $(page, "compile").isDisabled());
        await page.getByText("Choose main file", { exact: true }).click();
        const root = page.getByRole("combobox", { name: "Compile main file", exact: true });
        const engine = page.getByRole("combobox", { name: "Compile engine", exact: true });
        await root.selectOption({ label: "paper.tex" });
        await engine.selectOption("xelatex");
        await page.getByText("Compiler settings", { exact: true }).click();
        await compile(page, "Succeeded");
        await page.getByRole("button", { name: "PDF", exact: true }).click();
        await rendered(page, "The explicit root was compiled.");
        assert.match(await $(page, "compiler-log").textContent(), /XeTeX/);
        await withoutOverflow(page);
        console.log("Phone source import and ambiguous root selection honored the explicit root and real XeLaTeX engine.");

        const offlineEditor = $(page, "editor");
        const missingFont = "NoSuchFont";
        const useNamedFont = `\\setmainfont{${missingFont}}`;
        const fontSource = String.raw`\documentclass[14pt,a4paper]{extarticle}

\usepackage{fontspec}

${useNamedFont}
\begin{document}
Termes regular.\par
\textbf{Termes bold.}\par
\textit{Termes italic.}\par
\textbf{\textit{Termes bold italic.}}
\end{document}
`;
        await page.getByRole("button", { name: "Source", exact: true }).click();
        await offlineEditor.fill(fontSource);
        await save(page);
        await page.getByText("Compiler settings", { exact: true }).click();
        await engine.selectOption("");
        await page.getByText("Compiler settings", { exact: true }).click();
        await compile(page, "CompilerFailed");
        assert.match(await $(page, "compile-error").innerText(), /fontspec requires XeLaTeX or LuaLaTeX/);
        assert.equal(await $(page, "compile-error-jump").count(), 0);
        console.log("pdfLaTeX fontspec failure offered an explicit engine switch with no jump to the internal package line.");
        await page.getByRole("button", { name: "Use XeLaTeX", exact: true }).click();
        await page.waitForFunction(() => document.querySelector('select[aria-label="Compile engine"]')?.value === "xelatex");
        await compile(page, "CompilerFailed");
        assert.match(await $(page, "compile-error").innerText(), /could not find a requested font/);
        const log = await $(page, "compiler-log").textContent();
        assert.ok(log.includes(`The font "${missingFont}"`), log);
        assert.match(log, /\/work\/paper\.tex:6: Package fontspec Error/);
        assert.equal(await $(page, "compile-error-jump").innerText(), "Go to paper.tex:6");
        await $(page, "compile-error-jump").click();
        await page.waitForFunction(expected => {
            const source = document.querySelector("#editor");
            return source && document.activeElement === source &&
                source.value.slice(source.selectionStart, source.selectionEnd) === expected;
        }, String.raw`\begin{document}`);
        console.log("XeLaTeX missing-font diagnostic selected the exact compiler-reported line.");

        const fixedSource = fontSource.replace(useNamedFont, String.raw`\setmainfont{TeX Gyre Termes}
\usepackage{geometry,setspace,indentfirst,booktabs,array,tabularx,hyperref}
\usepackage{xstring}
\usepackage{fancyhdr}
\pagestyle{fancy}`)
            .replace("Termes regular.", String.raw`\IfStrEq{Overclone}{Overclone}{Xstring evaluated.}{Xstring failed.}\par
Termes regular.`);
        await offlineEditor.fill(fixedSource);
        await compile(page, "Succeeded");
        await save(page);
        const saved = await H.assertBytes(page, "Ambiguous root selection", Buffer.from(fixedSource), "paper.tex");
        assert.deepEqual(saved.files.map(file => file.path).sort(), ["chapter.tex", "paper.tex"]);
        await page.getByRole("button", { name: "PDF", exact: true }).click();
        for (const phrase of ["Xstring evaluated.", "Termes regular.", "Termes bold.", "Termes italic.", "Termes bold italic."])
            await rendered(page, phrase);
        const successfulLog = await $(page, "compiler-log").textContent();
        for (const packageFile of ["xstring.sty", "xstring.tex", "fancyhdr.sty"])
            assert.ok(successfulLog.includes("/tex/" + packageFile), `${packageFile} must load from the runtime`);
        assert.doesNotMatch(successfulLog, /Font shape .* undefined|defaults substituted/);
        const [download] = await Promise.all([page.waitForEvent("download"), $(page, "download-pdf").click()]);
        await download.saveAs(path.join(output, "termes-offline.pdf"));
        await withoutOverflow(page);
        assert.deepEqual(errors, []);
        console.log("Offline XeLaTeX used named Termes and evaluated xstring with the reported preamble packages and no project assets.");

        await page.getByRole("button", { name: "Source", exact: true }).click();
        const warnings = Array.from({ length: 150 }, (_, i) => String.raw`\typeout{LaTeX Warning: Fixture warning ${i}.}`).join("\n");
        const include = String.raw`\input conclusion`;
        const missingSource = fixedSource.replace(String.raw`\end{document}`, `${warnings}\n${include}\n\\end{document}`);
        await offlineEditor.fill(missingSource);
        await compile(page, "CompilerFailed");
        assert.match(await $(page, "compile-error").innerText(), /missing input file/);
        const missingLog = await $(page, "compiler-log").textContent();
        assert.match(missingLog, /I can't find file `conclusion'/);
        assert.ok(missingLog.match(/LaTeX Warning: Fixture warning/g).length >= 150);
        const diagnostics = await $(page, "compiler-diagnostics").textContent();
        assert.doesNotMatch(diagnostics, /unrecognized compiler-log record/);
        assert.match(diagnostics, /occurrences/);
        await $(page, "compile-error-jump").click();
        await page.waitForFunction(expected => {
            const source = document.querySelector("#editor");
            return source && document.activeElement === source &&
                source.value.slice(source.selectionStart, source.selectionEnd) === expected;
        }, include);

        const conclusion = Buffer.from(String.raw`\par Restored conclusion.\par` + "\n");
        await $(page, "import-input").setInputFiles({
            name: "conclusion.tex", mimeType: "text/plain", buffer: conclusion,
        });
        await page.locator(".source-tab", { hasText: "conclusion.tex" }).waitFor();
        await compile(page, "Succeeded");
        assert.match(await $(page, "compiler-log").textContent(), /\((?:\/work\/)?conclusion\.tex\b/);
        await H.assertBytes(page, "Ambiguous root selection", Buffer.from(missingSource), "paper.tex");
        await H.assertBytes(page, "Ambiguous root selection", conclusion, "conclusion.tex");
        await page.getByRole("button", { name: "PDF", exact: true }).click();
        await rendered(page, "Restored");
        assert.equal(await $(page, "compile-error").count(), 0);
        console.log("A missing include after 150 warnings jumped to the source line. Importing the file compiled and rendered without changing the root.");
    } catch (error) {
        if (page && !page.isClosed()) {
            fs.writeFileSync(path.join(output, "failure-compiler-log.txt"), await $(page, "compiler-log").textContent().catch(() => ""));
            fs.writeFileSync(path.join(output, "failure-diagnostics.txt"), await $(page, "compiler-diagnostics").textContent().catch(() => ""));
            const state = await page.evaluate(() => Object.fromEntries(
                ["compile-status", "compile-error", "compile-details", "preview-status", "canvas-status", "page-text"].map(id =>
                    [id, document.querySelector('[data-testid="' + id + '"]')?.textContent?.slice(0, 3000)]),
            )).catch(() => ({}));
            console.error(state);
            await page.screenshot({ path: path.join(output, "failure.png") }).catch(() => {});
        }
        throw error;
    } finally {
        if (context) await context.close();
        server.kill();
        fs.rmSync(profile, { recursive: true, force: true });
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
