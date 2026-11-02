// Real registered Rust/C# assistance UI, then a whole-browser host-stopped
// offline restart. Test tooling only: no provider, worker or storage injection.
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
const receiptPath = path.join(out, 'authoring-assist-receipt.json');
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
const source = Buffer.concat([Buffer.from([239,187,191]), Buffer.from([
    '\\documentclass{article}', '\\usepackage{graphicx,amsmath}', '\\newcommand{\\hello}{Hello}',
    '\\begin{document}', '\\section{Intro Київ🙂}\\label{sec:intro}',
    '\\input{chapter}', '\\ref{sec:}', '\\cite{bo}', '\\includegraphics{fig}',
    '\\begin{ali}', '\\hello', '\\sec', '\\text', '\\end{document}', ''
].join('\r\n'))]);
const fixtures = [
    ['main.tex', source], ['chapter.tex', Buffer.from('\\section{Chapter}\\label{sec:chapter}\n')],
    ['references.bib', Buffer.from('@book{book,title={Synthetic book},author={Author},year={2025}}\n')],
    ['figure.png', fs.readFileSync(path.join(__dirname, '../src/Client/wwwroot/icons/icon-192.png'))]
];
// Include the bibliography through the literal supported declaration.
fixtures[0][1] = Buffer.from(source.toString('utf8').replace('\\begin{document}', '\\bibliography{references}\r\n\\begin{document}'));
const record = {schemaVersion:1, result:'FAIL', scope:'registered-authoring-assistance-online-cold-offline',
    sdkVersion:manifest.version, sourceSha256:sha(fixtures[0][1]), online:null, coldOffline:null,
    observations:[], pageErrors:[], externalRequests:[], workers:[], failure:null};
const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'overclone-assistance-'));
const sockets = new Set();
let server, context, page;
const origin = 'http://127.0.0.1:8782';
const name = 'Assistance synthetic ' + Date.now();
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
    await new Promise(resolve => server.listen(8782,'127.0.0.1',resolve));
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
async function caret(token) {
    await H.reveal(page,H.$(page,'editor'));
    await H.$(page,'editor').evaluate((element,token)=>{
        const index=element.value.lastIndexOf(token);if(index<0)throw Error('Missing caret token');
        element.focus();element.setSelectionRange(index+token.length,index+token.length);
    },token);
    await H.$(page,'editor').evaluate(e=>({start:e.selectionStart,end:e.selectionEnd}));
}
async function suggest(contextName,token,query) {
    await caret(token);
    await H.$(page,'authoring-assist-context').selectOption(contextName);
    await H.$(page,'authoring-assist-query').fill(query);
    await H.$(page,'authoring-assist-suggest').click();
    await page.waitForFunction(()=>{
        const status=document.querySelector('[data-testid=authoring-assist-status]')?.textContent || '';
        return /Literal source observations only|Partial literal source observations|unavailable|refused|current collapsed|exact source prefix/.test(status);
    });
    assert.match(await H.$(page,'authoring-assist-status').textContent(),/Literal source observations only|Partial literal source observations/);
}
async function exercise(label) {
    await H.reveal(page,H.$(page,'authoring-assist-disclosure'));
    await H.$(page,'authoring-assist-disclosure').evaluate(e=>{e.open=true;});
    const rootId=await H.$(page,'authoring-assist-root').locator('option').filter({hasText:'main.tex'}).getAttribute('value');
    assert.ok(rootId);await H.$(page,'authoring-assist-root').selectOption(rootId);
    const before=await H.head(page,name);
    const cases=[['Environment','\\begin{ali','ali','align'],['Reference','\\ref{sec:','sec:','sec:intro'],
        ['Citation','\\cite{bo','bo','book'],['InputPath','\\includegraphics{fig','fig','figure.png'],
        ['Definition','\\hello\r','hello','hello']];
    // Textarea newlines are LF even when stored source has CRLF/BOM.
    cases[4][1]='\\hello\n';
    for(const [kind,token,query,expected] of cases) {
        await suggest(kind,token.trimEnd(),query);
        const candidates=await H.$(page,'authoring-assist-candidates').textContent();
        record.observations.push({label,kind,query,candidates:candidates.slice(0,2048),
            limitations:await H.$(page,'authoring-assist-limitations').textContent(),
            caret:await H.$(page,'editor').evaluate(e=>({start:e.selectionStart,end:e.selectionEnd,
                around:e.value.slice(Math.max(0,e.selectionEnd-24),e.selectionEnd+24)}))});
        assert.ok(candidates.includes(expected),kind);
        if(kind==='Definition') {
            await H.$(page,'authoring-assist-definition').first().click();
            await page.waitForFunction(()=>{
                const e=document.querySelector('[data-testid=editor]');
                return e&&e.value.slice(e.selectionStart,e.selectionEnd)==='hello';
            });
        }
    }
    await suggest('Outline','\\sec','');
    assert.ok((await H.$(page,'authoring-assist-outline').textContent()).includes('Intro Київ🙂'));
    await H.$(page,'authoring-assist-heading').filter({hasText:'Chapter'}).click();
    await page.waitForFunction(()=>{
        const e=document.querySelector('[data-testid=editor]');
        return e&&e.value.slice(e.selectionStart,e.selectionEnd)==='Chapter';
    });
    await H.$(page,'select-file').filter({hasText:'main.tex'}).click();
    await suggest('Command','\\text','text');
    const unmodified=await H.head(page,name);assert.equal(unmodified.id,before.id);
    for(const f of before.files)assert.deepEqual(unmodified.files.find(g=>g.id===f.id)?.bytes,f.bytes);
    // A native DOM caret move must invalidate insertion, even with unchanged source.
    await caret('\\sec');
    // The current report may be removed between a locator count and click.
    // Require the real UI to retire every eligible stale insertion instead.
    await page.waitForFunction(()=>[...document.querySelectorAll('[data-testid=authoring-assist-insert]')]
        .every(button=>button.disabled||!button.getClientRects().length));
    await saveCurrent();
    const stale=await H.head(page,name);assert.equal(stale.id,before.id);
    await suggest('Command','\\sec','sec');
    const candidate=H.$(page,'authoring-assist-candidates').locator('li').filter({has:page.locator('code',{hasText:/^section$/})});
    await candidate.getByRole('button',{name:'Insert',exact:true}).click();
    await saveCurrent();
    const after=await H.head(page,name);
    const oldMain=Buffer.from(before.files.find(f=>f.path==='main.tex').bytes);
    const newMain=Buffer.from(after.files.find(f=>f.path==='main.tex').bytes);
    assert.deepEqual(newMain,Buffer.from(oldMain.toString('utf8').replace(/\\sec(?=\r?\n)/,'\\section{}')));
    for(const f of before.files.filter(f=>f.path!=='main.tex'))assert.deepEqual(after.files.find(g=>g.id===f.id)?.bytes,f.bytes);
    return {contexts:7, definitionSelected:true, includedOutlineSelected:true, staleCaretRefused:true,
        exactInsertion:true, unrelatedFilesPreserved:true, finalSourceSha256:sha(newMain), label};
}
(async()=>{
    try {
        await start();await open(false);
        await H.reveal(page,page.locator('#new-project'));
        await page.locator('#new-project').fill(name);await page.getByRole('button',{name:'Create project',exact:true}).click();
        for(const [filename,buffer] of fixtures) {
            await H.$(page,'import-input').setInputFiles({name:filename,mimeType:'application/octet-stream',buffer});
            await H.$(page,'select-file').filter({hasText:filename}).waitFor();
        }
        await H.$(page,'select-file').filter({hasText:'main.tex'}).click();await saveCurrent();
        const imported=await H.head(page,name);
        for(const [filename,bytes] of fixtures)
            assert.deepEqual(Buffer.from(imported.files.find(f=>f.path===filename)?.bytes || []),bytes,'exact imported '+filename);
        record.initialFilesMatch=true;
        record.online=await exercise('online');
        await page.evaluate(()=>navigator.serviceWorker.ready);
        await page.waitForFunction(()=>navigator.serviceWorker.controller!==null);
        await context.close();context=null;await stop();
        await assert.rejects(fetch(origin+'/'),undefined,'static origin is physically stopped');
        await open(true);
        await H.$(page,'open-project').filter({hasText:name}).click().catch(async()=>{
            await page.getByRole('button',{name,exact:true}).click();
        });
        await H.$(page,'select-file').filter({hasText:'main.tex'}).click();
        // A second explicit insertion uses a fresh remaining command prefix.
        // Restore only through ordinary visible editing, then compare its exact LF preimage.
        const current=await H.$(page,'editor').inputValue();
        await H.$(page,'editor').fill(current.replace('\\section{}\n','\\sec\n'));await saveCurrent();
        record.coldOffline=await exercise('cold-offline');
        assert.deepEqual(record.pageErrors,[]);assert.deepEqual(record.externalRequests,[]);
        const assistanceWorkers=record.workers.filter(w=>w.url.endsWith('/js/authoring-assist-worker.js'));
        assert.ok(assistanceWorkers.length>=14,'real dedicated Rust workers in both sessions');
        assert.ok(assistanceWorkers.every(w=>w.closed),'completed assistance workers physically close');
        record.result='PASS';
    } catch(error) {record.failure=String(error.stack||error).slice(0,4096);process.exitCode=1;
        if(page)record.failureState=await page.evaluate(()=>({
            assistance:document.querySelector('[data-testid=authoring-assist-status]')?.textContent,
            query:document.querySelector('[data-testid=authoring-assist-query]')?.value,
            context:document.querySelector('[data-testid=authoring-assist-context]')?.value,
            source:document.querySelector('[data-testid=editor]')?.value,
            selection:[document.querySelector('[data-testid=editor]')?.selectionStart,document.querySelector('[data-testid=editor]')?.selectionEnd]
        })).catch(()=>null);}
    finally {
        if(context)await context.close().catch(()=>{});await stop();fs.rmSync(profile,{recursive:true,force:true});
        fs.writeFileSync(receiptPath,JSON.stringify(record,null,2)+'\n');
        process.stdout.write(JSON.stringify({result:record.result,failure:record.failure})+'\n');
    }
})();
