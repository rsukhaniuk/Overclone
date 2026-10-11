// M2b 3b: REAL Rust WASM happy paths. All instrumentation below is test-only.
// Holds delay original MessageEvents/JSON strings without reserializing integers.
const assert = require("node:assert/strict");
const http = require("node:http");
const { chromium } = require("playwright");
const { fixtures, hashes } = require("./knu-fixtures.cjs");
const UPSTREAM = process.env.APP_URL || "http://app:8080";
const BASE = "http://127.0.0.1:8097";
const $ = (p, id) => p.locator(`[data-testid="${id}"]`);
const proxy = http.createServer((req, res) => {
 const u = new URL(req.url, UPSTREAM);
 const r = http.request(
  u,
  { method: req.method, headers: { ...req.headers, host: u.host } },
  (v) => {
   res.writeHead(v.statusCode, v.headers);
   v.pipe(res);
  },
 );
 r.on("error", () => res.destroy());
 req.pipe(r);
});
const instrumentation = () => {
 const Native = window.Worker;
 const state = (window.__knuTest = {
  mode: "",
  reached: false,
  held: null,
  release: null,
  seen: [],
 });
 window.Worker = class extends Native {
  constructor(url, options) {
   super(url, options);
   this.rust = String(url).includes("rust-worker.js");
  }
  set onmessage(handler) {
   super.onmessage = (event) => {
    if (!this.rust || !event.data?.canonical) {
     handler(event);
     return;
    }
    const raw = event.data.responseJson;
    let value;
    try {
     value = JSON.parse(raw);
    } catch {
     handler(event);
     return;
    }
    const knu =
     value.status === "completed" &&
     value.payload?.profile?.id === "knu-csc-2025-source";
    const init = value.status === "completed" && value.payload?.engineVersion;
    if (knu) state.seen.push({ snapshotId: value.payload.snapshotId, raw });
    if (state.mode === "hold" && knu) {
     state.mode = "";
     state.reached = true;
     state.held = { raw, snapshotId: value.payload.snapshotId };
     state.release = () => {
      if (event.data.responseJson !== raw)
       throw new Error("native JSON mutated");
      handler(event);
      state.release = null;
     };
     return;
    }
    // Labeled SYNTHETIC malformed/initialization faults. Healthy/held paths
    // above/below always forward the original actual Rust event unchanged.
    if (
     (state.mode === "malformed" && knu) ||
     (state.mode === "badInit" && init)
    ) {
     state.mode = "";
     state.reached = true;
     handler(
      new MessageEvent("message", {
       data: {
        ...event.data,
        responseJson: JSON.stringify({
         protocolVersion: 1,
         requestId: event.data.requestId,
         status: "completed",
         payload: {},
        }),
       },
      }),
     );
     return;
    }
    handler(event);
   };
  }
 };
};
async function ready(p) {
 await $(p, "analysis-profile")
  .or(p.locator("#new-project"))
  .first()
  .waitFor({ timeout: 60000 });
}
async function save(p) {
 await $(p, "save").click();
 await p.waitForFunction(
  () =>
   document
    .querySelector(".workspace-header .status")
    ?.textContent.startsWith("Saved"),
  null,
  { timeout: 20000 },
 );
}
async function analyze(p) {
 await $(p, "analyze-now").click();
 await p.waitForFunction(
  () =>
   document.querySelector("[data-testid=analysis-status]")?.textContent ===
   "Analysis complete.",
  null,
  { timeout: 30000 },
 );
}
async function knu(p) {
 await $(p, "analysis-profile").selectOption("knu-csc-2025-source");
 await analyze(p);
}
async function head(p, name) {
 return p.evaluate(async (name) => {
  const m = await import("./js/workspace-storage.js");
  const project = (await m.listProjects()).find((x) => x.name === name);
  const r = await m.getRevision(project.headRevisionId);
  return {
   id: r.id,
   files: await Promise.all(
    r.files.map(async (f) => ({
     path: f.relativePath,
     bytes: Array.from(await m.readContent(f.contentHash)),
    })),
   ),
  };
 }, name);
}
async function create(p, bytes = fixtures.negative, file = "main.tex") {
 const name = "KNU-" + Date.now() + "-" + Math.random().toString(36).slice(2);
 await p.locator("#new-project").fill(name);
 await p.getByRole("button", { name: "Create project", exact: true }).click();
 await $(p, "import-input").setInputFiles({
  name: file,
  mimeType: "text/plain",
  buffer: bytes,
 });
 await p.locator(".editor-toolbar label", { hasText: file }).waitFor();
 await save(p);
 return name;
}
async function replace(p, bytes) {
 await $(p, "editor").fill(bytes.toString("utf8").replace(/^\uFEFF/, ""));
 await save(p);
 await analyze(p);
}
async function context(browser) {
 const ctx = await browser.newContext({ serviceWorkers: "block" });
 await ctx.addInitScript(instrumentation);
 const p = await ctx.newPage();
 const errors = [];
 p.on("pageerror", (e) => errors.push(String(e)));
 const external = [];
 p.on("request", (r) => {
  if (
   !r.url().startsWith(BASE) &&
   !r.url().startsWith("data:") &&
   !r.url().startsWith("blob:")
  )
   external.push(r.url());
 });
 await p.goto(BASE);
 await ready(p);
 return { ctx, p, errors, external };
}
async function assertRules(p, outcome) {
 await $(p, "knu-report").waitFor();
 assert.equal(await $(p, "knu-report").getAttribute("data-outcome"), outcome);
 assert.equal(await $(p, "knu-rule").count(), 2);
 assert.match(
  await $(p, "knu-citation").first().innerText(),
  /§3.1; printed pages 18; PDF pages 18/,
 );
 assert.match(
  await $(p, "knu-citation").last().innerText(),
  /§3.2; printed pages 18, 19/,
 );
 assert.match(
  await $(p, "knu-scope").innerText(),
  /supported observed literals/,
 );
 assert.match(
  await $(p, "analysis-unavailable").innerText(),
  /PDF.*unavailable/,
 );
 assert.equal(
  await p
   .getByRole("button", {
    name: /accept reviewed change|undo reviewed change/i,
   })
   .count(),
  0,
 );
 // Assignment 5 adds canonical preview, not automatic mutation. No accept/undo
 // action exists before an explicit preview/acceptance; only F1 may be offered.
 const previews = p.locator("[data-testid=preview-fix]");
 assert.ok((await previews.count()) <= 1);
 if (await previews.count())
  assert.equal(
   await previews.getAttribute("data-fix-id"),
   "KNU-FIX-PARINDENT-LITERAL-001",
  );
 if (outcome !== "Fail") assert.equal(await previews.count(), 0);
}
async function clickFinding(p, rule, expected, sourceBytes) {
 const finding = p
  .locator(`[data-testid=knu-finding][data-rule-id="${rule}"]`)
  .first();
 const start = Number(await finding.getAttribute("data-start-byte")),
  end = Number(await finding.getAttribute("data-end-byte"));
 assert.equal(sourceBytes.subarray(start, end).toString("utf8"), expected);
 const normalizedPrefix = sourceBytes
  .subarray(0, start)
  .toString("utf8")
  .replace(/^\uFEFF/, "")
  .replace(/\r\n?/g, "\n");
 await finding.locator("[data-testid=knu-finding-link]").click();
 await p.waitForFunction(
  ({ a, b, text }) => {
   const e = document.querySelector("[data-testid=editor]");
   return (
    e.selectionStart === a &&
    e.selectionEnd === b &&
    e.value.substring(a, b) === text
   );
  },
  {
   a: normalizedPrefix.length,
   b: normalizedPrefix.length + expected.length,
   text: expected,
  },
 );
}
async function arm(p, mode) {
 await p.evaluate((mode) => {
  window.__knuTest.mode = mode;
  window.__knuTest.reached = false;
 }, mode);
}
async function held(p) {
 await p.waitForFunction(() => window.__knuTest.reached);
 return p.evaluate(() => window.__knuTest.held);
}
async function release(p) {
 await p.evaluate(() => window.__knuTest.release?.());
}
async function healthy(browser) {
 const { ctx, p, errors, external } = await context(browser);
 const name = await create(p);
 assert.equal(await $(p, "analysis-profile").inputValue(), "engine-structural");
 assert.equal(await $(p, "analysis-work-type").count(), 0);
 const before = await head(p, name);
 await knu(p);
 await assertRules(p, "Fail");
 assert.equal(await $(p, "analysis-finding").count(), 0);
 assert.equal(await $(p, "knu-finding").count(), 2);
 assert.match(await $(p, "engine-outcome").innerText(), /Pass/);
 assert.match(await $(p, "knu-actual").first().innerText(), /10\/1 mm/);
 for (const work of [
  "unspecified",
  "coursework",
  "bachelorQualification",
  "masterQualification",
 ]) {
  await $(p, "analysis-work-type").selectOption(work);
  await analyze(p);
  await assertRules(p, "Fail");
  assert.equal(await $(p, "analysis-work-type").inputValue(), work);
  assert.ok((await $(p, "knu-work-type").innerText()).length > 12);
 }
 await clickFinding(p, "KNU-SRC-PARINDENT-001", "10 mm", fixtures.negative);
 await clickFinding(p, "KNU-SRC-HEADING-DOT-001", ".", fixtures.negative);
 assert.deepEqual(
  await head(p, name),
  before,
  "selection/configuration must not edit/save imported bytes",
 );
 await replace(p, fixtures.positive);
 await assertRules(p, "Pass");
 assert.equal(await $(p, "knu-finding").count(), 0);
 await replace(p, fixtures.uncertain);
 await assertRules(p, "Indeterminate");
 assert.equal(await $(p, "knu-finding").count(), 2);
 assert.match(
  await $(p, "knu-coverage").innerText(),
  /Includes are not resolved/,
 );
 await replace(p, fixtures.unavailable);
 await assertRules(p, "Unavailable");
 assert.equal(await $(p, "knu-missing").count(), 2);
 await replace(p, fixtures.mixed);
 await assertRules(p, "Indeterminate");
 assert.equal(await $(p, "analysis-finding").count(), 1);
 assert.equal(await $(p, "knu-finding").count(), 2);
 assert.match(await $(p, "engine-outcome").innerText(), /Fail/);
 await p.getByRole("button", { name: "Close", exact: true }).click();
 await create(p, fixtures.escape, "literal&name.tex");
 await knu(p);
 assert.match(await $(p, "knu-excerpt").last().innerText(), /<img src=x/);
 assert.match(await $(p, "knu-report").innerText(), /literal&name.tex/);
 assert.equal(await p.locator(".knu-report img,.knu-report script").count(), 0);
 assert.equal(await p.evaluate(() => window.__knuXss), undefined);
 assert.deepEqual(errors, []);
 assert.deepEqual(external, []);
 await ctx.close();
 console.log(
  "PASS K1 real WASM: all work types, four outcomes, mixed origins, exact Unicode/BOM/newline navigation, inert fixes and escaping",
 );
}
async function races(browser) {
 for (const change of [
  "cancel",
  "work",
  "profile",
  "edit",
  "project",
  "route",
 ]) {
  const { ctx, p, errors } = await context(browser);
  await create(p);
  await knu(p);
  await arm(p, "hold");
  await $(p, "analyze-now").click();
  const old = await held(p);
  assert.ok(
   old.raw.includes("knu-csc-2025-source"),
   "hold is an actual native KNU result",
  );
  if (change === "cancel") await $(p, "cancel-analysis").click();
  if (change === "work") {
   await $(p, "analysis-work-type").selectOption("coursework");
   await $(p, "analysis-work-type").selectOption("unspecified");
  }
  if (change === "profile") {
   await $(p, "analysis-profile").selectOption("engine-structural");
   await $(p, "analysis-profile").selectOption("knu-csc-2025-source");
  }
  if (change === "edit") {
   await $(p, "editor").fill(fixtures.positive.toString());
   await save(p);
  }
  if (change === "project") {
   await p.getByRole("button", { name: "Close", exact: true }).click();
   await create(p, fixtures.positive);
  }
  if (change === "route") {
   await p.locator('a.nav-link[href="/smoke"]').click();
   await p.locator('a.nav-link[href="/"]').click();
   await $(p, "analysis-profile").waitFor();
  }
  await release(p);
  await analyze(p);
  assert.notEqual(
   await $(p, "knu-report").getAttribute("data-snapshot-id"),
   old.snapshotId,
  );
  if (change === "edit" || change === "project") await assertRules(p, "Pass");
  else await assertRules(p, "Fail");
  assert.deepEqual(errors, []);
  await ctx.close();
  console.log(
   "PASS K2 native-result hold, stale rejection + same-page real recovery:",
   change,
  );
 }
}
async function navigationRaces(browser) {
 for (const change of [
  "cancel",
  "work",
  "profile",
  "edit",
  "project",
  "route",
 ]) {
  const { ctx, p, errors } = await context(browser);
  const name = await create(p);
  await knu(p);
  let reached, unblock;
  const r = new Promise((resolve) => (reached = resolve)),
   gate = new Promise((resolve) => (unblock = resolve));
  await p.route("**/js/editor-select.js", async (route) => {
   reached();
   await gate;
   await route.continue().catch(() => {});
  });
  await $(p, "knu-finding-link").last().click();
  await Promise.race([
   r,
   new Promise((_, reject) =>
    setTimeout(() => reject(new Error("held import not reached")), 10000),
   ),
  ]);
  if (change === "cancel") await $(p, "cancel-analysis").click();
  if (change === "work") {
   await $(p, "analysis-work-type").selectOption("coursework");
   await $(p, "analysis-work-type").selectOption("unspecified");
  }
  if (change === "profile") {
   await $(p, "analysis-profile").selectOption("engine-structural");
   await $(p, "analysis-profile").selectOption("knu-csc-2025-source");
  }
  if (change === "edit")
   await $(p, "editor").fill("edited while navigation held🙂");
  if (change === "project") {
   await p.getByRole("button", { name: "Close", exact: true }).click();
   await p
    .locator("[data-testid=project-row]", { hasText: name })
    .locator("[data-testid=open-project]")
    .click();
  }
  if (change === "route") {
   await p.locator('a.nav-link[href="/smoke"]').click();
   await p.locator('a.nav-link[href="/"]').click();
  }
  unblock();
  await p.unroute("**/js/editor-select.js");
  if (change === "edit") await save(p);
  await analyze(p);
  const selected = await $(p, "editor").evaluate((e) =>
   e.value.substring(e.selectionStart, e.selectionEnd),
  );
  assert.notEqual(selected, ".");
  if (change !== "edit")
   await clickFinding(p, "KNU-SRC-HEADING-DOT-001", ".", fixtures.negative);
  assert.deepEqual(errors, []);
  await ctx.close();
  console.log("PASS K3 held navigation:", change);
 }
}
async function faultsAndReadonly(browser) {
 const { ctx, p, errors } = await context(browser);
 const name = await create(p);
 await knu(p);
 const reader = await ctx.newPage();
 await reader.goto(BASE);
 await ready(reader);
 await reader
  .locator("[data-testid=project-row]", { hasText: name })
  .locator("[data-testid=open-project]")
  .click();
 await knu(reader);
 assert.equal(await $(reader, "editor").isDisabled(), true);
 await assertRules(reader, "Fail");
 assert.equal(await $(reader, "save").isDisabled(), true);
 const beforeSelection = await $(reader, "editor").evaluate((e) => [
  e.selectionStart,
  e.selectionEnd,
  e.value,
 ]);
 await $(reader, "knu-finding-link").last().click();
 await $(reader, "nav-notice").waitFor();
 assert.deepEqual(
  await $(reader, "editor").evaluate((e) => [
   e.selectionStart,
   e.selectionEnd,
   e.value,
  ]),
  beforeSelection,
  "disabled read-only editor rejects selection without mutation",
 );
 await reader.close();
 const before = await head(p, name);
 await p.evaluate(() => {
  window.__realDigest = SubtleCrypto.prototype.digest;
  SubtleCrypto.prototype.digest = () =>
   Promise.reject(Error("SYNTHETIC KNU save failure"));
 });
 await $(p, "editor").fill(fixtures.negative.toString().replace(/^\uFEFF/, ""));
 await $(p, "save").click();
 await p.waitForFunction(() =>
  document
   .querySelector(".workspace-header .status")
   .textContent.startsWith("Save failed"),
 );
 await analyze(p);
 await assertRules(p, "Fail");
 assert.equal((await head(p, name)).id, before.id);
 assert.match(
  await p.locator(".workspace-header .status").innerText(),
  /Save failed/,
 );
 await p.evaluate(() => (SubtleCrypto.prototype.digest = window.__realDigest));
 await save(p);
 for (const mode of ["malformed", "badInit"]) {
  if (mode === "badInit")
   await p.evaluate(async () => {
    const b = await import("./js/rust-bridge.js");
    await b.dispose();
   });
  await arm(p, mode);
  await $(p, "analyze-now").click();
  await p.waitForFunction(() =>
   document
    .querySelector("[data-testid=analysis-status]")
    .textContent.startsWith("Analysis failed"),
  );
  assert.equal(await $(p, "knu-finding").count(), 0);
  assert.equal(await $(p, "editor").isDisabled(), false);
  await $(p, "editor").fill(fixtures.positive.toString());
  await save(p);
  await analyze(p);
  await assertRules(p, "Pass");
 }
 assert.deepEqual(errors, []);
 await ctx.close();
 console.log(
  "PASS K4 readonly, failed-save analysis, labeled synthetic faults + real recovery, editing/saving independent",
 );
}
(async () => {
 await new Promise((r) => proxy.listen(8097, "127.0.0.1", r));
 const browser = await chromium.launch({ headless: true });
 try {
  console.log(
   "Browser:",
   browser.version(),
   "Synthetic fixture SHA-256:",
   JSON.stringify(hashes),
  );
  await healthy(browser);
  await races(browser);
  await navigationRaces(browser);
  await faultsAndReadonly(browser);
 } finally {
  await browser.close();
  await new Promise((r) => proxy.close(r));
 }
})().catch((e) => {
 console.error(e);
 process.exitCode = 1;
});
