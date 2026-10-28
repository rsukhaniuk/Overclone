// MODELED shared-port/PDF bridge ownership and wire checks, NOT renderer/native
// compatibility. Fake Workers record bytes only; no vendor loader/WASM is run.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { createHash, webcrypto } = require('node:crypto');
const { harness, frame, tick, hash } = require('./restricted-worker-test-helpers.cjs');
const raw = { document: new Uint8Array([10, 11]), loader: new Uint8Array([12, 13]), wasm: new Uint8Array([14, 15, 16, 17, 18, 19, 20, 21]) };
function request(id = 'pdf-request', changes = {}) {
    return { version: 2, inspectActions: false, requestId: id, documentSha256: hash(raw.document),
        loaderSha256: hash(raw.loader), wasmSha256: hash(raw.wasm), epoch: 1, pageIndex: 0,
        search: null, raster: false, width: 0, height: 0, budget: { deadlineMs: 10000, inputBytes: 64 }, ...changes };
}
function inspect(port, metadata = request(), data = raw) {
    return port.inspect(JSON.stringify(metadata), data.document, data.loader, data.wasm);
}
async function until(predicate, label) {
    for (let i = 0; i < 100 && !predicate(); i++) await new Promise(resolve => setTimeout(resolve, 2));
    assert.ok(predicate(), label);
}
function terminal(worker, changes = {}) {
    const r = worker.messages[0].request;
    const response = { version: 2, requestId: r.requestId, epoch: r.epoch, documentSha256: r.documentSha256,
        loaderSha256: r.loaderSha256, wasmSha256: r.wasmSha256, status: 'unavailable', enteredNative: null,
        actionState: null, hasCapabilitiesApi: false, hasActionsApi: false, capabilitiesJson: null, actionsJson: null,
        nativeCalls: [], ...changes };
    worker.onmessage({ data: { kind: 'terminal', response } });
}
async function clean(h, ports = []) {
    for (const port of ports) port.dispose(); h.api.dispose(); await tick();
    assert.equal(h.sw.count(), 0); assert.equal(h.controller.count(), 0); assert.equal(h.timers.size, 0);
    assert.ok(h.stats.ports.every(p => p.closed === 1));
    assert.equal(h.stats.urls.length, h.stats.revoked.length);
    assert.ok(h.stats.workers.every(w => w.terminated === 1));
}
function noDispatch(h) { assert.equal(h.stats.urls.length, 0); assert.equal(h.stats.workers.length, 0); }
async function actualHashOwnership(mode) {
    const h = harness(), port = h.createPdfPort(), jobs = [], unhandled = [];
    const subtle = webcrypto.subtle, original = subtle.digest;
    const descriptor = Object.getOwnPropertyDescriptor(subtle, 'digest');
    const rejected = error => unhandled.push(error);
    process.on('unhandledRejection', rejected);
    // Control the actual promises returned by the modeled digest API, not just
    // the logical inspect wait. Restore the shared Node API on every path.
    subtle.digest = function (algorithm, bytes) {
        if (jobs.length >= 3) return original.call(this, algorithm, bytes);
        let resolve, reject;
        const promise = new Promise((a, b) => { resolve = a; reject = b; });
        const answer = createHash('sha256').update(bytes).digest();
        jobs.push({ promise, resolve: () => resolve(answer.buffer.slice(answer.byteOffset, answer.byteOffset + answer.byteLength)), reject });
        return promise;
    };
    const blocked = async id => {
        const before = { copies: h.stats.byteAllocations, hashes: h.stats.hashes, workers: h.stats.workers.length };
        const retry = inspect(port, request(id));
        assert.deepEqual({ copies: h.stats.byteAllocations, hashes: h.stats.hashes, workers: h.stats.workers.length }, before,
            'unfinished actual preparation must refuse before cloning/hashing/Worker');
        assert.equal((await retry).status, 'unavailable');
    };
    try {
        const pending = inspect(port, request('held-' + mode));
        await until(() => jobs.length === 3, 'three actual digest tasks held');
        if (mode === 'failure') jobs[0].reject(new Error('modeled digest rejection'));
        else if (mode === 'timeout') h.fireTimer(10000);
        else if (mode === 'controller') h.changeController();
        else port.cancel('held-' + mode);
        assert.equal((await pending).status, { failure: 'transport-error', cancel: 'cancelled', timeout: 'timed-out', controller: 'unavailable' }[mode]);
        await blocked('while-all-or-two-pending');
        if (mode !== 'failure') jobs[0].reject(new Error('modeled late digest rejection after cancellation'));
        await Promise.allSettled([jobs[0].promise]); await tick();
        jobs[1].resolve(); await Promise.allSettled([jobs[1].promise]); await tick();
        await blocked('while-last-digest-pending');
        jobs[2].resolve(); await Promise.allSettled(jobs.map(job => job.promise)); await tick();
        noDispatch(h); assert.deepEqual(unhandled, []);
        if (mode === 'controller') h.sw.controller = h.controller; // modeled matching controller restored, no retry/fallback in product
        const retry = inspect(port, request('after-actual-retirement'));
        await until(() => h.stats.workers.length === 1, 'one explicit retry after all actual tasks settled');
        terminal(h.stats.workers[0]); assert.equal((await retry).requestId, 'after-actual-retirement');
    } finally {
        for (const job of jobs) job.resolve();
        await Promise.allSettled(jobs.map(job => job.promise)); await tick();
        if (descriptor) Object.defineProperty(subtle, 'digest', descriptor); else delete subtle.digest;
        process.removeListener('unhandledRejection', rejected);
        await clean(h, [port]);
    }
    assert.deepEqual(unhandled, []);
}
const deadline = setTimeout(() => { console.error('PDF/shared admission test deadline exceeded'); process.exit(1); }, 15000);
(async () => {
    for (const mode of ['cancel', 'timeout', 'controller', 'failure']) await actualHashOwnership(mode);
    const cap = harness();
    assert.throws(() => cap.createAdmission('arbitrary')); assert.equal(cap.sw.count(), 0);
    const slots = Array.from({ length: 16 }, (_, i) => cap.createAdmission(i % 2 ? 'pdf' : 'compile'));
    assert.equal(cap.sw.count(), 1); assert.throws(() => cap.createAdmission('pdf'));
    for (const slot of slots) slot.dispose(); await clean(cap);

    const shared = harness(), sa = shared.createAdmission('pdf'), sb = shared.createAdmission('pdf');
    const a = sa.provision(); assert.equal(sa.provision(), a);
    const [ba, bb] = await Promise.all([a, sb.provision()]); assert.equal(ba, bb);
    assert.equal(shared.stats.messages, 1); assert.equal(shared.stats.fetches, 1);
    sa.dispose(); assert.equal(sb.check(bb), bb, 'disposal cannot invalidate another same-kind port'); sb.dispose(); await clean(shared);
    const waiting = harness({ dropReply: true }), wa = waiting.createAdmission('pdf'), wb = waiting.createAdmission('pdf');
    const pa = wa.provision(), pb = wb.provision(), aRejected = assert.rejects(pa), bRejected = assert.rejects(pb);
    await until(() => waiting.stats.messages === 1, 'shared pending contract');
    wa.dispose(); await aRejected;
    assert.ok(waiting.stats.ports.every(p => p.closed === 0), 'other waiter keeps shared channel alive');
    waiting.changeController(); await bRejected;
    waiting.control.dropReply = false; waiting.sw.controller = waiting.controller;
    await wb.provision(); wb.dispose(); await clean(waiting);

    const mixed = harness({ dropKinds: new Set(['pdf']) }); await mixed.api.provision();
    const mixedPdf = mixed.createPdfPort(), mixedPending = inspect(mixedPdf);
    await until(() => mixed.stats.kinds.includes('pdf'), 'PDF pending alongside compiler');
    mixedPdf.dispose(); assert.equal((await mixedPending).status, 'disposed');
    const compile = mixed.api.execute(frame()); mixed.stats.workers.at(-1).onerror();
    assert.equal((await compile).outcome, 'worker-error'); assert.equal(mixed.sw.count(), 1, 'compiler listener survives PDF disposal');
    await clean(mixed, [mixedPdf]);

    const invalid = harness(), invalidPort = invalid.createPdfPort();
    await assert.rejects(invalidPort.inspect('{bad', [], [], []));
    await assert.rejects(invalidPort.inspect('null', [], [], []));
    for (const change of [{ version: 1 }, { inspectActions: null }, { requestId: '' }, { epoch: 0 }, { budget: { deadlineMs: 30001 } }])
        assert.equal((await inspect(invalidPort, request('bad', change))).status, 'transport-error');
    invalidPort.cancel('early'); assert.equal((await inspect(invalidPort, request('early'))).status, 'cancelled');
    noDispatch(invalid); assert.equal(invalid.stats.messages, 0); await clean(invalid, [invalidPort]);
    const bounds = harness(), boundsPort = bounds.createPdfPort();
    let observedProperties = 0;
    const plainArray = new Proxy([10, 11], { get(target, key) { observedProperties++; return Reflect.get(target, key); } });
    const proxyBytes = new Proxy(raw.document, { get() { observedProperties++; throw new Error('must not clone/read a proxy payload'); } });
    // One inert zero-filled backing buffer supplies all three over-limit views;
    // none of these inputs may be cloned, hashed, admitted or sent to a Worker.
    const oversized = new Uint8Array(16777217);
    for (const data of [
        { ...raw, document: plainArray }, { ...raw, document: 16777217 }, { ...raw, document: proxyBytes },
        { ...raw, document: new Uint8Array(0) }, { ...raw, document: oversized.subarray(0, 8388609) },
        { ...raw, loader: oversized.subarray(0, 1048577) }, { ...raw, loader: new Uint8Array(0) },
        { ...raw, wasm: oversized }, { ...raw, wasm: raw.wasm.subarray(0, 7) },
    ]) assert.equal((await inspect(boundsPort, request(), data)).status, 'transport-error');
    for (const inputBytes of [0, 1, 8388609, 1.5, null, '64'])
        assert.equal((await inspect(boundsPort, request('budget', { budget: { deadlineMs: 10000, inputBytes } }))).status, 'transport-error');
    for (const field of ['documentSha256', 'loaderSha256', 'wasmSha256'])
        for (const value of [null, 'A'.repeat(64), 'a'.repeat(63)])
            assert.equal((await inspect(boundsPort, request('digest', { [field]: value }))).status, 'transport-error');
    assert.equal(observedProperties, 0, 'plain Array/proxy properties are not consulted as byte lengths');
    assert.equal(bounds.stats.byteAllocations, 0, 'reject before owned byte allocation/clone');
    assert.equal(bounds.stats.hashes, 0); assert.equal(bounds.stats.messages, 0); noDispatch(bounds); await clean(bounds, [boundsPort]);
    const absent = harness({ noController: true }), absentPort = absent.createPdfPort();
    assert.equal((await inspect(absentPort)).status, 'unavailable'); noDispatch(absent); await clean(absent, [absentPort]);
    for (const field of ['documentSha256', 'loaderSha256', 'wasmSha256']) {
        const h = harness(), port = h.createPdfPort();
        assert.equal((await inspect(port, request('wrong-hash', { [field]: '0'.repeat(64) }))).status, 'unavailable');
        assert.equal(h.stats.messages, 0); noDispatch(h); await clean(h, [port]);
    }
    for (const changeReply of [r => ({ ...r, kind: 'compile' }), r => ({ ...r, source: '/js/compile-port-worker.js' }),
        r => ({ ...r, entry: r.entry.replace('/pdf?', '/compile?') }), r => ({ ...r, policy: "script-src 'self'" })]) {
        const h = harness({ changeReply }), port = h.createPdfPort();
        assert.equal((await inspect(port)).status, 'unavailable'); noDispatch(h); await clean(h, [port]);
    }

    for (const asBuffers of [false, true]) {
        const h = harness(), port = h.createPdfPort(); let release;
        h.control.hashGate = new Promise(resolve => { release = resolve; });
        const inputs = Object.fromEntries(Object.entries(raw).map(([k, bytes]) => [k, asBuffers ? bytes.slice().buffer : bytes.slice()]));
        const promise = inspect(port, request('snapshot', { loaderUrl: 'https://unselected.invalid/not-executable', source: 'blob:unselected' }), inputs);
        for (const value of Object.values(inputs)) new Uint8Array(value.buffer || value).fill(99);
        h.control.hashGate = null; release();
        await until(() => h.stats.workers.length === 1, 'private snapshots admitted');
        const worker = h.stats.workers[0], sent = worker.messages[0];
        assert.ok(worker.url.includes('/__oop/native-worker/pdf?')); assert.ok(!worker.url.startsWith('blob:'));
        assert.equal(h.stats.urls.length, 1); assert.equal(sent.loaderUrl, h.stats.urls[0].url);
        assert.notEqual(sent.loaderUrl, sent.request.loaderUrl, 'metadata may not choose executable URL');
        for (const key of ['document', 'loader', 'wasm']) assert.deepEqual(Array.from(sent[key]), Array.from(raw[key]));
        assert.deepEqual(Buffer.from(await h.stats.urls[0].blob.arrayBuffer()), Buffer.from(raw.loader));
        const saved = worker.onmessage;
        saved({ data: { kind: 'before-pdf-entry' } }); assert.equal(worker.terminated, 0);
        terminal(worker); assert.equal((await promise).status, 'unavailable');
        assert.deepEqual(h.stats.order.slice(-2), ['terminate', 'revoke']);
        saved({ data: { kind: 'terminal', response: {} } }); assert.equal(worker.terminated, 1); assert.equal(h.stats.revoked.length, 1);
        await clean(h, [port]);
    }
    const dual = harness(), portA = dual.createPdfPort(), portB = dual.createPdfPort();
    const resultA = inspect(portA, request('a')), resultB = inspect(portB, request('b'));
    await until(() => dual.stats.workers.length === 2, 'independent PDF ports'); assert.equal(dual.stats.messages, 1);
    const workerB = dual.stats.workers.find(worker => worker.messages[0].request.requestId === 'b');
    assert.ok(workerB, 'identify the second request independently of asynchronous hash completion order');
    assert.equal((await inspect(portB, request('busy'))).status, 'unavailable', 'active1 per port');
    portA.dispose(); assert.equal((await resultA).status, 'disposed'); assert.equal(workerB.terminated, 0);
    terminal(workerB); assert.equal((await resultB).status, 'unavailable'); await clean(dual, [portA, portB]);

    const hashCancel = harness(), hashPort = hashCancel.createPdfPort(); let releaseHash;
    hashCancel.control.hashGate = new Promise(resolve => { releaseHash = resolve; });
    const hashPending = inspect(hashPort); hashPort.dispose(); assert.equal((await hashPending).status, 'disposed');
    hashCancel.control.hashGate = null; releaseHash(); await tick(); noDispatch(hashCancel); await clean(hashCancel, [hashPort]);
    const pre = harness({ dropReply: true }), prePort = pre.createPdfPort(), prePending = inspect(prePort, request('cancel-prep'));
    await until(() => pre.stats.messages === 1, 'pending PDF admission'); prePort.cancel('cancel-prep');
    assert.equal((await prePending).status, 'cancelled'); noDispatch(pre); pre.control.dropReply = false;
    const later = inspect(prePort, request('later')); await until(() => pre.stats.workers.length === 1, 'explicit later request not poisoned');
    terminal(pre.stats.workers[0]); assert.equal((await later).requestId, 'later'); await clean(pre, [prePort]);
    const preparingTimeout = harness({ dropReply: true }), timeoutPort = preparingTimeout.createPdfPort(), timeoutWait = inspect(timeoutPort);
    await until(() => preparingTimeout.stats.messages === 1, 'pending timeout contract'); preparingTimeout.fireTimer(10000);
    assert.equal((await timeoutWait).status, 'timed-out'); noDispatch(preparingTimeout); await clean(preparingTimeout, [timeoutPort]);

    for (const mode of ['cancel', 'timeout', 'dispose', 'controller', 'error', 'messageerror']) {
        const h = harness(), port = h.createPdfPort(), promise = inspect(port);
        await until(() => h.stats.workers.length === 1, 'active PDF ' + mode);
        const worker = h.stats.workers[0]; worker.onmessage({ data: { kind: 'before-pdf-entry' } });
        if (mode === 'cancel') port.cancel('pdf-request');
        if (mode === 'timeout') h.fireTimer(10000);
        if (mode === 'dispose') port.dispose();
        if (mode === 'controller') h.changeController();
        if (mode === 'error') worker.onerror();
        if (mode === 'messageerror') worker.onmessageerror();
        const result = await promise;
        assert.equal(result.status, { cancel: 'cancelled', timeout: 'timed-out', dispose: 'disposed', controller: 'unavailable', error: 'transport-error', messageerror: 'transport-error' }[mode]);
        assert.equal(result.enteredNative, null, 'notification is not native preemption proof');
        assert.deepEqual(h.stats.order.slice(-2), ['terminate', 'revoke']); await clean(h, [port]);
    }
    for (const field of ['requestId', 'epoch', 'documentSha256', 'loaderSha256', 'wasmSha256']) {
        const h = harness(), port = h.createPdfPort(), promise = inspect(port);
        await until(() => h.stats.workers.length === 1, 'response correlation'); terminal(h.stats.workers[0], { [field]: 'mismatch' });
        assert.equal((await promise).status, 'transport-error'); await clean(h, [port]);
    }
    for (const options of [{ workerThrows: true }, { urlThrows: true }, { postThrows: true }, { terminateThrows: true }, { revokeThrows: true }]) {
        const h = harness(options), port = h.createPdfPort(), promise = inspect(port);
        if (options.terminateThrows || options.revokeThrows) {
            await until(() => h.stats.workers.length === 1, 'cleanup fault'); terminal(h.stats.workers[0]);
        }
        assert.equal((await promise).status, 'transport-error');
        const revocations = h.stats.revoked.length; await clean(h, [port]); assert.equal(h.stats.revoked.length, revocations, 'ownership cleared before revoke');
    }
    for (const hook of ['onUrl', 'onConstruct']) {
        const h = harness(), port = h.createPdfPort(); h.control[hook] = () => port.dispose();
        assert.equal((await inspect(port)).status, 'disposed');
        assert.equal(h.stats.revoked.length, 1);
        if (hook === 'onUrl') assert.equal(h.stats.workers.length, 0, 'cancellation before constructor cannot launch late worker');
        else { assert.equal(h.stats.workers[0].messages.length, 0); assert.deepEqual(h.stats.order, ['terminate', 'revoke']); }
        await clean(h, [port]);
    }
    const workerSource = fs.readFileSync(path.join(__dirname, '../../src/Client/wwwroot/js/pdf-port-worker.js'), 'utf8');
    assert.ok(!/\bexport\b/.test(workerSource)); assert.equal((workerSource.match(/^pdfWorkerMain\(\);$/gm) || []).length, 1);
    assert.ok(!workerSource.includes('createObjectURL') && !workerSource.includes('revokeObjectURL'));
    let nativeImports = 0;
    const self = {};
    vm.runInNewContext(workerSource, { self, importScripts() { nativeImports++; throw new Error('must not import before a request'); } }, { timeout: 1000 });
    assert.equal(typeof self.onmessage, 'function'); assert.equal(nativeImports, 0); // no message sent, no native body run
    console.log('PDF/shared admission modeled contracts PASS: bounded/shared/disposable ports and compiler isolation, private hashes/clones, classic entry, metadata URL ignored, active1/early/late/correlation/timeouts, parent loader ownership/termination/revoke on all paths. ZERO native/vendor/WASM execution.');
})().catch(error => { console.error(error); process.exitCode = 1; }).finally(() => clearTimeout(deadline));
