// Shared MODELED browser APIs, using actual product admission/bridge code.
// No worker source, loader or WASM is evaluated/executed by this fixture.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { createHash, webcrypto } = require('node:crypto');
const root = path.join(__dirname, '../../src/Client/wwwroot');
const workerBodies = Object.fromEntries(['compile', 'pdf', 'dvi'].map(kind => [kind, fs.readFileSync(path.join(root, 'js/' + kind + '-port-worker.js'))]));
workerBodies.xetex = workerBodies.luatex = fs.readFileSync(path.join(root, 'js/unicode-tex-port-worker.js'));
const sourceFor = kind => '/js/' + (['xetex', 'luatex'].includes(kind) ? 'unicode-tex' : kind) + '-port-worker.js';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const sri = bytes => 'sha256-' + createHash('sha256').update(bytes).digest('base64');
const policy = "default-src 'none'; script-src blob: 'wasm-unsafe-eval'; connect-src 'none'; worker-src 'none'; object-src 'none'";
const origin = 'https://bridge.test';
const tick = () => new Promise(resolve => setImmediate(resolve));
function product(name) {
    return fs.readFileSync(path.join(root, 'js/' + name), 'utf8')
        .replace(/^import \{ createRestrictedWorkerAdmission \} from ['"]\.\/restricted-worker-admission\.js['"];\r?\n/, '')
        .replaceAll('export function ', 'function ');
}
class Target {
    constructor() { this.listeners = new Map(); }
    addEventListener(type, fn) { if (!this.listeners.has(type)) this.listeners.set(type, new Set()); this.listeners.get(type).add(fn); }
    removeEventListener(type, fn) { this.listeners.get(type)?.delete(fn); }
    emit(type) { for (const fn of [...(this.listeners.get(type) || [])]) fn(); }
    count() { return [...this.listeners.values()].reduce((n, set) => n + set.size, 0); }
}
function harness(options = {}) {
    const control = { ...options }, stats = { urls: [], revoked: [], order: [], workers: [], ports: [], fetches: 0, messages: 0, kinds: [], hashes: 0, byteAllocations: 0 };
    const timers = new Map(); let timerId = 0;
    const sw = new Target(), controller = new Target();
    Object.assign(controller, { state: options.activating ? 'activating' : 'activated', scriptURL: origin + '/service-worker.js' });
    sw.controller = options.noController ? null : controller;
    const registration = { active: controller, scope: options.badScope ? origin + '/other/' : origin + '/' };
    sw.getRegistration = async () => registration;
    controller.postMessage = (request, ports) => {
        stats.messages++; stats.kinds.push(request.kind);
        if (control.onRequest) control.onRequest(request);
        if (control.dropReply || control.dropKinds?.has(request.kind)) return;
        const kind = request.kind, release = 'release-bridge-test', body = workerBodies[kind];
        assert.ok(body, 'fixed native role only');
        const digest = hash(body);
        const reply = { type: 'native-worker-entry-result', protocol: 1, kind, requestId: request.requestId,
            ok: true, release, source: sourceFor(kind), digest, sri: sri(body), policy,
            entry: '/__oop/native-worker/' + kind + '?' + new URLSearchParams({ protocol: '1', kind, release, digest }) };
        queueMicrotask(() => ports[0].peer.onmessage?.({ data: control.changeReply ? control.changeReply(reply) : reply }));
    };
    class Channel {
        constructor() {
            const make = () => ({ onmessage: null, onmessageerror: null, closed: 0, close() { this.closed++; } });
            this.port1 = make(); this.port2 = make(); this.port1.peer = this.port2; this.port2.peer = this.port1;
            stats.ports.push(this.port1, this.port2);
        }
    }
    class OwnedURL extends URL {
        static createObjectURL(blob) {
            if (control.urlThrows) throw new Error('modeled URL failure');
            const url = 'blob:owned-' + (stats.urls.length + 1); stats.urls.push({ url, blob });
            if (control.onUrl) control.onUrl(); return url;
        }
        static revokeObjectURL(url) { stats.revoked.push(url); stats.order.push('revoke'); if (control.revokeThrows) throw new Error('modeled revoke failure'); }
    }
    class Worker {
        constructor(url) {
            if (control.workerThrows) throw new Error('modeled constructor failure');
            this.url = String(url); this.messages = []; this.terminated = 0; stats.workers.push(this);
            if (control.onConstruct) control.onConstruct();
        }
        postMessage(message) { if (control.postThrows) throw new Error('modeled post failure'); this.messages.push(message); }
        terminate() { this.terminated++; stats.order.push('terminate'); if (control.terminateThrows) throw new Error('modeled terminate failure'); }
    }
    const ObservedBytes = new Proxy(Uint8Array, { construct(target, args) { stats.byteAllocations++; return Reflect.construct(target, args); } });
    const context = vm.createContext({ navigator: { serviceWorker: sw }, location: { origin }, URL: OwnedURL, URLSearchParams,
        AbortController, MessageChannel: Channel, crypto: { getRandomValues: value => webcrypto.getRandomValues(value),
            subtle: { async digest(algorithm, bytes) { stats.hashes++; if (control.hashGate) await control.hashGate; return webcrypto.subtle.digest(algorithm, bytes); } } },
        Uint8Array: ObservedBytes, ArrayBuffer, TextDecoder, TextEncoder, Blob, btoa, atob, structuredClone, console,
        async fetch(url, options) {
            stats.fetches++;
            const kind = Object.keys(workerBodies).find(role => String(url) === origin + sourceFor(role));
            assert.ok(kind, 'fixed same-origin source only'); assert.equal(options.redirect, 'error'); assert.equal(options.credentials, 'omit');
            return new Response(control.bodyByKind?.[kind] || control.body || workerBodies[kind]);
        }, Worker,
        setTimeout(fn, delay) {
            const id = ++timerId;
            const real = delay === 5000 ? setTimeout(() => { timers.delete(id); fn(); }, 120) : null;
            timers.set(id, { fn, real, delay }); return id;
        }, clearTimeout(id) { const timer = timers.get(id); if (timer?.real) clearTimeout(timer.real); timers.delete(id); } });
    vm.runInContext('globalThis.createRestrictedWorkerAdmission=(()=>{' + product('restricted-worker-admission.js') + ';return createRestrictedWorkerAdmission;})();', context);
    vm.runInContext('globalThis.api=(()=>{' + product('compile-port-bridge.js') + ';return {provision,provisionDvi,provisionXetex,provisionLuatex,execute,terminate,observations,dispose};})();', context);
    vm.runInContext('globalThis.createPdfPort=(()=>{' + product('pdf-port-bridge.js') + ';return createPdfPort;})();', context);
    const fireTimer = delay => { const pair = [...timers].find(([, t]) => t.delay === delay); assert.ok(pair); timers.delete(pair[0]); if (pair[1].real) clearTimeout(pair[1].real); pair[1].fn(); };
    return { api: context.api, createPdfPort: context.createPdfPort, createAdmission: context.createRestrictedWorkerAdmission,
        stats, sw, controller, registration, control, timers,
        changeController() { sw.controller = null; sw.emit('controllerchange'); }, timeoutPhase() { fireTimer(30000); }, fireTimer };
}
function frame(version = 1, id = 'job') {
    return { tag: { version, profile: version === 1 ? 0 : version === 2 ? 1 : 2, runtimeIdentity: version === 1 ? null : 'a'.repeat(64),
        job: id, request: '2'.repeat(32), epoch: 1, capture: '33333333-3333-4333-8333-333333333333',
        digest: hash(new Uint8Array([4])), ordinal: 1, nonce: '4'.repeat(32), snapshot: null,
        engineId: null, tool: 'latex' }, loader: new Uint8Array([1, 2]), wasm: new Uint8Array([3]),
        files: [{ name: 'main.tex', bytes: new Uint8Array([4]) }], outputs: ['main.pdf'], maxLogBytes: 100, maxOutputBytes: 100,
        mainEntry: 'main.tex', selectedRoot: 'main.tex', runtimeResourceNames: [], generatedInputNames: [] };
}
module.exports = { harness, frame, tick, origin, policy, workerBodies, hash };
