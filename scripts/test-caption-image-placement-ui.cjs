'use strict';
// Acceptance tooling. Serve one pinned publication and drive real compiled UI.
// Run in isolated Linux Playwright 1.56.1 with network disabled. See handoff.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const {createHash} = require('node:crypto');
const H = require('./review-ui-helpers.cjs');
const {buildZip} = require('./zip-util.cjs');
const {validate: validateCsp} = require('./test-csp.cjs');
const {P16, P16_ID} = require('./test-real-thesis-ui.cjs');

const MAIN = 'тези/my thesis.tex';
const CAPTION = 'CAPTIONANCHOR';
const FIXTURE = [
    {file: 'main.tex', name: MAIN, length: 432, sha256: 'bc3bf0110f1e133a7d15f2db6172ed3aaffd706527c8406e365ae70bb895ed87'},
    {file: 'marker.png', name: 'marker.png', length: 190, sha256: '0ffdf52e74ca7c98c399d72f2bd2952550eb34038cd986286ea6d9baa5880972'},
];
const RENDERER = {
    'e1.js': {length: 14175, sha256: 'f95ae827b12703999ce1daafebbf6830f5f4d2c14bb7805a8f47abe54772b079'},
    'e1.wasm': {length: 3725577, sha256: '2e2126ead8e00ef7c79d21e3836d66957b19e827523f7c054ca887f3c11ae5b7'},
};
// Oracle: actual P16 compiler -> exact E1 layout2/object schema1 -> Rust 1.1.
// Object 2 is object 1's same image/Y/size with only its X position changed.
const EXPECTED = [
    {index: '0', box: [163.66, 615.39, 288.29, 633.33], relation: 'CaptionBelow', gapMm: 9.408, errorMm: 0.028},
    {index: '1', box: [163.66, 531.71, 288.29, 549.64], relation: 'CaptionAbove', gapMm: 11.559, errorMm: 0.028},
    {index: '2', box: [318.08, 531.71, 442.71, 549.64], relation: 'Indeterminate'},
];
const MIME = {'.html':'text/html','.js':'text/javascript','.mjs':'text/javascript','.json':'application/json',
    '.wasm':'application/wasm','.css':'text/css','.svg':'image/svg+xml','.png':'image/png',
    '.ico':'image/x-icon','.webmanifest':'application/manifest+json'};
const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const pin = bytes => ({length: bytes.length, sha256: sha(bytes)});
const element = (page, name) => H.$(page, 'rendered-caption-image-' + name);
const env = key => { assert.ok(process.env[key], key + ' required'); return process.env[key]; };

function fixture(directory = path.join(__dirname, '../tests/browser/fixtures/caption-image-placement')) {
    return FIXTURE.map(({file, name, ...expected}) => {
        const bytes = fs.readFileSync(localFile(fs.realpathSync(directory), file));
        assert.deepEqual(pin(bytes), expected, name);
        return [name, bytes];
    });
}
function localFile(root, relative) {
    assert.ok(typeof relative === 'string' && relative && !relative.startsWith('/') && !relative.includes('\\')
        && relative.split('/').every(part => part && part !== '.' && part !== '..'));
    const file = fs.realpathSync(path.join(root, relative));
    assert.ok(file.startsWith(root + path.sep) && fs.statSync(file).isFile());
    return file;
}
function preflight(root, sdk, rustHash, headerFile = path.join(__dirname, '../docs/examples/static-csp-headers.conf')) {
    const read = name => fs.readFileSync(localFile(root, name));
    const headers = fs.readFileSync(headerFile, 'utf8');
    validateCsp(read('index.html').toString('utf8'), headers);
    const csp = headers.split('\n').map(line => line.trim()).find(line => line.startsWith('Content-Security-Policy:'))
        .slice('Content-Security-Policy:'.length).trim();
    const raw = read('service-worker-assets.js');
    const match = raw.toString('utf8').match(/self\.assetsManifest\s*=\s*(\{[\s\S]*\})\s*;/);
    assert.ok(match, 'published service worker manifest');
    const manifest = JSON.parse(match[1]);
    assert.equal(manifest.version, sdk, 'exact master-published SDK');
    assert.ok(manifest.assets.length > 0 && manifest.assets.length <= 4096);
    const assets = new Map();
    for (const asset of manifest.assets) {
        assert.ok(!assets.has(asset.url), 'unique published asset');
        const hash = createHash('sha256').update(read(asset.url));
        assert.equal('sha256-' + hash.copy().digest('base64'), asset.hash, asset.url);
        assets.set(asset.url, hash.digest('hex'));
    }
    for (const name of ['js/compile-port-worker.js', 'js/pdf-port-worker.js', 'js/rendered-review-worker.js',
        'rust-generated/document_engine_bg.wasm', 'bundled-runtime/compiler-suite.zip', 'bundled-runtime/e1.js', 'bundled-runtime/e1.wasm'])
        assert.ok(assets.has(name), 'precache: ' + name);
    assert.equal(assets.get('bundled-runtime/compiler-suite.zip'), P16.zipHash, 'exact reviewed P16 bundle');
    assert.equal(fs.statSync(localFile(root, 'bundled-runtime/compiler-suite.zip')).size, P16.zipBytes);
    for (const [name, expected] of Object.entries(RENDERER))
        assert.deepEqual(pin(read('bundled-runtime/' + name)), expected, 'exact E1 object-layout candidate');
    const rustWasm = read('rust-generated/document_engine_bg.wasm');
    const exports = WebAssembly.Module.exports(new WebAssembly.Module(rustWasm)).map(export_ => export_.name);
    assert.ok(exports.includes('evaluate_rendered_review'), 'actual Rust WASM evaluator');
    assert.equal(assets.get('rust-generated/document_engine_bg.wasm'), rustHash, 'exact merged Rust WASM');
    return {csp, assets, publication: {sdk, assets: assets.size, manifest: pin(raw), allAssetsSriVerified: true,
        p16: {...P16, identity: P16_ID}, renderer: RENDERER, rustWasmSha256: rustHash}};
}

function browsers() {
    const records = [];
    for (const name of fs.readdirSync('/proc')) {
        if (!/^\d+$/.test(name)) continue;
        try {
            const exe = path.basename(fs.readlinkSync('/proc/' + name + '/exe'));
            if (!/chromium|chrome|headless_shell/i.test(exe)) continue;
            const stat = fs.readFileSync('/proc/' + name + '/stat', 'utf8');
            records.push({pid: Number(name), exe, startTicks: stat.slice(stat.lastIndexOf(') ') + 2).split(' ')[19]});
        } catch { /* Process exited during census. */ }
    }
    return records;
}
async function waitForNoBrowser() {
    for (let attempt = 0; attempt < 40; attempt++) {
        if (browsers().length === 0) return [];
        await new Promise(resolve => setTimeout(resolve, 100));
    }
    assert.deepEqual(browsers(), [], 'every pre-restart browser process closed');
    return [];
}
async function sameHead(page, project, entries, original) {
    const head = await H.head(page, project);
    assert.equal(head.files.length, entries.length);
    for (const [name, bytes] of entries)
        assert.deepEqual(Buffer.from(head.files.find(file => file.path === name)?.bytes ?? []), bytes, name);
    if (original) assert.deepEqual(head, original, 'inspection and compilation are read-only for the saved revision');
    return head;
}
async function compile(page, outputPath) {
    const main = page.locator('select[aria-label="Compile main file"]');
    await H.reveal(page, main);
    await main.selectOption({label: MAIN});
    await page.getByRole('combobox', {name:'Compile engine', exact:true}).selectOption('local-authoring-v4');
    await H.bibliography(page, 'None');
    const details = H.$(page, 'compile-details');
    const previous = {request: (await details.textContent()).match(/Request: ([a-f0-9]{32})/)?.[1] ?? null,
        generation: await H.$(page, 'pdf-pane').getAttribute('data-generation')};
    await H.$(page, 'compile').click();
    await page.waitForFunction(old => {
        const element = id => document.querySelector(`[data-testid="${id}"]`);
        const request = element('compile-details')?.textContent.match(/Request: ([a-f0-9]{32})/)?.[1];
        const pane = element('pdf-pane'), canvas = element('pdf-canvas'), panel = element('compile-panel');
        return request && request !== old.request && pane.dataset.generation !== old.generation
            && panel.dataset.isCurrent === 'true' && pane.dataset.document === panel.dataset.outputHash
            && canvas?.dataset.drawGeneration === pane.dataset.generation && canvas?.dataset.pdfHash === pane.dataset.document
            && /^Compilation finished\./.test(element('compile-status')?.textContent ?? '')
            && /^Idle/.test(element('compile-work-status')?.textContent ?? '')
            && element('preview-status')?.dataset.previewStatus === 'Available' && element('canvas-status')?.textContent === 'Drawn';
    }, previous, {timeout:150000});
    const text = await details.textContent();
    const request = text.match(/Request: ([a-f0-9]{32})/)[1], digest = text.match(/Input: ([a-f0-9]{64})/)[1];
    const pdfHash = await H.$(page, 'compile-panel').getAttribute('data-output-hash');
    assert.match(pdfHash, /^[a-f0-9]{64}$/);
    assert.equal((await H.$(page, 'page-label').textContent()).trim(), 'Page 1 of 1');
    assert.equal((await H.$(page, 'page-text').textContent()).trim(), CAPTION);
    const observations = await page.evaluate(async () => (await import('./js/compile-port-bridge.js')).observations());
    assert.equal(observations.active, 0);
    assert.equal(observations.started, observations.settled);
    const phases = observations.trace.filter(row => row.tag.request === request);
    assert.deepEqual(phases.map(row => row.tag.tool), ['latex','latex']);
    for (const row of phases) {
        assert.equal(row.tag.profile, 16);
        assert.equal(row.tag.runtimeIdentity, P16_ID);
        assert.equal(row.tag.engineId, 'pdftex');
        assert.equal(row.tag.digest, digest);
        assert.ok(row.posted && row.entered && row.terminated);
        assert.equal(row.outcome, 'native-return');
        assert.equal(row.nativeReturn, 0);
    }
    const [download] = await Promise.all([page.waitForEvent('download'), H.$(page, 'download-pdf').click()]);
    await download.saveAs(outputPath);
    const pdf = fs.readFileSync(outputPath);
    assert.equal(pdf.subarray(0, 5).toString(), '%PDF-');
    assert.ok(pdf.subarray(-1024).includes(Buffer.from('%%EOF')));
    assert.equal(sha(pdf), pdfHash);
    return {request, digest, pdf: pin(pdf), phases};
}

async function annotateCaption(page, compile_) {
    const search = H.$(page, 'pdf-pane').locator('input[aria-label="Search PDF"]');
    await H.reveal(page, search);
    await search.fill(CAPTION);
    await page.getByRole('button', {name:'Search document', exact:true}).click();
    await page.waitForFunction(() => document.querySelector('[data-testid="search-status"]')?.dataset.searchStatus === 'Complete');
    assert.equal(await H.$(page, 'search-status').getAttribute('data-match-count'), '1');
    await page.getByRole('button', {name:'Next result', exact:true}).click();
    await H.$(page, 'glyph-highlight').first().waitFor();
    const useSearch = H.$(page, 'rendered-role-use-search');
    await H.reveal(page, useSearch);
    await H.$(page, 'rendered-role').selectOption('figureCaption');
    await H.$(page, 'rendered-role-certainty').selectOption('confirmedObservation');
    await useSearch.click();
    const draft = H.$(page, 'rendered-role-search-draft');
    assert.equal(await draft.getAttribute('data-current'), 'true');
    assert.equal(await draft.getAttribute('data-compile-request'), compile_.request);
    assert.equal(await draft.getAttribute('data-pdf-sha256'), compile_.pdf.sha256);
    assert.equal(await draft.getAttribute('data-glyph-text'), CAPTION);
    assert.equal(await draft.getAttribute('data-generation'), await H.$(page, 'pdf-pane').getAttribute('data-generation'));
    await H.$(page, 'rendered-role-add').click();
    const label = H.$(page, 'rendered-role-row');
    await label.waitFor({state:'attached'});
    assert.equal(await label.count(), 1);
    const data = await label.evaluate(element => ({...element.dataset}));
    assert.equal(data.role, 'figureCaption');
    assert.equal(data.certainty, 'confirmedObservation');
    assert.equal(data.provenance, 'authorLabel');
    assert.equal(data.pageIndex, '0');
    assert.equal(data.characterStart, '0');
    assert.equal(data.characterEnd, String(CAPTION.length));
    assert.match(data.annotationId, /^[1-9][0-9]*$/);
    return data;
}

async function placements(page, caption, workers) {
    const captionSelect = element(page, 'caption'), inspect = element(page, 'inspect');
    const imageSelect = element(page, 'object'), measure = element(page, 'measure'), result = element(page, 'result');
    await H.reveal(page, captionSelect);
    assert.deepEqual(await captionSelect.locator('option').evaluateAll(rows => rows.map(row => row.value)), ['', caption.annotationId]);
    assert.equal(await inspect.isDisabled(), true);
    await captionSelect.selectOption(caption.annotationId);
    const proof = {choices: [], results: []};
    const beforeInspection = workers.length;
    await inspect.click();
    await imageSelect.waitFor();
    const inspection = workers.slice(beforeInspection);
    assert.ok(inspection.some(worker => worker.path === '/__oop/native-worker/pdf'), 'actual native inventory worker');
    assert.ok(inspection.every(worker => worker.path !== '/js/rendered-review-worker.js'), 'image listing does not measure a pair');
    assert.equal(await measure.isDisabled(), true, 'image selection required');
    assert.equal(await result.count(), 0);
    const options = await imageSelect.locator('option').evaluateAll(rows => rows.filter(row => row.value)
        .map(row => ({index:row.value, text:row.textContent.trim()})));
    assert.deepEqual(options.map(option => option.index), EXPECTED.map(expected => expected.index));
    for (const [i, option] of options.entries()) {
        const coordinates = /^Object (\d+), x (-?[\d.]+)–(-?[\d.]+), y (-?[\d.]+)–(-?[\d.]+) in PDF coordinates$/.exec(option.text);
        assert.ok(coordinates, option.text);
        const [, index, left, right, bottom, top] = coordinates;
        assert.equal(index, option.index);
        assert.deepEqual([left,bottom,right,top].map(Number), EXPECTED[i].box);
    }
    proof.choices = options;
    for (const index of [1, 2, 0]) {
        const expected = EXPECTED[index];
        const before = workers.length;
        await imageSelect.selectOption(expected.index);
        await result.waitFor({state:'detached'});
        assert.ok(workers.slice(before).every(worker => worker.path !== '/js/rendered-review-worker.js'));
        await measure.click();
        await result.waitFor({timeout:125000});
        const data = await result.evaluate(element => ({text:element.textContent.trim(), ...element.dataset}));
        assert.equal(data.captionId, caption.annotationId);
        assert.equal(data.objectIndex, expected.index);
        assert.equal(data.relation, expected.relation, data.text);
        if (expected.relation === 'Indeterminate') {
            assert.equal(data.text, 'The caption and image do not have established horizontal overlap.');
            assert.doesNotMatch(data.text, /\bmm\b/);
        } else {
            const numbers = /^Caption (above|below) the selected image by ([\d.]+) ± ([\d.]+) mm\.$/.exec(data.text);
            assert.ok(numbers, data.text);
            assert.equal(numbers[1], expected.relation === 'CaptionAbove' ? 'above' : 'below');
            assert.equal(Number(numbers[2]), expected.gapMm);
            assert.equal(Number(numbers[3]), expected.errorMm);
        }
        const evaluated = workers.slice(before).filter(worker => worker.path === '/js/rendered-review-worker.js');
        assert.equal(evaluated.length, 1, 'one actual Rust worker per explicit pair');
        proof.results.push(data);
    }
    return proof;
}

async function main() {
    const root = fs.realpathSync(env('PUBLISHED_WWWROOT')), out = fs.realpathSync(env('RESULT_DIR'));
    const sdk = env('EXPECTED_SDK_VERSION'), source = env('CANDIDATE_SOURCE_DIGEST');
    const rust = env('EXPECTED_RUST_WASM_SHA256');
    assert.match(source, /^[a-f0-9]{64}$/);
    assert.match(rust, /^[a-f0-9]{64}$/);
    assert.equal(fs.readdirSync(out).length, 0, 'exclusive empty RESULT_DIR');
    assert.ok([undefined,'0','1'].includes(process.env.PREFLIGHT_ONLY));
    const receipt = path.join(out,'caption-image-placement-receipt.json');
    fs.writeFileSync(receipt, '', {flag:'wx', mode:0o600});
    const record = {schemaVersion:1, result:'FAIL', step:'preflight', actualBrowserExecution:false,
        sourceAssociation:{masterSuppliedSourceDigest:source}, harness:pin(fs.readFileSync(__filename)),
        online:{}, coldOffline:{}, workers:[], pageErrors:[], externalRequests:[], cspViolations:[],
        scope:'One pinned synthetic P16 pdfLaTeX project and author-selected caption/image placement observations.'};
    const flush = () => {
        const bytes = JSON.stringify(record,null,2)+'\n';
        assert.ok(Buffer.byteLength(bytes) <= 524288);
        fs.writeFileSync(receipt,bytes);
    };
    const step = text => {record.step = text; flush();};
    let server, context, origin;
    const sockets = new Set();
    const stopHost = async () => {
        if (!server) return;
        const old = server;
        server = null;
        for (const socket of sockets) socket.destroy();
        await new Promise((resolve,reject) => old.close(error => error ? reject(error) : resolve()));
    };
    const deadline = setTimeout(() => {
        record.failure = 'Caption placement acceptance deadline 600000ms';
        flush();
        const forced = setTimeout(() => process.exit(124),5000);
        Promise.allSettled([context?.close(),stopHost()]).finally(() => {clearTimeout(forced);process.exit(124);});
    },600000);
    try {
        const entries = fixture(process.env.CAPTION_IMAGE_FIXTURE_DIR), zip = buildZip(entries);
        record.projectInputs = {files:entries.map(([name,bytes]) => ({name,...pin(bytes)})), zip:pin(zip)};
        const checked = preflight(root,sdk,rust,process.env.CAPTION_IMAGE_CSP_HEADERS);
        record.publication = checked.publication;
        if (process.env.PREFLIGHT_ONLY === '1') {
            record.result = 'PREFLIGHT_ONLY';
            step('Exact static pins verified');
            return;
        }
        const {chromium} = require('playwright');
        assert.equal(require('playwright/package.json').version,'1.56.1');
        assert.deepEqual(browsers(), [], 'isolated browser process namespace');
        server = http.createServer((request,response) => {
            let file;
            try {
                const relative = decodeURIComponent(new URL(request.url,'http://localhost').pathname);
                file = localFile(root,relative === '/' ? 'index.html' : relative.slice(1));
            } catch {response.writeHead(404).end();return;}
            response.writeHead(200,{'Content-Type':MIME[path.extname(file)] || 'application/octet-stream',
                'Content-Security-Policy':checked.csp, 'X-Content-Type-Options':'nosniff', 'Cache-Control':'no-cache'});
            fs.createReadStream(file).pipe(response);
        });
        server.on('connection', socket => {
            sockets.add(socket);
            socket.on('close',() => sockets.delete(socket));
        });
        await new Promise((resolve,reject) => {server.once('error',reject);server.listen(0,'127.0.0.1',resolve);});
        origin = 'http://127.0.0.1:'+server.address().port;
        record.origin = origin;
        const project = 'CaptionImage-'+Date.now();
        record.project = project;
        async function launch(offline, phase) {
            context = await chromium.launchPersistentContext(path.join(out,'profile'),{headless:true,offline,acceptDownloads:true,locale:'en-US'});
            record.actualBrowserExecution = true;
            await context.addInitScript(() => {
                globalThis.__captionImageCsp = [];
                addEventListener('securitypolicyviolation', event => globalThis.__captionImageCsp.push(event.effectiveDirective));
            });
            const external = url => /^https?:/.test(url) && new URL(url).origin !== origin;
            context.on('request', request => {
                if (external(request.url())) record.externalRequests.push(new URL(request.url()).origin);
            });
            await context.route('**/*', route => external(route.request().url()) ? route.abort('blockedbyclient') : route.continue());
            context.on('page', page => {
                page.setDefaultTimeout(30000);
                page.on('pageerror', error => record.pageErrors.push(String(error).slice(0,600)));
                page.on('worker', worker => record.workers.push({phase,step:record.step,path:new URL(worker.url(),origin).pathname}));
            });
            const page = await context.newPage();
            const response = await page.goto(origin+'/');
            assert.equal(response?.status(),200,offline ? 'cached navigation' : 'served pinned publication');
            await H.ready(page);
            const urls = await page.evaluate(() => performance.getEntriesByType('resource')
                .map(entry => new URL(entry.name).pathname.slice(1)).filter(name => /^_framework\/Client\.[^/]+\.wasm$/.test(name)));
            assert.equal(urls.length,1,'one actual fingerprinted Client assembly');
            const loadedSha = checked.assets.get(urls[0]);
            assert.match(loadedSha,/^[a-f0-9]{64}$/);
            record[phase].loadedClient = {asset:urls[0],sha256:loadedSha};
            return page;
        }
        async function ready(page) {
            await page.waitForFunction(() => globalThis.__oopOfflineReady === true && navigator.serviceWorker.controller !== null,
                null,{timeout:240000});
            await page.waitForFunction(() => document.querySelector('[data-testid="compiler-readiness"]')?.textContent.startsWith('Local compiler checked'),
                null,{timeout:240000});
            assert.equal(await H.$(page,'runtime-zip-input').count(),0);
            assert.equal(await H.$(page,'pdf-runtime-manifest').count(),0);
        }
        async function exercise(page, phase, file) {
            const recordPhase = record[phase];
            step(phase+' actual P16 compile and draw');
            recordPhase.compile = await compile(page,path.join(out,file+'.pdf'));
            step(phase+' search and confirm caption');
            const caption = recordPhase.caption = await annotateCaption(page, recordPhase.compile);
            step(phase+' inspect and measure three explicit native occurrences');
            recordPhase.placement = await placements(page,caption,record.workers);
            assert.equal(await H.$(page,'compile-panel').getAttribute('data-output-hash'),recordPhase.compile.pdf.sha256);
            assert.equal(await H.$(page,'compile-panel').getAttribute('data-is-current'),'true');
            flush();
        }
        step('online publish pinned source ZIP');
        let page = await launch(false,'online');
        const importer = H.$(page,'import-zip');
        await H.reveal(page,importer);
        await importer.setInputFiles({name:'caption-image-source.zip',mimeType:'application/zip',buffer:zip});
        await H.reveal(page,H.$(page,'import-preview'));
        assert.equal(await H.$(page,'validation-error').count(),0);
        await H.$(page,'staged-name').fill(project);
        await H.$(page,'publish-import').click();
        await ready(page);
        const head = await sameHead(page,project,entries);
        record.persistedRevision = head.id;
        await exercise(page,'online','online');
        await sameHead(page,project,entries,head);
        step('recompile unchanged saved source and require stale labels and observation');
        assert.equal(await element(page,'result').count(),1);
        const evaluatorBefore = record.workers.filter(worker => worker.path === '/js/rendered-review-worker.js').length;
        const next = record.online.newCompile = await compile(page,path.join(out,'online-new-ticket.pdf'));
        assert.notEqual(next.request,record.online.compile.request);
        await H.$(page,'rendered-role-stale').waitFor({state:'attached'});
        for (const id of ['panel','inspect','object','measure','result']) assert.equal(await element(page,id).count(),0,id);
        assert.equal(record.workers.filter(worker => worker.path === '/js/rendered-review-worker.js').length,evaluatorBefore);
        await sameHead(page,project,entries,head);
        record.online.stale = {hiddenPreviousPlacementAndInputs:true,newTicket:true,
            pdfHashEqual:next.pdf.sha256 === record.online.compile.pdf.sha256,
            readOnlyRevision:true};
        record.cspViolations.push(...await page.evaluate(() => globalThis.__captionImageCsp));
        step('close whole browser and stop owned host');
        await context.close();
        context = null;
        const censusBeforeRelaunch = await waitForNoBrowser();
        await stopHost();
        await assert.rejects(fetch(origin+'/',{signal:AbortSignal.timeout(5000)}),error => error.cause?.code === 'ECONNREFUSED');
        record.restart = {censusBeforeRelaunch,hostRefused:true,offlineBeforeNavigation:true};
        step('cold offline reopen saved project');
        page = await launch(true,'coldOffline');
        const after = browsers();
        assert.ok(after.length > 0,'new Chromium processes');
        record.restart.newProcesses = after;
        assert.deepEqual(record.coldOffline.loadedClient,record.online.loadedClient);
        await H.open(page,project);
        await ready(page);
        await sameHead(page,project,entries,head);
        assert.equal(await H.$(page,'rendered-role-row').count(),0,'ephemeral labels are absent in the new browser session');
        assert.equal(await element(page,'result').count(),0);
        await exercise(page,'coldOffline','cold-offline');
        await sameHead(page,project,entries,head);
        assert.notEqual(record.coldOffline.compile.request,record.online.compile.request);
        assert.notEqual(record.coldOffline.compile.request,next.request);
        record.coldOffline.readOnlyRevisionVerified = true;
        record.cspViolations.push(...await page.evaluate(() => globalThis.__captionImageCsp));
        await assert.rejects(fetch(origin+'/',{signal:AbortSignal.timeout(5000)}),error => error.cause?.code === 'ECONNREFUSED');
        for (const key of ['pageErrors','externalRequests','cspViolations']) assert.deepEqual(record[key],[],key);
        record.result = 'PASS';
        step('Online and whole-browser stopped-host cold offline placements verified');
    } catch (error) {
        record.failure = {step:record.step, message:String(error.stack || error).slice(0,6000)};
        record.result = 'FAIL';
        const page = context?.pages().at(-1);
        if (page && !page.isClosed()) {
            try {
                record.failure.ui = await page.evaluate(() => Object.fromEntries(
                    ['compiler-readiness','compile-status','compile-details','preview-status','page-label','search-status',
                        'rendered-review-status','rendered-role-search-draft','rendered-caption-image-result'].map(id => {
                        const node = document.querySelector(`[data-testid="${id}"]`);
                        return [id,node ? {text:node.textContent?.slice(0,2000),data:{...node.dataset}} : null];
                    })));
                await page.screenshot({path:path.join(out,'failure.png'),fullPage:true,timeout:5000});
            } catch { /* Preserve the acceptance failure. */ }
        }
        process.exitCode = 1;
    } finally {
        clearTimeout(deadline);
        const cleanup = await Promise.allSettled([context?.close(),stopHost()]);
        if (cleanup.some(task => task.status === 'rejected')) {
            record.cleanupFailure = cleanup.filter(task => task.status === 'rejected').map(task => String(task.reason));
            record.result = 'FAIL';
            process.exitCode = 1;
        }
        flush();
        console.log(JSON.stringify({result:record.result,step:record.step,failure:record.failure}));
    }
}
if (require.main === module) main().catch(error => {console.error(error);process.exitCode = 1;});
module.exports = {fixture, preflight, FIXTURE, EXPECTED};
