// Enforced published-page CSP, real Blazor/Rust + local save/export.
// Optional origin checks are finite synthetic transport admissions, NOT real
// OAuth/Picker/provider behavior, authentication, native isolation or cold restart.
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
    'https://generativelanguage.googleapis.com/v1beta/models/csp-test:generateContent',
    'https://api.anthropic.com/v1/messages', 'https://www.googleapis.com/drive/v3/files/csp-test?alt=media',
    'https://accounts.google.com/gsi/status', 'https://apis.google.com/csp-test',
];
const sdkUrls = ['https://accounts.google.com/gsi/client', 'https://apis.google.com/js/api.js'];
const frameUrls = ['https://accounts.google.com/csp-test', 'https://apis.google.com/csp-frame-test', 'https://docs.google.com/picker?csp-test=1'];
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
    // No live cloud/local-service request is allowed by this test runner. Exact
    // synthetic endpoints only; anything else external aborts and fails the test.
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
        if (phase !== 'optional' || ![...admissions, ...sdkUrls, ...frameUrls].includes(url)) {
            await route.abort();
            return;
        }
        routed.push(url);
        if (sdkUrls.includes(url)) {
            await route.fulfill({ headers, contentType: 'text/javascript', body: 'window.__cspFakeSdkLoads = (window.__cspFakeSdkLoads || 0) + 1;' });
        } else if (frameUrls.includes(url)) {
            await route.fulfill({ headers, contentType: 'text/html', body: '<!doctype html><title>Synthetic CSP frame admission only</title>' });
        } else await route.fulfill({ headers, contentType: 'application/json', body: '{"syntheticCspAdmission":true}' });
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
        assert.deepEqual(external, [], 'no optional scripts/network at startup or during local work');
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

        phase = 'optional';
        // Deliberate test-only admissions, not product Connect/Send UI. No key or
        // user document is sent. Existing AI/Drive suites cover workflow gating.
        const results = await page.evaluate(async urls => Promise.all(urls.map(async url => {
            const r = await fetch(url, { cache: 'no-store', credentials: 'omit' });
            return r.ok && (await r.json()).syntheticCspAdmission;
        })), admissions);
        assert.deepEqual(results, admissions.map(() => true));
        await page.evaluate(async urls => {
            for (const url of urls) await new Promise((resolve, reject) => {
                const s = document.createElement('script'); s.src = url; s.onload = resolve; s.onerror = reject;
                document.head.appendChild(s);
            });
        }, sdkUrls);
        assert.equal(await page.evaluate(() => window.__cspFakeSdkLoads), 2);
        await page.evaluate(async urls => {
            for (const url of urls) await new Promise((resolve, reject) => {
                const frame = document.createElement('iframe'); frame.src = url; frame.onload = resolve; frame.onerror = reject;
                document.body.appendChild(frame);
            });
        }, frameUrls);
        assert.deepEqual([...new Set(routed)].sort(), [...admissions, ...sdkUrls, ...frameUrls].sort());
        assert.equal((await page.evaluate(() => window.__cspViolations)).length, 3, 'no optional-admission CSP violations');
        assert.deepEqual(errors, []);
        assert.ok(consoleErrors.every(p => p === 'negative'), 'no optional/core console errors');
        console.log('CSP UI passed: enforced published meta; real Blazor/Rust diagnostic; Unicode editing/save/download; inline/eval/image blocked; finite synthetic optional origin/script/frame admissions. NOT live auth/native/cold-offline acceptance.');
    } finally {
        await context.close();
        await browser.close();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
