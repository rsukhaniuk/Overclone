// R2 damaged-head recovery: real registered UI and one labeled raw OPFS deletion.
// Root executes; no product functions/providers/storage exports are patched.
"use strict";
const assert = require('node:assert/strict'), fs = require('node:fs'), path = require('node:path'), http = require('node:http');
const {createHash} = require('node:crypto');
const H = require('./review-ui-helpers.cjs'), {buildZip} = require('./zip-util.cjs'), {validate:validateCsp} = require('./test-csp.cjs');
const sha = b => createHash('sha256').update(b).digest('hex');
const required = key => { assert(process.env[key], 'Required environment: '+key); return process.env[key]; };
const out = fs.realpathSync(required('RESULT_DIR')); assert.equal(fs.readdirSync(out).length,0,'NEW empty exclusive RESULT_DIR required');
fs.writeFileSync(path.join(out,'exclusive.json'),JSON.stringify({pid:process.pid,started:new Date().toISOString()}),{flag:'wx'});
const receiptPath = path.join(out,'corrupt-head-receipt.json');
const record={schemaVersion:1,workPackage:'R2-U-corrupt-head-recovery',result:'FAIL',step:'setup',activeTab:null,harnessSha256:sha(fs.readFileSync(__filename)),runtime:{node:process.version},publication:null,tabs:[],pageErrors:[],externalRequests:[],attempts:[],downloads:[],phases:{},failure:null,limitations:['Synthetic unique C OPFS object deletion ONLY is an explicitly labeled corruption fault. No metadata/IDB writes or exported storage function patches.','Intentionally removed C bytes are NOT preserved; C manifest/head/checkpoint metadata is retained.','RepairHead selects existing B, not a new child. Ordinary later save creates D/E.','No browser eviction/permission/in-flight atomic revocation/general corruption or full R2/MVP claim.']};
let root,origin,port,server,context,browser,active,workflowComplete=false,ownedServer=false;
const sockets=new Set(),ids=new WeakMap(),quotaSessions=new Map(); // lifecycle helper clears empty map; no quota commands here
function flush() { fs.writeFileSync(receiptPath, JSON.stringify(record, null, 2) + "\n"); }

function step(text, page = active) { active = page; record.step = text; record.activeTab = page ? ids.get(page) : null; flush(); }

function attach(page) {
  if (ids.has(page)) return;
  const id = "tab-" + (record.tabs.length + 1); ids.set(page, id); record.tabs.push({ id, closed: false }); page.setDefaultTimeout(30000);
  page.on("close", () => { record.tabs.find(t => t.id === id).closed = true; });
  page.on("pageerror", e => { record.pageErrors.push({ tab: id, message: String(e).slice(0, 2000) }); });
}

function url(raw) { try { return new URL(raw); } catch { return null; } }

function external(raw) { const u = url(raw); return /^https?:/.test(raw) && (!u || u.origin !== origin); }

async function start() {
  const mime = { ".html": "text/html", ".js": "text/javascript", ".json": "application/json", ".css": "text/css", ".wasm": "application/wasm", ".svg": "image/svg+xml" };
  server = http.createServer((req, res) => {
    let file;
    try {
      const pathname = decodeURIComponent(new URL(req.url, origin).pathname);
      file = fs.realpathSync(path.resolve(root, "." + (pathname === "/" ? "/index.html" : pathname)));
      if (!file.startsWith(root + path.sep) || !fs.statSync(file).isFile()) { res.writeHead(403).end(); return; }
    } catch { res.writeHead(404).end(); return; }
    fs.readFile(file, (error, bytes) => {
      if (error) { res.writeHead(404).end(); return; }
      res.writeHead(200, { "Content-Type": mime[path.extname(file)] || "application/octet-stream", "Content-Security-Policy": record.publication.csp, "X-Content-Type-Options": "nosniff", "Cache-Control": "no-cache" }); res.end(bytes);
    });
  });
  server.on("connection", s => { sockets.add(s); s.on("close", () => sockets.delete(s)); });
  await new Promise((resolve, reject) => { server.once("error", reject); server.listen(port, "127.0.0.1", () => { ownedServer = true; resolve(); }); });
}

async function stop() {
  if (!ownedServer) return; ownedServer = false; const old = server; server = null;
  for (const socket of sockets) socket.destroy(); await new Promise((resolve, reject) => old.close(e => e ? reject(e) : resolve()));
}

async function open(offline) {
  const { chromium } = require("playwright");
  context = await chromium.launchPersistentContext(path.join(out, "profile"), { headless: true, offline, acceptDownloads: true,
    permissions: ["clipboard-read", "clipboard-write"] }); // Synthetic test context only: real native paste.
  browser = context.browser(); record.runtime.browser = browser?.version();
  try { record.runtime.playwright = require("playwright/package.json").version; } catch { record.runtime.playwright = "version unavailable"; }
  assert.equal(record.runtime.playwright, "1.56.1", "Root must use the accepted cached Playwright1.56.1");
  context.on("page", attach); for (const page of context.pages()) attach(page);
  context.on("request", r => { if (external(r.url())) { const u = url(r.url()); record.externalRequests.push({ url: u ? u.origin + u.pathname : "invalid URL", method: r.method() }); } });
  await context.route("**/*", route => external(route.request().url()) ? route.abort("blockedbyclient") : route.continue());
  const page = await context.newPage(); attach(page); step(offline ? "cold offline BEFORE navigation" : "online navigation", page);
  await page.goto(origin + "/"); await H.ready(page); return page;
}

async function closeBrowser() {
  const pages = context.pages(), b = browser; await context.close(); context = null;
  if (b?.isConnected()) await b.close(); assert(pages.every(p => p.isClosed())); assert(!b || !b.isConnected()); browser = null; quotaSessions.clear();
  return { tabsClosed: pages.length, browserDisconnected: true };
}
const $=H.$,BOM=Buffer.from([239,187,191]),main='джерело/main.tex',asset='untouched.bin';
const original=Buffer.concat([BOM,Buffer.from('% A Україна🙂 $1 \\literal\r\n\\documentclass{article}\r\n\\begin{document} A \\end{document}\r\n')]);
const binary=Buffer.from([255,0,128,13,10,239,187,191,59]);
const normalize=s=>s.replace(/\r\n?/g,'\n'); const displayed=b=>normalize(b.toString('utf8').replace(/^\uFEFF/,''));
const editedBytes=s=>Buffer.concat([BOM,Buffer.from(normalize(s))]); const entries=[[main,original],[asset,binary]];
const BText=displayed(original)+'% B saved Київ🙂\n',BBytes=editedBytes(BText);
const CText=BText+'% C UNIQUE removable object \u{1f642} 6f389d5b\n',CBytes=editedBytes(CText);

function brief(snapshot) { return { project: snapshot.project, revision: snapshot.revision, files: snapshot.files.map(f => ({ id: f.id, path: f.path, mediaKind: f.mediaKind, hasBom: f.hasBom, length: f.bytes.length, sha256: sha(Buffer.from(f.bytes)) })) }; }
// READ ONLY durable verification, using the same registered storage module as
// existing acceptance helpers. Never use this as the unsaved-text backup.

async function snapshot(page, name, revision = null) {
  return page.evaluate(async ({ name, revision }) => {
    const m = await import("./js/workspace-storage.js"), matches = (await m.listProjects()).filter(p => p.name === name);
    if (matches.length !== 1) throw new Error("Expected one synthetic project: " + name);
    const p = matches[0], r = await m.getRevision(revision || p.headRevisionId);
    return { project: { id: p.id, name: p.name, head: p.headRevisionId, schemaVersion: p.schemaVersion }, revision: { id: r.id, parent: r.parentRevisionId, projectId: r.projectId }, files: await Promise.all(r.files.map(async f => ({ id: f.id, path: f.relativePath, contentHash:f.contentHash, mediaKind: f.mediaKind, hasBom: f.hasBom, bytes: Array.from(await m.readContent(f.contentHash)) }))) };
  }, { name, revision });
}

function exact(s, textBytes) {
  assert.equal(s.files.length, 2); assert.deepEqual(Buffer.from(s.files.find(f => f.path === main)?.bytes || []), textBytes); assert.deepEqual(Buffer.from(s.files.find(f => f.path === asset)?.bytes || []), binary);
}

async function state(page) {
  return page.evaluate(() => {
    const e = document.querySelector("[data-testid=editor]"), a = document.querySelector("[data-testid=analysis-panel]"), save = document.querySelector("[data-testid=save]");
    return { status: document.querySelector(".workspace-header .status")?.textContent, banner: document.querySelector(".banner-readonly")?.textContent, file: e?.dataset.fileId, value: e?.value, readonly: !!e && (e.disabled || e.readOnly), saveDisabled: save?.disabled, generation: a?.dataset.generation, session: a?.dataset.session, head: a?.dataset.head };
  });
}

function stateBrief(s) { const { value, ...rest } = s; return { ...rest, textareaUtf16Units: value?.length, textareaUtf8Sha256: value === undefined ? null : sha(Buffer.from(value)) }; }

async function choose(page, name, expected) {
  active = page; const s = await snapshot(page, name), id = s.files.find(f => f.path === main)?.id; assert(id);
  const target = $(page, "select-file").filter({ has: page.locator(".file-name", { hasText: main }) }); assert.equal(await target.count(), 1); await target.click();
  await page.waitForFunction(({ id, text }) => { const e = document.querySelector("[data-testid=editor]"); return e?.dataset.fileId === id && e.value === text; }, { id, text: displayed(expected) }); return id;
}

async function settled(page) {
  await page.waitForFunction(() => navigator.serviceWorker.controller !== null && window.__oopOfflineReady === true, null, { timeout: 240000 });
  await page.waitForFunction(() => document.querySelector("[data-testid=compiler-readiness]")?.textContent.includes("compilation available."), null, { timeout: 240000 });
  await page.waitForLoadState("networkidle"); // genuine publication/provisioning activity, not a model-edit fence
}

async function importProject(page, name) {
  const close = page.getByRole("button", { name: "Close", exact: true });
  if (await close.count()) { await close.click(); await page.locator(".project-dashboard").waitFor(); }
  await H.reveal(page, $(page, "import-zip")); await $(page, "import-zip").setInputFiles({ name: "synthetic-quota.zip", mimeType: "application/zip", buffer: buildZip(entries) });
  await H.reveal(page, $(page, "import-preview")); await $(page, "staged-name").fill(name); await $(page, "publish-import").click(); await $(page, "select-file").first().waitFor({ timeout: 180000 });
  await settled(page); await choose(page, name, original); const s = await snapshot(page, name); exact(s, original); assert.equal((await state(page)).status, "Saved."); return s;
}

async function fillOnce(page, name, expectedBefore, text, label) {
  const id = await choose(page, name, expectedBefore), before = await state(page); assert.equal(before.readonly, false);
  const attempt = { label, tab: ids.get(page), before: stateBrief(before), intendedUtf16Units: text.length, intendedSavedSha256: sha(editedBytes(text)), inputMechanism: "native clipboard paste, exact checkpoint fixture" }; record.attempts.push(attempt); flush();
  const editor = $(page, "editor");
  await editor.focus();
  await editor.press("Control+A");
  await page.evaluate(async value => {
    await navigator.clipboard.writeText(value);
    const element = document.querySelector('[data-testid="editor"]');
    const observation = { input: 0, beforeinput: 0, types: [] };
    window.__corruptHeadPasteEvents = observation;
    window.__corruptHeadPasteDetach = [];
    for (const name of ["input", "beforeinput"]) {
      const observe = event => {
        observation[name]++;
        observation.types.push({ name, inputType: event.inputType, trusted: event.isTrusted });
      };
      element.addEventListener(name, observe); // Count every event, without mutation.
      window.__corruptHeadPasteDetach.push(() => element.removeEventListener(name, observe));
    }
  }, text);
  await editor.press("Control+V");
  await page.waitForFunction(({ id, text, generation, session }) => {
    const e = document.querySelector("[data-testid=editor]"), a = document.querySelector("[data-testid=analysis-panel]");
    return e?.dataset.fileId === id && e.value === text && a?.dataset.session === session && Number(a?.dataset.generation) > Number(generation);
  }, { id, text, generation: before.generation, session: before.session });
  attempt.modelAcknowledged = stateBrief(await state(page));
  assert.equal(Number(attempt.modelAcknowledged.generation), Number(before.generation) + 1, 'One trusted paste must cause one model edit');
  attempt.nativeEvents = await page.evaluate(() => {
    for (const detach of window.__corruptHeadPasteDetach) detach();
    window.__corruptHeadPasteDetach = [];
    return window.__corruptHeadPasteEvents;
  });
  assert.equal(attempt.nativeEvents.input, 1);
  assert.equal(attempt.nativeEvents.beforeinput, 1);
  assert(attempt.nativeEvents.types.every(e => e.inputType === "insertFromPaste" && e.trusted));
  flush(); return attempt;
}

async function download(page, expected, label) {
  active = page; await H.reveal(page, $(page, "download"));
  const [d] = await Promise.all([page.waitForEvent("download"), $(page, "download").click()]);
  const file = path.join(out, label + ".tex"); await d.saveAs(file); assert.equal(await d.failure(), null); const bytes = fs.readFileSync(file); assert.deepEqual(bytes, expected, "Real Download must contain exact current working bytes, not old saved content");
  const result = { label, tab: ids.get(page), suggestedFilename: d.suggestedFilename(), file: path.basename(file), length: bytes.length, sha256: sha(bytes), exact: true }; record.downloads.push(result); flush(); return result;
}

async function explicitSave(page, name, expected, parent, label) {
  const before = await snapshot(page, name), s = await state(page); assert.equal(before.revision.id, parent);
  assert.equal(s.saveDisabled, false, "No automatic success may be relabeled an explicit retry");
  const attempt = { label, explicitSaveClicks: 1, before: stateBrief(s), parent, clickedAt: new Date().toISOString() }; record.attempts.push(attempt); flush();
  await $(page, "save").click();
  await page.waitForFunction(() => document.querySelector(".workspace-header .status")?.textContent === "Saved." && document.querySelector("[data-testid=save]")?.disabled === true);
  const after = await snapshot(page, name); exact(after, expected); assert.notEqual(after.revision.id, parent); assert.equal(after.revision.parent, parent); assert.equal(after.project.id, before.project.id); assert.equal(after.project.name, name);
  const current = await state(page); assert.equal(current.value, displayed(expected)); assert.equal(current.generation, s.generation); assert.equal(current.head, after.revision.id);
  attempt.after = brief(after); attempt.state = stateBrief(current); flush(); return after;
}

async function captureFailure() {
  const bounded = (promise, milliseconds = 10000) => {
    let timer;
    return Promise.race([promise, new Promise((_, reject) => {
      timer = setTimeout(() => reject(new Error("Failure capture timed out")), milliseconds);
    })]).finally(() => clearTimeout(timer));
  };
  record.failureTabs ||= [];
  for (const page of context?.pages() || []) {
    if (page.isClosed()) continue; const id = ids.get(page); const s = await bounded(state(page)).catch(e => ({ unavailable: String(e) }));
    if (s.value !== undefined) fs.writeFileSync(path.join(out, "failure-" + id + "-textarea.txt"), s.value);
    const body = await bounded(page.locator("body").innerText({ timeout: 10000 })).catch(() => "unavailable"); record.failureTabs.push({ id, active: page === active, url: page.url(), state: stateBrief(s), body: body.slice(0, 45000), exactTextareaFile: "failure-" + id + "-textarea.txt" });
    flush();
    await page.screenshot({ path: path.join(out, "failure-" + id + ".png"), fullPage: true, timeout: 10000 }).catch(() => {});
  }
}

async function metadata(page,name,revision=null) {
  return page.evaluate(async ({name,revision})=>{
    const m=await import('./js/workspace-storage.js'),all=await m.listProjects(),p=all.find(p=>p.name===name);
    if(!p)throw new Error('Synthetic project missing');
    return {project:p,revision:await m.getRevision(revision||p.headRevisionId),checkpoints:await m.listCheckpoints(p.id)};
  },{name,revision});
}
async function dashboard(page) {
  const close=page.getByRole('button',{name:'Close',exact:true});
  if(await close.count()){await close.click();await page.locator('.project-dashboard').waitFor();}
}
async function openProject(page,name) {
  const row=page.locator('[data-testid=project-row]',{hasText:name});assert.equal(await row.count(),1);await row.locator('[data-testid=open-project]').click();
}
async function assertRecovery(page,C,A,B,readonly) {
  await $(page,'recovery-panel').waitFor();assert.equal(await $(page,'recovery-panel').getAttribute('data-damaged-head'),C.revision.id);
  const buttons=$(page,'recover'),offered=await buttons.evaluateAll(es=>es.map(e=>({id:e.dataset.revisionId,disabled:e.disabled})));
  assert(offered.some(e=>e.id===A.revision.id));assert(offered.some(e=>e.id===B.revision.id));assert(!offered.some(e=>e.id===C.revision.id));
  assert(offered.every(e=>e.disabled===readonly));
  assert(await $(page,'editor').evaluateAll(es=>es.every(e=>e.disabled||e.readOnly)),'No editable partial recovered source before action');
  if(readonly)assert.match(await $(page,'recovery-unavailable').textContent(),/read.only|writer/i);
  return {damagedHead:C.revision.id,offered,readonly,noEditablePartialProject:true};
}
async function corruptUniqueC(page,name,A,B,C,U) {
  step('explicit synthetic corruption FAULT: close clean writer, remove ONLY independently verified unique C OPFS object',page);
  const meta=await metadata(page,name,C.revision.id),target=meta.revision.files.find(f=>f.relativePath===main);assert(target);
  assert.equal(target.contentHash,sha(CBytes));assert.equal(target.byteLength,CBytes.length);
  const protectedHashes=new Set([...A.files,...B.files,...U.files].map(f=>sha(Buffer.from(f.bytes))));assert(!protectedHashes.has(target.contentHash));
  await dashboard(page);
  const locks=await page.evaluate(()=>navigator.locks.query());assert(!locks.held.some(l=>l.name==='oop-project:'+C.project.id),'Writer must cleanly release before fault');
  const fault=await context.newPage();attach(fault);step('native OPFS fault page: unique C hash verified before removal',fault);await fault.goto(origin+'/service-worker-assets.js');
  const evidence=await fault.evaluate(async ({hash,length})=>{
    if(!/^[0-9a-f]{64}$/.test(hash))throw new Error('Invalid synthetic hash');
    const root=await navigator.storage.getDirectory(),dir=await root.getDirectoryHandle('content'),handle=await dir.getFileHandle(hash),file=await handle.getFile(),bytes=new Uint8Array(await file.arrayBuffer());
    const digest=Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256',bytes)),b=>b.toString(16).padStart(2,'0')).join('');
    if(digest!==hash||bytes.length!==length)throw new Error('Native object does not match independently saved C');
    await dir.removeEntry(hash);let missing=false,errorName=null;
    try{await dir.getFileHandle(hash);}catch(e){errorName=e.name;missing=e.name==='NotFoundError';}
    if(!missing)throw new Error('Expected native object absence');
    return {scope:'LABELED SYNTHETIC RAW OPFS DELETION FAULT ONLY',path:'content/'+hash,before:{sha256:digest,length:bytes.length},removed:true,missing,errorName};
  },{hash:target.contentHash,length:CBytes.length});
  evidence.protectedHashes=[...protectedHashes];evidence.CMetadataBefore=meta;
  assert.deepEqual(await metadata(page,name,C.revision.id),meta,'Fault must not alter head/manifest/checkpoint metadata');
  await fault.close();active=page;record.phases.corruption=evidence;flush();return meta;
}
async function runWorkflow(page) {
  const suffix=Date.now(),other='Corrupt recovery unrelated '+suffix,name='Corrupt recovery primary '+suffix;
  step('ordinary UI import/save unrelated unique content',page);await importProject(page,other);
  const UText=displayed(original)+'% UNRELATED unique stored text 8a7b Київ🙂\n',UBytes=editedBytes(UText),UA=await snapshot(page,other);
  await fillOnce(page,other,original,UText,'unrelated-native-paste');const U=await explicitSave(page,other,UBytes,UA.revision.id,'unrelated-explicit-save');
  step('ordinary UI exact A import; native whole paste and explicit B then C saves',page);const A=await importProject(page,name);
  await fillOnce(page,name,original,BText,'native-paste-B');const B=await explicitSave(page,name,BBytes,A.revision.id,'save-B');
  await fillOnce(page,name,BBytes,CText,'native-paste-C');const C=await explicitSave(page,name,CBytes,B.revision.id,'save-C');
  record.phases.saved={name,other,A:brief(A),B:brief(B),C:brief(C),U:brief(U)};flush();
  const CMetadata=await corruptUniqueC(page,name,A,B,C,U);
  step('genuine writer opens damaged head; NO automatic repair, exact complete choices',page);await openProject(page,name);
  record.phases.writerDamaged=await assertRecovery(page,C,A,B,false);assert.deepEqual(await metadata(page,name,C.revision.id),CMetadata);
  exact(await snapshot(page,name,A.revision.id),original);exact(await snapshot(page,name,B.revision.id),BBytes);assert.deepEqual(brief(await snapshot(page,other)),brief(U));
  const reader=await context.newPage();attach(reader);step('genuine OTHER app tab readonly recovery cannot advance durable head',reader);
  try {
    await reader.goto(origin+'/');await H.ready(reader);await openProject(reader,name);
    record.phases.readonlyDamaged=await assertRecovery(reader,C,A,B,true);assert.deepEqual(await metadata(reader,name,C.revision.id),CMetadata);
    step('writer explicitly chooses existing B; pointer repair is NOT a new child',page);
    const target=page.locator(`[data-testid=recover][data-revision-id="${B.revision.id}"]`);assert.equal(await target.count(),1);await target.click();
    await $(page,'recovery-panel').waitFor({state:'detached'});await choose(page,name,BBytes);
    await page.waitForFunction(head=>document.querySelector('[data-testid=analysis-panel]')?.dataset.head===head,B.revision.id);
    const repaired=await snapshot(page,name);exact(repaired,BBytes);assert.equal(repaired.revision.id,B.revision.id);assert.equal(repaired.project.head,B.revision.id);
    const after=await metadata(page,name,C.revision.id);assert.deepEqual(after.revision,CMetadata.revision);assert.deepEqual(after.checkpoints,CMetadata.checkpoints);assert.equal(after.project.headRevisionId,B.revision.id);
    exact(await snapshot(page,name,A.revision.id),original);assert.deepEqual(brief(await snapshot(page,other)),brief(U));await download(page,BBytes,'recovered-existing-B');
    assert(await $(reader,'recover').evaluateAll(es=>es.every(e=>e.disabled)));assert.equal((await metadata(reader,name)).project.headRevisionId,B.revision.id);
    step('normal fresh edit/save AFTER recovery creates child D; readonly old prompt never writes',page);
    const DText=BText+'% D after explicit repair Україна🙂\n',DBytes=editedBytes(DText);
    await fillOnce(page,name,BBytes,DText,'native-paste-after-repair');const D=await explicitSave(page,name,DBytes,B.revision.id,'normal-child-D');await download(page,DBytes,'saved-child-D');
    assert(await $(reader,'recover').evaluateAll(es=>es.every(e=>e.disabled)));assert.equal((await metadata(reader,name)).project.headRevisionId,D.revision.id);
    record.phases.repaired={B:brief(repaired),D:brief(D),originalCMetadataRetained:true,readonlyStaleButtonsNeverEnabled:true,repairPointerPolicy:true};flush();
    await page.screenshot({path:path.join(out,'online-recovered-D.png'),fullPage:true});
    return {name,other,A,B,C,D,U,UBytes,DBytes,CMetadata};
  } catch(error){record.failureBeforeReaderClose=String(error.stack||error);flush();await captureFailure();throw error;}
  finally{await reader.close();active=page;}
}
(async()=>{
  flush();
  try {
    root=fs.realpathSync(required('PUBLISHED_WWWROOT'));assert(root!==out&&!out.startsWith(root+path.sep));
    const expectedSdk=required('EXPECTED_SDK_VERSION'),source=required('CANDIDATE_SOURCE_DIGEST');assert.match(source,/^[0-9a-f]{64}$/);
    port=Number(process.env.TEST_PORT||8791);assert(Number.isInteger(port)&&port>1024&&port<65536);origin='http://127.0.0.1:'+port;
    const headers=fs.readFileSync(path.join(__dirname,'../docs/examples/static-csp-headers.conf'),'utf8');validateCsp(fs.readFileSync(path.join(root,'index.html'),'utf8'),headers);
    const csp=headers.split('\n').map(x=>x.trim()).find(x=>x.startsWith('Content-Security-Policy:')).slice('Content-Security-Policy:'.length).trim();
    const raw=fs.readFileSync(path.join(root,'service-worker-assets.js')),manifest=JSON.parse(raw.toString().match(/self\.assetsManifest\s*=\s*(\{[\s\S]*\})\s*;/)[1]);assert.equal(manifest.version,expectedSdk);
    const seen=new Set();for(const a of manifest.assets){assert(!path.isAbsolute(a.url)&&!a.url.split('/').includes('..')&&!seen.has(a.url));seen.add(a.url);const file=fs.realpathSync(path.resolve(root,a.url));assert(file.startsWith(root+path.sep));assert.equal('sha256-'+createHash('sha256').update(fs.readFileSync(file)).digest('base64'),a.hash,a.url);}
    fs.writeFileSync(path.join(out,'verified-assets-manifest.json'),JSON.stringify(manifest,null,2)+'\n');fs.writeFileSync(path.join(out,'synthetic-fixture.zip'),buildZip(entries));
    record.publication={sdkVersion:manifest.version,manifestSha256:sha(raw),candidateSourceDigest:source,assetsVerified:manifest.assets.length,csp,cspSha256:sha(headers),origin};
    record.tooling=Object.fromEntries(['review-ui-helpers.cjs','zip-util.cjs','test-csp.cjs','test-r2-save-recovery-ui.cjs'].map(f=>[f,sha(fs.readFileSync(path.join(__dirname,f)))]));record.fixture={ASha256:sha(original),BSha256:sha(BBytes),CSha256:sha(CBytes),binarySha256:sha(binary)};flush();
    await start();let page=await open(false);const s=await runWorkflow(page);
    step('all source saved/downloaded; whole browser close, owned host stopped/refused',page);await settled(page);const closed=await closeBrowser();await stop();
    let refusal;try{await fetch(origin+'/',{signal:AbortSignal.timeout(5000)});assert.fail('Origin answered after stop');}catch(e){assert.equal(e.cause?.code,'ECONNREFUSED');refusal=e.cause.code;}
    record.restart={...closed,hostStopped:true,refusal,offlineAppliedBeforeNavigation:true};flush();
    page=await open(true);step('cold offline: D/current, older B/A and unrelated/binary exact; no recovery reappears',page);
    assert.deepEqual(brief(await snapshot(page,s.name)),brief(s.D));exact(await snapshot(page,s.name,s.A.revision.id),original);exact(await snapshot(page,s.name,s.B.revision.id),BBytes);assert.deepEqual(brief(await snapshot(page,s.other)),brief(s.U));
    const cmeta=await metadata(page,s.name,s.C.revision.id);assert.deepEqual(cmeta.revision,s.CMetadata.revision);
    await openProject(page,s.name);await choose(page,s.name,s.DBytes);assert.equal(await $(page,'recovery-panel').count(),0);
    const EText=displayed(s.DBytes)+'% E cold-offline native paste Київ🙂\n',EBytes=editedBytes(EText);
    await fillOnce(page,s.name,s.DBytes,EText,'cold-offline-native-paste');const E=await explicitSave(page,s.name,EBytes,s.D.revision.id,'cold-offline-child-E');await download(page,EBytes,'cold-offline-E');
    exact(await snapshot(page,s.name,s.A.revision.id),original);exact(await snapshot(page,s.name,s.B.revision.id),BBytes);assert.deepEqual(brief(await snapshot(page,s.other)),brief(s.U));assert.equal(await $(page,'recovery-panel').count(),0);
    record.phases.coldOffline={E:brief(E),AAndBReadable:true,unrelatedExact:true,noRecoveryForRepairedHead:true};await page.screenshot({path:path.join(out,'cold-offline-E.png'),fullPage:true});
    assert.deepEqual(record.externalRequests,[]);assert.deepEqual(record.pageErrors,[]);workflowComplete=true;
  } catch(error){record.result='FAIL';record.failure={message:String(error.stack||error),cause:String(error.cause?.stack||error.cause||'')};record.captureStatus='pending';process.exitCode=1;flush();await captureFailure().catch(e=>{record.captureError=String(e);});record.captureStatus='finished-or-bounded-timeout';flush();}
  finally {
    if(context)try{await closeBrowser();}catch(e){record.cleanupFailure=String(e);process.exitCode=1;}
    try{await stop();}catch(e){record.serverCleanupFailure=String(e);process.exitCode=1;}
    if(workflowComplete&&!record.cleanupFailure&&!record.serverCleanupFailure){record.result='PASS';record.step='PASS bounded explicit damaged-head recovery online/full cold offline';}
    flush();process.stdout.write(JSON.stringify({result:record.result,step:record.step,receipt:receiptPath,failure:record.failure?{message:record.failure.message.slice(0,3000),cause:record.failure.cause.slice(0,1000)}:null})+'\n');
  }
})();
