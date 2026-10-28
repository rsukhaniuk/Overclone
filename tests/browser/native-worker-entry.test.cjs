// Actual production/dev handlers with modeled CacheStorage/network IO.
// No native execution; real browser/header/offline proof is a separate script.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { createHash, webcrypto } = require('node:crypto');
const root = path.join(__dirname, '../../src/Client/wwwroot');
const origin = 'https://entry.test';
const sourcePath = '/js/compile-port-worker.js';
const worker = fs.readFileSync(path.join(root, sourcePath));
const pdfPath = '/js/pdf-port-worker.js';
const pdfWorker = fs.readFileSync(path.join(root, pdfPath));
const dviPath = '/js/dvi-port-worker.js';
const dviWorker = fs.readFileSync(path.join(root, dviPath));
const unicodePath = '/js/unicode-tex-port-worker.js';
const unicodeWorker = fs.readFileSync(path.join(root, unicodePath));
const policy = "default-src 'none'; script-src blob: 'wasm-unsafe-eval'; connect-src 'none'; worker-src 'none'; object-src 'none'";
const deny = "default-src 'none'; script-src 'none'; connect-src 'none'; worker-src 'none'; object-src 'none'";
const sri = bytes => 'sha256-' + createHash('sha256').update(bytes).digest('base64');
const hex = bytes => createHash('sha256').update(bytes).digest('hex');
const core = ['index.html', 'app.css', 'manifest.json', 'icons/icon-192.png', 'icons/icon-512.png',
    '_framework/blazor.webassembly.js', '_framework/dotnet.js', '_framework/dotnet.runtime.abc.js',
    '_framework/dotnet.native.abc.js', '_framework/dotnet.native.abc.wasm', '_framework/Client.abc.wasm',
    'js/workspace-storage.js', 'js/editor-select.js', 'rust-generated/document_engine.js', 'rust-generated/document_engine_bg.wasm',
    'bundled-runtime/compiler-suite.zip', 'bundled-runtime/e1.js', 'bundled-runtime/e1.wasm'];
const tick = () => new Promise(resolve => setImmediate(resolve));
function harness(options = {}) {
    const handlers = new Map(), stores = new Map(), fetches = [];
    const bodies = new Map(core.map(url => ['/' + url, Buffer.from('modeled SDK fixture ' + url)]));
    if (!options.missingRole) bodies.set(sourcePath, options.body || worker);
    if (!options.missingPdfRole) bodies.set(pdfPath, options.pdfBody || pdfWorker);
    if (!options.missingDviRole) bodies.set(dviPath, dviWorker);
    if (!options.missingUnicodeRole) bodies.set(unicodePath, unicodeWorker);
    const manifest = { version: 'release-native-test', assets: [...bodies].map(([url, body]) => ({ url: url.slice(1), hash: sri(body) })) };
    const releaseName = 'oop-workspace-release-' + manifest.version;
    let held = null;
    const absolute = value => new URL(typeof value === 'string' ? value : value.url, origin).href;
    const caches = {
        async open(name) {
            if (!stores.has(name)) stores.set(name, new Map());
            const store = stores.get(name);
            return { async match(key) { return store.get(absolute(key))?.clone(); },
                async put(key, value) { store.set(absolute(key), value.clone()); } };
        },
        async keys() { return [...stores.keys()]; }, async delete(name) { return stores.delete(name); },
    };
    class RelativeRequest extends Request { constructor(url, init) { super(absolute(url), init); } }
    const self = { location: { origin }, assetsManifest: undefined,
        importScripts() { self.assetsManifest = manifest; }, addEventListener(name, action) { handlers.set(name, action); },
        clients: { claim: async () => {}, matchAll: async () => [] }, skipWaiting() {} };
    const context = vm.createContext({ self, URL, URLSearchParams, Request: RelativeRequest, Response, Uint8Array,
        btoa, caches, crypto: { subtle: { async digest(algorithm, bytes) { if (held) await held.promise; return webcrypto.subtle.digest(algorithm, bytes); } } },
        async fetch(request) {
            fetches.push(request.url);
            const body = bodies.get(new URL(request.url).pathname);
            if (!body || request.integrity !== sri(body)) throw new Error('modeled integrity failure');
            return new Response(body);
        }, console: { warn() {} } });
    vm.runInContext(fs.readFileSync(path.join(root, 'service-worker.published.js'), 'utf8'), context);
    async function install() { let work; handlers.get('install')({ waitUntil(p) { work = p; } }); await work; }
    async function message(changes = {}, extra = {}) {
        const replies = []; let work, closed = 0;
        const request = { type: 'native-worker-entry', protocol: 1, kind: 'compile', requestId: 'a'.repeat(32), ...changes };
        handlers.get('message')({ data: request, source: { type: 'window', url: origin + '/', ...extra },
            ports: [{ postMessage(value) { replies.push(JSON.parse(JSON.stringify(value))); }, close() { closed++; } }],
            waitUntil(p) { work = p; } });
        await work; assert.equal(closed, 1); assert.equal(replies.length, 1); return replies[0];
    }
    async function route(entry, changes = {}) {
        let response;
        handlers.get('fetch')({ request: { url: origin + entry, method: 'GET', destination: 'worker', headers: new Headers(), ...changes },
            respondWith(value) { response = Promise.resolve(value); } });
        assert.ok(response, 'reserved/direct executing path must never bypass to network');
        return response;
    }
    return { install, message, route, stores, fetches, releaseName, manifest, absolute,
        hold() { let release; const promise = new Promise(resolve => { release = resolve; }); held = { promise }; return () => { held = null; release(); }; } };
}
async function refused(response) {
    assert.equal(response.status, 403); assert.equal(response.headers.get('Content-Security-Policy'), deny);
}
const deadline = setTimeout(() => { console.error('Native entry Node test deadline exceeded'); process.exit(1); }, 10000);
(async () => {
    const h = harness(); await h.install();
    const fetchCount = h.fetches.length;
    const contract = await h.message();
    assert.equal(contract.ok, true); assert.equal(contract.protocol, 1); assert.equal(contract.kind, 'compile');
    assert.equal(contract.release, h.manifest.version); assert.equal(contract.digest, hex(worker));
    assert.equal(contract.sri, sri(worker)); assert.equal(contract.source, sourcePath); assert.equal(contract.policy, policy);
    const response = await h.route(contract.entry);
    assert.equal(response.status, 200); assert.equal(response.headers.get('Content-Security-Policy'), policy);
    assert.equal(response.headers.get('X-Oop-Worker-Release'), contract.release);
    assert.equal(response.headers.get('X-Oop-Worker-Sha256'), hex(worker));
    assert.deepEqual(Buffer.from(await response.arrayBuffer()), worker, 'source bytes must not be rewritten');
    for (const changes of [{ protocol: 2 }, { kind: 'unknown' }, { kind: '__proto__' }, { requestId: 'bad' }, { extra: true }])
        assert.equal((await h.message(changes)).ok, false);
    assert.equal((await h.message({}, { type: 'worker' })).ok, false);
    assert.equal((await h.message({}, { url: 'https://other.test/' })).ok, false);
    for (const [entry, changes] of [
        [contract.entry, { method: 'POST' }], [contract.entry, { destination: '' }],
        [contract.entry, { destination: 'script' }], [contract.entry, { destination: 'sharedworker' }],
        [contract.entry, { headers: new Headers({ authorization: 'synthetic-not-a-key' }) }],
        [contract.entry + '&extra=1', {}], [contract.entry + '&protocol=1', {}],
        [contract.entry.replace('protocol=1', 'protocol=2'), {}],
        [contract.entry.replace('kind=compile', 'kind=pdf'), {}],
        [contract.entry.replace(contract.release, 'wrong-release'), {}],
        [contract.entry.replace(hex(worker), '0'.repeat(64)), {}],
        [contract.entry.replace('/compile?', '/unknown?'), {}],
    ]) await refused(await h.route(entry, changes));
    for (const destination of ['worker', 'sharedworker', 'script', 'serviceworker'])
        await refused(await h.route(sourcePath + '?anything=1', { destination }));
    assert.equal(h.fetches.length, fetchCount, 'admission/entry may not fetch network or populate aux');
    assert.equal([...h.stores.keys()].filter(n => n.includes('aux')).length, 1);
    assert.equal(h.stores.get('oop-workspace-aux-' + h.manifest.version).size, 0);

    const release = h.hold();
    const first = Array.from({ length: 4 }, () => h.message());
    await tick(); await tick();
    assert.equal((await h.message()).ok, false, 'bounded pending admission, no queue');
    assert.equal((await h.message({ kind: 'pdf' })).ok, false, 'same global budget shared by both roles');
    assert.equal((await h.message({ kind: 'dvi' })).ok, false, 'converter shares the admission budget');
    await refused(await h.route(contract.entry));
    release(); assert.ok((await Promise.all(first)).every(r => r.ok));

    const store = h.stores.get(h.releaseName), sourceKey = h.absolute(sourcePath);
    store.set(sourceKey, new Response('corrupt owned synthetic source'));
    assert.equal((await h.message()).ok, false); await refused(await h.route(contract.entry));
    store.set(sourceKey, new Response(worker));
    store.delete(h.absolute('/app.css'));
    assert.equal((await h.message()).ok, false); await refused(await h.route(contract.entry));
    store.set(h.absolute('/app.css'), new Response('restored modeled complete role'));
    store.delete(sourceKey);
    h.stores.get('oop-workspace-aux-' + h.manifest.version).set(sourceKey, new Response(worker));
    assert.equal((await h.message()).ok, false); await refused(await h.route(contract.entry));
    assert.equal(h.fetches.length, fetchCount, 'missing/corrupt/incomplete never recover native bytes from network');

    const optional = harness({ missingRole: true }); await optional.install();
    assert.equal((await optional.message()).ok, false, 'missing native role must not prevent source release install');
    const oversized = harness({ body: Buffer.alloc(32769, 32) }); await oversized.install();
    assert.equal((await oversized.message()).ok, false, 'streaming native-source size bound independent of valid SRI');

    const p = harness(); await p.install();
    const pdf = await p.message({ kind: 'pdf' });
    assert.equal(pdf.ok, true); assert.equal(pdf.kind, 'pdf'); assert.equal(pdf.source, pdfPath);
    assert.equal(pdf.release, p.manifest.version); assert.equal(pdf.digest, hex(pdfWorker)); assert.equal(pdf.sri, sri(pdfWorker));
    assert.equal(pdf.policy, policy);
    const pdfResponse = await p.route(pdf.entry);
    assert.equal(pdfResponse.status, 200); assert.equal(pdfResponse.headers.get('Content-Security-Policy'), policy);
    assert.equal(pdfResponse.headers.get('X-Oop-Worker-Kind'), 'pdf');
    assert.equal(pdfResponse.headers.get('X-Oop-Worker-Release'), pdf.release);
    assert.deepEqual(Buffer.from(await pdfResponse.arrayBuffer()), pdfWorker, 'classic PDF bytes are returned exactly');
    for (const [entry, changes] of [[pdf.entry, { method: 'POST' }], [pdf.entry, { destination: '' }],
        [pdf.entry, { destination: 'script' }], [pdf.entry + '&kind=pdf', {}], [pdf.entry + '&extra=1', {}],
        [pdf.entry.replace('protocol=1', 'protocol=2'), {}], [pdf.entry.replace(pdf.release, 'stale-release'), {}],
        [pdf.entry.replace('/pdf?', '/compile?'), {}], [pdf.entry.replace('kind=pdf', 'kind=compile'), {}],
        [pdf.entry.replace(hex(pdfWorker), hex(worker)), {}]]) await refused(await p.route(entry, changes));
    for (const destination of ['worker', 'sharedworker', 'script', 'serviceworker'])
        await refused(await p.route(pdfPath, { destination }));
    const passiveSource = await p.route(pdfPath, { destination: '' });
    assert.equal(passiveSource.status, 200); assert.deepEqual(Buffer.from(await passiveSource.arrayBuffer()), pdfWorker);
    const pstore = p.stores.get(p.releaseName), pdfKey = p.absolute(pdfPath), initialFetches = p.fetches.length;
    pstore.set(pdfKey, new Response('owned PDF source corruption'));
    assert.equal((await p.message({ kind: 'pdf' })).ok, false); await refused(await p.route(pdf.entry));
    pstore.delete(pdfKey);
    p.stores.get('oop-workspace-aux-' + p.manifest.version).set(pdfKey, new Response(pdfWorker));
    assert.equal((await p.message({ kind: 'pdf' })).ok, false); await refused(await p.route(pdf.entry));
    pstore.set(pdfKey, new Response(pdfWorker)); pstore.delete(p.absolute('/app.css'));
    assert.equal((await p.message({ kind: 'pdf' })).ok, false); await refused(await p.route(pdf.entry));
    assert.equal(p.fetches.length, initialFetches, 'PDF corrupt/missing/aux/incomplete must not recover via network');
    const optionalPdf = harness({ missingPdfRole: true }); await optionalPdf.install();
    assert.equal((await optionalPdf.message({ kind: 'pdf' })).ok, false);
    assert.equal((await optionalPdf.message()).ok, true, 'absent PDF manifest role must not disable compile/source startup');
    assert.equal((await optional.message({ kind: 'pdf' })).ok, true, 'absent compile role must not disable PDF admission');
    const bigPdf = harness({ pdfBody: Buffer.alloc(32769, 32) }); await bigPdf.install();
    assert.equal((await bigPdf.message({ kind: 'pdf' })).ok, false);

    const d = harness(); await d.install(); const converter = await d.message({ kind: 'dvi' });
    assert.equal(converter.ok, true); assert.equal(converter.digest, hex(dviWorker)); assert.equal(converter.source, dviPath);
    const converterResponse = await d.route(converter.entry);
    assert.equal(converterResponse.status, 200); assert.equal(converterResponse.headers.get('Content-Security-Policy'), policy);
    assert.deepEqual(Buffer.from(await converterResponse.arrayBuffer()), dviWorker);
    await refused(await d.route(dviPath, { destination: 'worker' }));
    await refused(await d.route(converter.entry.replace('kind=dvi', 'kind=compile')));
    d.stores.get(d.releaseName).delete(d.absolute(dviPath));
    assert.equal((await d.message({ kind: 'dvi' })).ok, false); await refused(await d.route(converter.entry));
    assert.equal((await d.message()).ok, false, 'declared missing asset makes this release incomplete');
    const noDvi = harness({ missingDviRole: true }); await noDvi.install();
    assert.equal((await noDvi.message()).ok, true, 'release without a converter role preserves normal compiler admission');
    assert.equal((await noDvi.message({ kind: 'dvi' })).ok, false);

    const unicodeEntries = [];
    for (const kind of ['xetex', 'luatex']) {
        const u = harness(); await u.install();
        const contract = await u.message({ kind }); unicodeEntries.push(contract.entry);
        assert.equal(contract.ok, true); assert.equal(contract.source, unicodePath);
        assert.equal(contract.digest, hex(unicodeWorker));
        const response = await u.route(contract.entry);
        assert.equal(response.status, 200);
        assert.equal(response.headers.get('Content-Security-Policy'), policy);
        assert.deepEqual(Buffer.from(await response.arrayBuffer()), unicodeWorker);
        await refused(await u.route(unicodePath, { destination: 'worker' }));
        await refused(await u.route(contract.entry.replace('kind=' + kind, 'kind=compile')));
        u.stores.get(u.releaseName).delete(u.absolute(unicodePath));
        assert.equal((await u.message({ kind })).ok, false);
        await refused(await u.route(contract.entry));
    }
    const noUnicode = harness({ missingUnicodeRole: true }); await noUnicode.install();
    assert.equal((await noUnicode.message()).ok, true);
    assert.equal((await noUnicode.message({ kind: 'xetex' })).ok, false);
    assert.equal((await noUnicode.message({ kind: 'luatex' })).ok, false);

    const devHandlers = new Map();
    vm.runInNewContext(fs.readFileSync(path.join(root, 'service-worker.js'), 'utf8'), { self: {
        location: { origin }, addEventListener(name, action) { devHandlers.set(name, action); } }, URL, Response });
    let devResponse;
    devHandlers.get('fetch')({ request: { url: origin + contract.entry, method: 'GET', destination: 'worker' }, respondWith(r) { devResponse = r; } });
    await refused(devResponse);
    let devReply, closed = 0;
    devHandlers.get('message')({ data: { type: 'native-worker-entry', requestId: 'a'.repeat(32) }, ports: [{ postMessage(r) { devReply = r; }, close() { closed++; } }] });
    assert.equal(devReply.ok, false); assert.equal(closed, 1);
    for (const entry of [pdf.entry, pdfPath, converter.entry, dviPath, unicodePath, ...unicodeEntries]) {
        devHandlers.get('fetch')({ request: { url: origin + entry, method: 'GET', destination: 'worker' }, respondWith(r) { devResponse = r; } });
        await refused(devResponse);
    }
    devHandlers.get('message')({ data: { type: 'native-worker-entry', kind: 'pdf', requestId: 'a'.repeat(32) },
        ports: [{ postMessage(r) { devReply = r; }, close() {} }] });
    assert.equal(devReply.ok, false); assert.equal(devReply.kind, 'pdf');
    console.log('Native compile+PDF entry Node handlers PASS: exact role/SRI/classic bytes/policy, namespace/protocol/release/destination, shared bound4, optional-role independence, corrupt/missing/incomplete/aux/dev refusal; no native execution.');
})().catch(error => { console.error(error); process.exitCode = 1; }).finally(() => clearTimeout(deadline));
