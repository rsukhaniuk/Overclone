import { createRestrictedWorkerAdmission } from './restricted-worker-admission.js';

// Browser lifecycle/byte transport only. C# owns product validation and hashes.
export function createSyncTeXQueryPort() {
    let disposed = false, active = null, preparation = null, admission = null;
    const cancelled = new Set(); // bounded, never forget a cancelled ID and allow it to start late
    const terminal = (id, status) => ({ version: 1, requestId: id, status, nativeStatus: null, json: null });
    try {
        admission = createRestrictedWorkerAdmission('synctex', () => {
            if (active) settle(active, terminal(active.id, 'unavailable'));
        });
    } catch { /* Missing fixed role/allowlist is safely unavailable, never a blob worker fallback. */ }
    const bufferLength = Object.getOwnPropertyDescriptor(ArrayBuffer.prototype, 'byteLength').get;
    const viewLength = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(Uint8Array.prototype), 'byteLength').get;
    function length(value) {
        try {
            if (value instanceof Uint8Array && ArrayBuffer.isView(value)) return viewLength.call(value);
            if (value instanceof ArrayBuffer) return bufferLength.call(value);
        } catch { /* detached/proxy/incompatible payload */ }
        return -1;
    }
    function finish(entry, response) {
        if (entry.finished) return;
        entry.finished = true;
        if (entry.worker) {
            entry.worker.onmessage = entry.worker.onerror = entry.worker.onmessageerror = null;
            try { entry.worker.terminate(); } catch { response = terminal(entry.id, 'failed'); }
        }
        const url = entry.loaderUrl;
        entry.loaderUrl = null;
        if (url) {
            try { URL.revokeObjectURL(url); } catch { response = terminal(entry.id, 'failed'); }
        }
        entry.resolve(response);
    }
    function settle(entry, response) {
        if (entry.settled || active !== entry) return;
        entry.settled = true;
        active = null;
        if (response.status === 'timed-out' && !cancelled.has(entry.id)) {
            if (cancelled.size < 64) cancelled.add(entry.id);
            else { disposed = true; admission?.dispose(); } // bounded fail-closed tombstones
        }
        clearTimeout(entry.timer);
        admission?.cancelPending();
        // Reentrant Worker/URL construction cannot leak a resource acquired after cancellation.
        if (entry.starting) { entry.terminal = response; return; }
        finish(entry, response);
    }
    function validQueryFields(r) {
        const coordinate=v=>typeof v==='number'&&Number.isFinite(v)&&Math.abs(v)<=1000000;
        if(!r||r.version!==1||typeof r.requestId!=='string'||!/^[0-9a-f]{32}$/.test(r.requestId)
            ||!coordinate(r.h)||!coordinate(r.v)||!Number.isSafeInteger(r.page))return false;
        if(r.direction==='inverse')return r.file===null&&r.line===0&&r.page>=1&&r.page<=512;
        if(r.direction!=='forward'||!Number.isSafeInteger(r.line)||r.line<1||r.line>2147483647||r.page<0||r.page>512)return false;
        const f=r.file;
        return typeof f==='string'&&f.length>0&&f.length<=1024&&f.isWellFormed()
            &&new TextEncoder().encode(f).length<=1024&&!/[\u0000-\u001f\u007f-\u009f\u2028\u2029\\:]/.test(f)
            &&f.split('/').every(p=>p&&p!=='.'&&p!=='..');
    }
    function query(metadata, mapBytes, loaderBytes, wasmBytes) {
        // Bound strings/arrays before parsing or allocating any owned copy.
        if (typeof metadata !== 'string' || metadata.length > 8192)
            return Promise.resolve(terminal('', 'failed'));
        let source;
        try { source = JSON.parse(metadata); } catch { return Promise.resolve(terminal('', 'failed')); }
        if (!source || typeof source !== 'object' || Array.isArray(source) || source.version !== 1 ||
            typeof source.requestId !== 'string' || !/^[0-9a-f]{32}$/.test(source.requestId) ||
            !Number.isInteger(source.deadlineMs) || source.deadlineMs < 1 || source.deadlineMs > 15000)
            return Promise.resolve(terminal('', 'failed'));
        const id = source.requestId;
        if (Object.hasOwn(source, 'queryProfile') && source.queryProfile !== 15) return Promise.resolve(terminal(id, 'failed'));
        if (!validQueryFields(source)) return Promise.resolve(terminal(id, 'failed'));
        if (disposed) return Promise.resolve(terminal(id, 'disposed'));
        if (cancelled.has(id)) return Promise.resolve(terminal(id, 'cancelled'));
        if (active || preparation) return Promise.resolve(terminal(id, 'busy'));
        if (!admission) return Promise.resolve(terminal(id, 'unavailable'));
        const ml = length(mapBytes), ll = length(loaderBytes), wl = length(wasmBytes);
        if (ml < 1 || ml > 1048575 || ll < 1 || ll > 1048576 || wl < 8 || wl > 1048576)
            return Promise.resolve(terminal(id, 'failed'));
        // Explicit allowlist: only selected query fields cross into the worker.
        const request = { version: 1, requestId: id, direction: source.direction, file: source.file,
            line: source.line, page: source.page, h: source.h, v: source.v };
        if (source.queryProfile === 15) request.queryProfile = 15;
        const reservation = {};
        preparation = reservation;
        let map, loader, wasm;
        try {
            map = new Uint8Array(mapBytes).slice();
            loader = new Uint8Array(loaderBytes).slice();
            wasm = new Uint8Array(wasmBytes).slice();
        } catch {
            preparation = null;
            return Promise.resolve(terminal(id, 'failed'));
        }
        return new Promise(resolve => {
            const entry = { id, resolve, worker: null, loaderUrl: null, settled: false,
                finished: false, starting: false, timer: null, terminal: null };
            active = entry;
            entry.timer = setTimeout(() => settle(entry, terminal(id, 'timed-out')), source.deadlineMs);
            (async () => {
                try {
                    const binding = await admission.provision();
                    if (active !== entry || entry.settled || disposed || cancelled.has(id)) return;
                    admission.check(binding);
                    entry.starting = true;
                    try {
                        entry.loaderUrl = URL.createObjectURL(new Blob([loader], { type: 'text/javascript' }));
                        if (!entry.settled && active === entry && !disposed) {
                            admission.check(binding);
                            entry.worker = new Worker(new URL(binding.entry, location.origin));
                        }
                    } finally {
                        entry.starting = false;
                        if (entry.settled) finish(entry, entry.terminal);
                    }
                    if (active !== entry || entry.settled || disposed) return;
                    const worker = entry.worker;
                    worker.onmessage = ({ data }) => {
                        if (active !== entry || entry.settled) return;
                        const reply = data?.response;
                        if (data?.kind !== 'terminal' || !reply || reply.version !== 1 || reply.requestId !== id ||
                            typeof reply.status !== 'string' || reply.status.length > 32 ||
                            (reply.nativeStatus !== null && typeof reply.nativeStatus !== 'number') ||
                            (reply.json !== null && (length(reply.json) < 1 || length(reply.json) > 32768))) {
                            settle(entry, terminal(id, 'failed')); return;
                        }
                        // Never forward native logs, unexpected object fields or buffers.
                        settle(entry, { version: 1, requestId: id, status: reply.status,
                            nativeStatus: reply.nativeStatus, json: reply.json });
                    };
                    worker.onerror = worker.onmessageerror = () => settle(entry, terminal(id, 'failed'));
                    admission.check(binding);
                    worker.postMessage({ request, map, loader, wasm, loaderUrl: entry.loaderUrl }, [map.buffer, loader.buffer, wasm.buffer]);
                } catch {
                    if (active === entry) settle(entry, terminal(id, 'unavailable'));
                } finally {
                    if (preparation === reservation) preparation = null;
                }
            })();
        });
    }
    function cancel(id) {
        if (typeof id !== 'string' || !/^[0-9a-f]{32}$/.test(id) || disposed) return;
        if (!cancelled.has(id)) {
            if (cancelled.size >= 64) { dispose(); return; } // fail closed rather than evict a tombstone
            cancelled.add(id);
        }
        if (active?.id === id) settle(active, terminal(id, 'cancelled'));
    }
    function dispose() {
        if (disposed) return;
        disposed = true;
        if (active) settle(active, terminal(active.id, 'disposed'));
        admission?.dispose();
    }
    return { query, cancel, dispose };
}
