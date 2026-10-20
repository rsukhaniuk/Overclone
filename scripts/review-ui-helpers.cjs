// Browser-test helpers only. All decisions/evaluation/application stay in C#/Rust.
const assert = require("node:assert/strict");
const { fixtures } = require("./knu-fixtures.cjs");
const $ = (p, id) => p.locator(`[data-testid="${id}"]`);
const after = Buffer.from(
 fixtures.negative.toString("utf8").replace("10 mm", "1.27cm"),
);
async function ready(p) {
 await p
  .locator(".project-dashboard,[data-testid=new-file-name]")
  .first()
  .waitFor({ timeout: 60000 });
}
async function reveal(p, target) {
 // Open real disclosure controls from the outside inward; never inject UI state.
 await target.waitFor({ state: "attached" });
 for (const detail of await target.locator("xpath=ancestor::details").all()) {
  if ((await detail.getAttribute("open")) === null)
   await detail.locator(":scope > summary").click();
 }
}
async function idle(p) {
 await p.waitForFunction(
  () =>
   document
    .querySelector("[data-testid=review-panel]")
    ?.getAttribute("data-busy")
    ?.toLowerCase() === "false",
  null,
  { timeout: 30000 },
 );
}
async function save(p) {
 await $(p, "save").click();
 await p.waitForFunction(
  () =>
   document.querySelector(".workspace-header .status")?.textContent ===
   "Saved.",
  null,
  { timeout: 30000 },
 );
}
async function analyze(p) {
 await reveal(p, $(p, "analyze-now"));
 await $(p, "analyze-now").click();
 await p.waitForFunction(
  () =>
   document.querySelector("[data-testid=analysis-status]")?.textContent ===
   "Analysis complete.",
  null,
  { timeout: 30000 },
 );
}
async function selectKnu(p) {
 await reveal(p, $(p, "analysis-profile"));
 await $(p, "analysis-profile").selectOption("knu-csc-2025-source");
 await $(p, "analysis-work-type").selectOption("bachelorQualification");
 await analyze(p);
}
async function create(p, bytes = fixtures.negative, file = "main.tex") {
 const name =
  "Review-" + Date.now() + "-" + Math.random().toString(36).slice(2);
 await reveal(p, p.locator("#new-project"));
 await p.locator("#new-project").fill(name);
 await p.getByRole("button", { name: "Create project", exact: true }).click();
 await $(p, "import-input").setInputFiles({
  name: file,
  mimeType: "text/plain",
  buffer: bytes,
 });
 await p.locator(".editor-toolbar label", { hasText: file }).waitFor();
 await save(p);
 await selectKnu(p);
 return name;
}
async function open(p, name) {
 await p
  .locator("[data-testid=project-row]", { hasText: name })
  .locator("[data-testid=open-project]")
  .click();
 await reveal(p, $(p, "analysis-profile"));
 await $(p, "analysis-profile").waitFor();
}
async function head(p, name) {
 return p.evaluate(async (name) => {
  const m = await import("./js/workspace-storage.js");
  const project = (await m.listProjects()).find((x) => x.name === name);
  const r = await m.getRevision(project.headRevisionId);
  return {
   id: r.id,
   parent: r.parentRevisionId,
   files: await Promise.all(
    r.files.map(async (f) => ({
     id: f.id,
     path: f.relativePath,
     bytes: Array.from(await m.readContent(f.contentHash)),
    })),
   ),
  };
 }, name);
}
async function stamp(p) {
 return $(p, "analysis-panel").evaluate((e) => ({
  generation: e.dataset.generation,
  session: e.dataset.session,
  head: e.dataset.head,
 }));
}
async function preview(p) {
 await $(p, "preview-fix").click();
 await $(p, "review-preview").waitFor();
 await idle(p);
 return $(p, "review-preview").getAttribute("data-preview-id");
}
async function action(p, undone = false, persisted = true) {
 await p.waitForFunction(
  ({ undone, persisted }) => {
   const a = document.querySelector("[data-testid=review-action]"),
    panel = document.querySelector("[data-testid=review-panel]");
   return (
    a?.getAttribute("data-undone")?.toLowerCase() === String(undone) &&
    a?.getAttribute("data-persisted")?.toLowerCase() === String(persisted) &&
    panel?.getAttribute("data-busy")?.toLowerCase() === "false"
   );
  },
  { undone, persisted },
  { timeout: 30000 },
 );
}
async function accept(p) {
 await $(p, "review-accept").click();
 await action(p);
}
async function undo(p) {
 await $(p, "review-undo").click();
 await action(p, true);
}
async function assertBytes(p, name, expected, path = "main.tex") {
 const data = await head(p, name);
 assert.deepEqual(
  Buffer.from(data.files.find((f) => f.path === path).bytes),
  expected,
 );
 return data;
}
async function declineNoChange(p, name) {
 const before = await head(p, name),
  s = await stamp(p);
 await preview(p);
 assert.equal((await $(p, "review-expected").innerText()).trim(), "10 mm");
 assert.equal((await $(p, "review-replacement").innerText()).trim(), "1.27cm");
 assert.match(
  await $(p, "review-citation").innerText(),
  /§3.1; printed pages 18; PDF pages 18/,
 );
 await $(p, "review-decline").click();
 await $(p, "review-preview").waitFor({ state: "detached" });
 assert.deepEqual(await head(p, name), before);
 assert.deepEqual(await stamp(p), s);
}
// SYNTHETIC persistence fault: only hashing the known intended saved image fails.
// Pre-apply verification of the different current image remains real/successful.
async function faultSave(p, bytes) {
 await p.evaluate((bytes) => {
  const old = SubtleCrypto.prototype.digest;
  window.__reviewDigest = { old, hits: 0 };
  SubtleCrypto.prototype.digest = function (algorithm, input) {
   const view =
    input instanceof ArrayBuffer
     ? new Uint8Array(input)
     : new Uint8Array(input.buffer, input.byteOffset, input.byteLength);
   if (view.length === bytes.length && view.every((v, i) => v === bytes[i])) {
    window.__reviewDigest.hits++;
    return Promise.reject(new Error("SYNTHETIC reviewed-save failure"));
   }
   return old.call(this, algorithm, input);
  };
 }, Array.from(bytes));
}
async function restoreDigest(p) {
 await p.evaluate(() => {
  if (window.__reviewDigest) {
   SubtleCrypto.prototype.digest = window.__reviewDigest.old;
   delete window.__reviewDigest;
  }
 });
}
// Deterministic reached/release barrier over the REAL browser lock-query result.
async function holdOwnership(p) {
 await p.evaluate(() => {
  const original = navigator.locks.query;
  let release;
  const gate = new Promise((r) => (release = r));
  window.__reviewHold = { reached: false, release, original };
  let armed = true;
  navigator.locks.query = async function () {
   const result = await original.call(this);
   if (armed) {
    armed = false;
    window.__reviewHold.reached = true;
    await gate;
   }
   return result;
  };
 });
}
async function reached(p) {
 await p.waitForFunction(() => window.__reviewHold?.reached, null, {
  timeout: 10000,
 });
}
async function releaseOwnership(p) {
 await p.evaluate(() => {
  if (window.__reviewHold) {
   navigator.locks.query = window.__reviewHold.original;
   window.__reviewHold.release();
   delete window.__reviewHold;
  }
 });
}
module.exports = {
 reveal,
 $,
 ready,
 idle,
 save,
 analyze,
 selectKnu,
 create,
 open,
 head,
 stamp,
 preview,
 action,
 accept,
 undo,
 assertBytes,
 declineNoChange,
 faultSave,
 restoreDigest,
 holdOwnership,
 reached,
 releaseOwnership,
 negative: fixtures.negative,
 after,
};
