'use strict';

// Runs the real PDF worker in Node with a test-only worker/loader adapter when
// pinned native fixtures are supplied. Bridge admission tests model browser APIs.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const {Worker, parentPort, workerData} = require('node:worker_threads');
const {test} = require('node:test');
const {harness, tick, hash} = require('./restricted-worker-test-helpers.cjs');

const workerSource = path.join(__dirname, '../../src/Client/wwwroot/js/pdf-port-worker.js');
const prefix = ['e1_version', 'e1_open', 'e1_document_pages', 'e1_page'];
const shell = (document, loader, wasm, more = {}) => ({
    version: 2, requestId: 'object-layout-request', epoch: 1, documentSha256: hash(document),
    loaderSha256: hash(loader), wasmSha256: hash(wasm), pageIndex: 0,
    search: null, raster: false, width: 1, height: 1, inspectActions: false,
    budget: {inputBytes: 8388608, pages: 32, textUnits: 100000, searchHits: 256,
        rasterPixels: 4000000, outputBytes: 16777216, liveHandles: 64, deadlineMs: 5000},
    ...more,
});

function child() {
    globalThis.self = globalThis;
    globalThis.postMessage = (message, transfer = []) => parentPort.postMessage(message, transfer);
    globalThis.importScripts = () => {
        const create = require(workerData.loader);
        globalThis.E1 = async options => {
            const native = await create(options);
            const original = native._e1_object_layout_facts;
            if (workerData.control === 'missing-layout') delete native._e1_layout_facts;
            if (workerData.control === 'hold-after-real-object') {
                native._e1_object_layout_facts = page => {
                    const status = original(page);
                    // Test-only gate after genuine native output and before JS
                    // reads it. The test terminates the actual worker thread.
                    parentPort.postMessage({kind: 'held-native-output', status});
                    Atomics.wait(new Int32Array(workerData.gate), 0, 0);
                    return status;
                };
            }
            if (['bad-utf8', 'oversized', 'layout-bom'].includes(workerData.control)) {
                let output = 0;
                const operation = workerData.control === 'layout-bom' ? '_e1_layout_facts' : '_e1_object_layout_facts';
                const read = native[operation];
                native[operation] = page => {
                    const status = read(page);
                    output = native._e1_result();
                    return status;
                };
                const copy = native._e1_copy;
                native._e1_copy = (source, destination) => {
                    const status = copy(source, destination);
                    if (status === 0 && source === output) {
                        const offset = native._e1_data(destination);
                        if (workerData.control === 'bad-utf8') native.HEAPU8[offset] = 255;
                        if (workerData.control === 'layout-bom') native.HEAPU8.set([239, 187, 191], offset);
                    }
                    return status;
                };
                const size = native._e1_size;
                native._e1_size = handle => workerData.control === 'oversized' && handle === output
                    ? 1048577 : size(handle);
            }
            return native;
        };
    };
    vm.runInThisContext(fs.readFileSync(workerSource, 'utf8'), {filename: workerSource});
    parentPort.on('message', data => globalThis.onmessage({data}));
}

function runNative(directory, document, options = {}) {
    const loaderPath = path.resolve(directory, 'e1.js');
    const loader = fs.readFileSync(loaderPath), wasm = fs.readFileSync(path.join(directory, 'e1.wasm'));
    const request = shell(document, loader, wasm, options.request);
    if (options.outputBytes !== undefined) request.budget.outputBytes = options.outputBytes;
    const gate = new SharedArrayBuffer(4);
    const worker = new Worker(__filename, {workerData: {loader: loaderPath, control: options.control, gate}});
    let timeout;
    let onHeld;
    const atGate = new Promise(resolve => { onHeld = resolve; });
    const terminal = new Promise((resolve, reject) => {
        timeout = setTimeout(() => reject(new Error('Native PDF worker test deadline')), 6000);
        worker.on('error', reject);
        worker.on('exit', () => resolve(null));
        worker.on('message', message => {
            if (message.kind === 'held-native-output') onHeld(message.status);
            if (message.kind === 'terminal') resolve(message.response);
        });
    }).finally(() => clearTimeout(timeout));
    worker.postMessage({request, document: new Uint8Array(document), loader: new Uint8Array(loader),
        wasm: new Uint8Array(wasm), loaderUrl: 'blob:test-owned'});
    return {terminal, atGate, worker, request};
}

async function capture(directory, document, options = {}) {
    const work = runNative(directory, document, options);
    try {
        const response = await work.terminal;
        assert.ok(response);
        for (const key of ['requestId', 'version', 'epoch', 'documentSha256', 'loaderSha256', 'wasmSha256'])
            assert.equal(response[key], work.request[key]);
        assert.equal(response.liveAfter, 0);
        return response;
    } finally { await work.worker.terminate(); }
}

function requireNoFacts(response) {
    for (const name of ['objectLayoutFactsJson', 'layoutFactsJson', 'layoutCapabilitiesJson', 'actionsJson', 'textJson', 'factsJson'])
        assert.equal(response[name] ?? null, null, name);
}

if (parentPort) child();
else {
    test('object layout flag admits an explicit pair and rejects conflicting frames before byte ownership', async () => {
        const h = harness();
        const port = h.createPdfPort();
        const [document, loader, wasm] = [Buffer.of(1, 2), Buffer.of(3), Buffer.alloc(8)];
        const changes = [{inspectObjectLayout: 'true'}, {inspectObjectLayout: true, raster: true},
            {inspectObjectLayout: true, search: 'a'}, {inspectObjectLayout: true, inspectActions: true}];
        try {
            for (const change of changes) {
                const result = await port.inspect(JSON.stringify(shell(document, loader, wasm, change)), document, loader, wasm);
                assert.equal(result.status, 'transport-error');
            }
            assert.equal(h.stats.byteAllocations, 0);
            assert.equal(h.stats.workers.length, 0);
            const request = shell(document, loader, wasm, {inspectObjectLayout: true, inspectLayout: true});
            const pending = port.inspect(JSON.stringify(request), document, loader, wasm);
            for (let i = 0; i < 200 && !h.stats.workers.at(-1)?.messages.length; i++) await tick();
            const worker = h.stats.workers.at(-1);
            assert.equal(worker.messages[0].request.inspectObjectLayout, true);
            assert.equal(worker.messages[0].request.inspectLayout, true);
            port.cancel(request.requestId);
            const cancelled = await pending;
            assert.equal(cancelled.status, 'cancelled');
            assert.equal(cancelled.enteredNative, null);
            requireNoFacts(cancelled);
            assert.equal(worker.terminated, 1);
            assert.equal(h.stats.revoked.length, 1);
        } finally { port.dispose(); h.api.dispose(); }
    });

    const candidate = process.env.OBJECT_LAYOUT_CANDIDATE, parent = process.env.OBJECT_LAYOUT_PARENT;
    const fixture = process.env.OBJECT_LAYOUT_PDF;
    const skip = !candidate || !parent || !fixture ? 'Requires exact native candidate, parent and original synthetic PDF' : false;
    test('pinned PDF worker pairs actual facts, and retains explicit unsupported and ordinary old layouts', {skip}, async () => {
        const document = fs.readFileSync(fixture);
        assert.equal(hash(document), '6e846e3d4a1c92fa9ec6f5556e9844c5a8a40dd369363211ee57fec8de1933cc');
        const only = await capture(candidate, document, {request: {inspectObjectLayout: true}});
        assert.equal(only.status, 'completed');
        assert.equal(only.objectLayoutState, 'available');
        assert.equal(only.pageCount, 2);
        assert.equal(only.layoutFactsJson, null);
        assert.deepEqual(only.nativeCalls, [...prefix, 'e1_object_layout_facts', 'e1_copy', 'e1_reset']);
        const facts = JSON.parse(only.objectLayoutFactsJson);
        assert.deepEqual(facts.images.map(r => r.objectIndex), [1, 3]);
        assert.deepEqual(facts.images[1].rawBox, [73, 38, 166, 131]);
        const paired = await capture(candidate, document, {request: {inspectObjectLayout: true, inspectLayout: true}});
        assert.equal(paired.status, 'completed');
        assert.equal(paired.objectLayoutState, 'available');
        assert.equal(paired.layoutState, 'available');
        assert.equal(paired.objectLayoutFactsJson, only.objectLayoutFactsJson);
        assert.deepEqual(paired.nativeCalls, [...prefix, 'e1_layout_capabilities', 'e1_copy', 'e1_layout_facts', 'e1_copy',
            'e1_object_layout_facts', 'e1_copy', 'e1_reset']);
        const old = await capture(parent, document, {request: {inspectLayout: true}});
        assert.equal(old.status, 'completed');
        assert.ok(!Object.hasOwn(old, 'objectLayoutState'));
        assert.deepEqual([old.layoutCapabilitiesJson, old.layoutFactsJson], [paired.layoutCapabilitiesJson, paired.layoutFactsJson]);
        for (const [runtime, control] of [[parent, undefined], [candidate, 'missing-layout']]) {
            const absent = await capture(runtime, document, {request: {inspectObjectLayout: true, inspectLayout: true}, control});
            assert.equal(absent.objectLayoutState, 'unsupported');
            assert.deepEqual(absent.nativeCalls, [...prefix, 'e1_reset']);
            requireNoFacts(absent);
        }
        const legacy = await capture(candidate, document);
        assert.equal(legacy.status, 'completed');
        assert.ok(!Object.hasOwn(legacy, 'objectLayoutFactsJson'));
        assert.ok(!legacy.nativeCalls.includes('e1_object_layout_facts'));
        if (process.env.OBJECT_LAYOUT_WORKER_FIXTURE_OUT) {
            const receipt = {pdfSha256: hash(document), pdfBase64: document.toString('base64'), nativeWorker: paired};
            fs.writeFileSync(process.env.OBJECT_LAYOUT_WORKER_FIXTURE_OUT, JSON.stringify(receipt, null, 2) + '\n', {flag: 'wx'});
        }
    });

    test('actual PDF worker enforces aggregate native bytes and fails closed on malformed object payloads', {skip}, async () => {
        const document = fs.readFileSync(fixture);
        const request = {inspectObjectLayout: true, inspectLayout: true};
        const first = await capture(candidate, document, {request});
        const bytes = Buffer.byteLength(first.objectLayoutFactsJson) + Buffer.byteLength(first.layoutFactsJson) + Buffer.byteLength(first.layoutCapabilitiesJson);
        const exact = await capture(candidate, document, {request, outputBytes: bytes});
        assert.equal(exact.status, 'completed');
        const clipped = await capture(candidate, document, {request, outputBytes: bytes - 1});
        assert.equal(clipped.status, 'output-limit');
        requireNoFacts(clipped);
        const bom = await capture(candidate, document, {request, control: 'layout-bom'});
        assert.equal(bom.layoutFactsJson.codePointAt(0), 0xfeff);
        assert.equal(bom.layoutFactsJson.slice(1), first.layoutFactsJson.slice(3));
        for (const [control, status] of [['bad-utf8', 'transport-error'], ['oversized', 'output-limit']]) {
            const result = await capture(candidate, document, {request, control});
            assert.equal(result.status, status);
            requireNoFacts(result);
        }
    });

    test('actual worker termination discards a genuine object result before it can publish', {skip}, async () => {
        const document = fs.readFileSync(fixture);
        const request = {inspectObjectLayout: true, inspectLayout: true};
        const run = runNative(candidate, document, {request, control: 'hold-after-real-object'});
        try {
            const status = await Promise.race([run.atGate, run.terminal.then(() => { throw new Error('Missing held native result'); })]);
            assert.equal(status, 0);
            assert.equal(await run.worker.terminate(), 1);
            assert.equal(await run.terminal, null);
        } finally { await run.worker.terminate(); }
        assert.equal((await capture(candidate, document, {request})).status, 'completed');
    });
}
