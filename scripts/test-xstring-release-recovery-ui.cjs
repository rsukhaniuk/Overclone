// Real old-to-current publication recovery in pinned Chromium.
const assert = require("node:assert/strict");
const { createReadStream, readFileSync, mkdtempSync, rmSync } = require("node:fs");
const http = require("node:http");
const os = require("node:os");
const path = require("node:path");
const H = require("./review-ui-helpers.cjs");
const { $ } = H;
const { chromium } = require("playwright");
const profile = mkdtempSync(path.join(os.tmpdir(), "oop-upgrade-"));
const roots = [process.env.APP_A, process.env.APP_B];
assert.ok(roots.every(Boolean), "Set APP_A and APP_B to the old and current published wwwroot directories.");
const versions = roots.map(root => readFileSync(path.join(root, "service-worker-assets.js"), "utf8").match(/"version": "([^"]+)"/)[1]);
const project = "Exact xstring update fixture";
const checkFontAliases = process.env.CHECK_FONT_ALIASES === "1";
const fontDeclarations = checkFontAliases ? String.raw`\setromanfont{Times New Roman}
\setsansfont{Arial}` : "";
const preamble = checkFontAliases && process.env.PREAMBLE_FILE ? readFileSync(process.env.PREAMBLE_FILE, "utf8")
  : String.raw`\usepackage{geometry,setspace,indentfirst,booktabs,array,tabularx,hyperref}
\usepackage{xstring}
\usepackage{fancyhdr}
${fontDeclarations}
`;
const fontText = checkFontAliases ? String.raw`
\par Serif regular.\par \textbf{Serif bold.}\par \textit{Serif italic.}\par
\textbf{\textit{Serif bold italic.}}\par
{\sffamily Sans regular.\par \textbf{Sans bold.}\par \textit{Sans italic.}\par
\textbf{\textit{Sans bold italic.}}}\par
Привіт, Київ, Їжак.` : "";
const source = String.raw`\documentclass[14pt,a4paper]{extarticle}
\usepackage{fontspec}
\setmainfont{TeX Gyre Termes}
${checkFontAliases ? String.raw`\input{preamble}` : preamble}
\begin{document}
\IfStrEq{same}{same}{Xstring after update.}{Unexpected result.}
${fontText}
\end{document}
`;
let stage = 0, context, page, archiveRequests = 0;
const types = { ".html": "text/html", ".js": "text/javascript", ".mjs": "text/javascript",
  ".wasm": "application/wasm", ".json": "application/json", ".css": "text/css" };
const server = http.createServer((request, response) => {
  const url = new URL(request.url, "http://127.0.0.1");
  const name = url.pathname === "/" ? "index.html" : url.pathname.slice(1);
  if (name === "bundled-runtime/compiler-suite.zip") archiveRequests++;
  const file = path.join(roots[stage], name);
  const stream = createReadStream(file);
  stream.on("open", () => {
    response.writeHead(200, { "Content-Type": types[path.extname(file)] || "application/octet-stream", "Cache-Control": "no-cache" });
    stream.pipe(response);
  });
  stream.on("error", () => { if (!response.headersSent) response.writeHead(404).end(); });
  response.on("close", () => stream.destroy());
});
async function revealReadyCompiler(target) {
  const settings = target.getByText("Compiler settings", { exact: true });
  await settings.click();
  await target.waitForFunction(() => {
    const choice = document.querySelector('select[aria-label="Compile engine"] option[value="xelatex"]');
    return !!choice && !choice.disabled;
  }, null, { timeout: 90000 });
  return settings;
}
async function compile(target, outcome, engine = "xelatex") {
  if (engine) {
    const settings = await revealReadyCompiler(target);
    await target.getByRole("combobox", { name: "Compile engine", exact: true }).selectOption(engine, { timeout: 150000 });
    await settings.click();
  }
  const previous = await $(target, "compile-details").textContent();
  await $(target, "compile").click();
  console.log("compiling, expecting", outcome);
  await target.waitForFunction(prior => {
    const detail = document.querySelector("[data-testid=compile-details]")?.textContent;
    return detail !== prior && /Outcome: [A-Za-z]+;/.test(detail ?? "") &&
      document.querySelector("[data-testid=compile-work-status]")?.textContent.trim() === "Idle";
  }, previous, { timeout: 120000 });
  assert.match(await $(target, "compile-details").textContent(), new RegExp(`Outcome: ${outcome};`));
  return $(target, "compiler-log").textContent();
}
async function controller(target, version) {
  await target.waitForFunction(async expected => {
    const cache = (await caches.keys()).filter(key => key.startsWith("oop-workspace-release-"));
    return navigator.serviceWorker.controller?.state === "activated" &&
      cache.length === 1 && cache[0] === "oop-workspace-release-" + expected;
  }, version, { timeout: 120000 });
}
(async () => {
  await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
  const origin = "http://127.0.0.1:" + server.address().port;
  context = await chromium.launchPersistentContext(profile, { headless: true, viewport: { width: 1440, height: 900 } });
  page = context.pages()[0] || await context.newPage();
  await page.goto(origin);
  await H.ready(page);
  await H.reveal(page, page.locator("#new-project"));
  await page.locator("#new-project").fill(project);
  await page.getByRole("button", { name: "Create project", exact: true }).click();
  await $(page, "starter-article").click();
  await $(page, "editor").fill(source);
  await page.getByRole("button", { name: "Save source", exact: true }).click();
  await page.getByText("Source saved", { exact: true }).waitFor({timeout: 45000});
  if (checkFontAliases) {
    await $(page, "import-input").setInputFiles({ name: "preamble.tex", mimeType: "text/plain", buffer: Buffer.from(preamble) });
    await page.locator(".source-tab", { hasText: "preamble.tex" }).waitFor();
    await page.getByRole("button", { name: "Save source", exact: true }).click();
    await page.getByText("Source saved", { exact: true }).waitFor({timeout: 45000});
    await H.assertBytes(page, project, Buffer.from(preamble), "preamble.tex");
  }
  const saved = await H.assertBytes(page, project, Buffer.from(source));
  await controller(page, versions[0]);
  console.log("older worker active and source saved");
  const oldLog = await compile(page, "CompilerFailed");
  assert.match(oldLog, checkFontAliases
    ? /preamble\.tex:\d+: Package fontspec Error: The font "Times New Roman"/
    : /File `xstring\.sty' not found/);
  console.log("reproduced the reported compile error on", versions[0]);

  const parked = await context.newPage();
  await parked.goto(origin);
  await H.ready(parked);
  await controller(parked, versions[0]);
  stage = 1;
  // Access the recovery route from the previous worker's controlled tab.
  const recovery = await page.goto(origin + "/recover-release.html?release-recovery=2");
  assert.equal(recovery.status(), 200);
  await page.getByRole("heading", { name: "Refresh Overclone", exact: true }).waitFor();
  const refresh = page.getByRole("button", { name: "Open current build", exact: true });
  await refresh.click();
  await page.getByText(/Close the other Overclone tabs/).waitFor();
  assert.equal((await page.evaluate(async () =>
    (await navigator.serviceWorker.getRegistration("/"))?.active?.state)), "activated");
  const unrelated = await parked.evaluate(async version => {
    const waiting = (await navigator.serviceWorker.getRegistration("/"))?.waiting;
    if (!waiting) return "no waiting worker";
    return await new Promise(resolve => {
      const channel = new MessageChannel();
      const timer = setTimeout(() => resolve("no response"), 3000);
      channel.port1.onmessage = event => {
        clearTimeout(timer);
        channel.port1.close();
        resolve(event.data);
      };
      waiting.postMessage({ type: "release-recovery", version, activate: true }, [channel.port2]);
    });
  }, versions[1]);
  assert.equal(unrelated, "unavailable");
  assert.deepEqual(await H.head(parked, project), saved);
  await parked.close();
  await Promise.all([
    page.waitForURL(origin + "/"),
    refresh.click(),
  ]);
  await H.ready(page);
  await controller(page, versions[1]);
  await H.open(page, project);
  assert.deepEqual(await H.head(page, project), saved);
  if (checkFontAliases) await H.assertBytes(page, project, Buffer.from(preamble), "preamble.tex");
  if (checkFontAliases) {
    assert.match(await compile(page, "CompilerFailed", "lualatex"), /File `xstring\.sty' not found/);
    await $(page, "compile-error").getByRole("button", { name: "Use XeLaTeX" }).click();
  }
  const newLog = await compile(page, "Succeeded", checkFontAliases ? null : "xelatex");
  assert.match(newLog, /\/tex\/xstring\.sty/);
  assert.match(newLog, /\/tex\/xstring\.tex/);
  await page.waitForFunction(() => document.querySelector("[data-testid=page-text]")?.textContent.includes("Xstring after update."), null, { timeout: 45000 });
  if (checkFontAliases) {
    const settings = page.getByText("Compiler settings", { exact: true });
    await settings.click();
    assert.match(await $(page, "xetex-font-aliases").innerText(), /Liberation Serif.*Times New Roman.*Liberation Sans.*Arial/);
    await settings.click();
  }
  assert.deepEqual(await H.head(page, project), saved);
  console.log("The recovery page loaded the corrected compiler and rendered the unchanged project.");

  const provisionedRequests = archiveRequests;
  await page.reload();
  await H.ready(page);
  await H.open(page, project);
  const started = Date.now();
  await revealReadyCompiler(page);
  assert.equal(archiveRequests, provisionedRequests, "Warm reload must reuse the cached archive.");
  assert.equal((await $(page, "compile-work-status").textContent()).trim(), "Idle");
  assert.deepEqual(await H.head(page, project), saved);
  console.log("Cached compiler ready after reload in", Date.now() - started, "ms.");

  await context.close();
  context = null;
  await new Promise(resolve => { server.close(resolve); server.closeAllConnections(); });
  await assert.rejects(fetch(origin));
  context = await chromium.launchPersistentContext(profile, { headless: true, viewport: { width: 1440, height: 900 } });
  await context.setOffline(true);
  page = context.pages()[0] || await context.newPage();
  await page.goto(origin);
  await H.ready(page);
  await controller(page, versions[1]);
  await H.open(page, project);
  assert.deepEqual(await H.head(page, project), saved);
  if (checkFontAliases) await H.assertBytes(page, project, Buffer.from(preamble), "preamble.tex");
  assert.match(await compile(page, "Succeeded"), /\/tex\/xstring\.sty/);
  if (checkFontAliases) {
    await page.waitForFunction(() => document.querySelector("[data-testid=page-text]")?.textContent.includes("Sans bold italic."));
    if (process.env.RESULT_DIR) {
      const [download] = await Promise.all([page.waitForEvent("download"), $(page, "download-pdf").click()]);
      await download.saveAs(path.join(process.env.RESULT_DIR, "compatible-fonts-offline.pdf"));
    }
  }
  console.log("Cold offline reopen and fresh compile preserved the original saved revision.");
})().catch(async error => {
  const log = page && !page.isClosed() ? await $(page, "compiler-log").textContent({ timeout: 2000 }).catch(() => "") : "";
  const status = page && !page.isClosed() ? await page.evaluate(() =>
    Object.fromEntries(["compile-status", "compile-work-status", "compiler-readiness"].map(id => [id, document.querySelector('[data-testid="' + id + '"]')?.textContent]))).catch(() => null) : null;
  console.error(error, status, log);
  process.exitCode = 1;
}).finally(async () => {
  if (context) await context.close();
  server.close(); server.closeAllConnections();
  rmSync(profile, { recursive: true, force: true });
});
