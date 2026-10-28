// Lead-coordinated actual published-CSP app only. No build/download/renderer execution here.
// Require explicit PDF_RUNTIME_DIR with manifest.json + files, symlinked to accepted cached E1 bytes.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const http = require('node:http');
const { createHash } = require('node:crypto');
const { chromium } = require('playwright');
const H = require('./review-ui-helpers.cjs');
const { validate } = require('./test-csp.cjs');
function required(name) { assert.ok(process.env[name], `${name} must be explicitly supplied`); return process.env[name]; }
function parseUrl(value) { try { return new URL(value); } catch { throw new Error('Invalid test URL'); } }
function json(value) { try { return JSON.parse(value); } catch { throw new Error('Invalid fixture/selection JSON'); } }
const upstream = parseUrl(required('APP_URL'));
const root = required('PDF_RUNTIME_DIR');
const manifestPath = path.join(root, 'manifest.json');
const manifest = json(fs.readFileSync(manifestPath, 'utf8'));
assert.equal(manifest.version, 1); assert.equal(manifest.resources.length, 2);
const loader = manifest.resources.find(r => r.role === 'loader');
const wasm = manifest.resources.find(r => r.role === 'wasm');
assert.ok(loader && wasm);
assert.equal(loader.sha256, '040bdfac50656b3993f622614b73feaaba31d8253d077ff50f925f316c2f5f6b');
assert.equal(wasm.sha256, 'ea5a9a81d6e200fb59ccaced8ecd510d84999c648acf4a13e4ebbef07b1e2e8b');
const hash = value => createHash('sha256').update(value).digest('hex');
const files = manifest.resources.map(r => {
    assert.match(r.name, /^[A-Za-z0-9._-]{1,200}$/);
    const p = path.join(root, 'files', r.name), bytes = fs.readFileSync(p);
    assert.equal(bytes.length, r.length); assert.equal(hash(bytes), r.sha256); return p;
});
const source = 'PDF resource storage — Привіт 👩🏽\nUnmatched { brace\n';
async function terminal(page, state) {
    await page.waitForFunction(() => document.querySelector('[data-testid=pdf-runtime-status]')?.dataset.busy === 'false', null, { timeout: 120000 });
    assert.equal(await H.$(page, 'pdf-runtime-status').getAttribute('data-state'), state, await H.$(page, 'pdf-runtime-status').textContent());
}
async function unavailable(page) {
    assert.equal(await H.$(page, 'compile').isDisabled(), true);
    assert.match(await H.$(page, 'compiler-readiness').textContent(), /Compiler unavailable/);
    assert.match(await H.$(page, 'renderer-readiness').textContent(), /Preview unavailable/);
}
// TEST-OWNED timing only: cached Blazor getJSDataStreamChunk calls
// selectedFile.slice(offset, end).arrayBuffer(). Hold BEFORE the original read,
// only for the exact selected E1 WASM File and descendant slices. Never replace bytes.
async function armWasmReadBarrier(page) {
    await page.evaluate(({ name, length }) => {
        if (window.__pdfWasmReadBarrier) throw new Error('Previous selected-file barrier was not cleaned up');
        const input = document.querySelector('[data-testid=pdf-runtime-files]');
        const targets = Array.from(input?.files || []).filter(file => file.name === name && file.size === length);
        if (targets.length !== 1) throw new Error('Exactly one actual selected E1 WASM File is required for the read barrier');
        const descendants = new WeakSet([targets[0]]);
        const sliceDescriptor = Object.getOwnPropertyDescriptor(Blob.prototype, 'slice');
        const bufferDescriptor = Object.getOwnPropertyDescriptor(Blob.prototype, 'arrayBuffer');
        const originalSlice = sliceDescriptor.value, originalArrayBuffer = bufferDescriptor.value;
        let release;
        const gate = new Promise(resolve => { release = resolve; });
        const pending = new Set();
        const state = { entered: false, released: false, pending: 0, releaseAndRestore: null };
        const slice = function (...args) {
            const child = originalSlice.apply(this, args);
            if (descendants.has(this)) descendants.add(child);
            return child;
        };
        const arrayBuffer = function (...args) {
            if (!descendants.has(this) || state.released) return originalArrayBuffer.apply(this, args);
            state.entered = true;
            const read = gate.then(() => originalArrayBuffer.apply(this, args));
            pending.add(read); state.pending = pending.size;
            const settled = () => { pending.delete(read); state.pending = pending.size; };
            // Observe both outcomes without changing the actual caller's result.
            read.then(settled, settled);
            return read;
        };
        state.releaseAndRestore = () => {
            state.released = true;
            Object.defineProperty(Blob.prototype, 'slice', sliceDescriptor);
            Object.defineProperty(Blob.prototype, 'arrayBuffer', bufferDescriptor);
            release(); // Pending reads now invoke original arrayBuffer and return its real bytes/error.
        };
        window.__pdfWasmReadBarrier = state;
        try {
            Object.defineProperty(Blob.prototype, 'slice', { ...sliceDescriptor, value: slice });
            Object.defineProperty(Blob.prototype, 'arrayBuffer', { ...bufferDescriptor, value: arrayBuffer });
        } catch (error) {
            state.releaseAndRestore(); delete window.__pdfWasmReadBarrier; throw error;
        }
    }, { name: wasm.name, length: wasm.length });
}
async function enteredWasmReadBarrier(page) {
    try {
        await page.waitForFunction(() => window.__pdfWasmReadBarrier?.entered === true, null, { timeout: 10000 });
    } catch {
        throw new Error('Actual selected E1 WASM chunk read did not enter the test barrier within 10 seconds');
    }
    await page.waitForFunction(() => {
        const state = window.__pdfWasmReadBarrier;
        return state?.entered && !state.released && state.pending > 0
            && document.querySelector('[data-testid=pdf-runtime-status]')?.dataset.busy === 'true'
            && document.querySelector('[data-testid=pdf-runtime-cancel]')?.disabled === false;
    }, null, { timeout: 5000 });
}
async function releaseWasmReadBarrier(page) {
    // Restore prototypes and release even if entry/UI/source assertions failed.
    await page.evaluate(() => window.__pdfWasmReadBarrier?.releaseAndRestore());
    await page.waitForFunction(() => !window.__pdfWasmReadBarrier || window.__pdfWasmReadBarrier.pending === 0,
        null, { timeout: 15000 });
    await page.evaluate(() => { delete window.__pdfWasmReadBarrier; });
}
(async () => {
    const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'pdf-resource-profile-'));
    const proxy = http.createServer((request, response) => {
        let url; try { url = new URL(request.url, upstream); } catch { response.writeHead(400).end(); return; }
        if (url.origin !== upstream.origin) { response.writeHead(400).end(); return; }
        const next = http.request(url, { method: request.method, headers: { ...request.headers, host: upstream.host } }, result => {
            response.writeHead(result.statusCode, result.headers); result.pipe(response);
        });
        next.on('error', () => response.destroy()); request.pipe(next);
    });
    await new Promise(resolve => proxy.listen(0, '127.0.0.1', resolve));
    const base = `http://127.0.0.1:${proxy.address().port}`;
    let context; const externalSuccesses = [], externalAttempts = [], workers = [], vendorRequests = [], errors = [], violations = [];
    async function launch(offline) {
        context = await chromium.launchPersistentContext(profile, { headless: true, offline });
        await context.route('**/*', route => {
            if (parseUrl(route.request().url()).origin === base) return route.continue();
            externalAttempts.push('external attempt'); return route.abort();
        });
        await context.addInitScript(() => {
            window.__pdfSetupCsp = [];
            addEventListener('securitypolicyviolation', e => window.__pdfSetupCsp.push(e.effectiveDirective));
        });
        context.on('response', response => { if (parseUrl(response.url()).origin !== base && response.ok()) externalSuccesses.push('external success'); });
        context.on('request', request => { if (/\/e1\.(js|wasm)|\/private-inputs\//.test(request.url())) vendorRequests.push('vendor URL requested'); });
        context.on('page', page => {
            page.on('worker', worker => { if (/pdf|compile|native|blob:/.test(worker.url())) workers.push('unexpected renderer/native/blob worker'); });
            page.on('pageerror', () => errors.push('pageerror'));
        });
        const page = await context.newPage(); page.setDefaultTimeout(60000); return page;
    }
    try {
        let page = await launch(false);
        const response = await page.goto(base);
        validate(await response.text(), fs.readFileSync(path.join(__dirname, '../docs/examples/static-csp-headers.conf'), 'utf8'));
        await H.ready(page);
        const project = 'PdfResources-' + Date.now();
        await H.reveal(page, page.locator('#new-project')); await page.locator('#new-project').fill(project);
        await page.getByRole('button', { name: 'Create project', exact: true }).click();
        await H.$(page, 'import-input').setInputFiles({ name: 'main.tex', mimeType: 'text/plain', buffer: Buffer.from(source) });
        await H.save(page); const before = await H.head(page, project);
        await H.reveal(page, H.$(page, 'pdf-runtime-recheck')); await unavailable(page);
        const nativeBefore = await page.evaluate(async () => (await import('./js/native-runtime-selection.js')).readSelection());
        await H.$(page, 'pdf-runtime-recheck').click(); await terminal(page, 'Empty');
        await H.$(page, 'pdf-runtime-manifest').setInputFiles({ name: 'manifest.json', mimeType: 'application/json', buffer: Buffer.from('{"version":1,"resources":[],"approved":true}') });
        await H.$(page, 'pdf-runtime-files').setInputFiles(files);
        await H.$(page, 'pdf-runtime-install').click(); await terminal(page, 'InvalidSelection');
        assert.deepEqual(await H.head(page, project), before);
        await H.$(page, 'pdf-runtime-manifest').setInputFiles(manifestPath);
        // One tiny bounded corrupt loader; WASM remains actual E1 bytes, never executed.
        const payloads = manifest.resources.map((r, i) => {
            const bytes = fs.readFileSync(files[i]); if (r.role === 'loader') bytes[0] ^= 1;
            return { name: r.name, mimeType: 'application/octet-stream', buffer: bytes };
        });
        await H.$(page, 'pdf-runtime-files').setInputFiles(payloads);
        await H.$(page, 'pdf-runtime-install').click(); await terminal(page, 'Corrupt');
        assert.deepEqual(await H.head(page, project), before);
        await H.$(page, 'pdf-runtime-files').setInputFiles(files);
        // Controlled actual-file timing, not a slow/fake runtime or a disabled-control force-click.
        try {
            await armWasmReadBarrier(page);
            await H.$(page, 'pdf-runtime-install').click();
            await enteredWasmReadBarrier(page);
            await H.$(page, 'pdf-runtime-cancel').click();
        } finally { await releaseWasmReadBarrier(page); }
        await terminal(page, 'Cancelled');
        assert.deepEqual(await H.head(page, project), before);
        let saved;
        try {
            await armWasmReadBarrier(page);
            await H.$(page, 'pdf-runtime-install').click();
            await enteredWasmReadBarrier(page);
            await H.$(page, 'editor').fill(source + 'Saved independently while PDF resources are installed.\n');
            await H.save(page); saved = await H.head(page, project);
            assert.equal(Buffer.from(saved.files.find(file => file.path === 'main.tex').bytes).toString('utf8'),
                source + 'Saved independently while PDF resources are installed.\n');
            await H.analyze(page); assert.deepEqual(await H.head(page, project), saved);
            // Prove save/analysis occurred during the held real resource read, not after installation.
            await enteredWasmReadBarrier(page);
            await unavailable(page);
        } finally { await releaseWasmReadBarrier(page); }
        await terminal(page, 'Installed');
        const label = await H.$(page, 'pdf-runtime-status').textContent();
        // Truthful NEW default lazy check: installing stores bytes but does NOT
        // make the renderer ready. Preview separately verifies its supported
        // E1 renderer on each explicit request; install alone must NOT execute
        // native renderer code or enable preview.
        assert.equal(label.trim(), 'PDF resources installed. Preview separately verifies the supported E1 renderer when used.');
        await unavailable(page); assert.deepEqual(await H.head(page, project), saved);
        await H.analyze(page); assert.deepEqual(await H.head(page, project), saved);
        assert.equal(await page.evaluate(async () => (await import('./js/native-runtime-selection.js')).readSelection()), nativeBefore);
        const selection = json(await page.evaluate(async () => (await import('./js/native-runtime-selection.js')).readPdfSelection()));
        assert.equal(selection.kind, 'pdf-resources'); assert.equal(selection.resources.length, 2);
        assert.ok(!/approved|credential|absolutePath|document|token/i.test(JSON.stringify(selection)));
        await page.waitForFunction(() => window.__oopOfflineReady === true, null, { timeout: 60000 });
        if (process.env.RESULT_DIR) await page.screenshot({ path: path.join(process.env.RESULT_DIR, 'pdf-resource-setup.png'), fullPage: true });
        violations.push(...await page.evaluate(() => window.__pdfSetupCsp));
        await context.close(); context = null; // Entire Chromium, no retained page/worker.
        await new Promise(resolve => { proxy.close(resolve); proxy.closeAllConnections(); });
        await new Promise((resolve, reject) => {
            const r = http.get(base, response => { response.resume(); reject(new Error('Facing origin still accepts connections')); });
            r.on('error', error => error.code === 'ECONNREFUSED' ? resolve() : reject(error));
            r.setTimeout(2000, () => { r.destroy(); reject(new Error('Connection refusal not proven')); });
        });
        page = await launch(true); // Offline BEFORE first app navigation, same persistent profile.
        await page.goto(base); await H.ready(page);
        if (await page.locator('[data-testid=project-row]').count()) await H.open(page, project);
        await H.reveal(page, H.$(page, 'pdf-runtime-recheck'));
        await H.$(page, 'pdf-runtime-recheck').click(); await terminal(page, 'Installed');
        await unavailable(page); assert.deepEqual(await H.head(page, project), saved);
        assert.equal(await H.$(page, 'editor').inputValue(), source + 'Saved independently while PDF resources are installed.\n');
        assert.equal(await page.evaluate(async () => (await import('./js/native-runtime-selection.js')).readSelection()), nativeBefore);
        violations.push(...await page.evaluate(() => window.__pdfSetupCsp));
        for (const list of [workers, vendorRequests, externalAttempts, externalSuccesses, errors, violations]) assert.deepEqual(list, []);
        process.stdout.write(JSON.stringify({ result: 'PASS', scope: 'opaque-pdf-resource-install-cold-recheck',
            resources: 2, loaderSha256: loader.sha256, wasmSha256: wasm.sha256, manifestSha256: hash(fs.readFileSync(manifestPath)),
            syntheticSourceSha256: hash(Buffer.from(source)), entireBrowserRestart: true, facingOriginRefused: true,
            offlineBeforeNavigation: true, freshByteRecheck: true, compileReadyAfterInstallAlone: false, previewReadyAfterInstallAlone: false,
            controlledSelectedWasmReadTiming: true, cancelledDuringActualRead: true, savedAndAnalyzedDuringHeldRead: true,
            vendorImports: 0, nativeWorkers: 0, externalSuccesses: 0, renderingOrCompatibilityProof: false }) + '\n');
    } finally {
        if (context) await context.close();
        if (proxy.listening) await new Promise(resolve => { proxy.close(resolve); proxy.closeAllConnections(); });
        fs.rmSync(profile, { recursive: true, force: true }); // Only this run's synthetic profile, never inputs/sites/cache.
    }
})().catch(error => { process.stderr.write(error.message + '\n'); process.exitCode = 1; });
