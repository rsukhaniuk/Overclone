// Real registered history comparison/review/restore with full host-stopped offline restart.
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
const receiptPath = path.join(out, 'history-comparison-receipt.json');
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

const BOM=Buffer.from([239,187,191]),mainName='thesis Київ🙂.tex',notesName='notes.tex';
const firstText=['\\documentclass{article}','\\begin{document}','\\section{First}','Київ🙂 е́ <b>source</b>.','\\end{document}',''].join('\r\n');
const secondText=firstText.replace(/\r\n/g,'\n').replace('{First}','{Second}');
const thirdText=secondText.replace('{Second}','{Unsaved third}');
const firstMain=Buffer.concat([BOM,Buffer.from(firstText)]),notesFirst=Buffer.from('Original notes Київ🙂.\n'),notesSecond=Buffer.from('Later notes Київ🙂.\n');
const fixtures=[[mainName,firstMain],[notesName,notesFirst],['asset.bin',Buffer.from([0,255,1,128])]];
const record={schemaVersion:1,result:'FAIL',scope:'registered-history-comparison-selected-restore-online-cold-offline',sdkVersion:manifest.version,
    online:null,coldOffline:null,step:null,pageErrors:[],externalRequests:[],workers:[],failure:null};
const profile=fs.mkdtempSync(path.join(os.tmpdir(),'overclone-history-comparison-'));
const sockets=new Set();let server,context,page;
const origin='http://127.0.0.1:8787',name='History comparison synthetic '+Date.now();
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
    await new Promise(resolve => server.listen(8787,'127.0.0.1',resolve));
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
function canonical(value){if(typeof value==='string')return value.replace(/-/g,'').toLowerCase();if(value&&typeof value==='object')return canonical(value.value??value.Value);throw Error('Unexpected revision identity');}
async function saveCurrent(){if(await H.$(page,'save').isEnabled())await H.save(page);else await page.waitForFunction(()=>document.querySelector('.workspace-header .status')?.textContent==='Saved.');}
async function selectFile(name){await H.$(page,'select-file').filter({hasText:name}).click();}
async function importFile(name,buffer){await H.$(page,'import-input').setInputFiles({name,mimeType:'application/octet-stream',buffer});await H.$(page,'select-file').filter({hasText:name}).waitFor();}
async function head(p=page){return H.head(p,name);}
function file(snapshot,path){const f=snapshot.files.find(f=>f.path===path);assert.ok(f,'Missing '+path);return f;}
function unchanged(before,after,except){for(const f of before.files.filter(f=>f.path!==except)){
    const g=after.files.find(g=>g.id===f.id&&g.path===f.path);assert.ok(g,'Unrelated identity '+f.path);assert.deepEqual(g.bytes,f.bytes,'Unrelated bytes '+f.path);}}
async function revisionChoice(p,id,value){const selector=H.$(p,id),options=await selector.locator('option').evaluateAll(es=>es.map(e=>e.value));
    const matches=options.filter(x=>x&&canonical(x)===canonical(value));assert.equal(matches.length,1,id+' must contain exact revision');await selector.selectOption(matches[0]);}
async function compared(p=page){await p.waitForFunction(()=>{
    const d=document.querySelector('[data-testid=history-direction]'),b=document.querySelector('[data-testid=history-compare]');
    return d&&b&&!b.disabled&&!document.querySelector('[data-testid=history-comparison-stale]');});}
async function phase(label){
    checkpoint(label+': automatic tools preparation');
    await page.waitForFunction(()=>document.querySelector('[data-testid=compiler-readiness]')?.textContent.includes('compilation available.'),null,{timeout:180000});
    checkpoint(label+': create saved revisions');await selectFile(mainName);await saveCurrent();await H.analyze(page);const a=await head();
    assert.deepEqual(Buffer.from(file(a,mainName).bytes),firstMain);
    await H.$(page,'editor').fill(secondText);await selectFile(notesName);await H.$(page,'editor').fill(notesSecond.toString('utf8'));
    await importFile('later.tex',Buffer.from('Added later Київ🙂.\n'));await selectFile(mainName);await saveCurrent();const b=await head();assert.notEqual(b.id,a.id);
    const panel=H.$(page,'history-comparison');await H.reveal(page,panel);
    const working=panel.getByRole('checkbox',{name:'Right: current working bytes (including unsaved changes)',exact:true});
    await working.uncheck();await revisionChoice(page,'history-left',a.id);await revisionChoice(page,'history-right',b.id);
    await H.$(page,'history-compare').click();await compared();assert.equal((await head()).id,b.id);
    const direction=await H.$(page,'history-direction').textContent();assert.ok(canonical(direction).includes(canonical(a.id)));assert.ok(canonical(direction).includes(canonical(b.id)));
    await H.$(page,'history-path-filter').fill(mainName);const rows=panel.locator('[data-testid=history-comparison-files] > li');assert.equal(await rows.count(),1);
    await rows.locator('[data-testid=history-show-diff]').click();const diff=await H.$(page,'history-detailed-diff').textContent();assert.ok(diff.includes('First')&&diff.includes('Second'));
    await H.$(page,'history-path-filter').fill('');await H.$(page,'history-status-filter').selectOption('ContentChanged');assert.equal(await rows.count(),2);
    await H.$(page,'history-status-filter').selectOption('');await page.screenshot({path:path.join(out,label+'-comparison.png'),fullPage:true});
    checkpoint(label+': dirty comparison without save');await working.check();await selectFile(mainName);
    const dirtyStart=Date.now();await H.$(page,'editor').fill(thirdText);await H.$(page,'history-compare').click();await compared();
    assert.ok((await H.$(page,'history-direction').textContent()).includes('working copy'));
    await panel.locator('[data-testid=history-comparison-files] > li').filter({hasText:mainName}).locator('[data-testid=history-show-diff]').click();
    assert.ok((await H.$(page,'history-detailed-diff').textContent()).includes('Unsaved third'));
    assert.equal(await H.$(page,'editor').inputValue(),thirdText);assert.equal(await H.$(page,'save').isEnabled(),true);
    assert.equal((await head()).id,b.id,'Comparison cannot force-save dirty work');const dirtyElapsedMs=Date.now()-dirtyStart;
    await saveCurrent();const c=await head();assert.notEqual(c.id,b.id);await H.analyze(page);
    await H.$(page,'history-compare').click();await compared();
    const mainRow=panel.locator('[data-testid=history-comparison-files] > li').filter({hasText:mainName});await mainRow.getByRole('checkbox').check();
    checkpoint(label+': explicit selected text review');await H.$(page,'history-text-restore-preview').click();await H.$(page,'review-preview').waitFor();await H.idle(page);
    assert.equal(await H.$(page,'review-file').count(),1);assert.equal((await H.$(page,'review-path').textContent()).trim(),mainName);
    assert.equal((await head()).id,c.id);assert.equal(await H.$(page,'editor').inputValue(),thirdText);
    await H.$(page,'review-decline').click();await H.$(page,'review-preview').waitFor({state:'detached'});assert.equal((await head()).id,c.id);
    await H.$(page,'history-text-restore-preview').click();await H.$(page,'review-preview').waitFor();await H.idle(page);
    await H.$(page,'review-accept').click();await page.waitForFunction(()=>document.querySelector('[data-testid=review-panel]')?.dataset.status==='AppliedSaved');
    const d=await head();assert.notEqual(d.id,c.id);assert.deepEqual(Buffer.from(file(d,mainName).bytes),firstMain);unchanged(c,d,mainName);
    await H.$(page,'review-undo').click();await page.waitForFunction(()=>document.querySelector('[data-testid=review-panel]')?.dataset.status==='UndoSaved');
    const e=await head();assert.deepEqual(Buffer.from(file(e,mainName).bytes),Buffer.concat([BOM,Buffer.from(thirdText)]));unchanged(c,e,mainName);
    checkpoint(label+': explicit whole-project restore');await H.$(page,'history-compare').click();await compared();
    await H.$(page,'history-whole-restore-preview').click();await H.reveal(page,H.$(page,'restore'));await H.$(page,'restore').click();
    await page.waitForFunction(expected=>document.querySelector('.workspace-header .status')?.textContent==='Saved.'
        && document.querySelector('[data-testid=editor]')?.value===expected
        && !document.querySelector('[data-testid=restore]'),firstText.replace(/\r\n/g,'\n'));
    const f=await head();assert.notEqual(f.id,e.id);assert.equal(canonical(f.parent),canonical(e.id),'Restore creates a child, never rewinds');
    assert.deepEqual(f.files.map(f=>f.path).sort(),a.files.map(f=>f.path).sort());
    for(const old of a.files)assert.deepEqual(file(f,old.path).bytes,old.bytes,'Whole restore '+old.path);
    checkpoint(label+': readonly saved comparison');const reader=await context.newPage();reader.setDefaultTimeout(30000);reader.on('pageerror',error=>record.pageErrors.push(String(error).slice(0,512)));
    try{
        await reader.goto(origin+'/');await H.ready(reader);await H.open(reader,name);await H.$(reader,'select-file').filter({hasText:mainName}).click();
        await reader.waitForFunction(()=>{const e=document.querySelector('[data-testid=editor]');return e&&(e.disabled||e.readOnly);});
        const readPanel=H.$(reader,'history-comparison');await H.reveal(reader,readPanel);
        await readPanel.getByRole('checkbox',{name:'Right: current working bytes (including unsaved changes)',exact:true}).uncheck();
        await revisionChoice(reader,'history-left',a.id);await revisionChoice(reader,'history-right',e.id);await H.$(reader,'history-compare').click();await compared(reader);
        assert.equal(await H.$(reader,'history-text-restore-preview').isDisabled(),true);assert.equal(await H.$(reader,'history-whole-restore-preview').isDisabled(),true);
        assert.equal((await head(reader)).id,f.id,'Readonly comparison cannot save');
    }finally{await reader.close();}
    return {label,savedPairDirection:true,dirtyComparisonWithoutSave:true,dirtyElapsedMs,filtersAndExplicitDiff:true,
        selectedTextPreviewDeclineAcceptUndo:true,exactBomCrlf:true,unrelatedFilesPreserved:true,wholeRestoreChild:true,readonlySavedComparison:true,
        finalHead:f.id,sourceSha256:sha(Buffer.from(file(f,mainName).bytes))};
}
(async()=>{
    try{
        await start();await open(false);await H.reveal(page,page.locator('#new-project'));await page.locator('#new-project').fill(name);
        await page.getByRole('button',{name:'Create project',exact:true}).click();for(const [n,b]of fixtures)await importFile(n,b);
        await selectFile(mainName);await saveCurrent();record.online=await phase('online');
        await page.evaluate(()=>navigator.serviceWorker.ready);await page.waitForFunction(()=>navigator.serviceWorker.controller!==null);
        await context.close();context=null;await stop();await assert.rejects(fetch(origin+'/'),'Origin must physically refuse connections');
        await open(true);await H.open(page,name);await selectFile(mainName);record.coldOffline=await phase('cold-offline');
        assert.deepEqual(record.pageErrors,[]);assert.deepEqual(record.externalRequests,[]);record.result='PASS';
    }catch(error){record.failure=String(error.stack||error).slice(0,4096);process.exitCode=1;
        if(page)record.failureState=await page.evaluate(()=>({notice:document.querySelector('[data-testid=history-comparison-notice]')?.textContent,
            stale:document.querySelector('[data-testid=history-comparison-stale]')?.textContent,review:document.querySelector('[data-testid=review-panel]')?.dataset.status,
            reviewNotice:document.querySelector('[data-testid=review-notice]')?.textContent,save:document.querySelector('.workspace-header .status')?.textContent,
            source:document.querySelector('[data-testid=editor]')?.value})).catch(()=>null);
    }finally{if(context)await context.close().catch(()=>{});await stop();fs.rmSync(profile,{recursive:true,force:true});
        fs.writeFileSync(receiptPath,JSON.stringify(record,null,2)+'\n');process.stdout.write(JSON.stringify({result:record.result,step:record.step,failure:record.failure})+'\n');}
})();
