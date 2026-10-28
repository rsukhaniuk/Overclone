// Focused ACTUAL published Blazor UI acceptance; no managed-call mocks or
// JavaScript tokenizer. Run by lead in the pinned browser container.
// APP_URL is the existing published static origin. This proxy owns an ephemeral
// localhost origin; profile/screenshots contain synthetic projects only.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const http = require("node:http");
const { chromium } = require("playwright");
const H = require("./review-ui-helpers.cjs");
const { $ } = H;
const upstream = process.env.APP_URL || "http://static-origin:8080";
const output = process.env.RESULT_DIR || fs.mkdtempSync(path.join(os.tmpdir(), "highlight-results-"));
fs.mkdirSync(output, { recursive: true });
const profile = fs.mkdtempSync(path.join(os.tmpdir(), "highlight-profile-"));
const launch = { headless: true, args: ["--no-sandbox"] };
const bom = Buffer.from([239, 187, 191]);
const bytes = text => Buffer.concat([bom, Buffer.from(text)]);
const normalize = text => text.replaceAll("\r\n", "\n").replaceAll("\r", "\n");
const attack = '<img src=x onerror="window.__highlightAttack=true"><script>window.__highlightAttack=true</script>';
const raw = "\\section{Вступ🙂}\r\n% " + attack + "\r\n" +
  "\\verb|% $ { } \\fake|\r\n$$E=mc^2$$ \\(x\\) \\[y\\]\r\n\t" + "long-line ".repeat(300) + "\r\n" +
  Array.from({ length: 120 }, (_, i) => `line ${i}\tУкраїна🙂`).join("\r\n") + "\r\n";
const proxy = http.createServer((request, response) => {
  const url = new URL(request.url, upstream);
  const remote = http.request(url, { method: request.method, headers: { ...request.headers, host: url.host } }, reply => {
    response.writeHead(reply.statusCode, reply.headers); reply.pipe(response);
  });
  remote.on("error", () => response.destroy()); request.pipe(remote);
});
let base, context;
const errors = [], failures = [], external = [];
function observe(page) {
  page.on("pageerror", error => { if (errors.length < 32) errors.push(String(error).slice(0, 1500)); });
  page.on("requestfailed", request => { if (failures.length < 32) failures.push(request.url().slice(0, 1000)); });
  page.on("request", request => {
    if (!request.url().startsWith(base) && !/^(data:|blob:)/.test(request.url()) && external.length < 32)
      external.push(request.url().slice(0, 1000));
  });
}
async function layerReady(page) {
  await page.waitForFunction(() => {
    const layer = document.querySelector('[data-testid="source-highlight-layer"]'), editor = document.querySelector("#editor");
    return layer?.dataset.ready === "true" && layer.dataset.fileId === editor?.dataset.fileId &&
      layer.dataset.revision === editor?.dataset.highlightRevision &&
      layer.querySelector(".source-highlight-text").textContent === editor.value;
  }, null, { timeout: 30000 });
}
async function selection(page, start, end = start) {
  await $(page, "editor").evaluate((editor, range) => editor.setSelectionRange(range.start, range.end), { start, end });
}
async function selected(page) { return $(page, "editor").evaluate(editor => [editor.selectionStart, editor.selectionEnd]); }
async function save(page) {
  if (!(await $(page, "save").isDisabled())) await $(page, "save").click();
  await page.waitForFunction(() => document.querySelector(".workspace-header .status")?.textContent === "Saved.");
}
async function sourceTool(page, name) {
  await H.reveal(page, $(page, `source-${name}`));
  await $(page, `source-${name}`).click();
  await page.waitForFunction(() => document.querySelector('[data-testid="source-tools"]')?.getAttribute("aria-busy") === "false");
}
async function screenshot(page, name) {
  await $(page, "source-editor-shell").scrollIntoViewIfNeeded();
  const image = await page.screenshot({ fullPage: false, timeout: 10000 });
  assert.ok(image.length <= 4 * 1024 * 1024, "bounded screenshot artifact");
  fs.writeFileSync(path.join(output, name), image);
}
async function scrollProof(page) {
  const geometry = await $(page, "editor").evaluate(editor => {
    editor.scrollTop = 73; editor.scrollLeft = 180;
    editor.dispatchEvent(new Event("scroll"));
    const root = editor.parentElement.querySelector(".source-highlighting");
    const text = root.querySelector(".source-highlight-text"), numbers = root.querySelector(".source-highlight-numbers");
    const t = new DOMMatrix(getComputedStyle(text).transform), g = new DOMMatrix(getComputedStyle(numbers).transform);
    return { top: editor.scrollTop, left: editor.scrollLeft, ty: t.m42, tx: t.m41, gy: g.m42,
      lineHeight: getComputedStyle(editor).lineHeight, gutterLineHeight: getComputedStyle(numbers).lineHeight,
      textY: text.getBoundingClientRect().top, gutterY: numbers.getBoundingClientRect().top,
      overflow: editor.scrollWidth > editor.clientWidth, wrap: editor.wrap };
  });
  assert.ok(geometry.top > 0 && geometry.left > 0 && geometry.overflow, "actual two-axis native scrolling");
  assert.equal(geometry.wrap, "off");
  assert.ok(Math.abs(geometry.tx + geometry.left) < 0.5);
  assert.ok(Math.abs(geometry.ty + geometry.top) < 0.5);
  assert.ok(Math.abs(geometry.gy + geometry.top) < 0.5);
  assert.ok(Math.abs(geometry.textY - geometry.gutterY) < 0.5, "gutter and source share the same first-line origin");
  assert.equal(geometry.lineHeight, geometry.gutterLineHeight);
  await $(page, "editor").evaluate(editor => { editor.scrollTop = 0; editor.scrollLeft = 0; editor.dispatchEvent(new Event("scroll")); });
}
async function selectFile(page, name) {
  await page.locator('[data-testid="select-file"]', { hasText: name }).click(); await layerReady(page);
}
async function coldOpen(name, expected) {
  context = await chromium.launchPersistentContext(profile, launch);
  await context.setOffline(true); // BEFORE new app navigation, not a warm reload
  const page = await context.newPage(); observe(page);
  await page.goto(base, { waitUntil: "domcontentloaded" }); await H.ready(page);
  if (await page.locator(".project-dashboard").count()) await H.open(page, name);
  await selectFile(page, "main.tex");
  assert.equal(await $(page, "editor").inputValue(), normalize(expected));
  await H.assertBytes(page, name, bytes(expected));
  return page;
}
(async () => {
  try {
    await new Promise(resolve => proxy.listen(0, "127.0.0.1", resolve));
    base = `http://127.0.0.1:${proxy.address().port}`;
    context = await chromium.launchPersistentContext(profile, launch);
    const page = await context.newPage(); observe(page);
    await page.setViewportSize({ width: 1440, height: 1000 });
    await page.goto(base); await H.ready(page);
    const name = `Highlight-${Date.now()}`;
    await H.reveal(page, page.locator("#new-project")); await page.locator("#new-project").fill(name);
    await page.getByRole("button", { name: "Create project", exact: true }).click();
    await $(page, "import-input").setInputFiles({ name: "main.tex", mimeType: "text/plain", buffer: bytes(raw) });
    await page.locator(".source-tab", { hasText: "main.tex" }).waitFor(); await layerReady(page); await save(page);
    assert.equal(await $(page, "source-highlight-text").textContent(), normalize(raw));
    assert.equal(await $(page, "source-highlight-text").locator("img,script").count(), 0);
    assert.equal(await page.evaluate(() => !!window.__highlightAttack), false);
    const colors = [];
    for (const kind of ["command", "comment", "group", "math", "verbatim"]) {
      const span = page.locator(`.source-token-${kind}`).first(); await span.waitFor();
      colors.push(await span.evaluate(element => getComputedStyle(element).color));
    }
    assert.equal(new Set(colors).size, colors.length, "real CSS token colors are distinct");
    assert.equal((await $(page, "source-line-numbers").textContent()).split("\n").length, normalize(raw).split("\n").length);
    const beforeView = await H.stamp(page), headBeforeView = await H.head(page, name);
    await selection(page, 2, 7); await scrollProof(page);
    await screenshot(page, "highlighting-source.png");
    await page.setViewportSize({ width: 390, height: 844 }); await layerReady(page); await scrollProof(page);
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    await screenshot(page, "highlighting-narrow.png");
    await page.setViewportSize({ width: 1440, height: 1000 }); await layerReady(page);
    assert.deepEqual(await H.head(page, name), headBeforeView, "viewing, selecting, scrolling and resize never save/normalize bytes");
    const afterView = await H.stamp(page);
    assert.equal(afterView.generation, beforeView.generation);
    await H.assertBytes(page, name, bytes(raw));
    // Only this ephemeral test origin gets clipboard permissions. The product
    // installs no copy/paste handler; these are the browser's normal shortcuts.
    await context.grantPermissions(["clipboard-read", "clipboard-write"], { origin: base });
    await $(page, "editor").focus();
    const copyAt = normalize(raw).indexOf("Вступ🙂");
    await selection(page, copyAt, copyAt + "Вступ🙂".length);
    await page.keyboard.press("ControlOrMeta+C");
    assert.equal(await page.evaluate(() => navigator.clipboard.readText()), "Вступ🙂");

    // Actual read-only second tab, with native source selection via existing find.
    const reader = await context.newPage(); observe(reader); await reader.goto(base); await H.ready(reader); await H.open(reader, name);
    await reader.waitForFunction(() => document.querySelector("#editor")?.disabled === true);
    await reader.waitForFunction(() => document.querySelector('[data-testid="source-highlight-layer"]')?.dataset.aligned === "true");
    assert.equal(await $(reader, "source-highlight-layer").getAttribute("data-ready"), "false");
    await H.reveal(reader, $(reader, "source-query")); await $(reader, "source-query").fill("Вступ🙂"); await sourceTool(reader, "next");
    const match = normalize(raw).indexOf("Вступ🙂"); assert.deepEqual(await selected(reader), [match, match + "Вступ🙂".length]);
    await H.assertBytes(reader, name, bytes(raw)); await reader.close();

    // Real existing Coordinator edit path; highlighting itself must not add edits.
    await H.reveal(page, $(page, "source-query")); await $(page, "source-query").fill("Вступ🙂");
    await $(page, "source-replacement").fill("Оновлено🙂"); await selection(page, 0); await sourceTool(page, "next");
    const beforeReplace = await H.stamp(page);
    await sourceTool(page, "replace"); await layerReady(page);
    const expected = raw.replace("Вступ🙂", "Оновлено🙂");
    const afterReplace = await H.stamp(page);
    assert.equal(Number(afterReplace.generation), Number(beforeReplace.generation) + 1);
    assert.equal(await $(page, "source-highlight-text").textContent(), normalize(expected));
    await save(page); await H.assertBytes(page, name, bytes(expected));

    // Identical content in a different file still needs the correct file identity.
    await $(page, "import-input").setInputFiles({ name: "notes.tex", mimeType: "text/plain", buffer: bytes(expected) });
    await page.locator(".source-tab", { hasText: "notes.tex" }).waitFor(); await layerReady(page); await save(page);
    const noteId = await $(page, "editor").getAttribute("data-file-id");
    await selectFile(page, "main.tex"); assert.notEqual(await $(page, "editor").getAttribute("data-file-id"), noteId);
    await H.reveal(page, $(page, "project-search-query")); await $(page, "project-search-query").fill("Оновлено🙂");
    await $(page, "project-search-submit").click();
    await page.locator('[data-testid="project-search-result"][data-source-path="notes.tex"]').first().click();
    await layerReady(page);
    assert.equal(await $(page, "editor").getAttribute("data-file-id"), noteId);
    const target = normalize(expected).indexOf("Оновлено🙂");
    assert.deepEqual(await selected(page), [target, target + "Оновлено🙂".length], "project-search target range preserved");

    // Imported mixed CRLF + lone CR + LF with UTF-8 BOM: the file-load path
    // yields a native DOM value normalized to LF, so the lexical projection
    // reaches ready and the durable bytes stay raw. This is real imported
    // mixed-EOL coverage, not a claim derived from the fill-matrix CRLF case.
    const mixed = "a\r\nb\rc\nУкраїна🙂\n";
    await $(page, "import-input").setInputFiles({ name: "mixed-import.tex", mimeType: "text/plain", buffer: bytes(mixed) });
    await page.locator(".source-tab", { hasText: "mixed-import.tex" }).waitFor();
    await layerReady(page);
    const mixedNorm = normalize(mixed);
    assert.equal(await $(page, "editor").inputValue(), mixedNorm, "imported mixed EOL loads as normalized native DOM value");
    assert.equal(await $(page, "source-highlight-text").textContent(), mixedNorm, "layer projection matches normalized import");
    await save(page);
    await H.assertBytes(page, name, bytes(mixed), "mixed-import.tex");
    // Merely viewing/selecting/scrolling must not bump generation or head.
    const headMixed = await H.head(page, name), stampMixed = await H.stamp(page);
    await selection(page, 2, 7);
    await $(page, "editor").evaluate(editor => { editor.scrollTop = 24; editor.dispatchEvent(new Event("scroll")); });
    assert.deepEqual(await H.head(page, name), headMixed, "viewing/selecting/scrolling mixed-import never saves/normalizes bytes");
    assert.equal((await H.stamp(page)).generation, stampMixed.generation, "view/select/scroll must not bump generation");
    await $(page, "editor").evaluate(editor => { editor.scrollTop = 0; editor.dispatchEvent(new Event("scroll")); });
    await H.assertBytes(page, name, bytes(mixed), "mixed-import.tex");
    // Restore the original matrix file so the fill matrix and clipboard/
    // rapid/IME phases keep their existing intent on notes.tex.
    await selectFile(page, "notes.tex");

    // Empty, tabs, terminal newline and no-wrap long-line presentation.
    // Pinned Chromium can dispatch multiple trusted input events for one fill
    // (observed for tab + Ukrainian/emoji + LF). Count events, not API actions.
    // A native insertText may also retain raw CR in textarea.value while the
    // C# projection holds normalized LF; that mismatch is an explicitly
    // supported legible fallback (ready=false), not a product defect.
    const inputTrace = await $(page, "editor").evaluateHandle(editor => {
      const trace = { count: 0, overflow: false };
      const listener = () => {
        if (trace.count >= 64) trace.overflow = true;
        else trace.count++;
      };
      editor.addEventListener("input", listener);
      return { trace, detach: () => editor.removeEventListener("input", listener) };
    });
    try {
      for (const text of ["", "\tУкраїна🙂\n", "a\r\nb\rc\n", "\t" + "x".repeat(5000) + "\t tail\n"]) {
        const before = await page.evaluate(handle => ({
          eventStart: handle.trace.count,
          generation: Number(document.querySelector('[data-testid="analysis-panel"]').dataset.generation),
        }), inputTrace);
        const requestedRaw = text, requestedNormalized = normalize(text);
        await $(page, "editor").fill(text);
        // Inspect actual value BEFORE any ready wait: a native insertText may
        // preserve raw CR instead of normalizing, which is a supported fallback.
        const actual = await $(page, "editor").evaluate(editor => editor.value);
        assert.ok(actual === requestedRaw || actual === requestedNormalized,
          "fill must produce exactly the requested text or its normalized form, no other transformation");
        const rawNativeCase = requestedRaw.includes("\r") && actual === requestedRaw && actual !== requestedNormalized;
        const measured = await inputTrace.evaluate(handle => handle.trace);
        assert.equal(measured.overflow, false, "native input counter overflow (64 events maximum for matrix)");
        assert.ok(measured.count > before.eventStart, "fill must dispatch at least one actual DOM input event");
        if (!rawNativeCase) {
          const expectedValue = actual;
          await layerReady(page);
          await page.waitForFunction(({ handle, before, expectedValue }) => {
            const trace = handle.trace, count = trace.count - before.eventStart;
            const editor = document.querySelector("#editor"), layer = document.querySelector(".source-highlighting");
            const generation = Number(document.querySelector('[data-testid="analysis-panel"]').dataset.generation);
            if (trace.overflow || count <= 0) throw new Error("invalid native input count");
            if (generation > before.generation + count) throw new Error("generation exceeded actual DOM input events");
            return generation === before.generation + count && layer?.dataset.ready === "true" &&
              editor.dataset.highlightRevision === String(generation) && layer.dataset.revision === String(generation) &&
              layer.dataset.fileId === editor.dataset.fileId && editor.value === expectedValue &&
              layer.querySelector(".source-highlight-text").textContent === expectedValue;
          }, { handle: inputTrace, before, expectedValue });
          // Check again without another settlement wait: deferred decoration-only
          // edits must not disappear behind an eventually-correct assertion.
          const final = await page.evaluate(async ({ handle, eventStart }) => {
            await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
            const editor = document.querySelector("#editor"), layer = document.querySelector(".source-highlighting");
            return {
              count: handle.trace.count - eventStart, overflow: handle.trace.overflow,
              generation: Number(document.querySelector('[data-testid="analysis-panel"]').dataset.generation),
              editorRevision: editor.dataset.highlightRevision, layerRevision: layer.dataset.revision,
              file: editor.dataset.fileId, layerFile: layer.dataset.fileId, ready: layer.dataset.ready,
              value: editor.value, layerText: layer.querySelector(".source-highlight-text").textContent,
              lineCount: layer.querySelector(".source-highlight-numbers").textContent.split("\n").length,
            };
          }, { handle: inputTrace, eventStart: before.eventStart });
          assert.equal(final.overflow, false, "native input counter must remain bounded");
          assert.ok(final.count > 0, "zero-event fill is not a valid oracle");
          assert.equal(final.generation, before.generation + final.count, "exactly one generation change per actual input event, none from decoration");
          assert.equal(final.editorRevision, String(final.generation));
          assert.equal(final.layerRevision, String(final.generation));
          assert.equal(final.layerFile, final.file);
          assert.equal(final.ready, "true");
          assert.equal(final.value, expectedValue);
          assert.equal(final.layerText, expectedValue);
          assert.equal(final.lineCount, expectedValue.split("\n").length);
        } else {
          // Raw CR retained in native DOM value: the exact-value guard correctly
          // refuses ready/aligned, leaving legible native text. Do NOT wait for
          // ready (it stays false by contract); settle rendered coherence first.
          await page.waitForFunction(({ handle, before, requestedNormalized }) => {
            const trace = handle.trace, count = trace.count - before.eventStart;
            const editor = document.querySelector("#editor"), layer = document.querySelector(".source-highlighting");
            const generation = Number(document.querySelector('[data-testid="analysis-panel"]').dataset.generation);
            if (trace.overflow || count <= 0) throw new Error("invalid native input count");
            if (generation > before.generation + count) throw new Error("generation exceeded actual DOM input events");
            return generation === before.generation + count &&
              editor.dataset.highlightRevision === String(generation) &&
              layer.dataset.revision === String(generation) &&
              layer.dataset.fileId === editor.dataset.fileId &&
              layer.querySelector(".source-highlight-text").textContent === requestedNormalized;
          }, { handle: inputTrace, before, requestedNormalized });
          const final = await page.evaluate(async ({ handle, eventStart }) => {
            await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
            const editor = document.querySelector("#editor"), layer = document.querySelector(".source-highlighting");
            return {
              count: handle.trace.count - eventStart, overflow: handle.trace.overflow,
              generation: Number(document.querySelector('[data-testid="analysis-panel"]').dataset.generation),
              editorRevision: editor.dataset.highlightRevision, layerRevision: layer.dataset.revision,
              file: editor.dataset.fileId, layerFile: layer.dataset.fileId,
              ready: layer.dataset.ready, aligned: layer.dataset.aligned,
              value: editor.value, layerText: layer.querySelector(".source-highlight-text").textContent,
              lineCount: layer.querySelector(".source-highlight-numbers").textContent.split("\n").length,
              foreground: getComputedStyle(editor).color,
              background: getComputedStyle(editor).backgroundColor,
              viewportVisibility: getComputedStyle(layer.querySelector(".source-highlight-viewport")).visibility,
              gutterVisibility: getComputedStyle(layer.querySelector(".source-highlight-gutter")).visibility,
            };
          }, { handle: inputTrace, eventStart: before.eventStart });
          assert.equal(final.overflow, false, "native input counter must remain bounded");
          assert.ok(final.count > 0, "zero-event fill is not a valid oracle");
          assert.equal(final.generation, before.generation + final.count, "exactly one generation change per actual input event, none from decoration");
          assert.equal(final.editorRevision, String(final.generation));
          assert.equal(final.layerRevision, String(final.generation));
          assert.equal(final.layerFile, final.file);
          assert.equal(final.ready, "false", "raw CR native fallback must not claim ready colors");
          assert.equal(final.aligned, "false", "raw CR native fallback must not claim alignment");
          assert.notEqual(final.foreground, "rgba(0, 0, 0, 0)", "native text foreground stays legible");
          assert.notEqual(final.background, "rgba(0, 0, 0, 0)", "native text background stays legible");
          assert.equal(final.viewportVisibility, "hidden", "colored viewport must hide during raw CR fallback");
          assert.equal(final.gutterVisibility, "hidden", "gutter must hide during raw CR fallback to avoid misleading numbers");
          assert.equal(final.value, requestedRaw, "native value preserves raw requested text");
          assert.equal(final.layerText, requestedNormalized, "layer projection holds normalized source");
          assert.equal(final.lineCount, requestedNormalized.split("\n").length, "line count follows normalized logical source, not raw split");
        }
      }
    } finally {
      try { await inputTrace.evaluate(handle => handle.detach()); }
      finally { await inputTrace.dispose(); }
    }
    await $(page, "editor").fill(""); await layerReady(page);
    await $(page, "editor").press("ControlOrMeta+V"); await layerReady(page);
    assert.equal(await $(page, "editor").inputValue(), "Вступ🙂", "native clipboard paste is still the normal input path");
    // During rapid input every synchronous mismatch must be plain, never ghosted.
    const finalText = "\\section{rapid🙂19}\n";
    const ghost = await $(page, "editor").evaluate(editor => {
      let ghost = false;
      for (let i = 0; i < 20; i++) {
        editor.value = `\\section{rapid🙂${i}}\n`; editor.dispatchEvent(new Event("input", { bubbles: true }));
        const layer = editor.parentElement.querySelector(".source-highlighting");
        if (layer.dataset.ready === "true" && layer.querySelector(".source-highlight-text").textContent !== editor.value) ghost = true;
      }
      return ghost;
    });
    assert.equal(ghost, false); await layerReady(page);
    assert.equal(await $(page, "editor").inputValue(), finalText);
    // Composition event gate (not a claim about every platform's physical IME).
    await $(page, "editor").evaluate(editor => editor.dispatchEvent(new CompositionEvent("compositionstart", { bubbles: true })));
    await $(page, "editor").fill("\\textbf{composition🙂}\n");
    assert.equal(await $(page, "source-highlight-layer").getAttribute("data-ready"), "false");
    await $(page, "editor").evaluate(editor => editor.dispatchEvent(new CompositionEvent("compositionend", { bubbles: true })));
    await layerReady(page);

    // Untrusted live DOM mismatch, interop loss and forced-colors remain legible.
    await page.evaluate(async () => {
      const editor = document.querySelector("#editor"), root = document.querySelector(".source-highlighting");
      const value = editor.value; editor.value = "DOM-only mismatch🙂";
      const module = await import("./js/editor-select.js");
      const ready = module.syncHighlight(root, editor.dataset.fileId, editor.dataset.highlightRevision);
      if (ready || getComputedStyle(editor).color === "rgba(0, 0, 0, 0)") throw new Error("mismatched textarea became invisible");
      editor.value = value; module.detachHighlight(root); // simulate lost decoration interop
      if (getComputedStyle(editor).color === "rgba(0, 0, 0, 0)") throw new Error("detached layer left invisible input");
      module.syncHighlight(root, editor.dataset.fileId, editor.dataset.highlightRevision);
    });
    await layerReady(page);
    await page.emulateMedia({ forcedColors: "active" });
    await page.waitForFunction(() => getComputedStyle(document.querySelector("#editor")).color !== "rgba(0, 0, 0, 0)");
    await page.emulateMedia({ forcedColors: "none" }); await layerReady(page);

    // Bounded plain fallback never truncates or disables actual source editing.
    const large = "z".repeat(128 * 1024 + 1); await $(page, "editor").fill(large);
    await page.waitForFunction(() => document.querySelector(".source-highlighting")?.dataset.highlightFallback === "TextLimit");
    assert.equal(await $(page, "editor").inputValue(), large);
    assert.equal(await page.locator(".source-highlight-text span").count(), 0);
    assert.notEqual(await $(page, "editor").evaluate(e => getComputedStyle(e).color), "rgba(0, 0, 0, 0)");
    await $(page, "editor").press("End"); await $(page, "editor").press("y");
    assert.equal(await $(page, "editor").inputValue(), large + "y");
    await save(page);
    // Keep main.tex's exact byte fixture untouched by all notes-file editing.
    await selectFile(page, "main.tex"); await H.assertBytes(page, name, bytes(expected));
    await page.waitForFunction(() => window.__oopOfflineReady === true, null, { timeout: 90000 });
    assert.equal(await page.evaluate(async () => !!(await caches.match(new URL("./js/editor-select.js", location.href).href))), true);
    await context.close(); context = null;
    await new Promise(resolve => proxy.close(resolve));
    const refused = await new Promise(resolve => {
      const request = http.get(base, () => resolve(false));
      request.on("error", () => resolve(true));
      request.setTimeout(1500, () => { request.destroy(); resolve(false); });
    });
    assert.equal(refused, true, "actual ephemeral browser-facing host is stopped");
    const cold = await coldOpen(name, expected);
    await H.reveal(cold, $(cold, "source-query")); await $(cold, "source-query").fill("Оновлено🙂");
    await $(cold, "source-replacement").fill("Офлайн🙂"); await selection(cold, 0);
    await sourceTool(cold, "next"); await sourceTool(cold, "replace"); await layerReady(cold); await save(cold);
    await H.assertBytes(cold, name, bytes(expected.replace("Оновлено🙂", "Офлайн🙂")));
    await screenshot(cold, "highlighting-cold-offline.png");
    assert.deepEqual(errors, []); assert.deepEqual(external, []);
    process.stdout.write("PASS actual lexical layer, escaping, native fallback, geometry, tools/search selection, bytes and cold offline restart\n");
  } catch (error) {
    fs.writeFileSync(path.join(output, "highlighting-failure.json"), JSON.stringify({ error: String(error).slice(0, 2000), errors, failures, external }, null, 2));
    throw error;
  } finally {
    if (context) await context.close();
    if (proxy.listening) await new Promise(resolve => proxy.close(resolve));
    fs.rmSync(profile, { recursive: true, force: true });
  }
})().catch(error => { process.stderr.write(`${error.stack || error}\n`); process.exitCode = 1; });
