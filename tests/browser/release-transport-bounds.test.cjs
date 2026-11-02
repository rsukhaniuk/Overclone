'use strict';
// R4 B1: actual product source, controlled browser APIs. NO native/WASM execution,
// real CSP, network, browser preemption, project storage or isolation acceptance.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { createHash, webcrypto } = require('node:crypto');
const root = path.resolve(__dirname, '../..');
const source = name => fs.readFileSync(path.join(root, 'src/Client/wwwroot/js', name), 'utf8');
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const origin = 'https://r4-transport.test'; // never contacted; all browser APIs below are controlled
const policy = "default-src 'none'; script-src blob: 'wasm-unsafe-eval'; connect-src 'none'; worker-src 'none'; object-src 'none'";
const bodies = { compile: 'compile-port-worker.js', dvi: 'dvi-port-worker.js', xetex: 'unicode-tex-port-worker.js',
    luatex: 'unicode-tex-port-worker.js', pdf: 'pdf-port-worker.js', synctex: 'synctex-query-worker.js' };
const tick = () => new Promise(resolve => setImmediate(resolve));
const tiny = () => new Uint8Array([1, 2, 3, 4, 5, 6, 7, 8]);
const product = name => source(name)
    .replace(/^import \{ createRestrictedWorkerAdmission \} from ['"]\.\/restricted-worker-admission\.js['"];\r?\n/, '')
    .replaceAll('export function ', 'function ');
function csharp(file, pattern) {
    const text = fs.readFileSync(path.join(root, file), 'utf8'), match = text.match(pattern);
    assert.ok(match, 'source-bound constant extraction: ' + file);
    return Number(match[1]);
}
const csPdf = 'src/Client/Pdf/PdfContracts.cs';
const limits = {
    files: csharp('src/Client/Compilation/Native/NativeCompilerSuiteContract.cs', /Files = (\d+)/) + 192,
    outputCount: Number(source('compile-port-worker.js').match(/d\.outputs\.length>128/)[0].match(/\d+/)[0]),
    output: csharp(csPdf, /InputBytes = (\d+)/),
    pdfOutput: csharp(csPdf, /OutputBytes = (\d+)/),
    pdfPages: csharp(csPdf, /MaxDocumentPages = (\d+)/),
    queryMap: csharp('src/Client/Compilation/Native/NativeSyncTeXContract.cs', /MaxMapBytes = (\d+)/),
};

class Target {
    constructor() { this.listeners = new Map(); }
    addEventListener(kind, fn) { if (!this.listeners.has(kind)) this.listeners.set(kind, new Set()); this.listeners.get(kind).add(fn); }
    removeEventListener(kind, fn) { this.listeners.get(kind)?.delete(fn); }
    emit(kind) { for (const fn of [...(this.listeners.get(kind) || [])]) fn(); }
}
function harness(options = {}) {
    const control = { ...options };
    const stats = { copies: 0, oversizedAllocationAttempts: 0, base64: 0, blobs: 0, urls: [], revoked: [], workers: [],
        posts: 0, fetches: 0, hashes: 0, contracts: 0, order: [], proxyReads: 0 };
    const timers = new Map(); let nextTimer = 0;
    // Every allocation stays <=64KiB. Oversized lengths are scalar/proxy sentinels;
    // this is an outer harness guard, NOT a passing product refusal oracle.
    const Bytes = new Proxy(Uint8Array, { construct(target, args) {
        stats.copies++;
        if (typeof args[0] === 'number' && args[0] > 65536) {
            stats.oversizedAllocationAttempts++;
            throw new Error('TEST SAFETY: large allocation intercepted, not product refusal');
        }
        return Reflect.construct(target, args);
    } });
    class OwnedBlob extends Blob {
        constructor(parts, opts) { stats.blobs++; super(parts, opts); }
    }
    class OwnedURL extends URL {
        static createObjectURL() { const url = 'blob:r4-' + (stats.urls.length + 1); stats.urls.push(url); return url; }
        static revokeObjectURL(url) { stats.revoked.push(url); stats.order.push('revoke'); }
    }
    class FakeWorker {
        constructor(url) { this.url = String(url); this.messages = []; this.terminated = 0; stats.workers.push(this); }
        postMessage(message) { stats.posts++; this.messages.push(message); }
        terminate() { this.terminated++; stats.order.push('terminate'); }
    }
    class Channel {
        constructor() {
            const make = () => ({ onmessage: null, onmessageerror: null, close() {} });
            this.port1 = make(); this.port2 = make(); this.port1.peer = this.port2; this.port2.peer = this.port1;
        }
    }
    const sw = new Target(), controller = new Target();
    Object.assign(controller, { state: 'activated', scriptURL: origin + '/service-worker.js' });
    sw.controller = control.noController ? null : controller;
    sw.getRegistration = async () => ({ active: controller, scope: origin + '/' });
    controller.postMessage = (request, ports) => {
        stats.contracts++;
        const name = bodies[request.kind]; assert.ok(name, 'only six actual source-defined roles, not reserved Biber');
        const bytes = Buffer.from(source(name)), digest = hash(bytes), release = 'r4-synthetic-release';
        const reply = { type: 'native-worker-entry-result', protocol: 1, kind: request.kind, requestId: request.requestId,
            ok: true, release, digest, sri: 'sha256-' + createHash('sha256').update(bytes).digest('base64'), policy,
            source: '/js/' + name,
            entry: '/__oop/native-worker/' + request.kind + '?' + new URLSearchParams({ protocol: '1', kind: request.kind, release, digest }) };
        queueMicrotask(() => ports[0].peer.onmessage?.({ data: control.reply ? control.reply(reply) : reply }));
    };
    const context = vm.createContext({ navigator: { serviceWorker: sw }, location: { origin }, URL: OwnedURL, URLSearchParams,
        Uint8Array: Bytes, ArrayBuffer, DataView, TextDecoder, TextEncoder, Blob: OwnedBlob, AbortController,
        structuredClone, MessageChannel: Channel, Worker: FakeWorker, btoa,
        atob(value) { stats.base64++; assert.ok(value.length <= 65536, 'TEST SAFETY: base64 decode bound'); return atob(value); },
        crypto: { getRandomValues: value => webcrypto.getRandomValues(value), subtle: { async digest(algorithm, bytes) {
            stats.hashes++; if (control.hashGate) await control.hashGate;
            return webcrypto.subtle.digest(algorithm, bytes);
        } } },
        async fetch(url, opts) {
            stats.fetches++; assert.equal(opts.credentials, 'omit'); assert.equal(opts.redirect, 'error');
            assert.equal(new URL(url).origin, origin);
            const name = String(url).slice((origin + '/js/').length);
            assert.ok(Object.values(bodies).includes(name), 'fixed source URL only');
            return new Response(control.sourceBody || source(name));
        },
        setTimeout(fn, delay) { const id = ++nextTimer; timers.set(id, { fn, delay }); return id; },
        clearTimeout(id) { timers.delete(id); }, console });
    vm.runInContext(product('restricted-worker-admission.js'), context, { timeout: 1000 });
    vm.runInContext(product('compile-port-bridge.js') + ';globalThis.compile={provision,execute,terminate,observations,dispose};', context, { timeout: 1000 });
    vm.runInContext(product('pdf-port-bridge.js'), context, { timeout: 1000 });
    vm.runInContext(product('synctex-query-bridge.js'), context, { timeout: 1000 });
    const ports = [];
    return { stats, control, context, compile: context.compile,
        pdf() { const port = context.createPdfPort(); ports.push(port); return port; },
        query() { const port = context.createSyncTeXQueryPort(); ports.push(port); return port; },
        reset() { for (const key of ['copies', 'base64', 'blobs', 'posts', 'hashes', 'contracts', 'fetches']) stats[key] = 0; },
        changeController() { sw.controller = null; sw.emit('controllerchange'); },
        fire(delay) { const pair = [...timers].find(([, value]) => value.delay === delay); assert.ok(pair, 'owned deadline'); timers.delete(pair[0]); pair[1].fn(); },
        async close() { for (const port of ports) port.dispose(); context.compile.dispose(); await tick(); assert.equal(timers.size, 0); }
    };
}
function snapshot(h) {
    const s = h.stats;
    return { copies: s.copies, base64: s.base64, blobs: s.blobs, urls: s.urls.length,
        workers: s.workers.length, posts: s.posts, hashes: s.hashes };
}
function compileFrame() {
    return { tag: { version: 6, profile: 6, engineId: 'pdftex', runtimeIdentity: 'a'.repeat(64),
        job: '1'.repeat(32), request: '2'.repeat(32), epoch: 1, capture: '33333333-3333-4333-8333-333333333333', digest: hash(tiny()),
        ordinal: 1, tool: 'latex', nonce: '4'.repeat(32), snapshot: {
            captureId: '33333333-3333-4333-8333-333333333333', serviceId: '55555555-5555-4555-8555-555555555555',
            projectId: { value: '66666666-6666-4666-8666-666666666666' }, sessionId: '77777777-7777-4777-8777-777777777777',
            workingGeneration: 1, baseHeadRevisionId: null, localWorkspaceEpoch: 1, selectionVersion: 1 } },
        loader: tiny(), wasm: tiny(), mainEntry: 'main.tex', selectedRoot: 'main.tex',
        files: [{ name: 'main.tex', bytes: tiny() }], outputs: ['main.pdf', 'main.aux', 'main.bbl', 'main.blg',
            'main.log', 'main.toc', 'main.lof', 'main.lot', 'main.out'],
        maxLogBytes: 262144, maxOutputBytes: limits.output,
        runtimeResourceNames: [], generatedInputNames: [] };
}
function compileReply(frame) {
    // Producer-compatible NativePhaseReply shape; bytes/measurements are explicit
    // VM vectors, NOT a real PDF/module/heap or native execution receipt.
    return { tag: structuredClone(frame.tag), outcome: 'native-return', nativeReturn: 0, entered: true,
        log: '', files: [{ name: 'main.pdf', bytes: new Uint8Array([37, 80, 68, 70, 45, 49, 46, 55]) }],
        inputs: frame.files.map(f => ({ name: f.name, length: f.bytes.length, sha256: hash(f.bytes) })),
        elapsedMs: 1, heapBytes: 64, wasmSha256: hash(frame.wasm), loaderSha256: hash(frame.loader), sourceMapPayload: null };
}
function sparseSentinel(length, stats) {
    return new Proxy([], { get(target, key, receiver) {
        if (key === 'length') return length;
        if (key === 'map' || key === Symbol.iterator) return () => { stats.proxyReads++; throw new Error('TEST SAFETY: enumerate/copy intercepted'); };
        return Reflect.get(target, key, receiver);
    } });
}
async function compileRefusal(change) {
    const h = harness();
    try {
        await h.compile.provision(); h.reset();
        const frame = compileFrame(); change(frame, h.stats);
        let pending;
        try { pending = h.compile.execute(frame); } catch { /* valid rejection only if BEFORE resources/copies */ }
        if (pending) { h.compile.terminate(frame.tag.job, frame.tag.epoch); await pending; }
        return snapshot(h);
    } finally { await h.close(); }
}
function pdfRequest() {
    return { version: 2, inspectActions: false, requestId: 'r4-pdf', epoch: 1,
        documentSha256: hash(tiny()), loaderSha256: hash(tiny()), wasmSha256: hash(tiny()),
        pageIndex: 0, search: null, raster: true, width: 2, height: 2,
        budget: { deadlineMs: 30000, inputBytes: limits.output, pages: 32, textUnits: 100000,
            searchHits: 256, rasterPixels: 4000000, outputBytes: limits.pdfOutput, liveHandles: 64 } };
}
function queryRequest() { return { version: 1, requestId: 'c'.repeat(32), deadlineMs: 15000,
    direction: 'forward', file: 'розділ/main.tex', line: 1, page: 0, h: 0, v: 0 }; }
async function until(predicate) {
    for (let i = 0; i < 100 && !predicate(); i++) await tick();
    assert.ok(predicate(), 'bounded modeled preparation completed');
}
async function stoppedBeforeWork(kind, mutate) {
    const h = harness(); let release;
    h.control.hashGate = new Promise(resolve => { release = resolve; });
    const port = kind === 'pdf' ? h.pdf() : h.query();
    const request = kind === 'pdf' ? pdfRequest() : queryRequest(); mutate(request);
    h.reset(); let pending;
    try {
        pending = kind === 'pdf' ? port.inspect(JSON.stringify(request), tiny(), tiny(), tiny())
            : port.query(JSON.stringify(request), tiny(), tiny(), tiny());
        const observed = snapshot(h);
        port.dispose(); release(); await pending; await tick();
        return observed;
    } finally { release(); await h.close(); }
}
function assertNoWork(observed, label) {
    assert.deepEqual(observed, { copies: 0, base64: 0, blobs: 0, urls: 0, workers: 0, posts: 0, hashes: 0 }, label);
}

// The tests assert the required defensive contract, NOT current vulnerable
// behavior. Known failures remain FAIL/nonzero, never todo/skip/expected-green.
test('R4-RES-01 compile counts/types rejected before Blob/Worker/copy', async () => {
    const cases = [
        ['file-count', (f, s) => { f.files = sparseSentinel(limits.files + 1, s); }],
        ['output-count', (f, s) => { f.outputs = sparseSentinel(limits.outputCount + 1, s); }],
        ['scalar-file-bytes', f => { f.files[0].bytes = Number.MAX_SAFE_INTEGER; }],
        ['scalar-loader-length', f => { f.loader = Number.MAX_SAFE_INTEGER; }],
        ['scalar-wasm-length', f => { f.wasm = Number.MAX_SAFE_INTEGER; }],
        ['overflow-log-budget', f => { f.maxLogBytes = Number.MAX_SAFE_INTEGER; }],
        ['overflow-output-budget', f => { f.maxOutputBytes = Number.MAX_SAFE_INTEGER; }],
        ['zero-output-budget', f => { f.maxOutputBytes = 0; }],
    ];
    const bad = [];
    for (const [name, change] of cases) {
        const observed = await compileRefusal(change);
        if (Object.values(observed).some(Boolean)) bad.push({ case: name, ...observed });
    }
    assert.deepEqual(bad, [], 'all scalar/count guards must run BEFORE allocation; harness guards are not product refusals');
});
test('R4-RES-01 compile typed-array impostor rejected without property reads or Blob', async () => {
    let reads = 0;
    const observed = await compileRefusal(f => {
        f.loader = new Proxy(tiny(), { get(target, key) { reads++; return Reflect.get(target, key, target); } });
    });
    assertNoWork(observed, 'typed-array Proxy is not an admitted byte payload');
    assert.equal(reads, 0);
});
test('R4-RES-01 compile malformed namespace/roles rejected before resource creation', async () => {
    const cases = [
        ['traversal', f => { f.files[0].name = '../r4-synthetic-canary.tex'; }],
        ['absolute', f => { f.files[0].name = '/r4-synthetic-canary.tex'; }],
        ['invalid-UTF16', f => { f.selectedRoot = 'bad\ud800.tex'; }],
        ['undeclared-runtime', f => { f.files.push({ name: '__oop_runtime__/r4.tex', bytes: tiny() }); }],
        ['generated-root', f => { f.generatedInputNames = ['main.tex']; }],
        ['generated-output-collision', f => { f.files.push({ name: 'main.pdf', bytes: tiny() }); }],
        ['duplicate-case-name', f => { f.files.push({ name: 'MAIN.TEX', bytes: tiny() }); }],
        ['NFC-alias', f => { f.files.push({ name: 'é.tex', bytes: tiny() }, { name: 'e\u0301.tex', bytes: tiny() }); }],
    ];
    const bad = [];
    for (const [name, change] of cases) {
        const observed = await compileRefusal(change);
        if (Object.values(observed).some(Boolean)) bad.push({ case: name, ...observed });
    }
    assert.deepEqual(bad, [], 'invalid captured/runtime/generated names must not boot a worker first');
});
test('R4-STALE-01 compile malformed role/epoch/tool tag rejected before any allocation', async () => {
    const bad = [];
    for (const [key, value] of [['epoch', -1], ['epoch', Number.MAX_SAFE_INTEGER + 1], ['nonce', ''], ['ordinal', 0], ['tool', 'unapproved']]) {
        const observed = await compileRefusal(f => { f.tag[key] = value; });
        if (Object.values(observed).some(Boolean)) bad.push({ key, value, ...observed });
    }
    assert.deepEqual(bad, []);
});
test('R4-NET-01 reserved Biber/mismatched map phases and unknown profiles do not create an entry', async () => {
    for (const tag of [{ version: 10, tool: 'biber', engineId: 'pdftex' }, { version: 11 }, { version: 12 },
        { version: 13 }, { version: 14 }, { version: 15 }, { version: 16 }, { version: 17 }, { profile: 11 }, { profile: 999 }]) {
        assertNoWork(await compileRefusal(f => Object.assign(f.tag, tag)), 'reservation is not admission');
    }
    const h = harness(); try { assert.throws(() => h.context.createRestrictedWorkerAdmission('biber')); } finally { await h.close(); }
});
test('R4-NET-01 actual admission checks controller/release/source/hash/UTF8/policy, no network fallback', async () => {
    const cases = [ { noController: true }, { reply: r => ({ ...r, release: '../forged' }) },
        { reply: r => ({ ...r, kind: 'biber' }) }, { reply: r => ({ ...r, digest: '0'.repeat(64) }) },
        { reply: r => ({ ...r, policy: "default-src *" }) }, { reply: r => ({ ...r, entry: 'blob:forged' }) },
        { sourceBody: new Uint8Array([0xc3, 0x28]) }, { sourceBody: 'corrupt-but-valid-UTF8' } ];
    for (const opts of cases) {
        const h = harness(opts);
        try { await assert.rejects(h.compile.provision()); assert.equal(h.stats.workers.length, 0); assert.equal(h.stats.blobs, 0); }
        finally { await h.close(); }
    }
});
test('R4-CAN-01 valid compile dispatch, active duplicate refusal, cancel/late reply, benign recovery', async () => {
    const h = harness();
    try {
        await h.compile.provision(); const frame = compileFrame();
        const pending = h.compile.execute(frame), worker = h.stats.workers[0], saved = worker.onmessage;
        assert.equal(worker.messages[0].operation, 'boot');
        saved({ data: { kind: 'ready' } }); saved({ data: { kind: 'ready' } });
        assert.equal(worker.messages.length, 2);
        assert.throws(() => h.compile.execute(frame), /duplicate logical job/);
        h.compile.terminate(frame.tag.job, frame.tag.epoch);
        assert.equal((await pending).outcome, 'cancelled'); assert.equal(worker.terminated, 1);
        assert.deepEqual(h.stats.order.slice(-2), ['terminate', 'revoke']);
        const fresh = compileFrame(); fresh.tag = { ...fresh.tag, job: '8'.repeat(32), epoch: 2, nonce: '9'.repeat(32) };
        const recovery = h.compile.execute(fresh), next = h.stats.workers[1];
        saved({ data: { kind: 'done', reply: compileReply(frame) } });
        assert.equal(next.terminated, 0, 'retired callback cannot settle recovery');
        next.onmessage({ data: { kind: 'ready' } });
        next.onmessage({ data: { kind: 'entered' } });
        next.onmessage({ data: { kind: 'done', reply: compileReply(fresh) } });
        assert.equal((await recovery).nativeReturn, 0); assert.equal(next.terminated, 1);
        assert.equal(h.compile.observations().active, 0);
    } finally { await h.close(); }
});
test('R4-STALE-01 live compile worker cannot settle a forged epoch/nonce/component reply', async () => {
    const accepted = [];
    for (const [key, value] of [['epoch', 2], ['nonce', 'forged'], ['runtimeIdentity', '0'.repeat(64)], ['engineId', 'lualatex'], ['ordinal', 2], ['snapshot', null]]) {
        const h = harness();
        try {
            await h.compile.provision(); const frame = compileFrame(), pending = h.compile.execute(frame);
            const worker = h.stats.workers[0];
            worker.onmessage({ data: { kind: 'ready' } }); worker.onmessage({ data: { kind: 'entered' } });
            const forged = compileReply(frame);
            forged.tag[key] = key === 'snapshot' ? { ...forged.tag.snapshot, workingGeneration: 2 } : value;
            worker.onmessage({ data: { kind: 'done', reply: forged } });
            const reply = await pending;
            if (reply.outcome === 'native-return') accepted.push(key);
        } finally { await h.close(); }
    }
    assert.deepEqual(accepted, [], 'C# later checks do not replace defensive bridge correlation');
});
test('R4-RES-01 compile reply log/output bounds enforced before returning to interop', async () => {
    const accepted = [];
    for (const [name, mutate] of [
        ['utf8-log', reply => { reply.log = 'я'.repeat(5); }],
        ['output-bytes', reply => { reply.files = [{ name: 'main.pdf', bytes: new Uint8Array(9) }]; }],
        ['duplicate-output-count', reply => { reply.files = Array.from({ length: 10 }, () => ({ name: 'main.pdf', bytes: tiny() })); }],
    ]) {
        const h = harness();
        try {
            await h.compile.provision(); const frame = compileFrame(); frame.maxLogBytes = frame.maxOutputBytes = 8;
            const pending = h.compile.execute(frame), worker = h.stats.workers[0];
            worker.onmessage({ data: { kind: 'ready' } });
            worker.onmessage({ data: { kind: 'entered' } });
            const reply = compileReply(frame);
            mutate(reply); worker.onmessage({ data: { kind: 'done', reply } });
            if ((await pending).outcome === 'native-return') accepted.push(name);
        } finally { await h.close(); }
    }
    assert.deepEqual(accepted, [], 'reply caps use UTF8 bytes and declared outputs, not unchecked native-return');
});
test('R4-CAN-01 controller replacement physically retires the owned fake compile Worker', async () => {
    const h = harness();
    try {
        await h.compile.provision(); const pending = h.compile.execute(compileFrame()); h.changeController();
        assert.equal((await pending).outcome, 'worker-error'); assert.equal(h.stats.workers[0].terminated, 1);
        assert.throws(() => h.compile.execute(compileFrame()));
        assert.equal(h.stats.urls.length, h.stats.revoked.length);
    } finally { await h.close(); }
});
test('R4-RES-01 PDF invalid full budget/search/geometry rejected before byte clone/hash', async () => {
    const cases = [ ['pages', r => { r.budget.pages = limits.pdfPages + 1; }],
        ['output', r => { r.budget.outputBytes = limits.pdfOutput + 1; }], ['handles', r => { r.budget.liveHandles = 65; }],
        ['page-index', r => { r.pageIndex = -1; }], ['raster-overflow', r => { r.width = Number.MAX_SAFE_INTEGER; }],
        ['invalid-search-UTF16', r => { r.search = 'bad\ud800'; }], ['search-count', r => { r.search = 'x'.repeat(256); }] ];
    const bad = [];
    for (const [name, mutate] of cases) {
        const observed = await stoppedBeforeWork('pdf', mutate);
        if (Object.values(observed).some(Boolean)) bad.push({ case: name, ...observed });
    }
    assert.deepEqual(bad, [], 'PDF budget fields must be checked before copies and hashing, not after native entry');
});
test('R4-RES-01 PDF/query scalar byte lengths cannot trigger any conversion', async () => {
    for (const kind of ['pdf', 'query']) {
        const h = harness(), port = kind === 'pdf' ? h.pdf() : h.query();
        try {
            h.reset();
            const result = kind === 'pdf' ? await port.inspect(JSON.stringify(pdfRequest()), Number.MAX_SAFE_INTEGER, tiny(), tiny())
                : await port.query(JSON.stringify(queryRequest()), Number.MAX_SAFE_INTEGER, tiny(), tiny());
            assert.ok(['transport-error', 'failed'].includes(result.status)); assertNoWork(snapshot(h), kind);
        } finally { await h.close(); }
    }
});
test('R4-RES-01 query direction/path/coordinates rejected before clone/admission', async () => {
    const cases = [ ['direction', r => { r.direction = 'execute'; }], ['traversal', r => { r.file = '../r4-canary.tex'; }],
        ['invalid-UTF16', r => { r.file = '\ud800.tex'; }], ['line', r => { r.line = -1; }],
        ['inverse-coordinates', r => { r.direction = 'inverse'; r.h = 1e300; r.page = -1; }] ];
    const bad = [];
    for (const [name, mutate] of cases) {
        const observed = await stoppedBeforeWork('query', mutate);
        if (Object.values(observed).some(Boolean)) bad.push({ case: name, ...observed });
    }
    assert.deepEqual(bad, []);
});
test('R4-RES-01 PDF/query over-limit byte budgets via tiny lowered-bound views (mechanical scale only)', async () => {
    // No production source is written. Only known byte ceiling literals are
    // scaled in private VM copies; the provenance receipt lists these transforms.
    for (const [kind, bridge] of [['pdf', 'pdf-port-bridge.js'], ['query', 'synctex-query-bridge.js']]) {
        for (const which of ['loader', 'wasm', 'input']) {
            const h = harness();
            try {
                const raw = product(bridge);
                const lowered = raw.replace(/\b(8388608|1048575|1048576|16777216)\b/g, '32');
                assert.notEqual(lowered, raw);
                const factory = kind === 'pdf' ? 'createPdfPort' : 'createSyncTeXQueryPort';
                const port = vm.runInContext('(()=>{' + lowered + ';return ' + factory + ';})()', h.context)();
                h.reset(); const data = new Uint8Array(33), normal = tiny();
                const args = [which === 'input' ? data : normal, which === 'loader' ? data : normal, which === 'wasm' ? data : normal];
                const req = kind === 'pdf' ? pdfRequest() : queryRequest(); if (kind === 'pdf') req.budget.inputBytes = 32;
                const response = kind === 'pdf' ? await port.inspect(JSON.stringify(req), ...args) : await port.query(JSON.stringify(req), ...args);
                assert.ok(['transport-error', 'failed'].includes(response.status)); assertNoWork(snapshot(h), kind + '/' + which);
                port.dispose();
            } finally { await h.close(); }
        }
    }
});
test('R4-CAN-01 valid query physical fake-worker retirement, bounded output, late/forged reply', async () => {
    const h = harness(), port = h.query();
    try {
        const req = queryRequest(), pending = port.query(JSON.stringify(req), tiny(), tiny(), tiny());
        await until(() => h.stats.workers.length === 1); const worker = h.stats.workers[0], saved = worker.onmessage;
        assert.ok(worker.url.startsWith(origin + '/__oop/native-worker/synctex?'));
        assert.equal((await port.query(JSON.stringify(req), tiny(), tiny(), tiny())).status, 'busy');
        port.cancel(req.requestId); assert.equal((await pending).status, 'cancelled'); assert.equal(worker.terminated, 1);
        saved({ data: { kind: 'terminal', response: { version: 1, requestId: req.requestId, status: 'native', nativeStatus: 0, json: tiny() } } });
        assert.equal((await port.query(JSON.stringify(req), tiny(), tiny(), tiny())).status, 'cancelled');
        const fresh = { ...req, requestId: 'd'.repeat(32) }, recovery = port.query(JSON.stringify(fresh), tiny(), tiny(), tiny());
        await until(() => h.stats.workers.length === 2);
        const next = h.stats.workers[1];
        next.onmessage({ data: { kind: 'terminal', response: { version: 1, requestId: 'e'.repeat(32), status: 'native', nativeStatus: 0, json: tiny() } } });
        assert.equal((await recovery).status, 'failed'); assert.equal(next.terminated, 1);
        const over = { ...req, requestId: 'f'.repeat(32) }, oversized = port.query(JSON.stringify(over), tiny(), tiny(), tiny());
        await until(() => h.stats.workers.length === 3);
        const third = h.stats.workers[2];
        third.onmessage({ data: { kind: 'terminal', response: { version: 1, requestId: over.requestId,
            status: 'native', nativeStatus: 0, json: new Uint8Array(32769) } } });
        assert.equal((await oversized).status, 'failed', '32KiB query output cap checked without forwarding oversized bytes');
        assert.equal(third.terminated, 1);
        assert.equal(h.stats.urls.length, h.stats.revoked.length);
    } finally { await h.close(); }
});
test('R4-CAN-01 actual PDF hash preparation retains reservation until noncooperative digests settle', async () => {
    const h = harness(), port = h.pdf(); let release;
    h.control.hashGate = new Promise(resolve => { release = resolve; });
    try {
        const req = pdfRequest(), pending = port.inspect(JSON.stringify(req), tiny(), tiny(), tiny());
        assert.equal(h.stats.hashes, 3); port.cancel(req.requestId);
        assert.equal((await pending).status, 'cancelled'); const before = snapshot(h);
        assert.equal((await port.inspect(JSON.stringify({ ...req, requestId: 'other' }), tiny(), tiny(), tiny())).status, 'unavailable');
        assert.deepEqual(snapshot(h), before); release(); await tick(); await tick();
        assert.equal(h.stats.workers.length, 0);
    } finally { release(); await h.close(); }
});

function entry(name, privateSource = null) {
    const stats = { imports: 0, vfs: 0, native: 0, replies: [] };
    const self = { postMessage(value) { stats.replies.push(value); } };
    const context = vm.createContext({ self, Uint8Array, ArrayBuffer, TextEncoder, TextDecoder, DataView, WebAssembly,
        performance, crypto: webcrypto, console: { log() {}, error() {} },
        importScripts() { stats.imports++; throw new Error('TEST STOP: no native/vendor module executes'); },
        FS: { mkdirTree() { stats.vfs++; throw new Error('TEST STOP before any real filesystem'); },
            writeFile() { stats.vfs++; throw new Error('TEST STOP before any real filesystem'); } },
        prepareExecutionContext() { stats.native++; throw new Error('TEST STOP before any native entry'); } });
    vm.runInContext(privateSource ?? source(name), context, { timeout: 1000 });
    if (name === 'compile-port-worker.js') vm.runInContext('globalThis.actualExecute=execute;globalThis.actualValidate=validateAuthoringV4Frame;', context);
    return { context, self, stats };
}
test('R4-RES-01 actual worker FFI rejects malformed UTF16/traversal/case/output quota before VFS/native', async () => {
    for (const mutate of [f => { f.files[0].name = '../r4-canary.tex'; }, f => { f.selectedRoot = '\ud800.tex'; },
        f => { f.files.push({ name: 'MAIN.TEX', bytes: tiny() }); }, f => { f.maxOutputBytes = limits.output + 1; }]) {
        const h = entry('compile-port-worker.js'), frame = compileFrame(); frame.operation = frame.tag.tool; mutate(frame);
        await h.context.actualExecute({ data: frame });
        assert.equal(h.stats.vfs, 0); assert.equal(h.stats.native, 0); assert.equal(h.stats.imports, 0);
        assert.equal(h.stats.replies.at(-1).reply.outcome, 'setup-failure');
    }
    const h = entry('compile-port-worker.js'), frame = compileFrame(); frame.operation = frame.tag.tool;
    assert.doesNotThrow(() => h.context.actualValidate(frame), 'positive current namespace control, not native execution');
});
test('R4-RES-01 actual suite aggregate byte guard runs before VFS (tiny literal scale)', async () => {
    const raw = source('compile-port-worker.js');
    const target = 'const maxTotal=suite?167772160:';
    assert.equal(raw.split(target).length - 1, 1);
    const scaled = raw.replace(target, 'const maxTotal=suite?48:').replaceAll('67108864', '32').replaceAll('33554432', '32');
    for (const count of [2, 3]) {
        const h = entry('compile-port-worker.js', scaled), frame = compileFrame(); frame.operation = frame.tag.tool;
        frame.files = Array.from({ length: count }, (_, i) => ({ name: i === 0 ? 'main.tex' : 'small-' + i + '.txt', bytes: new Uint8Array(17) }));
        await h.context.actualExecute({ data: frame });
        assert.equal(h.stats.vfs, count === 2 ? 1 : 0, '34 bytes reach the VFS sentinel;51 bytes must not');
        assert.equal(h.stats.native, 0);
    }
});
test('R4-RES-01 actual suite worker rejects generated/runtime role collision before VFS', async () => {
    const h = entry('compile-port-worker.js'), frame = compileFrame(); frame.operation = frame.tag.tool;
    frame.generatedInputNames = ['main.tex'];
    await h.context.actualExecute({ data: frame });
    assert.equal(h.stats.vfs, 0, 'selected captured root cannot become generated input');
    assert.equal(h.stats.native, 0);
});
test('R4-RES-01 actual PDF/query worker entry must reject invalid budget/map BEFORE loader import', async () => {
    const bad = [];
    for (const [kind, file] of [['pdf', 'pdf-port-worker.js'], ['query', 'synctex-query-worker.js']]) {
        const h = entry(file), request = kind === 'pdf' ? pdfRequest() : queryRequest();
        if (kind === 'pdf') request.budget.outputBytes = Number.MAX_SAFE_INTEGER;
        else request.direction = 'execute';
        await h.self.onmessage({ data: { request, document: tiny(), map: 12345, loader: tiny(), wasm: tiny(), loaderUrl: 'blob:synthetic' } });
        if (h.stats.imports) bad.push({ kind, loaderImports: h.stats.imports });
        assert.equal(h.stats.native, 0, 'no native module available in this test');
    }
    assert.deepEqual(bad, [], 'do not bootstrap native for malformed direct entry messages');
});
test('R4-RES/CAN/NET B1 fixture inventory and pure observation oracles (NOT runtime captures)', () => {
    const { preflight, checkObservation } = require('../../scripts/test-release-isolation.cjs');
    const inventory = preflight(), fixture = inventory.manifest.cases.find(c => c.id === 'cpu-lua');
    assert.equal(inventory.manifest.execution, 'preflight-only'); assert.equal(inventory.manifest.cases.length, 7);
    const expected = { fixtureManifestSha256: inventory.fixtureManifestSha256,
        sourceTreeSha256: hash(Buffer.from('SYNTHETIC source identity')), runtimeIdentitySha256: hash(Buffer.from('SYNTHETIC runtime identity')),
        publicationIdentitySha256: hash(Buffer.from('SYNTHETIC publication identity')), profile: 6 };
    const record = { schemaVersion: 1, fixtureId: fixture.id, fixtureManifestSha256: inventory.fixtureManifestSha256,
        evidenceKind: 'synthetic-oracle-vector', identity: { ...expected, engineId: 'lualatex' },
        supervisor: { cpuQuota: 2, memoryBytes: 2147483648, deadlineMs: 30000 },
        observed: { status: 'cancelled', elapsedMs: 500, peakHeapBytes: 1024, outputBytes: 0, logBytes: 0,
            outerSupervisorKilled: false, outputPromoted: false, sourceEditableAfter: true, benignRecoveryCompleted: true,
            savedProjectBeforeSha256: hash(tiny()), savedProjectAfterSha256: hash(tiny()), networkRequests: 0,
            processLaunches: 0, hostCanaryRead: false, nativeEntered: true, nativeActivityProof: 'native-workload-trace',
            workerDestroyed: true, workerDestroyLatencyMs: 25 } };
    const result = checkObservation(fixture, record, expected, { allowSynthetic: true });
    assert.equal(result.ok, true); assert.equal(result.scope, 'synthetic-oracle-vector');
    assert.equal(result.execution, 'not-run-by-this-tool');
    assert.equal(checkObservation(fixture, record, expected).ok, false, 'synthetic can never be real CLI evidence');
    for (const patch of [{ nativeActivityProof: 'before-call-marker' }, { outerSupervisorKilled: true },
        { workerDestroyed: false }, { workerDestroyLatencyMs: 2001 }, { outputPromoted: true },
        { sourceEditableAfter: false }, { benignRecoveryCompleted: false }, { networkRequests: 1 },
        { savedProjectAfterSha256: hash(Buffer.from('changed synthetic bytes')) }, { peakHeapBytes: 536870913 }]) {
        assert.equal(checkObservation(fixture, { ...record, observed: { ...record.observed, ...patch } }, expected,
            { allowSynthetic: true }).ok, false, 'independent negative oracle: ' + Object.keys(patch)[0]);
    }
    assert.equal(checkObservation(fixture, record, { ...expected, profile: 7 }, { allowSynthetic: true }).ok, false);
    assert.equal(checkObservation(fixture, record, { ...expected, publicationIdentitySha256: hash(Buffer.from('other publication')) },
        { allowSynthetic: true }).ok, false);
    const quota = inventory.manifest.cases.find(c => c.id === 'caught-quotas');
    const quotaRecord = { ...record, fixtureId: quota.id, observed: { ...record.observed, status: 'resource-limit',
        quotaLatched: true, outputGettersRevoked: true } };
    assert.equal(checkObservation(quota, quotaRecord, expected, { allowSynthetic: true }).ok, true);
    assert.equal(checkObservation(quota, { ...quotaRecord, observed: { ...quotaRecord.observed, quotaLatched: false } }, expected,
        { allowSynthetic: true }).ok, false, 'mere truncation cannot claim native quota latch');
});
test('R4-RES-01 B1 executable tooling refuses hostile mode instead of invoking a scheduler', () => {
    const { cli } = require('../../scripts/test-release-isolation.cjs');
    assert.throws(() => cli(['--run-hostile']), error => error.code === 3 && /no execution performed/.test(error.message));
});
