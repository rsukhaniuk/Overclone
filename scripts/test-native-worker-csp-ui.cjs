// Prepared combined compile+PDF classic entries under actual published app CSP.
// Lead coordinates execution; this task only runs Docker Node/syntax contracts.
// NO vendor/native boot messages, loader bytes, WASM or compilation are sent.
// Deterministic activating/controller-change races are separately MODELED in
// tests/browser/compile-worker-admission.test.cjs, not fake browser successes.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const http = require('node:http');
const crypto = require('node:crypto');
const { chromium } = require('playwright');
const H = require('./review-ui-helpers.cjs');
const ROOT = process.env.PUBLISHED_WWWROOT;
assert.ok(ROOT, 'PUBLISHED_WWWROOT must name the coordinated complete RAM publication');
const OUT = process.env.NATIVE_ENTRY_RESULTS || path.join(os.tmpdir(), 'native-worker-entry-results');
fs.mkdirSync(OUT, { recursive: true });
const BASE = 'http://127.0.0.1:8766', SOURCE = '/js/compile-port-worker.js', PDF_SOURCE = '/js/pdf-port-worker.js';
const POLICY = "default-src 'none'; script-src blob: 'wasm-unsafe-eval'; connect-src 'none'; worker-src 'none'; object-src 'none'";
const DENY = "default-src 'none'; script-src 'none'; connect-src 'none'; worker-src 'none'; object-src 'none'";
const sha = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
const manifestText = fs.readFileSync(path.join(ROOT, 'service-worker-assets.js'), 'utf8');
let manifest;
try { manifest = JSON.parse(manifestText.match(/self\.assetsManifest\s*=\s*(\{[\s\S]*\})\s*;/)[1]); }
catch { throw new Error('published SDK manifest must be valid generated JSON'); }
const sourceBytes = fs.readFileSync(path.join(ROOT, SOURCE));
const digest = sha(sourceBytes);
for (const asset of manifest.assets) {
    assert.ok(!asset.url.startsWith('/') && !asset.url.split('/').includes('..'));
    assert.equal('sha256-' + crypto.createHash('sha256').update(fs.readFileSync(path.join(ROOT, asset.url))).digest('base64'), asset.hash, asset.url);
}
const sourceSri = manifest.assets.find(a => a.url === SOURCE.slice(1))?.hash;
assert.ok(sourceSri);
const pdfBytes = fs.readFileSync(path.join(ROOT, PDF_SOURCE));
const pdfSri = manifest.assets.find(a => a.url === PDF_SOURCE.slice(1))?.hash;
assert.ok(pdfSri);
const roles = { compile: { source: SOURCE, digest, sri: sourceSri, bytes: sourceBytes.length },
    pdf: { source: PDF_SOURCE, digest: sha(pdfBytes), sri: pdfSri, bytes: pdfBytes.length } };
const record = { passed: false, scope: 'actual-published-worker-entry-only-no-native-boot', sdkRelease: manifest.version,
    sdkAssets: manifest.assets.length, allSdkAssetHashesVerified: true, sourceSha256: digest, sourceBytes: sourceBytes.length,
    policy: POLICY, modeledOnly: ['activating controller wait/timeout', 'controller identity change', 'MessageChannel correlation/timeout/disposal', 'constructor/URL/timer lifecycle'],
    roles, phases: {}, responses: [], nativeEntryConstructions: [], consoleErrors: [], pageErrors: [], cleanup: {}, nativeBootMessages: 0, nativeExecution: false };
let server = null, context = null, phase = 'setup';
const serverRequests = [], responseTasks = [];
const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'owned-native-entry-profile-'));
function parseUrl(value) { try { return new URL(value, BASE); } catch { throw new Error('invalid fixture URL'); } }
function save() { fs.writeFileSync(path.join(OUT, 'receipt.json'), JSON.stringify(record, null, 2) + '\n'); }
async function responseEvidence(response, capturedPhase = phase) {
    const headers = await response.allHeaders();
    const entry = { phase: capturedPhase, url: response.url(), status: response.status(), fromServiceWorker: response.fromServiceWorker(),
        policy: headers['content-security-policy'] || null, protocol: headers['x-oop-worker-protocol'] || null,
        kind: headers['x-oop-worker-kind'] || null, release: headers['x-oop-worker-release'] || null,
        digest: headers['x-oop-worker-sha256'] || null, resourceType: response.request().resourceType() };
    if (response.status() === 200) { const body = await response.body(); entry.bodySha256 = sha(body); entry.bytes = body.length; }
    return entry;
}
function responseGate(entry) {
    return context.waitForEvent('response', { timeout: 10000, predicate: response =>
        response.url() === BASE + entry && ['script', 'worker'].includes(response.request().resourceType())
    }).then(response => ({ response }), error => ({ error }));
}
function processes() {
    const result = [];
    for (const id of fs.readdirSync('/proc').filter(n => /^\d+$/.test(n))) {
        try {
            const exe = path.basename(fs.readlinkSync('/proc/' + id + '/exe'));
            if (!['chrome', 'headless_shell', 'chrome-headless-shell'].includes(exe)) continue;
            const stat = fs.readFileSync('/proc/' + id + '/stat', 'utf8').split(') ')[1].split(' ');
            if (stat[0] !== 'Z') result.push({ pid: Number(id), startTicks: stat[19], exe });
        } catch { /* process exited */ }
    }
    return result;
}
async function closeBrowser() {
    const browser = context.browser(), before = processes();
    assert.ok(before.length >= 2, 'process census observes actual browser/renderers');
    await context.close(); context = null;
    let after;
    for (let i = 0; i < 20; i++) { after = processes(); if (!after.length) break; await new Promise(r => setTimeout(r, 50)); }
    assert.equal(browser.isConnected(), false); assert.deepEqual(after, []);
    return { disconnected: true, before, after };
}
async function startBrowser(offline) {
    context = await chromium.launchPersistentContext(profile, { headless: true, timeout: 20000 });
    record.browser = context.browser().version();
    assert.ok(context.pages().every(p => p.url() === 'about:blank'));
    if (offline) {
        await context.setOffline(true);
        record.phases.offlineBeforeNavigation = context.pages().map(p => p.url());
    }
    context.on('response', response => {
        const url = response.url();
        if (!url.includes('/__oop/native-worker') && !url.endsWith(SOURCE) && !url.endsWith(PDF_SOURCE)) return;
        const capturedPhase = phase;
        if (responseTasks.length >= 64) throw new Error('response cap');
        const observed = responseEvidence(response, capturedPhase).then(entry => { record.responses.push(entry); });
        observed.catch(() => {}); responseTasks.push(observed);
    });
    context.on('page', page => {
        page.on('worker', worker => {
            if (worker.url().includes('/__oop/native-worker/')) {
                if (record.nativeEntryConstructions.length >= 64) throw new Error('worker observation cap');
                record.nativeEntryConstructions.push({ phase, url: worker.url() });
            }
        });
        page.on('console', message => {
            if (message.type() === 'error' && record.consoleErrors.length < 64)
                record.consoleErrors.push({ phase, text: message.text().slice(0, 768) });
        });
        page.on('pageerror', error => {
            if (record.pageErrors.length < 32) record.pageErrors.push({ phase, message: error.message.slice(0, 512) });
        });
    });
    await context.addInitScript(() => {
        window.__entryCspEvents = [];
        addEventListener('securitypolicyviolation', e => window.__entryCspEvents.push({ directive: e.effectiveDirective, blocked: e.blockedURI, disposition: e.disposition }));
    });
    return context.newPage();
}
async function ready(page) {
    page.setDefaultTimeout(30000);
    const response = await page.goto(BASE, { waitUntil: 'load', timeout: 45000 });
    await H.ready(page);
    await page.waitForFunction(() => navigator.serviceWorker.controller?.state === 'activated', null, { timeout: 60000 });
    return response;
}
async function contract(page, changes = {}) {
    return page.evaluate(changes => new Promise((resolve, reject) => {
        const channel = new MessageChannel(), requestId = 'c'.repeat(32);
        const done = () => { clearTimeout(timer); channel.port1.close(); channel.port2.close(); };
        const timer = setTimeout(() => { done(); reject(new Error('real controller contract timeout')); }, 5000);
        channel.port1.onmessage = event => { done(); resolve(event.data); };
        channel.port1.onmessageerror = () => { done(); reject(new Error('real controller message error')); };
        try { navigator.serviceWorker.controller.postMessage({ type: 'native-worker-entry', protocol: 1, kind: 'compile', requestId, ...changes }, [channel.port2]); }
        catch { done(); reject(new Error('real controller message refused')); }
    }), changes);
}
async function admitAndInspect(page, label, kind = 'compile') {
    const role = roles[kind];
    try {
        await page.evaluate(async kind => {
            if (kind === 'compile') {
                const bridge = await import('./js/compile-port-bridge.js'); window.__entryBridge = bridge; await bridge.provision();
            } else {
                const { createRestrictedWorkerAdmission } = await import('./js/restricted-worker-admission.js');
                window.__pdfEntryAdmission = createRestrictedWorkerAdmission('pdf');
                await window.__pdfEntryAdmission.provision();
            }
        }, kind);
        const reply = await contract(page, { kind });
        assert.equal(reply.ok, true); assert.equal(reply.kind, kind); assert.equal(reply.release, manifest.version);
        assert.equal(reply.digest, role.digest); assert.equal(reply.source, role.source); assert.equal(reply.sri, role.sri); assert.equal(reply.policy, POLICY);
        const waiting = page.waitForEvent('worker', { timeout: 10000 }).then(worker => ({ worker }), error => ({ error }));
        const gate = responseGate(reply.entry);
        try {
            await page.evaluate(entry => { window.__entryOnlyWorker = new Worker(entry); }, reply.entry);
            const created = await waiting; if (created.error) throw created.error;
            const inspected = await created.worker.evaluate(() => ({ onmessageInstalled: typeof self.onmessage === 'function', url: self.location.href }));
            assert.equal(inspected.onmessageInstalled, true); assert.equal(inspected.url, BASE + reply.entry);
            // Read-only handler/URL inspection. NO PDF inspect/boot/runtime bytes.
            const fresh = await gate; if (fresh.error) throw fresh.error;
            const actual = await responseEvidence(fresh.response, label);
            assert.equal(actual.status, 200); assert.equal(actual.fromServiceWorker, true); assert.equal(actual.policy, POLICY);
            assert.equal(actual.protocol, '1'); assert.equal(actual.kind, kind); assert.equal(actual.release, manifest.version);
            assert.equal(actual.digest, role.digest); assert.equal(actual.bodySha256, role.digest); assert.equal(actual.bytes, role.bytes);
            if (kind === 'compile') {
                const observation = await page.evaluate(() => window.__entryBridge.observations());
                assert.equal(observation.started, 0); assert.equal(observation.trace.length, 0);
            }
            return { contract: reply, inspected, response: actual, admission: kind === 'compile' ? 'actual-compile-bridge' : 'actual-shared-pdf-helper', nativeBootSent: false };
        } finally {
            await page.evaluate(() => { window.__entryOnlyWorker?.terminate(); delete window.__entryOnlyWorker; }).catch(() => {});
            await waiting; await gate;
        }
    } finally {
        if (kind === 'pdf') await page.evaluate(() => { window.__pdfEntryAdmission?.dispose(); delete window.__pdfEntryAdmission; }).catch(() => {});
    }
}
async function portIsolation(page) {
    const before = record.nativeEntryConstructions.length;
    const result = await page.evaluate(async () => {
        const { createRestrictedWorkerAdmission } = await import('./js/restricted-worker-admission.js');
        const { createPdfPort } = await import('./js/pdf-port-bridge.js');
        const first = createRestrictedWorkerAdmission('pdf'), second = createRestrictedWorkerAdmission('pdf'), idle = createPdfPort();
        try {
            const a = await first.provision(), b = await second.provision();
            first.dispose(); idle.dispose();
            const pdfStillAdmitted = second.check(b) === b;
            const compileStillAdmitted = await window.__entryBridge.provision();
            return { sameBinding: a === b, pdfStillAdmitted, compileStillAdmitted };
        } finally { first.dispose(); second.dispose(); idle.dispose(); }
    });
    assert.deepEqual(result, { sameBinding: true, pdfStillAdmitted: true, compileStillAdmitted: true });
    assert.equal(record.nativeEntryConstructions.length, before, 'idle port setup/disposal must construct no native workers');
    return result;
}
async function pageBlobDenied(page) {
    const result = await page.evaluate(async () => {
        const before = window.__entryCspEvents.length;
        delete self.__benignNativeEntryMarker;
        const url = URL.createObjectURL(new Blob(['self.__benignNativeEntryMarker=1;'], { type: 'text/javascript' }));
        const script = document.createElement('script'); script.src = url;
        try {
            const outcome = await new Promise(resolve => {
                const timer = setTimeout(() => resolve('timeout'), 2000);
                script.onload = () => { clearTimeout(timer); resolve('loaded'); };
                script.onerror = () => { clearTimeout(timer); resolve('blocked'); };
                document.body.append(script);
            });
            for (let i = 0; i < 20 && window.__entryCspEvents.length === before; i++) await new Promise(r => setTimeout(r, 25));
            return { outcome, executed: self.__benignNativeEntryMarker === 1, events: window.__entryCspEvents.slice(before) };
        } finally { script.remove(); URL.revokeObjectURL(url); }
    });
    assert.equal(result.outcome, 'blocked'); assert.equal(result.executed, false);
    assert.ok(result.events.some(e => e.directive === 'script-src-elem' && e.blocked === 'blob' && e.disposition === 'enforce'));
    return result;
}
async function rejectedWorker(page, entry) {
    // Arm before construction: worker.onerror can precede the async observation
    // task, and the same source URL may already have a valid integrity-fetch200.
    // Observe rejection immediately; drain the finite gate on EVERY exit path.
    const gate = responseGate(entry);
    try {
        const result = await page.evaluate(entry => new Promise(resolve => {
            const worker = new Worker(entry);
            const timer = setTimeout(() => { worker.terminate(); resolve('timeout'); }, 3000);
            worker.onerror = e => { e.preventDefault(); clearTimeout(timer); worker.terminate(); resolve('rejected'); };
            // No boot/native data; failure must come from refused entry response.
        }), entry);
        assert.equal(result, 'rejected');
        const fresh = await gate;
        if (fresh.error) throw fresh.error;
        const headers = await fresh.response.allHeaders();
        const response = { phase, url: fresh.response.url(), status: fresh.response.status(),
            fromServiceWorker: fresh.response.fromServiceWorker(), policy: headers['content-security-policy'] || null,
            resourceType: fresh.response.request().resourceType(), freshResponseGate: true };
        assert.equal(response.status, 403); assert.equal(response.fromServiceWorker, true); assert.equal(response.policy, DENY);
        await Promise.all(responseTasks); // supplementary log only, never response selection
        return response;
    } finally { await gate; }
}
async function cacheFault(label, key, corrupt, admittedEntry, kind = 'compile') {
    const page = await context.newPage(); await ready(page);
    try {
        await page.evaluate(async ({ release, key, corrupt }) => {
            const cache = await caches.open('oop-workspace-release-' + release);
            window.__savedEntry = await cache.match(key);
            if (!window.__savedEntry) throw new Error('fault requires actual existing release entry');
            if (corrupt) await cache.put(key, new Response('owned synthetic corrupt source', { headers: { 'Content-Type': 'text/javascript' } }));
            else await cache.delete(key);
        }, { release: manifest.version, key, corrupt });
        const constructions = record.nativeEntryConstructions.length;
        const result = await page.evaluate(async kind => {
            if (kind === 'compile') {
                const bridge = await import('./js/compile-port-bridge.js');
                let rejected = false; try { await bridge.provision(); } catch { rejected = true; }
                const observation = bridge.observations(); bridge.dispose(); return { rejected, observation };
            }
            const { createRestrictedWorkerAdmission } = await import('./js/restricted-worker-admission.js');
            const port = createRestrictedWorkerAdmission('pdf');
            try { await port.provision(); return { rejected: false }; }
            catch { return { rejected: true }; }
            finally { port.dispose(); }
        }, kind);
        assert.equal(result.rejected, true);
        if (kind === 'compile') { assert.equal(result.observation.started, 0); assert.equal(result.observation.trace.length, 0); }
        assert.equal(record.nativeEntryConstructions.length, constructions, 'failed admission constructs no native Worker');
        const denied = await rejectedWorker(page, admittedEntry);
        record.phases[label] = { ...result, denied };
    } finally {
        await page.evaluate(async ({ release, key }) => { const cache = await caches.open('oop-workspace-release-' + release); await cache.put(key, window.__savedEntry); delete window.__savedEntry; }, { release: manifest.version, key });
        await page.close();
    }
}
const deadline = setTimeout(() => { record.failure = { phase, message: 'browser acceptance deadline300s' }; save(); process.exit(124); }, 300000);
(async () => {
    try {
        const types = { '.js': 'text/javascript', '.html': 'text/html', '.css': 'text/css', '.wasm': 'application/wasm', '.json': 'application/json', '.png': 'image/png', '.dat': 'application/octet-stream' };
        server = http.createServer((request, response) => {
            const url = parseUrl(request.url), name = url.pathname === '/' ? '/index.html' : url.pathname;
            if (serverRequests.length >= 2048) throw new Error('request cap');
            serverRequests.push({ phase, path: name });
            const file = path.resolve(ROOT, '.' + name);
            if (!file.startsWith(path.resolve(ROOT) + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) {
                response.writeHead(404, { 'Content-Security-Policy': DENY }); response.end('not found'); return;
            }
            response.writeHead(200, { 'Content-Type': types[path.extname(file)] || 'application/octet-stream', 'Cache-Control': 'no-store' });
            fs.createReadStream(file).pipe(response);
        });
        await new Promise((resolve, reject) => { server.once('error', reject); server.listen(8766, '127.0.0.1', resolve); });
        phase = 'online'; let page = await startBrowser(false);
        // Real missing controller, explicitly modeled context blocks registration;
        // no arbitrary fake success controller messages are used by this script.
        const noSW = await context.browser().newContext({ serviceWorkers: 'block' });
        const noPage = await noSW.newPage(); await noPage.goto(BASE); await H.ready(noPage);
        record.phases.noController = await noPage.evaluate(async () => {
            const bridge = await import('./js/compile-port-bridge.js'); let rejected = false;
            try { await bridge.provision(); } catch { rejected = true; }
            const observation = bridge.observations(); bridge.dispose();
            const { createRestrictedWorkerAdmission } = await import('./js/restricted-worker-admission.js');
            const pdf = createRestrictedWorkerAdmission('pdf'); let pdfRejected = false;
            try { await pdf.provision(); } catch { pdfRejected = true; } finally { pdf.dispose(); }
            return { rejected, pdfRejected, observation, controller: !!navigator.serviceWorker.controller };
        });
        assert.equal(record.phases.noController.rejected, true); assert.equal(record.phases.noController.controller, false);
        assert.equal(record.phases.noController.observation.started, 0); assert.equal(record.phases.noController.pdfRejected, true); await noSW.close();
        await ready(page);
        const project = 'Native-entry-' + Date.now(); record.project = project;
        await H.reveal(page, page.locator('#new-project')); await page.locator('#new-project').fill(project);
        await page.getByRole('button', { name: 'Create project', exact: true }).click();
        await H.$(page, 'import-input').setInputFiles({ name: 'main.tex', mimeType: 'text/plain', buffer: Buffer.from('Привіт🙂 source {\n') });
        await H.save(page); await H.analyze(page);
        await H.$(page, 'editor').fill('Привіт🙂 saved {\n'); await H.save(page); await H.analyze(page);
        record.phases.localSource = await H.head(page, project);
        assert.equal(Buffer.from(record.phases.localSource.files[0].bytes).toString('utf8'), 'Привіт🙂 saved {\n');
        assert.deepEqual(await page.evaluate(() => window.__entryCspEvents), [], 'no core CSP violations');
        assert.deepEqual(record.consoleErrors, [], 'no core console errors');
        assert.deepEqual(record.pageErrors, [], 'no core page errors');
        record.phases.online = await admitAndInspect(page, phase);
        record.phases.onlinePdf = await admitAndInspect(page, phase, 'pdf');
        record.phases.onlinePortIsolation = await portIsolation(page);
        assert.deepEqual(await H.head(page, project), record.phases.localSource, 'entry setup never mutates source');
        record.phases.onlinePageDenial = await pageBlobDenied(page);
        const entry = record.phases.online.contract.entry, pdfEntry = record.phases.onlinePdf.contract.entry;
        for (const changes of [{ protocol: 2 }, { kind: 'unknown' }, { extra: true }]) assert.equal((await contract(page, changes)).ok, false);
        record.phases.wrongDestination = await page.evaluate(async entry => { const r = await fetch(entry); return { status: r.status, policy: r.headers.get('Content-Security-Policy') }; }, entry);
        assert.equal(record.phases.wrongDestination.status, 403); assert.equal(record.phases.wrongDestination.policy, DENY);
        record.phases.wrongProtocol = await rejectedWorker(page, entry.replace('protocol=1', 'protocol=2'));
        record.phases.wrongRelease = await rejectedWorker(page, entry.replace(encodeURIComponent(manifest.version), 'wrong-release'));
        record.phases.directSource = await rejectedWorker(page, SOURCE);
        await cacheFault('corruptSource', SOURCE, true, entry);
        await cacheFault('missingSource', SOURCE, false, entry);
        await cacheFault('incompleteRelease', '/app.css', false, entry);
        record.phases.pdfWrongDestination = await page.evaluate(async entry => { const r = await fetch(entry); return { status: r.status, policy: r.headers.get('Content-Security-Policy') }; }, pdfEntry);
        assert.equal(record.phases.pdfWrongDestination.status, 403); assert.equal(record.phases.pdfWrongDestination.policy, DENY);
        record.phases.pdfWrongProtocol = await rejectedWorker(page, pdfEntry.replace('protocol=1', 'protocol=2'));
        record.phases.pdfWrongRelease = await rejectedWorker(page, pdfEntry.replace(encodeURIComponent(manifest.version), 'wrong-release'));
        record.phases.pdfWrongRole = await rejectedWorker(page, pdfEntry.replace('kind=pdf', 'kind=compile'));
        record.phases.compileWrongRole = await rejectedWorker(page, entry.replace('kind=compile', 'kind=pdf'));
        record.phases.directPdfSource = await rejectedWorker(page, PDF_SOURCE);
        await cacheFault('corruptPdfSource', PDF_SOURCE, true, pdfEntry, 'pdf');
        await cacheFault('missingPdfSource', PDF_SOURCE, false, pdfEntry, 'pdf');
        await cacheFault('incompletePdfRelease', '/app.css', false, pdfEntry, 'pdf');
        await page.evaluate(() => window.__entryBridge.dispose());
        await Promise.all(responseTasks); save();
        record.phases.entireBrowserClosed = await closeBrowser();
        await new Promise(resolve => server.close(resolve)); server = null;
        record.phases.originStopped = await new Promise((resolve, reject) => {
            const req = http.get(BASE, () => reject(new Error('origin remains available')));
            req.setTimeout(1500, () => req.destroy(new Error('refusal timeout')));
            req.on('error', error => error.code === 'ECONNREFUSED' ? resolve({ code: error.code, address: error.address, port: error.port }) : reject(error));
        });
        phase = 'cold-offline'; page = await startBrowser(true);
        const offlineDocument = await ready(page); assert.equal(offlineDocument.fromServiceWorker(), true);
        await H.open(page, project); await H.analyze(page);
        assert.deepEqual(await H.head(page, project), record.phases.localSource);
        await H.$(page, 'editor').fill('Привіт🙂 offline {\n'); await H.save(page); await H.analyze(page);
        record.phases.offlineLocalSource = await H.head(page, project);
        assert.equal(Buffer.from(record.phases.offlineLocalSource.files[0].bytes).toString('utf8'), 'Привіт🙂 offline {\n');
        record.phases.offline = await admitAndInspect(page, phase);
        record.phases.offlinePdf = await admitAndInspect(page, phase, 'pdf');
        record.phases.offlinePortIsolation = await portIsolation(page);
        assert.deepEqual(await H.head(page, project), record.phases.offlineLocalSource, 'offline entry setup never mutates source');
        assert.deepEqual(await page.evaluate(() => window.__entryCspEvents), [], 'no cold core CSP violations');
        assert.ok(record.consoleErrors.every(e => e.phase !== 'cold-offline'), 'no cold core console errors');
        assert.deepEqual(record.pageErrors, []);
        record.phases.offlinePageDenial = await pageBlobDenied(page);
        await page.evaluate(() => window.__entryBridge.dispose());
        record.phases.offlineDocumentFromSW = true;
        assert.ok(!serverRequests.some(r => r.path.startsWith('/__oop/native-worker')), 'virtual entry never has a network fallback');
        record.passed = true;
    } catch (error) { record.failure = { phase, message: error.message.slice(0, 2048), stack: error.stack?.slice(0, 4096) }; process.exitCode = 1; }
    finally {
        try {
            if (context) record.cleanup.browser = await closeBrowser();
            if (server) { await new Promise(resolve => server.close(resolve)); server = null; }
            fs.rmSync(profile, { recursive: true, force: true });
            record.cleanup.profileRemoved = !fs.existsSync(profile); record.cleanup.serverClosed = true;
        } catch (error) { record.cleanup.failure = error.message; record.passed = false; process.exitCode = 1; }
        await Promise.allSettled(responseTasks); clearTimeout(deadline); save();
        console.log(JSON.stringify({ passed: record.passed, sdkRelease: manifest.version, sdkAssets: manifest.assets.length, sourceSha256: digest,
            nativeBootSent: false, nativeExecution: false, coldOffline: !!record.phases.offline && !!record.phases.offlinePdf, failure: record.failure || null, cleanup: record.cleanup }));
    }
})();
