'use strict';

const assert = require('node:assert/strict');
const H = require('./review-ui-helpers.cjs');
const PAGE_INDEX = 1;
const MARKERS = {above:'Bodyabove', heading:'HEADWORD', below:'Bodybelow'};
const EVALUATOR = '/js/rendered-review-worker.js';
const INSPECTOR = '/__oop/native-worker/pdf';

function fixture(plain) {
    const source = plain.toString('utf8'), end = '\\end{document}';
    assert.equal(source.split(end).length, 2);
    const extra = [
        '', '\\clearpage', '\\thispagestyle{empty}', '\\pdfpageattr{/CropBox [8 10 594 780] /UserUnit 2}',
        '\\noindent\\begin{picture}(320,180)',
        `  \\put(35,150){\\ttfamily ${MARKERS.above}}`,
        `  \\put(35,100){\\ttfamily ${MARKERS.heading}}`,
        `  \\put(35,40){\\ttfamily ${MARKERS.below}}`,
        '\\end{picture}\\par', end,
    ].join('\n');
    return Buffer.from(source.replace(end, extra), 'utf8');
}

async function confirmSearchRange(page, marker, role, authority) {
    const use = H.$(page,'rendered-role-use-search'), roleField = H.$(page,'rendered-role');
    const pane = H.$(page,'pdf-pane'), input = pane.locator('input[aria-label="Search PDF"]');
    await H.reveal(page,roleField);
    await roleField.selectOption(role);
    await H.$(page,'rendered-role-certainty').selectOption('confirmedObservation');
    await H.reveal(page,input);
    await input.fill(marker);
    const searchGeneration = await pane.getAttribute('data-generation');
    await pane.getByRole('button',{name:'Search document',exact:true}).click();
    await page.waitForFunction(old => {
        const pane = document.querySelector('[data-testid="pdf-pane"]');
        return pane?.dataset.generation !== old && pane?.querySelector('[data-testid="search-status"]')?.dataset.searchStatus === 'Complete';
    },searchGeneration,{timeout:45000});
    assert.equal(await H.$(page,'search-status').getAttribute('data-match-count'),'1',marker);
    const hitGeneration = await pane.getAttribute('data-generation');
    await pane.getByRole('button',{name:'Next result',exact:true}).click();
    await page.waitForFunction(old => {
        const pane = document.querySelector('[data-testid="pdf-pane"]'), canvas = pane?.querySelector('[data-testid="pdf-canvas"]');
        return pane?.dataset.generation !== old && pane?.querySelector('[data-testid="selected-result"]')?.textContent === 'Result 1 / 1'
            && canvas?.dataset.drawGeneration === pane?.dataset.generation && canvas?.dataset.pdfHash === pane?.dataset.document;
    },hitGeneration,{timeout:45000});
    assert.ok(await H.$(page,'glyph-highlight').count() > 0,marker+' has an actual rendered search highlight');
    await use.click();
    const draft = await H.$(page,'rendered-role-search-draft').evaluate(node => ({...node.dataset}));
    assert.equal(draft.current,'true');
    assert.equal(draft.glyphText,marker);
    assert.equal(draft.pageIndex,String(PAGE_INDEX));
    assert.equal(draft.pdfSha256,authority.hash);
    assert.equal(draft.compileRequest,authority.request);
    assert.equal(draft.generation,await pane.getAttribute('data-generation'));
    const start = Number(draft.characterStart), end = Number(draft.characterEnd);
    assert.ok(Number.isSafeInteger(start) && start >= 0);
    assert.equal(end-start,marker.length);
    const row = H.$(page,'rendered-role-row').and(page.locator(`[data-role="${role}"][data-character-start="${start}"][data-character-end="${end}"]`));
    assert.equal(await row.count(),0);
    await H.$(page,'rendered-role-add').click();
    await row.waitFor({state:'attached'});
    const label = await row.evaluate(node=>({...node.dataset}));
    assert.equal(label.pageIndex,String(PAGE_INDEX));
    assert.equal(label.provenance,'authorLabel');
    assert.equal(label.certainty,'confirmedObservation');
    assert.match(label.annotationId,/^[1-9][0-9]*$/);
    return {id:Number(label.annotationId),role,start,end,marker,pdfGeneration:draft.generation};
}

async function removeAndObserveStaleness(page, label, authority) {
    const report = H.$(page,'rendered-review-report');
    const version = await report.getAttribute('data-annotation-version');
    assert.equal(await report.getAttribute('data-current'),'true');
    const row = H.$(page,'rendered-role-row').and(page.locator(`[data-annotation-id="${label.id}"]`));
    await row.getByRole('button',{name:/^Remove /}).click();
    await row.waitFor({state:'detached'});
    await H.$(page,'rendered-review-stale').waitFor();
    assert.equal(await report.getAttribute('data-current'),'false');
    assert.equal(await report.getAttribute('data-pdf-sha256'),authority.hash);
    assert.equal(await report.getAttribute('data-annotation-version'),version);
    assert.equal(await report.getByRole('button',{name:'Show page 2 in PDF preview',exact:true}).first().isDisabled(),true);
    return Number(version);
}

async function check(page, authority, workers, outcome, reason, heading, body, order) {
    const offset = workers().length;
    await H.$(page,'rendered-review-check').click();
    await page.waitForFunction(() => document.querySelector('[data-testid="rendered-review-status"]')?.dataset.busy === 'false'
        && document.querySelector('[data-testid="rendered-review-report"]')?.dataset.current === 'true');
    const report = H.$(page,'rendered-review-report'), identity = H.$(page,'rendered-review-identity');
    assert.equal(await report.getAttribute('data-complete'),'true');
    assert.equal(await report.getAttribute('data-pdf-sha256'),authority.hash);
    assert.match(await H.$(page,'rendered-review-coverage').textContent(),/Inspected 1 of 1 requested pages\./);
    const identityText = await identity.textContent();
    assert.match(identityText,/knu-csc-2025-rendered-review 1\.3\.0/);
    assert.ok(identityText.includes(`Compile request: ${authority.request};`));
    assert.ok(identityText.includes(`Inspector loader SHA256: ${authority.renderer.loader}; WASM SHA256: ${authority.renderer.wasm}.`));
    const pagination = report.locator('[data-rule-id="KNU-PAGINATION-001"]');
    assert.equal(await pagination.getAttribute('data-outcome'),'indeterminate');
    assert.equal(await pagination.locator('[data-testid="rendered-review-reason"]').textContent(),'confirmed page number range required');
    const rule = report.locator('[data-rule-id="KNU-HEADINGS-RENDERED-001"]');
    assert.equal(await rule.getAttribute('data-outcome'),outcome);
    assert.equal(await rule.getAttribute('data-support'),'partial');
    assert.equal((await rule.locator('[data-testid="rendered-review-reason"]').textContent()).trim(),reason.replaceAll('-',' '));
    const evidence = await rule.locator('[data-testid="rendered-review-measurement"] pre').evaluateAll(nodes => nodes.map(node=>JSON.parse(node.textContent)));
    assert.equal(evidence.length,body?1:0);
    const headingLabel = H.$(page,'rendered-role-row').and(page.locator(
        `[data-role="headingMajor"][data-page-index="${PAGE_INDEX}"][data-character-start="${heading.start}"][data-character-end="${heading.end}"]`));
    assert.equal(await headingLabel.count(),1,'the same confirmed heading range remains selected');
    // Adding a role republishes all author annotations with new IDs. Compare
    // the evidence with the current ID for this exact range.
    const headingAnnotationId = Number(await headingLabel.getAttribute('data-annotation-id'));
    assert.ok(Number.isSafeInteger(headingAnnotationId) && headingAnnotationId > 0);
    if (body) {
        const [value] = evidence, observation = value.observation;
        assert.deepEqual([value.pageIndex,value.characterStart,value.characterEnd],[PAGE_INDEX,heading.start,heading.end]);
        assert.deepEqual(value.roleProvenance,{annotationId:headingAnnotationId,certainty:'confirmedObservation',provenance:'authorLabel',role:'headingMajor'});
        assert.equal(value.method,'confirmed-major-heading-and-body-ink');
        assert.equal(value.unit,'mm');
        assert.equal(value.expected,null);
        assert.equal(value.sourceLocation,null);
        assert.equal(observation.kind,'confirmed-major-heading-page-order');
        assert.equal(observation.relativeOrder,order);
        assert.deepEqual([observation.bodyAnnotationId,observation.bodyCharacterStart,observation.bodyCharacterEnd],[body.id,body.start,body.end]);
        assert.equal(observation.userUnit,2);
        const rawGap = order==='body-above-heading' ? observation.bodyInkBox[1]-value.rawBox[3] : observation.bodyInkBox[3]-value.rawBox[1];
        assert.ok(Number.isFinite(observation.absoluteErrorRaw) && observation.absoluteErrorRaw > 0);
        assert.ok(Math.abs(rawGap) > observation.absoluteErrorRaw);
        assert.ok(Math.abs(rawGap-observation.rawSignedGap) < 1e-9);
        assert.equal(Math.sign(value.measured),order==='body-above-heading'?1:-1);
        assert.ok(Math.abs(value.measured-observation.rawSignedGap*observation.userUnit*25.4/72)<1e-9);
        assert.ok(Number.isFinite(value.uncertainty.absoluteError) && value.uncertainty.absoluteError > 0 && Math.abs(value.measured)>value.uncertainty.absoluteError);
    }
    const launched = workers().slice(offset).map(worker=>new URL(worker.url).pathname);
    assert.equal(launched.filter(name=>name===INSPECTOR).length,1,'exactly one measured PDF page');
    assert.equal(launched.filter(name=>name===EVALUATOR).length,1,'exactly one real Rust page review');
    return {outcome,reason,evidence,headingAnnotationId,annotationVersion:Number(await report.getAttribute('data-annotation-version')),actualWorkers:launched};
}

async function verify(page, project, hash, workers, renderer, coldOffline) {
    const before = await H.head(page,project);
    const request = (await H.$(page,'compile-details').textContent()).match(/Request: ([a-f0-9]{32});/)?.[1];
    assert.ok(request);
    assert.equal(await H.$(page,'compile-panel').getAttribute('data-is-current'),'true');
    const authority = {hash,request,renderer};
    const numbers = H.$(page,'rendered-review-work-numbers'), order = H.$(page,'rendered-review-major-heading-order');
    await H.reveal(page,numbers);
    assert.equal(await numbers.isChecked(),false);
    assert.equal(await order.count(),0);
    assert.equal(await H.$(page,'rendered-role-row').count(),0);
    await numbers.check();
    await order.waitFor();
    assert.equal(await order.isChecked(),false);
    await order.check();
    await H.$(page,'rendered-review-work-type').selectOption('bachelorQualification');
    await H.$(page,'rendered-review-all-pages').uncheck();
    await H.$(page,'rendered-review-first').fill(String(PAGE_INDEX+1));
    await H.$(page,'rendered-review-last').fill('');
    const heading = await confirmSearchRange(page,MARKERS.heading,'headingMajor',authority);
    const above = await confirmSearchRange(page,MARKERS.above,'body',authority);
    const follows = await check(page,authority,workers,'fail','confirmed-major-heading-follows-body-on-page',heading,above,'body-above-heading');
    const version1 = await removeAndObserveStaleness(page,above,authority);
    const below = await confirmSearchRange(page,MARKERS.below,'body',authority);
    const precedes = await check(page,authority,workers,'indeterminate','conditional-major-heading-precedes-body-on-page',heading,below,'body-below-heading');
    const version2 = await removeAndObserveStaleness(page,below,authority);
    const unlabeled = await check(page,authority,workers,'indeterminate','confirmed-heading-body-range-required',heading,null,null);
    assert.equal(version1,follows.annotationVersion);
    assert.equal(version2,precedes.annotationVersion);
    assert.ok(unlabeled.annotationVersion > version2 && version2 > version1);
    assert.equal(await H.$(page,'rendered-role-row').count(),1);
    assert.equal(await H.$(page,'compile-panel').getAttribute('data-output-hash'),hash);
    assert.equal(await H.$(page,'compile-panel').getAttribute('data-is-current'),'true');
    assert.deepEqual(await H.head(page,project),before);
    return {coldOffline,authority,pageIndex:PAGE_INDEX,heading,above,below,follows,precedes,unlabeled,
        staleReportVersions:[version1,version2],sourceAndHeadUnchanged:true};
}

module.exports = {fixture,verify};
