'use strict';

// Restricted bridge admission with a modeled service worker. With explicit
// fixtures, also run the unchanged product worker and pinned WASM in Node.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const {webcrypto} = require('node:crypto');
const {Worker, parentPort, workerData} = require('node:worker_threads');
const {test} = require('node:test');
const {harness, hash} = require('./restricted-worker-test-helpers.cjs');

const standard = 8 * 1024 * 1024, expanded = 12 * 1024 * 1024;
const loaderHash = 'ea2cebb93f554788751e58554b6e16a094ee6d0883774cb697cbb4a5ce8ec38c';
const wasmHash = 'b1630e6501e7715b4e0870a059f4e9b8b5acc4c96aa2af0e4ecacb04071622d7';
const source = path.join(__dirname, '../../src/Client/wwwroot/js/pdf-port-worker.js');

function frame(document, pageIndex = 0) {
    return {version: 2, requestId: 'expanded-pdf-request', epoch: 1, documentSha256: hash(document),
        loaderSha256: loaderHash, wasmSha256: wasmHash, pageIndex, inputCapacity: 'compiled-12-mib',
        search: null, raster: true, width: 128, height: 192, inspectActions: false,
        budget: {inputBytes: expanded, pages: 512, textUnits: 100000, searchHits: 256,
            rasterPixels: 4000000, outputBytes: 16777216, liveHandles: 64, deadlineMs: 8000}};
}

function inputAt(bytes, size) {
    const at = bytes.lastIndexOf(Buffer.from('startxref'));
    assert.ok(at > 0 && size >= bytes.length);
    return Buffer.concat([bytes.subarray(0, at), Buffer.alloc(size - bytes.length, 32), bytes.subarray(at)]);
}

async function native(directory, data, ownedUrl, capability = 'actual') {
    const loader = path.join(directory, 'e1.js');
    assert.deepEqual(Buffer.from(await ownedUrl.blob.arrayBuffer()), fs.readFileSync(loader));
    const worker = new Worker(__filename, {workerData: {loader, capability, loaderUrl: ownedUrl.url}});
    let timer;
    try {
        return await new Promise((resolve, reject) => {
            timer = setTimeout(() => reject(new Error('PDF native worker deadline')), 10000);
            worker.on('error', reject);
            worker.on('exit', () => reject(new Error('PDF native worker stopped before terminal response')));
            worker.on('message', message => { if (message.kind === 'terminal') resolve(message.response); });
            worker.postMessage(data);
        });
    } finally { clearTimeout(timer); await worker.terminate(); }
}

function assertCompleted(response, request, count) {
    assert.equal(response.status, 'completed');
    assert.equal(response.nativeStatus, 0);
    assert.equal(response.enteredNative, true);
    assert.equal(response.liveAfter, 0);
    assert.equal(response.pageCount, count);
    for (const key of ['requestId', 'documentSha256', 'epoch', 'loaderSha256', 'wasmSha256'])
        assert.equal(response[key], request[key]);
    const text = JSON.parse(response.textJson);
    assert.ok(text.characterCount > 0 && text.utf16.length > 0);
    const raster = Buffer.from(response.raster);
    assert.equal(raster.readUInt32LE(0), 0x31424752);
    assert.equal(raster.length, 16 + 128 * 192 * 4);
    assert.ok(raster.subarray(16).some((v, index) => index % 4 !== 3 && v < 250));
}

if (parentPort) {
    globalThis.self = globalThis;
    if (!globalThis.crypto) globalThis.crypto = webcrypto;
    globalThis.postMessage = (message, transfer) => parentPort.postMessage(message, transfer);
    // Node worker threads cannot resolve a parent thread's Blob URL. The
    // caller checks the parent Blob byte-for-byte before this import adapter.
    globalThis.importScripts = url => {
        assert.equal(url, workerData.loaderUrl);
        const factory = require(workerData.loader);
        globalThis.E1 = async options => {
            const module = await factory(options);
            if (workerData.capability === 'absent') delete module._e1_input_bytes_limit;
            if (workerData.capability === 'wrong') module._e1_input_bytes_limit = () => standard;
            return module;
        };
    };
    vm.runInThisContext(fs.readFileSync(source, 'utf8'), {filename: source});
    parentPort.on('message', data => globalThis.onmessage({data}));
} else {
    test('large admission demands exact capacity and declared tuple before cloning and hashing', async () => {
        const h = harness(), port = h.createPdfPort();
        const document = Buffer.of(1, 2), loader = Buffer.alloc(14243), wasm = Buffer.alloc(3725611);
        const inspect = (request, bytes = document) => port.inspect(JSON.stringify(request), bytes, loader, wasm);
        try {
            for (const change of [
                {inputCapacity: undefined}, {inputCapacity: null}, {inputCapacity: 'forged'},
                {loaderSha256: hash(loader)}, {wasmSha256: hash(wasm)},
                {budget: {...frame(document).budget, inputBytes: expanded + 1}}
            ]) assert.equal((await inspect({...frame(document), ...change})).status, 'transport-error');
            assert.equal((await inspect(frame(document), Buffer.alloc(expanded + 1))).status, 'transport-error');
            assert.equal(h.stats.byteAllocations, 0);
            assert.equal(h.stats.hashes, 0);
            assert.equal((await inspect(frame(document))).status, 'unavailable', 'correct declared tuple cannot authenticate forged bytes');
            assert.equal(h.stats.workers.length, 0);
            assert.equal(h.stats.urls.length, 0);
        } finally { port.dispose(); h.api.dispose(); }
    });

    const renderer = process.env.PDF_LARGE_RENDERER_DIR;
    const fixture = process.env.PDF_LARGE_SYNTHETIC_PATH;
    const original = process.env.PDF_LARGE_ORIGINAL_PATH;
    const skip = !renderer || !fixture ? 'Requires explicit pinned native renderer and synthetic PDF' : false;
    const load = () => {
        const loader = fs.readFileSync(path.join(renderer, 'e1.js')), wasm = fs.readFileSync(path.join(renderer, 'e1.wasm'));
        assert.equal(hash(loader), loaderHash); assert.equal(hash(wasm), wasmHash);
        return {loader, wasm};
    };

    test('actual worker combines expanded bridge admission, native text and raster at the exact limit', {skip}, async () => {
        const pdf = fs.readFileSync(fixture);
        assert.equal(hash(pdf), '9731e35b54ea1a11474ae53c3b7375842d07a9d8370a15f3822c73de6c9dc0fe');
        const document = inputAt(pdf, expanded), runtime = load(), rendered = [];
        const h = harness(), port = h.createPdfPort();
        try {
            for (const page of [0, 3]) {
                const request = frame(document, page);
                const index = h.stats.workers.length;
                const pending = port.inspect(JSON.stringify(request), document, runtime.loader, runtime.wasm);
                for (let i = 0; i < 120 && !h.stats.workers[index]?.messages.length; i++)
                    await new Promise(resolve => setTimeout(resolve, 5));
                const worker = h.stats.workers[index];
                assert.ok(worker?.messages.length);
                const input = worker.messages[0];
                const ownedUrl = h.stats.urls[index];
                assert.equal(input.loaderUrl, ownedUrl.url);
                const response = await native(renderer, input, ownedUrl);
                worker.onmessage({data: {kind: 'terminal', response}});
                assert.deepEqual(await pending, response);
                assertCompleted(response, request, 4);
                assert.equal(worker.terminated, 1);
                rendered.push({request, response: {...response, raster: Buffer.from(response.raster).toString('base64')}});
            }
            assert.equal(h.stats.urls.length, 2);
            assert.deepEqual(h.stats.revoked, h.stats.urls.map(({url}) => url));
            const replay = process.env.PDF_LARGE_REPLAY_DIR;
            if (replay) {
                fs.mkdirSync(replay, {recursive: true});
                fs.writeFileSync(path.join(replay, 'synthetic-12-mib.pdf'), document);
                fs.writeFileSync(path.join(replay, 'synthetic-native-replies.json'), JSON.stringify(rendered));
            }
        } finally { port.dispose(); h.api.dispose(); }
    });

    test('actual worker refuses oversized input, forged bytes/URL and absent or wrong native capability', {skip}, async () => {
        const pdf = fs.readFileSync(fixture), runtime = load(), request = frame(pdf);
        const ownedUrl = {url: 'blob:trusted-fixture', blob: new Blob([runtime.loader])};
        const input = {request, document: pdf, ...runtime, loaderUrl: ownedUrl.url};
        const forged = Buffer.from(runtime.wasm); forged[forged.length - 1] ^= 1;
        for (const [data, capability] of [
            [{...input, document: inputAt(pdf, expanded + 1)}, 'actual'],
            [{...input, wasm: forged}, 'actual'],
            [{...input, loaderUrl: 'https://example.invalid/e1.js'}, 'actual'],
            [input, 'absent'], [input, 'wrong']
        ]) {
            const response = await native(renderer, data, ownedUrl, capability);
            assert.equal(response.status, 'transport-error');
            assert.equal(response.enteredNative, null);
            assert.equal(response.liveAfter, 0);
            assert.ok(!response.nativeCalls.includes('e1_open'));
        }
    });

    test('actual worker renders and extracts first and last pages of the private 142-page compilation',
        {skip: !renderer || !original ? 'Requires explicit exact renderer and private approved PDF' : false}, async () => {
        const document = fs.readFileSync(original), runtime = load();
        assert.equal(document.length, 11413886);
        assert.equal(hash(document), '30bc40f2c0049a6c833c600e1eb5a9b65c93806b01c84d768349571daf9ea316');
        const ownedUrl = {url: 'blob:trusted-fixture', blob: new Blob([runtime.loader])};
        for (const page of [0, 141]) {
            const request = frame(document, page);
            const response = await native(renderer, {request, document, ...runtime, loaderUrl: ownedUrl.url}, ownedUrl);
            assertCompleted(response, request, 142);
            assert.equal(JSON.parse(response.textJson).characterCount, page === 0 ? 810 : 1694);
        }
    });
}
