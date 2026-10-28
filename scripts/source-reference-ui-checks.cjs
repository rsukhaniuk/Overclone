// Extend the published offline-restart suite with actual Rust reference results.
// Observations and delayed delivery forward original events without synthesis.
const assert = require("node:assert/strict");
const H = require("./review-ui-helpers.cjs");
const { $, reveal } = H;
const ID = "engine-source-references";
const LABEL = "ENGINE-LABEL-001", REF = "ENGINE-REFERENCE-001";
const article = body => `\\documentclass{article}\r\n\\begin{document}\r\n${body}\r\n\\end{document}\r\n`;
const files = {
  "main.tex": Buffer.from("\uFEFF" + article("% Україна🙂\r\n\\ref{later}\\input{chapter}\r\n\\label{dup}\\label{dup}\\ref{бракує🙂}")),
  "chapter.tex": Buffer.from("\\label{later}\r\n"),
  "alternative.tex": Buffer.from(article("\\label{alone}\\ref{alone}")),
  "uncertain.tex": Buffer.from(article("\\custom{\\label{hidden}}\\ref{missing}")),
};
function observe() {
  const Native = window.Worker;
  const state = window.__referenceTest = { seen: [], hold: false, held: null, release: null };
  window.Worker = class extends Native {
    constructor(url, options) { super(url, options); this.referenceRust = String(url).includes("rust-worker.js"); }
    set onmessage(handler) {
      super.onmessage = event => {
        if (this.referenceRust && event.data?.canonical && typeof event.data.responseJson === "string") {
          let value;
          try { value = JSON.parse(event.data.responseJson); } catch { /* product handles malformed responses */ }
          if (value?.status === "completed" && value.payload?.profile?.id === "engine-source-references") {
            state.seen.push(value.payload);
            if (state.seen.length > 32) state.seen.shift();
            if (state.hold) {
              state.hold = false; state.held = value.payload.snapshotId;
              state.release = () => { handler.call(this, event); state.release = null; };
              return;
            }
          }
        }
        handler.call(this, event);
      };
    }
  };
}
async function install(context) { await context.addInitScript(observe); }
async function create(page) {
  const name = "SourceReferences-" + Date.now();
  await reveal(page, page.locator("#new-project"));
  await page.locator("#new-project").fill(name);
  await page.getByRole("button", { name: "Create project", exact: true }).click();
  for (const [file, bytes] of Object.entries(files)) {
    await $(page, "import-input").setInputFiles({ name: file, mimeType: "text/plain", buffer: bytes });
    await page.locator(".editor-toolbar .source-tab", { hasText: file }).waitFor();
  }
  await H.save(page);
  return name;
}
async function current(page, root, previous) {
  await page.waitForFunction(({ root, previous }) => {
    const section = document.querySelector('[data-testid="reference-checks"]');
    return document.querySelector('[data-testid="analysis-status"]')?.textContent === "Analysis complete."
      && section?.dataset.rootFileId === root && section.dataset.snapshotId !== previous
      && window.__referenceTest.seen.some(r => r.snapshotId === section.dataset.snapshotId && r.referenceConfiguration.rootFileId === root);
  }, { root, previous }, { timeout: 30000 });
  return page.evaluate(() => {
    const id = document.querySelector('[data-testid="reference-checks"]').dataset.snapshotId;
    return window.__referenceTest.seen.findLast(r => r.snapshotId === id);
  });
}
async function choose(page, root) {
  const previous = await $(page, "reference-checks").getAttribute("data-snapshot-id").catch(() => null);
  await $(page, "reference-root").selectOption(root);
  return current(page, root, previous);
}
async function exercise(page, name, phase) {
  const before = await H.head(page, name);
  const ids = Object.fromEntries(before.files.map(f => [f.path, f.id]));
  for (const f of before.files) assert.deepEqual(f.bytes, [...files[f.path]], "exact imported/saved bytes");
  await reveal(page, $(page, "analysis-profile"));
  await $(page, "analysis-profile").selectOption(ID);
  let report = await current(page, ids["main.tex"]);
  async function check(result, labels, references) {
    for (const [id, expected] of [[LABEL, labels], [REF, references]]) {
      const rule = result.ruleResults.find(r => r.ruleId === id);
      assert.equal(rule.outcome, expected);
      assert.equal(await page.locator(`[data-testid="reference-rule"][data-rule-id="${id}"]`).getAttribute("data-outcome"), expected[0].toUpperCase() + expected.slice(1));
    }
    assert.equal(await $(page, "preview-fix").count(), 0);
  }
  await check(report, "fail", "fail");
  for (const rule of report.ruleResults.filter(r => [LABEL, REF].includes(r.ruleId)))
    assert.deepEqual(rule.perFile.map(f => f.fileId), before.files.filter(f => ["main.tex", "chapter.tex"].includes(f.path)).map(f => f.id));
  assert.equal(report.diagnostics.length, 3);
  const missing = report.diagnostics.find(d => d.ruleId === REF);
  const bytes = files["main.tex"];
  assert.equal(bytes.subarray(missing.span.startByte, missing.span.endByte).toString(), "бракує🙂");
  await page.locator('[data-testid="analysis-finding"]').filter({ hasText: REF }).locator('[data-testid="finding-link"]').click();
  const prefix = bytes.subarray(0, missing.span.startByte).toString().replace(/^\uFEFF/, "").replace(/\r\n?/g, "\n");
  await page.waitForFunction(({ start, key }) => {
    const e = document.querySelector('[data-testid="editor"]');
    return e.selectionStart === start && e.selectionEnd === start + key.length && e.value.slice(e.selectionStart, e.selectionEnd) === key;
  }, { start: prefix.length, key: "бракує🙂" });
  assert.deepEqual(await H.head(page, name), before, "navigation never saves or edits");
  report = await choose(page, ids["alternative.tex"]); await check(report, "pass", "pass");
  assert.equal(report.diagnostics.length, 0);
  report = await choose(page, ids["uncertain.tex"]); await check(report, "indeterminate", "indeterminate");
  assert.equal(report.diagnostics.length, 0, "unsupported context suppresses missing-reference claims");
  report = await choose(page, ids["main.tex"]);
  await page.evaluate(() => { window.__referenceTest.hold = true; });
  await $(page, "analyze-now").click();
  await page.waitForFunction(() => window.__referenceTest.release !== null);
  const held = await page.evaluate(() => window.__referenceTest.held);
  const alternative = await choose(page, ids["alternative.tex"]);
  await page.evaluate(() => window.__referenceTest.release());
  assert.equal(await $(page, "reference-checks").getAttribute("data-snapshot-id"), alternative.snapshotId);
  report = await choose(page, ids["main.tex"]);
  assert.notEqual(report.snapshotId, held, "A-to-B-to-A rejects an old original worker result");
  await check(report, "fail", "fail");
  assert.deepEqual(await H.head(page, name), before, "all profile/root checks preserve exact saved files and revision");
  if (phase === "full cold offline") {
    const edited = (await $(page, "editor").inputValue()) + "% saved offline🙂\n";
    await $(page, "editor").fill(edited);
    await H.save(page);
    const fresh = await current(page, ids["main.tex"], report.snapshotId);
    await check(fresh, "fail", "fail");
    const after = await H.head(page, name);
    assert.notEqual(after.id, before.id);
    for (const file of after.files)
      assert.deepEqual(file.bytes, file.path === "main.tex" ? [239, 187, 191, ...Buffer.from(edited)] : [...files[file.path]]);
  }
  console.log(`PASS source references ${phase}: actual Rust closure, duplicate/missing/uncertainty, exact Unicode navigation, stale root response and exact saved bytes; no compiler resources installed`);
}
module.exports = { install, create, exercise };
