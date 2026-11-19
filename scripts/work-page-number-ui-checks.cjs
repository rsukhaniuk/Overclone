'use strict';

const assert = require('node:assert/strict');
const H = require('./review-ui-helpers.cjs');

function fixture(plain) {
    const token = '\\end{document}';
    const source = plain.toString('utf8');
    assert.equal(source.split(token).length, 2);
    return Buffer.from(source.replace('\\begin{document}',
        '\\begin{document}\n\\pagestyle{empty}\n\\noindent\\hfill\\texttt{91}\\par')
        .replace(token, [
            '\\clearpage\\noindent\\hfill\\texttt{002}\\par',
            'Second physical page.',
            '\\clearpage\\appendix\\noindent\\hfill\\texttt{73}\\par',
            'Appendix body with an explicitly wrong work page number.',
            '\\clearpage',
            'Page without an assigned number role.',
            token,
        ].join('\n')), 'utf8');
}

async function verify(page, project, outputHash, workers, coldOffline) {
    const beforeHead = await H.head(page, project);
    const toggle = H.$(page, 'rendered-review-work-numbers');
    await H.reveal(page, toggle);
    assert.equal(await toggle.isChecked(), false, 'the versioned check is opt-in');
    assert.equal(await H.$(page, 'rendered-role-row').count(), 0);
    await toggle.check();
    await H.$(page, 'rendered-review-work-type').selectOption('bachelorQualification');
    const role = H.$(page, 'rendered-role');
    await H.reveal(page, role);
    await role.selectOption('title');
    await H.$(page, 'rendered-role-first').fill('1');
    const certainty = H.$(page, 'rendered-role-certainty');
    await certainty.selectOption('confirmedObservation');
    const add = H.$(page, 'rendered-role-add');
    await add.click();
    await H.$(page, 'rendered-role-row').filter({hasText:'Title page'}).waitFor();

    await role.selectOption('pageNumber');
    const search = page.locator('input[aria-label="Search PDF"]');
    await H.reveal(page, search);
    for (const [number, pageIndex] of [['91',0], ['002',1], ['73',2]]) {
        await search.fill(number);
        await page.getByRole('button', {name:'Search document',exact:true}).click();
        await page.waitForFunction(() => document.querySelector('[data-testid=search-status]')?.dataset.searchStatus === 'Complete');
        assert.equal(await H.$(page,'search-status').getAttribute('data-match-count'), '1');
        await page.getByRole('button', {name:'Next result',exact:true}).click();
        const use = H.$(page, 'rendered-role-use-search');
        await H.reveal(page, use);
        await use.click();
        const draft = H.$(page, 'rendered-role-search-draft');
        await draft.waitFor();
        assert.equal(await draft.getAttribute('data-page-index'), String(pageIndex));
        assert.equal(await draft.getAttribute('data-glyph-text'), number);
        assert.equal(await draft.getAttribute('data-current'), 'true');
        assert.equal(await certainty.inputValue(), 'confirmedObservation');
        await add.click();
    }
    assert.equal(await H.$(page, 'rendered-role-row').count(), 4);
    assert.equal(await toggle.isDisabled(), true, 'the confirmed title and number pair belongs to the selected profile');

    const check = H.$(page, 'rendered-review-check');
    const prior = workers().length;
    await check.click();
    const report = H.$(page, 'rendered-review-report');
    await page.waitForFunction(() => document.querySelector('[data-testid=rendered-review-status]')?.dataset.busy === 'false'
        && document.querySelector('[data-testid=rendered-review-report]')?.dataset.current === 'true');
    assert.equal(await report.getAttribute('data-pdf-sha256'), outputHash);
    assert.match(await H.$(page, 'rendered-review-identity').textContent(), /knu-csc-2025-rendered-review 1\.2\.0/);
    const row = report.locator('[data-rule-id="KNU-PAGINATION-001"]');
    assert.equal(await row.getAttribute('data-outcome'), 'fail');
    const evidence = await row.locator('[data-testid=rendered-review-measurement] pre').evaluateAll(els => els.map(el => JSON.parse(el.textContent)));
    assert.deepEqual(evidence.map(e => [e.pageIndex, e.measured, e.expected, e.observation.comparison]), [
        [0, '91', null, 'title-number-present'], [1, '002', 2, 'index-match'], [2, '73', 3, 'index-mismatch'],
    ]);
    for (const {observation} of evidence) {
        assert.ok(observation.placement.topClearanceMm > 0 && observation.placement.rightClearanceMm > 0);
        assert.ok(observation.placement.absoluteErrorMm > 0);
    }
    const actual = workers().slice(prior);
    assert.ok(actual.filter(w => new URL(w.url).pathname === '/__oop/native-worker/pdf').length >= 4);
    assert.ok(actual.filter(w => new URL(w.url).pathname === '/js/rendered-review-worker.js').length >= 4);

    await H.$(page, 'rendered-review-all-pages').uncheck();
    await H.$(page, 'rendered-review-first').fill('4');
    await check.click();
    await page.waitForFunction(() => document.querySelector('[data-testid=rendered-review-status]')?.dataset.busy === 'false'
        && document.querySelector('[data-testid=rendered-review-report]')?.dataset.current === 'true'
        && document.querySelector('[data-testid=rendered-review-coverage]')?.textContent.includes('Inspected 1 of 1'));
    assert.equal(await row.getAttribute('data-outcome'), 'indeterminate');
    assert.equal(await row.locator('[data-testid=rendered-review-measurement]').count(), 0);
    assert.deepEqual(await H.head(page, project), beforeHead);
    return {evidence, unlabeledPageIndeterminate:true, actualNativeAndRustWorkers:true, sourceUnchanged:true, coldOffline};
}

module.exports = {fixture, verify};
