'use strict';
// MASTER-EXECUTED published-app acceptance tooling. No injected providers,
// workers, responses, facts, storage writes or direct compiler/evaluator calls.
// Required: PUBLISHED_WWWROOT, EXPECTED_SDK_VERSION, CANDIDATE_SOURCE_DIGEST,
// exclusive empty RESULT_DIR. PREFLIGHT_ONLY=1 exits before server/browser.
// SOURCE_BINDING_FIXTURE=ukrainian-included selects the profiled root/include case.
// SOURCE_BINDING_ENGINE=xelatex selects the old fixture's reproducible PDF control.
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),http=require('node:http');
const {createHash}=require('node:crypto'),H=require('./review-ui-helpers.cjs'),{buildZip}=require('./zip-util.cjs');
const {validate:validateCsp}=require('./test-csp.cjs');
const sha=b=>createHash('sha256').update(b).digest('hex'),pin=b=>({length:b.length,sha256:sha(b)});
const MAIN='тези/my thesis.tex',CHAPTER='chapter.tex',P16=Object.freeze({profile:16,count:4557,totalBytes:262320783,
 closure:'8e3fcc0c3255613609a423a5440934b261feee32a5cc73510f4633648b4a135e',
 identity:sha(Buffer.from('native-runtime-profile-v1\nlocal-compiler-input-budget-v1\n8e3fcc0c3255613609a423a5440934b261feee32a5cc73510f4633648b4a135e'))});
const E1=Object.freeze({loader:{length:14103,sha256:'968aae4935523f528e2314c4dbd831b2dd326ac8d3de3444fd033b73463b0225'},
 wasm:{length:3716447,sha256:'8b715ed561ec644757f305ddad0a99ae728c6705f9868e96ccb53c8c9143c4b3'}});
const E1_OBJECT_LAYOUT=Object.freeze({loader:{length:14175,sha256:'f95ae827b12703999ce1daafebbf6830f5f4d2c14bb7805a8f47abe54772b079'},
 wasm:{length:3725577,sha256:'2e2126ead8e00ef7c79d21e3836d66957b19e827523f7c054ca887f3c11ae5b7'}});
function sameHashControl(before,after){
 const cell={id:'same-hash-new-ticket',required:true,status:'not-observed',reason:'Dedicated same-source recompile control was not reached.'};
 const valid=c=>c&&typeof c.hash==='string'&&typeof c.request==='string'&&/^[0-9a-f]{64}$/.test(c.hash)&&/^[0-9a-f]{32}$/.test(c.request);
 if(!valid(before)||!valid(after))return cell;
 cell.before={hash:before.hash,request:before.request};cell.after={hash:after.hash,request:after.request};
 if(before.request===after.request)return {...cell,status:'failed',reason:'A new compile ticket was not observed.'};
 if(before.hash!==after.hash)return {...cell,status:'failed',reason:'Unchanged source produced different PDF hashes; the required same-hash/new-ticket acceptance cell was not observed. No source, clock or native output was changed to manufacture it.'};
 return {...cell,status:'passed',reason:'Distinct actual compile tickets produced the same actual PDF hash.'};
}
function terminalAcceptance(record,completed){
 // Derive the mandatory control from retained actual identities. A caller's
 // sameHashNewTicket boolean or previous PASS label is never sufficient.
 const cell=sameHashControl(record.online?.compile,record.online?.sameSourceCompile);
 const missingAcceptanceCells=cell.status==='passed'?[]:[cell];
 return {result:!completed||record.failure||record.cleanupFailure?'FAIL':missingAcceptanceCells.length?'PARTIAL':'PASS',
  acceptanceCells:{'same-hash-new-ticket':cell},missingAcceptanceCells};
}
function fixture(ukrainian=false){
 const folder=ukrainian?'RenderedRoleBindingUnicode':'RenderedRoleBinding';
 const base=path.join(__dirname,'../tests/Workspace.Tests',folder,'Fixtures');
 const files=ukrainian?[
  ['main.tex',MAIN,'88e98244c8397afb07758875581ff4ce2e21bc58e125b875e27628b82526b4b9','418d074133ae66f81720b67baab662f89e20f4e4244f33684a10d7ae50ed34ab'],
  ['chapter.tex',CHAPTER,'00fdfc1e7c7c8cb36c538c263bef72d59e9ba94fefdf5c3e222a071e7f16ea47','84b2e1d55ebd3edcdde2be2c1280915008d533e4df358631095b5abe551c16c5']]:[
  ['main.tex',MAIN,'e7e3f6cd7128ec650ed3e73a2fb9f2754e8f7c4403a2d912aeb01943d245ce13','d13fafa67790ba9f8d63353b5ee277b80ed50601c52880d7a0ef2a7cef85716a'],
  ['chapter.tex',CHAPTER,'53ff0f1b32c1fc7c2282b4732ca708c76fe6f9cedbeb0c4a7b2e528356791ef6','b21a3969ef92adca041c6d4edfc9197fa97ae05bdb56ce80ba558cacae5c583a']];
 return files
 .map(([name,relative,lfHash,crlfHash])=>{const source=fs.readFileSync(path.join(base,name));assert.equal(sha(source),lfHash);assert.ok(!source.includes(13));
  const bytes=Buffer.from(source.toString('utf8').replace(/\n/g,'\r\n'));assert.equal(sha(bytes),crlfHash);return [relative,bytes];});
}
function local(root,name){assert.ok(typeof name==='string'&&name&&!name.startsWith('/')&&!name.includes('\\')&&!name.split('/').some(p=>!p||p==='.'||p==='..'));
 const f=fs.realpathSync(path.join(root,name));assert.ok(f.startsWith(root+path.sep)&&fs.statSync(f).isFile());return f;}
function preflight(root,sdk,ukrainian=false){
 const headers=fs.readFileSync(path.join(__dirname,'../docs/examples/static-csp-headers.conf'),'utf8');validateCsp(fs.readFileSync(local(root,'index.html'),'utf8'),headers);
 const csp=headers.split('\n').map(s=>s.trim()).find(s=>s.startsWith('Content-Security-Policy:')).slice('Content-Security-Policy:'.length).trim();
 const raw=fs.readFileSync(local(root,'service-worker-assets.js')),match=raw.toString().match(/self\.assetsManifest\s*=\s*(\{[\s\S]*\})\s*;/);assert.ok(match);
 const manifest=JSON.parse(match[1]);assert.equal(manifest.version,sdk);assert.ok(manifest.assets.length>0&&manifest.assets.length<=4096);
 const seen=new Set();for(const a of manifest.assets){assert.ok(!seen.has(a.url));seen.add(a.url);const data=fs.readFileSync(local(root,a.url));assert.equal('sha256-'+createHash('sha256').update(data).digest('base64'),a.hash,a.url);}
 for(const name of ['js/rendered-review-worker.js','js/synctex-query-bridge.js','rust-generated/document_engine_bg.wasm'])assert.ok(seen.has(name),name);
 const renderer={loader:pin(fs.readFileSync(local(root,'bundled-runtime/e1.js'))),wasm:pin(fs.readFileSync(local(root,'bundled-runtime/e1.wasm')))};
 assert.ok([E1,E1_OBJECT_LAYOUT].some(pair=>['loader','wasm'].every(role=>renderer[role].length===pair[role].length
  &&renderer[role].sha256===pair[role].sha256)),'exact admitted E1 renderer required');
 const wasm=fs.readFileSync(local(root,'rust-generated/document_engine_bg.wasm')),exports=WebAssembly.Module.exports(new WebAssembly.Module(wasm)).map(x=>x.name);
 for(const name of ['analyze_source','evaluate_rendered_review'])assert.ok(exports.includes(name),'actual bundled Rust export '+name);
 const zip=fs.readFileSync(local(root,'bundled-runtime/compiler-suite.zip')),members=new Map();let at=0;
 // Only the known application STORED envelope, not a general ZIP reader.
 while(at+30<=zip.length&&zip.readUInt32LE(at)===0x04034b50){assert.equal(zip.readUInt16LE(at+6),0);assert.equal(zip.readUInt16LE(at+8),0);
  const size=zip.readUInt32LE(at+22),nl=zip.readUInt16LE(at+26),el=zip.readUInt16LE(at+28),start=at+30+nl+el;
  assert.equal(zip.readUInt32LE(at+18),size);assert.ok(start+size<=zip.length&&members.size<8192);const name=zip.subarray(at+30,at+30+nl).toString('utf8');
  assert.ok(!members.has(name));members.set(name,zip.subarray(start,start+size));at=start+size;}
 assert.equal(zip.readUInt32LE(at),0x02014b50);const pinsRaw=members.get('pins.json');assert.ok(pinsRaw&&pinsRaw.length<=2097152);const pins=JSON.parse(pinsRaw);
 assert.equal(pins.length,P16.count);assert.equal(members.size,pins.length+1);const names=new Set();
 for(const p of pins){assert.match(p.name,/^[A-Za-z0-9._-]+(?:\/[A-Za-z0-9._-]+)*$/);assert.ok(!p.name.split('/').some(s=>s==='.'||s==='..')&&!names.has(p.name));names.add(p.name);
  assert.ok(Number.isSafeInteger(p.length)&&p.length>=0&&p.length<=134217728);assert.match(p.sha256,/^[0-9a-f]{64}$/);const bytes=members.get('files/'+p.name);assert.ok(bytes);assert.deepEqual(pin(bytes),{length:p.length,sha256:p.sha256});}
 const sorted=pins.slice().sort((a,b)=>a.name<b.name?-1:a.name>b.name?1:0);assert.equal(pins.reduce((n,p)=>n+p.length,0),P16.totalBytes);
 assert.equal(sha(Buffer.from(sorted.map(p=>`${p.name}\t${p.length}\t${p.sha256}`).join('\n'))),P16.closure);
 return {csp,publication:{sdk,assets:manifest.assets.length,manifest:pin(raw),allAssetsSriVerified:true,compilerZip:pin(zip),runtime:P16,renderer,rust:pin(wasm)},
  sources:fixture(ukrainian).map(([name,bytes])=>({name,...pin(bytes)}))};
}
async function main(){
 const need=n=>{assert.ok(process.env[n],n+' required');return process.env[n];};
 const root=fs.realpathSync(need('PUBLISHED_WWWROOT')),out=fs.realpathSync(need('RESULT_DIR'));assert.equal(fs.readdirSync(out).length,0,'exclusive empty result directory');
 const choice=process.env.SOURCE_BINDING_ENGINE||'local-authoring-v4';
 const engineId=new Map([['local-authoring-v4','pdftex'],['xelatex','xelatex']]).get(choice);
 assert.ok(engineId,'SOURCE_BINDING_ENGINE must be pdfLaTeX or XeLaTeX');
 const fixtureMode=process.env.SOURCE_BINDING_FIXTURE||'legacy';
 assert.ok(['legacy','ukrainian-included'].includes(fixtureMode));
 const ukrainian=fixtureMode==='ukrainian-included';
 assert.ok(!ukrainian||engineId==='pdftex','profile 1.7 fixture requires exact P16 pdfLaTeX');
 const version=ukrainian?'1.7.0':'1.6.0';
 const expectedCaptions=ukrainian?[['Іван і Ірина','figureCaption'],['Інші вимірювання','tableCaption']]:[['Orbit comparison','figureCaption']];
 const requiredText=ukrainian?expectedCaptions.map(([text])=>text):['Orbit comparison','Engine comparison'];
 const sdk=need('EXPECTED_SDK_VERSION'),source=need('CANDIDATE_SOURCE_DIGEST');assert.match(source,/^[0-9a-f]{64}$/);assert.ok(['0','1',undefined].includes(process.env.PREFLIGHT_ONLY));
 const record={schemaVersion:1,result:'FAIL',step:'preflight',actualBrowserExecution:false,fixtureMode,engineId,sourceAssociation:{sdk,masterSuppliedSourceDigest:source},inputs:null,publication:null,
  online:{},coldOffline:{},acceptanceCells:{'same-hash-new-ticket':sameHashControl()},missingAcceptanceCells:[sameHashControl()],workers:[],pageErrors:[],externalRequests:[],cspViolations:[],limits:['Synthetic root ASCII captions only; Unicode/emoji are exact CRLF source context.','Second captured file is intentionally unexecuted.','Text/map correlation does not prove section extent, illustration geometry, glyph ink or normative compliance.','Only master executes the actual browser flow. No source-repair or whole-thesis acceptance claim.']};
 if(ukrainian)record.limits.splice(0,2,'Synthetic Ukrainian captions in the exact CRLF root and executed include. P16 pdfLaTeX only.');
 const receipt=path.join(out,'rendered-source-binding-receipt.json'),flush=()=>{const json=JSON.stringify(record,null,2)+'\n';assert.ok(Buffer.byteLength(json)<524288);fs.writeFileSync(receipt,json);};
 const step=s=>{record.step=s;flush();};let server,context,browser,origin,completed=false;const sockets=new Set();
 const stopServer=async()=>{if(!server)return;const current=server;server=null;for(const s of sockets)s.destroy();await new Promise((ok,bad)=>current.close(e=>e?bad(e):ok()));};
 const timeout=setTimeout(()=>{record.failure='Harness deadline600000ms';flush();const force=setTimeout(()=>process.exit(124),5000);Promise.allSettled([context?.close(),stopServer()]).finally(()=>{clearTimeout(force);process.exit(124);});},600000);
 try{
  const checked=preflight(root,sdk,ukrainian);record.publication=checked.publication;record.inputs=checked.sources;record.harness=pin(fs.readFileSync(__filename));flush();
  if(process.env.PREFLIGHT_ONLY==='1'){record.result='PREFLIGHT_ONLY';record.step='Static pins verified; no server/browser/native execution';return;}
  const entries=fixture(ukrainian),project='Source-binding-'+Date.now();record.project=project;
  server=http.createServer((req,res)=>{let file;try{const name=decodeURIComponent(new URL(req.url,'http://localhost').pathname);file=local(root,name==='/'?'index.html':name.slice(1));}catch{res.writeHead(404).end();return;}
   const types={'.html':'text/html','.js':'text/javascript','.json':'application/json','.wasm':'application/wasm','.css':'text/css','.svg':'image/svg+xml','.png':'image/png','.webmanifest':'application/manifest+json'};
   res.writeHead(200,{'Content-Type':types[path.extname(file)]||'application/octet-stream','Content-Security-Policy':checked.csp,'X-Content-Type-Options':'nosniff','Cache-Control':'no-cache'});fs.createReadStream(file).pipe(res);});
  server.on('connection',s=>{sockets.add(s);s.on('close',()=>sockets.delete(s));});await new Promise((ok,bad)=>{server.once('error',bad);server.listen(0,'127.0.0.1',ok);});origin='http://127.0.0.1:'+server.address().port;record.origin=origin;
  const {chromium}=require('playwright');assert.equal(require('playwright/package.json').version,'1.56.1');
  async function launch(offline){context=await chromium.launchPersistentContext(path.join(out,'profile'),{headless:true,offline,acceptDownloads:true});browser=context.browser();record.actualBrowserExecution=true;
   await context.addInitScript(()=>{globalThis.__bindingCsp=[];addEventListener('securitypolicyviolation',e=>globalThis.__bindingCsp.push(e.effectiveDirective));});
   context.on('request',r=>{if(/^https?:/.test(r.url())&&new URL(r.url()).origin!==origin)record.externalRequests.push(new URL(r.url()).origin);});
   await context.route('**/*',route=>/^https?:/.test(route.request().url())&&new URL(route.request().url()).origin!==origin?route.abort('blockedbyclient'):route.continue());
   const attach=p=>{p.setDefaultTimeout(30000);p.on('pageerror',e=>record.pageErrors.push(String(e).slice(0,1000)));p.on('worker',w=>record.workers.push({step:record.step,path:new URL(w.url(),origin).pathname}));};context.on('page',attach);
   const page=await context.newPage();await page.goto(origin+'/');await H.ready(page);return page;}
  const exact=head=>{assert.equal(head.files.length,2);for(const[name,bytes]of entries)assert.deepEqual(Buffer.from(head.files.find(f=>f.path===name)?.bytes||[]),bytes,name+' original exact bytes');};
  const idle=p=>p.waitForFunction(()=>document.querySelector('[data-testid=rendered-review-status]')?.dataset.busy==='false',null,{timeout:130000});
  async function ready(p){await p.waitForFunction(()=>globalThis.__oopOfflineReady===true&&navigator.serviceWorker.controller!==null,null,{timeout:240000});
   await p.waitForFunction(()=>document.querySelector('[data-testid=compiler-readiness]')?.textContent.startsWith('Local compiler checked'),null,{timeout:240000});
   assert.equal(await H.$(p,'runtime-zip-input').count(),0);assert.equal(await H.$(p,'pdf-runtime-manifest').count(),0);exact(await H.head(p,project));}
  async function choose(p,name){const head=await H.head(p,project),file=head.files.find(f=>f.path===name);assert.ok(file);if(await H.$(p,'editor').getAttribute('data-file-id')===file.id)return;
   const button=p.locator('[data-testid=select-file]').filter({hasText:name});assert.equal(await button.count(),1);await button.click();}
  async function compile(p,label){step(label+' native compile');await choose(p,MAIN);await H.reveal(p,p.locator('select[aria-label="Compile main file"]'));await p.locator('select[aria-label="Compile main file"]').selectOption({label:MAIN});
   await p.locator('select[aria-label="Compile engine"]').selectOption(choice);await H.bibliography(p,'None');
   const previous=(await H.$(p,'compile-details').textContent()).match(/Request: ([0-9a-f]{32})/)?.[1]||null,previousGeneration=Number(await H.$(p,'pdf-pane').getAttribute('data-generation'));
   await H.$(p,'compile').click();await p.waitForFunction(old=>{const text=document.querySelector('[data-testid=compile-details]')?.textContent||'',request=text.match(/Request: ([0-9a-f]{32})/)?.[1],pane=document.querySelector('[data-testid=pdf-pane]'),canvas=document.querySelector('[data-testid=pdf-canvas]');
    return request&&request!==old.request&&Number(pane?.dataset.generation)>old.generation&&document.querySelector('[data-testid=compile-panel]')?.dataset.isCurrent==='true'&&/^Compilation finished\./.test(document.querySelector('[data-testid=compile-status]')?.textContent||'')
     &&/^Idle/.test(document.querySelector('[data-testid=compile-work-status]')?.textContent||'')&&document.querySelector('[data-testid=preview-status]')?.dataset.previewStatus==='Available'
     &&document.querySelector('[data-testid=canvas-status]')?.textContent==='Drawn'&&pane?.dataset.document===document.querySelector('[data-testid=compile-panel]')?.dataset.outputHash
     &&canvas?.dataset.drawGeneration===pane?.dataset.generation&&canvas?.dataset.pdfHash===pane?.dataset.document;},{request:previous,generation:previousGeneration},{timeout:150000});
   const text=await H.$(p,'compile-details').textContent(),request=text.match(/Request: ([0-9a-f]{32})/)[1],digest=text.match(/Input: ([0-9a-f]{64})/)[1],hash=await H.$(p,'compile-panel').getAttribute('data-output-hash');
   const observed=await p.evaluate(async()=> (await import('./js/compile-port-bridge.js')).observations()),rows=observed.trace.filter(r=>r.tag.request===request);assert.ok(rows.length>0);
   const expectedTools=choice==='xelatex'?['latex','latex','dvipdfmx']:['latex','latex'];
   assert.deepEqual(rows.map(r=>r.tag.tool),expectedTools);
   for(const r of rows){assert.equal(r.tag.profile,16);assert.equal(r.tag.runtimeIdentity,P16.identity);assert.equal(r.tag.engineId,engineId);assert.equal(r.tag.digest,digest);assert.ok(r.posted&&r.entered&&r.terminated);assert.equal(r.outcome,'native-return');assert.equal(r.nativeReturn,0);}
   assert.equal(observed.active,0);assert.equal(observed.started,observed.settled);exact(await H.head(p,project));
   for(const literal of requiredText)assert.ok((await H.$(p,'page-text').textContent()).includes(literal),literal);
   const [download]=await Promise.all([p.waitForEvent('download'),H.$(p,'download-pdf').click()]);const target=path.join(out,label+'.pdf');await download.saveAs(target);assert.equal(sha(fs.readFileSync(target)),hash);
   return {request,digest,hash,capture:rows[0].tag.capture,epoch:rows[0].tag.epoch,phases:rows,pdf:pin(fs.readFileSync(target))};}
  async function analyze(p){step('explicit source profile '+version+', root and work type');await H.reveal(p,H.$(p,'analysis-profile'));await H.$(p,'analysis-profile').selectOption('knu-csc-2025-source@'+version);
   await H.$(p,'reference-root').selectOption({label:MAIN});await H.$(p,'analysis-work-type').selectOption('bachelorQualification');
   const old=await H.$(p,'thesis-report').getAttribute('data-snapshot-id').catch(()=>null);await H.$(p,'analyze-now').click();
   await p.waitForFunction(old=>{const r=document.querySelector('[data-testid=thesis-report]');return r?.dataset.snapshotId&&r.dataset.snapshotId!==old&&document.querySelector('[data-testid=analysis-status]')?.textContent==='Analysis complete.';},old,{timeout:30000});
   assert.match(await H.$(p,'thesis-selection').textContent(),/my thesis\.tex/);const snapshot=await H.$(p,'thesis-report').getAttribute('data-snapshot-id');exact(await H.head(p,project));return snapshot;}
  async function match(p){await H.reveal(p,H.$(p,'rendered-review-work-type'));await H.$(p,'rendered-review-work-type').selectOption('bachelorQualification');
   const select=H.$(p,'rendered-source-event');await H.reveal(p,select);
   const options=await select.locator('option').evaluateAll(es=>es.map(e=>({value:e.value,text:e.textContent})));
   if(ukrainian)assert.equal(options.filter(o=>o.value).length,expectedCaptions.length);
   const labels=[];
   for(const [text,role] of expectedCaptions){
    const candidate=options.find(o=>o.text==='Caption: '+text);assert.ok(candidate,text);assert.match(candidate.value,/^\d+$/);
    await select.selectOption(candidate.value);const before=record.workers.length;await H.$(p,'rendered-source-bind').click();
    await p.waitForFunction(count=>document.querySelector('[data-testid=rendered-review-status]')?.dataset.busy==='false'&&document.querySelectorAll('[data-testid=rendered-role-row][data-provenance="literalSourceAndNativeMap"]').length===count,labels.length+1,{timeout:130000});
    const label=await H.$(p,'rendered-role-row').filter({has:p.locator('button[aria-label*="Remove"]')}).and(p.locator(`[data-role="${role}"]`)).evaluate(e=>({...e.dataset}));
    assert.equal(label.role,role);assert.equal(label.provenance,'literalSourceAndNativeMap');assert.equal(label.certainty,'confirmedObservation');assert.ok(Number(label.characterEnd)>Number(label.characterStart));
    const metadata=H.$(p,'rendered-source-binding').and(p.locator(`[data-annotation-id="${label.annotationId}"]`));
    assert.equal(await metadata.getAttribute('data-range-kind'),'captionText');assert.equal(await metadata.getAttribute('data-glyph-ink-measured'),'false');
    assert.ok(record.workers.slice(before).some(w=>w.path==='/__oop/native-worker/synctex'),'actual restricted native query worker');labels.push({...label,text});
   }
   exact(await H.head(p,project));return labels;}
  async function review(p,authority,labels){const before=record.workers.length;await H.reveal(p,H.$(p,'rendered-review-check'));await H.$(p,'rendered-review-check').click();
   await p.waitForFunction(()=>document.querySelector('[data-testid=rendered-review-status]')?.dataset.busy==='false'&&document.querySelector('[data-testid=rendered-review-report]')?.dataset.current==='true',null,{timeout:130000});
   const report=H.$(p,'rendered-review-report');assert.equal(await report.getAttribute('data-pdf-sha256'),authority.hash);const identity=await H.$(p,'rendered-review-identity').textContent();
   const renderer=checked.publication.renderer;
   assert.ok(identity.includes(authority.request)&&identity.includes(authority.digest)&&identity.includes(authority.capture)&&identity.includes(renderer.loader.sha256)&&identity.includes(renderer.wasm.sha256));
   const row=report.locator('[data-testid=rendered-review-rule][data-rule-id="KNU-CAPTION-RENDERED-001"]');assert.equal(await row.getAttribute('data-outcome'),'indeterminate');
   const evidence=await row.locator('[data-testid=rendered-review-measurement] pre').evaluateAll(es=>es.map(e=>JSON.parse(e.textContent)));
   const captions=labels.map(label=>{
    const caption=evidence.find(e=>e.measured===label.text);assert.ok(caption,'actual Rust conditional caption observation: '+label.text);
    assert.equal(caption.roleProvenance.annotationId,Number(label.annotationId));assert.equal(caption.roleProvenance.provenance,'literalSourceAndNativeMap');assert.equal(caption.characterStart,Number(label.characterStart));assert.equal(caption.characterEnd,Number(label.characterEnd));
    assert.equal(caption.sourceLocation,null,'native line is not normative source-span precision');assert.equal(caption.observation.kind,'caption');return caption;
   });
   assert.ok(record.workers.slice(before).some(w=>w.path==='/js/rendered-review-worker.js'),'actual Rust evaluation worker');exact(await H.head(p,project));
   return {pdf:authority.hash,annotationVersion:await report.getAttribute('data-annotation-version'),captions};}
  async function stale(p){await p.waitForFunction(()=>document.querySelector('[data-testid=rendered-review-report]')?.dataset.current==='false'&&!!document.querySelector('[data-testid=rendered-role-stale]'),null,{timeout:30000});}
  step('online start');let page=await launch(false);await H.reveal(page,H.$(page,'import-zip'));await H.$(page,'import-zip').setInputFiles({name:'original-source-binding.zip',mimeType:'application/zip',buffer:buildZip(entries)});
  await H.reveal(page,H.$(page,'import-preview'));await H.$(page,'staged-name').fill(project);await H.$(page,'publish-import').click();await ready(page);
  record.online.compile=await compile(page,'online-original');record.online.snapshot=await analyze(page);record.online.label=await match(page);record.online.review=await review(page,record.online.compile,record.online.label);flush();
  record.online.sameSourceCompile=await compile(page,'online-same-source');assert.notEqual(record.online.sameSourceCompile.request,record.online.compile.request);
  assert.equal(await H.$(page,'thesis-report').getAttribute('data-snapshot-id'),record.online.snapshot,'same canonical source report isolates the new compile ticket');await stale(page);
  record.online.sameHashNewTicket=record.online.sameSourceCompile.hash===record.online.compile.hash;
  record.acceptanceCells['same-hash-new-ticket']=sameHashControl(record.online.compile,record.online.sameSourceCompile);
  record.missingAcceptanceCells=record.acceptanceCells['same-hash-new-ticket'].status==='passed'?[]:[record.acceptanceCells['same-hash-new-ticket']];flush();
  // Keep the unmet mandatory control explicit and continue independent real
  // staleness, source-edit/Undo and whole-browser cold-offline observations.
  // Finalization remains PARTIAL (nonzero exit) until this exact control passes.
  const annotationVersion=await H.$(page,'rendered-role-list').getAttribute('data-annotation-version');await H.reveal(page,H.$(page,'rendered-role-confirm'));await H.$(page,'rendered-role-confirm').click();assert.equal(await H.$(page,'rendered-role-list').getAttribute('data-annotation-version'),annotationVersion);assert.equal(await H.$(page,'rendered-review-check').isDisabled(),true);
  await H.$(page,'rendered-role-clear').click();await analyze(page);const label2=await match(page);await review(page,record.online.sameSourceCompile,label2);
  step('ordinary reversible source edit; no appended fixture');assert.equal(await H.$(page,'rendered-review-report').getAttribute('data-current'),'true');const oldText=await H.$(page,'editor').inputValue(),beforeEdit=await H.head(page,project);
  const comment=ukrainian?'Original synthetic':'Explicit synthetic';assert.ok(oldText.includes(comment));
  const edited=oldText.replace(comment,comment.toLowerCase());await H.$(page,'editor').fill(edited);await H.save(page);await stale(page);
  const editedHead=await H.head(page,project);assert.notEqual(editedHead.id,beforeEdit.id);assert.deepEqual(Buffer.from(editedHead.files.find(f=>f.path===MAIN).bytes),Buffer.from(edited,'utf8'));
  await H.reveal(page,H.$(page,'source-undo'));await H.$(page,'source-undo').click();await page.waitForFunction(text=>document.querySelector('[data-testid=editor]')?.value===text,oldText);await H.save(page);
  const undoneHead=await H.head(page,project);assert.notEqual(undoneHead.id,editedHead.id);
  assert.deepEqual(Buffer.from(undoneHead.files.find(f=>f.path===MAIN).bytes),Buffer.from(oldText,'utf8'),'ordinary editor Undo restores text under its LF save policy');
  const history=H.$(page,'history-comparison');await H.reveal(page,history);
  await history.getByRole('checkbox',{name:'Right: current working bytes (including unsaved changes)',exact:true}).check();
  const left=H.$(page,'history-left'),revisionId=id=>String(id).replace(/-/g,'').toLowerCase();
  const prior=await left.locator('option').evaluateAll(es=>es.map(e=>e.value));
  const original=prior.filter(id=>id&&revisionId(id)===revisionId(beforeEdit.id));assert.equal(original.length,1);
  await left.selectOption(original[0]);await H.$(page,'history-compare').click();
  const mainRow=history.locator('[data-testid=history-comparison-files] > li').filter({hasText:MAIN});
  await mainRow.getByRole('checkbox').check();await H.$(page,'history-text-restore-preview').click();
  await H.$(page,'review-preview').waitFor();await H.idle(page);assert.equal(await H.$(page,'review-file').count(),1);
  assert.equal((await H.$(page,'review-path').textContent()).trim(),MAIN);
  await H.$(page,'review-accept').click();await page.waitForFunction(()=>document.querySelector('[data-testid=review-panel]')?.dataset.status==='AppliedSaved');
  const restoredHead=await H.head(page,project);exact(restoredHead);assert.notEqual(restoredHead.id,undoneHead.id);
  record.online.sourceEditUndo={path:MAIN,existingCommentCaseChangeOnly:true,edited:pin(Buffer.from(edited)),editedHead:editedHead.id,undoneHead:undoneHead.id,restoredHead:restoredHead.id,restoredExactOriginalBytesThroughHistory:true};
  record.online.finalCompile=await compile(page,'online-restored');await H.reveal(page,H.$(page,'rendered-role-clear'));await H.$(page,'rendered-role-clear').click();
  await analyze(page);record.online.finalLabel=await match(page);record.online.finalReview=await review(page,record.online.finalCompile,record.online.finalLabel);
  record.cspViolations.push(...await page.evaluate(()=>globalThis.__bindingCsp));step('close every browser, stop owned host');const oldBrowser=browser;await context.close();context=null;if(oldBrowser?.isConnected())await oldBrowser.close();assert.ok(!oldBrowser||!oldBrowser.isConnected());
  let census;for(let n=0;n<40;n++){census=fs.readdirSync('/proc').filter(pid=>/^\d+$/.test(pid)).flatMap(pid=>{try{const exe=path.basename(fs.readlinkSync('/proc/'+pid+'/exe'));return /chromium|chrome|headless_shell/i.test(exe)?[{pid,exe}]:[];}catch{return[];}});if(!census.length)break;await new Promise(r=>setTimeout(r,100));}assert.deepEqual(census,[]);
  await stopServer();await assert.rejects(fetch(origin+'/',{signal:AbortSignal.timeout(5000)}),e=>e.cause?.code==='ECONNREFUSED');record.restart={allBrowserProcessesClosed:true,censusBeforeRelaunch:census,ownedHostStoppedAndRefused:true,offlineBeforeNavigation:true};
  step('cold offline start');page=await launch(true);await H.open(page,project);await ready(page);assert.equal(await H.$(page,'rendered-role-row').count(),0,'session-only machine annotations must not silently revive');
  record.coldOffline.compile=await compile(page,'cold-offline-original');record.coldOffline.snapshot=await analyze(page);record.coldOffline.label=await match(page);record.coldOffline.review=await review(page,record.coldOffline.compile,record.coldOffline.label);
  record.cspViolations.push(...await page.evaluate(()=>globalThis.__bindingCsp));await page.screenshot({path:path.join(out,'cold-source-binding.png'),fullPage:true});
  for(const name of ['pageErrors','externalRequests','cspViolations'])assert.deepEqual(record[name],[],name);completed=true;
 }catch(error){record.failure={step:record.step,message:String(error.stack||error).slice(0,6000)};process.exitCode=1;
  const p=context?.pages().at(-1);if(p&&!p.isClosed())try{record.failure.ui=await p.evaluate(()=>Object.fromEntries(['analysis-status','thesis-report','thesis-selection','rendered-review-status','rendered-source-status'].map(id=>{const e=document.querySelector(`[data-testid="${id}"]`);return[id,{text:e?.textContent?.slice(0,2000),data:e?{...e.dataset}:null}];})));await p.screenshot({path:path.join(out,'failure.png'),fullPage:true});}catch{}
 }
 finally{clearTimeout(timeout);try{await context?.close();await stopServer();}catch(e){record.cleanupFailure=String(e);process.exitCode=1;}if(record.result!=='PREFLIGHT_ONLY'){
  const status=terminalAcceptance(record,completed);
  if(ukrainian){
   status.acceptanceCells['same-hash-new-ticket'].required=false;
   status.missingAcceptanceCells=[];
   if(status.result==='PARTIAL')status.result='PASS';
  }
  Object.assign(record,status);
  if(completed&&!record.failure&&!record.cleanupFailure)record.step=record.result==='PASS'?'Actual registered source binding online and whole-browser host-refused cold offline':'Remaining registered source-binding checks completed; mandatory acceptance cells remain unmet';
  if(record.result!=='PASS')process.exitCode=1;
 }flush();}
}
module.exports={fixture,preflight,sameHashControl,terminalAcceptance,P16,E1};
if(require.main===module)main().catch(e=>{console.error(e);process.exitCode=1;});
