'use strict';

// Runs the actual pinned PDFium pair on synthetic PDF streams with known geometry.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const {createHash} = require('node:crypto');

const [parentPath, candidatePath, fixtures, receiptPath, arabicPdf, publicOnlyPath] = process.argv.slice(2);
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
const unknownCtm = 'source-ctm-accumulation-error-unmeasured';
const decoder = new TextDecoder('utf8', {fatal: true});
const keys = (value, expected) => assert.deepEqual(Object.keys(value).sort(), expected.slice().sort());

async function start(directory) {
    const loader = path.resolve(directory, 'e1.js');
    return require(loader)({wasmBinary: fs.readFileSync(path.join(directory, 'e1.wasm')), print() {}, printErr() {}});
}

function client(module) {
    function allocate(bytes) {
        const handle = module._e1_alloc(bytes.length);
        assert.notEqual(handle, 0);
        module.HEAPU8.set(bytes, module._e1_data(handle));
        return handle;
    }
    function copy(handle) {
        const size = module._e1_size(handle);
        assert.ok(size > 0);
        assert.equal(module._e1_data(handle), 0, 'immutable output has no raw pointer');
        const target = allocate(Buffer.alloc(size));
        assert.equal(module._e1_copy(handle, target), 0);
        const p = module._e1_data(target);
        const bytes = module.HEAPU8.slice(p, p + size);
        assert.equal(module._e1_release(target), 0);
        return bytes;
    }
    function take() {
        const handle = module._e1_result();
        assert.notEqual(handle, 0);
        const bytes = copy(handle);
        assert.equal(module._e1_release(handle), 0);
        return bytes;
    }
    function open(pdf, {pageIndex = 0, output = 16777216, live = 64} = {}) {
        const budget = Buffer.alloc(36);
        [1, 36, 8388608, 512, 100000, 256, 4000000, output, live].forEach((n, i) => budget.writeUInt32LE(n, 4 * i));
        const input = allocate(pdf);
        const config = allocate(budget);
        assert.equal(module._e1_open(input, config), 0);
        const document = module._e1_result();
        assert.equal(module._e1_release(input), 0);
        assert.equal(module._e1_release(config), 0);
        assert.equal(module._e1_page(document, pageIndex), 0);
        const page = module._e1_result();
        return {document, page, close() {
            assert.equal(module._e1_close(document), 0);
            assert.equal(module._e1_live(), 0);
        }};
    }
    return {module, allocate, copy, take, open};
}

function source(name) {
    return fs.readFileSync(path.join(fixtures, name + '.pdf'));
}

function facts(runtime, name, options) {
    const {module, take, open} = runtime;
    const pdf = source(name);
    const handles = open(pdf, options);
    const status = module._e1_object_layout_facts(handles.page);
    assert.equal(status, module._e1_status(), name);
    let bytes = null;
    if (status === 0) bytes = take();
    else assert.equal(module._e1_result(), 0, `${name}: partial output must not escape`);
    assert.equal(module._e1_live(), 2);
    handles.close();
    return {status, bytes, value: bytes && JSON.parse(decoder.decode(bytes))};
}

function validate(value) {
    keys(value, ['schemaVersion', 'coordinateSpace', 'scope', 'pageIndex', 'pageCount', 'media', 'crop',
        'effective', 'rotation', 'userUnit', 'directObjectCount', 'objectTypeCounts', 'images', 'scanComplete', 'limitations']);
    assert.equal(value.schemaVersion, 1);
    assert.equal(value.coordinateSpace, 'pdf-raw-unrotated-bottom-left');
    assert.equal(value.scope, 'direct-page-image-placement');
    assert.equal(value.scanComplete, true);
    assert.deepEqual(value.limitations, ['direct-page-objects-only', 'native-parsed-inventory-not-content-validation',
        'image-placement-not-ink', 'image-visibility-not-established', 'object-role-unestablished',
        'caption-association-unestablished', 'nested-forms-not-inspected', 'clip-or-compositing-uncertain-refused', unknownCtm]);
    keys(value.userUnit, ['status', 'value', 'method']);
    keys(value.objectTypeCounts, ['text', 'path', 'image', 'shading', 'form', 'unknown']);
    const counts = Object.values(value.objectTypeCounts);
    assert.ok(counts.every(n => Number.isInteger(n) && n >= 0));
    assert.equal(counts.reduce((sum, n) => sum + n, 0), value.directObjectCount);
    assert.equal(value.images.length, value.objectTypeCounts.image);
    let previousIndex = -1;
    for (const row of value.images) {
        keys(row, ['objectIndex', 'active', 'status', 'method', 'matrix', 'quad', 'rawBox', 'uncertainty', 'reason', 'limitations']);
        assert.ok(Number.isInteger(row.objectIndex) && row.objectIndex > previousIndex && row.objectIndex < value.directObjectCount);
        previousIndex = row.objectIndex;
        assert.equal(typeof row.active, 'boolean');
        assert.equal(row.method, 'native-image-unit-square-placement');
        assert.deepEqual(row.limitations, [unknownCtm]);
        if (row.status === 'available') {
            assert.equal(row.active, true);
            assert.equal(row.reason, 'parsed-native-image-matrix');
            assert.equal(row.matrix.length, 6);
            assert.equal(row.quad.length, 4);
            assert.ok(row.quad.every(point => point.length === 2 && point.every(Number.isFinite)));
            assert.ok(row.matrix.every(Number.isFinite));
            keys(row.uncertainty, ['absoluteErrorRaw', 'method']);
            assert.equal(row.uncertainty.method, 'pdfium-float32-matrix-bound');
            const error = row.uncertainty.absoluteErrorRaw;
            assert.ok(Number.isFinite(error) && error > 0);
            const [l, b, r, t] = row.rawBox;
            assert.ok(l - error >= value.effective[0] && b - error >= value.effective[1]);
            assert.ok(r + error <= value.effective[2] && t + error <= value.effective[3]);
        } else {
            assert.equal(row.status, 'indeterminate');
            assert.ok(row.reason.length > 0);
            for (const field of ['matrix', 'quad', 'rawBox', 'uncertainty']) assert.equal(row[field], null, field);
        }
    }
}

function exactImage(row, matrix, quad, box) {
    assert.ok(row, 'expected one native image occurrence');
    assert.equal(row.status, 'available', row.reason);
    assert.deepEqual(row.matrix, matrix);
    assert.deepEqual(row.quad, quad);
    assert.deepEqual(row.rawBox, box);
    assert.ok(row.uncertainty.absoluteErrorRaw < 0.001);
}

function legacy(runtime, pdf, query, measure) {
    const {module, allocate, take, open} = runtime;
    const handles = open(pdf);
    const slots = {};
    for (const name of ['facts', 'text', 'layout_capabilities', 'layout_facts', 'capabilities', 'actions']) {
        assert.equal(module['_e1_' + name](handles.page), 0, name);
        slots[name] = take();
    }
    if (query) {
        const input = allocate(Buffer.from(query + '\0', 'utf16le'));
        assert.equal(module._e1_search(handles.page, input), 0);
        slots.search = take();
        assert.ok(JSON.parse(decoder.decode(slots.search)).length > 0);
        assert.equal(module._e1_release(input), 0);
    }
    assert.equal(module._e1_raster(handles.page, 220, 220), 0);
    slots.raster = take();
    if (measure) {
        assert.equal(module._e1_object_layout_facts(handles.page), 0);
        take();
        assert.equal(module._e1_raster(handles.page, 220, 220), 0);
        assert.deepEqual(take(), slots.raster, 'read-only observation does not mutate raster paint');
        assert.equal(module._e1_layout_facts(handles.page), 0);
        assert.deepEqual(take(), slots.layout_facts, 'read-only observation does not mutate old layout');
    }
    handles.close();
    return slots;
}

function pixelBounds(raster) {
    const bytes = Buffer.from(raster);
    assert.equal(bytes.readUInt32LE(0), 0x31424752);
    assert.equal(bytes.readUInt32LE(4), 220);
    assert.equal(bytes.readUInt32LE(8), 220);
    const stride = bytes.readUInt32LE(12);
    let l = 220, b = 220, r = -1, t = -1;
    for (let y = 0; y < 220; y++) {
        for (let x = 0; x < 220; x++) {
            const at = 16 + y * stride + 4 * x;
            if (bytes[at] === 255 && bytes[at + 1] === 255 && bytes[at + 2] === 255) continue;
            l = Math.min(l, x);
            b = Math.min(b, 219 - y);
            r = Math.max(r, x + 1);
            t = Math.max(t, 220 - y);
        }
    }
    return [l, b, r, t];
}

async function main() {
    const parent = client(await start(parentPath));
    const candidate = client(await start(candidatePath));
    const m = candidate.module;
    assert.equal(parent.module._e1_version(), 1);
    assert.equal(m._e1_version(), 1);
    assert.equal(typeof parent.module._e1_object_layout_facts, 'undefined');
    assert.equal(typeof m._e1_object_layout_facts, 'function');
    const exports = module => Object.keys(module).filter(name => name.startsWith('_e1_'));
    assert.deepEqual(exports(m).sort(), [...exports(parent.module), '_e1_object_layout_facts'].sort());
    const cases = [];
    function check(name, status = 0, options) {
        const result = facts(candidate, name, options);
        assert.equal(result.status, status, name);
        if (result.value) validate(result.value);
        cases.push({name, options: options ?? null, status, pdfSha256: sha256(source(name)),
            outputSha256: result.bytes && sha256(result.bytes), outputBytes: result.bytes?.length ?? 0});
        return result;
    }
    const empty = check('empty').value;
    assert.equal(empty.directObjectCount, 0);
    assert.deepEqual(empty.images, []);
    assert.deepEqual(empty.media, [0, 0, 220, 220]);
    assert.equal(empty.crop, null);
    assert.deepEqual(empty.userUnit, {status: 'available', method: 'default-userunit', value: 1});
    const image = check('image');
    exactImage(image.value.images[0], [120, 0, 0, 120, 30, 40], [[30, 40], [150, 40], [150, 160], [30, 160]], [30, 40, 150, 160]);
    const ordering = check('ordering').value;
    assert.equal(ordering.pageCount, 2);
    assert.equal(ordering.pageIndex, 0);
    assert.deepEqual(ordering.objectTypeCounts, {text: 1, path: 1, image: 2, form: 0, shading: 0, unknown: 0});
    assert.deepEqual(ordering.images.map(row => row.objectIndex), [1, 3]);
    exactImage(ordering.images[1], [-75, 28, 18, 65, 148, 38], [[148, 38], [73, 66], [91, 131], [166, 103]], [73, 38, 166, 131]);
    const nextPage = check('ordering', 0, {pageIndex: 1}).value;
    assert.equal(nextPage.pageCount, 2);
    assert.equal(nextPage.pageIndex, 1);
    exactImage(nextPage.images[0], [60, 0, 0, 45, 20, 25], [[20, 25], [80, 25], [80, 70], [20, 70]], [20, 25, 80, 70]);
    exactImage(check('shear').value.images[0], [55, -13, 16, 48, 54, 68], [[54, 68], [109, 55], [125, 103], [70, 116]], [54, 55, 125, 116]);
    for (const rotation of [0, 90, 180, 270]) {
        const frame = check(`frame-${rotation}`).value;
        assert.deepEqual(frame.media, [-30, -20, 210, 200]);
        assert.deepEqual(frame.crop, [-8, 2, 178, 154]);
        assert.deepEqual(frame.effective, frame.crop);
        assert.equal(frame.rotation, rotation);
        assert.deepEqual(frame.userUnit, {status: 'available', method: 'page-dictionary-userunit', value: 2});
        exactImage(frame.images[0], [70, 0, 0, 60, 10, 20], [[10, 20], [80, 20], [80, 80], [10, 80]], [10, 20, 80, 80]);
    }
    assert.deepEqual(check('invalid-user-unit').value.userUnit, {status: 'indeterminate', method: 'invalid', value: null});
    assert.deepEqual(check('parent-unit').value.userUnit, {status: 'available', method: 'default-userunit', value: 1});
    check('incompatible-boxes', 6);
    const nested = check('forms-and-shading').value;
    assert.deepEqual(nested.objectTypeCounts, {text: 0, path: 0, image: 0, form: 1, shading: 1, unknown: 0});
    assert.deepEqual(nested.images, []);
    for (const [name, reason] of Object.entries({
        'cross-crop': 'crosses-effective-page', 'touch-crop': 'crosses-effective-page', 'cross-media': 'crosses-effective-page',
        clip: 'clip-path-present', marked: 'optional-content-unresolved', optional: 'optional-content-unresolved',
        alpha: 'nondefault-compositing', group: 'page-compositing-group', mask: 'image-mask-or-softmask',
        softmask: 'image-mask-or-softmask', stencil: 'image-mask-or-softmask', 'malformed-width': 'invalid-image-dimensions',
        degenerate: 'invalid-object-matrix', 'matrix-out-of-domain': 'invalid-object-matrix',
    })) {
        const rows = check(name).value.images;
        assert.equal(rows.length, 1, name);
        assert.equal(rows[0].status, 'indeterminate', name);
        assert.equal(rows[0].reason, reason, name);
    }
    assert.equal(check('explicit-non-stencil').value.images[0].status, 'available');
    assert.equal(check('512-images').value.images.length, 512);
    check('513-images', 3);
    assert.equal(check('10000-objects').value.directObjectCount, 10000);
    check('10001-objects', 3);
    assert.deepEqual(check('image', 0, {output: image.bytes.length}).bytes, image.bytes);
    check('image', 3, {output: image.bytes.length - 1});
    assert.deepEqual(check('image', 0, {live: 3}).bytes, image.bytes);

    const h = candidate.open(source('image'), {live: 3});
    assert.equal(m._e1_object_layout_facts(h.page), 0);
    const output = m._e1_result();
    assert.equal(m._e1_object_layout_facts(h.page), 3, 'a held output must consume its live slot');
    assert.equal(m._e1_result(), 0);
    assert.equal(m._e1_live(), 3);
    assert.equal(m._e1_close(h.document), 0);
    assert.equal(m._e1_object_layout_facts(h.page), 4, 'closed document invalidates page');
    assert.equal(m._e1_result(), 0);
    assert.deepEqual(candidate.copy(output), image.bytes, 'output owner remains valid after document close');
    const reopened = candidate.open(source('image'));
    m._e1_reset();
    assert.equal(m._e1_object_layout_facts(reopened.page), 4, 'reset invalidates page');
    assert.equal(m._e1_size(output), 0, 'reset invalidates retained output');
    assert.equal(m._e1_live(), 0);
    assert.deepEqual(check('image').bytes, image.bytes, 'fresh admission after reset');

    const legacyCases = [];
    for (const [name, pdf, query] of [
        ['image', source('image'), null],
        ['ordering', source('ordering'), 'Placement check'],
        ['forms-and-shading', source('forms-and-shading'), null],
        ['frame-270', source('frame-270'), null],
        ...(arabicPdf ? [['retained-arabic', fs.readFileSync(arabicPdf), 'مرحبا بالعالم']] : []),
    ]) {
        if (name === 'retained-arabic') {
            assert.equal(sha256(pdf), '613b5eb04c43251ec40e7b54e4d36db402dba1f4ce2289547fcd2be23f874653');
        }
        const old = legacy(parent, pdf, query, false);
        const next = legacy(candidate, pdf, query, true);
        assert.deepEqual(next, old, name);
        if (name === 'image') assert.deepEqual(pixelBounds(next.raster), image.value.images[0].rawBox);
        legacyCases.push({name, pdfSha256: sha256(pdf), outputSha256: Object.fromEntries(Object.entries(next).map(([n, b]) => [n, sha256(b)]))});
    }
    let publicOnlyStatus = null;
    if (publicOnlyPath) {
        const fallback = client(await start(publicOnlyPath));
        assert.equal(fallback.module._e1_version(), 1);
        publicOnlyStatus = facts(fallback, 'image').status;
        assert.equal(publicOnlyStatus, 6, 'public-header-only wrapper explicitly refuses native object facts');
    }
    const receipt = {status: 'PASS', scope: 'isolated native image placement facts and frozen legacy bytes', cases,
        lifecycle: ['held output quota', 'document close invalidation', 'output survives document close', 'reset invalidation', 'fresh admission'],
        legacyCases, arabicControlSupplied: Boolean(arabicPdf), publicOnlyStatus};
    fs.writeFileSync(receiptPath, JSON.stringify(receipt, null, 2) + '\n', {flag: 'wx'});
    console.log(JSON.stringify({status: 'PASS', nativeCaseCount: cases.length, lifecycle: receipt.lifecycle.length, legacyByteComparisons: legacyCases.length}));
}

main().catch(error => { console.error(error); process.exitCode = 1; });
