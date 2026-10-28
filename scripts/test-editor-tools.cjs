// M4a: focused real published UI flow. Run by lead against the existing static
// publish; no compiler proof or complete authoring/parity claim is made here.
const assert = require("node:assert/strict");
const http = require("node:http");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { chromium } = require("playwright");
const H = require("./review-ui-helpers.cjs");
const { $ } = H;
const UPSTREAM = process.env.APP_URL || "http://static-origin:8080";
const PORT = 8102,
  BASE = `http://127.0.0.1:${PORT}`;
const proxy = http.createServer((req, res) => {
  const url = new URL(req.url, UPSTREAM);
  const upstream = http.request(
    url,
    { method: req.method, headers: { ...req.headers, host: url.host } },
    (r) => {
      res.writeHead(r.statusCode, r.headers);
      r.pipe(res);
    },
  );
  upstream.on("error", () => res.destroy());
  req.pipe(upstream);
});
const launch = { headless: true, args: ["--no-sandbox"] };
// Default still runs every source/stale/offline check. Focused diagnostics use
// the SAME publication and a fresh synthetic profile, not a weaker readiness.
const selectedCase = process.env.EDITOR_TOOLS_CASE || "all";
assert.ok(
  ["all", "offline"].includes(selectedCase),
  "EDITOR_TOOLS_CASE must be all or offline",
);
const raw = "перше🙂\r\nціль ціль\r\nостаннє\r\n";
const bom = Buffer.from([239, 187, 191]);
const bytes = (text) => Buffer.concat([bom, Buffer.from(text)]);
async function selection(p, start, end = start) {
  await $(p, "editor").evaluate((e, r) => e.setSelectionRange(r.start, r.end), {
    start,
    end,
  });
}
async function selected(p) {
  return $(p, "editor").evaluate((e) => ({
    start: e.selectionStart,
    end: e.selectionEnd,
  }));
}
async function tool(p, id) {
  await $(p, `source-${id}`).click();
}
async function generation(p) {
  const { generation } = await H.stamp(p);
  return generation;
}
async function save(p) {
  if (!(await $(p, "save").isDisabled())) await $(p, "save").click();
  await p.waitForFunction(
    () =>
      document.querySelector(".workspace-header .status")?.textContent ===
      "Saved.",
  );
}
async function create(p, text = raw) {
  await p.goto(BASE);
  await H.ready(p);
  const name = `Editor-tools-${Date.now()}-${Math.random().toString(36).slice(2)}`;
  await H.reveal(p, p.locator("#new-project"));
  await p.locator("#new-project").fill(name);
  await p.getByRole("button", { name: "Create project", exact: true }).click();
  await $(p, "import-input").setInputFiles({
    name: "main.tex",
    mimeType: "text/plain",
    buffer: bytes(text),
  });
  await p.locator(".source-tab", { hasText: "main.tex" }).waitFor();
  await save(p);
  await H.reveal(p, $(p, "source-query"));
  return name;
}
async function status(p, pattern, busy = false) {
  await p.waitForFunction(
    ({ pattern, busy }) =>
      document
        .querySelector('[data-testid="source-tools"]')
        ?.getAttribute("aria-busy") === String(busy) &&
      new RegExp(pattern).test(
        document.querySelector('[data-testid="source-status"]')?.textContent ||
          "",
      ),
    { pattern, busy },
  );
}
async function actualTools(browser) {
  const ctx = await browser.newContext({ serviceWorkers: "block" });
  const p = await ctx.newPage(),
    errors = [];
  p.on("pageerror", (e) => errors.push(String(e)));
  try {
    const name = await create(p),
      before = await H.head(p, name),
      stamp = await H.stamp(p);
    await $(p, "source-query").fill("ціль");
    await selection(p, 0);
    await tool(p, "next");
    await status(p, "1 of 2");
    const start = raw.replaceAll("\r\n", "\n").indexOf("ціль");
    assert.deepEqual(await selected(p), { start, end: start + 4 });
    await tool(p, "next");
    await status(p, "2 of 2");
    await tool(p, "next");
    await status(p, "1 of 2");
    await tool(p, "previous");
    await status(p, "2 of 2");
    const lastMatch = await selected(p);
    await $(p, "source-query").fill("");
    await tool(p, "next");
    await status(p, "0 of 0");
    assert.deepEqual(
      await selected(p),
      lastMatch,
      "empty query does not move selection",
    );
    await $(p, "source-query").fill("ЦІЛЬ");
    await $(p, "source-case").check();
    await tool(p, "next");
    await status(p, "0 of 0");
    await $(p, "source-case").uncheck();
    await tool(p, "next");
    await status(p, "1 of 2");
    await $(p, "source-query").fill("ціль");
    await tool(p, "next");
    await status(p, "2 of 2");
    assert.equal(
      await generation(p),
      stamp.generation,
      "find does not enter edit path",
    );
    assert.deepEqual(
      await H.head(p, name),
      before,
      "find does not save or dirty source",
    );
    assert.equal(await $(p, "save").isDisabled(), true);
    await $(p, "source-replacement").fill("нова🙂");
    await tool(p, "replace");
    await status(p, "Source edited");
    const expected = raw.replace("ціль ціль", "ціль нова🙂");
    assert.equal(
      await $(p, "editor").inputValue(),
      expected.replaceAll("\r\n", "\n"),
    );
    assert.ok(
      Number(await generation(p)) > Number(stamp.generation),
      "Coordinator edit path invalidates analysis",
    );
    await save(p);
    await H.assertBytes(p, name, bytes(expected));
    // The UI value is normalized LF, but durable CRLF + BOM must survive tools.
    const current = await $(p, "editor").inputValue();
    await selection(p, 0, current.length);
    await tool(p, "indent");
    await status(p, "Source edited");
    const indented = await $(p, "editor").inputValue();
    assert.ok(indented.startsWith("  перше"));
    await tool(p, "unindent");
    await p.waitForFunction(
      (expected) => document.querySelector("#editor").value === expected,
      expected.replaceAll("\r\n", "\n"),
    );
    await tool(p, "comment");
    await p.waitForFunction(() =>
      document.querySelector("#editor").value.startsWith("%перше"),
    );
    await tool(p, "comment");
    await p.waitForFunction(
      (expected) => document.querySelector("#editor").value === expected,
      expected.replaceAll("\r\n", "\n"),
    );
    await save(p);
    await H.assertBytes(p, name, bytes(expected));
    // Read-only second tab: find is allowed; no edit controls are enabled.
    const reader = await ctx.newPage();
    await reader.goto(BASE);
    await H.ready(reader);
    await H.open(reader, name);
    await H.reveal(reader, $(reader, "source-query"));
    assert.equal(await $(reader, "editor").isDisabled(), true);
    const readStamp = await H.stamp(reader);
    await $(reader, "source-query").fill("ціль");
    await tool(reader, "next");
    await status(reader, "1 of 1");
    for (const id of ["replace", "all", "indent", "unindent", "comment"])
      assert.equal(await $(reader, `source-${id}`).isDisabled(), true);
    assert.equal(await generation(reader), readStamp.generation);
    // Deliberately bypass the HTML disabled attribute: C# must still refuse.
    await $(reader, "source-all").evaluate((e) => {
      e.disabled = false;
      e.click();
    });
    await status(reader, "writable text file is required");
    assert.equal(await generation(reader), readStamp.generation);
    await H.assertBytes(p, name, bytes(expected));
    await reader.close();
    // Untrusted DOM-only value must not authorize edits against C# source.
    const unchanged = await H.stamp(p);
    await $(p, "editor").evaluate((e) => {
      e.value = "untrusted DOM only";
    });
    await tool(p, "all");
    await status(p, "Selection or source is invalid");
    assert.equal(await generation(p), unchanged.generation);
    assert.deepEqual(errors, []);
    // The toolbar must wrap at narrow viewport widths, not widen the page.
    await p.setViewportSize({ width: 390, height: 844 });
    assert.equal(
      await p.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth,
      ),
      true,
    );
  } finally {
    await ctx.close();
  }
  process.stdout.write(
    "PASS source search, real edits, CRLF/BOM, roundtrips, read-only, DOM mismatch and narrow layout\n",
  );
}
// SYNTHETIC network-delivery barrier only. The real shared selection module is
// neither replaced nor modified; fresh contexts block SW/module-map bypasses.
async function holdSelectionImport(ctx) {
  let observed, release;
  let released = false, hits = 0;
  const held = new Promise((r) => { observed = r; });
  const gate = new Promise((r) => { release = r; });
  await ctx.route("**/js/editor-select.js", async (route) => {
    hits++;
    observed();
    await gate;
    await route.continue();
  });
  return {
    async reached() {
      let timeout;
      try {
        await Promise.race([held, new Promise((_, reject) => {
          timeout = setTimeout(() => reject(new Error("selection import was not held")), 30000);
        })]);
      } finally { clearTimeout(timeout); }
    },
    release() { released = true; release(); },
    isHeld: () => !released,
    hits: () => hits,
  };
}

// Passive DOM observations only, never trusted C# requests. Observe from the
// latest explicit action through actual module evaluation + render checkpoints,
// so a stale callback repaint cannot be hidden by a later status assertion.
async function observeSourceOperation(p) {
  await p.evaluate(() => {
    const tools = document.querySelector('[data-testid="source-tools"]');
    const state = window.__editorOperationProbe = { released: false, rows: [] };
    const record = () => state.rows.push({
      released: state.released,
      busy: tools.getAttribute("aria-busy"),
      notice: tools.querySelector('[data-testid="source-status"]').textContent,
    });
    state.observer = new MutationObserver(record);
    state.observer.observe(tools, { subtree: true, childList: true, characterData: true, attributes: true, attributeFilter: ["aria-busy"] });
    record();
  });
}
async function releaseObservedImport(p, held) {
  await p.evaluate(() => { window.__editorOperationProbe.released = true; });
  held.release();
}
async function finishSourceObservation(p) {
  return p.evaluate(async () => {
    await import("./js/editor-select.js");
    await new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    const state = window.__editorOperationProbe;
    state.observer.disconnect();
    delete window.__editorOperationProbe;
    return state.rows;
  });
}
async function stale(browser, mode) {
  const ctx = await browser.newContext({ serviceWorkers: "block" });
  const held = await holdSelectionImport(ctx);
  const p = await ctx.newPage();
  try {
    const name = await create(p, "same same\n");
    // Both files exist BEFORE capture, so switching alone does not bump generation.
    await $(p, "new-file-name").fill("other.tex");
    await $(p, "new-file").click();
    await p.locator(".source-tab", { hasText: "other.tex" }).waitFor();
    await $(p, "editor").fill("same same\n");
    await save(p); // No pending ordinary autosave can obscure navigation-only assertions.
    await p
      .locator('[data-testid="select-file"]', { hasText: "main.tex" })
      .click();
    const before = await H.head(p, name), beforeStamp = await H.stamp(p);
    await H.reveal(p, $(p, "source-query"));
    await $(p, "source-query").fill("same");
    await $(p, "source-replacement").fill("WRONG");
    await tool(p, "all");
    await held.reached();
    await status(p, "Source action in progress", true);
    if (mode === "aba") {
      await $(p, "editor").fill("changed\n");
      await $(p, "editor").fill("same same\n");
    } else {
      await p
        .locator('[data-testid="select-file"]', { hasText: "other.tex" })
        .click();
      // Cancellation now settles promptly, BEFORE shared module delivery. This
      // is not a module failure and must release the interrupted busy owner.
      await status(p, "action discarded");
      assert.equal(held.isHeld(), true);
      assert.equal(await $(p, "source-next").isDisabled(), false);
      assert.doesNotMatch(await $(p, "source-status").textContent(), /module unavailable/);
      if (mode === "file-aba") {
        await p
          .locator('[data-testid="select-file"]', { hasText: "main.tex" })
          .click();
        // The second explicit tree action owns a NEWER notice. The cancelled
        // old source callback must not repaint 'action discarded' on release.
        await status(p, "Search changed");
      }
      assert.deepEqual(await H.stamp(p), beforeStamp);
      assert.deepEqual(await H.head(p, name), before);
    }
    const stamp = await H.stamp(p), range = await selected(p);
    const latestNotice = await $(p, "source-status").textContent();
    await observeSourceOperation(p);
    await releaseObservedImport(p, held);
    if (mode === "aba") await status(p, "action discarded");
    const rows = await finishSourceObservation(p);
    assert.ok(rows.every((row) => !/module unavailable/.test(row.notice)));
    if (mode !== "aba") {
      assert.ok(rows.every((row) => row.notice === latestNotice), "released old callback cannot repaint the latest tree action's notice");
      assert.equal(await $(p, "source-status").textContent(), latestNotice);
      assert.deepEqual(await selected(p), range);
      assert.deepEqual(await H.head(p, name), before);
      await p.locator(".source-tab", { hasText: mode === "file" ? "other.tex" : "main.tex" }).waitFor();
    }
    assert.equal(held.hits(), 1);
    assert.equal(await $(p, "editor").inputValue(), "same same\n");
    assert.equal(await generation(p), stamp.generation);
    assert.equal(await $(p, "source-query").inputValue(), "same");
    assert.equal(await $(p, "source-replacement").inputValue(), "WRONG");
  } finally {
    held.release();
    await ctx.close();
  }
  process.stdout.write(`PASS asynchronous stale ${mode}: precise cancellation/notice ownership and no late source/selection changes\n`);
}
async function sourceOperationOwnership(browser, mode) {
  const ctx = await browser.newContext({ serviceWorkers: "block" });
  const held = await holdSelectionImport(ctx);
  const p = await ctx.newPage(), errors = [];
  p.on("pageerror", (error) => errors.push(String(error)));
  try {
    const name = await create(p, "same same\n");
    await $(p, "new-file-name").fill("other.tex");
    await $(p, "new-file").click();
    await p.locator(".source-tab", { hasText: "other.tex" }).waitFor();
    await $(p, "editor").fill("same same\n");
    await save(p);
    await p.locator('[data-testid="select-file"]', { hasText: "main.tex" }).click();
    await H.reveal(p, $(p, "source-query"));
    await $(p, "source-query").fill("same");
    await $(p, "source-replacement").fill("WRONG");
    const before = await H.head(p, name), stamp = await H.stamp(p);
    await tool(p, "all");
    await held.reached();
    await status(p, "Source action in progress", true);
    if (mode === "newer-source") {
      // Start the real competing action as soon as the real tree click enables
      // its button. Do not wait for the older callback's catch/finally first.
      await p.locator('[data-testid="select-file"]', { hasText: "other.tex" }).click();
      await tool(p, "next");
      await status(p, "Source action in progress", true);
    } else {
      // A query-only intent change keeps the same operation owner. Its eventual
      // finalizer must release busy despite the changed intent, without erasing
      // the query handler's newer notice or applying the original replacement.
      await $(p, "source-query").fill("new query");
      await status(p, "Search changed", true);
    }
    await observeSourceOperation(p);
    assert.equal(held.isHeld(), true);
    assert.equal(await $(p, "source-next").isDisabled(), true);
    assert.deepEqual(await H.stamp(p), stamp);
    assert.deepEqual(await H.head(p, name), before);
    // These are actual independent UI/storage round trips while delivery is
    // held: an older finalizer cannot clear the newer/in-progress busy owner.
    assert.equal(await $(p, "source-next").isDisabled(), true);
    const oldRange = await selected(p);
    await releaseObservedImport(p, held);
    await status(p, mode === "newer-source" ? "1 of 2" : "Search changed");
    const rows = await finishSourceObservation(p);
    assert.ok(rows.filter((row) => !row.released).every((row) => row.busy === "true"), "busy ownership cannot be cleared before held import delivery");
    assert.ok(rows.every((row) => !/action discarded|unavailable|Source edited/.test(row.notice)), "old source callbacks cannot overwrite newer action/query status");
    if (mode === "newer-source") {
      assert.deepEqual(await selected(p), { start: 0, end: 4 });
      await p.locator(".source-tab", { hasText: "other.tex" }).waitFor();
      assert.match(await $(p, "source-status").textContent(), /1 of 2/);
    } else {
      assert.deepEqual(await selected(p), oldRange);
      assert.equal(await $(p, "source-query").inputValue(), "new query");
      assert.match(await $(p, "source-status").textContent(), /Search changed/);
    }
    assert.equal(await $(p, "source-next").isDisabled(), false);
    assert.equal(held.hits(), 1, "competing source operations share one genuine module import");
    assert.deepEqual(await H.stamp(p), stamp);
    assert.deepEqual(await H.head(p, name), before);
    assert.equal(await $(p, "editor").inputValue(), "same same\n");
    assert.equal(await $(p, "source-replacement").inputValue(), "WRONG");
    assert.deepEqual(errors, []);
  } finally {
    held.release();
    await ctx.close();
  }
  process.stdout.write(`PASS source operation ownership under held import: ${mode}\n`);
}
async function sourceImportFailure(browser) {
  const ctx = await browser.newContext({ serviceWorkers: "block" });
  // SYNTHETIC genuine import failure installed BEFORE any project/file is
  // created, so it covers the highlighting layer's proactive editor-select
  // import too. A fresh context already blocks SW/module-map bypasses; the
  // abort barrier must precede the first file load, or the proactively cached
  // module would never see the failure and the route would miss it. This also
  // tests legible highlight fallback through a normal startup module failure.
  await ctx.route("**/js/editor-select.js", (route) => route.abort("failed"));
  const p = await ctx.newPage();
  try {
    const name = await create(p, "same same\n");
    const before = await H.head(p, name), stamp = await H.stamp(p);
    // Cancellation must not be labelled this way, but a real failure must
    // remain visible for the CURRENT operation against the failed module.
    await $(p, "source-query").fill("same");
    await tool(p, "next");
    await status(p, "selection module unavailable");
    assert.equal(await $(p, "source-next").isDisabled(), false);
    assert.deepEqual(await H.stamp(p), stamp);
    assert.deepEqual(await H.head(p, name), before);
  } finally { await ctx.close(); }
  process.stdout.write("PASS genuine current module failure remains distinguishable from cancellation/staleness\n");
}
// Passive, bounded startup evidence only. Never record headers, request bodies,
// storage contents or provider URLs; this profile contains synthetic projects.
function diagnosticUrl(value) {
  try {
    const url = new URL(value);
    if (url.origin !== BASE) return "<non-test-origin URL omitted>";
    url.username = "";
    url.password = "";
    url.hash = "";
    for (const key of [...url.searchParams.keys()]) {
      if (/key|token|secret|credential|authorization|code/i.test(key))
        url.searchParams.set(key, "<redacted>");
    }
    return url.href.slice(0, 2048);
  } catch {
    return "<invalid URL>";
  }
}
function startupTrace(page) {
  const trace = {
    pageErrors: [],
    consoleErrors: [],
    failedRequests: [],
    httpErrors: [],
    requestedUrls: [],
    dropped: {},
  };
  const append = (kind, item, limit = 32) => {
    if (trace[kind].length < limit) trace[kind].push(item);
    else trace.dropped[kind] = (trace.dropped[kind] || 0) + 1;
  };
  page.on("pageerror", (error) =>
    append("pageErrors", String(error).slice(0, 2000)),
  );
  page.on("console", (message) => {
    if (message.type() === "error")
      append("consoleErrors", message.text().slice(0, 2000));
  });
  page.on("request", (request) => {
    if (request.method() !== "GET") return;
    const url = diagnosticUrl(request.url());
    if (!trace.requestedUrls.includes(url)) append("requestedUrls", url, 384);
  });
  page.on("requestfailed", (request) =>
    append(
      "failedRequests",
      {
        url: diagnosticUrl(request.url()),
        resourceType: request.resourceType(),
        error: (request.failure()?.errorText || "unknown").slice(0, 500),
      },
      64,
    ),
  );
  page.on("response", (response) => {
    if (response.status() >= 400)
      append(
        "httpErrors",
        {
          url: diagnosticUrl(response.url()),
          status: response.status(),
          fromServiceWorker: response.fromServiceWorker(),
        },
        64,
      );
  });
  return trace;
}
async function diagnosticDeadline(task) {
  let timer;
  try {
    return await Promise.race([
      task,
      new Promise((_, reject) => {
        timer = setTimeout(
          () => reject(new Error("diagnostic snapshot exceeded 5 seconds")),
          5000,
        );
      }),
    ]);
  } finally {
    clearTimeout(timer);
  }
}
async function startupState(page, trace, provisionedUrls = []) {
  const candidates = [
    ...new Set([
      ...trace.failedRequests.map((request) => request.url),
      ...trace.httpErrors.map((response) => response.url),
      ...trace.requestedUrls,
      ...provisionedUrls,
      `${BASE}/`,
      `${BASE}/index.html`,
      `${BASE}/js/editor-select.js`,
    ]),
  ]
    .filter((url) => url.startsWith(`${BASE}/`))
    .slice(0, 512);
  const state = await diagnosticDeadline(
    page.evaluate(async (candidates) => {
      const clip = (value) => String(value || "").slice(0, 2000);
      const safeUrl = (value) => {
        try {
          const url = new URL(value, location.href);
          if (url.origin !== location.origin)
            return "<non-test-origin URL omitted>";
          url.username = "";
          url.password = "";
          url.hash = "";
          for (const key of [...url.searchParams.keys()])
            if (/key|token|secret|credential|authorization|code/i.test(key))
              url.searchParams.set(key, "<redacted>");
          return url.href.slice(0, 2048);
        } catch {
          return "<invalid URL>";
        }
      };
      const state = {
        url: safeUrl(location.href),
        readyState: document.readyState,
        online: navigator.onLine,
        title: clip(document.title),
        bodyText: clip(document.body?.innerText),
        appText: clip(document.querySelector("#app")?.textContent),
        workspacePresent: !!document.querySelector(
          ".project-dashboard,[data-testid=new-file-name]",
        ),
        bootstrap: {
          blazorType: typeof globalThis.Blazor,
          dotNetType: typeof globalThis.DotNet,
          appStartupSucceeded:
            typeof appStartupSucceeded === "boolean"
              ? appStartupSucceeded
              : null,
          offlineReady: globalThis.__oopOfflineReady ?? null,
          offlineReadiness: globalThis.__oopOfflineReadiness ?? null,
          blazorErrorText: clip(
            document.querySelector("#blazor-error-ui")?.textContent,
          ),
        },
        scripts: [...document.scripts]
          .filter((script) => script.src)
          .slice(0, 24)
          .map((script) => safeUrl(script.src)),
        serviceWorker: {
          supported: "serviceWorker" in navigator,
          controller: null,
          registrations: [],
        },
        caches: [],
        cacheProbes: [],
      };
      try {
        const controller = navigator.serviceWorker?.controller;
        state.serviceWorker.controller = controller
          ? {
              scriptURL: safeUrl(controller.scriptURL),
              state: controller.state,
            }
          : null;
        const registrations =
          (await navigator.serviceWorker?.getRegistrations()) || [];
        state.serviceWorker.registrations = registrations
          .slice(0, 8)
          .map((registration) => ({
            scope: safeUrl(registration.scope),
            active: registration.active
              ? {
                  scriptURL: safeUrl(registration.active.scriptURL),
                  state: registration.active.state,
                }
              : null,
            waiting: registration.waiting
              ? {
                  scriptURL: safeUrl(registration.waiting.scriptURL),
                  state: registration.waiting.state,
                }
              : null,
            installing: registration.installing
              ? {
                  scriptURL: safeUrl(registration.installing.scriptURL),
                  state: registration.installing.state,
                }
              : null,
          }));
      } catch (error) {
        state.serviceWorker.error = clip(error);
      }
      try {
        const names = await caches.keys();
        for (const name of names.slice(0, 8)) {
          const cache = await caches.open(name);
          state.caches.push({
            name: clip(name),
            entries: (await cache.keys()).length,
          });
        }
        state.omittedCacheCount = Math.max(0, names.length - 8);
        for (const url of candidates) {
          state.cacheProbes.push({
            url,
            defaultMatch: !!(await caches.match(url)),
            exactUrlIgnoringVary: !!(await caches.match(url, {
              ignoreVary: true,
            })),
            ignoringQueryAndVary: !!(await caches.match(url, {
              ignoreSearch: true,
              ignoreVary: true,
            })),
          });
        }
        // Cache absence alone is not proof of a required missing dependency (the
        // worker itself is intentionally uncached). Correlate with failed URLs.
        state.uncachedResourceUrls = state.cacheProbes
          .filter((probe) => !probe.exactUrlIgnoringVary)
          .map((probe) => probe.url);
      } catch (error) {
        state.cacheError = clip(error);
      }
      return state;
    }, candidates),
  );
  return state;
}
async function startupFailure(page, trace, phase, error, provisioned) {
  const diagnostic = {
    event: "editor-tools-offline-startup-failure",
    phase,
    error: String(error).slice(0, 2000),
    trace,
    provisioned: provisioned || null,
    failedResourceUrls: [
      ...new Set([
        ...trace.failedRequests.map((request) => request.url),
        ...trace.httpErrors.map((response) => response.url),
      ]),
    ],
  };
  try {
    diagnostic.state = await startupState(
      page,
      trace,
      provisioned?.requestedUrls || [],
    );
  } catch (snapshotError) {
    diagnostic.snapshotError = String(snapshotError).slice(0, 1000);
  }
  if (process.env.RESULT_DIR) {
    try {
      const directory = path.resolve(process.env.RESULT_DIR);
      fs.mkdirSync(directory, { recursive: true });
      const screenshot = path.join(directory, `editor-tools-${phase}.png`);
      await page.screenshot({
        path: screenshot,
        fullPage: false,
        timeout: 5000,
      });
      diagnostic.screenshot = screenshot;
    } catch (screenshotError) {
      diagnostic.screenshotError = String(screenshotError).slice(0, 1000);
    }
  }
  process.stderr.write(`${JSON.stringify(diagnostic)}\n`);
}
async function offline(profile) {
  let ctx = await chromium.launchPersistentContext(profile, launch);
  let name, provisioned;
  try {
    const p = await ctx.newPage();
    const trace = startupTrace(p); // Attach before create() performs navigation.
    try {
      name = await create(p);
      await p.waitForFunction(() => window.__oopOfflineReady === true, null, {
        timeout: 90000,
      });
      assert.equal(
        await p.evaluate(
          async () =>
            !!(await caches.match(
              new URL("/js/editor-select.js", location.href).href,
            )),
        ),
        true,
      );
      // Best-effort diagnostic snapshot, not another readiness condition or a
      // claim that module presence alone establishes offline startup readiness.
      provisioned = { requestedUrls: trace.requestedUrls.slice() };
      try {
        provisioned.state = await startupState(p, trace);
      } catch (error) {
        provisioned.snapshotError = String(error).slice(0, 1000);
      }
    } catch (error) {
      await startupFailure(p, trace, "provisioning", error, null);
      throw error;
    }
  } finally {
    await ctx.close();
  }
  // Stop the browser-facing host; then launch a fresh browser process/profile
  // with networking disabled BEFORE navigation. This is not a warm reload.
  await new Promise((r) => proxy.close(r));
  ctx = await chromium.launchPersistentContext(profile, launch);
  try {
    await ctx.setOffline(true);
    const p = await ctx.newPage();
    const trace = startupTrace(p); // Attach before the cold offline navigation.
    try {
      await p.goto(BASE, { waitUntil: "domcontentloaded" });
      await H.ready(p);
    } catch (error) {
      await startupFailure(p, trace, "cold-start", error, provisioned);
      throw error;
    }
    // Active project need not be restored automatically after a cold restart.
    if (await p.locator(".project-dashboard").count()) await H.open(p, name);
    await H.reveal(p, $(p, "source-query"));
    await $(p, "source-query").fill("ціль");
    await tool(p, "next");
    await status(p, "1 of 2");
    await $(p, "source-replacement").fill("нова");
    await tool(p, "all");
    await status(p, "Source edited");
    assert.equal(
      await $(p, "editor").inputValue(),
      raw.replaceAll("ціль", "нова").replaceAll("\r\n", "\n"),
    );
    await save(p);
    await H.assertBytes(p, name, bytes(raw.replaceAll("ціль", "нова")));
  } finally {
    await ctx.close();
  }
  process.stdout.write(
    "PASS cached source module and tools after offline cold browser restart with host stopped\n",
  );
}
(async () => {
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), "editor-tools-"));
  let browser;
  try {
    await new Promise((r) => proxy.listen(PORT, "127.0.0.1", r));
    if (selectedCase === "all") {
      browser = await chromium.launch(launch);
      await actualTools(browser);
      for (const mode of ["aba", "file", "file-aba"])
        await stale(browser, mode);
      for (const mode of ["newer-source", "query-only"])
        await sourceOperationOwnership(browser, mode);
      await sourceImportFailure(browser);
      await browser.close();
      browser = null;
    }
    await offline(profile);
  } finally {
    if (browser) await browser.close();
    if (proxy.listening) await new Promise((r) => proxy.close(r));
    fs.rmSync(profile, { recursive: true, force: true });
  }
})().catch((error) => {
  process.stderr.write(`${error.stack || error}\n`);
  process.exitCode = 1;
});
