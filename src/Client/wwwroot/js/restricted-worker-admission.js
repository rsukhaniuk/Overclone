// Browser-only admission for fixed application worker entries. No orchestration,
// runtime selection, document payloads or caller-selected script/policy URLs.
const ROLES = Object.freeze({
    compile: Object.freeze({ source: '/js/compile-port-worker.js', entry: '/__oop/native-worker/compile' }),
    dvi: Object.freeze({ source: '/js/dvi-port-worker.js', entry: '/__oop/native-worker/dvi' }),
    xetex: Object.freeze({ source: '/js/unicode-tex-port-worker.js', entry: '/__oop/native-worker/xetex' }),
    luatex: Object.freeze({ source: '/js/unicode-tex-port-worker.js', entry: '/__oop/native-worker/luatex' }),
    biber: Object.freeze({ source: '/js/biber-port-worker.js', entry: '/__oop/native-worker/biber' }),
    pdf: Object.freeze({ source: '/js/pdf-port-worker.js', entry: '/__oop/native-worker/pdf' }),
    synctex: Object.freeze({ source: '/js/synctex-query-worker.js', entry: '/__oop/native-worker/synctex' }),
});
const POLICY = "default-src 'none'; script-src blob: 'wasm-unsafe-eval'; connect-src 'none'; worker-src 'none'; object-src 'none'";
const MAX_PORTS = 16, TIMEOUT = 5000, SOURCE_LIMIT = 32768;
const states = new Map();
let ports = 0, serviceWorkers = null;
function checkBinding(binding) {
    if (!binding || navigator.serviceWorker?.controller !== binding.controller ||
        binding.controller.state !== 'activated' || binding.controller.scriptURL !== location.origin + '/service-worker.js' ||
        binding.registration.scope !== location.origin + '/' || binding.registration.active !== binding.controller)
        throw new Error('matching activated production controller required');
}
function abortable(promise, signal) {
    return new Promise((resolve, reject) => {
        const abort = () => { signal.removeEventListener('abort', abort); reject(new Error('worker admission cancelled or timed out')); };
        // Observe the foreign promise even if cancellation happened during the
        // synchronous API call that produced it (registration/fetch may reject).
        Promise.resolve(promise).then(value => { signal.removeEventListener('abort', abort); resolve(value); },
            error => { signal.removeEventListener('abort', abort); reject(error); });
        if (signal.aborted) { abort(); return; }
        signal.addEventListener('abort', abort, { once: true });
    });
}
async function activatedController(signal) {
    const sw = navigator.serviceWorker, controller = sw?.controller;
    if (!controller || controller.scriptURL !== location.origin + '/service-worker.js') throw new Error('production controller required');
    const registration = await abortable(sw.getRegistration(location.origin + '/'), signal);
    if (!registration || registration.scope !== location.origin + '/' || registration.active !== controller) throw new Error('production scope required');
    if (controller.state !== 'activated') await new Promise((resolve, reject) => {
        const cleanup = () => { controller.removeEventListener('statechange', changed); signal.removeEventListener('abort', abort); };
        const abort = () => { cleanup(); reject(new Error('controller activation cancelled or timed out')); };
        const changed = () => {
            if (sw.controller !== controller || controller.state === 'redundant') { abort(); return; }
            if (controller.state === 'activated') { cleanup(); resolve(); }
        };
        controller.addEventListener('statechange', changed); signal.addEventListener('abort', abort, { once: true });
        if (signal.aborted) abort(); else changed();
    });
    const binding = { controller, registration }; checkBinding(binding); return binding;
}
async function entryContract(binding, kind, signal) {
    const role = ROLES[kind];
    const requestId = Array.from(crypto.getRandomValues(new Uint8Array(16)), v => v.toString(16).padStart(2, '0')).join('');
    const channel = new MessageChannel();
    try {
        const reply = await new Promise((resolve, reject) => {
            const cleanup = () => { channel.port1.onmessage = null; channel.port1.onmessageerror = null; signal.removeEventListener('abort', abort); };
            const fail = () => { cleanup(); reject(new Error('native worker contract refused')); };
            const abort = () => fail();
            channel.port1.onmessage = event => { cleanup(); resolve(event.data); };
            channel.port1.onmessageerror = fail; signal.addEventListener('abort', abort, { once: true });
            if (signal.aborted) { fail(); return; }
            try { checkBinding(binding); binding.controller.postMessage({ type: 'native-worker-entry', protocol: 1, kind, requestId }, [channel.port2]); }
            catch { fail(); }
        });
        if (!reply || Object.keys(reply).sort().join(',') !== 'digest,entry,kind,ok,policy,protocol,release,requestId,source,sri,type' ||
            reply.type !== 'native-worker-entry-result' || reply.requestId !== requestId || reply.ok !== true || reply.protocol !== 1 || reply.kind !== kind ||
            typeof reply.release !== 'string' || reply.release.length === 0 || reply.release.length > 128 || !/^[A-Za-z0-9+/_-]+={0,2}$/.test(reply.release) ||
            typeof reply.digest !== 'string' || !/^[0-9a-f]{64}$/.test(reply.digest) || reply.source !== role.source || reply.policy !== POLICY)
            throw new Error('native worker contract mismatch');
        const entry = role.entry + '?' + new URLSearchParams({ protocol: '1', kind, release: reply.release, digest: reply.digest });
        if (reply.entry !== entry) throw new Error('native worker entry mismatch');
        return Object.freeze({ ...binding, kind, entry, release: reply.release, digest: reply.digest, sri: reply.sri });
    } finally { channel.port1.close(); channel.port2.close(); }
}
async function verifySource(binding, signal) {
    const response = await fetch(new URL(ROLES[binding.kind].source, location.origin), { cache: 'no-store', credentials: 'omit', redirect: 'error', signal });
    if (!response.ok || !response.body) throw new Error('explicit worker provisioning failed');
    const reader = response.body.getReader(), chunks = []; let size = 0, complete = false;
    try {
        for (;;) {
            const { done, value } = await reader.read(); if (done) { complete = true; break; }
            size += value.byteLength; if (size > SOURCE_LIMIT) throw new Error('worker byte bound'); chunks.push(value);
        }
    } finally { if (!complete) reader.cancel().catch(() => {}); reader.releaseLock(); }
    if (size === 0) throw new Error('empty worker source');
    const bytes = new Uint8Array(size); let offset = 0;
    for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
    new TextDecoder('utf8', { fatal: true }).decode(bytes); // validate, never rewrite executable source
    const hash = new Uint8Array(await crypto.subtle.digest('SHA-256', bytes));
    if (Array.from(hash, v => v.toString(16).padStart(2, '0')).join('') !== binding.digest ||
        'sha256-' + btoa(String.fromCharCode(...hash)) !== binding.sri) throw new Error('worker source integrity mismatch');
}
function controllerChanged() {
    for (const state of [...states.values()]) {
        state.binding = null;
        state.round?.cancel.abort();
        for (const client of [...state.clients]) {
            try { client.invalidated(); } catch { /* one port's cleanup must not prevent another's */ }
        }
    }
}
function startRound(state, kind) {
    const round = { cancel: new AbortController(), promise: null };
    const timer = setTimeout(() => round.cancel.abort(), TIMEOUT);
    state.round = round;
    round.promise = (async () => {
        try {
            const binding = await activatedController(round.cancel.signal);
            const candidate = await entryContract(binding, kind, round.cancel.signal);
            await abortable(verifySource(candidate, round.cancel.signal), round.cancel.signal);
            checkBinding(candidate);
            if (round.cancel.signal.aborted || state.round !== round) throw new Error('worker admission cancelled');
            state.binding = candidate; return candidate;
        } finally { clearTimeout(timer); if (state.round === round) state.round = null; }
    })();
    // Observe immediately, including when the last waiting port disposes.
    round.promise.catch(() => {});
    return round;
}
export function createRestrictedWorkerAdmission(kind, invalidated = () => {}) {
    if (!Object.hasOwn(ROLES, kind) || typeof invalidated !== 'function') throw new Error('fixed worker kind required');
    if (ports >= MAX_PORTS) throw new Error('worker admission port bound');
    if (!states.has(kind)) states.set(kind, { binding: null, round: null, clients: new Set(), waiters: new Set() });
    const state = states.get(kind), client = { invalidated };
    let disposed = false, pending = null;
    state.clients.add(client); ports++;
    if (!serviceWorkers && navigator.serviceWorker) {
        serviceWorkers = navigator.serviceWorker; serviceWorkers.addEventListener('controllerchange', controllerChanged);
    }
    function check(binding = state.binding) {
        if (disposed || !binding || binding !== state.binding) throw new Error('worker entry not admitted');
        checkBinding(binding); return binding;
    }
    function releaseWaiter(waiter) {
        state.waiters.delete(waiter);
        if (pending === waiter) pending = null;
        if (state.round === waiter.round && ![...state.waiters].some(value => value.round === waiter.round)) {
            waiter.round.cancel.abort(); state.round = null;
        }
    }
    function cancelPending() {
        if (!pending) return;
        const waiter = pending; waiter.cancel.abort(); releaseWaiter(waiter);
    }
    function provision() {
        if (disposed) return Promise.reject(new Error('disposed worker admission port'));
        if (state.binding) { try { return Promise.resolve(check()); } catch (error) { return Promise.reject(error); } }
        if (pending) return pending.promise;
        const round = state.round || startRound(state, kind);
        const waiter = { round, cancel: new AbortController(), promise: null };
        state.waiters.add(waiter); pending = waiter;
        waiter.promise = abortable(round.promise, waiter.cancel.signal).then(binding => check(binding));
        waiter.promise.then(() => releaseWaiter(waiter), () => releaseWaiter(waiter));
        return waiter.promise;
    }
    function dispose() {
        if (disposed) return;
        disposed = true; cancelPending(); state.clients.delete(client); ports--;
        if (!state.clients.size) { state.round?.cancel.abort(); state.binding = null; states.delete(kind); }
        if (!ports && serviceWorkers) { serviceWorkers.removeEventListener('controllerchange', controllerChanged); serviceWorkers = null; }
    }
    return Object.freeze({ provision, check, cancelPending, dispose });
}
