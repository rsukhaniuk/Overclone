// Published editor regression with a local synthetic Ollama reply.
const assert = require('node:assert/strict');
const { chromium } = require('playwright');
const H = require('./review-ui-helpers.cjs');
const { $ } = H;
const origin = process.env.APP_URL || 'http://127.0.0.1:8080';
const withBom = text => Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), Buffer.from(text)]);

(async () => {
    const browser = await chromium.launch();
    try {
        const page = await browser.newPage({ serviceWorkers: 'block', viewport: { width: 1440, height: 900 } });
        const errors = [], requests = [];
        page.on('pageerror', error => errors.push(error.message));
        const source = 'Привіт🙂\r\nповтор повтор\r\\setlength{\\parindent}{ 10 mm } \\section{Методи.}\r\n';
        const passage = 'Привіт🙂\r\nповтор повтор';
        const response = JSON.stringify({ findings: [
            { quote: 'Привіт🙂', comment: 'Use a formal greeting.', replacement: 'Вітаю🙂' },
            { quote: 'повтор', comment: 'Repeated quote', replacement: 'інше' },
            { quote: 'Методи', comment: 'Outside the selected passage', replacement: null },
        ] });
        await page.route('http://localhost:11434/api/chat', async route => {
            const headers = { 'access-control-allow-origin': origin, 'access-control-allow-headers': 'content-type' };
            if (route.request().method() === 'OPTIONS') return route.fulfill({ status: 204, headers });
            const data = route.request().postDataJSON();
            requests.push(data);
            await route.fulfill({ headers, json: { done: true, message: { content: response } } });
        });
        await page.goto(origin);
        await H.ready(page);
        const project = await H.create(page, withBom(source));
        const editor = $(page, 'editor');
        await $(page, 'import-input').setInputFiles({ name: 'notes.tex', mimeType: 'text/plain', buffer: Buffer.from('% notes\n') });
        await page.locator('.source-tab').filter({ hasText: 'notes.tex' }).waitFor();
        await H.save(page);
        await H.selectKnu(page);
        await $(page, 'analysis-check-storage').filter({ hasText: 'Report saved in this browser.' }).waitFor();
        assert.equal(await page.locator('.source-mark-check').count(), 0, 'findings belong to their own files');
        const violation = $(page, 'knu-finding-link').filter({ hasText: /indent|10 mm/i }).first();
        await violation.click();
        await page.locator('.source-tab').filter({ hasText: 'main.tex' }).waitFor();
        await page.locator('.source-mark-check').first().waitFor();
        assert.ok((await page.locator('.source-mark-check').allTextContents()).join('').includes('10 mm'));
        assert.equal(await $(page, 'source-highlight-layer').getAttribute('data-ready'), 'true');
        assert.equal(await page.locator('.source-mark-check').first().evaluate(mark => getComputedStyle(mark).textDecorationLine), 'underline');
        assert.match(await editor.evaluate(input => input.value.slice(input.selectionStart, input.selectionEnd)), /10 mm/);
        assert.equal(await $(page, 'open-checks').getAttribute('aria-pressed'), 'true');
        assert.equal(await page.getByText('Rule evidence and coverage').locator('..').getAttribute('open'), null);
        await page.locator('.source-gutter-marker').first().click();
        const marked = $(page, 'active-check-finding');
        assert.equal(await $(page, 'marked-check-finding').count(), 2);
        assert.match(await marked.innerText(), /Guideline §3.1/);
        assert.equal(await marked.locator('[data-testid="marked-check-finding"]').filter({ hasText: /period/ }).getByRole('button', { name: 'Preview fix' }).count(), 0);
        await marked.getByRole('button', { name: 'Preview fix' }).click();
        await $(page, 'review-preview').waitFor();
        assert.equal((await $(page, 'review-replacement').textContent()).trim(), '1.27cm');
        await H.assertBytes(page, project, withBom(source));
        await H.accept(page);
        await H.assertBytes(page, project, withBom(source.replace('10 mm', '1.27cm')));
        await H.undo(page);
        await H.assertBytes(page, project, withBom(source));
        await page.getByRole('button', { name: 'Close change review' }).click();
        assert.equal(await page.locator('.source-mark-check').count(), 0);
        await H.analyze(page);
        await page.locator('.source-mark-check').first().waitFor();

        await H.selectAnalysisProfile(page, 'engine-structural');
        await $(page, 'analysis-profile').selectOption('fcsc-knu');
        await $(page, 'analysis-work-type').selectOption('bachelorQualification');
        await $(page, 'reference-root').selectOption({ label: 'main.tex' });
        await H.analyze(page);
        const repair = page.getByRole('button', { name: 'Check for indent fix' });
        await repair.waitFor();
        assert.equal(await $(page, 'preview-fix').count(), 0);
        await repair.click();
        const fix = $(page, 'preview-fix');
        await fix.waitFor();
        assert.equal(await fix.getAttribute('data-fix-id'), 'KNU-FIX-PARINDENT-LITERAL-001');
        assert.equal(await H.knuReportVersion(page), '1.0.0');
        await H.assertBytes(page, project, withBom(source));

        await editor.evaluate((input, length) => {
            window.inlineReviewEditor = input;
            input.focus();
            input.setSelectionRange(0, length, 'backward');
        }, passage.replace('\r\n', '\n').length);
        const selection = () => editor.evaluate(input => ({ same: input === window.inlineReviewEditor,
            start: input.selectionStart, end: input.selectionEnd, direction: input.selectionDirection }));
        const before = await selection();
        for (const width of [1440, 768, 390]) {
            await page.setViewportSize({ width, height: 900 });
            await H.reveal(page, $(page, 'ai-panel'));
            assert.ok(await editor.isVisible());
            assert.deepEqual(await selection(), before);
            assert.equal(await page.evaluate(() => document.documentElement.scrollWidth), width);
            await H.reveal(page, $(page, 'analyze-now'));
            assert.deepEqual(await selection(), before);
        }
        assert.deepEqual(requests, [], 'opening sidebar tabs sends no document');
        await page.setViewportSize({ width: 1440, height: 900 });
        const ai = $(page, 'ai-panel');
        await H.reveal(page, ai);
        await H.reveal(page, $(page, 'ai-model'));
        await $(page, 'ai-model').fill('synthetic-inline');
        await $(page, 'ai-connect').click();
        await ai.getByRole('button', { name: 'Prepare selected passage' }).click();
        assert.equal(await $(page, 'ai-context').textContent(), passage);
        const payload = await $(page, 'ai-payload').textContent();
        assert.ok(payload.includes('selectedPassage'));
        assert.ok(!payload.includes('parindent'));
        await ai.getByRole('button', { name: 'Send disclosed request' }).click();
        await $(page, 'ai-finding').first().waitFor();
        assert.equal(requests.length, 1);
        assert.equal(requests[0].messages[0].content, payload);
        assert.deepEqual(requests[0].format.required, ['findings']);
        assert.equal(await ai.locator('[data-anchored="true"]').count(), 1);
        assert.equal(await ai.locator('[data-anchored="false"]').count(), 2);
        assert.equal(await page.locator('.source-mark-ai').textContent(), 'Привіт🙂');
        await page.locator('.source-gutter-marker.ai').click();
        assert.match(await $(page, 'active-ai-finding').textContent(), /formal greeting/);
        await ai.getByRole('button', { name: 'Use a formal greeting.' }).click();
        assert.equal(await editor.evaluate(input => input.value.slice(input.selectionStart, input.selectionEnd)), 'Привіт🙂');
        await ai.getByRole('button', { name: 'Preview fix' }).click();
        await $(page, 'review-preview').waitFor();
        assert.ok(await editor.isVisible());
        assert.equal(await $(page, 'open-ai').getAttribute('aria-pressed'), 'true');
        await H.assertBytes(page, project, withBom(source));
        await page.getByRole('button', { name: 'Close change review' }).click();
        assert.ok(!await $(page, 'review-preview').isVisible());
        await $(page, 'open-review').click();
        await H.accept(page);
        await H.assertBytes(page, project, withBom(source.replace('Привіт🙂', 'Вітаю🙂')));
        assert.equal(await page.locator('.source-mark-ai').count(), 0);
        await H.undo(page);
        await H.assertBytes(page, project, withBom(source));
        assert.equal(await page.locator('.source-mark-ai, .source-mark-check').count(), 0);
        await H.reveal(page, $(page, 'analyze-now'));
        assert.equal(await $(page, 'analyze-now').textContent(), 'Run again');

        const edited = (await editor.inputValue()) + '% draft\n';
        await editor.fill(edited);
        await $(page, 'analyze-now').click();
        await page.waitForFunction(() => document.querySelector('.workspace-header .status')?.textContent === 'Saved.');
        await page.locator('.source-mark-check').first().waitFor();
        await H.assertBytes(page, project, withBom(edited));
        await H.selectAnalysisProfile(page, 'engine-structural');
        assert.equal(await page.locator('.source-mark-check').count(), 0);
        assert.deepEqual(errors, []);
        console.log('Inline review passed: exact source marks, same-line findings, cited rule fix preview/accept/undo, cross-file navigation, responsive tabs, disclosed AI selection, and stale mark removal.');
    } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
