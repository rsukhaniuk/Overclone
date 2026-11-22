// Run the published app with an explicitly supplied local ZIP and file-hash map.
// The original archive is opened read-only. Browser edits are disposable.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawn } = require('node:child_process');
const { once } = require('node:events');
const { createHash } = require('node:crypto');
const { chromium } = require('playwright');
const H = require('./review-ui-helpers.cjs');

const { PUBLISHED_WWWROOT, PROJECT_ZIP, PROJECT_FILE_HASHES } = process.env;
const origin = 'http://127.0.0.1:8128';
const project = 'Real project regression';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');

(async () => {
    assert.ok(PUBLISHED_WWWROOT && PROJECT_ZIP && PROJECT_FILE_HASHES, 'Supply publication, project ZIP and exact file hashes');
    const expected = JSON.parse(fs.readFileSync(PROJECT_FILE_HASHES, 'utf8'));
    const zip = fs.readFileSync(PROJECT_ZIP);
    const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'overclone-real-project-'));
    let browser, page, server;
    const errors = [];
    const by = id => H.$(page, id).first();
    const button = name => page.getByRole('button', { name, exact: true });
    async function launch(offline = false) {
        browser = await chromium.launchPersistentContext(profile, { headless: true, offline, viewport: { width: 1850, height: 1080 } });
        page = await browser.newPage();
        page.setDefaultTimeout(12000);
        page.on('pageerror', error => errors.push(error.message));
        await page.goto(origin);
        await H.ready(page);
    }
    async function stopOrigin() {
        if (!server || server.exitCode !== null) return;
        const exited = once(server, 'exit');
        server.kill();
        await exited;
        server = null;
    }
    async function reveal(id) { await H.reveal(page, by(id)); }
    async function exactFiles() {
        const head = await H.head(page, project);
        assert.deepEqual(Object.fromEntries(head.files.map(file => [file.path, hash(Buffer.from(file.bytes))])), expected);
    }
    async function analyze() {
        await reveal('analysis-profile');
        assert.deepEqual(await by('analysis-profile').locator('option').allTextContents(),
            ['Engine checks', 'Source references', 'FCSC KNU ruleset']);
        await by('analysis-profile').selectOption({ label: 'FCSC KNU ruleset' });
        assert.equal(await by('analysis-work-type').inputValue(), '');
        assert.equal(await by('reference-root').inputValue(), '');
        assert.ok(await by('analyze-now').isDisabled());
        await by('analysis-work-type').selectOption('coursework');
        const [root] = await by('reference-root').selectOption({ label: 'main.tex' });
        await H.analyze(page);
        assert.equal(await H.knuReportVersion(page), '1.7.0');

        const sourceSnapshot = await by('knu-report').getAttribute('data-snapshot-id');
        await page.locator(`[data-testid="analysis-check-storage"][data-saved-snapshot="${sourceSnapshot}"]`).waitFor();
        assert.equal(await by('knu-report').locator('h3').first().textContent(), 'FCSC KNU ruleset');
        assert.equal(await by('knu-edition').textContent(), 'KNU Faculty of Computer Science and Cybernetics · 2025 guidelines');
        assert.equal(await H.$(page, 'thesis-rule').count(), 5);
        assert.equal(await H.$(page, 'thesis-catalog-row').count(), 31);

        await reveal('knu-checks');
        assert.equal(await by('knu-checks').locator('option:checked').textContent(), 'Source and thesis checks');
        await by('knu-checks').selectOption({ label: 'Project margin repair' });
        assert.equal(await by('analysis-work-type').inputValue(), 'coursework');
        assert.equal(await by('reference-root').inputValue(), root);
        await H.analyze(page);
        assert.equal(await H.knuReportVersion(page), '1.8.0');
        const marginSnapshot = await by('knu-report').getAttribute('data-snapshot-id');
        await page.locator(`[data-testid="analysis-check-storage"][data-saved-snapshot="${marginSnapshot}"]`).waitFor();
        assert.notEqual(await by('knu-report').getAttribute('data-snapshot-id'), sourceSnapshot);
        assert.equal(await by('thesis-report').count(), 0);
        assert.equal(await H.$(page, 'knu-rule').count(), 3);

        await by('knu-checks').selectOption({ label: 'Paragraph indent and heading punctuation' });
        await H.analyze(page);
        assert.equal(await H.knuReportVersion(page), '1.0.0');
        const legacySnapshot = await by('knu-report').getAttribute('data-snapshot-id');
        await page.locator(`[data-testid="analysis-check-storage"][data-saved-snapshot="${legacySnapshot}"]`).waitFor();
        await by('knu-checks').selectOption({ label: 'Source and thesis checks' });
        assert.equal(await by('analysis-work-type').inputValue(), '');
        assert.equal(await by('reference-root').inputValue(), '');
        assert.ok(await by('analyze-now').isDisabled());
    }
    async function compile(engine, succeeds = true) {
        await reveal('refresh-compile-tools');
        await page.waitForFunction(() => document.querySelector('[data-testid=compiler-readiness]')?.textContent.includes('compilation available.'), null, { timeout: 120000 });
        await page.getByRole('combobox', { name: 'Compile main file', exact: true }).selectOption({ label: 'main.tex' });
        await page.getByRole('combobox', { name: 'Compile engine', exact: true }).selectOption(engine);
        await H.bibliography(page, 'None');
        await by('compiler-settings').locator(':scope > summary').click();
        const prior = (await by('compile-details').textContent()).match(/Attempt ([0-9a-f]{32}) at worker epoch/)?.[1];
        await by('compile').click();
        await page.waitForFunction(previous => {
            const details = document.querySelector('[data-testid=compile-details]')?.textContent || '';
            const request = details.match(/Attempt ([0-9a-f]{32}) at worker epoch/)?.[1];
            const status = document.querySelector('[data-testid=compile-status]')?.textContent || '';
            return request && request !== previous && /^(Compilation finished|No new PDF)/.test(status)
                && document.querySelector('[data-testid=compile-work-status]')?.textContent.trim() === 'Idle';
        }, prior || null, { timeout: 60000 }).catch(async error => {
            throw Error(`${await by('compile-status').textContent()} (${engine}): ${error.message}`);
        });
        const status = await by('compile-status').textContent();
        assert.match(status, succeeds ? /^Compilation finished/ : /^No new PDF: CompilerFailed/);
        if (succeeds) {
            await page.waitForFunction(() => document.querySelector('[data-testid=canvas-status]')?.textContent === 'Drawn');
            assert.equal(await by('compile-panel').getAttribute('data-is-current'), 'true');
            assert.equal((await by('compiler-log').textContent()).includes('Missing character:'), false);
        }
    }
    async function checkLayout() {
        await reveal('rendered-review-check');
        await by('rendered-review-work-type').selectOption('coursework');
        await by('rendered-review-check').click();
        await page.waitForFunction(() => document.querySelector('[data-testid=rendered-review-status]')?.dataset.busy === 'false', null, { timeout: 130000 });
        const notice = await by('rendered-review-status').textContent();
        assert.equal(await by('rendered-review-report').count(), 1, notice);
        assert.equal(await by('rendered-review-report').getAttribute('data-current'), 'true', notice);
        assert.equal(await by('rendered-review-report').getAttribute('data-complete'), 'true', notice);
        assert.match(await by('rendered-review-coverage').textContent(), /Inspected 47 of 47/);
    }
    async function savedReports(kinds) {
        if (!await by('checks-page').isVisible()) await by('open-checks').click();
        await page.locator('[data-checks-tab="saved"]').click();
        for (const kind of kinds) {
            const row = page.locator(`[data-check-kind="${kind}"]`).first();
            await row.waitFor().catch(async error => {
                console.error('Saved-report state', await page.evaluate(() => ({
                    kinds: [...document.querySelectorAll('[data-check-kind]')].map(element => element.dataset.checkKind),
                    notice: document.querySelector('[data-testid=checks-saved-status]')?.textContent,
                    section: document.querySelector('[data-checks-tab][aria-current]')?.dataset.checksTab,
                })));
                throw error;
            });
        }
        const content = await H.$(page, 'saved-check').allTextContents();
        return content;
    }
    async function chooseFile(name) {
        const file = page.locator('[data-testid=select-file]').filter({ has: page.locator('.file-name').getByText(name, { exact: true }) });
        await H.reveal(page, file);
        await file.click();
        await page.waitForFunction(expected => document.querySelector('.editor-toolbar .source-tab')?.textContent.trim() === expected, name);
    }
    try {
        server = spawn(process.execPath, [path.join(__dirname, 'static-server.cjs'), PUBLISHED_WWWROOT, '8128'], { stdio: ['ignore', 'pipe', 'inherit'] });
        await once(server.stdout, 'data');
        await launch();
        await by('import-zip').setInputFiles(PROJECT_ZIP);
        await reveal('staged-name');
        await by('staged-name').fill(project);
        await by('publish-import').click();
        await by('editor').waitFor();
        await exactFiles();
        await analyze();
        const sourceReports = await savedReports(['source']);
        assert.ok(sourceReports.some(text => text.includes('FCSC KNU ruleset') && text.includes('main.tex')),
            JSON.stringify(await H.$(page, 'saved-check').evaluateAll(rows => rows.map(row => ({
                title: row.querySelector('strong')?.textContent,
                detail: row.querySelector('pre')?.textContent.length,
                hasRoot: row.querySelector('pre')?.textContent.includes('main.tex'),
            })))));
        console.log('PASS FCSC KNU ruleset and project margin repair with the exact imported source');

        await reveal('refresh-compile-tools');
        await by('refresh-compile-tools').click();
        await compile('xelatex');
        const pdfHash = await by('pdf-pane').getAttribute('data-document');
        assert.match(await by('compiler-log').textContent(), /Rerun to get/);
        assert.doesNotMatch((await H.$(page, 'compiler-finding').allTextContents()).join('\n'), /convergence is not established/);
        await checkLayout();
        const saved = await savedReports(['source', 'compiler', 'pdf']);
        assert.ok(saved.some(text => text.includes('FCSC KNU ruleset') && text.includes('main.tex')));
        assert.ok(saved.some(text => text.includes('Rendered layout review') && text.includes('47 of 47')));
        assert.equal(saved.filter(text => text.includes('Rendered layout review')).length, 1);
        assert.equal(await page.locator('[data-testid="source-history-disclosure"], .workspace-tools').count(), 0);
        console.log('PASS XeLaTeX convergence summary and all 47 rendered pages without an idle delay');

        await chooseFile('intro.tex');
        await by('editor').evaluate(input => {
            const at = input.value.indexOf('\\section') + 2;
            input.focus();
            input.setSelectionRange(at, at);
        });
        await reveal('source-go-to-pdf');
        await by('source-go-to-pdf').click();
        const highlight = by('source-pdf-highlight');
        await highlight.scrollIntoViewIfNeeded();
        const box = await highlight.boundingBox();
        await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
        await button('Go to source').click();
        await page.waitForFunction(() => document.querySelector('[data-testid=source-map-status]')?.textContent.startsWith('Selected intro.tex:'));
        console.log('PASS bidirectional XeLaTeX source/PDF navigation');

        await chooseFile('main.tex');
        const editor = by('editor');
        const original = await editor.inputValue();
        const missing = '\\input{missing-overclone-regression}';
        const broken = original.replace('\\input conclusion', `${missing}\n\\input conclusion`);
        assert.notEqual(broken, original);
        await editor.fill(broken);
        await compile('xelatex', false);
        assert.equal(await by('pdf-pane').getAttribute('data-document'), pdfHash);
        await reveal('diagnostic-filter-origin');
        await by('diagnostic-filter-origin').selectOption('Compiler');
        await by('diagnostic-filter-severity').selectOption('Error');
        const missingInput = page.locator('[data-testid="diagnostic-row"][data-rule-id="TEX001"] [data-testid="diagnostic-compiler"]').first();
        await missingInput.click().catch(async error => {
            console.error('Compiler findings', await H.$(page, 'diagnostic-row').evaluateAll(rows => rows.map(row => ({
                rule: row.dataset.ruleId, current: row.dataset.current, navigable: !!row.querySelector('button'),
            }))));
            throw error;
        });
        await editor.waitFor({ state: 'visible' });
        await page.waitForFunction(text => {
            const input = document.querySelector('[data-testid=editor]');
            return input?.value.slice(input.selectionStart, input.selectionEnd) === text;
        }, missing);
        await by('compile-error-jump').click();
        await page.waitForFunction(text => {
            const editor = document.querySelector('[data-testid=editor]');
            return editor?.value.slice(editor.selectionStart, editor.selectionEnd) === text;
        }, missing);
        await editor.fill(original);
        await page.waitForFunction(() => document.querySelector('.workspace-header .status')?.textContent === 'Saved.');
        await exactFiles();
        console.log('PASS exact missing-file jump and previous PDF retention');

        await browser.close(); browser = null;
        await launch();
        await H.open(page, project);
        await compile('lualatex');
        assert.match(await by('compiler-log').textContent(), /\bxstring\.sty\b/);
        await exactFiles();
        console.log('PASS unchanged LuaLaTeX project compile in a fresh browser');

        await page.waitForFunction(() => globalThis.__oopOfflineReady === true && navigator.serviceWorker.controller !== null, null, { timeout: 180000 });
        await browser.close(); browser = null;
        await stopOrigin();
        await launch(true);
        await H.open(page, project);
        await exactFiles();
        const archived = await savedReports(['source', 'compiler', 'pdf']);
        assert.ok(archived.some(text => text.includes('Rendered layout review') && text.includes('47 of 47')));
        const failedCompile = page.locator('[data-check-kind="compiler"]').filter({ hasText: 'TEX001' });
        assert.equal(await failedCompile.locator('.saved-outcome').textContent(), 'Failed');
        await analyze();
        await compile('lualatex');
        await exactFiles();
        assert.deepEqual(errors, []);
        assert.equal(hash(fs.readFileSync(PROJECT_ZIP)), hash(zip));
        console.log('PASS whole-browser cold offline FCSC KNU ruleset, margin repair and LuaLaTeX');
    } finally {
        if (browser) await browser.close();
        await stopOrigin();
        fs.rmSync(profile, { recursive: true, force: true });
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
