// Actual diagnostic filters/source/compiler/page navigation; full host-stopped offline restart.
// Test tooling only: no provider, worker or storage injection.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const http = require('node:http');
const {createHash} = require('node:crypto');
const {chromium} = require('playwright');
const H = require('./review-ui-helpers.cjs');
const {validate: validateCsp} = require('./test-csp.cjs');
const root = fs.realpathSync(process.env.PUBLISHED_WWWROOT);
const out = fs.realpathSync(process.env.RESULT_DIR);
const receiptPath = path.join(out, 'diagnostics-receipt.json');
fs.closeSync(fs.openSync(receiptPath, 'wx'));
const sha = b => createHash('sha256').update(b).digest('hex');
const cspFile = fs.readFileSync(path.join(__dirname, '../docs/examples/static-csp-headers.conf'), 'utf8');
validateCsp(fs.readFileSync(path.join(root, 'index.html'), 'utf8'), cspFile);
const csp = cspFile.split('\n').map(s => s.trim()).find(s => s.startsWith('Content-Security-Policy:')).slice(24).trim();
const manifest = JSON.parse(fs.readFileSync(path.join(root, 'service-worker-assets.js'), 'utf8')
    .match(/self\.assetsManifest\s*=\s*(\{[\s\S]*\})\s*;/)[1]);
for (const asset of manifest.assets) {
    assert.ok(!path.isAbsolute(asset.url) && !asset.url.split('/').includes('..'));
    assert.equal('sha256-' + createHash('sha256').update(fs.readFileSync(path.join(root, asset.url))).digest('base64'), asset.hash);
}

const {buildZip}=require('./zip-util.cjs');
const record={schemaVersion:1,result:'FAIL',scope:'diagnostics-real-browser-online-cold-offline',sdkVersion:manifest.version,
 online:null,coldOffline:null,step:null,pageErrors:[],externalRequests:[],workers:[],failure:null};
record.limitations=['No held native/DOM callback is manufactured in this run; managed lifecycle tests cover those races.',
 'Historical same-hash page revocation requires a separate real history workflow; no physical PDF rule compliance is inferred.'];
record.harnessSha256=sha(fs.readFileSync(__filename));
const profile=fs.mkdtempSync(path.join(os.tmpdir(),'overclone-diagnostics-'));
const sockets=new Set();let server,context,page;
const origin='http://127.0.0.1:8788';
async function start() {
    const types = {'.js':'text/javascript','.json':'application/json','.wasm':'application/wasm','.css':'text/css','.html':'text/html'};
    server = http.createServer((req,res) => {
        let pathname;
        try { pathname = decodeURIComponent(new URL(req.url, origin).pathname); } catch {res.writeHead(400).end();return;}
        const file = path.resolve(root, '.' + (pathname === '/' ? '/index.html' : pathname));
        if (!file.startsWith(root + path.sep)) {res.writeHead(403).end();return;}
        fs.readFile(file, (error, bytes) => {
            if (error) {res.writeHead(404).end();return;}
            res.writeHead(200, {'Content-Type':types[path.extname(file)] || 'application/octet-stream',
                'Content-Security-Policy':csp, 'Cache-Control':'no-cache'});res.end(bytes);
        });
    });
    server.on('connection', socket => {sockets.add(socket);socket.on('close',()=>sockets.delete(socket));});
    await new Promise(resolve => server.listen(8788,'127.0.0.1',resolve));
}
async function stop() {
    if (!server) return;
    const old=server;server=null;
    for(const socket of sockets)socket.destroy();
    await new Promise(resolve=>old.close(resolve));
}
async function open(offline) {
    context = await chromium.launchPersistentContext(profile,{headless:true,offline});
    page = await context.newPage();page.setDefaultTimeout(30000);
    page.on('pageerror',e=>record.pageErrors.push(String(e).slice(0,512)));
    page.on('request',r=>{if(/^https?:/.test(r.url())&&!r.url().startsWith(origin+'/'))record.externalRequests.push(r.url().slice(0,256));});
    page.on('worker',w=>{
        const observation={url:w.url().replace(origin,''),closed:false};record.workers.push(observation);
        w.on('close',()=>{observation.closed=true;});
    });
    await page.goto(origin+'/');await H.ready(page);
}
function checkpoint(step){record.step=step;fs.writeFileSync(receiptPath,JSON.stringify(record,null,2)+'\n');}

const {fixtures}=require('./knu-fixtures.cjs');
const main='main.tex',chapter='chapter.tex',notes='нотатки/текст файл.tex';
const mainBytes=Buffer.from('\\documentclass{article}\r\n\\begin{document}\r\n\\input{chapter}\r\n\\newpage Second page.\r\n\\newpage Third page.\r\n\\end{document}\r\n');
const badLine='\\overcloneUndefinedCommand % Київ 😀';
const chapterText='% Україна 😀\r\n\\noindent Ordinary ASCII paragraph.\\par\r\n'+badLine+'\r\n';
const original=[[main,mainBytes],[chapter,Buffer.from(chapterText)],[notes,fixtures.negative],['untouched.bin',Buffer.from([0,255,13,10,128])]];
const rows=p=>H.$(p,'diagnostic-row');
async function choose(p,name){await H.$(p,'select-file').filter({has:p.locator('.file-name',{hasText:name})}).click();const id=await p.getByLabel('Compile main file',{exact:true}).locator('option').filter({hasText:name}).getAttribute('value');await p.waitForFunction(id=>document.querySelector('[data-testid=editor]')?.dataset.fileId===id,id);}
async function openFilters(p){await H.reveal(p,H.$(p,'diagnostic-list'));await H.$(p,'diagnostic-filter-reset').click();}
async function filter(p,id,value){await H.$(p,'diagnostic-filter-'+id).selectOption(value);}
async function fileFilter(p,name){const o=H.$(p,'diagnostic-filter-file').locator('option').filter({hasText:name});assert.equal(await o.count(),1);await filter(p,'file',await o.getAttribute('value'));}
async function selection(p,text){try{await p.waitForFunction(text=>{const e=document.querySelector('[data-testid=editor]');return e&&e.value.slice(e.selectionStart,e.selectionEnd)===text;},text);}catch(error){record.failedSelection=await p.evaluate(()=>{const e=document.querySelector('[data-testid=editor]');return{url:location.href,editor:e&&{file:e.dataset.fileId,value:e.value,start:e.selectionStart,end:e.selectionEnd,readonly:e.readOnly},notice:document.querySelector('[data-testid=diagnostic-action-status]')?.textContent,rows:Array.from(document.querySelectorAll('[data-testid=diagnostic-row]')).map(e=>({id:e.dataset.rowId,current:e.dataset.current,text:e.textContent})),body:document.body.innerText.slice(-12000)};});throw error;}return H.$(p,'editor').evaluate(e=>({file:e.dataset.fileId,start:e.selectionStart,end:e.selectionEnd,text:e.value.slice(e.selectionStart,e.selectionEnd)}));}
async function sourceChecks(p,name){
 await H.selectKnu(p);const before=await H.head(p,name),stamp=await H.stamp(p);
 await openFilters(p);await filter(p,'origin','Source');await filter(p,'severity','Warning');await fileFilter(p,notes);await filter(p,'rule','KNU-SRC-PARINDENT-001');await filter(p,'currentness','Current');
 const row=rows(p);assert.equal(await row.count(),1);assert.equal(await row.getAttribute('data-current'),'true');
 await choose(p,main);await row.locator('[data-testid=diagnostic-source]').click();const selected=await selection(p,'10 mm');assert.equal(selected.file,before.files.find(f=>f.path===notes).id);
 assert.deepEqual(await H.head(p,name),before);assert.deepEqual(await H.stamp(p),stamp);
 await filter(p,'origin','Compiler');await H.$(p,'diagnostic-empty-filter').waitFor();assert.equal(await rows(p).count(),0);
 await H.$(p,'diagnostic-filter-reset').click();assert((await rows(p).count())<=50);assert.match(await H.$(p,'diagnostic-origin-status').innerText(),/completeness|limited|unsupported|unavailable/i);
 return {exactUnicodeBomCrlfSelection:selected,allFiveFilters:true,emptyAndReset:true,unchangedHeadGeneration:true};
}
async function compile(p,succeeded){
 await H.reveal(p,H.$(p,'compile'));await H.reveal(p,p.getByLabel('Compile main file',{exact:true}));
 const rootOption=p.getByLabel('Compile main file',{exact:true}).locator('option').filter({hasText:main});await p.getByLabel('Compile main file',{exact:true}).selectOption(await rootOption.getAttribute('value'));
 const engine=p.getByLabel('Compile engine',{exact:true}),pdf=engine.locator('option').filter({hasText:'pdfLaTeX'});assert.equal(await pdf.count(),1);await engine.selectOption(await pdf.getAttribute('value'));
 await H.$(p,'compile').click();await H.reveal(p,H.$(p,'compile-details'));
 // This fixture starts without an attempt, fails, then explicitly repairs and
 // succeeds. The completion notice must match the requested outcome too: Idle
 // and retained diagnostics can still describe the preceding attempt.
 await p.waitForFunction(ok=>document.querySelector('[data-testid=compile-work-status]')?.textContent.trim()==='Idle'&&document.querySelector('[data-testid=compile-status]')?.textContent===(ok?'Compilation finished.':'No new PDF: CompilerFailed.')&&document.querySelector('[data-testid=compile-details]')?.textContent.includes('Outcome: '+(ok?'Succeeded':'CompilerFailed')+';'),succeeded,{timeout:180000});
 const terminal=await H.$(p,'compile-details').textContent();const outcome=terminal.match(/Outcome: ([A-Za-z]+);/)?.[1];
 (record.compiles??=[]).push({step:record.step,outcome,details:terminal.slice(0,3000)});
 if(outcome!==(succeeded?'Succeeded':'CompilerFailed'))record.failedCompile=await p.evaluate(()=>({details:document.querySelector('[data-testid=compile-details]')?.textContent,body:document.body.innerText.slice(-14000)}));
 assert.equal(outcome,succeeded?'Succeeded':'CompilerFailed','Actual terminal compile outcome');
 if(succeeded)await p.waitForFunction(()=>document.querySelector('[data-testid=preview-status]')?.dataset.previewStatus==='Available'&&document.querySelector('[data-testid=canvas-status]')?.textContent==='Drawn',null,{timeout:60000});
}
async function compilerChecks(p,name){
 await compile(p,false);const before=await H.head(p,name),stamp=await H.stamp(p);await openFilters(p);await filter(p,'origin','Compiler');await filter(p,'severity','Error');await fileFilter(p,chapter);
 const row=rows(p).filter({has:p.locator('[data-testid=diagnostic-compiler]')}).first();await row.waitFor();await choose(p,notes);await row.locator('[data-testid=diagnostic-compiler]').click();
 const selected=await selection(p,badLine);assert.equal(selected.file,before.files.find(f=>f.path===chapter).id);
 assert.deepEqual(await H.head(p,name),before);assert.deepEqual(await H.stamp(p),stamp);
 return {genuineFailedCompile:true,exactCapturedLine:selected,unchangedHeadGeneration:true};
}
async function renderedChecks(name){
 const before=await H.head(page,name),stamp=await H.stamp(page);await H.reveal(page,H.$(page,'rendered-extent-work-type'));await H.$(page,'rendered-extent-work-type').selectOption('bachelorQualification');
 await H.$(page,'rendered-section-enable').check();for(const [id,v] of [['intro-start','1'],['intro-end','1'],['body-start','2'],['body-end','2'],['conclusions-start','3'],['conclusions-end','3']])await H.$(page,'section-'+id).fill(v);
 await H.$(page,'section-no-appendices').check();await H.$(page,'section-ranges-confirm').click();await H.$(page,'rendered-extent-check').click();
 await page.waitForFunction(()=>document.querySelector('[data-testid=rendered-extent-result]')?.dataset.current==='true'&&document.querySelector('[data-testid=rendered-extent-status]')?.dataset.busy==='false',null,{timeout:60000});
 await openFilters(page);await filter(page,'origin','Rendered');await filter(page,'severity','Unknown');const pageRow=rows(page).filter({has:page.locator('[data-testid=diagnostic-page]')}).first();await pageRow.waitFor();
 assert.equal(await rows(page).locator('[data-testid=diagnostic-source],[data-testid=diagnostic-compiler]').count(),0);
 await pageRow.locator('[data-testid=diagnostic-page]').click();await page.waitForFunction(()=>document.querySelector('[data-testid=preview-status]')?.dataset.previewStatus==='Available'&&document.querySelector('[data-testid=page-label]')?.textContent==='Page 1 of 3');
 await page.getByRole('button',{name:'Next page',exact:true}).click();await page.waitForFunction(()=>document.querySelector('[data-testid=preview-status]')?.dataset.previewStatus==='Available'&&document.querySelector('[data-testid=page-label]')?.textContent==='Page 2 of 3');
 assert.equal(await rows(page).locator('[data-testid=diagnostic-page]').count(),0,'External page generation must retire old actions');
 await H.$(page,'rendered-extent-work-type').selectOption('masterQualification');await H.$(page,'rendered-extent-work-type').selectOption('bachelorQualification');
 assert(await rows(page).evaluateAll(es=>es.every(e=>e.dataset.current==='false')));assert.equal(await rows(page).locator('[data-testid=diagnostic-page]').count(),0);
 assert.deepEqual(await H.head(page,name),before);assert.deepEqual(await H.stamp(page),stamp);
 return {actualThreePagePdf:true,pageOnlyNavigation:true,externalGenerationRetiresAction:true,workTypeAbaStaysStale:true,unchangedHeadGeneration:true};
}
async function phase(label){
 checkpoint(label+': import exact synthetic project');const name='Diagnostic workflow '+label+' '+Date.now();await H.reveal(page,H.$(page,'import-zip'));await H.$(page,'import-zip').setInputFiles({name:'diagnostics.zip',mimeType:'application/zip',buffer:buildZip(original)});
 await H.reveal(page,H.$(page,'import-preview'));await H.$(page,'import-preview').waitFor({timeout:180000});await H.$(page,'staged-name').fill(name);await H.$(page,'publish-import').click();await H.$(page,'select-file').first().waitFor({timeout:180000});
 await page.waitForFunction(()=>document.querySelector('[data-testid=compiler-readiness]')?.textContent.includes('compilation available.'),null,{timeout:180000});
 const originalHead=await H.head(page,name);for(const [file,bytes] of original)assert.deepEqual(Buffer.from(originalHead.files.find(f=>f.path===file).bytes),bytes);
 checkpoint(label+': source filters and cross-file canonical selection');const source=await sourceChecks(page,name);
 checkpoint(label+': genuine failed-compiler captured-line navigation');const compiler=await compilerChecks(page,name);
 checkpoint(label+': genuine read-only tab');const reader=await context.newPage();reader.setDefaultTimeout(30000);let readonly;
 try{await reader.goto(origin+'/');await H.ready(reader);await H.open(reader,name);await choose(reader,main);await reader.waitForFunction(()=>{const e=document.querySelector('[data-testid=editor]');return e&&(e.readOnly||e.disabled);});readonly={source:await sourceChecks(reader,name),compiler:await compilerChecks(reader,name)};assert.deepEqual(await H.head(reader,name),originalHead);}catch(error){record.failedReader=await reader.evaluate(()=>({compile:document.querySelector('[data-testid=compile-details]')?.textContent,notice:document.querySelector('[data-testid=diagnostic-action-status]')?.textContent,body:document.body.innerText.slice(-14000)})).catch(()=>null);throw error;}finally{await reader.close();}
 checkpoint(label+': explicit repair, successful compile and page-only observation');await choose(page,chapter);const beforeEdit=await H.stamp(page),repaired=chapterText.replace(badLine,'% repaired explicitly').replace(/\r\n/g,'\n');await H.$(page,'editor').fill(repaired);
 await page.waitForFunction(({text,generation})=>document.querySelector('[data-testid=editor]')?.value===text&&document.querySelector('[data-testid=analysis-panel]')?.dataset.generation!==generation,{text:repaired,generation:beforeEdit.generation});await H.save(page);
 const savedRepair=await H.head(page,name);assert.deepEqual(Buffer.from(savedRepair.files.find(f=>f.path===chapter).bytes),Buffer.from(repaired),'The intended repair must be saved in the exact target file before compile');
 await compile(page,true);const rendered=await renderedChecks(name);
 const finalHead=await H.head(page,name);for(const [file,bytes] of original.filter(([file])=>file!==chapter))assert.deepEqual(Buffer.from(finalHead.files.find(f=>f.path===file).bytes),bytes);
 await page.screenshot({path:path.join(out,label+'-diagnostics.png'),fullPage:true});return {label,project:name,source,compiler,readonly,rendered,finalHead};
}
(async()=>{try{await start();await open(false);record.online=await phase('online');await page.evaluate(()=>navigator.serviceWorker.ready);await page.waitForFunction(()=>navigator.serviceWorker.controller!==null&&window.__oopOfflineReady===true,null,{timeout:180000});
 await context.close();context=null;await stop();await assert.rejects(fetch(origin+'/'));await open(true);assert.deepEqual(await H.head(page,record.online.project),record.online.finalHead);record.coldOffline=await phase('cold-offline');
 assert.deepEqual(record.pageErrors,[]);assert.deepEqual(record.externalRequests,[]);record.result='PASS';
 }catch(error){record.failure=String(error.stack||error).slice(0,5000);process.exitCode=1;if(page){record.failureState=await page.evaluate(()=>({body:document.body.innerText.slice(-7000),diagnostics:document.querySelector('[data-testid=diagnostic-action-status]')?.textContent,compile:document.querySelector('[data-testid=compile-status]')?.textContent})).catch(()=>null);await page.screenshot({path:path.join(out,'failure.png'),fullPage:true}).catch(()=>{});}}
 finally{if(context)await context.close().catch(()=>{});await stop();fs.rmSync(profile,{recursive:true,force:true});fs.writeFileSync(receiptPath,JSON.stringify(record,null,2)+'\n');process.stdout.write(JSON.stringify({result:record.result,step:record.step,failure:record.failure})+'\n');}})();
