'use strict';
// Actual published app. Run with PUBLISHED_WWWROOT and a writable RESULT_DIR.
const assert = require('node:assert/strict');
const {chromium} = require('playwright');
const {createHash} = require('node:crypto');
const {spawn} = require('node:child_process');
const {once} = require('node:events');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const H = require('./review-ui-helpers.cjs');

async function selectNumber(page, literal, hitIndex, count) {
  const pane = H.$(page, 'pdf-pane');
  const field = pane.locator('input[aria-label="Search PDF"]');
  await H.reveal(page, field);
  await field.fill(literal);
  const before = await pane.getAttribute('data-generation');
  await pane.getByRole('button',{name:'Search document',exact:true}).click();
  await page.waitForFunction(previous => {
    const pane = document.querySelector('[data-testid="pdf-pane"]');
    return pane?.dataset.generation !== previous && pane?.querySelector('[data-testid="search-status"]')?.dataset.searchStatus === 'Complete';
  }, before, {timeout:45000});
  assert.equal(await H.$(page,'search-status').getAttribute('data-match-count'), String(count));
  for (let i = 1; i <= hitIndex; i++) {
    const beforeHit = await pane.getAttribute('data-generation');
    await pane.getByRole('button',{name:'Next result',exact:true}).click();
    await page.waitForFunction(({beforeHit,text}) => {
      const pane = document.querySelector('[data-testid="pdf-pane"]'), canvas = pane?.querySelector('[data-testid="pdf-canvas"]');
      return pane?.dataset.generation !== beforeHit && pane?.querySelector('[data-testid="selected-result"]')?.textContent === text
        && canvas?.dataset.drawGeneration === pane?.dataset.generation && canvas?.dataset.pdfHash === pane?.dataset.document;
    }, {beforeHit,text:`Result ${i} / ${count}`}, {timeout:45000});
  }
  assert.ok(await H.$(page,'glyph-highlight').count() > 0);
}

async function capture(page, role, literal, pageIndex, start) {
  const selector = H.$(page, `rendered-reference-pair-${role}`);
  await H.reveal(page, selector);
  await selector.click();
  const slot = H.$(page, `rendered-reference-pair-${role}-selection`);
  await slot.waitFor();
  const data = await slot.evaluate(node => ({...node.dataset, text:node.textContent.trim()}));
  assert.deepEqual([data.pageIndex,data.characterStart,data.characterEnd], [String(pageIndex),String(start),String(start+literal.length)]);
  assert.equal(data.text, `${literal} on PDF page ${pageIndex+1}`);
  assert.equal(data.generation,await H.$(page,'pdf-pane').getAttribute('data-generation'));
  return data;
}

async function compare(page, comparison, workers) {
  const offset = workers().length;
  await H.$(page,'rendered-reference-pair-compare').click();
  const result = H.$(page,'rendered-reference-pair-result');
  await result.and(page.locator(`[data-comparison="${comparison}"]`)).waitFor({timeout:45000});
  const data = await result.evaluate(node => ({...node.dataset, text:node.textContent.trim()}));
  const requests = workers().slice(offset).map(worker => new URL(worker.url).pathname);
  assert.equal(requests.filter(name=>name==='/__oop/native-worker/pdf').length,comparison==='NumbersMatch'||comparison==='NumbersDiffer'?2:1);
  assert.equal(requests.filter(name=>name==='/js/rendered-review-worker.js').length,1);
  assert.match(data.compileRequest,/^[0-9a-f]{32}$/);
  assert.match(data.factsSha256,/^[0-9a-f]{64}$/);
  return {...data,requests};
}

async function verify(page, project, workers, recompile) {
  const before = await H.head(page,project);
  const compile = H.$(page,'compile-details');
  const request = (await compile.textContent()).match(/Request: ([a-f0-9]{32});/)?.[1];
  assert.ok(request);
  const compareButton = H.$(page,'rendered-reference-pair-compare');
  await H.reveal(page, compareButton);
  assert.ok(await compareButton.isDisabled());
  await selectNumber(page,'[1]',1,2);
  const citation = await capture(page,'citation','[1]',0,17);
  const sameRange = await capture(page,'entry','[1]',0,17);
  const overlap = await compare(page,'Indeterminate',workers);
  assert.equal(overlap.reason,'overlapping-selection-ranges');

  await selectNumber(page,'[1]',2,2);
  const firstEntry = await capture(page,'entry','[1]',1,0);
  const match = await compare(page,'NumbersMatch',workers);
  assert.equal(match.reason,'confirmed-printed-number-pair');
  assert.equal(match.compileRequest,request);
  assert.match(match.text,/match \(\[1\]\)/);

  await selectNumber(page,'[2]',1,1);
  const secondEntry = await capture(page,'entry','[2]',1,18);
  const differ = await compare(page,'NumbersDiffer',workers);
  assert.equal(differ.reason,'confirmed-printed-number-pair');
  assert.equal(differ.compileRequest,request);
  assert.equal(differ.factsSha256,match.factsSha256);
  assert.match(differ.text,/\[1\] differs from .*\[2\]/);
  assert.deepEqual(await H.head(page,project),before);
  const unchangedCitation = await H.$(page,'rendered-reference-pair-citation-selection').evaluate(node=>({...node.dataset,text:node.textContent.trim()}));
  assert.deepEqual(unchangedCitation,citation);

  let newRequest = null;
  if (recompile) {
    await recompile();
    newRequest = (await compile.textContent()).match(/Request: ([a-f0-9]{32});/)?.[1];
    assert.ok(newRequest && newRequest!==request);
    await H.$(page,'rendered-reference-pair-result').waitFor({state:'detached'});
    assert.ok(await compareButton.isDisabled());
    assert.equal(await H.$(page,'rendered-reference-pair-citation-selection').count(),0);
    assert.equal(await H.$(page,'rendered-reference-pair-entry-selection').count(),0);
  }
  return {request,newRequest,citation,sameRange,firstEntry,secondEntry,overlap,match,differ};
}

const origin = 'http://127.0.0.1:8080';
const project = 'Printed reference acceptance';
assert.ok(process.env.PUBLISHED_WWWROOT && process.env.RESULT_DIR,
    'Set PUBLISHED_WWWROOT and RESULT_DIR explicitly');
const publication = path.resolve(process.env.PUBLISHED_WWWROOT);
const output = path.resolve(process.env.RESULT_DIR);
const receipt = {result:'RUNNING',errors:[],external:[]};
let context, page, server, workers, profile, phase='setup';
const digest = bytes => createHash('sha256').update(bytes).digest('hex');

async function open(offline) {
    context=await chromium.launchPersistentContext(profile,{headless:true,offline});
    page=context.pages()[0]??await context.newPage();
    workers=[];
    page.on('worker',worker=>workers.push({url:worker.url()}));
    page.on('pageerror',error=>receipt.errors.push(String(error).slice(0,400)));
    page.on('request',request=>{
        if (/^https?:/.test(request.url()) && !request.url().startsWith(origin+'/')) receipt.external.push(new URL(request.url()).origin);
    });
    await page.goto(origin);
    await H.ready(page);
}
async function choose() {
    const engine=page.locator('select[aria-label="Compile engine"]');
    await H.reveal(page,engine);
    await page.waitForFunction(()=>document.querySelector('[data-testid=compiler-readiness]')?.textContent.startsWith('Local compiler checked'),null,{timeout:85000});
    await page.getByRole('combobox',{name:'Compile main file',exact:true}).selectOption({label:'main.tex'});
    await engine.selectOption('local-authoring-v4');
    await H.bibliography(page,'None');
}
async function compile() {
    const previous=await H.$(page,'compile-details').textContent();
    await H.$(page,'compile').click();
    await page.waitForFunction(prior=>
        document.querySelector('[data-testid=compile-details]')?.textContent!==prior
        && document.querySelector('[data-testid=compile-status]')?.textContent.startsWith('Compilation finished.')
        && document.querySelector('[data-testid=compile-panel]')?.dataset.isCurrent==='true'
        && document.querySelector('[data-testid=canvas-status]')?.textContent==='Drawn'
        && document.querySelector('[data-testid=page-label]')?.textContent==='Page 1 of 2',
    previous,{timeout:105000});
}
(async()=>{
    try {
        profile=await fs.mkdtemp(path.join(os.tmpdir(),'overclone-reference-'));
        const source=await fs.readFile(path.join(__dirname,'../rust/src/rendered_review/fixtures/reference_pair/main.tex'));
        receipt.fixtureSha256=digest(source);
        assert.equal(receipt.fixtureSha256,'0d12adacda55be0f1424429d5fb483acfe8252e444a16b9a47d3851ded07dd4e');
        server=spawn(process.execPath,[path.join(__dirname,'static-server.cjs'),publication,'8080'],{stdio:'ignore'});
        for(let i=0;i<100;i++){
            try {
                const response=await fetch(origin+'/service-worker-assets.js');
                const assets=await response.text();
                receipt.manifest={sha256:digest(assets),version:JSON.parse(assets.match(/self\.assetsManifest\s*=\s*(\{[\s\S]*\})\s*;/)[1]).version};
                break;
            } catch {await new Promise(resolve=>setTimeout(resolve,100));}
        }
        assert.ok(receipt.manifest);
        phase='online';
        await open(false);
        await H.reveal(page,page.locator('#new-project'));
        await page.locator('#new-project').fill(project);
        await page.getByRole('button',{name:'Create project',exact:true}).click();
        await H.$(page,'import-input').setInputFiles({name:'main.tex',mimeType:'text/plain',buffer:source});
        await page.locator('.editor-toolbar label',{hasText:'main.tex'}).waitFor();
        await H.save(page);
        await choose();
        await compile();
        process.stdout.write('online compiled\n');
        receipt.online=await verify(page,project,()=>workers,compile);
        await page.waitForFunction(()=>!!navigator.serviceWorker.controller,null,{timeout:45000});
        await context.close(); context=null;
        const exited=once(server,'exit'); server.kill(); await exited; server=null;
        await assert.rejects(fetch(origin),/fetch failed/);
        receipt.originRefused=true;
        phase='coldOffline';
        await open(true);
        await H.open(page,project);
        const input=await H.head(page,project);
        assert.deepEqual(input.files.map(f=>[f.path,digest(Buffer.from(f.bytes))]),[['main.tex',receipt.fixtureSha256]]);
        await choose();
        await compile();
        process.stdout.write('cold offline compiled\n');
        receipt.coldOffline=await verify(page,project,()=>workers,compile);
        assert.deepEqual(receipt.errors,[]);
        assert.deepEqual(receipt.external,[]);
        receipt.result='PASS';
    } catch(error) {
        receipt.result='FAIL'; receipt.failure={phase,error:String(error).slice(0,800)};
        if (page && !page.isClosed()) {
            for(const id of ['compiler-readiness','compile-status','preview-status','search-status','selected-result','rendered-reference-pair-notice','rendered-reference-pair-result']){
                const loc=H.$(page,id);
                if(await loc.count()===1) receipt.failure[id]=(await loc.textContent()).slice(0,350);
            }
        }
        process.exitCode=1;
    } finally {
        if(context) await context.close();
        if(server) server.kill();
        if(profile) await fs.rm(profile,{recursive:true,force:true});
        await fs.writeFile(path.join(output,'reference-pair-receipt.json'),JSON.stringify(receipt,null,2)+'\n');
        process.stdout.write(JSON.stringify(receipt)+'\n');
    }
})();
