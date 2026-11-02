// Real registered source search/replacement/thesis review with full host-stopped offline restart.
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
const receiptPath = path.join(out, 'source-workflows-receipt.json');
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
const record={schemaVersion:1,result:'FAIL',scope:'source-search-replacement-thesis-real-browser-online-cold-offline',sdkVersion:manifest.version,
 online:null,coldOffline:null,maximum:null,files5000:null,step:null,pageErrors:[],externalRequests:[],workers:[],failure:null};
const profile=fs.mkdtempSync(path.join(os.tmpdir(),'overclone-source-workflows-'));
const sockets=new Set();let server,context,page;
const origin='http://127.0.0.1:8788';
function checkpoint(step){record.step=step;fs.writeFileSync(receiptPath,JSON.stringify(record,null,2)+'\n');}
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
const main='main.tex',thesis='теза/основний.tex',BOM=Buffer.from([239,187,191]);
const a=Buffer.concat([BOM,Buffer.from('old🙂\r\nold\r\n')]),b=Buffer.from('keep é\r\nold\r\n'),replacement='new🙂$1\\';
const rootText=['\\documentclass{article}','\\begin{document}','\\maketitle','\\section*{РЕФЕРАТ}','\\tableofcontents','\\section*{ВСТУП}','\\input{chapter}','\\section*{ВИСНОВКИ}','\\bibliography{refs}','\\section*{ДОДАТОК A}','\\end{document}',''].join('\r\n');
const files=[[main,a],['notes.tex',b],['unrelated.txt',Buffer.from('unchanged\r\n')],['asset.bin',Buffer.from([255,0,128,9])],
 [thesis,Buffer.concat([BOM,Buffer.from(rootText)])],['chapter.tex',Buffer.from('\\section{Огляд🙂}\n\\cite{ключ😀}\n\\begin{figure}\\caption{}\\end{figure}\n')],
 ['refs.bib',Buffer.from('@book{ключ😀,title={Синтетичне джерело}}\n')]];
const normalize=s=>s.replace(/\r\n/g,'\n').replace(/\r/g,'\n');
async function closeProject(){if(await page.getByRole('button',{name:'Close',exact:true}).count()){await page.getByRole('button',{name:'Close',exact:true}).click();await page.locator('.project-dashboard').waitFor();}}
async function importZip(name,buffer){await closeProject();await H.$(page,'import-zip').setInputFiles({name:'synthetic.zip',mimeType:'application/zip',buffer});
 await H.reveal(page,H.$(page,'import-preview'));await H.$(page,'import-preview').waitFor({timeout:180000});await H.$(page,'staged-name').fill(name);await H.$(page,'publish-import').click();await H.$(page,'select-file').first().waitFor({timeout:180000});}
async function choose(name,p=page){await H.$(p,'select-file').filter({has:p.locator('.file-name',{hasText:name})}).click();}
async function status(want,p=page){await p.waitForFunction(want=>document.querySelector('[data-testid=project-search-status]')?.dataset.searchStatus===want,want,{timeout:180000});}
async function search(query,p=page){await H.reveal(p,H.$(p,'project-search-query'));await H.$(p,'project-search-query').fill(query);await H.$(p,'project-search-submit').click();await status('Complete',p);await H.$(p,'project-search-submit').waitFor({state:'visible'});}
const file=(head,path)=>{const v=head.files.find(f=>f.path===path);assert(v,path);return Buffer.from(v.bytes);};
async function analysisProfile(p=page){await H.reveal(p,H.$(p,'analysis-profile'));await H.$(p,'analysis-profile').selectOption('knu-csc-2025-source@1.5.0');
 assert.equal(await H.$(p,'analysis-work-type').inputValue(),'');assert.equal(await H.$(p,'reference-root').inputValue(),'');
 assert(await H.$(p,'analyze-now').isDisabled());await H.$(p,'analysis-work-type').selectOption('unspecified');
 const option=H.$(p,'reference-root').locator('option').filter({hasText:thesis});assert.equal(await option.count(),1);await H.$(p,'reference-root').selectOption(await option.getAttribute('value'));
 try { await H.analyze(p); } catch(error) { record.failedAnalysis=await p.evaluate(()=>({status:document.querySelector('[data-testid=analysis-status]')?.textContent,root:document.querySelector('[data-testid=reference-root]')?.value,work:document.querySelector('[data-testid=analysis-work-type]')?.value,body:document.body.innerText.slice(0,5500)}));throw error; }}
async function phase(label){const name='Source workflow '+label+' '+Date.now();checkpoint(label+': import actual files');await importZip(name,buildZip(files));
 await page.waitForFunction(()=>document.querySelector('[data-testid=compiler-readiness]')?.textContent.includes('compilation available.'),null,{timeout:180000});
 await choose(main);await H.analyze(page);const before=await H.head(page,name);assert.deepEqual(file(before,main),a);assert.deepEqual(file(before,'notes.tex'),b);
 checkpoint(label+': search and exact selected multi-file replacement');await search('old');assert.equal(await H.$(page,'project-search-result').count(),3);
 const originalHead=(await H.head(page,name)).id;await H.$(page,'project-search-result').nth(0).click();
 assert.equal(await H.$(page,'editor').evaluate(e=>e.value.slice(e.selectionStart,e.selectionEnd)),'old');assert.equal((await H.head(page,name)).id,originalHead);
 await H.$(page,'project-replace-text').fill(replacement);await H.$(page,'project-replace-match').nth(0).check();await H.$(page,'project-replace-match').nth(2).check();
 await H.$(page,'project-replace-preview').click();await H.$(page,'review-preview').waitFor();await H.idle(page);assert.equal(await H.$(page,'review-file').count(),2);assert.deepEqual(await H.head(page,name),before);
 await H.$(page,'review-decline').click();await H.$(page,'review-preview').waitFor({state:'detached'});assert.deepEqual(await H.head(page,name),before);
 await H.$(page,'project-replace-preview').click();await H.$(page,'review-preview').waitFor();await H.idle(page);await H.accept(page);
 const after=await H.head(page,name);assert.notEqual(after.id,before.id);assert.deepEqual(file(after,main),Buffer.concat([BOM,Buffer.from(replacement+'🙂\r\nold\r\n')]));
 assert.deepEqual(file(after,'notes.tex'),Buffer.from('keep é\r\n'+replacement+'\r\n'));
 for(const f of before.files.filter(f=>![main,'notes.tex'].includes(f.path)))assert.deepEqual(file(after,f.path),Buffer.from(f.bytes));
 await H.undo(page);const undone=await H.head(page,name);for(const f of before.files)assert.deepEqual(file(undone,f.path),Buffer.from(f.bytes));
 checkpoint(label+': explicit-root cited source report');await analysisProfile();await H.$(page,'thesis-report').waitFor();assert.equal(await H.$(page,'thesis-rule').count(),5);
 assert.equal(await H.$(page,'thesis-catalog-row').count(),31);assert((await H.$(page,'thesis-selection').textContent()).includes(thesis));assert((await H.$(page,'thesis-scope').textContent()).includes('Incomplete standards coverage'));
 assert((await H.$(page,'thesis-known-finding').count())>=2);for(const id of ['KNU-SRC-APPENDIX-001','KNU-SRC-CAPTION-PRESENCE-001'])assert.match(await H.$(page,'thesis-rule').filter({has:page.locator('code',{hasText:id})}).innerText(),/literal|counterexample/);
 const evidence=H.$(page,'thesis-evidence');await H.reveal(page,evidence.first());const cite=evidence.filter({has:page.locator('pre',{hasText:'ключ😀'})}).filter({has:page.locator('p',{hasText:'cite —'})});assert.equal(await cite.count(),1);
 await choose(main);await cite.locator('[data-testid=thesis-source-link]').click();await page.waitForFunction(()=>{const e=document.querySelector('[data-testid=editor]');return e&&e.value.slice(e.selectionStart,e.selectionEnd)==='ключ😀';});
 const selection=await H.$(page,'editor').evaluate(e=>({value:e.value.slice(e.selectionStart,e.selectionEnd),start:e.selectionStart,end:e.selectionEnd}));assert.equal(selection.value,'ключ😀');
 assert.deepEqual(await H.head(page,name),undone,'Source navigation never saves');
 const empty=evidence.filter({has:page.locator('p',{hasText:'captionTitle —'})});await empty.locator('[data-testid=thesis-source-link]').click();assert.equal(await H.$(page,'editor').evaluate(e=>e.selectionStart===e.selectionEnd),true);
 checkpoint(label+': genuine read-only tab');const reader=await context.newPage();reader.setDefaultTimeout(30000);
 try{await reader.goto(origin+'/');await H.ready(reader);await H.open(reader,name);await choose(main,reader);await reader.waitForFunction(()=>{const e=document.querySelector('[data-testid=editor]');return e&&(e.readOnly||e.disabled);});
  await search('old',reader);assert(await H.$(reader,'project-replace-preview').isDisabled());assert.equal(await H.$(reader,'project-search-result').count(),3);await H.$(reader,'project-search-result').nth(0).click();
  assert.equal(await H.$(reader,'editor').evaluate(e=>e.value.slice(e.selectionStart,e.selectionEnd)),'old');await analysisProfile(reader);await H.analyze(reader);await H.analyze(reader);assert.equal(await H.$(reader,'thesis-rule').count(),5);assert.deepEqual(await H.head(reader,name),undone);
 }finally{await reader.close();}
 checkpoint(label+': invalidate source report');await H.$(page,'analysis-work-type').selectOption('masterQualification');await H.analyze(page);assert((await H.$(page,'thesis-selection').textContent()).includes('Master'));
 await choose(thesis);const current=await H.$(page,'editor').inputValue();await H.$(page,'editor').fill(current.replace('\\end{document}','\\unknownMacro\n\\end{document}'));await H.save(page);await H.analyze(page);
 assert.equal(await H.$(page,'thesis-rule').evaluateAll(es=>es.filter(e=>e.dataset.outcome==='Indeterminate').length),5);assert((await H.$(page,'thesis-known-finding').count())>=2);
 await H.$(page,'analysis-profile').selectOption('knu-csc-2025-source@1.4.0');await H.analyze(page);assert.equal(await H.$(page,'thesis-report').count(),0);
 await page.screenshot({path:path.join(out,label+'-source-workflows.png'),fullPage:true});return {label,project:name,selectedReplacementExactBytes:true,declineAcceptSaveUndo:true,unrelatedBytesPreserved:true,
  explicitRootWorkType:true,fiveSourceRules:true,catalogCells:31,unicodeAndEmptySourceNavigation:true,readonlySearchAndAnalysis:true,knownCounterexamplesRetained:true,legacyProfilePreserved:true};}
async function summary(name){return page.evaluate(async name=>{const m=await import('./js/workspace-storage.js'),p=(await m.listProjects()).find(p=>p.name===name),r=await m.getRevision(p.headRevisionId);return{id:r.id,files:r.files.map(f=>({path:f.relativePath,hash:f.contentHash,length:f.byteLength}))};},name);}
async function probeStart(){await page.evaluate(()=>{let previous=performance.now();const p=window.__searchProbe={ticks:0,frames:0,gaps:[],maxGap:0};p.timer=setInterval(()=>{const now=performance.now(),gap=now-previous;previous=now;p.ticks++;p.maxGap=Math.max(p.maxGap,gap);if(p.gaps.length<1024)p.gaps.push(gap);},16);function frame(){p.frames++;p.raf=requestAnimationFrame(frame);}p.raf=requestAnimationFrame(frame);});}
async function probeEnd(){return page.evaluate(()=>{const p=window.__searchProbe;clearInterval(p.timer);cancelAnimationFrame(p.raf);return{ticks:p.ticks,frames:p.frames,maxGap:p.maxGap,samples:p.gaps.length};});}
async function maximum(fileName,name,query,count){checkpoint(name+': maximum admitted import');await importZip(name,fs.readFileSync(path.join(process.env.MAX_FIXTURE_DIR,fileName)));
 await H.reveal(page,H.$(page,'project-search-query'));const before=await summary(name),stamp=await H.stamp(page);await probeStart();const start=Date.now();await search(query);const metrics=await probeEnd();
 assert.equal(await H.$(page,'project-search-result').count(),count);assert(metrics.ticks>1&&metrics.frames>1,'Actual browser input/paint heartbeat must advance');assert(metrics.maxGap<=250,'Search UI blocked for '+metrics.maxGap+'ms');assert.deepEqual(await summary(name),before);assert.deepEqual(await H.stamp(page),stamp);
 await H.$(page,'project-search-submit').click();await status('Searching');await H.$(page,'project-search-cancel').click();await status('Cancelled');await page.waitForFunction(()=>!document.querySelector('[data-testid=project-search-submit]')?.disabled);
 assert.equal(await H.$(page,'project-search-result').count(),0);assert.equal(await H.$(page,'project-search-status').getAttribute('data-search-status'),'Cancelled');
 return{elapsedMs:Date.now()-start,metrics,files:before.files.length,complete:true,cancel:true,unchangedHeadAndGeneration:true};}
(async()=>{try{await start();await open(false);record.online=await phase('online');
 if(process.env.MAX_FIXTURE_DIR){record.maximum=await maximum('maximum.zip','Synthetic maximum64MiB','a'.repeat(1023)+'b',0);record.files5000=await maximum('files5000.zip','Synthetic maximum5000files','final_unique_marker',1);}
 await page.evaluate(()=>navigator.serviceWorker.ready);await page.waitForFunction(()=>navigator.serviceWorker.controller!==null);await context.close();context=null;await stop();await assert.rejects(fetch(origin+'/'));await open(true);record.coldOffline=await phase('cold-offline');
 assert.deepEqual(record.pageErrors,[]);assert.deepEqual(record.externalRequests,[]);record.result='PASS';
 }catch(error){record.failure=String(error.stack||error).slice(0,5000);process.exitCode=1;if(page){record.failureState=await page.evaluate(()=>({body:document.body.innerText.slice(-7000),analysis:document.querySelector('[data-testid=analysis-status]')?.textContent,search:document.querySelector('[data-testid=project-search-status]')?.textContent})).catch(()=>null);await page.screenshot({path:path.join(out,'failure.png'),fullPage:true}).catch(()=>{});}}
 finally{if(context)await context.close().catch(()=>{});await stop();fs.rmSync(profile,{recursive:true,force:true});fs.writeFileSync(receiptPath,JSON.stringify(record,null,2)+'\n');process.stdout.write(JSON.stringify({result:record.result,step:record.step,failure:record.failure})+'\n');}})();
