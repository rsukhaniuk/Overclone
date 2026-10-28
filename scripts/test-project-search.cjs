// Mounted published C#/Blazor project search. Real coordinator, imports, storage,
// source mapping and editor-select module; no trusted request/proposal injection.
// SYNTHETIC faults are DOM-only mismatches and a held NETWORK module import.
// Run only against a lead-coordinated published static build with cached assets.
const assert = require("node:assert/strict");
const http = require("node:http");
const { chromium } = require("playwright");
const H = require("./review-ui-helpers.cjs");
const { $, reveal } = H;
const UPSTREAM = process.env.APP_URL || "http://static-origin:8080";
const PORT = Number(process.env.PROJECT_SEARCH_PORT || 8102);
const BASE = `http://127.0.0.1:${PORT}`;
const QUERY = "Needle🙂";
const A =
  'Україна🙂\r\nNeedle🙂 <img src=x onerror="window.__projectSearchXss=1"> <script>window.__projectSearchXss=1</script> &\r\nneedle🙂\r\n';
const TEXT = new Map([
  ["z-last.tex", "кінець Needle🙂 tail\n"],
  ["m&notes.tex", "Needle🙂\n"],
  ["b-second.tex", "Needle🙂 { unmatched\n"],
  ["a-first.tex", A],
]);
const normalize = (text) => text.replace(/\r\n/g, "\n").replace(/\r/g, "\n");
const proxy = http.createServer((req, res) => {
  const u = new URL(req.url, UPSTREAM);
  const upstream = http.request(
    u,
    { method: req.method, headers: { ...req.headers, host: u.host } },
    (reply) => {
      res.writeHead(reply.statusCode, reply.headers);
      reply.pipe(res);
    },
  );
  upstream.on("error", () => res.destroy());
  req.pipe(upstream);
});

async function deadline(promise, ms, message) {
  let timer;
  try {
    return await Promise.race([
      promise,
      new Promise((_, reject) => {
        timer = setTimeout(() => reject(new Error(message)), ms);
      }),
    ]);
  } finally {
    clearTimeout(timer);
  }
}
async function newPage(context) {
  const p = await context.newPage();
  const errors = [],
    external = [];
  p.setDefaultTimeout(15000);
  p.on("pageerror", (e) => errors.push(String(e)));
  p.on("request", (r) => {
    if (!r.url().startsWith(BASE) && !/^(data|blob):/.test(r.url()))
      external.push(r.url());
  });
  await p.goto(BASE);
  await H.ready(p);
  return { p, errors, external };
}
async function importFile(p, name, buffer) {
  await $(p, "import-input").setInputFiles({
    name,
    mimeType: name.endsWith(".bin") ? "application/octet-stream" : "text/plain",
    buffer,
  });
  await p.locator(".source-tab", { hasText: name }).waitFor();
}
function tree(p, path) {
  return $(p, "select-file").filter({
    has: p.locator(".file-name", { hasText: path }),
  });
}
async function chooseFile(p, path) {
  await tree(p, path).click();
  await p.locator(".source-tab", { hasText: path }).waitFor();
}
async function fixture(p, prefix) {
  const name = `${prefix}-${Date.now()}-${Math.random().toString(36).slice(2, 7)}`;
  await reveal(p, p.locator("#new-project"));
  await p.locator("#new-project").fill(name);
  await p.getByRole("button", { name: "Create project", exact: true }).click();
  await $(p, "new-file-name").waitFor();
  for (const [path, text] of TEXT) {
    const body = Buffer.from(text);
    await importFile(
      p,
      path,
      path === "a-first.tex"
        ? Buffer.concat([Buffer.from([239, 187, 191]), body])
        : body,
    );
  }
  await importFile(
    p,
    "asset.bin",
    Buffer.concat([Buffer.from([255, 0, 254]), Buffer.from(QUERY)]),
  );
  await chooseFile(p, "z-last.tex");
  await H.save(p);
  await reveal(p, $(p, "project-search-query"));
  return name;
}
async function status(p, expected) {
  await p.waitForFunction(
    (expected) =>
      document.querySelector("[data-testid=project-search-status]")?.dataset
        .searchStatus === expected,
    expected,
    { timeout: 15000 },
  );
}
async function searched(p, query, matchCase, expected = "Complete") {
  // Wait for the rendered last-explicit-search label, not merely a terminal
  // status that could still belong to the previous search.
  await p.waitForFunction(
    ({ query, matchCase, expected }) => {
      const state = document.querySelector(
        "[data-testid=project-search-status]",
      );
      const last = document.querySelector(
        "[data-testid=project-search-summary]",
      )?.nextElementSibling;
      return (
        state?.dataset.searchStatus === expected &&
        last?.querySelector("code")?.textContent === query &&
        last.textContent
          .trim()
          .endsWith(matchCase ? "(case sensitive)." : "(ignore case).")
      );
    },
    { query, matchCase, expected },
    { timeout: 15000 },
  );
}
async function search(p, query = QUERY, matchCase = true) {
  await $(p, "project-search-query").fill(query);
  await $(p, "project-search-case").setChecked(matchCase);
  await $(p, "project-search-submit").click();
  await searched(p, query, matchCase);
}
async function paths(p) {
  return $(p, "project-search-result").evaluateAll((nodes) =>
    nodes.map((e) => e.dataset.sourcePath),
  );
}
function result(p, path, ordinal = 0) {
  // Path is selected by DOM data for assertions/clicks only. It never mints a
  // navigation request; the real button closure uses the owned C# match object.
  return $(p, "project-search-result")
    .filter({ has: p.locator("span", { hasText: `${path} — line` }) })
    .nth(ordinal);
}
async function selection(p) {
  return $(p, "editor").evaluate((e) => ({
    value: e.value,
    start: e.selectionStart,
    end: e.selectionEnd,
    id: e.dataset.fileId,
    disabled: e.disabled,
  }));
}
async function expectSelection(
  p,
  path,
  query = QUERY,
  sourceStart = TEXT.get(path).indexOf(query),
) {
  const source = TEXT.get(path);
  const start = normalize(source.slice(0, sourceStart)).length;
  const end = normalize(source.slice(0, sourceStart + query.length)).length;
  await p.waitForFunction(
    ({ value, start, end }) => {
      const e = document.querySelector("[data-testid=editor]");
      return (
        e?.value === value &&
        e.selectionStart === start &&
        e.selectionEnd === end
      );
    },
    { value: normalize(source), start, end },
  );
  await p.waitForFunction(
    (path) =>
      (
        document.querySelector("[data-testid=project-search-navigation-status]")
          ?.textContent || ""
      ).startsWith(`Selected ${path},`),
    path,
  );
  assert.equal((await selection(p)).value.slice(start, end), normalize(query));
  assert.match(
    await $(p, "project-search-navigation-status").textContent(),
    /^Selected /,
  );
  await p.locator(".source-tab", { hasText: path }).waitFor();
}
async function unchanged(p, name, before, stamp) {
  assert.deepEqual(
    await H.head(p, name),
    before,
    "search/navigation must not write a durable revision or alter source bytes",
  );
  assert.deepEqual(
    await H.stamp(p),
    stamp,
    "search/navigation must not change working generation/session/head",
  );
  assert.equal(await p.locator(".dirty-dot").count(), 0);
}

async function functional(browser) {
  const ctx = await browser.newContext({ serviceWorkers: "block" });
  try {
    const { p, errors, external } = await newPage(ctx);
    const name = await fixture(p, "ProjectSearch");
    const before = await H.head(p, name),
      stamp = await H.stamp(p);
    assert.deepEqual(
      Buffer.from(before.files.find((f) => f.path === "a-first.tex").bytes),
      Buffer.concat([Buffer.from([239, 187, 191]), Buffer.from(A)]),
    );
    await status(p, "Stale"); // fixture imports invalidated the initial empty listing
    await $(p, "project-search-query").fill(QUERY);
    await $(p, "project-search-case").uncheck();
    assert.equal(
      await $(p, "project-search-result").count(),
      0,
      "typing/toggling alone must not search",
    );
    await search(p);
    assert.deepEqual(await paths(p), [
      "a-first.tex",
      "b-second.tex",
      "m&notes.tex",
      "z-last.tex",
    ]);
    assert.match(
      await $(p, "project-search-summary").textContent(),
      /4 text files; 1 binary files skipped/,
    );
    assert.match(
      await result(p, "a-first.tex").textContent(),
      /<img src=x onerror=/,
    );
    assert.equal(
      await $(p, "project-search-panel").locator("img,script").count(),
      0,
    );
    assert.equal(await p.evaluate(() => window.__projectSearchXss), undefined);
    assert.equal(
      await result(p, "a-first.tex").getAttribute("data-source-start"),
      String(A.indexOf(QUERY)),
    );
    await $(p, "project-search-case").uncheck();
    assert.equal(
      await $(p, "project-search-result").count(),
      4,
      "case preference is not an implicit Search",
    );
    await $(p, "project-search-submit").click();
    await searched(p, QUERY, false);
    assert.deepEqual(await paths(p), [
      "a-first.tex",
      "a-first.tex",
      "b-second.tex",
      "m&notes.tex",
      "z-last.tex",
    ]);
    const starts = await $(p, "project-search-result").evaluateAll((nodes) =>
      nodes.slice(0, 2).map((n) => Number(n.dataset.sourceStart)),
    );
    assert.deepEqual(starts, [A.indexOf(QUERY), A.indexOf("needle🙂")]);

    // Cross-file CRLF/BOM/emoji mapping, then same-file selection without a
    // preliminary render. Source positions differ from normalized DOM positions.
    await result(p, "a-first.tex").click();
    await expectSelection(p, "a-first.tex");
    await $(p, "editor").evaluate((e) => e.setSelectionRange(0, 0));
    await result(p, "a-first.tex", 1).click();
    await expectSelection(p, "a-first.tex", "needle🙂", A.indexOf("needle🙂"));
    await result(p, "m&notes.tex").click();
    await expectSelection(p, "m&notes.tex");
    await unchanged(p, name, before, stamp);

    // Read-only is established by the real writer lock in this tab, not DOM
    // flags or synthetic coordinator state. Both cross- and same-file find work.
    const ro = await newPage(ctx);
    await H.open(ro.p, name);
    await ro.p.locator(".banner-readonly").waitFor();
    await reveal(ro.p, $(ro.p, "project-search-query"));
    const roStamp = await H.stamp(ro.p);
    await search(ro.p);
    await result(ro.p, "z-last.tex").click();
    await expectSelection(ro.p, "z-last.tex");
    assert.equal((await selection(ro.p)).disabled, true);
    await result(ro.p, "a-first.tex").click();
    await expectSelection(ro.p, "a-first.tex");
    await $(ro.p, "editor").evaluate((e) => e.setSelectionRange(0, 0));
    await result(ro.p, "a-first.tex").click();
    await expectSelection(ro.p, "a-first.tex");
    assert.equal((await selection(ro.p)).disabled, true);
    await unchanged(ro.p, name, before, roStamp);
    assert.deepEqual(ro.errors, []);
    assert.deepEqual(ro.external, []);
    await ro.p.close();

    await result(p, "a-first.tex").click();
    await expectSelection(p, "a-first.tex");
    const id = (await selection(p)).id;
    // SYNTHETIC DOM-only text mismatch, no input event, no trusted C# request
    // injection. The same-file path must reject instead of rendering it away.
    await $(p, "editor").evaluate((e) => {
      e.value += " DOM-only mismatch";
      e.setSelectionRange(0, 0);
    });
    await result(p, "a-first.tex").click();
    await p.waitForFunction(() =>
      /selection rejected/.test(
        document.querySelector("[data-testid=project-search-navigation-status]")
          ?.textContent || "",
      ),
    );
    let dom = await selection(p);
    assert.equal(dom.value, normalize(A) + " DOM-only mismatch");
    assert.equal(dom.start, 0);
    assert.equal(dom.end, 0);
    await unchanged(p, name, before, stamp);
    await $(p, "editor").evaluate((e, value) => {
      e.value = value;
    }, normalize(A));
    await result(p, "a-first.tex").click();
    await expectSelection(p, "a-first.tex");
    // SYNTHETIC wrong-file DOM identity: identical source alone cannot pass the
    // atomic file-ID guard. Dataset values are not navigation inputs.
    await $(p, "editor").evaluate((e) => {
      e.dataset.fileId = "00000000000000000000000000000000";
      e.setSelectionRange(0, 0);
    });
    await result(p, "a-first.tex").click();
    await p.waitForFunction(() =>
      /selection rejected/.test(
        document.querySelector("[data-testid=project-search-navigation-status]")
          ?.textContent || "",
      ),
    );
    dom = await selection(p);
    assert.equal(dom.start, 0);
    assert.equal(dom.end, 0);
    await $(p, "editor").evaluate((e, id) => {
      e.dataset.fileId = id;
    }, id);
    await unchanged(p, name, before, stamp);

    // Narrow viewport: search itself must not cause page horizontal overflow;
    // result scrolling stays bounded and the existing project actions survive.
    await p.setViewportSize({ width: 390, height: 844 });
    assert.ok(await $(p, "new-file").isVisible());
    assert.ok(await $(p, "import-input").isVisible());
    assert.equal(
      await p.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth + 1,
      ),
      true,
    );
    assert.equal(
      await $(p, "project-search-results").evaluate(
        (e) =>
          e.scrollHeight <= e.clientHeight ||
          getComputedStyle(e).overflowY === "auto",
      ),
      true,
    );
    await p.setViewportSize({ width: 1440, height: 1000 });

    await $(p, "project-search-query").fill("needle🙂");
    await $(p, "project-search-case").uncheck();
    await $(p, "project-search-submit").click();
    await status(p, "Complete");
    const edited = normalize(A) + "\n% ordinary edit\n";
    await $(p, "editor").fill(edited);
    await status(p, "Stale");
    assert.equal(await $(p, "project-search-result").count(), 0);
    assert.equal(await $(p, "project-search-query").inputValue(), "needle🙂");
    assert.equal(await $(p, "project-search-case").isChecked(), false);
    assert.notEqual((await H.stamp(p)).generation, stamp.generation);
    await H.save(p);
    assert.equal(await $(p, "project-search-query").inputValue(), "needle🙂");
    await $(p, "project-search-submit").click();
    await status(p, "Complete");
    assert.equal(await $(p, "project-search-result").count(), 5);
    assert.deepEqual(errors, []);
    assert.deepEqual(external, []);
    process.stdout.write(
      "PASS mounted project search: explicit literal search, order/case, escaped text, exact CRLF/BOM/emoji cross/same-file selection, read-only selection, unchanged source/generation/head, mismatch rejection, stale clearing/preferences, narrow layout\n",
    );
  } finally {
    await ctx.close();
  }
}

async function heldImport(browser, departure) {
  // Fresh context/realm + blocked SW guarantee that the first module import
  // reaches this actual network barrier, rather than an ES-module/SW cache.
  // The barrier is registered BEFORE the first file is imported: the
  // highlighting layer proactively imports editor-select.js on the first file,
  // so a route installed after fixture() would miss that cached module. The
  // held delivery may therefore already belong to highlighting by the time the
  // result is clicked; the click does not necessarily initiate the first
  // fetch, and actual search navigation awaits the SAME real single-flight
  // module import. newPage is first since no file means no proactive import.
  const ctx = await browser.newContext({ serviceWorkers: "block" });
  let release = () => {};
  try {
    const { p, errors } = await newPage(ctx);
    let reachedResolve;
    const reached = new Promise((resolve) => {
      reachedResolve = resolve;
    });
    const gate = new Promise((resolve) => {
      release = resolve;
    });
    let hits = 0;
    const handler = async (route) => {
      // Obtain the actual published module unchanged. Only delivery is held.
      const response = await route.fetch({ timeout: 10000 });
      assert.equal(response.status(), 200);
      hits++;
      reachedResolve();
      await gate;
      await route.fulfill({ response });
    };
    await p.route("**/js/editor-select.js", handler);
    const name = await fixture(p, `SearchHeld-${departure}`);
    const before = await H.head(p, name),
      stamp = await H.stamp(p);
    await search(p);
    await result(p, "a-first.tex").click();
    await deadline(
      reached,
      10000,
      "search did not reach the genuinely held editor-select import",
    );
    await p.locator(".source-tab", { hasText: "a-first.tex" }).waitFor();
    if (departure === "tree") {
      await chooseFile(p, "b-second.tex");
      const current = await selection(p);
      const delivered = p.waitForResponse((r) =>
        r.url().endsWith("/js/editor-select.js"),
      );
      release();
      await delivered;
      await p.waitForFunction(() =>
        /superseded/.test(
          document.querySelector(
            "[data-testid=project-search-navigation-status]",
          )?.textContent || "",
        ),
      );
      // Observe past module settlement to catch late reclamation, not just the
      // synchronous file switch before the old callback could resume.
      await p.waitForTimeout(300);
      assert.deepEqual(await selection(p), current);
      await p.locator(".source-tab", { hasText: "b-second.tex" }).waitFor();
    } else {
      await reveal(p, $(p, "source-query"));
      await $(p, "source-query").fill("Україна");
      await $(p, "source-next").click();
      release();
      await p.waitForFunction(() => {
        const e = document.querySelector("[data-testid=editor]");
        return e?.selectionStart === 0 && e.selectionEnd === "Україна".length;
      });
      assert.match(await $(p, "source-status").textContent(), /1 of 1/);
      assert.match(
        await $(p, "project-search-navigation-status").textContent(),
        /superseded/,
      );
      await p.waitForTimeout(300);
      const current = await selection(p);
      assert.equal(current.start, 0);
      assert.equal(current.end, 7);
    }
    assert.equal(
      hits,
      1,
      "all competing callers share the real single-flight module import",
    );
    assert.equal(
      await $(p, "project-search-result").count(),
      4,
      "active-file selection alone must preserve listing",
    );
    await unchanged(p, name, before, stamp);
    assert.deepEqual(errors, []);
    await p.unroute("**/js/editor-select.js", handler);
    process.stdout.write(
      `PASS held real module import: later ${departure} navigation wins; cancelled search cannot reclaim file/range\n`,
    );
  } finally {
    release();
    await ctx.close();
  }
}

async function performanceProbe(browser) {
  const ctx = await browser.newContext({ serviceWorkers: "block" });
  try {
    const { p, errors } = await newPage(ctx);
    await fixture(p, "SearchRepetition");
    // Representative 4 MiB repetitive source + longest permitted query. Keep
    // this fixed: do not reduce it to make synchronous UI latency look better.
    const sourceBytes = 4 * 1024 * 1024;
    await importFile(
      p,
      "large.tex",
      Buffer.from("a".repeat(sourceBytes) + "🙂\r\n"),
    );
    await H.save(p);
    // Avoid attributing an independently queued analyzer render to the search
    // probe. This is the real UI cancellation, not a worker replacement.
    await reveal(p, $(p, "cancel-analysis"));
    await $(p, "cancel-analysis").click();
    const original = await H.stamp(p);
    const measurements = [];
    for (const [label, query, expected] of [
      ["long-near-miss", "a".repeat(1023) + "b", "Complete"],
      ["repetitive-result-cap", "a".repeat(1024), "Truncated"],
    ]) {
      await $(p, "project-search-query").fill(query);
      await $(p, "project-search-case").check();
      await p.evaluate(() => {
        const probe = (window.__projectSearchTiming = {
          started: performance.now(),
          last: performance.now(),
          maxGapMs: 0,
          ticks: 0,
          longTasks: [],
        });
        probe.timer = setInterval(() => {
          const now = performance.now();
          probe.maxGapMs = Math.max(probe.maxGapMs, now - probe.last);
          probe.last = now;
          probe.ticks++;
        }, 20);
        if (PerformanceObserver.supportedEntryTypes.includes("longtask")) {
          probe.observer = new PerformanceObserver((list) => {
            for (const task of list.getEntries())
              probe.longTasks.push(task.duration);
          });
          probe.observer.observe({ type: "longtask" });
        }
      });
      const start = Date.now();
      try {
        await deadline(
          $(p, "project-search-submit")
            .click()
            .then(() => searched(p, query, true, expected)),
          20000,
          `4 MiB ${label} search exceeded 20s; synchronous scan may be blocking the UI`,
        );
      } catch (error) {
        console.error(
          JSON.stringify({
            scenario: "project-search-4MiB-repetition",
            label,
            sourceBytes: sourceBytes + 6,
            queryUtf16Units: query.length,
            elapsedMs: Date.now() - start,
            issue:
              "Search did not settle within the finite probe deadline; investigate UI blocking, do not reduce fixture size.",
          }),
        );
        throw error;
      }
      const wallMs = Date.now() - start;
      await p.waitForTimeout(100); // allow heartbeat/longtask delivery after scan
      const timing = await p.evaluate(() => {
        const probe = window.__projectSearchTiming;
        clearInterval(probe.timer);
        probe.observer?.disconnect();
        const result = {
          maxHeartbeatGapMs: probe.maxGapMs,
          heartbeatTicks: probe.ticks,
          longTasksMs: probe.longTasks,
        };
        delete window.__projectSearchTiming;
        return result;
      });
      const record = {
        label,
        sourceBytes: sourceBytes + 6,
        queryUtf16Units: query.length,
        wallMs,
        ...timing,
      };
      measurements.push(record);
      if (expected === "Truncated")
        assert.equal(await $(p, "project-search-result").count(), 100);
      else assert.equal(await $(p, "project-search-result").count(), 0);
      assert.deepEqual(await H.stamp(p), original);
      assert.equal(await p.locator(".dirty-dot").count(), 0);
      assert.equal(
        await $(p, "editor").inputValue(),
        "a".repeat(sourceBytes) + "🙂\n",
      );
    }
    const materiallyBlocked = measurements.some(
      (m) =>
        m.maxHeartbeatGapMs > 250 ||
        m.longTasksMs.some((duration) => duration > 250),
    );
    process.stdout.write(
      JSON.stringify({
        scenario: "project-search-4MiB-repetition",
        materiallyBlocked,
        measurements,
        limitation:
          "Measured representative fixture only, not 64 MiB / 5000-file responsiveness evidence.",
      }) + "\n",
    );
    if (materiallyBlocked)
      process.stdout.write(
        "OBSERVED_UI_BLOCK: representative synchronous scan/render materially blocked the UI; report measurements for lead decision, do not silently shrink input or weaken limits.\n",
      );
    assert.deepEqual(errors, []);
  } finally {
    await ctx.close();
  }
}

(async () => {
  await new Promise((resolve) => proxy.listen(PORT, "127.0.0.1", resolve));
  const browser = await chromium.launch({ headless: true });
  try {
    await deadline(
      (async () => {
        await functional(browser);
        await heldImport(browser, "tree");
        await heldImport(browser, "source-tool");
        await performanceProbe(browser);
      })(),
      180000,
      "project-search browser suite exceeded its 180s resource/time budget",
    );
  } finally {
    await browser.close();
    await new Promise((resolve) => proxy.close(resolve));
  }
})().catch((e) => {
  process.stderr.write((e?.stack || String(e)) + "\n");
  process.exitCode = 1;
});
