// Run: node --experimental-vm-modules --test tests/Workspace.Tests/SyncTeXTransport/lifecycle.test.cjs
// Browser/FFI lifecycle mocks, not actual WASM or service-worker admission acceptance.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '../../..');
const bridgeSource = fs.readFileSync(path.join(root, 'src/Client/wwwroot/js/synctex-query-bridge.js'), 'utf8');
const workerSource = fs.readFileSync(path.join(root, 'src/Client/wwwroot/js/synctex-query-worker.js'), 'utf8');
const deferred = () => { let resolve, reject; const promise = new Promise((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; };
const tick = () => new Promise(resolve => setImmediate(resolve));
const id = n => n.toString(16).padStart(32, '0');
const meta = (n = 1, extra = {}) => JSON.stringify({ version: 1, requestId: id(n), deadlineMs: 15000,
    direction: 'forward', file: 'розділ/🙂.tex', line: 100, page: 0, h: 0, v: 0, ...extra });
const bytes = () => [new Uint8Array([1, 2]), new Uint8Array([3, 4]), new Uint8Array([0, 97, 115, 109, 1, 0, 0, 0])];
async function harness(options = {}) {
    const workers = [], revoked = [], created = [], timers = new Map();
    let invalidated, port, nextTimer = 0, provisions = 0, checks = 0, disposals = 0;
    const admission = {
        provision() { provisions++; return options.hold?.promise ?? Promise.resolve({ entry: '/__oop/native-worker/synctex?fixed=1' }); },
        check() { checks++; if (options.failCheck) throw new Error('refused'); },
        cancelPending() {}, dispose() { disposals++; }
    };
    class Worker {
        constructor(url) {
            this.url = url.href; this.terminations = 0; workers.push(this);
            options.construct?.(port);
        }
        postMessage(value) { this.sent = value; options.post?.(this, port); }
        terminate() { this.terminations++; }
    }
    class URLMock extends URL {
        static createObjectURL(blob) { const url = 'blob:owned-' + created.length; created.push({ url, blob }); options.blob?.(port); return url; }
        static revokeObjectURL(url) { revoked.push(url); }
    }
    const context = vm.createContext({ Uint8Array, ArrayBuffer, TextEncoder, URL: URLMock, Blob, Worker,
        location: { origin: 'https://app.test' },
        setTimeout(fn) { const key = ++nextTimer; timers.set(key, fn); return key; },
        clearTimeout(key) { timers.delete(key); }
    });
    const dependency = new vm.SyntheticModule(['createRestrictedWorkerAdmission'], function () {
        this.setExport('createRestrictedWorkerAdmission', (kind, callback) => {
            assert.equal(kind, 'synctex'); invalidated = callback;
            if (options.missingRole) throw new Error('fixed role required');
            return admission;
        });
    }, { context });
    const module = new vm.SourceTextModule(bridgeSource, { context });
    await module.link(() => dependency); await module.evaluate();
    port = module.namespace.createSyncTeXQueryPort();
    const reply = (worker, n = 1, values = {}) => worker.onmessage?.({ data: { kind: 'terminal', response: {
        version: 1, requestId: id(n), status: 'native', nativeStatus: 1, json: null, ...values
    } } });
    return { port, workers, revoked, created, timers, reply, invalidate: () => invalidated(),
        get provisions() { return provisions; }, get checks() { return checks; }, get disposals() { return disposals; } };
}

test('fixed admitted entry, exact bytes, selected fields only, every result terminates/revokes', async () => {
    const h = await harness();
    const input = bytes(), promise = h.port.query(meta(1, { credentials: 'SECRET', project: 'UNRELATED' }), ...input);
    input[0][0] = 99; input[1][0] = 99;
    await tick(); assert.equal(h.workers.length, 1);
    const worker = h.workers[0];
    assert.equal(worker.url, 'https://app.test/__oop/native-worker/synctex?fixed=1');
    assert.deepEqual([...worker.sent.map], [1, 2]);
    assert.equal(worker.sent.request.file, 'розділ/🙂.tex');
    assert.equal('credentials' in worker.sent.request, false); assert.equal('project' in worker.sent.request, false);
    assert.equal('loader' in worker.sent, false); assert.equal(worker.sent.loaderUrl, 'blob:owned-0');
    assert.deepEqual([...new Uint8Array(await h.created[0].blob.arrayBuffer())], [3, 4]);
    h.reply(worker, 1, { nativeLog: 'UNTRUSTED' }); const result = await promise;
    assert.equal(result.status, 'native'); assert.equal('nativeLog' in result, false);
    assert.equal(worker.terminations, 1); assert.deepEqual(h.revoked, ['blob:owned-0']);
    h.port.dispose(); h.port.dispose(); assert.equal(h.disposals, 1); assert.equal(h.timers.size, 0);
});

test('missing fixed role is unavailable, never a source/blob worker fallback', async () => {
    const h = await harness({ missingRole: true });
    assert.equal((await h.port.query(meta(), ...bytes())).status, 'unavailable');
    assert.equal(h.workers.length, 0); assert.equal(h.created.length, 0);
});

test('bounds typed bytes and metadata before provisioning/allocation', async () => {
    const h = await harness();
    const invalid = [[], 1048575, new Uint8Array(1048576), new Proxy(new Uint8Array([1]), {})];
    for (const value of invalid) assert.equal((await h.port.query(meta(), value, bytes()[1], bytes()[2])).status, 'failed');
    assert.equal((await h.port.query(' '.repeat(8193), ...bytes())).status, 'failed');
    assert.equal((await h.port.query(meta(1, { deadlineMs: 15001 }), ...bytes())).status, 'failed');
    assert.equal(h.provisions, 0); assert.equal(h.created.length, 0);
});

test('busy refuses without queue; admission cancellation cannot release unfinished preparation', async () => {
    const hold = deferred(), h = await harness({ hold });
    const first = h.port.query(meta(), ...bytes());
    assert.equal((await h.port.query(meta(2), ...bytes())).status, 'busy');
    h.port.cancel(id(1)); assert.equal((await first).status, 'cancelled');
    assert.equal((await h.port.query(meta(2), ...bytes())).status, 'busy');
    hold.resolve({ entry: '/__oop/native-worker/synctex' }); await tick();
    assert.equal(h.workers.length, 0); assert.equal(h.created.length, 0);
    const second = h.port.query(meta(2), ...bytes()); await tick(); h.reply(h.workers[0], 2);
    assert.equal((await second).status, 'native');
});

test('all cancelled IDs remain tombstoned, including cancellation before invocation', async () => {
    const h = await harness(); h.port.cancel(id(1)); h.port.cancel(id(2));
    assert.equal((await h.port.query(meta(1), ...bytes())).status, 'cancelled');
    assert.equal((await h.port.query(meta(2), ...bytes())).status, 'cancelled');
    assert.equal((await h.port.query(meta(1), ...bytes())).status, 'cancelled');
    assert.equal(h.workers.length, 0);
    for (let n = 3; n <= 65; n++) h.port.cancel(id(n));
    assert.equal((await h.port.query(meta(66), ...bytes())).status, 'disposed');
});

test('cancel promptly terminates and late callback cannot settle a newer query', async () => {
    const h = await harness(); const first = h.port.query(meta(1), ...bytes()); await tick();
    const old = h.workers[0], late = old.onmessage;
    h.port.cancel(id(1)); assert.equal((await first).status, 'cancelled');
    assert.equal(old.terminations, 1); assert.equal(h.revoked.length, 1);
    const second = h.port.query(meta(2), ...bytes()); await tick();
    late({ data: { kind: 'terminal', response: { version: 1, requestId: id(1), status: 'native', nativeStatus: 1, json: null } } });
    assert.equal(h.workers[1].terminations, 0); h.reply(h.workers[1], 2); assert.equal((await second).requestId, id(2));
});

test('deadline terminates, revokes and tombstones; dispose during provision cannot start late', async () => {
    const h = await harness(); const first = h.port.query(meta(), ...bytes()); await tick();
    [...h.timers.values()][0](); assert.equal((await first).status, 'timed-out');
    assert.equal(h.workers[0].terminations, 1); assert.equal(h.revoked.length, 1);
    assert.equal((await h.port.query(meta(), ...bytes())).status, 'cancelled');
    const hold = deferred(), late = await harness({ hold }); const pending = late.port.query(meta(), ...bytes());
    late.port.dispose(); assert.equal((await pending).status, 'disposed'); hold.resolve({ entry: '/never' }); await tick();
    assert.equal(late.created.length, 0); assert.equal(late.workers.length, 0);
});

for (const stage of ['construct', 'blob']) test(`reentrant cancellation during ${stage} still terminates/revokes ownership`, async () => {
    const h = await harness({ [stage]: port => port.cancel(id(1)) });
    const result = await h.port.query(meta(), ...bytes()); assert.equal(result.status, 'cancelled');
    assert.equal(h.revoked.length, 1);
    assert.equal(h.workers.length, stage === 'construct' ? 1 : 0);
    if (h.workers.length) assert.equal(h.workers[0].terminations, 1);
});

for (const mode of ['error', 'messageerror', 'wrong-id', 'oversize', 'invalidate']) test(`worker ${mode} settles and cleans resources`, async () => {
    const h = await harness(), promise = h.port.query(meta(), ...bytes()); await tick(); const worker = h.workers[0];
    if (mode === 'error') worker.onerror({ message: 'SECRET native log' });
    else if (mode === 'messageerror') worker.onmessageerror({});
    else if (mode === 'wrong-id') h.reply(worker, 2);
    else if (mode === 'oversize') h.reply(worker, 1, { nativeStatus: 0, json: new Uint8Array(32769) });
    else h.invalidate();
    const result = await promise; assert.ok(['failed', 'unavailable'].includes(result.status));
    assert.equal(worker.terminations, 1); assert.equal(h.revoked.length, 1);
});

async function workerHarness(options = {}) {
    const calls = [], messages = [], output = new TextEncoder().encode('{"synthetic":"🙂"}');
    const heap = new Uint8Array(2 * 1048576); let pointer = 8, factoryOptions;
    const module = {
        HEAPU8: heap,
        _malloc(size) { const p = pointer; pointer += size; calls.push(['malloc', size]); return p; },
        _free(p) { calls.push(['free', p]); },
        _sq_version() { return 1; },
        _sq_load(p, n) { calls.push(['load', [...heap.slice(p, p + n)]]); return options.load ?? 0; },
        _sq_forward(p, n, line, column, page) { calls.push(['forward', new TextDecoder().decode(heap.slice(p, p + n)), line, column, page]); return options.query ?? 0; },
        _sq_inverse(page, h, v) { calls.push(['inverse', page, h, v]); return options.query ?? 0; },
        _sq_result_length() { return options.size ?? output.length; },
        _sq_result_copy(p, n) { calls.push(['copy', n]); heap.set(output, p); return options.copy ?? 0; },
        _sq_close() { calls.push(['close']); return 0; }
    };
    const imports = { fixture: {} }, instance = { fixture: true };
    const self = { createSyncTeXQuery: value => new Promise(resolve => {
        factoryOptions = value;
        assert.equal(typeof value.instantiateWasm, 'function');
        const immediate = value.instantiateWasm(imports, actual => { assert.equal(actual, instance); resolve(module); });
        assert.equal(Object.keys(immediate).length, 0);
    }),
        postMessage(value) { messages.push(value); } };
    const context = vm.createContext({ self, Uint8Array, ArrayBuffer, TextEncoder,
        WebAssembly: { async instantiate(binary, actualImports) {
            calls.push(['instantiate', [...binary]]); assert.equal(actualImports, imports);
            if (options.instantiateFailure) throw new Error('synthetic WASM initialization failure');
            return { instance };
        } },
        importScripts(url) { assert.equal(url, 'blob:approved'); calls.push(['import']); } });
    vm.runInContext(workerSource, context);
    const request = { version: 1, requestId: id(1), direction: 'forward', file: 'розділ/🙂.tex', line: 100, page: 0, h: 12.5, v: -5, ...options.request };
    const event = { data: { request, loaderUrl: 'blob:approved', map: bytes()[0], wasm: bytes()[2] } };
    await self.onmessage(event); await self.onmessage(event); // one-use even after errors
    return { calls, messages, output, factoryOptions };
}

test('worker FFI order, supplied WASM instantiation, quiet native logs, exact output bytes, one use', async () => {
    const h = await workerHarness(); assert.equal(h.messages.length, 1);
    assert.deepEqual(h.calls.find(v => v[0] === 'forward'), ['forward', 'розділ/🙂.tex', 100, 0, -1]);
    assert.deepEqual(h.calls.find(v => v[0] === 'load'), ['load', [1, 2]]);
    assert.deepEqual([...h.messages[0].response.json], [...h.output]);
    assert.equal(h.messages[0].response.nativeStatus, 0);
    assert.deepEqual([...h.factoryOptions.wasmBinary], [...bytes()[2]]);
    assert.deepEqual(h.calls.find(v => v[0] === 'instantiate'), ['instantiate', [...bytes()[2]]]);
    assert.equal(h.factoryOptions.print('SECRET'), undefined); assert.equal(h.factoryOptions.printErr('SECRET'), undefined);
    assert.equal(h.calls.filter(v => v[0] === 'close').length, 1);
    assert.equal(h.calls.filter(v => v[0] === 'malloc').length, h.calls.filter(v => v[0] === 'free').length);
});

test('rejected supplied WASM initialization finishes without native calls or network fallback', async () => {
    const h = await workerHarness({ instantiateFailure: true });
    assert.equal(h.messages.length, 1); assert.equal(h.messages[0].response.status, 'failed');
    assert.equal(h.messages[0].response.json, null);
    assert.equal(h.calls.filter(value => ['load', 'forward', 'inverse', 'malloc'].includes(value[0])).length, 0);
});

test('inverse preserves upstream visible coordinates with no DOM conversion', async () => {
    const h = await workerHarness({ request: { direction: 'inverse', file: null, line: 0, page: 512 } });
    assert.deepEqual(h.calls.find(v => v[0] === 'inverse'), ['inverse', 512, 12.5, -5]);
});

for (const [options, status, native] of [
    [{ query: 1 }, 'native', 1], [{ load: -4 }, 'native', -4], [{ load: 1 }, 'failed', null],
    [{ size: 32769 }, 'failed', null], [{ size: 0 }, 'failed', null], [{ copy: -8 }, 'native', -8]
]) test(`worker empty/error/copy/size guard ${JSON.stringify(options)} closes without output`, async () => {
    const h = await workerHarness(options), result = h.messages[0].response;
    assert.equal(result.status, status); assert.equal(result.nativeStatus, native); assert.equal(result.json, null);
    assert.equal(h.calls.filter(v => v[0] === 'close').length, 1);
    assert.equal(h.calls.filter(v => v[0] === 'malloc').length, h.calls.filter(v => v[0] === 'free').length);
});
