const CASE='geometry-package',PORT=8780,BUDGET=16*1024*1024;
// Standalone actual-published acceptance. Root executes, agent parser-only.
// Test scaffolding copied as source, never imports another harness IIFE.
// No private C# calls, storage seeding/writes or fake product results.
const assert=require("node:assert/strict"),fs=require("node:fs"),os=require("node:os"),path=require("node:path"),http=require("node:http");
const {createHash}=require("node:crypto"),{inflateRawSync}=require("node:zlib");
assert.ok(process.env.SOURCE_ROOT,"SOURCE_ROOT required (readonly626-file tree)");
const SOURCE_ROOT=fs.realpathSync(process.env.SOURCE_ROOT);
const H=require(path.join(SOURCE_ROOT,"scripts/review-ui-helpers.cjs")),{validate:validateCsp}=require(path.join(SOURCE_ROOT,"scripts/test-csp.cjs")),{crc32,buildZip}=require(path.join(SOURCE_ROOT,"scripts/zip-util.cjs"));
const need=n=>{assert.ok(process.env[n],`${n} required`);return process.env[n];};
const sha=b=>createHash("sha256").update(b).digest("hex"),identity=b=>({bytes:b.length,sha256:sha(b)}),canonical=s=>String(s??"").replace(/-/g,"").toLowerCase();
const OUT=path.resolve(need("RESULT_DIR")),RECEIPT=path.join(OUT,CASE+"-receipt.json");assert.ok(fs.statSync(OUT).isDirectory());fs.closeSync(fs.openSync(RECEIPT,"wx"));
const TEXT="main.tex",BOM=Buffer.from([239,187,191]),NATIVE=/\/__oop\/native-worker\/(?:compile|pdf)\b/;
const record={receiptSchemaVersion:2,result:"FAIL",scope:CASE,actualBrowserExecution:"started by caller",sdk:null,online:{},cold:{},actions:[],navigation:[],history:[],snapshots:[],artifacts:[],workers:[],immutableRevisions:Object.create(null),contentObjects:Object.create(null),checkpoints:Object.create(null),pageErrors:[],violations:[],externalAttempts:[],externalSuccesses:[],failure:null,cleanup:null,limits:[]};
let root, inputs, chromium, playwrightErrors, page = null, context = null, launching = null, server = null, profile = null, base = null;
let phase = "output-preflight", category = "PREFLIGHT", stopped = false, lastClosedSession = null;
const RECEIPT_LIMIT = 262144;
let receiptIssue = null, lastGoodReceipt = null;
const running = () => { if (stopped) throw new Error("Deadline/cleanup reached"); };
const stage = (p, c) => { running(); phase = p; category = c; };
function outputBytes() {
  let total = 0, entries = 0; const pending = [OUT];
  while (pending.length) { const dir = pending.pop(); for (const n of fs.readdirSync(dir)) {
    assert.ok(++entries <= 4096); const p = path.join(dir, n), s = fs.lstatSync(p); assert.ok(!s.isSymbolicLink());
    if (s.isDirectory()) pending.push(p); else { assert.ok(s.isFile()); total += s.size; }
  } } return total;
}
function smallEvidence(value, maximum) {
  try {
    const text = JSON.stringify(value);
    if (typeof text !== "string") return null;
    return Buffer.byteLength(text) <= maximum ? value : { truncated: true, preview: text.slice(0, Math.floor(maximum / 4)) };
  } catch { return { truncated: true, reason: "Evidence could not be serialized" }; }
}
function writeEmergencyReceipt() {
  // Failure-only diagnostic reduction: never convert a failed/over-budget case
  // to PASS and never skip UI assertions to make a successful receipt smaller.
  record.result = "FAIL"; process.exitCode = 1;
  if (!receiptIssue.preservationAttempted) {
    receiptIssue.preservationAttempted = true;
    if (lastGoodReceipt) {
      try {
        const backup = path.join(OUT, "last-full-receipt-before-fallback.json");
        const size = Buffer.byteLength(lastGoodReceipt);
        if (outputBytes() + size + 65536 <= BUDGET) {
          fs.writeFileSync(backup, lastGoodReceipt, { flag: "wx" });
          receiptIssue.lastFullReceipt = { artifact: path.basename(backup), bytes: size, sha256: sha(Buffer.from(lastGoodReceipt)) };
        } else receiptIssue.lastFullReceiptNotCopied = "Insufficient output-budget reserve; artifacts were not deleted.";
      } catch (e) { receiptIssue.lastFullReceiptNotCopied = String(e.message || e).slice(0, 512); }
    }
  }
  const first = record.failure || { phase, classification: "RECEIPT_REPORTING", message: "Receipt reporting failed", stack: null };
  const failure = { phase: first.phase, classification: first.classification, message: String(first.message).slice(0, 1024),
    stack: typeof first.stack === "string" ? first.stack.slice(0, 4096) : null, ui: smallEvidence(first.ui, 8192) };
  let emergency = { receiptSchemaVersion: 2, result: "FAIL", scope: record.scope, actualBrowserExecution: record.actualBrowserExecution,
    failure, receiptEvidence: { truncated: true, normalReceiptLimit: RECEIPT_LIMIT, issue: receiptIssue,
      qualification: "Case failed; bulk progress diagnostics omitted. Full assertions/artifacts were not waived." },
    sdk: smallEvidence(record.sdk, 4096), projects: smallEvidence(record.projects, 2048),
    target: smallEvidence(record.online.target ? { id: record.online.target.id, path: record.online.target.path, source: record.online.target.source } : null, 2048),
    completedActions: record.actions.slice(0, 32).map(a => ({ label: a.label, beforeHead: a.before?.head?.id, afterHead: a.after?.head?.id,
      session: a.after?.stamp?.session, generation: a.after?.stamp?.generation })),
    completedHistoryChecks: record.history.slice(0, 32).map(h => ({ label: h.label, revisionIds: h.revisionIds?.slice(0, 32) })),
    immutableRevisionIds: Object.keys(record.immutableRevisions || {}).slice(0, 32),
    artifacts: smallEvidence(record.artifacts.slice(0, 64), 8192), readCleanup: smallEvidence(record.readHookCleanup, 4096),
    cleanup: smallEvidence(record.cleanup, 4096), profileCleaned: record.profileCleaned === true,
    signalCounts: { pageErrors: record.pageErrors.length, csp: record.violations.length, externalAttempts: record.externalAttempts.length,
      externalSuccesses: record.externalSuccesses.length, nativeWorkers: record.workers.filter(w => NATIVE.test(w.url)).length } };
  let data = JSON.stringify(emergency, null, 2) + "\n";
  if (Buffer.byteLength(data) > 65536) {
    emergency = { receiptSchemaVersion: 2, result: "FAIL", scope: record.scope, failure,
      receiptEvidence: { truncated: true, issue: receiptIssue, emergencySummaryFurtherReduced: true },
      cleanup: smallEvidence(record.cleanup, 4096), readCleanup: smallEvidence(record.readHookCleanup, 4096), profileCleaned: record.profileCleaned === true };
    data = JSON.stringify(emergency, null, 2) + "\n";
  }
  try {
    const priorSize = fs.existsSync(RECEIPT) ? fs.statSync(RECEIPT).size : 0;
    if (Buffer.byteLength(data) > RECEIPT_LIMIT || outputBytes() - priorSize + Buffer.byteLength(data) > BUDGET)
      throw new Error("Emergency receipt cannot fit unchanged receipt/output budgets");
    fs.writeFileSync(RECEIPT, data);
  } catch (e) {
    // ENOSPC/IO failures cannot be repaired by dropping artifacts or increasing
    // budgets. Root captures bounded stderr; preserve the ORIGINAL first error.
    process.stderr.write(JSON.stringify({ ...emergency, receiptWriteError: String(e.message || e).slice(0, 512) }) + "\n");
  }
}
function save({ halt = true } = {}) {
  if (receiptIssue) { writeEmergencyReceipt(); return; }
  let measuredBytes = null;
  try {
    const data = JSON.stringify(record, null, 2) + "\n"; measuredBytes = Buffer.byteLength(data);
    if (measuredBytes > RECEIPT_LIMIT) {
      const e = new Error(`Receipt budget exceeded: ${measuredBytes} bytes > ${RECEIPT_LIMIT}`); e.receiptClassification = "RECEIPT_BUDGET"; throw e;
    }
    if (outputBytes() - fs.statSync(RECEIPT).size + measuredBytes > BUDGET) {
      const e = new Error("Output artifact/receipt budget exceeded16MiB"); e.receiptClassification = "OUTPUT_BUDGET"; throw e;
    }
    fs.writeFileSync(RECEIPT, data); lastGoodReceipt = data;
  } catch (e) {
    receiptIssue = { phase, classification: e.receiptClassification || "RECEIPT_WRITE_OR_SERIALIZATION", message: String(e.message || e).slice(0, 1024), measuredBytes };
    fail(e, receiptIssue.classification); process.exitCode = 1;
    writeEmergencyReceipt();
    process.stderr.write(JSON.stringify({ result: "FAIL", receiptIssue, firstFailure: { phase: record.failure.phase, classification: record.failure.classification, message: record.failure.message } }) + "\n");
    if (halt) throw e; // stop the case; catch/finally use halt:false and cannot recursively throw on the same overflow
  }
}
function fail(e, kind = category) { if (!record.failure) record.failure = { phase, classification: kind, message: String(e.message || e).slice(0, 1024), stack: typeof e.stack === "string" ? e.stack.slice(0, 4096) : null }; record.result = "FAIL"; }
function append(list, value, max = 128) { if (list.length < max) list.push(value); else record.observationOverflow = true; }
async function bounded(p, ms, message) { let timer; try { return await Promise.race([p, new Promise((_, reject) => { timer = setTimeout(() => reject(new Error(message)), ms); })]); } finally { clearTimeout(timer); } }
function cleanSignals() {
  assert.deepEqual(record.assetObservationErrors||[],[],"actual loaded Rust identity");
  assert.ok(!record.observationOverflow); assert.equal(record.workers.filter(w => NATIVE.test(w.url)).length, 0, "source-only workflow: no compiler/PDF workers");
  for (const n of ["pageErrors", "violations", "externalAttempts", "externalSuccesses"]) assert.deepEqual(record[n], [], n);
}
function preflight() {
  root = fs.realpathSync(need("PUBLISHED_WWWROOT")); assert.ok(outputBytes() <= BUDGET - 3 * 1024 * 1024);
  const headers = fs.readFileSync(path.join(SOURCE_ROOT, "docs/examples/static-csp-headers.conf"), "utf8");
  validateCsp(fs.readFileSync(path.join(root, "index.html"), "utf8"), headers);
  const line = headers.split("\n").map(s => s.trim()).find(s => s.startsWith("Content-Security-Policy:")); assert.ok(line);
  const text = fs.readFileSync(path.join(root, "service-worker-assets.js"), "utf8"), match = text.match(/self\.assetsManifest\s*=\s*(\{[\s\S]*\})\s*;/); assert.ok(match);
  const manifest = JSON.parse(match[1]); assert.equal(typeof manifest.version, "string");
  assert.equal(manifest.version, "9fWbz/Nt", "root-reviewed actual SDK release");
  assert.ok(Array.isArray(manifest.assets) && manifest.assets.length > 0 && manifest.assets.length <= 4096);
  assert.equal(manifest.assets.length, 239, "observed root-published asset inventory, not old237 estimate");
  const names = new Set();
  for (const a of manifest.assets) {
    assert.equal(typeof a.url, "string"); assert.ok(!path.isAbsolute(a.url) && !a.url.includes("\\") && !a.url.split("/").includes(".."));
    assert.ok(!names.has(a.url)); names.add(a.url); const f = fs.realpathSync(path.join(root, a.url)); assert.ok(f.startsWith(root + path.sep));
    assert.equal("sha256-" + createHash("sha256").update(fs.readFileSync(f)).digest("base64"), a.hash, a.url);
  }
  for (const n of ["index.html", "_framework/blazor.webassembly.js", "js/workspace-storage.js", "js/download.js", "js/offline-readiness.js", "js/editor-select.js"]) assert.ok(names.has(n), n);
  record.sdk = { version: manifest.version, observedAssets: manifest.assets.length, manifestSha256: sha(Buffer.from(text)), everySriVerified: true, strictCsp: true };
  verifyPublishedPair(names);
  return { headers, csp: line.slice("Content-Security-Policy:".length).trim() };
}
const TYPES = { ".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8", ".mjs": "text/javascript", ".css": "text/css", ".json": "application/json",
  ".wasm": "application/wasm", ".png": "image/png", ".svg": "image/svg+xml", ".ico": "image/x-icon", ".txt": "text/plain", ".dat": "application/octet-stream", ".blat": "application/octet-stream", ".pdb": "application/octet-stream" };
async function serve() {
  server = http.createServer((req, res) => {
    let name, f; try { name = decodeURIComponent(new URL(req.url, `http://127.0.0.1:${PORT}`).pathname); } catch { res.writeHead(400); res.end(); return; }
    if (name.includes("\\") || name.includes("\0") || name.split("/").includes("..")) { res.writeHead(400); res.end(); return; }
    try { f = fs.realpathSync(path.resolve(root, "." + (name === "/" ? "/index.html" : name))); } catch { res.writeHead(404); res.end(); return; }
    if (!f.startsWith(root + path.sep) || !fs.statSync(f).isFile()) { res.writeHead(404); res.end(); return; }
    res.writeHead(200, { "Content-Type": TYPES[path.extname(f).toLowerCase()] || "application/octet-stream", "Cache-Control": "no-store", "Content-Security-Policy": inputs.csp, "X-Content-Type-Options": "nosniff" });
    const s = fs.createReadStream(f); s.on("error", () => res.destroy()); s.pipe(res);
  });
  await new Promise((resolve, reject) => { server.once("error", reject); server.listen(PORT, "127.0.0.1", resolve); }); base = `http://127.0.0.1:${PORT}`;
}
function census() {
  const found = [];
  for (const pid of fs.readdirSync("/proc")) {
    if (!/^\d+$/.test(pid)) continue; let exe, stat; try { exe = path.basename(fs.readlinkSync(`/proc/${pid}/exe`)); } catch { continue; }
    if (!/chromium|chrome|headless_shell/i.test(exe)) continue;
    try { stat = fs.readFileSync(`/proc/${pid}/stat`, "utf8"); } catch (e) { if (["ENOENT", "ESRCH"].includes(e.code)) continue; throw e; }
    const startticks = stat.slice(stat.lastIndexOf(") ") + 2).split(" ")[19]; assert.match(startticks, /^\d+$/); found.push({ pid: Number(pid), startticks, exe });
  } return found.sort((a, b) => a.pid - b.pid);
}
async function reap() { let c; for (let i = 0; i < 40; i++) { c = census(); if (!c.length) return c; await new Promise(r => setTimeout(r, 100)); } assert.deepEqual(c, []); return c; }
async function stopOrigin() { if (server?.listening) await bounded(new Promise((resolve, reject) => { server.close(e => e ? reject(e) : resolve()); server.closeIdleConnections(); }), 5000, "origin close timeout"); }
async function refusal() { await new Promise((resolve, reject) => { const r = http.get(base, response => { response.resume(); reject(new Error("origin still alive")); }); r.on("error", e => e.code === "ECONNREFUSED" ? resolve() : reject(e)); r.setTimeout(2000, () => r.destroy(new Error("origin refusal unproven"))); }); }
async function launch(offline) {
  running(); launching = chromium.launchPersistentContext(profile, { headless: true, offline, acceptDownloads: true, timeout: 15000 }); context = await launching; launching = null; running();
  context.on("request", r => { const u = new URL(r.url()); if (/^https?:$/.test(u.protocol) && u.origin !== base) append(record.externalAttempts, { phase, origin: u.origin }); });
  await context.route("**/*", r => new URL(r.request().url()).origin === base ? r.continue() : r.abort());
  await context.addInitScript(() => { window.__focusedCsp = []; window.__focusedCspOverflow = false; addEventListener("securitypolicyviolation", e => { if (window.__focusedCsp.length < 64) window.__focusedCsp.push(e.effectiveDirective); else window.__focusedCspOverflow = true; }); });
  const watch = p => { p.on("worker", w => append(record.workers, { phase, url: w.url() })); p.on("pageerror", e => append(record.pageErrors, { phase, message: String(e.message).slice(0, 1024) })); };
  context.pages().forEach(watch); context.on("page", watch); context.on("response", r => { const u = new URL(r.url()); if (u.origin !== base && r.ok()) append(record.externalSuccesses, { phase, origin: u.origin }); });
  page = await context.newPage(); page.setDefaultTimeout(30000);
}
async function collectCsp() { if (context) for (const p of context.pages()) if (new URL(p.url()).origin === base) { const c = await p.evaluate(() => ({ events: window.__focusedCsp || [], overflow: window.__focusedCspOverflow === true })); if(c.overflow) record.observationOverflow = true; for(const directive of c.events) append(record.violations, {phase,directive}); } }
async function downloadActual(trigger, label) {
  const target = path.join(OUT, label); assert.ok(!fs.existsSync(target)); const event = page.waitForEvent("download", { timeout: 30000 }); const [d] = await Promise.all([event, trigger.click()]);
  assert.equal(await bounded(d.failure(), 30000, "download completion timeout"), null); const staged = await bounded(d.path(), 5000, "download staging timeout"), size = fs.statSync(staged).size;
  assert.ok(size > 0 && size <= 1048576); assert.ok(outputBytes() + size + 262144 <= BUDGET); await bounded(d.saveAs(target), 30000, "saveAs timeout"); const bytes = fs.readFileSync(target); assert.equal(bytes.length, size);
  const info = { artifact: label, suggested: d.suggestedFilename(), ...identity(bytes), failure: null }; record.artifacts.push(info); return { bytes, info };
}
function unzip(bytes) {
  assert.ok(bytes.length >= 22 && bytes.length <= 1048576); let end = -1;
  for (let i = bytes.length - 22; i >= Math.max(0, bytes.length - 65557); i--) if (bytes.readUInt32LE(i) === 0x06054b50 && i + 22 + bytes.readUInt16LE(i + 20) === bytes.length) { end = i; break; }
  assert.ok(end >= 0); assert.equal(bytes.readUInt16LE(end + 4), 0); assert.equal(bytes.readUInt16LE(end + 6), 0); const n = bytes.readUInt16LE(end + 10), start = bytes.readUInt32LE(end + 16); assert.equal(bytes.readUInt16LE(end + 8), n); assert.ok(n > 0 && n <= 32); assert.equal(start + bytes.readUInt32LE(end + 12), end);
  const files = Object.create(null); let at = start, total = 0;
  for (let i = 0; i < n; i++) {
    assert.ok(at + 46 <= end); assert.equal(bytes.readUInt32LE(at), 0x02014b50); const flags = bytes.readUInt16LE(at + 8), method = bytes.readUInt16LE(at + 10), crc = bytes.readUInt32LE(at + 16), packed = bytes.readUInt32LE(at + 20), length = bytes.readUInt32LE(at + 24), nl = bytes.readUInt16LE(at + 28), ex = bytes.readUInt16LE(at + 30), comment = bytes.readUInt16LE(at + 32), local = bytes.readUInt32LE(at + 42);
    assert.ok(!(flags & 1) && [0, 8].includes(method) && nl > 0 && nl <= 200 && length <= 32768); total += length; assert.ok(total <= 32768); assert.ok(at + 46 + nl + ex + comment <= end);
    const name = bytes.subarray(at + 46, at + 46 + nl).toString("utf8"); assert.ok(name.split("/").every(s => /^[A-Za-z0-9._-]+$/.test(s) && ![".", ".."].includes(s))); assert.ok(!Object.hasOwn(files, name));
    assert.ok(local + 30 <= start); assert.equal(bytes.readUInt32LE(local), 0x04034b50); assert.equal(bytes.readUInt16LE(local + 8), method); const ln = bytes.readUInt16LE(local + 26), le = bytes.readUInt16LE(local + 28), data = local + 30 + ln + le;
    assert.equal(bytes.subarray(local + 30, local + 30 + ln).toString("utf8"), name); assert.ok(data + packed <= start); const compressed = bytes.subarray(data, data + packed), plain = method === 0 ? Buffer.from(compressed) : inflateRawSync(compressed, { maxOutputLength: 32768 });
    assert.equal(plain.length, length); assert.equal(crc32(plain), crc); files[name] = plain; at += 46 + nl + ex + comment;
  } assert.equal(at, end); return files;
}


async function projectRecord(id) {
 return page.evaluate(async id=>{const m=await import("./js/workspace-storage.js"),p=await m.getProject(id);if(!p)throw new Error("Missing controlled ProjectId");return p;},id);
}
async function uniqueSetupProject(name) {
 return page.evaluate(async name=>{const m=await import("./js/workspace-storage.js"),ps=(await m.listProjects()).filter(p=>p.name===name);if(ps.length!==1)throw new Error("Unique initial setup name required");return ps[0];},name);
}
async function storageSnapshot(id,label) {
 const raw=await page.evaluate(async id=>{
  const m=await import("./js/workspace-storage.js"),project=await m.getProject(id);if(!project)throw new Error("Missing controlled project");
  const checkpoints=await m.listCheckpoints(id);if(checkpoints.length>64)throw new Error("Checkpoint evidence cap64");
  const todo=[project.headRevisionId,...checkpoints.map(c=>c.revisionId)].filter(Boolean),seen=new Set(),revisions=[],contents={},missingParents=[];
  let declaredTotal=0;
  while(todo.length){const rid=todo.shift();if(seen.has(rid))continue;seen.add(rid);if(seen.size>32)throw new Error("Revision evidence cap32");
   const revision=await m.getRevision(rid);if(!revision){missingParents.push(rid);continue;}
   if(revision.projectId!==id)throw new Error("Foreign revision project");if(revision.files.length>16)throw new Error("Fixture file cap16");
   // Bound all declared metadata before any actual content read for this manifest.
   for(const f of revision.files){if(!Number.isSafeInteger(f.byteLength)||f.byteLength<0||f.byteLength>32768)throw new Error("Invalid fixture declared content length");if(!/^[a-f0-9]{64}$/i.test(f.contentHash))throw new Error("Invalid content hash");}
   for(const f of revision.files){if(!Object.hasOwn(contents,f.contentHash)){declaredTotal+=f.byteLength;if(declaredTotal>65536)throw new Error("Content evidence cap64KiB");
     const bytes=await m.readContent(f.contentHash);if(bytes.length!==f.byteLength)throw new Error("Actual/declared content length differs");contents[f.contentHash]=Array.from(bytes);}}
   revisions.push(revision);if(revision.parentRevisionId)todo.push(revision.parentRevisionId);
  }
  checkpoints.sort((a,b)=>String(a.id).localeCompare(String(b.id)));revisions.sort((a,b)=>String(a.id).localeCompare(String(b.id)));missingParents.sort();
  return {project,checkpoints,revisions,contents,missingParents};
 },id);
 assert.equal(canonical(raw.project.id),canonical(id));
 if(raw.project.headRevisionId)assert.ok(raw.revisions.some(r=>canonical(r.id)===canonical(raw.project.headRevisionId)),"actual durable head manifest exists");
 for(const cp of raw.checkpoints){assert.equal(canonical(cp.projectId),canonical(id));assert.ok(raw.revisions.some(r=>canonical(r.id)===canonical(cp.revisionId)),"checkpoint manifest exists");}
 for(const [hash,bytes]of Object.entries(raw.contents)){assert.equal(sha(Buffer.from(bytes)),hash.toLowerCase(),"actual OPFS content hash");
  const meta=identity(Buffer.from(bytes));if(Object.hasOwn(record.contentObjects,hash))assert.deepEqual(record.contentObjects[hash],meta);else{assert.ok(Object.keys(record.contentObjects).length<64);record.contentObjects[hash]=meta;}}
 for(const revision of raw.revisions){assert.ok(Buffer.byteLength(JSON.stringify(revision))<=16384);const key=canonical(revision.id);
  if(Object.hasOwn(record.immutableRevisions,key))assert.deepEqual(record.immutableRevisions[key],revision,"immutable manifest unchanged");else{assert.ok(Object.keys(record.immutableRevisions).length<64);record.immutableRevisions[key]=revision;}}
 for(const cp of raw.checkpoints){assert.ok(Buffer.byteLength(JSON.stringify(cp))<=4096);const key=canonical(cp.id);
  if(Object.hasOwn(record.checkpoints,key))assert.deepEqual(record.checkpoints[key],cp,"immutable checkpoint record unchanged");else{assert.ok(Object.keys(record.checkpoints).length<128);record.checkpoints[key]=cp;}}
 assert.ok(record.snapshots.length<128);record.snapshots.push({label,project:raw.project,revisionIds:raw.revisions.map(r=>r.id),checkpointIds:raw.checkpoints.map(c=>c.id),contentHashes:Object.keys(raw.contents).sort(),missingParents:raw.missingParents});
 return raw;
}
function assertOnlyName(after,before,name) {
 assert.deepEqual(after.project,{...before.project,name},"only project Name changes; schema/head/config/other metadata exact");
 for(const k of ["checkpoints","revisions","contents","missingParents"])assert.deepEqual(after[k],before[k],k+" exact after rename");
}
function assertHeadFiles(s,expected,ids=null) {
 const r=s.revisions.find(r=>canonical(r.id)===canonical(s.project.headRevisionId));assert.ok(r);assert.deepEqual(r.files.map(f=>f.relativePath).sort(),Object.keys(expected).sort());
 for(const [name,bytes]of Object.entries(expected)){const f=r.files.find(f=>f.relativePath===name);assert.deepEqual(Buffer.from(s.contents[f.contentHash]),bytes,name);assert.equal(f.byteLength,bytes.length);assert.equal(f.mediaKind,name.endsWith(".bin")?"binary":"text");
  if(ids?.[name])assert.equal(canonical(f.id),canonical(ids[name]));}
 return Object.fromEntries(r.files.map(f=>[f.relativePath,f.id]));
}
async function rowFor(id,name) {
 await page.waitForFunction(({id,name})=>{const canon=x=>String(x??"").replace(/-/g,"").toLowerCase();return [...document.querySelectorAll('[data-testid="project-row"]')].filter(r=>r.dataset.name===name&&canon(r.dataset.projectId)===canon(id)).length===1;},{id,name});
 const rows=await H.$(page,"project-row").evaluateAll((rows,{id,name})=>{const canon=x=>String(x??"").replace(/-/g,"").toLowerCase();return rows.filter(r=>r.dataset.name===name&&canon(r.dataset.projectId)===canon(id)).map(r=>({id:r.dataset.projectId,name:r.dataset.name}));},{id,name});
 assert.equal(rows.length,1);assert.ok(rows[0].id);assert.equal(canonical(rows[0].id),canonical(id));assert.equal(rows[0].name,name);
 const row=page.locator(`[data-testid="project-row"][data-project-id=${JSON.stringify(rows[0].id)}][data-name=${JSON.stringify(name)}]`);assert.equal(await row.count(),1);
 assert.equal((await row.locator('[data-testid="open-project"]').textContent()).trim(),name);return {row,identity:rows[0]};
}
async function config() {return page.evaluate(()=>({main:document.querySelector('select[aria-label="Compile main file"]')?.value,engine:document.querySelector('select[aria-label="Compile engine"]')?.value,bibtex:(document.querySelector('[data-testid="bibliography-tool"]') ? document.querySelector('[data-testid="bibliography-tool"]').value === "Bibtex" : document.querySelector('[data-testid="use-bibtex"]')?.checked)??null}));}
async function settleConfiguration(){await H.reveal(page,H.$(page,"refresh-compile-tools"));await page.waitForFunction(()=>document.querySelector('[data-testid="refresh-compile-tools"]')?.disabled===false,null,{timeout:60000});}
async function waitDestination(id,name,file=null,previousSession=lastClosedSession) {
 stage("wait-destination:"+id,"PROJECT_NAVIGATION");
 await page.waitForFunction(name=>document.querySelector(".workspace-header h1")?.textContent.trim()===name&&!!document.querySelector("main.workbench")&&!document.querySelector("main.project-dashboard"),name);
 const p=await projectRecord(id),saved=await storageSnapshot(id,"destination-readonly"),manifest=saved.revisions.find(r=>r.id===p.headRevisionId),target=file===null?null:manifest?.files.find(f=>f.relativePath===file);if(file!==null)assert.ok(target);
 await page.waitForFunction(({name,head,file,previousSession})=>{const s=document.querySelector('[data-testid="analysis-panel"]'),canon=x=>String(x??"").replace(/-/g,"").toLowerCase();return document.querySelector(".workspace-header h1")?.textContent.trim()===name&&!!s?.dataset.session&&(!previousSession||canon(s.dataset.session)!==canon(previousSession))&&canon(s.dataset.head)===canon(head)&&(file===null||[...document.querySelectorAll('[data-testid="select-file"] .file-name')].some(e=>e.textContent.trim()===file));},{name,head:p.headRevisionId,file,previousSession});
 record.navigation.push({action:"destination-loaded",projectId:id,name,stamp:await H.stamp(page),target:target?{id:target.id,path:target.relativePath}:null});return {saved,target};
}
async function selectFile(file) {
 const b=H.$(page,"select-file").filter({hasText:file});await b.waitFor({state:"attached"});assert.equal(await b.count(),1);await b.click();await page.locator('.editor-toolbar label[for="editor"]',{hasText:file}).waitFor();
 assert.equal((await page.locator('.file-tree .files li.active [data-testid="select-file"] .file-name').textContent()).trim(),file);
}
async function assertSelected(saved,file) {
 const r=saved.revisions.find(r=>canonical(r.id)===canonical(saved.project.headRevisionId)),f=r.files.find(f=>f.relativePath===file);assert.ok(f);
 if(f.mediaKind==="binary"){await H.$(page,"binary-view").waitFor();assert.equal(await H.$(page,"editor").count(),0);}
 else{const bytes=Buffer.from(saved.contents[f.contentHash]),text=bytes.toString("utf8").replace(/^\uFEFF/,"").replace(/\r\n?/g,"\n");await page.waitForFunction(({id,text})=>{const e=document.querySelector('[data-testid="editor"]');return e?.dataset.fileId?.replace(/-/g,"").toLowerCase()===String(id).replace(/-/g,"").toLowerCase()&&e.value===text;},{id:f.id,text});assert.equal(await H.$(page,"editor").isEnabled(),true);}
}
async function openProject(id,name,file,{previousSession=lastClosedSession}={}) {
 stage("open-ID:"+id,"PROJECT_NAVIGATION");const requested=await projectRecord(id);assert.equal(requested.name,name);const {row,identity:actual}=await rowFor(id,name),button=row.locator('[data-testid="open-project"]');assert.equal(await button.count(),1);
 const entry={action:"open-identity-row",requested:{id,name,head:requested.headRevisionId},actual};record.navigation.push(entry);save();await button.click();
 const {saved}=await waitDestination(id,name,file,previousSession);await H.reveal(page,H.$(page,"analysis-profile"));await selectFile(file);await assertSelected(saved,file);await settleConfiguration();entry.destination=await H.stamp(page);
 assert.equal(await page.locator(".banner-readonly, .banner-recovery").count(),0);return saved;
}
async function closeProject() {
 const stamp=await H.stamp(page);await page.locator('.workspace-header button[aria-label="Close"]').click();await page.locator("main.project-dashboard").waitFor();await page.waitForFunction(()=>!!document.querySelector("main.project-dashboard")&&!document.querySelector(".workspace-header"));lastClosedSession=stamp.session;record.navigation.push({action:"close-dashboard",session:stamp.session});
}
async function settleSave() {
 assert.equal(await page.locator(".banner-readonly, .banner-recovery").count(),0);const b=H.$(page,"save");let mode="already-saved";
 if(await b.isEnabled()){try{await b.click({timeout:2000});mode="explicit-click";}catch(e){if(!(e instanceof playwrightErrors.TimeoutError)||!(await b.isDisabled()))throw e;mode="autosave-raced-click";}}
 await page.waitForFunction(()=>document.querySelector(".workspace-header .status")?.textContent==="Saved."&&!document.querySelector(".banner-readonly, .banner-recovery"));return {mode,status:"Saved."};
}
async function createProject(name,fixtures) {
 stage("create:"+name,"SETUP");await H.reveal(page,page.locator("#new-project"));await page.locator("#new-project").fill(name);await page.getByRole("button",{name:"Create project",exact:true}).click();
 await page.waitForFunction(name=>document.querySelector(".workspace-header h1")?.textContent.trim()===name,name);const p=await uniqueSetupProject(name);await waitDestination(p.id,name);
 for(const [filename,bytes]of Object.entries(fixtures)){await H.$(page,"import-input").setInputFiles({name:filename,mimeType:"application/octet-stream",buffer:bytes});await page.locator('.editor-toolbar label[for="editor"]',{hasText:filename}).waitFor();await page.waitForFunction(()=>{const s=document.querySelector('[data-testid="single-file-import-status"]');return s?.dataset.state==="imported"&&s.dataset.busy==="false";});await settleSave();}
 const snapshot=await storageSnapshot(p.id,"prepared-project"),ids=assertHeadFiles(snapshot,fixtures);await settleConfiguration();return {id:p.id,snapshot,ids};
}


const PROFILE="knu-csc-2025-source@1.3.0",MARGIN="KNU-SRC-MARGINS-001",FIX="KNU-FIX-MARGIN-LEFT-LITERAL-001";
const PAIR={"rust-generated/document_engine.js":"343ab4007e45157be1efa77ffd0df0d03b761270f9da0eeda68776825f682df5","rust-generated/document_engine_bg.wasm":"20ef29a1160c02d341c04a4f0284c261b3c9dc6070bb7d0097ccc119275d13c7"};
let coldContext=false;const bodyTasks=new Set(),assetScopes=new Map();record.assetObserverScopes=[];record.loadedAssets=[];record.assetObservationErrors=[];record.fixtures={};record.reports=[];record.limits=["Complete articles are analysis input, NOT package/engine/rendered-geometry compile support.","Legacy profiles retain old unknown-context behavior. No native/PDF/provider provisioning or worker is needed.","Only actual delivered original Rust reports; immutable golden response files are never injected.","Normal textarea stale-edit fixture intentionally serializes LF+BOM; positive exact patch goldens retain BOM/CRLF/Unicode."];
function verifyPublishedPair(names){for(const [name,expected]of Object.entries(PAIR)){assert.ok(names.has(name),name);const b=fs.readFileSync(path.join(root,name));assert.equal(sha(b),expected,"new approved Rust pair");if(name.endsWith(".wasm"))assert.deepEqual([...b.subarray(0,4)],[0,97,115,109]);}record.sdk.approvedPair=PAIR;record.sdk.rootReportedPublication={release:"9fWbz/Nt",assetCount:239,archiveSha256:"67e27c013498189b0a5aa842e0f734176fc029f40d63ef3bd16b14d709f5d9bd",archiveProvenance:"root-reported; script independently verifies extracted SDK SRIs/pair"};record.script=identity(fs.readFileSync(__filename));}
function completeArticle(bytes){assert.ok(bytes.subarray(0,3).equals(BOM));const text=bytes.subarray(3).toString("utf8");assert.ok(text.includes("\r\n")&&text.includes("Україна")&&text.includes("🙂"));for(const token of ["\\documentclass{article}","\\begin{document}","Plain body text.","\\end{document}"])assert.ok(text.includes(token),"complete analysis article: "+token);assert.ok(text.indexOf("\\documentclass")<text.indexOf("\\begin{document}"));return bytes;}
function fixture(name){const file=path.join(SOURCE_ROOT,"tests/Workspace.Tests/Fixtures/GeometryPackage",name+".json"),raw=fs.readFileSync(file);assert.ok(raw.length<=262144);const f=JSON.parse(raw);assert.match(f.sourceHex,/^(?:[a-f0-9]{2})+$/i);const bytes=completeArticle(Buffer.from(f.sourceHex,"hex"));assert.equal(sha(bytes),f.sourceSha256);assert.equal(f.request.payload.files.length,1);const input=f.request.payload.files[0];assert.equal(input.byteLength,bytes.length);assert.equal(input.sha256,sha(bytes));assert.equal(input.hasBom,true);assert.equal(input.relativePath,TEXT);assert.deepEqual(f.request.payload.profile,{id:"knu-csc-2025-source",version:"1.3.0"});record.fixtures[name]={json:identity(raw),source:identity(bytes),requestProfile:f.request.payload.profile,body:"complete article"};return bytes;}
function patch(bytes){const at=bytes.indexOf(Buffer.from("20mm"));assert.ok(at>=3);assert.equal(bytes.subarray(at-5,at).toString(),"left=");return Buffer.concat([bytes.subarray(0,at),Buffer.from("25mm"),bytes.subarray(at+4)]);}
async function poll(sample,arg,ms=30000,label="bounded awaited observation"){const deadline=Date.now()+ms;let last;for(;;){last=await bounded(page.evaluate(sample,arg),Math.max(1,Math.min(5000,deadline-Date.now())),label+" sample timeout");if(last?.ready===true)return last;if(Date.now()>=deadline){record.lastPoll={label,last:smallEvidence(last,4096)};throw new Error(label+" deadline");}await new Promise(r=>setTimeout(r,Math.min(25,Math.max(1,deadline-Date.now()))));}}
async function caseObserver(){
 const owner=context;assert.ok(owner&&!assetScopes.has(owner));const scope={id:assetScopes.size+1,owner,cold:coldContext,attached:true,closed:false,tasks:new Set(),handler:null};assetScopes.set(owner,scope);
 scope.handler=response=>{const u=new URL(response.url()),name=u.pathname.replace(/^\//,"");if(u.origin!==base||!Object.hasOwn(PAIR,name))return;const responsePhase=phase,responseUrl=u.href;
  if(!scope.attached||scope.closed){append(record.assetObservationErrors,{scopeId:scope.id,cold:scope.cold,phase:responsePhase,url:responseUrl,message:"Owned response callback invoked after detach/close"},16);return;}
  if(bodyTasks.size>=8){record.observationOverflow=true;return;}
  const task=response.body().then(bytes=>{assert.equal(sha(bytes),PAIR[name],"actual browser-loaded Rust bytes");assert.ok(record.loadedAssets.length<32);record.loadedAssets.push({name,scopeId:scope.id,phase:responsePhase,cold:scope.cold,...identity(bytes),fromServiceWorker:response.fromServiceWorker()});})
   .catch(e=>append(record.assetObservationErrors,{scopeId:scope.id,cold:scope.cold,phase:responsePhase,url:responseUrl,message:String(e.message||e).slice(0,1024)},16))
   .finally(()=>{scope.tasks.delete(task);bodyTasks.delete(task);});scope.tasks.add(task);bodyTasks.add(task);
 };owner.on("response",scope.handler);await owner.addInitScript(()=>{const Native=window.Worker,state=window.__geometryObserver={Native,count:0,seen:[],overflow:false};class Observed extends Native{constructor(url,options){super(url,options);if(String(url).includes("rust-worker.js"))this.addEventListener("message",event=>{if(typeof event.data?.responseJson!=="string")return;try{const p=JSON.parse(event.data.responseJson);if(p.status==="completed"&&p.payload?.profile?.id==="knu-csc-2025-source"){if(event.data.responseJson.length>65536){state.overflow=true;return;}state.count++;state.seen.push({snapshotId:p.payload.snapshotId,count:state.count,raw:event.data.responseJson});if(state.seen.length>8)state.seen.shift();}}catch{state.overflow=true;}});}}state.Observed=Observed;window.Worker=Observed;});}
async function drainAssets(){if(bodyTasks.size)await bounded(Promise.all([...bodyTasks]),10000,"loaded asset observation timeout");cleanSignals();}
async function stopAssetObserver(owner){
 const scope=assetScopes.get(owner);if(!scope)return {installed:false};
 // Detach FIRST: no new reads from this context may register after the drain snapshot.
 if(scope.attached){scope.attached=false;owner.off("response",scope.handler);}
 if(scope.tasks.size)await bounded(Promise.all([...scope.tasks]),10000,"detached context asset drain timeout");
 assert.equal(scope.tasks.size,0,"all registered context reads settled before browser close");
 if(!scope.closed){scope.closed=true;record.assetObserverScopes.push({id:scope.id,cold:scope.cold,listenerDetached:true,pendingReads:scope.tasks.size,closed:true});}
 cleanSignals();return {id:scope.id,cold:scope.cold,listenerDetached:true,pendingReads:0,closed:scope.closed};
}
async function restoreWorkerObserver(){if(page&&!page.isClosed())return page.evaluate(()=>{const s=window.__geometryObserver;if(!s)return {installed:false};if(!s.restored){if(window.Worker!==s.Observed)throw new Error("Owned Worker observer changed");window.Worker=s.Native;s.restored=true;}return {restored:true,count:s.count,overflow:s.overflow};});return null;}
async function caseCleanup(){let first=null;try{record.observerCleanup=await restoreWorkerObserver();}catch(e){first=e;}try{if(context)record.assetObserverCleanup=await stopAssetObserver(context);}catch(e){if(!first)first=e;else record.assetCleanupSecondary=String(e.message||e).slice(0,1024);}if(first)throw first;}
async function selectProfile(value){await H.selectAnalysisProfile(page,value);assert.equal(await H.selectedAnalysisProfile(page),value);}
async function analyze(version="1.3.0"){stage("actual-Rust-analysis-"+version,"REAL_RUST");const before=await page.evaluate(()=>window.__geometryObserver.count);await H.reveal(page,H.$(page,"analyze-now"));await H.$(page,"analyze-now").click();const observed=await poll(before=>{const o=window.__geometryObserver,r=document.querySelector('[data-testid="knu-report"]'),hit=o.seen.findLast(e=>e.count>before&&e.snapshotId===r?.dataset.snapshotId);return {ready:!o.overflow&&!!hit&&document.querySelector('[data-testid="analysis-status"]')?.textContent==="Analysis complete.",count:o.count,overflow:o.overflow,snapshotId:r?.dataset.snapshotId,raw:hit?.raw};},before,30000,"fresh actual analysis/report correlation");const raw=observed.raw,p=JSON.parse(raw).payload;assert.deepEqual(p.profile,{id:"knu-csc-2025-source",version});assert.equal(p.knu.fixDescriptors.length<=1,true);assert.equal(p.snapshotId,observed.snapshotId);const filename=`report-${String(record.reports.length+1).padStart(2,"0")}-${version}.json`;assert.ok(record.reports.length<48);assert.ok(outputBytes()+Buffer.byteLength(raw)+262144<=BUDGET);fs.writeFileSync(path.join(OUT,filename),raw,{flag:"wx"});record.reports.push({artifact:filename,...identity(Buffer.from(raw)),snapshotId:p.snapshotId,profile:p.profile,aggregate:p.aggregateOutcome,ruleOutcomes:p.ruleResults.map(r=>({id:r.ruleId,outcome:r.outcome})),fixes:p.knu.fixDescriptors.map(f=>({id:f.id,ruleVersion:f.ruleVersion,span:f.span,expectedOriginal:f.expectedOriginal,replacement:f.replacement}))});await drainAssets();save();return p;}
const fixButton=()=>page.locator(`[data-testid="preview-fix"][data-fix-id="${FIX}"]`);
async function checkFinding(p,bytes,previousSnapshot=null){assert.equal(p.profile.version,"1.3.0");if(previousSnapshot)assert.notEqual(p.snapshotId,previousSnapshot);const f=p.knu.fixDescriptors[0];assert.ok(f);assert.equal(f.id,FIX);assert.equal(f.ruleId,MARGIN);assert.equal(f.ruleVersion,"1.2.0");assert.equal(f.expectedOriginal,"20mm");assert.equal(f.replacement,"25mm");assert.equal(f.expectedFileSha256,sha(bytes));const at=bytes.indexOf(Buffer.from("20mm"));assert.deepEqual(f.span,{startByte:at,endByte:at+4});const metadata=p.knu.metadata.rules.find(r=>r.ruleId===MARGIN);assert.ok(metadata);assert.equal(metadata.version,"1.2.0");assert.deepEqual(metadata.citation,{section:"3.1",printedPages:[18],pdfPages:[18]});const rows=page.locator(`[data-testid="knu-finding"][data-rule-id="${MARGIN}"]`);assert.equal(await rows.count(),1);assert.equal(Number(await rows.getAttribute("data-start-byte")),at);assert.equal(Number(await rows.getAttribute("data-end-byte")),at+4);assert.equal(await fixButton().count(),1);}
async function preview(id,bytes){const before=await uiSnapshot(id);await H.reveal(page,fixButton());await fixButton().click();await H.$(page,"review-preview").waitFor();await H.idle(page);assert.equal(await H.$(page,"review-edit").count(),1);assert.equal(Number(await H.$(page,"review-edit").getAttribute("data-start-byte")),bytes.indexOf(Buffer.from("20mm")));assert.equal((await H.$(page,"review-expected").textContent()).trim(),"20mm");assert.equal((await H.$(page,"review-replacement").textContent()).trim(),"25mm");assert.match(await H.$(page,"review-citation").textContent(),/KNU-SRC-MARGINS-001 v1\.2\.0; guideline §3\.1; printed pages 18; PDF pages 18/);assert.deepEqual(await uiSnapshot(id),before);return before;}
async function accept(id,bytes,before){await H.$(page,"review-accept").click();await H.action(page,false,true);await settleSave();const saved=await assertSaved(id,{[TEXT]:bytes});const stamp=await H.stamp(page);assert.equal(Number(stamp.generation),Number(before.stamp.generation)+1);assert.equal(stamp.session,before.stamp.session);assert.deepEqual(await config(),before.config);return saved;}
async function decline(id,before){await H.$(page,"review-decline").click();await H.idle(page);await noEnabled("review-accept");assert.deepEqual(await uiSnapshot(id),before);}
async function refuse(id){await H.reveal(page,H.$(page,"review-panel"));const before=await uiSnapshot(id),b=H.$(page,"review-accept");if(await b.count()===1&&await b.isVisible()&&await b.isEnabled())await b.click();await H.idle(page);await noEnabled("review-accept");assert.deepEqual(await uiSnapshot(id),before);}
async function positiveOnline(label,bytes){const name=`Geometry-${label}-${Date.now()}`,a=await createProject(name,{[TEXT]:bytes});if(!record.defaultProfile){assert.equal(await H.$(page,"analysis-profile").inputValue(),"engine-structural");record.defaultProfile="engine-structural";}await selectProfile(PROFILE);let p=await analyze();await checkFinding(p,bytes);const shown=await preview(a.id,bytes);await decline(a.id,shown);p=await analyze();await checkFinding(p,bytes);const before=await preview(a.id,bytes),fixed=patch(bytes),applied=await accept(a.id,fixed,before),fixedReport=await analyze();assert.notEqual(fixedReport.snapshotId,p.snapshotId);assert.equal(fixedReport.knu.fixDescriptors.length,0);assert.equal(fixedReport.ruleResults.find(r=>r.ruleId===MARGIN).outcome,"pass");await H.$(page,"review-undo").click();await H.action(page,true,true);await settleSave();const undone=await assertSaved(a.id,{[TEXT]:bytes},a.ids);assert.notEqual(undone.project.headRevisionId,applied.project.headRevisionId);assert.equal(undone.revisions.some(r=>r.id===applied.project.headRevisionId),true);await noEnabled("review-undo");await exportSource(a.id,{[TEXT]:bytes},"online-"+label);await closeProject();return {...a,label,name,bytes,fixed,appliedHead:applied.project.headRevisionId};}
async function legacy(a){await openProject(a.id,a.name,TEXT);for(const [option,version]of [["knu-csc-2025-source@1.0.0","1.0.0"],["knu-csc-2025-source","1.1.0"],["knu-csc-2025-source@1.2.0","1.2.0"]]){await selectProfile(option);const p=await analyze(version);assert.equal(p.knu.fixDescriptors.length,0);assert.equal(await fixButton().count(),0);assert.ok(p.ruleResults.filter(r=>r.origin==="knu").some(r=>r.outcome==="indeterminate"),"legacy unsupported package context remains indeterminate");await assertSaved(a.id,{[TEXT]:a.bytes},a.ids);}await closeProject();}
async function guard(label,bytes){completeArticle(bytes);const a=await createProject(`Geometry-guard-${label}-${Date.now()}`,{[TEXT]:bytes});await selectProfile(PROFILE);const p=await analyze();assert.equal(p.knu.fixDescriptors.length,0);assert.equal(await fixButton().count(),0);assert.equal(p.ruleResults.find(r=>r.ruleId===MARGIN).outcome,"indeterminate");assert.deepEqual(await storageSnapshot(a.id,"guard-"+label),a.snapshot);await closeProject();}
async function run(){const command=fixture("command-left"),optional=fixture("left-mm"),unknown=fixture("unknown-package"),mixed=fixture("multiple"),dynamic=fixture("dynamic"),duplicate=fixture("duplicate");await boot();const positives=[await positiveOnline("command",command),await positiveOnline("options",optional)];for(const a of positives)await legacy(a);
 await openProject(positives[0].id,positives[0].name,TEXT);await selectProfile(PROFILE);let p=await analyze();await checkFinding(p,command);await preview(positives[0].id,command);await selectProfile("knu-csc-2025-source@1.2.0");await refuse(positives[0].id);await selectProfile(PROFILE);p=await analyze();await preview(positives[0].id,command);await closeProject();await openProject(positives[0].id,positives[0].name,TEXT);await noEnabled("review-accept");await assertSaved(positives[0].id,{[TEXT]:command},positives[0].ids);await closeProject();
 const stale=await createProject(`Geometry-source-stale-${Date.now()}`,{[TEXT]:command});await selectProfile(PROFILE);p=await analyze();await preview(stale.id,command);const edited=command.subarray(3).toString("utf8").replace(/\r\n/g,"\n")+"% Actual user source edit.\n";await H.$(page,"editor").fill(edited);await settleSave();await assertSaved(stale.id,{[TEXT]:Buffer.concat([BOM,Buffer.from(edited)])});await refuse(stale.id);await closeProject();
 for(const [label,bytes]of [["unknown-package",unknown],["mixed-options-command",mixed],["macro",dynamic],["duplicate",duplicate],["import",Buffer.from(command.toString("utf8").replace("\\end{document}","\\input{unresolved-chapter}\r\n\\end{document}"))]])await guard(label,bytes);
 record.online.loadedPair=Object.keys(PAIR).every(name=>record.loadedAssets.some(a=>a.name===name));assert.equal(record.online.loadedPair,true);await drainAssets();record.online.observerCleanup=await restoreWorkerObserver();await coldBoot();
 for(const a of positives){await openProject(a.id,a.name,TEXT,{previousSession:record.cold.previousSession});await assertSaved(a.id,{[TEXT]:a.bytes},a.ids);await selectProfile(PROFILE);p=await analyze();await checkFinding(p,a.bytes);const priorId=p.snapshotId,before=await preview(a.id,a.bytes),changed=await accept(a.id,a.fixed,before);p=await analyze();assert.notEqual(p.snapshotId,priorId);assert.equal(p.knu.fixDescriptors.length,0);assert.equal(p.ruleResults.find(r=>r.ruleId===MARGIN).outcome,"pass");await assertSaved(a.id,{[TEXT]:a.fixed},a.ids);await exportSource(a.id,{[TEXT]:a.fixed},"cold-fixed-"+a.label);const history=await storageSnapshot(a.id,"cold-repeated-full-history-"+a.label);for(const id of [a.snapshot.project.headRevisionId,a.appliedHead,changed.project.headRevisionId])assert.ok(history.revisions.some(r=>r.id===id));record.history.push({projectId:a.id,form:a.label,head:history.project.headRevisionId,revisionIds:history.revisions.map(r=>r.id),checkpointIds:history.checkpoints.map(c=>c.id),sourceEdit:"explicit reviewed exact literal patch",freshResultChanged:true});await closeProject();}
 await drainAssets();for(const name of Object.keys(PAIR))assert.ok(record.loadedAssets.some(a=>a.name===name&&a.cold===true),"actual cold-loaded new Rust asset "+name);await collectCsp();cleanSignals();record.result="PASS";}
async function boot(){inputs=preflight();({chromium,errors:playwrightErrors}=require("playwright"));assert.deepEqual(census(),[]);profile=fs.mkdtempSync(path.join(os.tmpdir(),CASE+"-profile-"));await serve();await launch(false);await caseObserver();const response=await page.goto(base);assert.ok(response?.ok());validateCsp(await response.text(),inputs.headers);assert.equal(response.headers()["content-security-policy"],inputs.csp);await H.ready(page);cleanSignals();save();}
async function coldBoot(){stage("whole-browser-cold","COLD_OFFLINE");await page.waitForFunction(()=>window.__oopOfflineReady===true,null,{timeout:90000});await collectCsp();cleanSignals();record.cold.oldProcesses=census();record.cold.previousSession=lastClosedSession;record.online.assetObserverCleanup=await stopAssetObserver(context);await bounded(context.close(),10000,"close all Chromium timeout");context=null;page=null;record.cold.censusZero=await reap();await stopOrigin();await refusal();record.cold.originRefused=true;assert.deepEqual(census(),[]);coldContext=true;await launch(true);await caseObserver();record.cold.offlineBeforeNavigation=true;record.cold.newProcesses=census();const old=new Set(record.cold.oldProcesses.map(p=>`${p.pid}:${p.startticks}`));assert.ok(record.cold.newProcesses.length&&record.cold.newProcesses.every(p=>!old.has(`${p.pid}:${p.startticks}`)));await page.goto(base);await H.ready(page);}
async function uiSnapshot(id){return {storage:await storageSnapshot(id,"invariance-read"),stamp:await H.stamp(page),config:await config(),view:await page.evaluate(()=>{const e=document.querySelector('[data-testid="editor"]');return {file:e?.dataset.fileId,source:e?.value,start:e?.selectionStart,end:e?.selectionEnd};})};}
async function assertSaved(id,expected,ids=null){const actual=await storageSnapshot(id,"exact-saved-source");assertHeadFiles(actual,expected,ids);return actual;}
async function exportSource(id,expected,label){await selectFile(TEXT);const b=H.$(page,"download");await H.reveal(page,b);const before=await uiSnapshot(id),d=await downloadActual(b,label+".tex");assert.deepEqual(d.bytes,expected[TEXT]);assert.deepEqual(await uiSnapshot(id),before);const z=H.$(page,"export-zip");await H.reveal(page,z);const beforeZip=await uiSnapshot(id),zip=await downloadActual(z,label+".zip"),entries=unzip(zip.bytes);assert.deepEqual(Object.keys(entries).sort(),Object.keys(expected).sort());for(const [n,b]of Object.entries(expected))assert.deepEqual(entries[n],b,n);zip.info.entries=Object.fromEntries(Object.entries(entries).map(([n,b])=>[n,identity(b)]));assert.deepEqual(await uiSnapshot(id),beforeZip);save();}
async function noEnabled(id){for(const b of await H.$(page,id).all())assert.ok(!(await b.isVisible())||await b.isDisabled(),id+" must be unavailable");}
async function failureEvidence(){if(!page||page.isClosed())return;record.failure.ui=await page.evaluate(()=>{const get=id=>document.querySelector(`[data-testid="${id}"]`),s=get("analysis-panel"),e=get("editor");return {title:document.querySelector(".workspace-header h1")?.textContent,stamp:{generation:s?.dataset.generation,session:s?.dataset.session,head:s?.dataset.head},file:e?.dataset.fileId,sourcePreview:e?.value?.slice(0,1024),save:document.querySelector(".workspace-header .status")?.textContent,statuses:Object.fromEntries(["analysis-status","review-panel","review-notice","runtime-status","pdf-runtime-status","compiler-readiness","compile-status","compile-details","preview-status","canvas-status"].map(id=>[id,{text:get(id)?.textContent?.slice(0,1024),state:get(id)?.dataset.state,busy:get(id)?.dataset.busy}]))};});}
save();
(async()=>{let timer;const deadline=new Promise((_,reject)=>{timer=setTimeout(()=>{stopped=true;reject(new Error("harness deadline360s"));},360000);});try{await Promise.race([run(),deadline]);}catch(e){fail(e);save({halt:false});try{await bounded(failureEvidence(),5000,"failure evidence timeout");}catch{}process.exitCode=1;}finally{stopped=true;clearTimeout(timer);try{await caseCleanup();}catch(e){fail(e,"OBSERVER_CLEANUP");process.exitCode=1;}if(launching){try{context=await bounded(launching,20000,"late launch timeout");}catch(e){fail(e,"CLEANUP");process.exitCode=1;}}try{if(context){try{await bounded(collectCsp(),5000,"CSP cleanup timeout");}finally{try{await stopAssetObserver(context);}finally{await bounded(context.close(),10000,"browser cleanup timeout");context=null;}}}}catch(e){fail(e,"CLEANUP");process.exitCode=1;}try{record.cleanup={chromiumRemaining:await reap()};}catch(e){fail(e,"CLEANUP");process.exitCode=1;}try{await stopOrigin();}catch(e){fail(e,"CLEANUP");process.exitCode=1;}if(profile){try{fs.rmSync(profile,{recursive:true,force:true});record.profileCleaned=true;}catch(e){fail(e,"CLEANUP");process.exitCode=1;}}try{cleanSignals();}catch(e){fail(e,"SIGNALS");process.exitCode=1;}save({halt:false});process.stdout.write(JSON.stringify({result:record.result,failure:record.failure})+"\n");}})();
