// Explicit live local-service acceptance; never part of offline/default tests.
// Run with the cached browser image on the host network, a read-only static
// publication, OLLAMA_MODEL and RESULT_DIR. No response interception or keys.
// Optional APP_URL verifies an existing canonical loopback origin instead of
// starting a test server; its served manifest must match the supplied publication.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const http = require("node:http");
const { createHash } = require("node:crypto");
const { chromium } = require("playwright");
const H = require("./review-ui-helpers.cjs");
const { $, reveal } = H;
const root = fs.realpathSync(process.env.PUBLISHED_WWWROOT);
const output = process.env.RESULT_DIR;
const model = process.env.OLLAMA_MODEL;
const existingOrigin = process.env.APP_URL;
if (existingOrigin) {
  const u = new URL(existingOrigin);
  assert.ok(["http:", "https:"].includes(u.protocol));
  assert.equal(existingOrigin, u.origin, "APP_URL must be a canonical origin");
  assert.ok(["localhost", "127.0.0.1", "[::1]"].includes(u.hostname), "This harness targets local preview only; use the deployed runner for HTTPS acceptance");
}
assert.ok(output && model, "Explicit result directory and installed local model required");
fs.mkdirSync(output, { recursive: true });
const sha = bytes => createHash("sha256").update(bytes).digest("hex");
const source = "\\documentclass{article}\n\\begin{document}\nSynthetic ALPHA. Україна🙂\n\\end{document}\n";
const expected = source.replace("ALPHA", "BETA");
const receipt = { passed: false, syntheticContextOnly: true, model, started: new Date().toISOString(),
  publishedIndexSha256: sha(fs.readFileSync(path.join(root, "index.html"))),
  manifestVersion: fs.readFileSync(path.join(root, 'service-worker-assets.js'), 'utf8').match(/"version":\s*"([^"\n]+)"/)?.[1],
  harnessSha256: sha(fs.readFileSync(__filename)),
  existingOrigin: !!existingOrigin, phases: [], errors: [], externalRequests: [] };
const mime = { ".html": "text/html", ".js": "text/javascript", ".wasm": "application/wasm", ".css": "text/css", ".json": "application/json", ".svg": "image/svg+xml", ".woff2": "font/woff2", ".webmanifest": "application/manifest+json" };
const server = http.createServer((request, response) => {
  let file;
  try {
    const relative = decodeURIComponent(new URL(request.url, "http://localhost").pathname);
    file = fs.realpathSync(path.join(root, relative === "/" ? "index.html" : relative));
    assert.ok(file.startsWith(root + path.sep) && fs.statSync(file).isFile());
  } catch { response.writeHead(404); response.end(); return; }
  response.writeHead(200, { "Content-Type": mime[path.extname(file)] || "application/octet-stream", "Cache-Control": "no-cache" });
  fs.createReadStream(file).pipe(response);
});
let browser;
async function phase(name, action) {
  receipt.phases.push({ name, status: "running" });
  await action(); receipt.phases.at(-1).status = "passed";
}
(async () => {
  try {
    if (existingOrigin) receipt.origin = existingOrigin;
    else {
      await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
      receipt.origin = "http://localhost:" + server.address().port;
    }
    browser = await chromium.launch({ headless: true });
    const context = await browser.newContext(); const page = await context.newPage();
    const requests = [], catalogRequests = [];
    page.on("pageerror", error => receipt.errors.push(String(error).slice(0, 500)));
    page.on("request", request => {
      const url = new URL(request.url());
      if (url.origin === "http://localhost:11434" && url.pathname === "/api/chat" && request.method() === "POST")
        requests.push(JSON.parse(request.postData()));
      else if (url.origin === "http://localhost:11434" && url.pathname === "/api/tags")
        catalogRequests.push({ method: request.method(), body: request.postData() });
      else if (["http:", "https:"].includes(url.protocol) && url.origin !== receipt.origin && url.origin !== "http://localhost:11434")
        receipt.externalRequests.push(url.origin);
    });
    await page.goto(receipt.origin); await H.ready(page);
    const servedManifest = await page.request.get(receipt.origin + '/service-worker-assets.js');
    assert.equal(await servedManifest.text(), fs.readFileSync(path.join(root, 'service-worker-assets.js'), 'utf8'), 'actual origin serves the supplied immutable publication');
    const name = "Synthetic live Ollama review " + Date.now();
    await phase("save synthetic project", async () => {
      await reveal(page, page.locator("#new-project")); await page.locator("#new-project").fill(name);
      await page.getByRole("button", { name: "Create project", exact: true }).click();
      await $(page, "import-input").setInputFiles({ name: "main.tex", mimeType: "text/plain", buffer: Buffer.from(source) });
      await page.locator(".editor-toolbar .source-tab", { hasText: "main.tex" }).waitFor(); await H.save(page);
    });
    const before = await H.head(page, name);
    await phase("disclose exact local context without sending", async () => {
      await reveal(page, $(page, "ai-discover-models"));
      assert.equal(catalogRequests.length, 0);
      await $(page, "ai-discover-models").click();
      const models = $(page, "ai-installed-models");
      await models.selectOption(model);
      receipt.installedModelCount = await models.locator('option[value]:not([value=""])').count();
      assert.deepEqual(catalogRequests, [{ method: "GET", body: null }]);
      await $(page, "ai-connect").click(); await $(page, "ai-destination").waitFor();
      await page.getByRole("button", { name: "Prepare active-file context", exact: true }).click();
      await $(page, "ai-context").waitFor(); assert.equal(await $(page, "ai-context").textContent(), source);
      await $(page, "ai-panel").locator('input[type="checkbox"]').check();
      await $(page, "ai-panel").locator("textarea").fill("Replace only ALPHA with BETA. Preserve every other character and line ending of the supplied file. Return the prescribed JSON object only.");
      assert.equal(requests.length, 0);
    });
    const payload = await $(page, "ai-payload").textContent();
    await phase("one real model proposal remains unapplied", async () => {
      await page.getByRole("button", { name: "Send disclosed request", exact: true }).click();
      await $(page, "ai-answer").waitFor({ timeout: 100000 });
      const proposal = JSON.parse(await $(page, "ai-answer").textContent());
      assert.equal(proposal.replacementText, expected); assert.ok(proposal.explanation);
      assert.equal(requests.length, 1); assert.equal(requests[0].stream, false);
      assert.equal(requests[0].messages.length, 1); assert.equal(requests[0].messages[0].content, payload);
      assert.deepEqual(await H.head(page, name), before); assert.equal(await $(page, "editor").inputValue(), source);
    });
    await phase("preview explicit acceptance and durable undo", async () => {
      await page.getByRole("button", { name: "Review proposed edits", exact: true }).click();
      await $(page, "review-preview").waitFor(); await H.idle(page);
      assert.deepEqual(await H.head(page, name), before);
      await reveal(page, $(page, "review-accept")); await $(page, "review-accept").click(); await H.action(page);
      const applied = await H.assertBytes(page, name, Buffer.from(expected), "main.tex"); assert.equal(applied.parent, before.id);
      await H.undo(page); const undone = await H.assertBytes(page, name, Buffer.from(source), "main.tex"); assert.equal(undone.parent, applied.id);
      receipt.appliedHash = sha(Buffer.from(expected)); receipt.restoredHash = sha(Buffer.from(source));
    });
    await phase("disconnect clears memory and leaves source usable", async () => {
      await page.getByRole("button", { name: "Disconnect and clear memory", exact: true }).click();
      await $(page, "ai-context").waitFor({ state: "detached" }); await $(page, "ai-answer").waitFor({ state: "detached" });
      assert.equal(await $(page, "editor").inputValue(), source); assert.equal(requests.length, 1);
    });
    assert.deepEqual(receipt.externalRequests, []); assert.deepEqual(receipt.errors, []); receipt.passed = true;
    console.log("PASS live Ollama proposal, explicit review/accept/save/undo and memory disconnect on " + receipt.origin);
  } catch (error) {
    receipt.error = String(error).slice(0, 1200); console.error(error); process.exitCode = 1;
  } finally {
    if (browser) await browser.close(); server.closeAllConnections(); if (server.listening) await new Promise(resolve => server.close(resolve));
    receipt.finished = new Date().toISOString(); fs.writeFileSync(path.join(output, "receipt.json"), JSON.stringify(receipt, null, 2) + "\n");
  }
})();
