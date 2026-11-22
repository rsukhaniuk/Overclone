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
const thesisVersion=process.env.THESIS_PROFILE_VERSION||'1.5.0';
assert(['1.5.0','1.6.0'].includes(thesisVersion),'THESIS_PROFILE_VERSION must be 1.5.0 or 1.6.0');
const citationNotes=thesisVersion==='1.6.0';
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
 thesisProfileVersion:thesisVersion,
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
    context = await chromium.launchPersistentContext(profile,{headless:true,offline,viewport:{width:1850,height:1080},
        ...(citationNotes?{permissions:['clipboard-read','clipboard-write']}:{})});
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
const chapterText='\\section{Огляд🙂}\n'+(citationNotes?'\\cite[с. 2]{ключ😀}':'\\cite{ключ😀}')+'\n\\begin{figure}\\caption{}\\end{figure}\n';
const files=[[main,a],['notes.tex',b],['unrelated.txt',Buffer.from('unchanged\r\n')],['asset.bin',Buffer.from([255,0,128,9])],
 [thesis,Buffer.concat([BOM,Buffer.from(rootText)])],['chapter.tex',Buffer.from(chapterText)],
 ['refs.bib',Buffer.from('@book{ключ😀,title={Синтетичне джерело}}\n')]];
const normalize=s=>s.replace(/\r\n/g,'\n').replace(/\r/g,'\n');
async function closeProject(){if(await page.getByRole('button',{name:'Close',exact:true}).count()){await page.getByRole('button',{name:'Close',exact:true}).click();await page.locator('.project-dashboard').waitFor();}}
async function importZip(name,buffer){await closeProject();await H.$(page,'import-zip').setInputFiles({name:'synthetic.zip',mimeType:'application/zip',buffer});
 await H.reveal(page,H.$(page,'import-preview'));await H.$(page,'import-preview').waitFor({timeout:180000});await H.$(page,'staged-name').fill(name);await H.$(page,'publish-import').click();await H.$(page,'select-file').first().waitFor({timeout:180000});}
async function choose(name,p=page){await H.$(p,'select-file').filter({has:p.locator('.file-name',{hasText:name})}).click();}
async function status(want,p=page){await p.waitForFunction(want=>document.querySelector('[data-testid=project-search-status]')?.dataset.searchStatus===want,want,{timeout:180000});}
async function search(query,p=page){await H.reveal(p,H.$(p,'project-search-query'));await H.$(p,'project-search-query').fill(query);await H.$(p,'project-search-submit').click();await status('Complete',p);await H.$(p,'project-search-submit').waitFor({state:'visible'});}
const file=(head,path)=>{const v=head.files.find(f=>f.path===path);assert(v,path);return Buffer.from(v.bytes);};
async function analysisProfile(p=page,version=thesisVersion){const root=H.$(p,'reference-root'),work=H.$(p,'analysis-work-type');
 const keepScope=await H.$(p,'analysis-profile').inputValue()==='fcsc-knu'&&await root.count()>0;
 const previousRoot=keepScope?await root.inputValue():'',previousWork=keepScope?await work.inputValue():'';
 await H.selectAnalysisProfile(p,'knu-csc-2025-source@'+version);
 assert.equal(await work.inputValue(),previousWork);assert.equal(await root.inputValue(),previousRoot);
 if(!previousRoot||!previousWork)assert(await H.$(p,'analyze-now').isDisabled());await work.selectOption('unspecified');
 const option=root.locator('option').filter({hasText:thesis});assert.equal(await option.count(),1);await root.selectOption(await option.getAttribute('value'));
 try { await H.analyze(p);if(citationNotes)await reportEdition(p,version); } catch(error) { record.failedAnalysis=await p.evaluate(()=>({status:document.querySelector('[data-testid=analysis-status]')?.textContent,root:document.querySelector('[data-testid=reference-root]')?.value,work:document.querySelector('[data-testid=analysis-work-type]')?.value,body:document.body.innerText.slice(0,5500)}));throw error; }}
async function reportEdition(p,version,previous=null){await p.waitForFunction(({version,previous})=>{
 const report=document.querySelector('[data-testid=thesis-report]'),edition=document.querySelector('[data-testid=knu-edition]');
 return report?.dataset.snapshotId&&report.dataset.snapshotId!==previous
  &&(edition?.dataset.profileVersion===version||edition?.textContent.endsWith('source profile '+version))
  &&document.querySelector('[data-testid=analysis-status]')?.textContent==='Analysis complete.';
 },{version,previous});}
const thesisRule=(p,id)=>H.$(p,'thesis-rule').filter({has:p.locator('code',{hasText:id})});
const citationEvidence=p=>H.$(p,'thesis-evidence').filter({has:p.locator('p',{hasText:'cite —'})});
async function editionMetadata(p,version){
 const metadataBytes=fs.readFileSync(path.join(__dirname,'../src/Client/Analysis/knu-profile-v'+version.slice(0,3).replace('.','_')+'-metadata.json'));
 const catalogBytes=fs.readFileSync(path.join(__dirname,'../src/Client/Analysis/thesis-support-catalog'+(version==='1.6.0'?'-v1_6':'')+'.json'));
 const metadata=JSON.parse(metadataBytes),catalog=JSON.parse(catalogBytes);
 assert.deepEqual(metadata.profile,{id:'knu-csc-2025-source',version});assert.equal(catalog.profileVersion,version);
 assert.equal(catalog.catalogVersion,version==='1.6.0'?'1.1.0':'1.0.0');assert.equal(metadata.guidelineSha256,catalog.guidelineSha256);
 assert.equal(await H.selectedAnalysisProfile(p),'knu-csc-2025-source@'+version);
 assert.match(await H.$(p,'thesis-report').locator('h3').textContent(),/^Cited thesis source observations/);
 assert.equal(await H.knuReportVersion(p),version);
 const row=catalog.rules.find(r=>r.ruleId==='KNU-SRC-CITATIONS-001');
 const rendered=H.$(p,'thesis-catalog-row').filter({has:p.locator('summary',{hasText:row.ruleId})});assert.equal(await rendered.count(),1);
 const text=await rendered.textContent();assert(text.includes(row.supportedSyntax));assert(text.includes('Fix policy: none'));
 assert.equal(await thesisRule(p,row.ruleId).locator('[data-testid=thesis-citation]').textContent(),
  'Guideline §'+row.citation.section+'; printed pages '+row.citation.printedPages.join(', ')+'; PDF pages '+row.citation.pdfPages.join(', ')+'.');
 assert.equal(await H.$(p,'thesis-report').locator('[data-testid=preview-fix]').count(),0);
 return{profileVersion:version,catalogSourceVersion:catalog.catalogVersion,metadataSha256:sha(metadataBytes),catalogSha256:sha(catalogBytes)};
}
async function retiredControl(button,retiredSnapshot=null){const observed=await button.evaluate((e,old)=>({
 connected:e.isConnected,disabled:e.disabled,snapshot:e.closest('[data-testid=thesis-report]')?.dataset.snapshotId,
 rebound:old!==null&&e.closest('[data-testid=thesis-report]')?.dataset.snapshotId!==old,
 }),retiredSnapshot);assert(!observed.connected||observed.disabled||observed.rebound,
 'Retired evidence must be unavailable or rebound to a fresh report snapshot');await button.dispose();return observed;}
async function rootRoundTrip(name,head){
 const before=await H.$(page,'thesis-report').getAttribute('data-snapshot-id'),old=await citationEvidence(page).locator('[data-testid=thesis-source-link]').elementHandle();
 const selected=await H.$(page,'reference-root').inputValue();
 const other=H.$(page,'reference-root').locator('option').filter({hasText:main});assert.equal(await other.count(),1);
 await H.$(page,'reference-root').selectOption(await other.getAttribute('value'));await H.analyze(page);await reportEdition(page,thesisVersion,before);await retiredControl(old);
 assert((await H.$(page,'thesis-selection').textContent()).includes('Root: '+main+'.'));
 assert.equal(await citationEvidence(page).count(),0);const otherSnapshot=await H.$(page,'thesis-report').getAttribute('data-snapshot-id');
 await H.$(page,'reference-root').selectOption(selected);await H.analyze(page);await reportEdition(page,thesisVersion,otherSnapshot);
 assert.equal(await thesisRule(page,'KNU-SRC-CITATIONS-001').getAttribute('data-outcome'),'Pass');assert.deepEqual(await H.head(page,name),head);
 return{oldSnapshot:before,restoredSnapshot:await H.$(page,'thesis-report').getAttribute('data-snapshot-id'),retiredControlUnavailable:true};
}
async function pasteSource(text){
 const editor=H.$(page,'editor'),before=await H.stamp(page),id=await editor.getAttribute('data-file-id');
 await page.bringToFront();await editor.focus();await editor.press('Control+A');
 await page.evaluate(async value=>{await navigator.clipboard.writeText(value);const e=document.querySelector('[data-testid=editor]');
  const events=window.__citationPaste={input:0,beforeinput:0,trusted:true,types:[]};
  window.__citationPasteDetach=[];for(const name of ['input','beforeinput']){const observe=event=>{events[name]++;events.trusted&&=event.isTrusted;events.types.push(event.inputType);};
   e.addEventListener(name,observe);window.__citationPasteDetach.push(()=>e.removeEventListener(name,observe));}
 },text);await editor.press('Control+V');
 await page.waitForFunction(({text,id,before})=>{const e=document.querySelector('[data-testid=editor]'),a=document.querySelector('[data-testid=analysis-panel]');
  return e?.dataset.fileId===id&&e.value===text&&a?.dataset.session===before.session&&Number(a?.dataset.generation)===Number(before.generation)+1;
 },{text,id,before});
 const observed=await page.evaluate(()=>{for(const detach of window.__citationPasteDetach)detach();return window.__citationPaste;});
 assert.equal(observed.input,1);assert.equal(observed.beforeinput,1);assert(observed.trusted);assert(observed.types.every(t=>t==='insertFromPaste'));return observed;
}
async function phase(label){const name='Source workflow '+label+' '+Date.now();checkpoint(label+': import actual files');await importZip(name,buildZip(files));
 checkpoint(label+': prepare compiler tools');
 if(!(await H.$(page,'compiler-readiness').textContent()).includes('compilation available.')){
  const refresh=H.$(page,'refresh-compile-tools');await H.reveal(page,refresh);await refresh.click();
  await H.$(page,'compiler-settings').locator(':scope > summary').click();
 }
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
 let notesCheck=null;
 checkpoint(label+': explicit-root cited source report');
 if(citationNotes){await analysisProfile(page,'1.5.0');const legacy=await editionMetadata(page,'1.5.0');
  assert.equal(await thesisRule(page,'KNU-SRC-CITATIONS-001').getAttribute('data-outcome'),'Indeterminate');
  assert((await H.$(page,'thesis-limitations').textContent()).includes('dynamicCitationOrBibliography'));assert.equal(await citationEvidence(page).count(),0);
  assert.deepEqual(await H.head(page,name),undone);notesCheck={legacy,legacyNoteIndeterminate:true};}
 await analysisProfile();await H.$(page,'thesis-report').waitFor();assert.equal(await H.$(page,'thesis-rule').count(),5);
 assert.equal(await H.$(page,'thesis-catalog-row').count(),31);assert((await H.$(page,'thesis-selection').textContent()).includes(thesis));assert((await H.$(page,'thesis-scope').textContent()).includes('Incomplete standards coverage'));
 if(citationNotes){notesCheck.current=await editionMetadata(page,thesisVersion);assert.equal(await thesisRule(page,'KNU-SRC-CITATIONS-001').getAttribute('data-outcome'),'Pass');
  assert((await H.$(page,'thesis-literal-coverage').textContent()).includes('complete within the finite grammar'));
  assert.equal((await H.$(page,'thesis-limitations').textContent()).trim(),'');notesCheck.rootRoundTrip=await rootRoundTrip(name,undone);}
 assert((await H.$(page,'thesis-known-finding').count())>=2);for(const id of ['KNU-SRC-APPENDIX-001','KNU-SRC-CAPTION-PRESENCE-001'])assert.match(await H.$(page,'thesis-rule').filter({has:page.locator('code',{hasText:id})}).innerText(),/literal|counterexample/);
 const evidence=H.$(page,'thesis-evidence');await H.reveal(page,evidence.first());const cite=evidence.filter({has:page.locator('pre',{hasText:'ключ😀'})}).filter({has:page.locator('p',{hasText:'cite —'})});assert.equal(await cite.count(),1);
 await choose(main);await cite.locator('[data-testid=thesis-source-link]').click();await page.waitForFunction(()=>{const e=document.querySelector('[data-testid=editor]');return e&&e.value.slice(e.selectionStart,e.selectionEnd)==='ключ😀';});
 const selection=await H.$(page,'editor').evaluate(e=>({value:e.value.slice(e.selectionStart,e.selectionEnd),start:e.selectionStart,end:e.selectionEnd}));assert.equal(selection.value,'ключ😀');
 if(citationNotes){const start=chapterText.indexOf('ключ😀'),bytesStart=Buffer.byteLength(chapterText.slice(0,start)),bytesEnd=bytesStart+Buffer.byteLength('ключ😀');
  assert.equal(selection.start,start);assert.equal(selection.end,start+'ключ😀'.length);
  const span=(await cite.locator('p').textContent()).match(/UTF-8 bytes \[(\d+), (\d+)\)/);assert(span);assert.deepEqual(span.slice(1).map(Number),[bytesStart,bytesEnd]);
  notesCheck.exactKey={text:selection.value,utf16:[selection.start,selection.end],utf8:[bytesStart,bytesEnd]};}
 assert.deepEqual(await H.head(page,name),undone,'Source navigation never saves');
 const empty=evidence.filter({has:page.locator('p',{hasText:'captionTitle —'})});await empty.locator('[data-testid=thesis-source-link]').click();assert.equal(await H.$(page,'editor').evaluate(e=>e.selectionStart===e.selectionEnd),true);
 checkpoint(label+': genuine read-only tab');const reader=await context.newPage();reader.setDefaultTimeout(30000);
 try{await reader.goto(origin+'/');await H.ready(reader);await H.open(reader,name);await choose(main,reader);await reader.waitForFunction(()=>{const e=document.querySelector('[data-testid=editor]');return e&&(e.readOnly||e.disabled);});
  await search('old',reader);assert(await H.$(reader,'project-replace-preview').isDisabled());assert.equal(await H.$(reader,'project-search-result').count(),3);await H.$(reader,'project-search-result').nth(0).click();
  assert.equal(await H.$(reader,'editor').evaluate(e=>e.value.slice(e.selectionStart,e.selectionEnd)),'old');await analysisProfile(reader);await H.analyze(reader);await H.analyze(reader);assert.equal(await H.$(reader,'thesis-rule').count(),5);assert.deepEqual(await H.head(reader,name),undone);
  if(citationNotes){await editionMetadata(reader,thesisVersion);assert.equal(await thesisRule(reader,'KNU-SRC-CITATIONS-001').getAttribute('data-outcome'),'Pass');notesCheck.readonlyNoteObserved=true;}
 }finally{await reader.close();}
 checkpoint(label+': invalidate source report');const workSnapshot=citationNotes?await H.$(page,'thesis-report').getAttribute('data-snapshot-id'):null;
 const workControl=citationNotes?await citationEvidence(page).locator('[data-testid=thesis-source-link]').elementHandle():null;
 await H.$(page,'analysis-work-type').selectOption('masterQualification');await H.analyze(page);
 if(citationNotes){await reportEdition(page,thesisVersion,workSnapshot);notesCheck.workTypeControl=await retiredControl(workControl,workSnapshot);assert.equal(await H.selectedAnalysisProfile(page),'knu-csc-2025-source@1.6.0');
  assert.equal(await thesisRule(page,'KNU-SRC-CITATIONS-001').getAttribute('data-outcome'),'Pass');}
 assert((await H.$(page,'thesis-selection').textContent()).includes('Master'));
 await choose(thesis);const current=await H.$(page,'editor').inputValue(),edited=current.replace('\\end{document}','\\unknownMacro\n\\end{document}');
 const sourceSnapshot=citationNotes?await H.$(page,'thesis-report').getAttribute('data-snapshot-id'):null;
 if(citationNotes){const old=await citationEvidence(page).locator('[data-testid=thesis-source-link]').elementHandle();notesCheck.nativeSourceEdit=await pasteSource(edited);
  await retiredControl(old);assert.deepEqual(await H.head(page,name),undone);notesCheck.sourceRetiredControlUnavailable=true;
 }else await H.$(page,'editor').fill(edited);
 await H.save(page);await H.analyze(page);if(citationNotes)await reportEdition(page,thesisVersion,sourceSnapshot);
 assert.equal(await H.$(page,'thesis-rule').evaluateAll(es=>es.filter(e=>e.dataset.outcome==='Indeterminate').length),5);assert((await H.$(page,'thesis-known-finding').count())>=2);
 await H.selectAnalysisProfile(page,'knu-csc-2025-source@1.4.0');await H.analyze(page);assert.equal(await H.$(page,'thesis-report').count(),0);
 await page.screenshot({path:path.join(out,label+'-source-workflows.png'),fullPage:true});return {label,project:name,selectedReplacementExactBytes:true,declineAcceptSaveUndo:true,unrelatedBytesPreserved:true,
  explicitRootWorkType:true,fiveSourceRules:true,catalogCells:31,unicodeAndEmptySourceNavigation:true,readonlySearchAndAnalysis:true,knownCounterexamplesRetained:true,legacyProfilePreserved:true,
  ...(citationNotes?{literalCitationNotes:notesCheck}:{})};}
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
