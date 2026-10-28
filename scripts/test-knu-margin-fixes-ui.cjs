const CASE='knu-margin-fixes',PORT=8778,BUDGET=16*1024*1024;
// Standalone actual-published acceptance. Root executes, agent parser-only.
// Test scaffolding copied as source, never imports another harness IIFE.
// No private C# calls, storage seeding/writes or fake product results.
const assert=require("node:assert/strict"),fs=require("node:fs"),os=require("node:os"),path=require("node:path"),http=require("node:http");
const {createHash}=require("node:crypto"),{inflateRawSync}=require("node:zlib");
const H=require("./review-ui-helpers.cjs"),{validate:validateCsp}=require("./test-csp.cjs"),{crc32,buildZip}=require("./zip-util.cjs");
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
  assert.ok(!record.observationOverflow); assert.equal(record.workers.filter(w => NATIVE.test(w.url)).length, 0, "source-only workflow: no compiler/PDF workers");
  for (const n of ["pageErrors", "violations", "externalAttempts", "externalSuccesses"]) assert.deepEqual(record[n], [], n);
}
function preflight() {
  root = fs.realpathSync(need("PUBLISHED_WWWROOT")); assert.ok(outputBytes() <= BUDGET - 3 * 1024 * 1024);
  const headers = fs.readFileSync(path.join(__dirname, "../docs/examples/static-csp-headers.conf"), "utf8");
  validateCsp(fs.readFileSync(path.join(root, "index.html"), "utf8"), headers);
  const line = headers.split("\n").map(s => s.trim()).find(s => s.startsWith("Content-Security-Policy:")); assert.ok(line);
  const text = fs.readFileSync(path.join(root, "service-worker-assets.js"), "utf8"), match = text.match(/self\.assetsManifest\s*=\s*(\{[\s\S]*\})\s*;/); assert.ok(match);
  const manifest = JSON.parse(match[1]); assert.equal(typeof manifest.version, "string");
  assert.ok(Array.isArray(manifest.assets) && manifest.assets.length > 0 && manifest.assets.length <= 4096);
  const names = new Set();
  for (const a of manifest.assets) {
    assert.equal(typeof a.url, "string"); assert.ok(!path.isAbsolute(a.url) && !a.url.includes("\\") && !a.url.split("/").includes(".."));
    assert.ok(!names.has(a.url)); names.add(a.url); const f = fs.realpathSync(path.join(root, a.url)); assert.ok(f.startsWith(root + path.sep));
    assert.equal("sha256-" + createHash("sha256").update(fs.readFileSync(f)).digest("base64"), a.hash, a.url);
  }
  for (const n of ["index.html", "_framework/blazor.webassembly.js", "js/workspace-storage.js", "js/download.js", "js/offline-readiness.js", "js/editor-select.js"]) assert.ok(names.has(n), n);
  record.sdk = { version: manifest.version, observedAssets: manifest.assets.length, manifestSha256: sha(Buffer.from(text)), everySriVerified: true, strictCsp: true };
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
async function config() {return page.evaluate(()=>({main:document.querySelector('select[aria-label="Compile main file"]')?.value,engine:document.querySelector('select[aria-label="Compile engine"]')?.value,bibtex:document.querySelector('[data-testid="use-bibtex"]')?.checked??null}));}
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

const PROFILE="knu-csc-2025-source@1.2.0",MARGIN="KNU-SRC-MARGINS-001",FIX="KNU-FIX-MARGIN-LEFT-LITERAL-001",F1="KNU-FIX-PARINDENT-LITERAL-001";
const ORIGINAL=Buffer.concat([BOM,Buffer.from("% Україна 😀 literal byte fixture\r\n\\geometry{left=20mm,right=10mm,top=20mm,bottom=20mm}\r\n\\setlength{\\parindent}{1.27cm}\r\n\\section{Методи}\r\n% Unchanged suffix Київ 🙂\r\n","utf8")]);
const VALUE_START=ORIGINAL.indexOf(Buffer.from("20mm")),FIXED=Buffer.concat([ORIGINAL.subarray(0,VALUE_START),Buffer.from("25mm"),ORIGINAL.subarray(VALUE_START+4)]);
const MACRO=Buffer.from(ORIGINAL.toString("utf8").replace("left=20mm","left=\\LeftMargin")),DUPLICATE=Buffer.concat([ORIGINAL,Buffer.from("\\geometry{left=20mm,right=10mm,top=20mm,bottom=20mm}\r\n")]);
record.fixtures={original:identity(ORIGINAL),fixed:identity(FIXED),macro:identity(MACRO),duplicate:identity(DUPLICATE)};record.limits=["Explicit source-literal fix only; rendered compliance unverified.","No fake Rust results; Worker observation adds listeners and preserves original events. No noncooperative/writer/malformed input fault injection."];
async function caseObserver(){await context.addInitScript(()=>{const Native=window.Worker;const state=window.__marginFixObserver={Native,seen:[],count:0,overflow:false};class ObservedWorker extends Native{constructor(url,options){super(url,options);if(String(url).includes("rust-worker.js"))this.addEventListener("message",event=>{if(typeof event.data?.responseJson!=="string")return;try{const value=JSON.parse(event.data.responseJson);if(value.status==="completed"&&value.payload?.profile?.id==="knu-csc-2025-source"){if(event.data.responseJson.length>65536){state.overflow=true;return;}state.count++;state.seen.push({id:value.payload.snapshotId,raw:event.data.responseJson});if(state.seen.length>8)state.seen.shift();}}catch{state.overflow=true;}});}}state.ObservedWorker=ObservedWorker;window.Worker=ObservedWorker;});}
async function caseCleanup(){if(page&&!page.isClosed())record.observerCleanup=await page.evaluate(()=>{const s=window.__marginFixObserver;if(!s)return {installed:false};if(window.Worker!==s.ObservedWorker)throw new Error("Worker observer ownership changed");window.Worker=s.Native;return {restored:true,count:s.count,overflow:s.overflow};});}
async function selectProfile(value){await H.reveal(page,H.$(page,"analysis-profile"));await H.$(page,"analysis-profile").selectOption(value);assert.equal(await H.$(page,"analysis-profile").inputValue(),value);}
async function analyze(version="1.2.0"){stage("real-Rust-analysis-"+version,"RUST_ANALYSIS");const before=await page.evaluate(()=>window.__marginFixObserver.count);await H.reveal(page,H.$(page,"analyze-now"));await H.$(page,"analyze-now").click();await page.waitForFunction(before=>{const o=window.__marginFixObserver,r=document.querySelector('[data-testid="knu-report"]');return !o.overflow&&o.count>before&&document.querySelector('[data-testid="analysis-status"]')?.textContent==="Analysis complete."&&o.seen.some(e=>e.id===r?.dataset.snapshotId);},before);const raw=await page.evaluate(()=>{const o=window.__marginFixObserver,id=document.querySelector('[data-testid="knu-report"]').dataset.snapshotId;return o.seen.findLast(e=>e.id===id).raw;});const p=JSON.parse(raw).payload;assert.deepEqual(p.profile,{id:"knu-csc-2025-source",version});assert.equal(p.knu.fixDescriptors.length<=1,true);record.actions.push({label:"actual-Rust-report",profile:p.profile,snapshotId:p.snapshotId,response:identity(Buffer.from(raw)),fixes:p.knu.fixDescriptors.map(f=>({id:f.id,ruleId:f.ruleId,ruleVersion:f.ruleVersion,fileId:f.fileId,span:f.span,expectedOriginal:f.expectedOriginal,replacement:f.replacement})),margin:p.ruleResults.find(r=>r.ruleId===MARGIN)?.outcome});return p;}
const fixButton=()=>page.locator(`[data-testid="preview-fix"][data-fix-id="${FIX}"]`);
async function finding(p,bytes){const f=p.knu.fixDescriptors[0];assert.ok(f);assert.equal(f.id,FIX);assert.equal(f.ruleId,MARGIN);assert.equal(f.ruleVersion,"1.1.0");assert.equal(f.expectedOriginal,"20mm");assert.equal(f.replacement,"25mm");assert.equal(f.expectedFileSha256,sha(bytes));assert.deepEqual(f.span,{startByte:bytes.indexOf(Buffer.from("20mm")),endByte:bytes.indexOf(Buffer.from("20mm"))+4});const rows=page.locator(`[data-testid="knu-finding"][data-rule-id="${MARGIN}"]`);assert.equal(await rows.count(),1);assert.equal(Number(await rows.getAttribute("data-start-byte")),f.span.startByte);assert.equal(Number(await rows.getAttribute("data-end-byte")),f.span.endByte);assert.equal(await fixButton().count(),1);}
async function preview(id,bytes){const before=await uiSnapshot(id);await H.reveal(page,fixButton());await fixButton().click();await H.$(page,"review-preview").waitFor();await H.idle(page);assert.equal(await H.$(page,"review-edit").count(),1);assert.equal(Number(await H.$(page,"review-edit").getAttribute("data-start-byte")),bytes.indexOf(Buffer.from("20mm")));assert.equal((await H.$(page,"review-expected").innerText()).trim(),"20mm");assert.equal((await H.$(page,"review-replacement").innerText()).trim(),"25mm");assert.match(await H.$(page,"review-citation").innerText(),/KNU-SRC-MARGINS-001 v1\.1\.0; guideline §3\.1; printed pages 18; PDF pages 18/);assert.deepEqual(await uiSnapshot(id),before);return before;}
async function accept(id,expected,before){await H.$(page,"review-accept").click();await H.action(page,false,true);await settleSave();const current=await assertSaved(id,{[TEXT]:expected});const stamp=await H.stamp(page);assert.equal(Number(stamp.generation),Number(before.stamp.generation)+1);assert.equal(stamp.session,before.stamp.session);assert.deepEqual(await config(),before.config);return current;}
async function rejectPreview(id){await H.reveal(page,H.$(page,"review-panel"));const before=await uiSnapshot(id),b=H.$(page,"review-accept");if(await b.count()===1&&await b.isVisible()&&await b.isEnabled())await b.click();await H.idle(page);await noEnabled("review-accept");assert.deepEqual(await uiSnapshot(id),before);}
async function run(){await boot();const name=`KnuFix-${Date.now()}`,a=await createProject(name,{[TEXT]:ORIGINAL});await selectProfile(PROFILE);let p=await analyze();await finding(p,ORIGINAL);await preview(a.id,ORIGINAL);await H.$(page,"review-decline").click();await H.idle(page);assert.deepEqual(await storageSnapshot(a.id,"decline-exact"),a.snapshot);
 p=await analyze();await finding(p,ORIGINAL);const before=await preview(a.id,ORIGINAL),applied=await accept(a.id,FIXED,before);assert.equal(applied.project.headRevisionId!==a.snapshot.project.headRevisionId,true);p=await analyze();assert.equal(p.knu.fixDescriptors.length,0);assert.equal(p.ruleResults.find(r=>r.ruleId===MARGIN).outcome,"pass");await H.$(page,"review-undo").click();await H.action(page,true,true);await settleSave();const undone=await assertSaved(a.id,{[TEXT]:ORIGINAL},a.ids);assert.notEqual(undone.project.headRevisionId,applied.project.headRevisionId);await noEnabled("review-undo");
 p=await analyze();await finding(p,ORIGINAL);await preview(a.id,ORIGINAL);await selectProfile("knu-csc-2025-source");await rejectPreview(a.id);await selectProfile(PROFILE);p=await analyze();await preview(a.id,ORIGINAL);await closeProject();await openProject(a.id,name,TEXT);await noEnabled("review-accept");await closeProject();
 // Stale ordinary edit is isolated so cold A retains original CRLF preimage.
 const staleName=`KnuStale-${Date.now()}`,stale=await createProject(staleName,{[TEXT]:ORIGINAL});await selectProfile(PROFILE);p=await analyze();await preview(stale.id,ORIGINAL);const edited=ORIGINAL.subarray(3).toString("utf8").replace(/\r\n/g,"\n")+"% Actual user edit.\n";await H.$(page,"editor").fill(edited);await settleSave();await assertSaved(stale.id,{[TEXT]:Buffer.concat([BOM,Buffer.from(edited)])});await rejectPreview(stale.id);await closeProject();
 for(const [label,bytes]of [["macro",MACRO],["duplicate",DUPLICATE]]){const item=await createProject(`Knu-${label}-${Date.now()}`,{[TEXT]:bytes});await selectProfile(PROFILE);p=await analyze();assert.equal(p.knu.fixDescriptors.length,0);assert.equal(await fixButton().count(),0);assert.deepEqual(await storageSnapshot(item.id,label+"-unchanged"),item.snapshot);await closeProject();}
 const oldBytes=Buffer.from(ORIGINAL.toString("utf8").replace("1.27cm","10mm")),old=await createProject(`KnuLegacy-${Date.now()}`,{[TEXT]:oldBytes});for(const [option,version,expected]of [["knu-csc-2025-source","1.1.0",[F1]],["knu-csc-2025-source@1.0.0","1.0.0",[]]]){await selectProfile(option);p=await analyze(version);assert.deepEqual(p.knu.fixDescriptors.map(f=>f.id),expected);assert.equal(await fixButton().count(),0);await assertSaved(old.id,{[TEXT]:oldBytes},old.ids);}await closeProject();
 // Historical1.0 treats geometry as unsupported context; prove its original F1
 // independently on supported syntax, never change the legacy engine for a test.
 const legacySupportedBytes=Buffer.concat([BOM,Buffer.from("% Україна 😀 historical supported syntax\r\n\\setlength{\\parindent}{10mm}\r\n\\section{Методи}\r\n","utf8")]);
 const supported=await createProject(`KnuLegacySupported-${Date.now()}`,{[TEXT]:legacySupportedBytes});await selectProfile("knu-csc-2025-source@1.0.0");p=await analyze("1.0.0");assert.deepEqual(p.knu.fixDescriptors.map(f=>f.id),[F1]);assert.equal(await fixButton().count(),0);await assertSaved(supported.id,{[TEXT]:legacySupportedBytes},supported.ids);await closeProject();
 await coldBoot();await openProject(a.id,name,TEXT,{previousSession:record.cold.previousSession});await assertSaved(a.id,{[TEXT]:ORIGINAL},a.ids);await selectProfile(PROFILE);p=await analyze();await finding(p,ORIGINAL);const coldBefore=await preview(a.id,ORIGINAL);await accept(a.id,FIXED,coldBefore);await exportSource(a.id,{[TEXT]:FIXED},"cold-fixed");await selectProfile(PROFILE);p=await analyze();assert.equal(p.knu.fixDescriptors.length,0);const final=await storageSnapshot(a.id,"cold-all-history");assert.ok(final.revisions.some(r=>r.id===a.snapshot.project.headRevisionId));assert.ok(final.revisions.some(r=>r.id===applied.project.headRevisionId));await collectCsp();cleanSignals();record.result="PASS";}

async function boot(){inputs=preflight();({chromium,errors:playwrightErrors}=require("playwright"));assert.deepEqual(census(),[]);profile=fs.mkdtempSync(path.join(os.tmpdir(),CASE+"-profile-"));await serve();await launch(false);await caseObserver();const response=await page.goto(base);assert.ok(response?.ok());validateCsp(await response.text(),inputs.headers);assert.equal(response.headers()["content-security-policy"],inputs.csp);await H.ready(page);cleanSignals();save();}
async function coldBoot(){stage("whole-browser-cold","COLD_OFFLINE");await page.waitForFunction(()=>window.__oopOfflineReady===true,null,{timeout:90000});await collectCsp();cleanSignals();record.cold.oldProcesses=census();record.cold.previousSession=lastClosedSession;await bounded(context.close(),10000,"close all Chromium timeout");context=null;page=null;record.cold.censusZero=await reap();await stopOrigin();await refusal();record.cold.originRefused=true;assert.deepEqual(census(),[]);await launch(true);await caseObserver();record.cold.offlineBeforeNavigation=true;record.cold.newProcesses=census();const old=new Set(record.cold.oldProcesses.map(p=>`${p.pid}:${p.startticks}`));assert.ok(record.cold.newProcesses.length&&record.cold.newProcesses.every(p=>!old.has(`${p.pid}:${p.startticks}`)));await page.goto(base);await H.ready(page);}
async function uiSnapshot(id){return {storage:await storageSnapshot(id,"invariance-read"),stamp:await H.stamp(page),config:await config(),view:await page.evaluate(()=>{const e=document.querySelector('[data-testid="editor"]');return {file:e?.dataset.fileId,source:e?.value,start:e?.selectionStart,end:e?.selectionEnd};})};}
async function assertSaved(id,expected,ids=null){const actual=await storageSnapshot(id,"exact-saved-source");assertHeadFiles(actual,expected,ids);return actual;}
async function exportSource(id,expected,label){await selectFile(TEXT);const b=H.$(page,"download");await H.reveal(page,b);const before=await uiSnapshot(id),d=await downloadActual(b,label+".tex");assert.deepEqual(d.bytes,expected[TEXT]);assert.deepEqual(await uiSnapshot(id),before);const z=H.$(page,"export-zip");await H.reveal(page,z);const beforeZip=await uiSnapshot(id),zip=await downloadActual(z,label+".zip"),entries=unzip(zip.bytes);assert.deepEqual(Object.keys(entries).sort(),Object.keys(expected).sort());for(const [n,b]of Object.entries(expected))assert.deepEqual(entries[n],b,n);zip.info.entries=Object.fromEntries(Object.entries(entries).map(([n,b])=>[n,identity(b)]));assert.deepEqual(await uiSnapshot(id),beforeZip);save();}
async function noEnabled(id){for(const b of await H.$(page,id).all())assert.ok(!(await b.isVisible())||await b.isDisabled(),id+" must be unavailable");}
async function failureEvidence(){if(!page||page.isClosed())return;record.failure.ui=await page.evaluate(()=>{const get=id=>document.querySelector(`[data-testid="${id}"]`),s=get("analysis-panel"),e=get("editor");return {title:document.querySelector(".workspace-header h1")?.textContent,stamp:{generation:s?.dataset.generation,session:s?.dataset.session,head:s?.dataset.head},file:e?.dataset.fileId,sourcePreview:e?.value?.slice(0,1024),save:document.querySelector(".workspace-header .status")?.textContent,statuses:Object.fromEntries(["analysis-status","review-panel","review-notice","runtime-status","pdf-runtime-status","compiler-readiness","compile-status","compile-details","preview-status","canvas-status"].map(id=>[id,{text:get(id)?.textContent?.slice(0,1024),state:get(id)?.dataset.state,busy:get(id)?.dataset.busy}]))};});}
save();
(async()=>{let timer;const deadline=new Promise((_,reject)=>{timer=setTimeout(()=>{stopped=true;reject(new Error("harness deadline360s"));},360000);});try{await Promise.race([run(),deadline]);}catch(e){fail(e);save({halt:false});try{await bounded(failureEvidence(),5000,"failure evidence timeout");}catch{}process.exitCode=1;}finally{stopped=true;clearTimeout(timer);try{await caseCleanup();}catch(e){fail(e,"OBSERVER_CLEANUP");process.exitCode=1;}if(launching){try{context=await bounded(launching,20000,"late launch timeout");}catch(e){fail(e,"CLEANUP");process.exitCode=1;}}try{if(context){try{await bounded(collectCsp(),5000,"CSP cleanup timeout");}finally{await bounded(context.close(),10000,"browser cleanup timeout");context=null;}}}catch(e){fail(e,"CLEANUP");process.exitCode=1;}try{record.cleanup={chromiumRemaining:await reap()};}catch(e){fail(e,"CLEANUP");process.exitCode=1;}try{await stopOrigin();}catch(e){fail(e,"CLEANUP");process.exitCode=1;}if(profile){try{fs.rmSync(profile,{recursive:true,force:true});record.profileCleaned=true;}catch(e){fail(e,"CLEANUP");process.exitCode=1;}}try{cleanSignals();}catch(e){fail(e,"SIGNALS");process.exitCode=1;}save({halt:false});process.stdout.write(JSON.stringify({result:record.result,failure:record.failure})+"\n");}})();
