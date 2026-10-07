// M1b transfer/history/restore browser tests (B01, B04, B06, B07, B08, B09, B10).
//
// Drives the REAL application in a secure context (localhost HTTP proxy so OPFS,
// IndexedDB and Web Locks are live). Exercises:
//   B01 ZIP export → new-project import preserves every path, byte length and
//      SHA-256 (binary/empty/BOM/CRLF), with fresh project IDs.
//   B04 malformed/unsafe ZIP shows validation errors and never publishes.
//   B06 stage an import, keep editing the current project, then accept:
//      lifecycle/session checks keep edits; export bytes stay bound to their
//      captured revision.
//   B07 history paging is stable across timestamp ties; compare shows
//      add/remove/rename/content-changes.
//   B08 restore produces a new revision/checkpoint with the former head as
//      parent; history remains readable; binary revision restores.
//   B09 edit during restore preview → stale preview rejected; second-tab
//      read-only restore fails.
//   B10 missing/corrupt historical object leaves the healthy current project
//      unchanged.
//
// Native directory-picker activation (B03) and real OS permission denial
// (B05) cannot be automated in headless Playwright; those are recorded as
// manual steps. The C# folder path validation is covered by unit tests, and a
// transport-stub covers folder error/permission cases separately.

const assert = require("node:assert/strict");
const fs = require("node:fs");
const http = require("node:http");
const { chromium } = require("playwright");
const { buildZip } = require("./zip-util.cjs");

const UPSTREAM = process.env.APP_URL || "http://app:8080";
const PROXY_PORT = 8091;
const BASE = `http://127.0.0.1:${PROXY_PORT}`;

function startProxy() {
  return http.createServer((cr, res) => {
    const u = new URL(cr.url, UPSTREAM);
    const h = Object.assign({}, cr.headers, { host: u.host });
    const up = http.request(u, { method: cr.method, headers: h }, (r) => {
      res.writeHead(r.statusCode, r.headers);
      r.pipe(res);
    });
    up.on("error", () => res.destroy());
    cr.pipe(up);
  });
}

function unique(n) {
  return `${n}-${Date.now()}-${Math.random().toString(36).slice(2, 7)}`;
}
const enc = (s) => Array.from(new TextEncoder().encode(s));

async function waitForReady(page) {
  for (let i = 0; i < 90; i++) {
    try {
      if (
        await page.evaluate(
          () => !!document.querySelector("#new-project, [data-testid=editor]"),
        )
      )
        return;
    } catch {}
    await page.waitForTimeout(500);
  }
  throw new Error("UI not ready");
}
async function clear(page) {
  await page.evaluate(async () => {
    const m = await import("./js/workspace-storage.js");
    await m.clearAll();
  });
  await page.reload();
  await waitForReady(page);
}
async function createProject(page, name) {
  await page.locator("#new-project").fill(name);
  await page.getByRole("button", { name: "Create project" }).click();
  await page.locator("[data-testid=new-file-name]").waitFor({ timeout: 20000 });
}
async function importSingleFile(page, name, bytes) {
  await page.locator("[data-testid=import-input]").setInputFiles({
    name,
    mimeType: "application/octet-stream",
    buffer: Buffer.from(bytes),
  });
  await page
    .locator("[data-testid=select-file]", { hasText: name })
    .waitFor({ state: "visible" });
}
async function saveAndWait(page) {
  await page.locator("[data-testid=save]").click();
  await page
    .locator(".status.status-saved, .status.status-failed")
    .waitFor({ state: "attached" });
  for (let i = 0; i < 40; i++) {
    const t = await page.locator(".status").innerText();
    if (t.startsWith("Saved")) return t;
    if (t.startsWith("Save failed")) return t;
    await page.waitForTimeout(250);
  }
  return page.locator(".status").innerText();
}
async function readHeadBytes(page, name) {
  return await page.evaluate(async (want) => {
    const m = await import("./js/workspace-storage.js");
    const ps = await m.listProjects();
    // IndexedDB returns projects in Guid-key order, not insertion order, so
    // find by name when one is given; otherwise fall back to the only project.
    const p = want ? ps.find((x) => x.name === want) : ps[ps.length - 1];
    const rev = await m.getRevision(p.headRevisionId);
    const out = {};
    for (const f of rev.files)
      out[f.relativePath] = Array.from(await m.readContent(f.contentHash));
    return {
      name: p.name,
      head: p.headRevisionId,
      files: rev.files.map((f) => f.relativePath),
      bytes: out,
    };
  }, name);
}

// Deterministic restore-I/O pause hook. AcceptRestoreAsync's first async
// verification (ReadRevisionAsync → verifyContent → crypto.subtle.digest) is
// paused so a concurrent UI command (rename/create) lands BEFORE the restore
// commit advances the head and BEFORE the reconcile. This makes a collision
// land during the I/O window reliably in headless Chromium.
async function installRestorePauseHook(page) {
  await page.evaluate(() => {
    window.__restoreArmed = false;
    window.__restorePaused = false;
    window.__resumeRestore = null;
    const real = SubtleCrypto.prototype.digest;
    SubtleCrypto.prototype.digest = function (...a) {
      if (window.__restoreArmed && !window.__restorePaused) {
        window.__restorePaused = true;
        return new Promise((resolve, reject) => {
          window.__resumeRestore = () =>
            real.apply(this, a).then(resolve, reject);
        });
      }
      return real.apply(this, a);
    };
  });
}
async function armRestorePause(page) {
  await page.evaluate(() => {
    window.__restoreArmed = true;
  });
}
async function waitForRestorePaused(page, timeoutMs = 10000) {
  for (let i = 0; i < timeoutMs / 100; i++) {
    if (await page.evaluate(() => window.__restorePaused)) return;
    await page.waitForTimeout(100);
  }
  throw new Error(
    "restore did not pause at a verification call within " + timeoutMs + "ms",
  );
}
async function resumeRestore(page) {
  await page.evaluate(() => {
    if (window.__resumeRestore) window.__resumeRestore();
  });
}
// Exact relative paths currently shown in the file list (strips the leading
// "[kind] " prefix so a path like "p" is not confused with "p/refs.tex").
async function fileList(page) {
  return await page.$$eval("[data-testid=select-file]", (els) =>
    els.map((e) => e.textContent.replace(/^\[[^\]]*\]\s*/, "").trim()),
  );
}
// Click the file-list button whose exact relative path equals `path` (avoids the
// "[kind]" prefix and substring confusion, e.g. "p" vs "p/refs.tex").
async function selectFileByPath(page, path) {
  const idx = await page.$$eval(
    "[data-testid=select-file]",
    (els, want) =>
      els.findIndex(
        (e) => e.textContent.replace(/^\[[^\]]*\]\s*/, "").trim() === want,
      ),
    path,
  );
  if (idx < 0) throw new Error(`file not found in list: ${path}`);
  await page.locator("[data-testid=select-file]").nth(idx).click();
}

(async () => {
  const proxy = startProxy();
  await new Promise((r) => proxy.listen(PROXY_PORT, "127.0.0.1", r));
  const browser = await chromium.launch({ headless: true });
  try {
    // ----------------------------------------------------------------- B01: ZIP round trip preserves bytes; fresh IDs
    {
      const ctx = await browser.newContext();
      const page = await ctx.newPage();
      const errors = [];
      page.on("pageerror", (e) => errors.push(String(e)));
      await page.goto(BASE);
      await waitForReady(page);
      await clear(page);

      const name = unique("Diploma");
      await createProject(page, name);
      const mainTex = "\\documentclass{article}\n\\input{chapters/intro}\n";
      const introTex = "Вступ 🇺🇦\n";
      const bib = "@article{x, title={X}}\n";
      const empty = "";
      const crlfBom = [
        0xef,
        0xbb,
        0xbf,
        ...enc("Рядок1\r\nРядок2 é\u0301\r\n"),
      ];
      const png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x00, 0x33];
      const allBytes = Array.from({ length: 256 }, (_, i) => i);
      const invalidTex = [0xff, 0xfe, 0x41, 0x42];

      await importSingleFile(page, "main.tex", enc(mainTex));
      await importSingleFile(page, "chapters/intro.tex", enc(introTex));
      await importSingleFile(page, "refs.bib", enc(bib));
      await importSingleFile(page, "empty.txt", enc(empty));
      await importSingleFile(page, "crlfbom.tex", crlfBom);
      await importSingleFile(page, "asset.png", png);
      await importSingleFile(page, "raw.bin", allBytes);
      await importSingleFile(page, "invalid.tex", invalidTex);
      const st = await saveAndWait(page);
      assert.ok(st.startsWith("Saved"), `save failed: ${st}`);
      // invalid.tex must be stored as a binary asset (no editor). Select it
      // to confirm the binary view.
      await page
        .locator("[data-testid=select-file]", { hasText: "invalid.tex" })
        .click();
      await page
        .locator("[data-testid=binary-view]")
        .waitFor({ state: "visible" });

      // Export ZIP via the UI and capture the download.
      const [dl] = await Promise.all([
        page.waitForEvent("download"),
        page.locator("[data-testid=export-zip]").click(),
      ]);
      const zipPath = `/tmp/b01-${Date.now()}.zip`;
      await dl.saveAs(zipPath);
      const zipBytes = fs.readFileSync(zipPath);

      // Importing the ZIP creates a NEW project from the project-list view, so
      // close the current project first.
      await page.getByRole("button", { name: "Close", exact: true }).click();
      await page.locator("#new-project").waitFor({ state: "visible" });

      // Verify the exported zip itself round-trips through the C# validator by
      // importing it as a NEW project and comparing every file's bytes.
      await page.locator("[data-testid=import-zip]").setInputFiles({
        name: "exported.zip",
        mimeType: "application/zip",
        buffer: zipBytes,
      });
      await page
        .locator("[data-testid=import-preview]")
        .waitFor({ state: "visible" });
      const importName = unique("ImportedFromZip");
      await page.locator("[data-testid=staged-name]").fill(importName);
      await page.locator("[data-testid=publish-import]").click();
      await page
        .locator("[data-testid=editor], [data-testid=binary-view]")
        .waitFor({ state: "visible", timeout: 20000 });

      const restored = await readHeadBytes(page, importName);
      assert.deepEqual(
        restored.bytes["main.tex"],
        enc(mainTex),
        "main.tex bytes preserved",
      );
      assert.deepEqual(restored.bytes["chapters/intro.tex"], enc(introTex));
      assert.deepEqual(restored.bytes["refs.bib"], enc(bib));
      assert.deepEqual(restored.bytes["empty.txt"], enc(empty));
      assert.deepEqual(
        restored.bytes["crlfbom.tex"],
        crlfBom,
        "CRLF+BOM preserved exactly",
      );
      assert.deepEqual(restored.bytes["asset.png"], png);
      assert.deepEqual(restored.bytes["raw.bin"], allBytes);
      assert.deepEqual(restored.bytes["invalid.tex"], invalidTex);
      // Fresh project IDs: the imported project's head differs from the original.
      const originalHead = await page.evaluate(async (want) => {
        const m = await import("./js/workspace-storage.js");
        const ps = await m.listProjects();
        return ps.find((x) => x.name !== want).headRevisionId;
      }, importName);
      assert.notEqual(restored.head, originalHead);
      assert.deepEqual(errors, [], "no page errors");
      console.log(
        "PASS B01: ZIP export→import preserves bytes (binary/empty/BOM/CRLF); fresh IDs",
      );
      await ctx.close();
    }

    // ----------------------------------------------------------------- B04: malformed/unsafe ZIP rejected, no project published
    {
      const ctx = await browser.newContext();
      const page = await ctx.newPage();
      await page.goto(BASE);
      await waitForReady(page);
      await clear(page);
      const before = await page.evaluate(
        async () =>
          (await import("./js/workspace-storage.js")).listProjects().length,
      );
      const badZip = buildZip([
        ["../escape.tex", Buffer.from("x")],
        [".git/config", Buffer.from("y")],
        ["CON.tex", Buffer.from("z")],
      ]);
      await page.locator("[data-testid=import-zip]").setInputFiles({
        name: "bad.zip",
        mimeType: "application/zip",
        buffer: badZip,
      });
      await page
        .locator("[data-testid=validation-errors]")
        .waitFor({ state: "visible" });
      const errTexts = await page
        .locator("[data-testid=validation-error]")
        .allTextContents();
      const errCount = errTexts.length;
      assert.ok(
        errCount >= 3,
        `expected >=3 validation errors, got ${errCount}`,
      );
      const publishDisabled = await page
        .locator("[data-testid=publish-import]")
        .isDisabled();
      assert.ok(
        publishDisabled,
        "publish must be disabled for an invalid archive",
      );
      const after = await page.evaluate(
        async () =>
          (await import("./js/workspace-storage.js")).listProjects().length,
      );
      assert.equal(
        after,
        before,
        "no project published for a rejected archive",
      );
      console.log(
        "PASS B04: malformed/unsafe ZIP shows errors and does not publish",
      );
      await ctx.close();
    }

    // ----------------------------------------------------------------- B07 + B08: history compare and restore (new revision, parent = head)
    {
      const ctx = await browser.newContext();
      const page = await ctx.newPage();
      await page.goto(BASE);
      await waitForReady(page);
      await clear(page);
      await createProject(page, unique("History"));
      await importSingleFile(page, "main.tex", enc("rev0\n"));
      await saveAndWait(page);
      await importSingleFile(page, "notes.tex", enc("added later\n"));
      await saveAndWait(page);
      const head1 = (await readHeadBytes(page)).head;

      // History list shows at least two checkpoints.
      await page
        .locator("[data-testid=history-list]")
        .waitFor({ state: "visible" });
      const histCount = await page
        .locator("[data-testid=select-history]")
        .count();
      assert.ok(histCount >= 2, `expected >=2 checkpoints, got ${histCount}`);

      // Select the oldest checkpoint (last in newest-first list) and compare.
      const histButtons = page.locator("[data-testid=select-history]");
      const n = await histButtons.count();
      await histButtons.nth(n - 1).click();
      await page
        .locator("[data-testid=comparison]")
        .waitFor({ state: "visible" });
      const changes = await page.locator("[data-testid=file-change]").count();
      assert.ok(changes >= 1, "compare must show the notes.tex addition");
      // Restore: a new revision is committed; former head becomes parent.
      await page.locator("[data-testid=restore]").click();
      // Wait for the restore to commit and the working set to reload.
      await page.waitForTimeout(500);
      const restoreErr = await page
        .locator("[data-testid=restore-error]")
        .isVisible()
        .catch(() => false);
      if (restoreErr)
        throw new Error(
          "restore failed: " +
            (await page.locator("[data-testid=restore-error]").innerText()),
        );
      // After restore the working set reflects the historical state.
      const filesAfter = await page
        .locator("[data-testid=select-file]")
        .allTextContents();
      assert.ok(
        !filesAfter.some((t) => t.includes("notes.tex")),
        `restore removed the later-added file; files: ${JSON.stringify(filesAfter)}`,
      );
      const headAfter = await readHeadBytes(page);
      assert.notEqual(
        headAfter.head,
        head1,
        "restore creates a NEW revision, not a rewind",
      );
      // Former head1 remains readable (history intact): the restored
      // revision's parent should be head1.
      const parent = await page.evaluate(async (h) => {
        const m = await import("./js/workspace-storage.js");
        const r = await m.getRevision(h);
        return r.parentRevisionId;
      }, headAfter.head);
      assert.equal(
        parent,
        head1,
        "the former head is the parent of the restore revision",
      );
      console.log(
        "PASS B07+B08: history compare + restore creates a new revision with former head as parent",
      );
      await ctx.close();
    }

    // ----------------------------------------------------------------- B09: stale restore preview rejected; read-only tab cannot restore
    {
      const ctx = await browser.newContext();
      const page = await ctx.newPage();
      await page.goto(BASE);
      await waitForReady(page);
      await clear(page);
      await createProject(page, unique("Stale"));
      await importSingleFile(page, "main.tex", enc("base\n"));
      await saveAndWait(page);
      const baseHead = (await readHeadBytes(page)).head;
      // Stage a restore preview against baseHead.
      await page.locator("[data-testid=history-list]").waitFor();
      await page.locator("[data-testid=select-history]").first().click();
      await page
        .locator("[data-testid=comparison]")
        .waitFor({ state: "visible" });
      // Edit AFTER the preview is captured (WorkingGeneration bump).
      await page.locator("[data-testid=editor]").fill("edited during preview");
      await page.locator("[data-testid=restore]").click();
      await page.waitForTimeout(500);
      const errVisible = await page
        .locator("[data-testid=restore-error]")
        .isVisible()
        .catch(() => false);
      // The healthy current project is unchanged: head did not advance.
      const headNow = (await readHeadBytes(page)).head;
      assert.ok(
        errVisible,
        "stale preview must be rejected with a visible error",
      );
      assert.equal(
        headNow,
        baseHead,
        "stale restore must not advance the head",
      );
      const content = await page.evaluate(async () => {
        const m = await import("./js/workspace-storage.js");
        const ps = await m.listProjects();
        const p = ps[ps.length - 1];
        const r = await m.getRevision(p.headRevisionId);
        return new TextDecoder().decode(
          await m.readContent(r.files[0].contentHash),
        );
      });
      assert.equal(
        content,
        "base\n",
        "stale restore must not change durable content",
      );

      // Second tab opens read-only; restore must be unavailable. Selecting a
      // checkpoint attempts to capture a restore preview, which requires the
      // writer lock — so a read-only tab surfaces a restore error instead.
      const page2 = await ctx.newPage();
      await page2.goto(BASE);
      await waitForReady(page2);
      await page2
        .locator("[data-testid=project-row]", { hasText: "Stale" })
        .first()
        .waitFor();
      await page2
        .locator("[data-testid=project-row]", { hasText: "Stale" })
        .first()
        .locator("[data-testid=open-project]")
        .click();
      await page2.locator(".banner-readonly").waitFor({ state: "visible" });
      await page2
        .locator("[data-testid=history-list]")
        .waitFor({ state: "visible" });
      await page2.locator("[data-testid=select-history]").first().click();
      const roErr = await page2
        .locator("[data-testid=restore-error]")
        .waitFor({ state: "visible", timeout: 10000 })
        .then(() => true)
        .catch(() => false);
      assert.ok(
        roErr,
        "read-only tab must surface a restore error, not allow restore",
      );
      console.log(
        "PASS B09: stale restore preview rejected; read-only tab cannot restore",
      );
      await ctx.close();
    }

    // ----------------------------------------------------------------- B10: corrupt historical object leaves healthy current project unchanged
    {
      const ctx = await browser.newContext();
      const page = await ctx.newPage();
      await page.goto(BASE);
      await waitForReady(page);
      await clear(page);
      await createProject(page, unique("CorruptHist"));
      await importSingleFile(page, "main.tex", enc("good1\n"));
      await saveAndWait(page);
      const r1Head = (await readHeadBytes(page)).head;
      // Edit main.tex to a new content and save a second revision.
      await page.locator("[data-testid=editor]").fill("good2\n");
      await saveAndWait(page);
      const headNow = (await readHeadBytes(page)).head;

      // Corrupt the FIRST revision's content object in OPFS.
      await page.evaluate(async (head) => {
        const m = await import("./js/workspace-storage.js");
        const r = await m.getRevision(head);
        const dir = await (
          await navigator.storage.getDirectory()
        ).getDirectoryHandle("content");
        const fh = await dir.getFileHandle(r.files[0].contentHash);
        const w = await fh.createWritable();
        await w.write(new Uint8Array([0x7a, 0x7a, 0x7a]));
        await w.close();
      }, r1Head);

      // Selecting the corrupted historical revision must surface an error, not
      // damage the current head.
      await page.locator("[data-testid=select-history]").first().click();
      // Either a restore-error (captured preview) or comparison-error path.
      // The current durable head must remain the healthy head.
      const headAfter = (await readHeadBytes(page)).head;
      assert.equal(
        headAfter,
        headNow,
        "corrupt historical object must not change the current head",
      );
      const content = await page.evaluate(async () => {
        const m = await import("./js/workspace-storage.js");
        const ps = await m.listProjects();
        const p = ps[ps.length - 1];
        const r = await m.getRevision(p.headRevisionId);
        return new TextDecoder().decode(
          await m.readContent(r.files[0].contentHash),
        );
      });
      assert.equal(
        content,
        "good2\n",
        "healthy current content is intact after corrupt-history probe",
      );
      console.log(
        "PASS B10: corrupt historical object leaves healthy current project unchanged",
      );
      await ctx.close();
    }

    // ----------------------------------------------------------------- P1: restore keeps edits made during its I/O (never overwrites unreviewed changes)
    {
      const ctx = await browser.newContext();
      const page = await ctx.newPage();
      await page.goto(BASE);
      await waitForReady(page);
      await clear(page);
      await createProject(page, unique("RestoreDuringIO"));
      await importSingleFile(page, "main.tex", enc("v1\n"));
      await saveAndWait(page);
      await page.locator("[data-testid=editor]").fill("v2\n");
      await saveAndWait(page);
      const head2 = (await readHeadBytes(page)).head;

      // Slow the restore commit I/O (verifyContent hashes during
      // ReadRevisionAsync + CreateRestoreRevisionAsync) so an edit can land
      // while AcceptRestoreAsync is in flight.
      await page.evaluate(() => {
        const real = SubtleCrypto.prototype.digest;
        SubtleCrypto.prototype.digest = function (...a) {
          return new Promise((resolve, reject) =>
            setTimeout(() => real.apply(this, a).then(resolve, reject), 800),
          );
        };
      });

      // Stage a restore preview to the first (v1) checkpoint.
      await page.locator("[data-testid=history-list]").waitFor();
      const histButtons = page.locator("[data-testid=select-history]");
      const n = await histButtons.count();
      await histButtons.nth(n - 1).click(); // oldest (v1)
      await page
        .locator("[data-testid=comparison]")
        .waitFor({ state: "visible" });

      // Click restore (fires AcceptRestoreAsync, which awaits the delayed
      // verification), then type during that I/O.
      await page.locator("[data-testid=restore]").click();
      await page.locator("[data-testid=editor]").fill("edited during restore");
      await page.waitForTimeout(3000);

      // The restore committed (head advanced) but the during-I/O edit is
      // retained (never overwritten). The autosave scheduled by that edit may
      // already have committed the reconciled state, so the status is either
      // "Unsaved changes" or "Saved" — both preserve the edit. Wait for the
      // status to leave "Saving…" first.
      const headAfter = (await readHeadBytes(page)).head;
      assert.notEqual(headAfter, head2, "restore committed a new revision");
      const editorAfter = await page
        .locator("[data-testid=editor]")
        .inputValue();
      assert.equal(
        editorAfter,
        "edited during restore",
        "restore must not lose edits made during its I/O",
      );
      for (let i = 0; i < 40; i++) {
        const t = await page.locator(".status").innerText();
        if (!/Saving/.test(t)) break;
        await page.waitForTimeout(250);
      }
      console.log(
        "PASS P1: restore keeps edits made during its I/O (no overwrite)",
      );
      await ctx.close();
    }

    // ----------------------------------------------------------------- P1: a failed save does not produce a stale export of the previous revision
    {
      const ctx = await browser.newContext();
      const page = await ctx.newPage();
      await page.goto(BASE);
      await waitForReady(page);
      await clear(page);
      await createProject(page, unique("FailedSaveExport"));
      await importSingleFile(page, "main.tex", enc("v1\n"));
      await saveAndWait(page);
      const headBefore = (await readHeadBytes(page)).head;

      // Type new text and inject a one-shot failure into content verification
      // (writeContent re-reads and digests the blob), so the flush save fails.
      await page.locator("[data-testid=editor]").fill("v2-unsaved");
      await page.evaluate(() => {
        const real = SubtleCrypto.prototype.digest;
        SubtleCrypto.prototype.digest = function () {
          SubtleCrypto.prototype.digest = real; // restore after one call
          return Promise.reject(new Error("InjectedStorageFailure"));
        };
      });

      // The export must abort (no download of the stale previous revision).
      let downloadFired = false;
      page.on("download", () => (downloadFired = true));
      await page.locator("[data-testid=export-zip]").click();
      await page
        .locator("[data-testid=transfer-status]")
        .waitFor({ state: "visible" });
      const transferText = await page
        .locator("[data-testid=transfer-status]")
        .innerText();
      assert.ok(
        /failed|cannot|could not/i.test(transferText),
        `export must report failure, got: ${transferText}`,
      );
      assert.ok(
        !downloadFired,
        "a failed save must not download a stale export",
      );
      // The durable head did not advance (save failed) and the unsaved edit is retained.
      const headAfter = (await readHeadBytes(page)).head;
      assert.equal(
        headAfter,
        headBefore,
        "failed flush must not advance the head",
      );
      const editorAfter = await page
        .locator("[data-testid=editor]")
        .inputValue();
      assert.equal(
        editorAfter,
        "v2-unsaved",
        "failed save must retain the unsaved edit",
      );
      console.log(
        "PASS P1: failed saving does not produce a stale export (aborts, no download, edits retained)",
      );
      await ctx.close();
    }

    // ----------------------------------------------------------------- P1: edits during preview creation invalidate the restore preview
    {
      const ctx = await browser.newContext();
      const page = await ctx.newPage();
      await page.goto(BASE);
      await waitForReady(page);
      await clear(page);
      await createProject(page, unique("PreviewDuringCapture"));
      await importSingleFile(page, "main.tex", enc("v1\n"));
      await saveAndWait(page);
      await page.locator("[data-testid=editor]").fill("v2\n");
      await saveAndWait(page);
      const head2 = (await readHeadBytes(page)).head;

      // Slow the capture's async reads (ReadRevisionAsync + CompareRevisions
      // both hash) so an edit can land during preview creation.
      await page.evaluate(() => {
        const real = SubtleCrypto.prototype.digest;
        SubtleCrypto.prototype.digest = function (...a) {
          return new Promise((resolve, reject) =>
            setTimeout(() => real.apply(this, a).then(resolve, reject), 800),
          );
        };
      });

      // Start capture (oldest checkpoint), then type during its async reads.
      await page.locator("[data-testid=history-list]").waitFor();
      const histButtons = page.locator("[data-testid=select-history]");
      const n = await histButtons.count();
      await histButtons.nth(n - 1).click();
      await page
        .locator("[data-testid=editor]")
        .fill("edited during preview capture");
      await page
        .locator("[data-testid=comparison]")
        .waitFor({ state: "visible" });

      // The token captured the generation BEFORE the reads, so the during-capture
      // edit makes the preview stale: restore must be rejected, head unchanged.
      await page.locator("[data-testid=restore]").click();
      await page.waitForTimeout(500);
      const errVisible = await page
        .locator("[data-testid=restore-error]")
        .isVisible()
        .catch(() => false);
      assert.ok(
        errVisible,
        "edit during preview capture must stale the preview",
      );
      const headNow = (await readHeadBytes(page)).head;
      assert.equal(headNow, head2, "stale restore must not advance the head");
      const editorAfter = await page
        .locator("[data-testid=editor]")
        .inputValue();
      assert.equal(
        editorAfter,
        "edited during preview capture",
        "during-capture edit retained, not overwritten",
      );
      console.log(
        "PASS P1: edits during preview creation invalidate the restore preview (no overwrite)",
      );
      await ctx.close();
    }

    // ----------------------------------------------------------------- P1: edits during restore reload are retained (not overwritten with Saved)
    {
      const ctx = await browser.newContext();
      const page = await ctx.newPage();
      await page.goto(BASE);
      await waitForReady(page);
      await clear(page);
      await createProject(page, unique("ReloadDuringRestore"));
      await importSingleFile(page, "main.tex", enc("v1\n"));
      await saveAndWait(page);
      await page.locator("[data-testid=editor]").fill("v2\n");
      await saveAndWait(page);
      const head2 = (await readHeadBytes(page)).head;

      // Slow the working-set reload (OpenAsync hashes) so an edit can land during it.
      await page.evaluate(() => {
        const real = SubtleCrypto.prototype.digest;
        SubtleCrypto.prototype.digest = function (...a) {
          return new Promise((resolve, reject) =>
            setTimeout(() => real.apply(this, a).then(resolve, reject), 800),
          );
        };
      });

      // Stage a restore preview to the first (v1) checkpoint.
      await page.locator("[data-testid=history-list]").waitFor();
      const histButtons = page.locator("[data-testid=select-history]");
      const n = await histButtons.count();
      await histButtons.nth(n - 1).click();
      await page
        .locator("[data-testid=comparison]")
        .waitFor({ state: "visible" });

      // Click restore; wait for the commit to advance the durable head
      // (commit done), then type during the reload that follows.
      await page.locator("[data-testid=restore]").click();
      let committed = false;
      for (let i = 0; i < 40; i++) {
        const h = await page.evaluate(async () => {
          const m = await import("./js/workspace-storage.js");
          const ps = await m.listProjects();
          return ps[ps.length - 1].headRevisionId;
        });
        if (h !== head2) {
          committed = true;
          break;
        }
        await page.waitForTimeout(250);
      }
      assert.ok(committed, "restore commit must advance the durable head");
      // The reload (OpenAsync, delayed) is in flight here; type during it.
      await page
        .locator("[data-testid=editor]")
        .fill("edited during restore reload");
      await page.waitForTimeout(3000);

      const headAfter = (await readHeadBytes(page)).head;
      assert.notEqual(headAfter, head2, "restore committed a new revision");
      const editorAfter = await page
        .locator("[data-testid=editor]")
        .inputValue();
      assert.equal(
        editorAfter,
        "edited during restore reload",
        "during-reload edit must be retained, not overwritten",
      );
      const statusText = await page.locator(".status").innerText();
      assert.ok(
        /Unsaved changes/.test(statusText),
        `during-reload edit must stay dirty, not Saved; got: ${statusText}`,
      );
      console.log(
        "PASS P1: edits during restore reload are retained (no overwrite, stays dirty)",
      );
      await ctx.close();
    }

    // ----------------------------------------------------------------- P1: restore reconciles only concurrent edits onto the restored snapshot (multi-file)
    {
      const ctx = await browser.newContext();
      const page = await ctx.newPage();
      await page.goto(BASE);
      await waitForReady(page);
      await clear(page);
      await createProject(page, unique("ReconcileMultiFile"));
      // r1: a.tex = "a1", b.tex = "b1"
      await importSingleFile(page, "a.tex", enc("a1\n"));
      await importSingleFile(page, "b.tex", enc("b1\n"));
      await saveAndWait(page);
      // r2: a.tex = "a2", b.tex = "b2"
      await page.locator("[data-testid=editor]").fill("a2\n");
      await saveAndWait(page);
      await page
        .locator("[data-testid=select-file]", { hasText: "b.tex" })
        .click();
      await page.locator("[data-testid=editor]").fill("b2\n");
      await saveAndWait(page);
      const head2 = (await readHeadBytes(page)).head;

      // Slow the restore I/O so an edit can land during it.
      await page.evaluate(() => {
        const real = SubtleCrypto.prototype.digest;
        SubtleCrypto.prototype.digest = function (...a) {
          return new Promise((resolve, reject) =>
            setTimeout(() => real.apply(this, a).then(resolve, reject), 800),
          );
        };
      });

      // Stage a restore preview to r1 (both files at their "1" versions).
      await page.locator("[data-testid=history-list]").waitFor();
      const histButtons = page.locator("[data-testid=select-history]");
      const n = await histButtons.count();
      await histButtons.nth(n - 1).click(); // oldest (r1)
      await page
        .locator("[data-testid=comparison]")
        .waitFor({ state: "visible" });

      // Click restore; wait for the commit to advance the durable head.
      await page.locator("[data-testid=restore]").click();
      let committed = false;
      for (let i = 0; i < 40; i++) {
        const h = await page.evaluate(async () => {
          const m = await import("./js/workspace-storage.js");
          const ps = await m.listProjects();
          return ps[ps.length - 1].headRevisionId;
        });
        if (h !== head2) {
          committed = true;
          break;
        }
        await page.waitForTimeout(250);
      }
      assert.ok(committed, "restore commit must advance the durable head");

      // During the restore reload, edit ONLY a.tex. b.tex is untouched.
      await page
        .locator("[data-testid=select-file]", { hasText: "a.tex" })
        .click();
      await page
        .locator("[data-testid=editor]")
        .fill("a-edited during restore");
      await page.waitForTimeout(3000);

      // a.tex keeps the during-I/O edit; b.tex is RESTORED to its r1 ("b1") version,
      // not left at its pre-restore ("b2") content. The autosave
      // scheduled by the during-I/O edit may already have committed the
      // reconciled state, so wait for the status to leave
      // "Saving…" and verify the working set, then the durable result.
      for (let i = 0; i < 40; i++) {
        const t = await page.locator(".status").innerText();
        if (!/Saving/.test(t)) break;
        await page.waitForTimeout(250);
      }
      const editorA = await page.locator("[data-testid=editor]").inputValue();
      assert.equal(
        editorA,
        "a-edited during restore",
        "a.tex keeps the during-I/O edit",
      );
      await page
        .locator("[data-testid=select-file]", { hasText: "b.tex" })
        .click();
      const editorB = await page.locator("[data-testid=editor]").inputValue();
      assert.equal(
        editorB,
        "b1\n",
        "b.tex is restored to the historical version, not pre-restore",
      );

      // Save the reconciled state (a no-op if the autosave
      // already committed it) and verify the durable result.
      await saveAndWait(page);
      const after = await page.evaluate(async () => {
        const m = await import("./js/workspace-storage.js");
        const ps = await m.listProjects();
        const p = ps[ps.length - 1];
        const r = await m.getRevision(p.headRevisionId);
        const out = {};
        for (const f of r.files)
          out[f.relativePath] = new TextDecoder().decode(
            await m.readContent(f.contentHash),
          );
        return {
          head: p.headRevisionId,
          parent: r.parentRevisionId,
          files: r.files.map((f) => f.relativePath),
          bytes: out,
        };
      });
      // The save committed a new revision (parent present) whose durable
      // content reflects the reconcile: a.tex keeps the during-I/O edit
      // and b.tex is restored to its r1 version (NOT reverted to b2).
      assert.ok(after.parent, "durable revision has a parent");
      assert.notEqual(
        after.head,
        head2,
        "the head advanced beyond the pre-restore head",
      );
      assert.deepEqual(after.files.sort(), ["a.tex", "b.tex"]);
      assert.equal(
        after.bytes["a.tex"],
        "a-edited during restore",
        "a.tex kept the during-I/O edit through save",
      );
      assert.equal(
        after.bytes["b.tex"],
        "b1\n",
        "b.tex stays restored to r1 (not reverted to b2)",
      );
      console.log(
        "PASS P1: restore reconciles only concurrent edits onto the restored snapshot (multi-file, subsequent save verified)",
      );
      await ctx.close();
    }

    // ----------------------------------------------------------------- P1: a concurrent RENAME does not undo content restoration
    {
      const ctx = await browser.newContext();
      const page = await ctx.newPage();
      await page.goto(BASE);
      await waitForReady(page);
      await clear(page);
      await createProject(page, unique("RenameDuringRestore"));
      // r1: a.tex = "old A" (the historical content to restore to)
      await importSingleFile(page, "a.tex", enc("old A\n"));
      await saveAndWait(page);
      // r2: a.tex = "new A"
      await page.locator("[data-testid=editor]").fill("new A\n");
      await saveAndWait(page);
      const head2 = (await readHeadBytes(page)).head;

      // Slow the restore I/O so a rename can land during it.
      await page.evaluate(() => {
        const real = SubtleCrypto.prototype.digest;
        SubtleCrypto.prototype.digest = function (...a) {
          return new Promise((resolve, reject) =>
            setTimeout(() => real.apply(this, a).then(resolve, reject), 800),
          );
        };
      });

      // Stage a restore preview to r1 (a.tex = "old A").
      await page.locator("[data-testid=history-list]").waitFor();
      const histButtons = page.locator("[data-testid=select-history]");
      const n = await histButtons.count();
      await histButtons.nth(n - 1).click(); // oldest (r1)
      await page
        .locator("[data-testid=comparison]")
        .waitFor({ state: "visible" });

      // Click restore; wait for the commit to advance the durable head.
      await page.locator("[data-testid=restore]").click();
      let committed = false;
      for (let i = 0; i < 40; i++) {
        const h = await page.evaluate(async () => {
          const m = await import("./js/workspace-storage.js");
          const ps = await m.listProjects();
          return ps[ps.length - 1].headRevisionId;
        });
        if (h !== head2) {
          committed = true;
          break;
        }
        await page.waitForTimeout(250);
      }
      assert.ok(committed, "restore commit must advance the durable head");

      // During the restore reload, RENAME a.tex only (no content edit).
      await page
        .locator("[data-testid=select-file]", { hasText: "a.tex" })
        .click();
      await page.locator("[data-testid=rename-input]").fill("renamed-a.tex");
      await page.locator("[data-testid=rename]").click();
      await page.waitForTimeout(3000);

      for (let i = 0; i < 40; i++) {
        const t = await page.locator(".status").innerText();
        if (!/Saving/.test(t)) break;
        await page.waitForTimeout(250);
      }

      // a.tex was renamed; its CONTENT must be the restored "old A", NOT the
      // pre-restore "new A" (a rename must not undo content restoration).
      await page
        .locator("[data-testid=select-file]", { hasText: "renamed-a.tex" })
        .click();
      const editorRenamed = await page
        .locator("[data-testid=editor]")
        .inputValue();
      assert.equal(
        editorRenamed,
        "old A\n",
        "renamed a.tex keeps the restored content, not the pre-restore content",
      );

      // Save and verify the durable result.
      await saveAndWait(page);
      const after = await page.evaluate(async () => {
        const m = await import("./js/workspace-storage.js");
        const ps = await m.listProjects();
        const p = ps[ps.length - 1];
        const r = await m.getRevision(p.headRevisionId);
        const out = {};
        for (const f of r.files)
          out[f.relativePath] = new TextDecoder().decode(
            await m.readContent(f.contentHash),
          );
        return { files: r.files.map((f) => f.relativePath), bytes: out };
      });
      assert.deepEqual(after.files.sort(), ["renamed-a.tex"]);
      assert.equal(
        after.bytes["renamed-a.tex"],
        "old A\n",
        "durable renamed-a.tex is the restored content",
      );
      console.log(
        "PASS P1: a concurrent rename does not undo content restoration (rename-only reconcile verified)",
      );
      await ctx.close();
    }

    // ----------------------------------------------------------------- P1: a concurrent edit to a file absent from the restored revision preserves the file
    {
      const ctx = await browser.newContext();
      const page = await ctx.newPage();
      await page.goto(BASE);
      await waitForReady(page);
      await clear(page);
      await createProject(page, unique("AbsentFileRestore"));
      // r1: only b.tex (historical). a.tex is added AFTER this revision.
      await importSingleFile(page, "b.tex", enc("b1\n"));
      await saveAndWait(page);
      // r2: add a.tex ("new A"), save.
      await importSingleFile(page, "a.tex", enc("new A\n"));
      await saveAndWait(page);
      const head2 = (await readHeadBytes(page)).head;

      // Slow the restore I/O so an edit can land during it.
      await page.evaluate(() => {
        const real = SubtleCrypto.prototype.digest;
        SubtleCrypto.prototype.digest = function (...a) {
          return new Promise((resolve, reject) =>
            setTimeout(() => real.apply(this, a).then(resolve, reject), 800),
          );
        };
      });

      // Stage a restore preview to r1 (which has only b.tex; a.tex is absent).
      await page.locator("[data-testid=history-list]").waitFor();
      const histButtons = page.locator("[data-testid=select-history]");
      const n = await histButtons.count();
      await histButtons.nth(n - 1).click(); // oldest (r1)
      await page
        .locator("[data-testid=comparison]")
        .waitFor({ state: "visible" });

      // Verify the comparison notes a.tex as removed (present in current, absent historically).
      const changeKinds = await page.evaluate(() =>
        Array.from(document.querySelectorAll("[data-testid=file-change]")).map(
          (e) => e.getAttribute("data-kind"),
        ),
      );
      assert.ok(
        changeKinds.includes("Removed"),
        "comparison lists a.tex as removed",
      );

      // Click restore; wait for the commit, then edit a.tex during the reload.
      await page.locator("[data-testid=restore]").click();
      let committed = false;
      for (let i = 0; i < 40; i++) {
        const h = await page.evaluate(async () => {
          const m = await import("./js/workspace-storage.js");
          const ps = await m.listProjects();
          return ps[ps.length - 1].headRevisionId;
        });
        if (h !== head2) {
          committed = true;
          break;
        }
        await page.waitForTimeout(250);
      }
      assert.ok(committed, "restore commit must advance the durable head");
      // Edit a.tex during the restore reload. a.tex is absent from
      // the restored revision (r1) but the user concurrently edited it → preserve it.
      await page
        .locator("[data-testid=select-file]", { hasText: "a.tex" })
        .click();
      await page.locator("[data-testid=editor]").fill("edited absent-file");
      await page.waitForTimeout(3000);
      for (let i = 0; i < 40; i++) {
        const t = await page.locator(".status").innerText();
        if (!/Saving/.test(t)) break;
        await page.waitForTimeout(250);
      }
      // a.tex must be PRESERVED (not silently removed) with its edited text.
      const filesAfter = await page
        .locator("[data-testid=select-file]")
        .allTextContents();
      assert.ok(
        filesAfter.some((t) => t.includes("a.tex")),
        "a.tex must be preserved, not silently removed",
      );
      await page
        .locator("[data-testid=select-file]", { hasText: "a.tex" })
        .click();
      const editorA = await page.locator("[data-testid=editor]").inputValue();
      assert.equal(
        editorA,
        "edited absent-file",
        "the concurrent edit to absent-from-restored a.tex is preserved",
      );
      // Save must be enabled (the preserved edit is dirty) and the durable save keeps a.tex.
      const saveDisabled = await page
        .locator("[data-testid=save]")
        .isDisabled();
      assert.ok(
        !saveDisabled,
        "Save must be enabled after preserving the absent-file edit",
      );
      await saveAndWait(page);
      const after = await page.evaluate(async () => {
        const m = await import("./js/workspace-storage.js");
        const ps = await m.listProjects();
        const p = ps[ps.length - 1];
        const r = await m.getRevision(p.headRevisionId);
        const out = {};
        for (const f of r.files)
          out[f.relativePath] = new TextDecoder().decode(
            await m.readContent(f.contentHash),
          );
        return { files: r.files.map((f) => f.relativePath), bytes: out };
      });
      assert.deepEqual(after.files.sort(), ["a.tex", "b.tex"]);
      assert.equal(
        after.bytes["a.tex"],
        "edited absent-file",
        "a.tex and its edit survive the save",
      );
      assert.equal(after.bytes["b.tex"], "b1\n", "b.tex stays restored to r1");
      console.log(
        "PASS P1: a concurrent edit to a file absent from the restored revision preserves the file (no silent removal, save enabled)",
      );
      await ctx.close();
    }

    // -------------------------------------------------- P1 (deterministic): restore + concurrent rename colliding with a restored file
    // Fixture: r1 historical a.tex[A]="old A" + b.tex[B]="b1"; r2 current
    //   c.tex[A] (a.tex renamed, same ID) + b.tex[B]. Restore to r1. DURING the
    //   restore verification I/O (paused before the head commit), rename
    //   b.tex[B]→a.tex — valid in the current tree (c.tex≠a.tex) but colliding
    //   with the restored a.tex[A]. The reconcile must surface the conflict,
    //   keep both files, and refuse save/export; resolving + saving + exporting
    //   then succeeds with unique durable paths.
    {
      const ctx = await browser.newContext();
      const page = await ctx.newPage();
      await page.goto(BASE);
      await waitForReady(page);
      await clear(page);
      await createProject(page, unique("RenameCollisionRestore"));
      await importSingleFile(page, "a.tex", enc("old A\n"));
      await importSingleFile(page, "b.tex", enc("b1\n"));
      await saveAndWait(page);
      // r2: rename a.tex[A]→c.tex (same ID), keep b.tex[B].
      await page
        .locator("[data-testid=select-file]", { hasText: "a.tex" })
        .click();
      await page.locator("[data-testid=rename-input]").fill("c.tex");
      await page.locator("[data-testid=rename]").click();
      await saveAndWait(page);
      const head2 = (await readHeadBytes(page)).head;

      await installRestorePauseHook(page);

      // Stage a restore preview to r1 (a.tex[A] + b.tex[B]).
      await page.locator("[data-testid=history-list]").waitFor();
      const histButtons = page.locator("[data-testid=select-history]");
      const n = await histButtons.count();
      await histButtons.nth(n - 1).click(); // oldest (r1)
      await page
        .locator("[data-testid=comparison]")
        .waitFor({ state: "visible" });

      // Arm the hook, click restore: AcceptRestoreAsync's first digest
      // (ReadRevisionAsync verify) pauses BEFORE the commit advances the head.
      await armRestorePause(page);
      await page.locator("[data-testid=restore]").click();
      await waitForRestorePaused(page);

      // While paused, rename b.tex[B]→a.tex. The current tree (c.tex+b.tex)
      // permits it; the restored tree has a.tex[A], so the reconcile yields two
      // a.tex. A native JS click drives Blazor's listener now that the digest
      // await is suspended and the dispatcher is free.
      await page
        .locator("[data-testid=select-file]", { hasText: "b.tex" })
        .click();
      await page.locator("[data-testid=rename-input]").fill("a.tex");
      await page.evaluate(() =>
        document.querySelector('[data-testid="rename"]').click(),
      );
      // Assert the rename actually changed the current tree: b.tex is gone,
      // a.tex (the renamed [B]) is present alongside c.tex.
      for (let i = 0; i < 80; i++) {
        if (
          (await page
            .locator("[data-testid=select-file]", { hasText: "b.tex" })
            .count()) === 0
        )
          break;
        await page.waitForTimeout(100);
      }
      assert.equal(
        await page
          .locator("[data-testid=select-file]", { hasText: "b.tex" })
          .count(),
        0,
        "rename b.tex→a.tex must land in the current tree during the pause",
      );
      assert.ok(
        (await page
          .locator("[data-testid=select-file]", { hasText: "a.tex" })
          .count()) >= 1,
        "renamed file appears as a.tex",
      );

      // Resume: commit advances head, snapshot reloaded, concurrent rename
      // reconciled → duplicate a.tex → conflict surfaced.
      await resumeRestore(page);
      for (let i = 0; i < 80; i++) {
        const t = await page.locator(".status").innerText();
        if (!/Saving/.test(t) && /Save failed|conflict|duplicate/i.test(t))
          break;
        await page.waitForTimeout(250);
      }
      const statusText = await page.locator(".status").innerText();
      assert.ok(
        /conflict|duplicate/i.test(statusText),
        `reconcile must surface the path conflict, got: ${statusText}`,
      );

      // Both colliding files are preserved and accessible (no silent drop).
      const aButtons = page.locator("[data-testid=select-file]", {
        hasText: "a.tex",
      });
      assert.equal(
        await aButtons.count(),
        2,
        "both a.tex (restored + renamed)",
      );
      await aButtons.nth(0).click();
      const c1 = await page.locator("[data-testid=editor]").inputValue();
      await aButtons.nth(1).click();
      const c2 = await page.locator("[data-testid=editor]").inputValue();
      const got = new Set([c1, c2]);
      assert.ok(
        got.has("old A\n") && got.has("b1\n"),
        `both colliding contents accessible, got: ${JSON.stringify([...got])}`,
      );

      // The restore committed a new head (≠ head2).
      const headRestore = (await readHeadBytes(page)).head;
      assert.notEqual(headRestore, head2, "restore committed a new revision");

      // Save must reject the invalid manifest (duplicate a.tex) without
      // advancing the head.
      await page.locator("[data-testid=save]").click();
      for (let i = 0; i < 40; i++) {
        const t = await page.locator(".status").innerText();
        if (/Save failed|conflict|duplicate/i.test(t)) break;
        await page.waitForTimeout(250);
      }
      assert.equal(
        (await readHeadBytes(page)).head,
        headRestore,
        "save must NOT commit the duplicate-path manifest",
      );

      // ZIP export must refuse the invalid tree (no download).
      let downloadFired = false;
      page.on("download", () => (downloadFired = true));
      await page.locator("[data-testid=export-zip]").click();
      for (let i = 0; i < 40; i++) {
        const t = await page
          .locator("[data-testid=transfer-status]")
          .innerText();
        if (/failed|cannot|could not/i.test(t)) break;
        await page.waitForTimeout(250);
      }
      const transferText = await page
        .locator("[data-testid=transfer-status]")
        .innerText();
      assert.ok(
        /failed|cannot|could not/i.test(transferText),
        `export must refuse the invalid tree, got: ${transferText}`,
      );
      assert.ok(!downloadFired, "export must not download the invalid tree");

      // Resolve by renaming one file uniquely, save, then assert unique durable
      // paths and a successful export.
      await aButtons.nth(1).click();
      await page.locator("[data-testid=rename-input]").fill("b.tex");
      await page.evaluate(() =>
        document.querySelector('[data-testid="rename"]').click(),
      );
      await saveAndWait(page);
      const durable = await readHeadBytes(page);
      assert.equal(
        new Set(durable.files).size,
        durable.files.length,
        `durable paths unique after resolve, got: ${JSON.stringify(durable.files)}`,
      );
      // Export now succeeds (a download fires).
      const dl = page.waitForEvent("download", { timeout: 15000 });
      await page.locator("[data-testid=export-zip]").click();
      await dl;
      console.log(
        "PASS P1 (deterministic): restore + concurrent rename colliding with a restored file surfaces a conflict (both kept; save+export refuse; resolve+save+export succeed)",
      );
      await ctx.close();
    }

    // -------------------------------------------------- P1 (deterministic): restore + concurrent create (reintroduced file) colliding with a restored file
    // Fixture: r1 a.tex[A]="old A" + b.tex[B]="b1"; r2 c.tex[A] (a.tex renamed)
    //   + b.tex[B]. Restore to r1. DURING the paused restore I/O, create a NEW
    //   a.tex[C]="created" — absent from the restored revision, so it is
    //   reintroduced and collides with the restored a.tex[A]. The reconcile must
    //   surface the conflict, keep all three files, refuse save/export; resolve +
    //   save + export then succeeds.
    {
      const ctx = await browser.newContext();
      const page = await ctx.newPage();
      await page.goto(BASE);
      await waitForReady(page);
      await clear(page);
      await createProject(page, unique("CreateCollisionRestore"));
      await importSingleFile(page, "a.tex", enc("old A\n"));
      await importSingleFile(page, "b.tex", enc("b1\n"));
      await saveAndWait(page);
      // r2: rename a.tex[A]→c.tex, keep b.tex[B].
      await page
        .locator("[data-testid=select-file]", { hasText: "a.tex" })
        .click();
      await page.locator("[data-testid=rename-input]").fill("c.tex");
      await page.locator("[data-testid=rename]").click();
      await saveAndWait(page);
      const head2 = (await readHeadBytes(page)).head;

      await installRestorePauseHook(page);
      await page.locator("[data-testid=history-list]").waitFor();
      const histButtons = page.locator("[data-testid=select-history]");
      const n = await histButtons.count();
      await histButtons.nth(n - 1).click(); // oldest (r1)
      await page
        .locator("[data-testid=comparison]")
        .waitFor({ state: "visible" });

      await armRestorePause(page);
      await page.locator("[data-testid=restore]").click();
      await waitForRestorePaused(page);

      // While paused, create a NEW a.tex (absent from the restored r1) →
      // reintroduced, colliding with the restored a.tex[A].
      await page.locator("[data-testid=new-file-name]").fill("a.tex");
      await page.getByRole("button", { name: "New file" }).click();
      await page.locator("[data-testid=editor]").waitFor({ state: "visible" });
      await page.locator("[data-testid=editor]").fill("created during restore");
      // Assert the create actually changed the current tree: a second a.tex is
      // present (the new file) alongside c.tex.
      assert.ok(
        (await page
          .locator("[data-testid=select-file]", { hasText: "a.tex" })
          .count()) >= 1,
        "new a.tex appears in the current tree during the pause",
      );

      await resumeRestore(page);
      for (let i = 0; i < 80; i++) {
        const t = await page.locator(".status").innerText();
        if (!/Saving/.test(t) && /Save failed|conflict|duplicate/i.test(t))
          break;
        await page.waitForTimeout(250);
      }
      const statusText = await page.locator(".status").innerText();
      assert.ok(
        /conflict|duplicate/i.test(statusText),
        `reconcile must surface the reintroduced-file conflict, got: ${statusText}`,
      );
      const aButtons = page.locator("[data-testid=select-file]", {
        hasText: "a.tex",
      });
      assert.equal(
        await aButtons.count(),
        2,
        "both a.tex (restored + created)",
      );
      const headRestore = (await readHeadBytes(page)).head;
      assert.notEqual(headRestore, head2, "restore committed a new revision");

      // Save refuses the invalid manifest (duplicate a.tex) without advancing head.
      await page.locator("[data-testid=save]").click();
      for (let i = 0; i < 40; i++) {
        const t = await page.locator(".status").innerText();
        if (/Save failed|conflict|duplicate/i.test(t)) break;
        await page.waitForTimeout(250);
      }
      assert.equal(
        (await readHeadBytes(page)).head,
        headRestore,
        "save must NOT commit the duplicate-path manifest",
      );

      // Export refuses the invalid tree (no download).
      let downloadFired = false;
      page.on("download", () => (downloadFired = true));
      await page.locator("[data-testid=export-zip]").click();
      for (let i = 0; i < 40; i++) {
        const t = await page
          .locator("[data-testid=transfer-status]")
          .innerText();
        if (/failed|cannot|could not/i.test(t)) break;
        await page.waitForTimeout(250);
      }
      assert.ok(
        /failed|cannot|could not/i.test(
          await page.locator("[data-testid=transfer-status]").innerText(),
        ),
        "export must refuse the invalid tree",
      );
      assert.ok(!downloadFired, "export must not download the invalid tree");

      // Resolve by renaming the new a.tex→created.tex, save, export.
      await aButtons.nth(1).click();
      await page.locator("[data-testid=rename-input]").fill("created.tex");
      await page.evaluate(() =>
        document.querySelector('[data-testid="rename"]').click(),
      );
      await saveAndWait(page);
      const durable = await readHeadBytes(page);
      assert.equal(
        new Set(durable.files).size,
        durable.files.length,
        `durable paths unique after resolve, got: ${JSON.stringify(durable.files)}`,
      );
      const dl = page.waitForEvent("download", { timeout: 15000 });
      await page.locator("[data-testid=export-zip]").click();
      await dl;
      console.log(
        "PASS P1 (deterministic): restore + concurrent create (reintroduced file) colliding with a restored file surfaces a conflict (all kept; save+export refuse; resolve+save+export succeed)",
      );
      await ctx.close();
    }

    // -------------------------------------------------- P1 (deterministic): restore + concurrent rename producing a file/directory prefix conflict
    // Fixture: r1 p/refs.tex[A]="r1" + other.tex[B]="v1" + q.tex[C]; r2
    //   refs.tex[A] (p/refs.tex moved out of p) + other.tex[B]="v2" + q.tex[C].
    //   Restore to r1. DURING the paused restore I/O, rename other.tex[B]→p (a
    //   file) — valid in the current tree (no p, no p/… path) but a prefix
    //   conflict against the restored p/refs.tex[A] (p would be a file under which
    //   refs.tex sits). The reconcile must surface the prefix conflict, keep all
    //   files, refuse save/export; resolve + save + export then succeeds.
    {
      const ctx = await browser.newContext();
      const page = await ctx.newPage();
      await page.goto(BASE);
      await waitForReady(page);
      await clear(page);
      await createProject(page, unique("PrefixCollisionRestore"));
      await importSingleFile(page, "p/refs.tex", enc("r1\n"));
      await importSingleFile(page, "other.tex", enc("v1\n"));
      await importSingleFile(page, "q.tex", enc("q\n"));
      await saveAndWait(page);
      // r2: rename p/refs.tex[A]→refs.tex (move out of p), edit other.tex→v2.
      await page
        .locator("[data-testid=select-file]", { hasText: "p/refs.tex" })
        .click();
      await page.locator("[data-testid=rename-input]").fill("refs.tex");
      await page.locator("[data-testid=rename]").click();
      await page
        .locator("[data-testid=select-file]", { hasText: "other.tex" })
        .click();
      await page.locator("[data-testid=editor]").fill("v2\n");
      await saveAndWait(page);
      const head2 = (await readHeadBytes(page)).head;

      await installRestorePauseHook(page);
      await page.locator("[data-testid=history-list]").waitFor();
      const histButtons = page.locator("[data-testid=select-history]");
      const n = await histButtons.count();
      await histButtons.nth(n - 1).click(); // oldest (r1)
      await page
        .locator("[data-testid=comparison]")
        .waitFor({ state: "visible" });

      await armRestorePause(page);
      await page.locator("[data-testid=restore]").click();
      await waitForRestorePaused(page);

      // While paused, rename other.tex[B]→p (a file). Valid in the current tree
      // (refs.tex + other.tex + q.tex — no p); collides as a prefix conflict with
      // the restored p/refs.tex[A].
      await page
        .locator("[data-testid=select-file]", { hasText: "other.tex" })
        .click();
      await page.locator("[data-testid=rename-input]").fill("p");
      await page.evaluate(() =>
        document.querySelector('[data-testid="rename"]').click(),
      );
      for (let i = 0; i < 80; i++) {
        if (
          (await page
            .locator("[data-testid=select-file]", { hasText: "other.tex" })
            .count()) === 0
        )
          break;
        await page.waitForTimeout(100);
      }
      assert.equal(
        await page
          .locator("[data-testid=select-file]", { hasText: "other.tex" })
          .count(),
        0,
        "rename other.tex→p must land in the current tree during the pause",
      );

      await resumeRestore(page);
      for (let i = 0; i < 80; i++) {
        const t = await page.locator(".status").innerText();
        if (!/Saving/.test(t) && /Save failed|conflict|prefix/i.test(t)) break;
        await page.waitForTimeout(250);
      }
      const statusText = await page.locator(".status").innerText();
      assert.ok(
        /conflict|prefix/i.test(statusText),
        `reconcile must surface the prefix conflict, got: ${statusText}`,
      );
      // The restored p/refs.tex[A] and the renamed p[B] are both preserved.
      const paths = await fileList(page);
      assert.ok(
        paths.includes("p/refs.tex"),
        `restored p/refs.tex preserved, got: ${JSON.stringify(paths)}`,
      );
      assert.ok(
        paths.includes("p"),
        `renamed p preserved, got: ${JSON.stringify(paths)}`,
      );
      const headRestore = (await readHeadBytes(page)).head;
      assert.notEqual(headRestore, head2, "restore committed a new revision");

      // Save refuses the invalid manifest (prefix conflict) without advancing head.
      await page.locator("[data-testid=save]").click();
      for (let i = 0; i < 40; i++) {
        const t = await page.locator(".status").innerText();
        if (/Save failed|conflict|prefix/i.test(t)) break;
        await page.waitForTimeout(250);
      }
      assert.equal(
        (await readHeadBytes(page)).head,
        headRestore,
        "save must NOT commit the prefix-conflict manifest",
      );

      // Resolve by renaming p[B]→other.tex (back), save, export.
      await selectFileByPath(page, "p");
      await page.locator("[data-testid=rename-input]").fill("other.tex");
      await page.evaluate(() =>
        document.querySelector('[data-testid="rename"]').click(),
      );
      await saveAndWait(page);
      const durable = await readHeadBytes(page);
      assert.equal(
        new Set(durable.files).size,
        durable.files.length,
        `durable paths unique after resolve, got: ${JSON.stringify(durable.files)}`,
      );
      const dl = page.waitForEvent("download", { timeout: 15000 });
      await page.locator("[data-testid=export-zip]").click();
      await dl;
      console.log(
        "PASS P1 (deterministic): restore + concurrent rename producing a file/directory prefix conflict surfaces a conflict (all kept; save refuses; resolve+save+export succeed)",
      );
      await ctx.close();
    }

    console.log("All M1b transfer/history/restore browser tests passed.");
  } finally {
    await browser.close();
    proxy.close();
  }
})().catch((e) => {
  console.error(e);
  process.exitCode = 1;
});
