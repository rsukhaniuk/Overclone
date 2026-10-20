// Workspace persistence, concurrency and recovery browser tests.
//
// These tests drive the REAL application in a secure context. OPFS, IndexedDB
// and Web Locks require a secure context (HTTPS or localhost/127.0.0.1). The
// app container only speaks http://app:8080 (not a secure context), so we run
// a tiny localhost HTTP proxy inside the test container and point Chromium at
// http://127.0.0.1:8090, which is a secure context for storage APIs.
//
// Coverage:
//   - create project, edit, persist, reload (survives browser close)
//   - Ukrainian, emoji, combining marks, CRLF and zero-length byte preservation
//   - offline cold start after provisioning (static host reachable only via cache)
//   - save-failure injection: no false "saved", dirty retained, head not advanced
//   - rapid/overlapping saves preserve latest content and a valid parent chain
//   - two tabs: second is read-only; takeover after the writer releases
//   - corrupt content: explicit recovery from a complete checkpoint

const assert = require("node:assert/strict");
const http = require("node:http");
const { chromium } = require("playwright");
const { reveal } = require("./review-ui-helpers.cjs");

const UPSTREAM = process.env.APP_URL || "http://app:8080";
const PROXY_PORT = 8090;
const BASE = `http://127.0.0.1:${PROXY_PORT}`;

function startProxy() {
  return http.createServer((clientReq, clientRes) => {
    const url = new URL(clientReq.url, UPSTREAM);
    const headers = { ...clientReq.headers, host: url.host };
    const upstream = http.request(
      url,
      { method: clientReq.method, headers },
      (upRes) => {
        clientRes.writeHead(upRes.statusCode, upRes.headers);
        upRes.pipe(clientRes);
      },
    );
    upstream.on("error", () => {
      clientRes.destroy();
    });
    clientReq.pipe(upstream);
  });
}

function unique(n) {
  return `${n}-${Date.now()}-${Math.random().toString(36).slice(2, 7)}`;
}

async function waitForReady(page) {
  // The .NET WASM app takes a moment to boot. Wait for the visible dashboard
  // or open-project file controls, not the collapsed creation form.
  for (let i = 0; i < 90; i++) {
    try {
      const ok = await page.evaluate(
        () => !!document.querySelector(".project-dashboard, [data-testid=new-file-name]"),
      );
      if (ok) return;
    } catch {
      /* not ready */
    }
    await page.waitForTimeout(500);
  }
  throw new Error("Workspace UI did not become ready");
}

async function createProject(page, name) {
  await reveal(page, page.locator("#new-project"));
  await page.locator("#new-project").fill(name);
  await page.getByRole("button", { name: "Create project" }).click();
  await page.locator("[data-testid=new-file-name]").waitFor({ timeout: 20000 });
}

async function createFile(page, path) {
  await page.locator("[data-testid=new-file-name]").fill(path);
  await page.getByRole("button", { name: "New file" }).click();
  await page.locator("[data-testid=editor]").waitFor({ state: "visible" });
}

async function saveAndWait(page) {
  await page.locator("[data-testid=save]").click();
  await page
    .locator(".status.status-saved, .status.status-failed")
    .waitFor({ state: "attached" });
  // Give the async save round-trip a moment to settle.
  for (let i = 0; i < 40; i++) {
    const t = await page.locator(".status").innerText();
    if (t.startsWith("Saved")) return t;
    if (t.startsWith("Save failed")) return t;
    await page.waitForTimeout(250);
  }
  return page.locator(".status").innerText();
}

(async () => {
  const proxy = startProxy();
  await new Promise((r) => proxy.listen(PROXY_PORT, "127.0.0.1", r));
  const browser = await chromium.launch({ headless: true });
  try {
    // ---------------------------------------------------------------- create/edit/reload
    {
      const ctx = await browser.newContext();
      const page = await ctx.newPage();
      const errors = [];
      page.on("pageerror", (e) => errors.push(String(e)));
      await page.goto(BASE);
      await waitForReady(page);
      // Clean any previous storage from a prior run in this origin.
      await page.evaluate(async () => {
        const m = await import("./js/workspace-storage.js");
        await m.clearAll();
      });
      await page.reload();
      await waitForReady(page);

      const name = unique("Diploma");
      await createProject(page, name);

      // Editor path: Ukrainian, emoji, combining marks, ZWJ. Newlines here are
      // \n (the textarea normalizes CRLF to LF, tested separately via import).
      await createFile(page, "main.tex");
      const editorContent =
        "Привіт, світ! 🇺🇦 é\u0301 かな 🎉\nLF line\n\u200b末";
      await page.locator("[data-testid=editor]").fill(editorContent);
      const st = await saveAndWait(page);
      assert.ok(st.startsWith("Saved"), `expected Saved, got: ${st}`);

      // Import path: exact bytes including CRLF, combining marks and an
      // empty-ish file are preserved because InputFile reads raw bytes.
      const crlfContent = "CRLF line\r\nSecond line 🇺🇦 é\u0301\r\n";
      const crlfBytes = Array.from(new TextEncoder().encode(crlfContent));
      await page.locator("[data-testid=import-input]").setInputFiles({
        name: "crlf.tex",
        mimeType: "text/plain",
        buffer: Buffer.from(crlfContent),
      });
      await page
        .locator("[data-testid=select-file]", { hasText: "crlf.tex" })
        .waitFor({ state: "visible" });
      const st2 = await saveAndWait(page);
      assert.ok(
        st2.startsWith("Saved"),
        `expected Saved for import, got: ${st2}`,
      );

      // Also create an empty (zero-length) file and persist it.
      await createFile(page, "empty.tex");
      // Leave content empty (no edit) — but CreateFile marks it dirty=false for empty
      // initial content, so force a save cycle by adding then saving.
      const st3 = await saveAndWait(page);
      assert.ok(
        st3.startsWith("Saved"),
        `expected Saved for empty file, got: ${st3}`,
      );

      // Reload the page (simulates close/reopen) and reopen the project.
      await page.reload();
      await waitForReady(page);
      await page
        .locator("[data-testid=project-row]", { hasText: name })
        .waitFor({ timeout: 20000 });
      await page
        .locator("[data-testid=project-row]", { hasText: name })
        .locator("[data-testid=open-project]")
        .click();
      await page.locator("[data-testid=editor]").waitFor({ state: "visible" });

      // Read every file's persisted bytes directly from OPFS and compare.
      const restored = await page.evaluate(async () => {
        const m = await import("./js/workspace-storage.js");
        const projects = await m.listProjects();
        const project = projects[projects.length - 1];
        const rev = await m.getRevision(project.headRevisionId);
        const out = {};
        for (const f of rev.files) {
          const bytes = await m.readContent(f.contentHash);
          out[f.relativePath] = {
            bytes: Array.from(bytes),
            length: f.byteLength,
          };
        }
        return out;
      });
      const enc = (s) => Array.from(new TextEncoder().encode(s));
      assert.deepEqual(
        restored["main.tex"].bytes,
        enc(editorContent),
        "main.tex bytes must match (Ukrainian/emoji/LF)",
      );
      assert.deepEqual(
        restored["crlf.tex"].bytes,
        crlfBytes,
        "crlf.tex bytes must be exact (CRLF preserved)",
      );
      assert.equal(
        restored["empty.tex"].bytes.length,
        0,
        "empty.tex must persist zero bytes",
      );
      assert.deepEqual(errors, [], "No uncaught page errors");
      console.log(
        "PASS: persistence + Unicode/CRLF/zero-length byte preservation across reload",
      );
      await ctx.close();
    }

    // ---------------------------------------------------------------- offline cold start
    {
      const ctx = await browser.newContext();
      const page = await ctx.newPage();
      await page.goto(BASE);
      await waitForReady(page);
      const name = unique("Offline");
      await createProject(page, name);
      await createFile(page, "a.tex");
      await page.locator("[data-testid=editor]").fill("offline body");
      await saveAndWait(page);
      // Provision the shell via the service worker, then go offline. Wait until
      // the runtime cache is actually warmed (window.__oopOfflineReady), not
      // merely until a worker is registered — otherwise the first load's
      // _framework assets were fetched before the worker controlled the page
      // and would be missing offline.
      await page.waitForFunction(
        () => window.__oopOfflineReady === true,
        null,
        { timeout: 30000 },
      );
      await ctx.setOffline(true);
      // Reload while offline: the SW must serve the cached shell and storage must work.
      await page.goto(BASE, { waitUntil: "domcontentloaded" });
      await waitForReady(page);
      await page
        .locator("[data-testid=project-row]", { hasText: name })
        .waitFor({ timeout: 20000 });
      await page
        .locator("[data-testid=project-row]", { hasText: name })
        .locator("[data-testid=open-project]")
        .click();
      await page.locator("[data-testid=editor]").waitFor({ state: "visible" });
      const text = await page.locator("[data-testid=editor]").inputValue();
      assert.equal(
        text,
        "offline body",
        "Offline reload must restore saved content",
      );
      // Edit while offline and save; it must persist locally (no network).
      await page.locator("[data-testid=editor]").fill("edited offline");
      await saveAndWait(page);
      await ctx.setOffline(false);
      const after = await page.evaluate(async () => {
        const m = await import("./js/workspace-storage.js");
        const projects = await m.listProjects();
        const project = projects[projects.length - 1];
        const rev = await m.getRevision(project.headRevisionId);
        const bytes = await m.readContent(rev.files[0].contentHash);
        return new TextDecoder().decode(bytes);
      });
      assert.equal(
        after,
        "edited offline",
        "Offline edit must be durably stored",
      );
      console.log("PASS: offline cold start + offline edit/save");
      await ctx.close();
    }

    // ---------------------------------------------------------------- save-failure injection
    {
      const ctx = await browser.newContext();
      const page = await ctx.newPage();
      await page.goto(BASE);
      await waitForReady(page);
      await createProject(page, unique("Fail"));
      await createFile(page, "main.tex");
      await page.locator("[data-testid=editor]").fill("initial good");
      await saveAndWait(page);

      // Capture the durable head before the failing save attempt.
      const headBefore = await page.evaluate(async () => {
        const m = await import("./js/workspace-storage.js");
        const projects = await m.listProjects();
        const project = projects[projects.length - 1];
        return {
          head: project.headRevisionId,
          content: await m
            .readContent(
              (await m.getRevision(project.headRevisionId)).files[0]
                .contentHash,
            )
            .then((b) => new TextDecoder().decode(b)),
        };
      });

      // Inject a one-shot failure into content verification: make the next
      // crypto.subtle.digest call reject. writeContent re-reads and digests the
      // stored blob before returning, so this makes the save fail AFTER the
      // blob is written but BEFORE the manifest/head transaction runs.
      await page.evaluate(() => {
        const realDigest = SubtleCrypto.prototype.digest;
        SubtleCrypto.prototype.digest = function () {
          SubtleCrypto.prototype.digest = realDigest; // restore after one call
          return Promise.reject(new Error("InjectedStorageFailure"));
        };
      });

      await page.locator("[data-testid=editor]").fill("should not be saved");
      await page.locator("[data-testid=save]").click();
      const failedStatus = await page
        .locator(".status.status-failed")
        .waitFor({ state: "attached" })
        .then(() => page.locator(".status").innerText());
      assert.ok(
        /Save failed/i.test(failedStatus),
        `UI must report failure, got: ${failedStatus}`,
      );

      // The editor must still hold the unsaved text (dirty retained).
      const editorText = await page
        .locator("[data-testid=editor]")
        .inputValue();
      assert.equal(
        editorText,
        "should not be saved",
        "Failed save must retain dirty edits",
      );
      const dirtyVisible = await page.evaluate(
        () => !!document.querySelector(".dirty-dot"),
      );
      assert.ok(
        dirtyVisible,
        "Dirty indicator must remain after a failed save",
      );

      // The durable head must NOT have advanced and must still hold the prior content.
      const headAfter = await page.evaluate(async () => {
        const m = await import("./js/workspace-storage.js");
        const projects = await m.listProjects();
        const project = projects[projects.length - 1];
        return {
          head: project.headRevisionId,
          content: await m
            .readContent(
              (await m.getRevision(project.headRevisionId)).files[0]
                .contentHash,
            )
            .then((b) => new TextDecoder().decode(b)),
        };
      });
      assert.equal(
        headAfter.head,
        headBefore.head,
        "Head revision must not advance on a failed save",
      );
      assert.equal(
        headAfter.content,
        "initial good",
        "Incomplete content must not replace the durable head",
      );

      // Retry succeeds after the failure injection is cleared.
      const st = await saveAndWait(page);
      assert.ok(st.startsWith("Saved"), `Retry save must succeed, got: ${st}`);
      const headRetry = await page.evaluate(async () => {
        const m = await import("./js/workspace-storage.js");
        const projects = await m.listProjects();
        const project = projects[projects.length - 1];
        return {
          head: project.headRevisionId,
          content: await m
            .readContent(
              (await m.getRevision(project.headRevisionId)).files[0]
                .contentHash,
            )
            .then((b) => new TextDecoder().decode(b)),
        };
      });
      assert.notEqual(
        headRetry.head,
        headBefore.head,
        "Head must advance after a successful retry",
      );
      assert.equal(
        headRetry.content,
        "should not be saved",
        "Retried save must persist the latest content",
      );
      console.log(
        "PASS: save-failure injection — no false saved, dirty retained, head not advanced, retry succeeds",
      );
      await ctx.close();
    }

    // ---------------------------------------------------------------- rapid overlapping saves + valid chain
    {
      const ctx = await browser.newContext();
      const page = await ctx.newPage();
      await page.goto(BASE);
      await waitForReady(page);
      await createProject(page, unique("Rapid"));
      await createFile(page, "main.tex");
      const editor = page.locator("[data-testid=editor]");
      for (let i = 0; i < 12; i++) {
        await editor.fill(`revision ${i} Привіт 🎉`);
        await page.locator("[data-testid=save]").click();
        // Immediately keep typing; the semaphore serializes saves.
        await editor.fill(`revision ${i} continued`);
        await page.locator("[data-testid=save]").click();
      }
      // Wait for the queued saves to settle without clicking again (the button is
      // correctly disabled once everything is committed). Poll until the dirty
      // indicator is gone and the status reports Saved.
      for (let i = 0; i < 80; i++) {
        const dirty = await page.evaluate(
          () => !!document.querySelector(".dirty-dot"),
        );
        if (!dirty) break;
        await page.waitForTimeout(250);
      }
      const { finalText, chain } = await page.evaluate(async () => {
        const m = await import("./js/workspace-storage.js");
        const projects = await m.listProjects();
        const project = projects[projects.length - 1];
        let id = project.headRevisionId;
        const chain = [];
        while (id) {
          const rev = await m.getRevision(id);
          chain.push(rev.id);
          id = rev.parentRevisionId;
        }
        const rev = await m.getRevision(project.headRevisionId);
        const bytes = await m.readContent(rev.files[0].contentHash);
        return { finalText: new TextDecoder().decode(bytes), chain };
      });
      assert.equal(
        finalText,
        "revision 11 continued",
        "Latest content must win after overlapping saves",
      );
      // Validate the revision chain is a proper linked list reaching the root:
      // every parent resolves, IDs are unique, and the tail has no parent.
      const ids = new Set(chain);
      assert.equal(ids.size, chain.length, "Revision IDs must be unique");
      assert.ok(
        chain.length >= 2,
        `Chain must have multiple revisions; got ${chain.length}`,
      );
      console.log(
        `PASS: rapid overlapping saves preserve latest content + valid chain (${chain.length} revisions)`,
      );
      await ctx.close();
    }

    // ---------------------------------------------------------------- deletion during save is not lost
    {
      const ctx = await browser.newContext();
      const page = await ctx.newPage();
      await page.goto(BASE);
      await waitForReady(page);
      await createProject(page, unique("DeleteDuringSave"));
      await createFile(page, "keep.tex");
      await page.locator("[data-testid=editor]").fill("keep me");
      await saveAndWait(page);

      // Start a save, then delete the file while the save is in flight by
      // injecting a delay into the commit so the deletion lands mid-commit.
      await page.evaluate(async () => {
        const m = await import("./js/workspace-storage.js");
        const orig = m.commitBatch;
        window.__oopCommitBatch = orig;
      });
      // The app holds its own module reference, so we cannot easily patch it.
      // Instead, drive the race at the UI level: type an edit (dirty), click
      // save, and immediately delete the file before the save finishes.
      await page.locator("[data-testid=editor]").fill("keep me edited");
      await page.locator("[data-testid=save]").click();
      // Delete immediately (deletion happens after the save snapshot).
      await page.locator("[data-testid=delete-file]").click();
      // Wait for the in-flight save to settle.
      await page.waitForTimeout(1000);
      // The deletion was NOT part of the committed snapshot, so the workspace
      // must still be dirty (pending deletion), not silently saved/clean.
      const statusAfter = await page.locator(".status").innerText();
      assert.ok(
        /Unsaved changes|Save failed/.test(statusAfter),
        `A deletion made during save must keep the workspace dirty, got: ${statusAfter}`,
      );
      // Now flush the deletion with a second save.
      const st = await saveAndWait(page);
      assert.ok(
        st.startsWith("Saved"),
        `Second save must commit the deletion, got: ${st}`,
      );
      // The durable head revision must no longer contain the file.
      const headFiles = await page.evaluate(async () => {
        const m = await import("./js/workspace-storage.js");
        const projects = await m.listProjects();
        const project = projects[projects.length - 1];
        const rev = await m.getRevision(project.headRevisionId);
        return rev.files.map((f) => f.relativePath);
      });
      assert.ok(
        !headFiles.includes("keep.tex"),
        "The deletion must be durable after the second save; file must be gone from the head",
      );
      // Reopen and confirm the file stays gone (no resurrection).
      await page.reload();
      await waitForReady(page);
      await page
        .locator("[data-testid=project-row]", { hasText: "Delete" })
        .first()
        .waitFor({ timeout: 20000 });
      await page
        .locator("[data-testid=project-row]", { hasText: "Delete" })
        .first()
        .locator("[data-testid=open-project]")
        .click();
      await page
        .locator("[data-testid=new-file-name]")
        .waitFor({ state: "visible" });
      const filesAfterReopen = await page.evaluate(() =>
        Array.from(document.querySelectorAll("[data-testid=select-file]")).map(
          (e) => e.textContent.trim(),
        ),
      );
      assert.ok(
        !filesAfterReopen.includes("keep.tex"),
        "Reopening must not resurrect a deleted file",
      );
      console.log(
        "PASS: deletion made during save is retained until the next save (no silent loss/resurrection)",
      );
      await ctx.close();
    }

    // ---------------------------------------------------------------- two tabs: read-only + takeover
    {
      const ctx = await browser.newContext();
      const page1 = await ctx.newPage();
      const page2 = await ctx.newPage();
      await page1.goto(BASE);
      await waitForReady(page1);
      const name = unique("Tabs");
      await createProject(page1, name);
      await createFile(page1, "main.tex");
      await page1.locator("[data-testid=editor]").fill("tab1 content");
      await saveAndWait(page1);

      // Second tab opens the same project.
      await page2.goto(BASE);
      await waitForReady(page2);
      await page2
        .locator("[data-testid=project-row]", { hasText: name })
        .waitFor({ timeout: 20000 });
      await page2
        .locator("[data-testid=project-row]", { hasText: name })
        .locator("[data-testid=open-project]")
        .click();
      await page2.locator(".banner-readonly").waitFor({ state: "visible" });
      // The editor must be disabled (read-only).
      const disabled = await page2.locator("[data-testid=editor]").isDisabled();
      assert.ok(disabled, "Second tab must open the editor read-only");

      // While page2 is read-only, page1 advances the durable head. The
      // read-only snapshot page2 holds is now stale; takeover must reload it
      // rather than save against the old head (RevisionConflict).
      await page1.locator("[data-testid=editor]").fill("tab1 second save");
      await saveAndWait(page1);

      // First tab releases the writer lock by closing the project.
      await page1.getByRole("button", { name: "Close", exact: true }).click();
      // Give the lock release a moment to propagate.
      await page1.waitForTimeout(500);

      // Second tab takes over.
      await page2
        .getByRole("button", { name: "Take over editing", exact: true })
        .click();
      await page2.locator(".banner-readonly").waitFor({ state: "hidden" });
      const writable = await page2.locator("[data-testid=editor]").isDisabled();
      assert.ok(!writable, "After takeover the editor must be writable");
      // The takeover must have reloaded the latest head ("tab1 second save"),
      // not the stale read-only snapshot ("tab1 content").
      // Permission/banner state changes before TakeOverAsync's awaited load
      // completes. Wait for its content postcondition, not an arbitrary delay.
      await page2.waitForFunction(
        () =>
          document.querySelector("[data-testid=editor]")?.value ===
          "tab1 second save",
        null,
        { timeout: 20000 },
      );
      const afterTakeover = await page2
        .locator("[data-testid=editor]")
        .inputValue();
      assert.equal(
        afterTakeover,
        "tab1 second save",
        "Takeover must reload the latest durable head",
      );
      await page2.locator("[data-testid=editor]").fill("tab2 takeover edit");
      const st = await saveAndWait(page2);
      assert.ok(
        st.startsWith("Saved"),
        `Takeover save must succeed against the reloaded head; got ${st}`,
      );

      // The two tabs must not have silently overwritten each other: reload tab2
      // and confirm only the takeover content survived.
      await page2.reload();
      await waitForReady(page2);
      await page2
        .locator("[data-testid=project-row]", { hasText: name })
        .waitFor({ timeout: 20000 });
      await page2
        .locator("[data-testid=project-row]", { hasText: name })
        .locator("[data-testid=open-project]")
        .click();
      await page2.locator("[data-testid=editor]").waitFor({ state: "visible" });
      const text = await page2.locator("[data-testid=editor]").inputValue();
      assert.equal(
        text,
        "tab2 takeover edit",
        "Takeover content must persist; tab1 never overwrote it",
      );
      console.log("PASS: two-tab single-writer lock + explicit takeover");
      await ctx.close();
    }

    // ---------------------------------------------------------------- close flushes unsaved edits (no silent discard)
    {
      const ctx = await browser.newContext();
      const page = await ctx.newPage();
      await page.goto(BASE);
      await waitForReady(page);
      const name = unique("CloseFlush");
      await createProject(page, name);
      await createFile(page, "main.tex");
      await page.locator("[data-testid=editor]").fill("before close");

      // Slow the save path so an edit can land DURING the flush save: wrap
      // crypto.subtle.digest (used by content verification) to add a delay.
      await page.evaluate(() => {
        const real = SubtleCrypto.prototype.digest;
        SubtleCrypto.prototype.digest = function (...args) {
          return new Promise((resolve, reject) => {
            setTimeout(() => real.apply(this, args).then(resolve, reject), 800);
          });
        };
      });

      // Start Close (flush save #1, slow). Immediately type an edit that
      // lands after save #1's snapshot but before close completes.
      await page.getByRole("button", { name: "Close", exact: true }).click();
      await page.locator("[data-testid=editor]").fill("edited during close");
      // The fixed CloseAsync loops the flush save until clean, so the
      // during-save edit must be committed too. Wait for the project list.
      await page
        .locator(".project-dashboard")
        .waitFor({ state: "visible", timeout: 30000 });

      // Reopen and verify BOTH edits survived (no silent discard).
      await page
        .locator("[data-testid=project-row]", { hasText: name })
        .waitFor({ timeout: 20000 });
      await page
        .locator("[data-testid=project-row]", { hasText: name })
        .locator("[data-testid=open-project]")
        .click();
      await page.locator("[data-testid=editor]").waitFor({ state: "visible" });
      const text = await page.locator("[data-testid=editor]").inputValue();
      assert.equal(
        text,
        "edited during close",
        "Close must flush edits made during the flush save, not discard them",
      );
      console.log(
        "PASS: close flushes unsaved edits incl. edits made during the flush save",
      );
      await ctx.close();
    }

    // ---------------------------------------------------------------- corrupt content recovery
    {
      const ctx = await browser.newContext();
      const page = await ctx.newPage();
      await page.goto(BASE);
      await waitForReady(page);
      const name = unique("Corrupt");
      await createProject(page, name);
      await createFile(page, "main.tex");
      await page.locator("[data-testid=editor]").fill("good revision one");
      await saveAndWait(page);
      // Edit and save a second time to create a second checkpoint.
      await page.locator("[data-testid=editor]").fill("good revision two");
      await saveAndWait(page);

      // Corrupt the head content object in OPFS so verification fails on open.
      await page.evaluate(async () => {
        const m = await import("./js/workspace-storage.js");
        const projects = await m.listProjects();
        const project = projects[projects.length - 1];
        const rev = await m.getRevision(project.headRevisionId);
        const dir = await (
          await navigator.storage.getDirectory()
        ).getDirectoryHandle("content");
        const fh = await dir.getFileHandle(rev.files[0].contentHash);
        const w = await fh.createWritable();
        await w.write(new Uint8Array([0x78, 0x79, 0x7a])); // wrong bytes
        await w.close();
      });

      await page.reload();
      await waitForReady(page);
      await page
        .locator("[data-testid=project-row]", { hasText: name })
        .waitFor({ timeout: 20000 });
      await page
        .locator("[data-testid=project-row]", { hasText: name })
        .locator("[data-testid=open-project]")
        .click();
      // Recovery banner must appear (not a silent saved state).
      await page.locator(".banner-recovery").waitFor({ state: "visible" });
      const status = await page.locator(".status").innerText();
      assert.ok(
        /Recovery|required|damaged/i.test(status),
        `Recovery state must be shown; got: ${status}`,
      );
      // Recover to the first complete checkpoint (newest-first list; pick last).
      const recoverButtons = page.locator("[data-testid=recover]");
      const count = await recoverButtons.count();
      assert.ok(count > 0, "At least one complete checkpoint must be offered");
      await recoverButtons.nth(count - 1).click();
      await page.locator("[data-testid=editor]").waitFor({ state: "visible" });
      const text = await page.locator("[data-testid=editor]").inputValue();
      assert.equal(
        text,
        "good revision one",
        "Recovery must restore the checkpoint's content",
      );
      console.log(
        "PASS: corrupt head produces explicit recovery, restored checkpoint content",
      );
      await ctx.close();
    }

    console.log("All workspace browser tests passed.");
  } finally {
    await browser.close();
    proxy.close();
  }
})().catch((e) => {
  console.error(e);
  process.exitCode = 1;
});
