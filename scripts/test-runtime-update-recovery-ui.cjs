// Real immutable P9 -> P10 release/resource update. Root execution ONLY.
// Oracles are exported for pure Node tests; requiring this file never launches.
"use strict";
const assert = require("node:assert/strict"),
 fs = require("node:fs"),
 path = require("node:path"),
 http = require("node:http");
const { createHash } = require("node:crypto");
const H = require("./review-ui-helpers.cjs"),
 { buildZip } = require("./zip-util.cjs"),
 { validate: validateCsp } = require("./test-csp.cjs");
const sha = (b) => createHash("sha256").update(b).digest("hex");
const PIN = {
 A: {
  profile: 9,
  length: 193549026,
  sha256: "e397b145df4815b10180378f4913933d262c528a6cc7a350451c4f516f0e379d",
 },
 B: {
  profile: 10,
  length: 193561168,
  sha256: "f09f363850d2ebecc18d3c019cdc159a8ea099ae6615fe9547d94a3e19e18bd1",
 },
};
const REQUIRED = [
 "AInstalled",
 "ASourceHistoryPdf",
 "interruptedBRejected",
 "AColdOfflineAfterFailure",
 "dirtyActivationRefused",
 "explicitBRetryAccepted",
 "oldHistoryExactOnB",
 "freshP10CompileNavigation",
 "BColdOfflineFreshCompile",
];
function parseJson(text, label) {
 try {
  return JSON.parse(text);
 } catch (error) {
  throw new Error("Invalid JSON: " + label, { cause: error });
 }
}
function relativeAsset(url) {
 assert.equal(typeof url, "string");
 assert(url.length > 0 && !url.includes("\\") && !/[?#\x00]/.test(url));
 const decoded = decodeURIComponent(url);
 assert(
  !decoded.startsWith("/") &&
   !decoded.split("/").some((p) => p === ".." || p === "." || !p),
 );
 return decoded;
}
function profileTrace(observation, profile) {
 assert([9, 10].includes(profile));
 assert(
  observation && observation.trace.length > 0 && observation.active === 0,
 );
 const phase = profile === 9 ? 14 : 17,
  allowed = new Set([
   `latex:lualatex:${profile}:${phase}`,
   `bibtex:lualatex:${profile}:${phase}`,
  ]);
 // These are TRANSPORT phase tags, not a separate native tool ABI query;
 // factory Run(tool) copies the selected Lua plan14/17 into BOTH tool tags.
 const first = observation.trace[0].tag;
 assert(
  typeof first.job === "string" &&
   first.job.length > 0 &&
   typeof first.request === "string" &&
   first.request.length > 0 &&
   Number.isSafeInteger(first.epoch) &&
   first.epoch > 0,
 );
 let latex = 0,
  bibtex = 0;
 observation.trace.forEach((t, index) => {
  assert(
   t.posted &&
    t.entered &&
    t.terminated &&
    t.outcome === "native-return" &&
    t.nativeReturn === 0,
  );
  assert(
   allowed.has(
    `${t.tag.tool}:${t.tag.engineId}:${t.tag.profile}:${t.tag.version}`,
   ),
   "Unexpected tool/engine/profile/phase tuple",
  );
  assert.equal(t.tag.ordinal, index + 1);
  assert.equal(t.tag.job, first.job);
  assert.equal(t.tag.request, first.request);
  assert.equal(t.tag.epoch, first.epoch);
  if (t.tag.tool === "latex") latex++;
  else {
   assert(index > 0 && observation.trace[index - 1].tag.tool === "latex", "Each BibTeX pass must immediately follow its producing LaTeX pass");
   bibtex++;
  }
 });
 assert(
  latex >= 2 &&
   latex <= 6 &&
   bibtex >= 1 && bibtex <= latex &&
   observation.trace.at(-1).tag.tool === "latex",
  "Bibliography pipeline requires 2..6 LaTeX passes, bounded real BibTeX reruns, and a final LaTeX pass",
 );
 return true;
}
function expectedInverseLine(source, needle, actualLine) {
 const at = source.indexOf(needle);
 assert(
  at >= 0 && source.indexOf(needle, at + 1) < 0,
  "Unique fixture source target",
 );
 const line = source.slice(0, at).split("\n").length;
 if (actualLine !== undefined)
  assert.equal(
   actualLine,
   line,
   "Inverse must hit the independently known fixture source line, not merely any self-consistent returned span",
  );
 return line;
}
function completeCoverage(rows) {
 for (const key of REQUIRED)
  assert.equal(rows[key]?.result, "PASS", "Missing actual PASS scope: " + key);
 return true;
}
function dirtyRefusal(before, after, guidance) {
 assert(before.dirty);
 assert.equal(after.release, before.release);
 assert.equal(after.head, before.head);
 assert.equal(after.value, before.value);
 assert.match(guidance, /save|unsaved|work|draft/i);
 return true;
}
function cutProof(fault, expectedLength) {
 assert.equal(fault.requests, 1);
 assert.equal(fault.expectedLength, expectedLength);
 assert(
  fault.actualPrefixBytes > 0 && fault.actualPrefixBytes < expectedLength,
 );
 assert(fault.connectionDestroyed);
 return true;
}
module.exports = {
 relativeAsset,
 profileTrace,
 expectedInverseLine,
 completeCoverage,
 dirtyRefusal,
 cutProof,
 PIN,
 REQUIRED,
};

async function main() {
 const need = (k) => {
  assert(process.env[k], "Required environment: " + k);
  return process.env[k];
 };
 const out = fs.realpathSync(need("RESULT_DIR"));
 assert.equal(fs.readdirSync(out).length, 0, "NEW empty exclusive RESULT_DIR");
 fs.writeFileSync(
  path.join(out, "exclusive.json"),
  JSON.stringify({ pid: process.pid, time: new Date().toISOString() }),
  { flag: "wx" },
 );
 const receipt = path.join(out, "runtime-update-receipt.json");
 const record = {
  schemaVersion: 1,
  workPackage: "R2-P-runtime-update",
  result: "FAIL",
  step: "preflight",
  activeTab: null,
  harnessSha256: sha(fs.readFileSync(__filename)),
  runtime: { node: process.version },
  publications: {},
  coverage: {},
  actions: [],
  requests: [],
  externalRequests: [],
  pageErrors: [],
  tabs: [],
  failure: null,
  notProved: [
   "Controlled native held I/O/disposal/activation-await race was not forced.",
   "Arbitrary permission loss/quota/eviction/general release security/Chrome+Edge matrix/cloud/licensing are separate.",
   "Dirty activation refusal is tested against a genuinely installed waiting B on explicit retry, not attributed to the earlier rejected install.",
  ],
 };
 let roots = {},
  ident = {},
  activeRelease = "A",
  server,
  ownedServer = false,
  context,
  browser,
  active,
  origin,
  port,
  completed = false,
  retryAllowed = false;
 const sockets = new Set(),
  tabs = new WeakMap();
 const fault = {
  armed: false,
  requests: 0,
  expectedLength: PIN.B.length,
  actualPrefixBytes: 0,
  connectionDestroyed: false,
  relative: "bundled-runtime/compiler-suite.zip",
  sha256: PIN.B.sha256,
 };
 function flush() {
  fs.writeFileSync(receipt, JSON.stringify(record, null, 2) + "\n");
 }
 function step(s, p = active) {
  active = p;
  record.step = s;
  record.activeTab = p ? tabs.get(p) : null;
  flush();
 }
 function pass(key, evidence) {
  record.coverage[key] = { result: "PASS", evidence };
  flush();
 }
 function bounded(p, ms, label) {
  let timer;
  return Promise.race([
   p,
   new Promise((_, reject) => {
    timer = setTimeout(() => reject(new Error(label)), ms);
   }),
  ]).finally(() => clearTimeout(timer));
 }
 function attach(p) {
  if (tabs.has(p)) return;
  const id = "tab-" + (record.tabs.length + 1);
  tabs.set(p, id);
  record.tabs.push({ id, closed: false });
  p.setDefaultTimeout(30000);
  p.on("close", () => (record.tabs.find((t) => t.id === id).closed = true));
  p.on("pageerror", (e) =>
   record.pageErrors.push({ tab: id, message: String(e) }),
  );
 }
 function parsed(s) {
  try {
   return new URL(s);
  } catch {
   return null;
  }
 }
 function external(s) {
  const u = parsed(s);
  return /^https?:/.test(s) && (!u || u.origin !== origin);
 }
 function hashFile(file) {
  const hash = createHash("sha256"),
   fd = fs.openSync(file, "r"),
   b = Buffer.alloc(1024 * 1024);
  try {
   let n;
   while ((n = fs.readSync(fd, b, 0, b.length, null)) > 0)
    hash.update(b.subarray(0, n));
   return hash.digest("hex");
  } finally {
   fs.closeSync(fd);
  }
 }
 function preflightRelease(label, dir, expected) {
  const raw = fs.readFileSync(path.join(dir, "service-worker-assets.js")),
   manifest = parseJson(
    raw.toString().match(/self\.assetsManifest\s*=\s*(\{[\s\S]*\})\s*;/)[1],
    label + " manifest",
   );
  assert.equal(manifest.version, expected);
  const seen = new Set();
  for (const a of manifest.assets) {
   const rel = relativeAsset(a.url);
   assert(!seen.has(rel));
   seen.add(rel);
   const file = fs.realpathSync(path.join(dir, rel));
   assert(file.startsWith(dir + path.sep));
   assert.equal(
    "sha256-" + Buffer.from(hashFile(file), "hex").toString("base64"),
    a.hash,
    `${label} asset ${rel}`,
   );
  }
  const zip = path.join(dir, fault.relative);
  assert.equal(fs.statSync(zip).size, PIN[label].length);
  assert.equal(hashFile(zip), PIN[label].sha256);
  assert(
   manifest.assets.some((a) => relativeAsset(a.url) === fault.relative),
   "Runtime ZIP must be a REQUIRED integrity-checked release asset",
  );
  const client = manifest.assets.find((a) =>
   /^_framework\/Client\.[^/]+\.wasm$/.test(relativeAsset(a.url)),
  );
  assert(client);
  const value = {
   sdkVersion: manifest.version,
   manifestSha256: sha(raw),
   assetCount: manifest.assets.length,
   client: { url: client.url, hash: client.hash },
   runtimeZip: PIN[label],
   workerSha256: hashFile(path.join(dir, "service-worker.js")),
   manifest,
  };
  fs.writeFileSync(
   path.join(out, label + "-verified-assets-manifest.json"),
   JSON.stringify(manifest, null, 2) + "\n",
  );
  record.publications[label] = { ...value, manifest: undefined };
  return value;
 }
 async function start() {
  const mime = {
   ".html": "text/html",
   ".js": "text/javascript",
   ".json": "application/json",
   ".css": "text/css",
   ".wasm": "application/wasm",
   ".svg": "image/svg+xml",
  };
  server = http.createServer((req, res) => {
   let rel, file;
   const label = activeRelease;
   try {
    rel =
     decodeURIComponent(new URL(req.url, origin).pathname).replace(/^\//, "") ||
     "index.html";
    relativeAsset(rel);
    file = fs.realpathSync(path.join(roots[label], rel));
    assert(
     file.startsWith(roots[label] + path.sep) && fs.statSync(file).isFile(),
    );
   } catch {
    res.writeHead(404).end();
    return;
   }
   const request = {
    release: label,
    relative: rel,
    retryAllowed,
    started: new Date().toISOString(),
    expectedLength: fs.statSync(file).size,
    flushedBytes: 0,
    finished: false,
   };
   if (record.requests.length < 5000) record.requests.push(request);
   else record.requestsTruncated = true;
   if (
    label === "B" &&
    rel === fault.relative &&
    fault.armed &&
    !retryAllowed
   ) {
    fault.requests++;
    record.fault = fault;
    flush();
    if (fault.requests !== 1) {
     record.unexpectedAutomaticRuntimeRetry = true;
     flush();
     res.destroy();
     return;
    }
    const fd = fs.openSync(file, "r"),
     prefix = Buffer.alloc(65536);
    fs.readSync(fd, prefix, 0, prefix.length, 0);
    fs.closeSync(fd);
    res.writeHead(200, {
     "Content-Type": "application/zip",
     "Content-Length": String(PIN.B.length),
     "Content-Security-Policy": record.csp,
     "Cache-Control": "no-cache",
    });
    res.write(prefix, () => {
     request.flushedBytes = prefix.length;
     request.interrupted = true;
     fault.actualPrefixBytes = prefix.length;
     fault.prefixMeasurement =
      "server bytes flushed; browser install rejection recorded independently";
     fault.connectionDestroyed = true;
     flush();
     res.destroy();
    });
    return;
   }
   res.writeHead(200, {
    "Content-Type": mime[path.extname(file)] || "application/octet-stream",
    "Content-Security-Policy": record.csp,
    "Cache-Control": "no-cache",
    "X-Content-Type-Options": "nosniff",
   });
   const stream = fs.createReadStream(file);
   stream.on("data", (b) => (request.flushedBytes += b.length));
   stream.on("error", () => res.destroy());
   res.on("finish", () => {
    request.finished = true;
    if (rel === fault.relative) flush();
   });
   res.on("close", () => stream.destroy());
   stream.pipe(res);
  });
  server.on("connection", (s) => {
   sockets.add(s);
   s.on("close", () => sockets.delete(s));
  });
  await new Promise((resolve, reject) => {
   server.once("error", reject);
   server.listen(port, "127.0.0.1", () => {
    ownedServer = true;
    resolve();
   });
  });
 }
 async function stop() {
  if (!ownedServer) return;
  ownedServer = false;
  const old = server;
  server = null;
  for (const s of sockets) s.destroy();
  await new Promise((resolve, reject) =>
   old.close((e) => (e ? reject(e) : resolve())),
  );
 }
 async function launch(offline) {
  const { chromium } = require("playwright");
  assert.equal(require("playwright/package.json").version, "1.56.1");
  context = await chromium.launchPersistentContext(path.join(out, "profile"), {
   headless: true,
   offline,
   acceptDownloads: true,
   permissions: ["clipboard-read", "clipboard-write"],
  });
  browser = context.browser();
  record.runtime.chromium = browser?.version();
  record.runtime.playwright = "1.56.1";
  context.on("page", attach);
  for (const p of context.pages()) attach(p);
  context.on("request", (r) => {
   if (external(r.url())) {
    const u = parsed(r.url());
    record.externalRequests.push({
     url: u ? u.origin + u.pathname : "invalid",
     method: r.method(),
    });
   }
  });
  await context.route("**/*", (route) =>
   external(route.request().url())
    ? route.abort("blockedbyclient")
    : route.continue(),
  );
  const p = await context.newPage();
  attach(p);
  step(offline ? "offline context BEFORE navigation" : "online context", p);
  await p.goto(origin + "/");
  await H.ready(p);
  return p;
 }
 async function close() {
  const pages = context.pages(),
   b = browser;
  await context.close();
  context = null;
  if (b?.isConnected()) await b.close();
  assert(pages.every((p) => p.isClosed()));
  assert(!b || !b.isConnected());
  return { closedTabs: pages.length, browserDisconnected: true };
 }
 async function refuse() {
  try {
   await fetch(origin + "/", { signal: AbortSignal.timeout(5000) });
   assert.fail("Origin still answers");
  } catch (e) {
   assert.equal(e.cause?.code, "ECONNREFUSED");
   return "ECONNREFUSED";
  }
 }
 const $ = H.$,
  MAIN = "main.tex",
  BOM = Buffer.from([239, 187, 191]),
  project = "Runtime update synthetic " + Date.now();
 const normalize = (s) => s.replace(/\r\n?/g, "\n"),
  textOf = (b) => normalize(b.toString("utf8").replace(/^\uFEFF/, "")),
  encode = (s) => Buffer.concat([BOM, Buffer.from(normalize(s))]);
 const fixtureDir = path.join(__dirname, "../tests/CompilerSuiteFixtures");
 const fixture = [
  [
   MAIN,
   Buffer.concat([
    BOM,
    Buffer.from(
     fs
      .readFileSync(path.join(fixtureDir, "main-unicode.tex"), "utf8")
      .replace(/\r?\n/g, "\r\n") + "% Україна🙂 $1 \\literal exact A\r\n",
    ),
   ]),
  ],
  ...["chapter.tex", "references.bib", "figure.xbb"].map((f) => [
   f,
   fs.readFileSync(path.join(fixtureDir, f)),
  ]),
  [
   "figure.png",
   fs.readFileSync(
    path.join(__dirname, "../src/Client/wwwroot/icons/icon-192.png"),
   ),
  ],
  ["untouched.bin", Buffer.from([255, 0, 13, 10, 128, 239, 187, 191])],
 ];
 let expected = new Map(fixture.map(([f, b]) => [f, Buffer.from(b)]));
 const saved = [];
 let oldPdf;
 function summary(h) {
  return {
   id: h.id,
   parent: h.parent,
   files: h.files.map((f) => ({
    id: f.id,
    path: f.path,
    length: f.bytes.length,
    sha256: sha(Buffer.from(f.bytes)),
   })),
  };
 }
 function exact(h, e = expected) {
  assert.equal(h.files.length, e.size);
  for (const [f, b] of e) {
   const got = h.files.find((v) => v.path === f);
   assert(got, f);
   assert.deepEqual(Buffer.from(got.bytes), b, f);
  }
 }
 async function head(p, e = expected) {
  const h = await H.head(p, project);
  exact(h, e);
  return h;
 }
 async function sourceRevision(p, id, e) {
  const h = await p.evaluate(async (id) => {
   const m = await import("./js/workspace-storage.js"),
    r = await m.getRevision(id);
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
  }, id);
  assert.equal(h.id, id);
  exact(h, e);
  return summary(h);
 }
 async function openProject(p) {
  const prior = (await $(p, "analysis-panel").count())
   ? await $(p, "analysis-panel").getAttribute("data-session")
   : null;
  const row = p.locator("[data-testid=project-row]", { hasText: project });
  assert.equal(await row.count(), 1);
  await row.locator("[data-testid=open-project]").click();
  await p.waitForFunction(
   ({ name, prior, count }) => {
    const h = document.querySelector(".workspace-header h1"),
     a = document.querySelector("[data-testid=analysis-panel]");
    return (
     h?.textContent === name &&
     a?.dataset.session &&
     a.dataset.session !== prior &&
     (document.querySelectorAll("[data-testid=select-file]").length === count ||
      document.querySelector("[data-testid=recovery-panel]"))
    );
   },
   { name: project, prior, count: fixture.length },
  );
  assert.equal(await $(p, "recovery-panel").count(), 0);
 }
 async function choose(p) {
  const h = await head(p),
   id = h.files.find((f) => f.path === MAIN).id;
  const button = $(p, "select-file").filter({
   has: p.locator(".file-name", { hasText: /^main\.tex$/ }),
  });
  await button.click();
  await p.waitForFunction(
   ({ id, value }) => {
    const e = document.querySelector("[data-testid=editor]");
    return e?.dataset.fileId === id && e.value === value;
   },
   { id, value: textOf(expected.get(MAIN)) },
  );
  return id;
 }
 async function paste(p, text, label) {
  const id = await choose(p),
   stamp = await H.stamp(p);
  const e = $(p, "editor");
  await e.focus();
  await e.press("Control+A");
  await p.evaluate(async (text) => {
   await navigator.clipboard.writeText(text);
   if ((await navigator.clipboard.readText()) !== text)
    throw new Error("Native clipboard mismatch");
   const e = document.querySelector("[data-testid=editor]"),
    s = { input: 0, beforeinput: 0, events: [] };
   window.__runtimePaste = s;
   window.__runtimePasteDetach = [];
   for (const type of ["input", "beforeinput"]) {
    const f = (event) => {
     s[type]++;
     s.events.push({ type: event.inputType, trusted: event.isTrusted });
    };
    e.addEventListener(type, f);
    window.__runtimePasteDetach.push(() => e.removeEventListener(type, f));
   }
  }, text);
  await e.press("Control+V");
  await p.waitForFunction(
   ({ id, text, stamp }) => {
    const e = document.querySelector("[data-testid=editor]"),
     a = document.querySelector("[data-testid=analysis-panel]");
    return (
     e?.dataset.fileId === id &&
     e.value === text &&
     a?.dataset.session === stamp.session &&
     Number(a.dataset.generation) === Number(stamp.generation) + 1
    );
   },
   { id, text, stamp },
  );
  const events = await p.evaluate(() => {
   for (const f of window.__runtimePasteDetach) f();
   window.__runtimePasteDetach = [];
   return window.__runtimePaste;
  });
  assert.equal(events.input, 1);
  assert.equal(events.beforeinput, 1);
  assert(events.events.every((e) => e.trusted && e.type === "insertFromPaste"));
  record.actions.push({
   label,
   inputMechanism: "native whole clipboard paste",
   stamp,
   after: await H.stamp(p),
   events,
   expectedBytesSha256: sha(encode(text)),
  });
  flush();
 }
 async function save(p, text, label) {
  const before = await H.head(p, project);
  assert(
   await $(p, "save").isEnabled(),
   "Do not relabel old Saved/autosave as explicit save",
  );
  await $(p, "save").click();
  await p.waitForFunction(
   () =>
    document.querySelector(".workspace-header .status")?.textContent ===
     "Saved." && document.querySelector("[data-testid=save]")?.disabled,
  );
  expected.set(MAIN, encode(text));
  const h = await head(p);
  assert.notEqual(h.id, before.id);
  assert.equal(h.parent, before.id);
  saved.push({ id: h.id, files: new Map(expected) });
  record.actions.push({ label, explicitSaveClicks: 1, head: summary(h) });
  flush();
  return h;
 }
 async function sourceDownload(p, bytes, label) {
  await H.reveal(p, $(p, "download"));
  const [d] = await Promise.all([
   p.waitForEvent("download"),
   $(p, "download").click(),
  ]);
  const file = path.join(out, label + ".tex");
  await d.saveAs(file);
  assert.equal(await d.failure(), null);
  assert.deepEqual(fs.readFileSync(file), bytes);
  return {
   file: path.basename(file),
   length: bytes.length,
   sha256: sha(bytes),
  };
 }
 async function releaseIdentity(p, label) {
  const value = await p.evaluate(async () => {
   const c = navigator.serviceWorker.controller;
   if (!c) throw new Error("No actual controller");
   const channel = new MessageChannel(),
    requestId = crypto.randomUUID().replace(/-/g, "");
   return await new Promise((resolve, reject) => {
    const timer = setTimeout(
     () => reject(new Error("Native entry identity deadline")),
     20000,
    );
    channel.port1.onmessage = (e) => {
     clearTimeout(timer);
     channel.port1.close();
     resolve({
      controller: c.scriptURL,
      controllerState: c.state,
      entry: e.data,
      caches: null,
     });
    };
    c.postMessage(
     { type: "native-worker-entry", protocol: 1, requestId, kind: "compile" },
     [channel.port2],
    );
   });
  });
  assert(value.entry.ok);
  assert.equal(value.entry.release, ident[label].sdkVersion);
  const resources = await p.evaluate(() =>
   performance.getEntriesByType("resource").flatMap((e) => {
    try {
     return [new URL(e.name).pathname];
    } catch {
     return [];
    }
   }),
  );
  assert(
   resources.includes("/" + ident[label].client.url),
   "Actual loaded managed Client assembly identity",
  );
  value.caches = await p.evaluate(() => caches.keys());
  assert(
   value.caches.includes("oop-workspace-release-" + ident[label].sdkVersion),
  );
  record.actions.push({ label: "actual release " + label, value });
  flush();
  return value;
 }
 async function ready(p) {
  await p.waitForFunction(
   () =>
    navigator.serviceWorker.controller && window.__oopOfflineReady === true,
   null,
   { timeout: 300000 },
  );
  await p.waitForFunction(
   () =>
    document
     .querySelector("[data-testid=compiler-readiness]")
     ?.textContent.includes("compilation available."),
   null,
   { timeout: 300000 },
  );
 }
 async function compile(p, profile, label) {
  step(label, p);
  await H.reveal(p, $(p, "compile"));
  await ready(p);
  await H.reveal(p, p.getByLabel("Compile main file", { exact: true }));
  await p
   .getByLabel("Compile main file", { exact: true })
   .selectOption({ label: MAIN });
  await p
   .getByLabel("Compile engine", { exact: true })
   .selectOption("lualatex");
  await H.bibliography(p, "Bibtex");
  const before = await head(p),
   bridge = await p.evaluate(async () =>
    (await import("./js/compile-port-bridge.js")).observations(),
   ),
   oldHash = await $(p, "compile-panel").getAttribute("data-output-hash");
  await $(p, "compile").click();
  await p.waitForFunction(
   (old) => {
    const c = document.querySelector("[data-testid=compile-panel]");
    return (
     c?.dataset.outputHash &&
     c.dataset.outputHash !== old &&
     c.dataset.isCurrent === "true" &&
     document.querySelector("[data-testid=compile-status]")?.textContent ===
      "Compilation finished." &&
     document
      .querySelector("[data-testid=compile-work-status]")
      ?.textContent.trim() === "Idle"
    );
   },
   oldHash,
   { timeout: 180000 },
  );
  const after = await p.evaluate(async () =>
   (await import("./js/compile-port-bridge.js")).observations(),
  );
  const obs = { ...after, trace: after.trace.slice(bridge.trace.length) };
  // Persist actual facts BEFORE any trace oracle can reject this attempt.
  // Retain full before/after observations as well as the evaluated delta.
  record.nativeBridgeObservations ??= [];
  record.nativeBridgeObservations.push({ label, expectedProfile: profile, before: bridge, after, evaluated: obs });
  flush();
  profileTrace(obs, profile);
  assert.deepEqual(await head(p), before);
  const pane = $(p, "compile-panel").locator(":scope > [data-testid=pdf-pane]");
  await H.reveal(p, pane);
  await pane
   .locator("[data-testid=preview-status][data-preview-status=Available]")
   .waitFor({ timeout: 90000 });
  await H.reveal(p, pane.locator('[data-testid=canvas-status]'));
  await pane
   .locator("[data-testid=canvas-status]")
   .filter({ hasText: "Drawn" })
   .waitFor({ timeout: 90000 });
  const hash = await $(p, "compile-panel").getAttribute("data-output-hash");
  const [d] = await Promise.all([
   p.waitForEvent("download"),
   $(p, "compile-panel").locator("[data-testid=download-pdf]").click(),
  ]);
  const file = path.join(out, label + ".pdf");
  await d.saveAs(file);
  assert.equal(await d.failure(), null);
  const bytes = fs.readFileSync(file);
  assert.equal(bytes.subarray(0, 5).toString(), "%PDF-");
  assert.equal(sha(bytes), hash);
  const result = {
   profile,
   hash,
   length: bytes.length,
   file: path.basename(file),
   head: summary(before),
   trace: obs.trace,
  };
  record.actions.push({ label, compile: result });
  flush();
  return result;
 }
 const hp = (p, id) =>
  $(p, "compile-history-panel").locator(`[data-testid="${id}"]`);
 async function historical(p, label) {
  await H.reveal(p, hp(p, "refresh-pdf-history"));
  await hp(p, "refresh-pdf-history").click();
  const buttons = hp(p, "open-saved-pdf");
  await buttons.first().waitFor();
  assert.equal(await buttons.count(), 1);
  await buttons.first().click();
  await H.reveal(p, hp(p, "history-record-identity"));
  await hp(p, "history-record-identity").waitFor({ state: "visible" });
  const key = (await hp(p, "history-record-identity").textContent()).trim();
  if (oldPdf?.record) assert.equal(key, oldPdf.record);
  assert.equal(
   (await hp(p, "history-pdf-sha256").textContent()).trim(),
   oldPdf.hash,
  );
  const pane = hp(p, "pdf-pane");
  await H.reveal(p, pane);
  await pane
   .locator("[data-testid=preview-status][data-preview-status=Available]")
   .waitFor({ timeout: 90000 });
  assert.match(
   await pane.locator("[data-testid=pdf-provenance]").textContent(),
   /CallerSaysHistorical/,
  );
  assert(
   await pane
    .getByRole("button", { name: "Go to source", exact: true })
    .isDisabled(),
  );
  assert.equal(
   await pane.locator("[data-testid=source-pdf-highlight]").count(),
   0,
  );
  const [d] = await Promise.all([
   p.waitForEvent("download"),
   hp(p, "download-saved-pdf").click(),
  ]);
  const file = path.join(out, label + "-old-history.pdf");
  await d.saveAs(file);
  assert.equal(await d.failure(), null);
  const bytes = fs.readFileSync(file);
  assert.equal(sha(bytes), oldPdf.hash);
  assert.equal(bytes.length, oldPdf.length);
  const metadata = await storedMetadata(p, key);
  assert.equal(metadata.metadata.classification, "HistoricalOnly");
  assert.equal(
   canonical(metadata.metadata.originalSnapshot.baseHeadRevisionId),
   canonical(oldPdf.head.id),
  );
  assert.equal(hex(metadata.metadata.pdfHash), oldPdf.hash);
  if (oldPdf.metadata) assert.deepEqual(metadata, oldPdf.metadata);
  const current = await head(p),
   expectedAssociation =
    canonical(current.id) === canonical(oldPdf.head.id)
     ? "VerifiedFileManifest"
     : "Mismatch";
  await hp(p, "compare-saved-pdf-source").click();
  await p.waitForFunction(
   ({ head, status }) => {
    const a = document.querySelector(
     "[data-testid=compile-history-panel] [data-testid=history-source-association]",
    )?.dataset;
    return (
     a?.comparisonRequested === "true" &&
     a.comparisonPending === "false" &&
     a.comparisonStale === "false" &&
     a.associationStatus === status &&
     a.comparedRevision.replace(/-/g, "").toLowerCase() === head
    );
   },
   { head: canonical(current.id), status: expectedAssociation },
  );
  assert.deepEqual(
   await head(p),
   current,
   "Historical comparison cannot save/recompile/edit",
  );
  return {
   record: key,
   metadata,
   sourceAssociation: {
    status: expectedAssociation,
    comparedHead: current.id,
    scope: "RevisionFileManifestOnly",
   },
   download: {
    file: path.basename(file),
    sha256: sha(bytes),
    length: bytes.length,
   },
   historicalOnly: true,
   noLiveNavigation: true,
  };
 }
 function canonical(v) {
  return typeof v === "string"
   ? v.replace(/-/g, "").toLowerCase()
   : v && typeof v === "object"
     ? canonical(v.value ?? v.Value)
     : null;
 }
 const hex = (v) =>
  typeof v === "string"
   ? v.toLowerCase()
   : String(v?.hex ?? v?.Hex).toLowerCase();
 async function storedMetadata(p, recordId) {
  return p.evaluate(async (recordId) => {
   const dbName = "overclone-compile-artifacts-v1";
   if (!(await indexedDB.databases()).some((d) => d.name === dbName))
    throw new Error("No actual artifact database");
   const db = await new Promise((resolve, reject) => {
    const r = indexedDB.open(dbName);
    r.onupgradeneeded = () => {
     r.transaction.abort();
     reject(new Error("No schema creation in read witness"));
    };
    r.onsuccess = () => resolve(r.result);
    r.onerror = () => reject(r.error);
   });
   let text;
   try {
    text = await new Promise((resolve, reject) => {
     const tx = db.transaction("catalog", "readonly"),
      r = tx.objectStore("catalog").get(recordId);
     let v;
     r.onsuccess = () => (v = r.result);
     tx.oncomplete = () => resolve(v);
     tx.onabort = () => reject(tx.error);
    });
   } finally {
    db.close();
   }
   let c;
   try {
    c = JSON.parse(text);
   } catch (error) {
    throw new Error("Invalid stored catalog JSON", { cause: error });
   }
   const i = c.assets.findIndex(
    (a) => a.relativePath === "history/metadata.json",
   );
   if (i < 0) throw new Error("Actual history metadata absent");
   const d = await (
     await (await navigator.storage.getDirectory()).getDirectoryHandle(dbName)
    ).getDirectoryHandle(recordId),
    f = await (await d.getFileHandle(String(i))).getFile(),
    b = new Uint8Array(await f.arrayBuffer()),
    hash = Array.from(
     new Uint8Array(await crypto.subtle.digest("SHA-256", b)),
     (x) => x.toString(16).padStart(2, "0"),
    ).join("");
   if (b.length !== c.assets[i].declaredLength || hash !== c.assets[i].hash)
    throw new Error("History metadata integrity");
   let metadata;
   try {
    metadata = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(b));
   } catch (error) {
    throw new Error("Invalid stored history metadata JSON", { cause: error });
   }
   return {
    metadata,
    metadataSha256: hash,
    storageInventoryDigest: c.inventoryDigest,
   };
  }, recordId);
 }
 async function currentNavigation(p) {
  const before = await head(p),
   pane = $(p, "compile-panel").locator(":scope > [data-testid=pdf-pane]"),
   records = [];
  for (const [file, needle] of [
   [MAIN, "Portable compiler fixture."],
   ["chapter.tex", "Included file, reference"],
  ]) {
   const id = before.files.find((f) => f.path === file).id;
   await $(p, "select-file")
    .filter({
     has: p.locator(".file-name", {
      hasText: new RegExp(
       "^" + file.replace(/[.*+?^${}()|[\]\\]/g, "\\$&") + "$",
      ),
     }),
    })
    .click();
   await p.waitForFunction(
    ({ id, needle }) => {
     const e = document.querySelector("[data-testid=editor]");
     return e?.dataset.fileId === id && e.value.includes(needle);
    },
    { id, needle },
   );
   const editor = $(p, "editor"),
    value = await editor.inputValue(),
    at = value.indexOf(needle);
   assert(at >= 0);
   const expectedLine = expectedInverseLine(value, needle);
   await editor.evaluate((e, at) => {
    e.focus();
    e.setSelectionRange(at, at);
   }, at);
   await $(p, "source-go-to-pdf").click();
   await p.waitForFunction(
    (file) => {
     const s =
      document.querySelector("[data-testid=source-map-status]")?.textContent ||
      "";
     return s.startsWith("PDF page ") && s.includes(file + ":");
    },
    file,
    { timeout: 45000 },
   );
   await pane.locator("[data-testid=source-pdf-highlight]").waitFor();
   const box = await pane
     .locator("[data-testid=source-pdf-highlight]")
     .boundingBox(),
    canvas = pane.locator("[data-testid=pdf-canvas]"),
    bounds = await canvas.boundingBox();
   assert(box && bounds && box.width > 0 && box.height > 0);
   const other = file === MAIN ? "chapter.tex" : MAIN;
   await $(p, "select-file")
    .filter({ has: p.locator(".file-name", { hasText: other }) })
    .click();
   await p.waitForFunction(
    (id) =>
     document.querySelector("[data-testid=editor]")?.dataset.fileId === id,
    before.files.find((f) => f.path === other).id,
   );
   await canvas.dblclick({
    position: {
     x: box.x + box.width / 2 - bounds.x,
     y: box.y + box.height / 2 - bounds.y,
    },
   });
   await p.waitForFunction(
    (file) => {
     const s =
      document.querySelector("[data-testid=source-map-status]")?.textContent ||
      "";
     return (
      s.startsWith("Selected " + file + ":") &&
      s.includes("native line-level precision")
     );
    },
    file,
    { timeout: 45000 },
   );
   const status = await $(p, "source-map-status").textContent(),
    line = Number(
     status.slice(("Selected " + file + ":").length).match(/^\d+/)?.[0],
    );
   assert(line > 0);
   const selected = await editor.evaluate((e) => ({
     id: e.dataset.fileId,
     value: e.value,
     start: e.selectionStart,
     end: e.selectionEnd,
    })),
    lines = value.split("\n");
   assert.equal(
    line,
    expectedLine,
    "Inverse line must equal the fixture target known BEFORE navigation",
   );
   const start = lines
    .slice(0, expectedLine - 1)
    .reduce((n, t) => n + t.length + 1, 0);
   assert.equal(selected.id, id);
   assert.equal(selected.value, value);
   assert.equal(selected.start, start);
   assert.equal(selected.end, start + lines[expectedLine - 1].length);
   assert.deepEqual(await head(p), before);
   records.push({
    file,
    line,
    expectedLine,
    precision:
     "exact fixture source line only; no character/glyph-level inverse claim",
    status,
    start: selected.start,
    end: selected.end,
   });
  }
  return {
   records,
   currentPaneOnly: true,
   actualP10GraphicsWitness: true,
   sourceUnchanged: true,
  };
 }
 async function observeUpdate(p, label) {
  await p.evaluate(async (label) => {
   const r = await navigator.serviceWorker.getRegistration();
   if (!r) throw new Error("No standard registration");
   const s = { label, states: [], error: null, worker: null };
   window.__runtimeUpdate = s;
   const found = () => {
    if (s.worker || !r.installing) return;
    s.worker = r.installing;
    const changed = () => {
     s.states.push(s.worker.state);
    };
    s.worker.addEventListener("statechange", changed);
    changed();
   };
   r.addEventListener("updatefound", found);
   found();
   r.update().catch((e) => (s.error = String(e)));
  }, label);
 }
 async function updateState(p) {
  return p.evaluate(() => {
   const r = window.__runtimeUpdate;
   return r ? { label: r.label, states: r.states, error: r.error } : null;
  });
 }
 async function failedInstall(p) {
  await p.waitForFunction(
   () =>
    window.__runtimeUpdate?.states.some((state) =>
     ["redundant", "installed", "activated"].includes(state),
    ),
   null,
   { timeout: 300000 },
  );
  const states = await updateState(p);
  assert(
   states.states.includes("installing") && states.states.includes("redundant"),
  );
  assert(!states.states.includes("installed"));
  const registration = await p.evaluate(async () => {
   const r = await navigator.serviceWorker.getRegistration();
   return { waiting: !!r.waiting, active: r.active?.state };
  });
  assert.equal(registration.waiting, false);
  assert.equal(registration.active, "activated");
  cutProof(fault, PIN.B.length);
  assert(!record.unexpectedAutomaticRuntimeRetry);
  const identity = await releaseIdentity(p, "A");
  return { states, registration, fault: { ...fault }, identity };
 }
 async function importFixture(p) {
  await H.reveal(p, $(p, "import-zip"));
  await $(p, "import-zip").setInputFiles({
   name: "runtime-update-portable.zip",
   mimeType: "application/zip",
   buffer: buildZip(fixture),
  });
  await H.reveal(p, $(p, "import-preview"));
  await $(p, "staged-name").fill(project);
  await $(p, "publish-import").click();
  await p.waitForFunction(
   ({ name, count }) =>
    document.querySelector(".workspace-header h1")?.textContent === name &&
    document.querySelector("[data-testid=analysis-panel]")?.dataset.session &&
    document.querySelectorAll("[data-testid=select-file]").length === count,
   { name: project, count: fixture.length },
   { timeout: 180000 },
  );
  const h = await head(p);
  saved.push({ id: h.id, files: new Map(expected) });
  return h;
 }
 async function capture() {
  record.failureTabs ||= [];
  for (const p of context?.pages() || []) {
   if (p.isClosed()) continue;
   const id = tabs.get(p);
   const value = await bounded(
    p.evaluate(() => ({
     url: location.href,
     body: document.body.innerText.slice(0, 50000),
     editor: (() => {
      const e = document.querySelector("[data-testid=editor]");
      return e
       ? {
          file: e.dataset.fileId,
          value: e.value,
          start: e.selectionStart,
          end: e.selectionEnd,
          disabled: e.disabled,
         }
       : null;
     })(),
     analysis: document.querySelector("[data-testid=analysis-panel]")
      ?.outerHTML,
     update: document.querySelector("#update-banner")?.outerHTML,
     readiness: document.querySelector("[data-testid=compiler-readiness]")
      ?.textContent,
    })),
    8000,
    "state capture deadline",
   ).catch((e) => ({ unavailable: String(e) }));
   record.failureTabs.push({ id, active: p === active, value });
   flush();
   await bounded(
    p.screenshot({
     path: path.join(out, "failure-" + id + ".png"),
     fullPage: true,
     timeout: 8000,
    }),
    9000,
    "screenshot deadline",
   ).catch(() => {});
  }
 }
 try {
  roots = {
   A: fs.realpathSync(need("PUBLISHED_A_WWWROOT")),
   B: fs.realpathSync(need("PUBLISHED_B_WWWROOT")),
  };
  assert.notEqual(roots.A, roots.B);
  assert(
   !Object.values(roots).some((r) => out === r || out.startsWith(r + path.sep)),
  );
  record.baselineSourceDigest = need("BASELINE_SOURCE_DIGEST");
  assert.equal(
   record.baselineSourceDigest,
   "5e2064160dffbec6ac5dbd74544544efbee51c8ad4b2f3aaea3a473d629ebd16",
  );
  record.publicationBSourceDigest =
   "803e65b0d466e1c2300e9ffe78e819cb20c6d8cacfe0daf082d04ea09ffa9cf4";
  ident.A = preflightRelease("A", roots.A, need("EXPECTED_A_SDK_VERSION"));
  ident.B = preflightRelease("B", roots.B, need("EXPECTED_B_SDK_VERSION"));
  assert.equal(ident.A.sdkVersion, "WZlFq+Ha");
  assert.equal(ident.B.sdkVersion, "rprfYTC8");
  assert.notEqual(ident.A.client.hash, ident.B.client.hash);
  for (const renderer of ["bundled-runtime/e1.js", "bundled-runtime/e1.wasm"])
   assert.equal(
    hashFile(path.join(roots.A, renderer)),
    hashFile(path.join(roots.B, renderer)),
    "Renderer expected unchanged across actual A/B",
   );
  const headers = fs.readFileSync(
   path.join(__dirname, "../docs/examples/static-csp-headers.conf"),
   "utf8",
  );
  for (const r of Object.values(roots))
   validateCsp(fs.readFileSync(path.join(r, "index.html"), "utf8"), headers);
  record.csp = headers
   .split("\n")
   .map((s) => s.trim())
   .find((s) => s.startsWith("Content-Security-Policy:"))
   .slice("Content-Security-Policy:".length)
   .trim();
  record.inputs = Object.fromEntries(
   [
    "review-ui-helpers.cjs",
    "zip-util.cjs",
    "test-csp.cjs",
    "source-pdf-navigation-ui-checks.cjs",
   ].map((f) => [f, sha(fs.readFileSync(path.join(__dirname, f)))]),
  );
  record.fixture = {
   zipSha256: sha(buildZip(fixture)),
   files: fixture.map(([f, b]) => ({
    path: f,
    length: b.length,
    sha256: sha(b),
   })),
   project,
  };
  port = Number(process.env.TEST_PORT || 8792);
  assert(Number.isInteger(port) && port > 1024 && port < 65536);
  origin = "http://127.0.0.1:" + port;
  flush();
  await start();
  let p = await launch(false);
  step("actual A install/import/cache two saved revisions", p);
  const first = await importFixture(p);
  await ready(p);
  pass("AInstalled", await releaseIdentity(p, "A"));
  const s2Text =
   textOf(expected.get(MAIN)) +
   "% saved second revision Україна🙂 $1 \\literal\n";
  await paste(p, s2Text, "A second saved revision");
  const s2 = await save(p, s2Text, "A explicit second revision");
  oldPdf = await compile(p, 9, "A-P9-Lua-portable");
  await H.reveal(p, hp(p, "save-pdf-history"));
  await hp(p, "save-pdf-history").click();
  await p.waitForFunction(() =>
   document
    .querySelector(
     "[data-testid=compile-history-panel] [data-testid=history-status]",
    )
    ?.textContent.startsWith("PDF saved to history."),
  );
  const historicA = await historical(p, "A-original");
  oldPdf.record = historicA.record;
  oldPdf.metadata = historicA.metadata;
  pass("ASourceHistoryPdf", {
   first: summary(first),
   second: summary(s2),
   pdf: oldPdf,
  });
  step(
   "genuine A draft, switch static transport to real B and interrupt ONE required B runtime ZIP fetch",
   p,
  );
  const stageDraft =
   textOf(expected.get(MAIN)) + "% working draft while candidate staging\n";
  await paste(p, stageDraft, "A working draft before B staging");
  activeRelease = "B";
  fault.armed = true;
  await observeUpdate(p, "B-interrupted-runtime");
  const backup = await sourceDownload(
   p,
   encode(stageDraft),
   "A-staging-working-backup",
  );
  await save(p, stageDraft, "A explicit save of staged-update draft");
  const headBeforeFailure = await head(p);
  const failed = await failedInstall(p);
  assert.deepEqual(await head(p), headBeforeFailure);
  pass("interruptedBRejected", {
   ...failed,
   workingBackup: backup,
   documentsUnchanged: true,
  });
  step(
   "close ALL browser, stop/refuse host, cold offline must retain healthy A and source/history",
   p,
  );
  await ready(p);
  const closedA = await close();
  await stop();
  const refusedA = await refuse();
  p = await launch(true);
  await releaseIdentity(p, "A");
  await openProject(p);
  await ready(p);
  assert.deepEqual(await head(p), headBeforeFailure);
  for (const s of saved) await sourceRevision(p, s.id, s.files);
  const oldAOffline = await historical(p, "A-failed-update-offline");
  const aOfflineText =
   textOf(expected.get(MAIN)) +
   "% actual A offline edit after rejected runtime update 🙂\n";
  await paste(p, aOfflineText, "A offline local edit after failed B");
  await save(p, aOfflineText, "A offline explicit child after failed B");
  const aOfflineBackup = await sourceDownload(
   p,
   expected.get(MAIN),
   "A-offline-after-failed-update-source",
  );
  await compile(p, 9, "A-offline-after-rejected-B");
  pass("AColdOfflineAfterFailure", {
   closedA,
   refusedA,
   head: summary(await head(p)),
   oldPdf: oldAOffline,
   actualP9Compile: true,
   actualOfflineEditSave: true,
   sourceBackup: aOfflineBackup,
  });
  step(
   "bring SAME origin online/full actual B, ONE explicit update retry; no cache/profile clear",
   p,
  );
  retryAllowed = true;
  await start();
  await context.setOffline(false);
  await observeUpdate(p, "B-explicit-retry");
  await p.waitForFunction(
   async () => !!(await navigator.serviceWorker.getRegistration())?.waiting,
   null,
   { timeout: 300000 },
  );
  await p.locator("#update-banner button").waitFor();
  step("dirty refusal against genuinely WAITING B; no silent save/reload", p);
  const dirtyText =
    textOf(expected.get(MAIN)) + "% genuine dirty activation guard Україна🙂\n",
   dirtyHead = (await head(p)).id;
  await paste(p, dirtyText, "waiting-B dirty guard");
  const before = {
   dirty: await $(p, "save").isEnabled(),
   head: dirtyHead,
   value: await $(p, "editor").inputValue(),
   release: (await releaseIdentity(p, "A")).entry.release,
  };
  await p.locator("#update-banner button").click();
  await p.locator("[data-testid=update-guidance]").waitFor();
  const guidance = await p
   .locator("[data-testid=update-guidance]")
   .textContent();
  const after = {
   release: (await releaseIdentity(p, "A")).entry.release,
   head: (await H.head(p, project)).id,
   value: await $(p, "editor").inputValue(),
  };
  dirtyRefusal(before, after, guidance);
  pass("dirtyActivationRefused", { before, after, guidance });
  await sourceDownload(p, encode(dirtyText), "A-waiting-B-dirty-backup");
  await save(p, dirtyText, "A explicit safe-boundary save before B acceptance");
  const beforeActivation = await head(p),
   retryObservation = await updateState(p);
  assert(
   retryObservation?.states.includes("installed"),
   "Explicit retry must observe actual B installation",
  );
  assert.equal(retryObservation.error, null);
  step("explicit clean accept B and actual controllerchange/reload", p);
  await Promise.all([
   p.waitForEvent("domcontentloaded", { timeout: 90000 }),
   p.locator("#update-banner button").click(),
  ]);
  await H.ready(p);
  await releaseIdentity(p, "B");
  await openProject(p);
  await ready(p);
  assert.deepEqual(await head(p), beforeActivation);
  for (const s of saved) await sourceRevision(p, s.id, s.files);
  pass("explicitBRetryAccepted", {
   observedUpdate: retryObservation,
   identity: await releaseIdentity(p, "B"),
   head: summary(await head(p)),
   sameProfile: true,
   noClearing: true,
  });
  pass("oldHistoryExactOnB", await historical(p, "B-upgraded-old-P9"));
  step(
   "fresh ordinary edit/save + P10 Lua PNG/include/BibTeX compile/render/download/navigation",
   p,
  );
  const bText = textOf(expected.get(MAIN)).replace(
   "Portable compiler fixture.",
   "Portable compiler fixture. Runtime upgrade B witness.",
  );
  await paste(p, bText, "B fresh source");
  await save(p, bText, "B fresh saved head");
  const bPdf = await compile(p, 10, "B-P10-Lua-portable");
  assert.notEqual(bPdf.hash, oldPdf.hash);
  const navigation = await currentNavigation(p);
  pass("freshP10CompileNavigation", { pdf: bPdf, navigation });
  step(
   "B whole-browser close/host stop/refuse then SAME-profile offline fresh source/save/compile/download",
   p,
  );
  await ready(p);
  const closedB = await close();
  await stop();
  const refusedB = await refuse();
  p = await launch(true);
  await releaseIdentity(p, "B");
  await openProject(p);
  await ready(p);
  for (const s of saved) await sourceRevision(p, s.id, s.files);
  await head(p);
  const oldBOffline = await historical(p, "B-offline-old-P9");
  const coldText =
   textOf(expected.get(MAIN)) + "% fresh cold-offline source edit 🙂\n";
  await paste(p, coldText, "B offline fresh edit");
  await save(p, coldText, "B offline saved child");
  const coldPdf = await compile(p, 10, "B-offline-P10-Lua");
  for (const s of saved) await sourceRevision(p, s.id, s.files);
  pass("BColdOfflineFreshCompile", {
   closedB,
   refusedB,
   pdf: coldPdf,
   oldPdf: oldBOffline,
   head: summary(await head(p)),
  });
  assert.deepEqual(record.externalRequests, []);
  assert.deepEqual(record.pageErrors, []);
  assert(!record.unexpectedAutomaticRuntimeRetry);
  completeCoverage(record.coverage);
  completed = true;
 } catch (e) {
  record.failure = {
   message: String(e.stack || e),
   cause: String(e.cause?.stack || e.cause || ""),
  };
  process.exitCode = 1;
  record.captureStatus = "pending";
  flush();
  await capture().catch((e) => (record.captureError = String(e)));
  record.captureStatus = "finished-or-bounded-timeout";
  flush();
 } finally {
  if (context)
   try {
    await bounded(close(), 20000, "browser close deadline");
   } catch (e) {
    record.cleanupFailure = String(e);
    process.exitCode = 1;
   }
  try {
   await stop();
  } catch (e) {
   record.cleanupFailure = String(e);
   process.exitCode = 1;
  }
  if (completed && !record.cleanupFailure) {
   record.result = "PASS";
   record.step = "PASS all named runtime-update persistence scopes";
  }
  flush();
  process.stdout.write(
   JSON.stringify({
    result: record.result,
    step: record.step,
    receipt,
    failure: record.failure?.message.slice(0, 3000),
   }) + "\n",
  );
 }
}
if (require.main === module)
 main().catch((e) => {
  process.stderr.write(String(e.stack || e).slice(0, 3000) + "\n");
  process.exitCode = 1;
 });
