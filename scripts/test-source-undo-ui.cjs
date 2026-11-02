// Real app-owned source Undo/Redo, native Chromium IME and full host-stopped offline restart.
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
const receiptPath = path.join(out, 'source-undo-receipt.json');
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

const fixtures=[['main.tex',Buffer.concat([Buffer.from([239,187,191]),Buffer.from([
    '\\documentclass{article}','\\begin{document}','Київ🙂 е́ source journal.',
    'Typing: ','IME: ','\\sec','\\end{document}',''].join('\r\n'))])],
    ['notes.tex',Buffer.from('Unrelated source Київ🙂.\n')],
    ['asset.bin',Buffer.from([0,255,1,128])]];
const record={schemaVersion:1,result:'FAIL',scope:'source-undo-native-input-ime-online-cold-offline',
    sdkVersion:manifest.version,online:null,coldOffline:null,pageErrors:[],externalRequests:[],workers:[],failure:null};
const profile=fs.mkdtempSync(path.join(os.tmpdir(),'overclone-source-undo-'));
const sockets=new Set();let server,context,page;
const origin='http://127.0.0.1:8784',name='Source undo synthetic '+Date.now();
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
    await new Promise(resolve => server.listen(8784,'127.0.0.1',resolve));
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
async function saveCurrent() {
    if(await H.$(page,'save').isEnabled())await H.save(page);
    else await page.waitForFunction(()=>document.querySelector('.workspace-header .status')?.textContent==='Saved.');
}
async function selectedFile(filename){await H.$(page,'select-file').filter({hasText:filename}).click();}
async function value(){return H.$(page,'editor').inputValue();}
async function expectValue(expected){await page.waitForFunction(text=>document.querySelector('[data-testid=editor]')?.value===text,expected);}
async function caret(token){
    await H.reveal(page,H.$(page,'editor'));
    await H.$(page,'editor').evaluate((e,t)=>{const i=e.value.lastIndexOf(t);if(i<0)throw Error('Missing target');
        e.focus();e.setSelectionRange(i+t.length,i+t.length);},token);
    await H.$(page,'editor').evaluate(e=>({start:e.selectionStart,end:e.selectionEnd}));
}
async function command(redo=false,keyboard=false){
    const button=H.$(page,redo?'source-redo':'source-undo');
    await page.waitForFunction(id=>{const b=document.querySelector('[data-testid="'+id+'"]');return b&&!b.disabled;},redo?'source-redo':'source-undo');
    if(keyboard){await H.$(page,'editor').focus();await page.keyboard.press(redo?'Control+Shift+z':'Control+z');}
    else {await H.reveal(page,button);await button.click();}
}
async function capture(){return H.head(page,name);}
async function unchangedOthers(before,after){
    for(const f of before.files.filter(f=>f.path!=='main.tex'))
        assert.deepEqual(after.files.find(g=>g.id===f.id&&g.path===f.path)?.bytes,f.bytes,'unrelated '+f.path);
}
async function exercise(label){
    await selectedFile('main.tex');
    // The first native edit normalizes imported CRLF through the ordinary input
    // path. That whole-buffer change is a separate unit from pure insertions.
    // Establish an unchanged LF baseline before testing adjacent abc grouping.
    await caret('Typing: ');await page.keyboard.type('x');await page.keyboard.press('Backspace');await saveCurrent();
    checkpoint(label+': typing');
    await selectedFile('main.tex');const before=await capture(),initial=await value();
    await H.$(page,'editor').evaluate(e=>{e.__typingTimes=[];e.addEventListener('input',()=>{
        if(e.__typingTimes.length<12)e.__typingTimes.push(performance.now());});});
    await caret('Typing: ');await page.keyboard.type('abc',{delay:25});
    const typed=initial.replace('Typing: ','Typing: abc');await expectValue(typed);
    record.typingTimes=await H.$(page,'editor').evaluate(e=>e.__typingTimes);
    await saveCurrent();const typedHead=await capture();assert.notEqual(typedHead.id,before.id);
    await command(false,true);await expectValue(initial); // One contiguous typing unit survives a save/new head.
    await command(true);await expectValue(typed);
    checkpoint(label+': per-file history');
    await selectedFile('notes.tex');const notes=await value();await caret('Unrelated source Київ🙂.');
    await page.keyboard.type('X');await expectValue(notes.replace('Київ🙂.','Київ🙂.X'));
    await selectedFile('main.tex');await command(false);await expectValue(initial);
    await selectedFile('notes.tex');await command(false);await expectValue(notes);
    await selectedFile('main.tex');await command(true,true);await expectValue(typed);
    // Source shortcuts must not capture unrelated form controls.
    await H.reveal(page,H.$(page,'source-query'));await H.$(page,'source-query').fill('local query');
    await H.$(page,'source-query').press('Control+z');assert.equal(await value(),typed);
    checkpoint(label+': native composition');
    // Real Chromium IME events through CDP, not synthetic app/provider results.
    await caret('IME: ');
    const client=await context.newCDPSession(page);
    const observed=[];
    await page.exposeFunction('observeSourceUndoIme',(type,composing)=>{if(observed.length<32)observed.push({type,composing});});
    await H.$(page,'editor').evaluate(e=>{
        const callback=event=>window.observeSourceUndoIme(event.type,event.isComposing===true);
        for(const type of ['compositionstart','compositionupdate','compositionend','beforeinput','input'])e.addEventListener(type,callback);
        e.__sourceUndoImeObserver=callback;
    });
    await client.send('Input.imeSetComposition',{text:'ї',selectionStart:1,selectionEnd:1});
    await client.send('Input.imeSetComposition',{text:'їж',selectionStart:2,selectionEnd:2});
    assert.equal(await H.$(page,'source-undo').isDisabled(),true,'history unavailable during composition');
    await client.send('Input.insertText',{text:'їжак'});await client.detach();
    const composed=typed.replace('IME: ','IME: їжак');await expectValue(composed);
    await command(false);await expectValue(typed);await command(true);await expectValue(composed);
    checkpoint(label+': explicit completion');
    assert.ok(observed.some(e=>e.type==='compositionstart'));assert.ok(observed.some(e=>e.type==='compositionend'));
    assert.ok(observed.some(e=>e.type==='input'&&e.composing));
    await H.$(page,'editor').evaluate(e=>{const f=e.__sourceUndoImeObserver;if(f)for(const t of ['compositionstart','compositionupdate','compositionend','beforeinput','input'])e.removeEventListener(t,f);delete e.__sourceUndoImeObserver;});
    // Existing explicit completion remains byte-preserving; journal Undo uses ordinary encoding.
    await H.reveal(page,H.$(page,'source-completion-show'));await caret('\\sec');await H.$(page,'source-completion-show').click();
    await page.waitForFunction(()=>document.querySelector('[data-testid=source-completion-status]')?.dataset.state!=='reading');
    assert.equal(await H.$(page,'source-completion-status').getAttribute('data-state'),'ready');
    await page.locator('[data-testid=source-completion-insert][data-command-id=section]').click();
    const completed=composed.replace('\\sec\n','\\section{}\n');await expectValue(completed);
    await command(false,true);await expectValue(composed);await command(true);await expectValue(completed);
    checkpoint(label+': line tools and replacement');
    await caret('Typing: abc');await H.$(page,'source-indent').click();
    const indented=completed.replace('Typing: abc','  Typing: abc');await expectValue(indented);
    await command(false);await expectValue(completed);await command(true);await expectValue(indented);
    await command(false);await expectValue(completed);
    await H.$(page,'source-query').fill('source journal.');await H.$(page,'source-replacement').fill('source history.');
    await H.$(page,'source-all').click();const replaced=completed.replace('source journal.','source history.');await expectValue(replaced);
    await command(false);await expectValue(completed);await command(true);await expectValue(replaced);
    await command(false);await expectValue(completed);
    await saveCurrent();const after=await capture();await unchangedOthers(before,after);
    const main=Buffer.from(after.files.find(f=>f.path==='main.tex').bytes);
    assert.deepEqual(main,Buffer.concat([Buffer.from([239,187,191]),Buffer.from(completed)]));
    return {typingCoalesced:true,saveRetainedUndo:true,perFileHistory:true,keyboardSourceOnly:true,
        nativeImeOneUnit:true,completionUndoRedo:true,lineToolUndoRedo:true,replaceUndoRedo:true,unrelatedFilesPreserved:true,imeEvents:observed,
        sourceSha256:sha(main),label};
}
(async()=>{
    try{
        await start();await open(false);await H.reveal(page,page.locator('#new-project'));
        await page.locator('#new-project').fill(name);await page.getByRole('button',{name:'Create project',exact:true}).click();
        for(const [filename,buffer]of fixtures){await H.$(page,'import-input').setInputFiles({name:filename,mimeType:'application/octet-stream',buffer});await H.$(page,'select-file').filter({hasText:filename}).waitFor();}
        await selectedFile('main.tex');await saveCurrent();
        checkpoint('imported exact bytes');
        const imported=await capture();for(const [filename,bytes]of fixtures)assert.deepEqual(Buffer.from(imported.files.find(f=>f.path===filename).bytes),bytes);
        record.online=await exercise('online');
        await page.evaluate(()=>navigator.serviceWorker.ready);await page.waitForFunction(()=>navigator.serviceWorker.controller!==null);
        await context.close();context=null;await stop();await assert.rejects(fetch(origin+'/'));
        await open(true);await H.open(page,name);await selectedFile('main.tex');
        assert.equal(await H.$(page,'source-undo').isDisabled(),true,'session undo is not durable history');
        const current=await value();
        // Start a fresh ordinary edit after cold restart, retaining saved project/history.
        await H.$(page,'editor').fill(current.replace('Typing: abc','Typing: ').replace('IME: їжак','IME: ').replace('\\section{}\n','\\sec\n'));
        await saveCurrent();record.coldOffline=await exercise('cold-offline');
        assert.deepEqual(record.pageErrors,[]);assert.deepEqual(record.externalRequests,[]);record.result='PASS';
    }catch(error){record.failure=String(error.stack||error).slice(0,4096);process.exitCode=1;
        if(page){record.failureState=await page.evaluate(()=>({source:document.querySelector('[data-testid=editor]')?.value,
            journal:document.querySelector('[data-testid=source-journal-status]')?.textContent,
            undoDisabled:document.querySelector('[data-testid=source-undo]')?.disabled,
            redoDisabled:document.querySelector('[data-testid=source-redo]')?.disabled,
            completion:document.querySelector('[data-testid=source-completion-status]')?.outerHTML,
            proposals:document.querySelector('[data-testid=source-completion-results]')?.textContent,
            selection:[document.querySelector('[data-testid=editor]')?.selectionStart,document.querySelector('[data-testid=editor]')?.selectionEnd],
            status:document.querySelector('.workspace-header .status')?.textContent})).catch(()=>null);}}
    finally{if(context)await context.close().catch(()=>{});await stop();fs.rmSync(profile,{recursive:true,force:true});
        fs.writeFileSync(receiptPath,JSON.stringify(record,null,2)+'\n');process.stdout.write(JSON.stringify({result:record.result,failure:record.failure})+'\n');}
})();
