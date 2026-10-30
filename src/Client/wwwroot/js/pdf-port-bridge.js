import { createRestrictedWorkerAdmission } from './restricted-worker-admission.js';

// Dedicated per-adapter port; no source Blob, global worker or unbounded queue.
export function createPdfPort() {
    let disposed = false, active = null, earlyCancel = null, preparation = null;
    const admission = createRestrictedWorkerAdmission('pdf', () => {
        if (active) settle(active, terminal(active.request, 'unavailable'));
    });
    const digest = async bytes =>
        [...new Uint8Array(await crypto.subtle.digest('SHA-256', bytes))]
            .map(v => v.toString(16).padStart(2, '0')).join('');
    const ownBytes = value => value instanceof Uint8Array ? new Uint8Array(value) : new Uint8Array(value).slice();
    const bufferLength = Object.getOwnPropertyDescriptor(ArrayBuffer.prototype, 'byteLength').get;
    const viewLength = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(Uint8Array.prototype), 'byteLength').get;
    function byteLength(value) {
        try {
            if (value instanceof Uint8Array && ArrayBuffer.isView(value)) return viewLength.call(value);
            if (value instanceof ArrayBuffer) return bufferLength.call(value);
        } catch { /* Proxies/detached or incompatible values are not byte payloads. */ }
        return -1;
    }
    const isHash = value => typeof value === 'string' && /^[0-9a-f]{64}$/.test(value);
    const terminal = (request, status) => ({
        version: 2,
        loaderSha256: request.loaderSha256,
        wasmSha256: request.wasmSha256,
        actionState: null, hasCapabilitiesApi: false, hasActionsApi: false,
        capabilitiesJson: null, actionsJson: null,
        requestId: request.requestId,
        documentSha256: request.documentSha256,
        epoch: request.epoch,
        status,
        nativeStatus: null,
        nativeError: 0,
        enteredNative: null,
        liveAfter: 0,
        pageCount: 0,
        factsJson: null,
        textJson: null,
        searchJson: null,
        raster: null,
        nativeCalls: [],
    });
    function settle(entry, response) {
        if (entry.settled || active !== entry) return;
        entry.settled = true;
        active = null;
        clearTimeout(entry.timer);
        admission.cancelPending(); // only this port's wait, never another port's preparation
        if (entry.starting) { entry.terminalResponse = response; return; }
        finish(entry, response);
    }
    function finish(entry, response) {
        if (entry.finished) return;
        entry.finished = true;
        if (entry.worker) {
            entry.worker.onmessage = null;
            entry.worker.onerror = null;
            entry.worker.onmessageerror = null;
            try { entry.worker.terminate(); }
            catch { response = terminal(entry.request, 'transport-error'); }
        }
        // Parent owns the loader URL, so terminating a worker cannot skip its
        // revocation by aborting a child's finally block. Remove ownership first.
        const loaderUrl = entry.loaderUrl;
        entry.loaderUrl = null;
        if (loaderUrl) {
            try { URL.revokeObjectURL(loaderUrl); }
            catch { response = terminal(entry.request, 'transport-error'); }
        }
        entry.resolve(response);
    }
    function validPdfRequest(r) {
        const b=r?.budget, positive=(v,max)=>Number.isSafeInteger(v)&&v>0&&v<=max;
        if(!r||r.version!==2||typeof r.inspectActions!=='boolean'||typeof r.raster!=='boolean'
            ||typeof r.requestId!=='string'||r.requestId.length<1||r.requestId.length>64
            ||!Number.isSafeInteger(r.epoch)||r.epoch<1
            ||![r.documentSha256,r.loaderSha256,r.wasmSha256].every(v=>typeof v==='string'&&/^[0-9a-f]{64}$/.test(v))
            ||!b||!positive(b.deadlineMs,30000)||!positive(b.inputBytes,8388608)||!positive(b.pages,512)
            ||!positive(b.textUnits,100000)||!positive(b.searchHits,256)||!positive(b.rasterPixels,4000000)
            ||!positive(b.outputBytes,16777216)||!positive(b.liveHandles,64)
            ||!Number.isSafeInteger(r.pageIndex)||r.pageIndex<0||r.pageIndex>=b.pages
            ||!positive(r.width,16384)||!positive(r.height,16384)||r.width*r.height>b.rasterPixels
            ||r.raster&&r.width*r.height*4+16>b.outputBytes)return false;
        return r.search===null||typeof r.search==='string'&&r.search.length>0&&r.search.length<=255
            &&r.search.isWellFormed()&&!r.search.includes('\0');
    }
    function inspect(metadata, documentBytes, loaderBytes, wasmBytes) {
        let request;
        try { request = JSON.parse(metadata); }
        catch { return Promise.reject(new Error('Invalid PDF transport envelope')); }
        if (!request || typeof request !== 'object' || Array.isArray(request))
            return Promise.reject(new Error('Invalid PDF transport envelope'));
        if (
            request.version !== 2 || typeof request.inspectActions !== 'boolean' ||
            typeof request.requestId !== 'string' || request.requestId.length === 0 || request.requestId.length > 64 ||
            !Number.isSafeInteger(request.epoch) || request.epoch <= 0 ||
            !request.budget || !Number.isInteger(request.budget.deadlineMs) ||
            request.budget.deadlineMs <= 0 || request.budget.deadlineMs > 30000 ||
            !Number.isSafeInteger(request.budget.inputBytes) || request.budget.inputBytes <= 0 || request.budget.inputBytes > 8388608 ||
            !isHash(request.documentSha256) || !isHash(request.loaderSha256) || !isHash(request.wasmSha256)
        ) return Promise.resolve(terminal(request, 'transport-error'));
        if (!validPdfRequest(request)) return Promise.resolve(terminal(request, 'transport-error'));
        if (disposed) return Promise.resolve(terminal(request, 'disposed'));
        if (earlyCancel === request.requestId) {
            earlyCancel = null;
            return Promise.resolve(terminal(request, 'cancelled'));
        }
        if (active || preparation) return Promise.resolve(terminal(request, 'unavailable'));
        // Existing managed PDF/package byte contracts, checked before any clone,
        // hash or admission. Never treat a plain Array/number as a typed length.
        const documentLength = byteLength(documentBytes), loaderLength = byteLength(loaderBytes), wasmLength = byteLength(wasmBytes);
        if (documentLength < 1 || documentLength > 8388608 || documentLength > request.budget.inputBytes ||
            loaderLength < 1 || loaderLength > 1048576 || wasmLength < 8 || wasmLength > 16777216)
            return Promise.resolve(terminal(request, 'transport-error'));
        // Actual preparation owns a slot BEFORE cloning. Logical cancellation
        // may settle active, but cannot retire unfinished hash operations.
        const reservation = {};
        preparation = reservation;
        let document, loader, wasm;
        try { document = ownBytes(documentBytes); loader = ownBytes(loaderBytes); wasm = ownBytes(wasmBytes); }
        catch {
            if (preparation === reservation) preparation = null;
            return Promise.resolve(terminal(request, 'transport-error'));
        }
        return new Promise(resolve => {
            const entry = { request, requestId: request.requestId, documentSha256: request.documentSha256,
                epoch: request.epoch, resolve, worker: null, loaderUrl: null, settled: false, timer: null,
                starting: false, finished: false, terminalResponse: null };
            active = entry;
            entry.timer = setTimeout(() => settle(entry, terminal(request, 'timed-out')), request.budget.deadlineMs);
            (async () => {
                try {
                    const tasks = [digest(document), digest(loader), digest(wasm)].map(task => task.catch(error => {
                        // Preserve prompt typed failure, but still observe ALL
                        // actual tasks before releasing this reservation.
                        if (active === entry) settle(entry, terminal(request, 'transport-error'));
                        throw error;
                    }));
                    const completed = await Promise.allSettled(tasks);
                    if (active !== entry || entry.settled || disposed) return;
                    if (completed.some(value => value.status === 'rejected')) return;
                    const hashes = completed.map(value => value.value);
                    if (hashes[0] !== entry.documentSha256 || hashes[1] !== request.loaderSha256 || hashes[2] !== request.wasmSha256) {
                        settle(entry, terminal(request, 'unavailable')); return;
                    }
                    let binding;
                    try { binding = await admission.provision(); }
                    catch { if (active === entry) settle(entry, terminal(request, 'unavailable')); return; }
                    if (active !== entry || entry.settled || disposed) return;
                    admission.check(binding);
                    // Do not revoke before a synchronously reentrant constructor
                    // returns ownership of its Worker (or throws without one).
                    entry.starting = true;
                    try {
                        entry.loaderUrl = URL.createObjectURL(new Blob([loader], { type: 'text/javascript' }));
                        if (!entry.settled && active === entry && !disposed) {
                            admission.check(binding);
                            entry.worker = new Worker(new URL(binding.entry, location.origin));
                        }
                    } finally {
                        entry.starting = false;
                        if (entry.settled) finish(entry, entry.terminalResponse);
                    }
                    if (active !== entry || entry.settled || disposed) return;
                    const worker = entry.worker;
                    worker.onmessage = ({ data }) => {
                        if (active !== entry || entry.settled) return;
                        if (data?.kind === 'before-pdf-entry') return; // never blocking-preemption evidence
                        const value = data?.response;
                        if (data?.kind !== 'terminal' || !value ||
                            value.requestId !== entry.requestId || value.documentSha256 !== entry.documentSha256 ||
                            value.epoch !== entry.epoch || value.loaderSha256 !== entry.request.loaderSha256 ||
                            value.wasmSha256 !== entry.request.wasmSha256) {
                            settle(entry, terminal(request, 'transport-error')); return;
                        }
                        settle(entry, value);
                    };
                    worker.onerror = () => { if (active === entry) settle(entry, terminal(request, 'transport-error')); };
                    worker.onmessageerror = () => { if (active === entry) settle(entry, terminal(request, 'transport-error')); };
                    admission.check(binding);
                    // URL is internal parent-owned transport state, never a field
                    // selected from C# metadata, a PDF document or a package record.
                    worker.postMessage({ request, document, loader, wasm, loaderUrl: entry.loaderUrl },
                        [document.buffer, loader.buffer, wasm.buffer]);
                } catch {
                    if (active === entry) settle(entry, terminal(request, 'transport-error'));
                } finally {
                    if (preparation === reservation) preparation = null;
                }
            })();
        });
    }
    function cancel(id) {
        if (typeof id !== 'string' || id.length > 64 || disposed) return;
        if (active && active.requestId === id) settle(active, terminal(active.request, 'cancelled'));
        else if (!active) earlyCancel = id;
    }
    function dispose() {
        if (disposed) return;
        disposed = true; earlyCancel = null;
        if (active) settle(active, terminal(active.request, 'disposed'));
        admission.dispose();
    }
    return { inspect, cancel, dispose };
}
