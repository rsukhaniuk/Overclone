// Coordinated published-app acceptance ONLY. No publication/build here.
// Requires explicit APP_URL and AUTHORING_RUNTIME_DIR mounts; never acquires vendor bytes.
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
function parseUrl(value) { try { return new URL(value); } catch { throw new Error('Expected a valid test URL'); } }
function parseJson(value) { try { return JSON.parse(value); } catch { throw new Error('Expected valid fixture/selection JSON'); } }
const upstream = parseUrl(required('APP_URL'));
const root = required('AUTHORING_RUNTIME_DIR');
const pinsPath = path.join(root, 'pins.json');
const pins = parseJson(fs.readFileSync(pinsPath, 'utf8'));
assert.equal(pins.length, 497);
const files = pins.map(p => path.join(root, 'files', p.name));
// Browser selected file paths exist ONLY in this private runner, never product metadata/log output.
for (const p of pins) assert.match(p.name, /^[A-Za-z0-9._-]{1,200}$/);
const source = 'Local resource setup — Привіт 👩🏽\nUnmatched { brace\n';
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
async function terminal(page, state) {
    await page.waitForFunction(() => document.querySelector('[data-testid=runtime-status]')?.dataset.busy === 'false', null, { timeout: 120000 });
    assert.equal(await H.$(page, 'runtime-status').getAttribute('data-state'), state, await H.$(page, 'runtime-status').textContent());
}
async function unavailable(page) {
    assert.equal(await H.$(page, 'compile').isDisabled(), true);
    assert.match(await H.$(page, 'compiler-readiness').textContent(), /Compiler unavailable/);
}
(async () => {
    const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'runtime-setup-profile-'));
    const proxy = http.createServer((request, response) => {
        let url;
        try { url = new URL(request.url, upstream); } catch { response.writeHead(400).end(); return; }
        if (url.origin !== upstream.origin) { response.writeHead(400).end(); return; }
        const next = http.request(url, { method: request.method, headers: { ...request.headers, host: upstream.host } }, result => {
            response.writeHead(result.statusCode, result.headers); result.pipe(response);
        });
        next.on('error', () => response.destroy()); request.pipe(next);
    });
    await new Promise(resolve => proxy.listen(0, '127.0.0.1', resolve));
    const port = proxy.address().port, base = `http://127.0.0.1:${port}`;
    let context; const external = [], nativeWorkers = [], errors = [], violations = [];
    async function launch(offline) {
        context = await chromium.launchPersistentContext(profile, { headless: true, offline });
        await context.route('**/*', route => parseUrl(route.request().url()).origin === base ? route.continue() : route.abort());
        context.on('page', page => {
            page.on('worker', worker => { if (/compile|native|blob:/.test(worker.url())) nativeWorkers.push('unexpected native/blob worker'); });
            page.on('pageerror', () => errors.push('pageerror'));
            page.on('response', response => { if (parseUrl(response.url()).origin !== base && response.ok()) external.push('external response'); });
        });
        const page = await context.newPage(); page.setDefaultTimeout(60000);
        return page;
    }
    try {
        let page = await launch(false);
        const response = await page.goto(base);
        validate(await response.text(), fs.readFileSync(path.join(__dirname, '../docs/examples/static-csp-headers.conf'), 'utf8'));
        await H.ready(page);
        // CSP observation only: does not change policy, bypass it, or inject vendor code.
        await page.evaluate(() => { window.__setupCsp = []; addEventListener('securitypolicyviolation', e => window.__setupCsp.push(e.effectiveDirective)); });
        const project = 'LocalSetup-' + Date.now();
        await H.reveal(page, page.locator('#new-project')); await page.locator('#new-project').fill(project);
        await page.getByRole('button', { name: 'Create project', exact: true }).click();
        await H.$(page, 'import-input').setInputFiles({ name: 'main.tex', mimeType: 'text/plain', buffer: Buffer.from(source) });
        await H.save(page); const before = await H.head(page, project);
        await H.reveal(page, H.$(page, 'runtime-recheck'));
        await unavailable(page); await H.$(page, 'runtime-recheck').click(); await terminal(page, 'Empty');
        // Bounded synthetic invalid input; no native execution or hostile native corpus.
        await H.$(page, 'runtime-pins').setInputFiles({ name: 'pins.json', mimeType: 'application/json', buffer: Buffer.from('[]') });
        await H.$(page, 'runtime-files').setInputFiles(files);
        await H.$(page, 'runtime-install').click(); await terminal(page, 'InvalidSelection');
        assert.deepEqual(await H.head(page, project), before);
        await H.$(page, 'runtime-pins').setInputFiles(pinsPath);
        // Exact-size single-byte corruption. No resource is interpreted as executable code.
        const smallIndex = pins.findIndex(p => p.length > 0 && p.length < 8192);
        assert.ok(smallIndex >= 0); const small = pins[smallIndex];
        const corrupt = fs.readFileSync(files[smallIndex]); corrupt[0] ^= 1;
        const payloads = files.map((file, i) => i === smallIndex ? { name: small.name, mimeType: 'application/octet-stream', buffer: corrupt } : { name: pins[i].name, mimeType: 'application/octet-stream', buffer: fs.readFileSync(file) });
        await H.$(page, 'runtime-files').setInputFiles(payloads);
        await H.$(page, 'runtime-install').click(); await terminal(page, 'Corrupt');
        assert.deepEqual(await H.head(page, project), before);
        await H.$(page, 'runtime-files').setInputFiles(files);
        await H.$(page, 'runtime-install').click();
        await H.$(page, 'runtime-cancel').click(); await terminal(page, 'Cancelled');
        assert.deepEqual(await H.head(page, project), before);
        await H.$(page, 'runtime-install').click();
        // Editing/saving remains independent while selected resources are read/stored.
        await H.$(page, 'editor').fill(source + 'Saved independently during resource installation.\n');
        await H.save(page); const saved = await H.head(page, project);
        await terminal(page, 'Installed');
        const installedLabel = await H.$(page, 'runtime-status').textContent();
        // Truthful NEW default lazy check: installing stores bytes but does NOT
        // make the compiler ready. Explicit Check tools + main.tex + supported
        // engine selection are still required, and install alone must NOT
        // execute native code. The build DOES register the local authoring
        // configuration; it is simply not auto-activated by installation.
        assert.equal(installedLabel.trim(), 'Compiler resources installed. Use Check tools to verify local compilation availability.');
        await unavailable(page); assert.deepEqual(await H.head(page, project), saved);
        await H.analyze(page); assert.deepEqual(await H.head(page, project), saved);
        await page.waitForFunction(() => window.__oopOfflineReady === true, null, { timeout: 60000 });
        const metadata = await page.evaluate(async () => (await import('./js/native-runtime-selection.js')).readSelection());
        const record = parseJson(metadata);
        assert.equal(record.profile, 2); assert.equal(record.resources.length, 497);
        assert.ok(!/approved|token|credential|absolutePath|document/i.test(metadata));
        violations.push(...await page.evaluate(() => window.__setupCsp));
        // Entire Chromium closes. No page/worker is retained across this boundary.
        await context.close(); context = null;
        await new Promise(resolve => { proxy.close(resolve); proxy.closeAllConnections(); });
        await new Promise((resolve, reject) => {
            const r = http.get(base, result => { result.resume(); reject(new Error('Facing proxy still accepts connections')); });
            r.on('error', error => error.code === 'ECONNREFUSED' ? resolve() : reject(error));
            r.setTimeout(2000, () => { r.destroy(); reject(new Error('Refusal not proven')); });
        });
        page = await launch(true); // network disabled BEFORE first navigation
        await page.goto(base); await H.ready(page);
        if (await page.locator('[data-testid=project-row]').count()) await H.open(page, project);
        await H.reveal(page, H.$(page, 'runtime-recheck'));
        await H.$(page, 'runtime-recheck').click(); await terminal(page, 'Installed');
        await unavailable(page); assert.deepEqual(await H.head(page, project), saved);
        assert.equal(await H.$(page, 'editor').inputValue(), source + 'Saved independently during resource installation.\n');
        assert.deepEqual(nativeWorkers, []); assert.deepEqual(external, []); assert.deepEqual(errors, []); assert.deepEqual(violations, []);
        process.stdout.write(JSON.stringify({ result: 'PASS', test: 'local-resource-install-cold-offline-recheck', resources: 497,
            pinsSha256: digest(fs.readFileSync(pinsPath)), syntheticSourceSha256: digest(Buffer.from(source)),
            entireBrowserRestart: true, facingProxyConnectionRefused: true, offlineBeforeNavigation: true,
            compileReadyAfterInstallAlone: false, nativeExecutionDuringInstall: false, nativeWorkers: nativeWorkers.length, externalSuccesses: external.length }) + '\n');
    } finally {
        if (context) await context.close();
        if (proxy.listening) await new Promise(resolve => { proxy.close(resolve); proxy.closeAllConnections(); });
        // Only this uniquely owned synthetic browser profile; never fixture/proof/cache inputs.
        fs.rmSync(profile, { recursive: true, force: true });
    }
})().catch(error => { process.stderr.write(error.message + '\n'); process.exitCode = 1; });
