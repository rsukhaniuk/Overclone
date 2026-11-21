// Enforced published-page CSP, real Blazor/Rust and local save/export.
// Loopback checks use synthetic transport responses.
// Run only with the lead's shared browser/build coordination; no publication here.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require('playwright');
const H = require('./review-ui-helpers.cjs');
const { validate } = require('./test-csp.cjs');
const base = (process.env.APP_URL || 'http://127.0.0.1:8080').replace(/\/$/, '');
function parseUrl(value) {
    try { return new URL(value); }
    catch { throw new Error('CSP test requires a valid absolute URL'); }
}
const origin = parseUrl(base).origin;
const admissions = [
    'http://localhost:11434/api/chat', 'http://127.0.0.1:11434/api/chat',
];
const blockedConnect = [
    'http://127.0.0.1:11435/api/chat',
    'https://generativelanguage.googleapis.com/v1beta/models/csp-test:generateContent',
    'https://api.anthropic.com/v1/messages',
    'https://www.googleapis.com/drive/v3/files/csp-test?alt=media',
];
const blockedScript = 'https://accounts.google.com/gsi/client';
const blockedFrame = 'https://docs.google.com/picker?csp-test=1';
const probePath = '/__test_only_csp_probe__.js';
// This is a same-origin, routed external SCRIPT: string evaluation here is subject
// to page CSP. Playwright page.evaluate alone can bypass unsafe-eval restrictions.
const probe = `
window.__cspProbe = { inline: 0, eval: 0, evalBlocked: false };
const inline = document.createElement('script');
inline.textContent = 'window.__cspProbe.inline++';
document.body.appendChild(inline);
try { eval('window.__cspProbe.eval++'); } catch (e) { window.__cspProbe.evalBlocked = e instanceof EvalError; }
const image = new Image();
image.src = 'https://unselected.invalid/csp-test.png';
document.body.appendChild(image);
window.__cspProbe.done = true;
`;

(async () => {
    const browser = await chromium.launch({ headless: true });
    const context = await browser.newContext({ acceptDownloads: true }); // CSP bypass remains OFF; real SW allowed.
    const page = await context.newPage();
    page.setDefaultTimeout(30000);
    const errors = [], consoleErrors = [], external = [], routed = [];
    let phase = 'core';
    page.on('pageerror', () => errors.push(phase));
    page.on('console', m => { if (m.type() === 'error') consoleErrors.push(phase); });
    await context.addInitScript(() => {
        window.__cspViolations = [];
        addEventListener('securitypolicyviolation', e => window.__cspViolations.push({
            directive: e.effectiveDirective, blocked: e.blockedURI, disposition: e.disposition,
        }));
    });
    // Fulfill the exact loopback probes synthetically. Record and abort
    // everything else outside the app origin.
    await context.route('**/*', async route => {
        const request = route.request(), url = request.url(), parsed = parseUrl(url);
        if (parsed.origin === origin) {
            if (parsed.pathname === probePath) {
                await route.fulfill({ contentType: 'text/javascript', body: probe });
            } else await route.continue();
            return;
        }
        external.push({ phase, url });
        const headers = { 'access-control-allow-origin': origin, 'access-control-allow-methods': 'GET,POST,OPTIONS',
            'access-control-allow-headers': 'content-type', 'cache-control': 'no-store' };
        if (phase !== 'ollama' || !admissions.includes(url)) {
            await route.abort();
            return;
        }
        routed.push(url);
        await route.fulfill({ headers, contentType: 'application/json', body: '{"syntheticCspAdmission":true}' });
    });
    try {
        const response = await page.goto(base, { waitUntil: 'domcontentloaded' });
        assert.equal(response.status(), 200);
        validate(await response.text(), fs.readFileSync(path.join(__dirname, '../docs/examples/static-csp-headers.conf'), 'utf8'));
        await H.ready(page);
        const project = 'CSP-' + Date.now();
        await H.reveal(page, page.locator('#new-project'));
        await page.locator('#new-project').fill(project);
        await page.getByRole('button', { name: 'Create project', exact: true }).click();
        await H.$(page, 'import-input').setInputFiles({ name: 'main.tex', mimeType: 'text/plain', buffer: Buffer.from('Initial text\n') });
        await H.$(page, 'editor').fill('Привіт 👩🏽\nUnmatched { brace\n');
        await H.save(page);
        const saved = await H.head(page, project);
        assert.equal(Buffer.from(saved.files[0].bytes).toString('utf8'), 'Привіт 👩🏽\nUnmatched { brace\n');
        await H.analyze(page);
        await page.locator('[data-testid=analysis-finding]', { hasText: 'ENGINE-STRUCT-001' }).first().waitFor();
        const downloadPending = page.waitForEvent('download');
        await H.reveal(page, H.$(page, 'download'));
        await H.$(page, 'download').click();
        const download = await downloadPending;
        assert.equal(download.suggestedFilename(), 'main.tex');
        const stream = await download.createReadStream();
        const chunks = [];
        for await (const chunk of stream) chunks.push(chunk);
        assert.equal(Buffer.concat(chunks).toString('utf8'), 'Привіт 👩🏽\nUnmatched { brace\n');
        assert.deepEqual(await H.head(page, project), saved, 'analysis/export preserve saved source');
        assert.deepEqual(external, [], 'startup and local work send no Ollama requests');
        assert.deepEqual(errors, [], 'no uncaught core errors');
        assert.deepEqual(consoleErrors, [], 'no core console errors');
        assert.deepEqual(await page.evaluate(() => window.__cspViolations), [], 'no core CSP violations');

        phase = 'negative';
        await page.evaluate(src => new Promise((resolve, reject) => {
            const s = document.createElement('script'); s.src = src; s.onload = resolve; s.onerror = reject;
            document.body.appendChild(s);
        }), probePath);
        await page.waitForFunction(() => window.__cspProbe?.done && window.__cspViolations.length >= 3);
        const negative = await page.evaluate(() => ({ result: window.__cspProbe, violations: window.__cspViolations }));
        assert.deepEqual(negative.result, { inline: 0, eval: 0, evalBlocked: true, done: true });
        for (const [directive, blocked] of [['script-src-elem', 'inline'], ['script-src', 'eval'], ['img-src', 'https://unselected.invalid/csp-test.png']]) {
            assert.ok(negative.violations.some(v => v.directive === directive && v.blocked === blocked && v.disposition === 'enforce'), directive);
        }
        assert.equal(negative.violations.length, 3, 'only three intentional violations');
        assert.deepEqual(external, [], 'blocked image never reaches transport');

        phase = 'ollama';
        const results = await page.evaluate(async urls => Promise.all(urls.map(async url => {
            const r = await fetch(url, { cache: 'no-store', credentials: 'omit' });
            return r.ok && (await r.json()).syntheticCspAdmission;
        })), admissions);
        assert.deepEqual(results, admissions.map(() => true));
        assert.deepEqual([...new Set(routed)].sort(), [...admissions].sort());
        assert.equal((await page.evaluate(() => window.__cspViolations)).length, 3);

        phase = 'removed-origins';
        const refused = await page.evaluate(async ({ blockedConnect, blockedScript, blockedFrame }) => {
            const script = document.createElement('script');
            script.src = blockedScript;
            document.head.appendChild(script);
            const frame = document.createElement('iframe');
            frame.src = blockedFrame;
            document.body.appendChild(frame);
            return await Promise.all(blockedConnect.map(url => fetch(url)
                .then(() => false, () => true)));
        }, { blockedConnect, blockedScript, blockedFrame });
        assert.deepEqual(refused, blockedConnect.map(() => true));
        await page.waitForFunction(count => window.__cspViolations.length >= count, 3 + blockedConnect.length + 2);
        const denied = (await page.evaluate(() => window.__cspViolations)).slice(3);
        const blocked = (directive, url) => denied.some(v =>
            v.directive === directive && v.disposition === 'enforce' &&
            (v.blocked === url || v.blocked === new URL(url).origin));
        for (const url of blockedConnect) assert.ok(blocked('connect-src', url), url);
        assert.ok(blocked('script-src-elem', blockedScript));
        assert.ok(blocked('frame-src', blockedFrame));
        assert.deepEqual(external.map(r => r.url).sort(), [...admissions].sort(), 'denied requests never reach transport');
        assert.deepEqual(errors, []);
        assert.ok(consoleErrors.every(p => p === 'negative' || p === 'removed-origins'));
        console.log('CSP UI passed: real Blazor/Rust diagnostic, Unicode editing/save/download, loopback Ollama transport, and browser denial of removed cloud origins.');
    } finally {
        await context.close();
        await browser.close();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
