// Assignment 5: actual published C# UI + actual Rust WASM. No proposal injection.
const assert = require("node:assert/strict");
const http = require("node:http");
const { chromium } = require("playwright");
const H = require("./review-ui-helpers.cjs");
const { $, reveal } = H;
const UPSTREAM = process.env.APP_URL || "http://static-origin:8080",
 BASE = "http://127.0.0.1:8099";
const proxy = http.createServer((req, res) => {
 const u = new URL(req.url, UPSTREAM);
 const r = http.request(
  u,
  { method: req.method, headers: { ...req.headers, host: u.host } },
  (s) => {
   res.writeHead(s.statusCode, s.headers);
   s.pipe(res);
  },
 );
 r.on("error", () => res.destroy());
 req.pipe(r);
});
async function context(browser) {
 const ctx = await browser.newContext({ serviceWorkers: "block" });
 const p = await ctx.newPage();
 const errors = [],
  external = [];
 p.on("pageerror", (e) => errors.push(String(e)));
 p.on("request", (r) => {
  if (
   !r.url().startsWith(BASE) &&
   !r.url().startsWith("data:") &&
   !r.url().startsWith("blob:")
  )
   external.push(r.url());
 });
 await p.goto(BASE);
 await H.ready(p);
 return { ctx, p, errors, external };
}
async function closeProject(p) {
 await p.getByRole("button", { name: "Close", exact: true }).click();
 await p.locator(".project-dashboard").waitFor();
}
async function addNote(p, name = "notes.txt") {
 await reveal(p, $(p, "new-file-name"));
 await $(p, "new-file-name").fill(name);
 await $(p, "new-file").click();
 await p.locator(".editor-toolbar .source-tab", { hasText: name }).waitFor();
}
// Persistence is polled from Node with explicit awaited immutable-head reads.
// An async page.waitForFunction predicate is not evidence that a commit occurred.
async function savedHead(p, name, previousId, expected, timeout = 6500) {
 const deadline = Date.now() + timeout;
 let saved;
 do {
  saved = await H.head(p, name);
  if (saved.id !== previousId) {
   assert.deepEqual(
    Buffer.from(saved.files.find((f) => f.path === "main.tex").bytes),
    expected,
   );
   return saved;
  }
  await new Promise((resolve) => setTimeout(resolve, 100));
 } while (Date.now() < deadline);
 assert.notEqual(
  saved.id,
  previousId,
  "ordinary editor autosave must advance the actual durable head without Save or acceptance",
 );
}
async function autosave(browser) {
 for (const operation of [
  "control",
  "preview-decline",
  "preview-cancel",
  "preview-open",
  "accept-cancel",
  "accept-reject",
 ]) {
  const { ctx, p, errors } = await context(browser);
  const name = await H.create(p);
  const before = await H.head(p, name);
  const text =
   (await $(p, "editor").inputValue()) + "\n% independent autosave probe🙂\n";
  const expected = Buffer.concat([
   Buffer.from([239, 187, 191]),
   Buffer.from(text),
  ]);
  const originalGeneration = (await H.stamp(p)).generation;
  await $(p, "editor").fill(text);
  await p.waitForFunction(
   (old) =>
    document.querySelector("[data-testid=analysis-panel]")?.dataset
     .generation !== old,
   originalGeneration,
  );
  const generation = (await H.stamp(p)).generation;
  let oldPreview = null,
   oldAccept = null;
  if (operation !== "control") {
   await H.analyze(p);
   oldPreview = await H.preview(p);
   oldAccept = await $(p, "review-accept").elementHandle();
   assert.equal(
    (await H.head(p, name)).id,
    before.id,
    "review inspection must not force-save to create a preview",
   );
   if (operation === "preview-decline") await $(p, "review-decline").click();
   if (operation === "preview-cancel") await $(p, "review-cancel").click();
   if (operation.startsWith("accept-")) {
    await H.holdOwnership(p);
    await $(p, "review-accept").click();
    await H.reached(p);
    if (operation === "accept-cancel") await $(p, "review-cancel").click();
    else {
     await $(p, "analysis-work-type").selectOption("coursework");
     await $(p, "analysis-work-type").selectOption("bachelorQualification");
    }
    await H.releaseOwnership(p);
    await H.idle(p);
   }
  }
  const saved = await savedHead(p, name, before.id, expected);
  assert.notEqual(saved.id, before.id);
  assert.equal(saved.parent, before.id);
  assert.equal(
   (await H.stamp(p)).generation,
   generation,
   "inspection/rejection must not mutate the working generation",
  );
  assert.equal(await $(p, "review-action").count(), 0);
  assert.equal(await $(p, "review-undo").count(), 0);
  assert.ok(
   Buffer.from(saved.files.find((f) => f.path === "main.tex").bytes).includes(
    Buffer.from("10 mm"),
   ),
  );
  assert.ok(
   !Buffer.from(saved.files.find((f) => f.path === "main.tex").bytes).includes(
    Buffer.from("1.27cm"),
   ),
  );
  await p.waitForFunction(
   () =>
    document.querySelector(".workspace-header .status")?.textContent ===
    "Saved.",
  );
  if (operation === "preview-open") {
   await p
    .locator(`[data-testid=review-preview][data-preview-id="${oldPreview}"]`)
    .waitFor({ state: "detached" });
   assert.match(
    await $(p, "review-page-notice").textContent(),
    /no longer current|fresh preview/,
   );
   await oldAccept.evaluate((e) => e.click()); // detached old UI cannot authorize anything
   assert.equal(await $(p, "review-action").count(), 0);
   assert.equal((await H.head(p, name)).id, saved.id);
   await H.analyze(p);
   const fresh = await H.preview(p);
   assert.notEqual(fresh, oldPreview);
   await $(p, "review-decline").click();
  }
  await oldAccept?.dispose();
  assert.deepEqual(errors, []);
  await ctx.close();
  process.stdout.write(
   "PASS R8 ordinary immutable-head/byte autosave without implicit F1: " +
    operation +
    "\n",
  );
 }
}
async function unchangedHead(p, name, id, expected, duration = 1000) {
 const until = Date.now() + duration;
 do {
  const data = await H.head(p, name);
  assert.equal(data.id, id, "cancelled old callback must not commit later");
  assert.deepEqual(
   Buffer.from(data.files.find((f) => f.path === "main.tex").bytes),
   expected,
  );
  await new Promise((resolve) => setTimeout(resolve, 100));
 } while (Date.now() < until);
}
async function autosaveLifetime(browser) {
 for (const departure of ["route", "session"]) {
  const { ctx, p, errors } = await context(browser);
  const name = await H.create(p);
  const before = await H.head(p, name);
  const text =
   (await $(p, "editor").inputValue()) + "\n% pending departure draft🙂\n";
  await $(p, "editor").fill(text);
  await H.analyze(p);
  await H.preview(p);
  // Hold the actual ordinary autosave after its debounce in the browser lock
  // query, not a guessed sleep. Route disposal must cancel it before persistence.
  await H.holdOwnership(p);
  await H.reached(p);
  const link = p.locator(".project-menu a.project-smoke-link");
  await reveal(p, link);
  await link.click();
  if (departure === "route") {
   await H.releaseOwnership(p);
   await unchangedHead(p, name, before.id, H.negative);
   await p.locator('a.nav-link[href="/"]').click();
   await H.ready(p);
   assert.equal(await $(p, "editor").inputValue(), text);
   assert.equal(await $(p, "review-action").count(), 0);
   const next = text + "% new mounted-page input\n";
   await $(p, "editor").fill(next);
   await savedHead(
    p,
    name,
    before.id,
    Buffer.concat([Buffer.from([239, 187, 191]), Buffer.from(next)]),
   );
  } else {
   // Keep the old query held across route return, normal close/flush and a NEW
   // project/session. Releasing its late result must not save the new session.
   await p.locator('a.nav-link[href="/"]').click();
   await H.ready(p);
   await closeProject(p);
   const newName = await H.create(p);
   const newHead = await H.head(p, newName);
   await H.releaseOwnership(p);
   await unchangedHead(p, newName, newHead.id, H.negative);
   await H.assertBytes(
    p,
    name,
    Buffer.concat([Buffer.from([239, 187, 191]), Buffer.from(text)]),
   );
   assert.equal(await $(p, "review-action").count(), 0);
  }
  assert.deepEqual(errors, []);
  await ctx.close();
  process.stdout.write(
   "PASS R9 reached autosave cancellation cannot cross page/project lifetime: " +
    departure +
    "\n",
  );
 }
}
async function basic(browser) {
 const { ctx, p, errors, external } = await context(browser);
 const name = await H.create(p);
 const before = await H.assertBytes(p, name, H.negative);
 const initial = await H.stamp(p);
 assert.equal(await $(p, "review-accept").count(), 0);
 assert.equal(await $(p, "review-action").count(), 0);
 await H.analyze(p);
 assert.deepEqual(await H.head(p, name), before);
 assert.deepEqual(await H.stamp(p), initial);
 await H.declineNoChange(p, name);
 await H.preview(p);
 assert.match(await $(p, "review-path").innerText(), /main.tex/);
 assert.match(
  await $(p, "review-byte-policy").innerText(),
  /77 bytes; after 78 bytes.*True → True/,
 );
 await reveal(p, $(p, "review-before"));
 assert.match(await $(p, "review-before").innerText(), /⟦BOM⟧Привіт🙂⟦CR⟧⟦LF⟧/);
 await H.accept(p);
 const applied = await H.assertBytes(p, name, H.after);
 assert.equal(applied.parent, before.id);
 assert.equal(
  Number((await H.stamp(p)).generation),
  Number(initial.generation) + 1,
 );
 assert.match(
  await $(p, "review-persistence").innerText(),
  /^Reviewed change saved/,
 );
 assert.match(await $(p, "review-workspace-state").innerText(), /clean/);
 await H.analyze(p);
 assert.equal(await $(p, "preview-fix").count(), 0);
 assert.equal(
  await p
   .locator("[data-testid=knu-finding][data-rule-id=KNU-SRC-HEADING-DOT-001]")
   .count(),
  1,
 );
 // App-scoped recovery survives route remount even though the old report doesn't.
 const actionId = await $(p, "review-action").getAttribute("data-action-id");
 const link = p.locator(".project-menu a.project-smoke-link");
 await reveal(p, link);
 await link.click();
 await p.locator('a.nav-link[href="/"]').click();
 await $(p, "review-action").waitFor();
 assert.equal(
  await $(p, "review-action").getAttribute("data-action-id"),
  actionId,
 );
 await H.undo(p);
 const undone = await H.assertBytes(p, name, H.negative);
 assert.equal(undone.parent, applied.id);
 assert.notEqual(undone.id, before.id);
 assert.equal(await $(p, "review-undo").count(), 0);
 assert.match(
  await $(p, "review-undo-consumed").innerText(),
  /cannot be repeated/,
 );
 await closeProject(p);
 await H.open(p, name);
 assert.equal(
  await $(p, "review-action").count(),
  0,
  "a reopened session has history, not old in-memory undo",
 );
 assert.deepEqual(errors, []);
 assert.deepEqual(external, []);
 await ctx.close();
 process.stdout.write(
  "PASS R1 native F1 preview/decline/accept/save/reanalysis, exact bytes/new undo revision, remount and session lifetime\n",
 );
}
async function staleness(browser) {
 for (const change of [
  "affected-edit",
  "other-edit",
  "rename",
  "work",
  "profile",
  "reanalyze",
  "project",
 ]) {
  const { ctx, p, errors } = await context(browser);
  const name = await H.create(p);
  if (change === "other-edit") {
   await addNote(p);
   await $(p, "editor").fill("notes");
   await H.save(p);
   await H.analyze(p);
  }
  const id = await H.preview(p);
  const before = await H.head(p, name);
  if (change === "affected-edit") {
   await p
    .locator("[data-testid=select-file]", { hasText: "main.tex" })
    .click();
   await $(p, "editor").fill("new affected content🙂");
  }
  if (change === "other-edit")
   await $(p, "editor").fill("new unrelated content🙂");
  if (change === "rename") {
   await reveal(p, $(p, "rename-input"));
   await $(p, "rename-input").fill("renamed.tex");
   await $(p, "rename").click();
  }
  if (change === "work") {
   await $(p, "analysis-work-type").selectOption("coursework");
   await $(p, "analysis-work-type").selectOption("bachelorQualification");
  }
  if (change === "profile") {
   await $(p, "analysis-profile").selectOption("engine-structural");
   await H.selectAnalysisProfile(p, "knu-csc-2025-source");
  }
  if (change === "reanalyze") await H.analyze(p);
  if (change === "project") {
   await closeProject(p);
   await H.create(p);
  }
  await p
   .locator(`[data-testid=review-preview][data-preview-id="${id}"]`)
   .waitFor({ state: "detached" });
  assert.equal(await $(p, "review-action").count(), 0);
  assert.equal(await $(p, "review-accept").count(), 0);
  if (["work", "profile", "reanalyze"].includes(change))
   assert.deepEqual(await H.head(p, name), before);
  if (change === "affected-edit")
   assert.equal(await $(p, "editor").inputValue(), "new affected content🙂");
  assert.deepEqual(errors, []);
  await ctx.close();
  process.stdout.write(
   "PASS R2 stale preview cannot revive/apply: " + change + "\n",
  );
 }
}
async function held(browser) {
 for (const change of [
  "double",
  "cancel",
  "work",
  "edit",
  "route",
  "project",
 ]) {
  const { ctx, p, errors } = await context(browser);
  const name = await H.create(p);
  await H.preview(p);
  const before = await H.head(p, name);
  const start = await H.stamp(p);
  await H.holdOwnership(p);
  await $(p, "review-accept").evaluate((e) => {
   e.click();
   if (e.isConnected) e.click();
  });
  await H.reached(p);
  assert.equal(await $(p, "review-accept").isDisabled(), true);
  if (change === "cancel") await $(p, "review-cancel").click();
  if (change === "work") {
   await $(p, "analysis-work-type").selectOption("coursework");
   await $(p, "analysis-work-type").selectOption("bachelorQualification");
  }
  if (change === "edit") await $(p, "editor").fill("edited during validation");
  if (change === "route") {
   const link = p.locator(".project-menu a.project-smoke-link");
   await reveal(p, link);
   await link.click();
   await p.locator('a.nav-link[href="/"]').click();
  }
  if (change === "project") {
   await closeProject(p);
   await H.create(p);
  }
  await H.releaseOwnership(p);
  await H.idle(p);
  if (change === "double") {
   await H.action(p);
   const data = await H.assertBytes(p, name, H.after);
   assert.equal(data.parent, before.id);
   assert.equal(
    Number((await H.stamp(p)).generation),
    Number(start.generation) + 1,
   );
  } else {
   assert.equal(await $(p, "review-action").count(), 0);
   if (change !== "edit") assert.deepEqual(await H.head(p, name), before);
  }
  assert.deepEqual(errors, []);
  await ctx.close();
  process.stdout.write(
   "PASS R3 real ownership reached/release barrier: " + change + "\n",
  );
 }
}
async function readonly(browser) {
 const { ctx, p, errors } = await context(browser);
 const name = await H.create(p);
 const reader = await ctx.newPage();
 await reader.goto(BASE);
 await H.ready(reader);
 await H.open(reader, name);
 await H.selectKnu(reader);
 await H.preview(reader);
 assert.equal(await $(reader, "review-accept").isDisabled(), true);
 assert.match(await $(reader, "review-readonly").innerText(), /writer lock/);
 assert.equal(await $(reader, "save").isDisabled(), true);
 assert.equal(await $(reader, "review-undo").count(), 0);
 await $(reader, "review-accept").evaluate((e) => e.click());
 await H.assertBytes(p, name, H.negative);
 assert.equal(await $(reader, "review-action").count(), 0);
 await reader.close();
 assert.deepEqual(errors, []);
 await ctx.close();
 process.stdout.write(
  "PASS R4 actual read-only second tab previews but cannot accept/save/undo\n",
 );
}
async function saveFailures(browser) {
 const { ctx, p, errors } = await context(browser);
 const name = await H.create(p);
 await H.preview(p);
 const before = await H.head(p, name);
 try {
  await H.faultSave(p, H.after);
  await $(p, "review-accept").click();
  await H.action(p, false, false);
  const gen = (await H.stamp(p)).generation;
  assert.equal((await H.head(p, name)).id, before.id);
  assert.match(
   await $(p, "review-persistence").innerText(),
   /in memory; not yet saved/,
  );
  assert.match(
   await $(p, "review-recovery-guidance").innerText(),
   /requires a successful save/,
  );
  assert.ok((await p.evaluate(() => window.__reviewDigest.hits)) > 0);
  assert.equal(await $(p, "review-undo").isEnabled(), true);
  await H.restoreDigest(p);
  await $(p, "review-retry").click();
  await H.action(p);
  assert.equal((await H.stamp(p)).generation, gen);
  const applied = await H.assertBytes(p, name, H.after);
  await H.faultSave(p, H.negative);
  await $(p, "review-undo").click();
  await H.action(p, true, false);
  const undoGen = (await H.stamp(p)).generation;
  assert.equal((await H.head(p, name)).id, applied.id);
  assert.equal(await $(p, "review-undo").count(), 0);
  assert.match(
   await $(p, "review-persistence").innerText(),
   /undo applied in memory; not yet saved/,
  );
  await H.restoreDigest(p);
  await $(p, "review-retry").click();
  await H.action(p, true);
  assert.equal((await H.stamp(p)).generation, undoGen);
  const restored = await H.assertBytes(p, name, H.negative);
  assert.equal(restored.parent, applied.id);
 } finally {
  await H.restoreDigest(p);
 }
 assert.deepEqual(errors, []);
 await ctx.close();
 process.stdout.write(
  "PASS R5 labeled synthetic apply-save/undo-save faults, exact recovery, retry does not reapply/invert\n",
 );
}
async function conflictsAndUnrelated(browser) {
 const { ctx, p, errors } = await context(browser);
 const name = await H.create(p);
 await H.preview(p);
 await H.accept(p);
 await addNote(p);
 await $(p, "editor").fill("unrelated latest🙂");
 await p.waitForFunction(() =>
  document
   .querySelector("[data-testid=review-workspace-state]")
   ?.textContent.includes("unsaved changes"),
 );
 assert.match(
  await $(p, "review-persistence").innerText(),
  /^Reviewed change saved/,
 );
 await H.save(p);
 await reveal(p, $(p, "rename-input"));
 await $(p, "rename-input").fill("renamed.txt");
 await $(p, "rename").click();
 await H.save(p);
 await H.undo(p);
 const saved = await H.assertBytes(p, name, H.negative);
 assert.deepEqual(
  Buffer.from(saved.files.find((f) => f.path === "renamed.txt").bytes),
  Buffer.from("unrelated latest🙂"),
 );
 await H.analyze(p);
 await H.preview(p);
 await H.accept(p);
 await p.locator("[data-testid=select-file]", { hasText: "main.tex" }).click();
 await reveal(p, $(p, "rename-input"));
 await $(p, "rename-input").fill("affected.tex");
 await $(p, "rename").click();
 await H.save(p);
 const before = await H.head(p, name);
 await $(p, "review-undo").click();
 await H.idle(p);
 assert.equal(
  await $(p, "review-notice").getAttribute("data-code"),
  "Conflict",
 );
 assert.deepEqual(await H.head(p, name), before);
 assert.equal(await $(p, "review-undo").isEnabled(), true);
 await reveal(p, $(p, "rename-input"));
 await $(p, "rename-input").fill("main.tex");
 await $(p, "rename").click();
 await H.save(p);
 await H.undo(p);
 await H.assertBytes(p, name, H.negative);
 await H.analyze(p);
 await H.preview(p);
 await H.accept(p);
 await $(p, "editor").fill("affected newer authored text🙂");
 await H.save(p);
 const later = await H.head(p, name);
 await $(p, "review-undo").click();
 await H.idle(p);
 assert.equal(
  await $(p, "review-notice").getAttribute("data-code"),
  "Conflict",
 );
 assert.deepEqual(await H.head(p, name), later);
 assert.equal(
  await $(p, "editor").inputValue(),
  "affected newer authored text🙂",
 );
 assert.deepEqual(errors, []);
 await ctx.close();
 process.stdout.write(
  "PASS R6 unrelated text/tree preserved, affected rename/content conflicts retain recovery without overwrite\n",
 );
}
async function escaping(browser) {
 const { ctx, p, errors } = await context(browser);
 const literal = "&lt;img onerror=alert(1)&gt;.tex";
 const text = Buffer.from(
  "\\setlength{\\parindent}{ 10 mm }\n\\section{<img src=x onerror=window.reviewXss=1>}\n",
 );
 const name = await H.create(p, text, literal);
 await H.preview(p);
 assert.equal(await $(p, "review-path").innerText(), literal);
 assert.match(await $(p, "review-before").textContent(), /<img src=x/);
 assert.equal(
  await p
   .locator(".review-panel img,.review-panel script,.knu-report img")
   .count(),
  0,
 );
 assert.equal(await p.evaluate(() => window.reviewXss), undefined);
 await $(p, "review-decline").click();
 await H.assertBytes(p, name, text, literal);
 assert.deepEqual(errors, []);
 await ctx.close();
 process.stdout.write(
  "PASS R7 imported HTML-looking literal source/filename stays escaped; no nodes/events execute\n",
 );
}
(async () => {
 await new Promise((r) => proxy.listen(8099, "127.0.0.1", r));
 const browser = await chromium.launch({ headless: true });
 try {
  process.stdout.write(
   "Review UI real Chromium " +
    browser.version() +
    "; target " +
    UPSTREAM +
    "\n",
  );
  await autosave(browser);
  await autosaveLifetime(browser);
  await basic(browser);
  await staleness(browser);
  await held(browser);
  await readonly(browser);
  await saveFailures(browser);
  await conflictsAndUnrelated(browser);
  await escaping(browser);
 } finally {
  await browser.close();
  await new Promise((r) => proxy.close(r));
 }
})().catch((e) => {
 process.stderr.write((e?.stack || String(e)) + "\n");
 process.exitCode = 1;
});
