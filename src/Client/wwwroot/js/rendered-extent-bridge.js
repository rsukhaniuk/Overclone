// Browser Worker/message/lifetime glue ONLY. C# owns PDF authority; Rust owns
// extent policy. One independently disposable port, one actual worker at a time.
const id = s => typeof s === 'string' && /^[0-9a-f]{32}$/.test(s);
const validText = s => {
    if (typeof s !== 'string') return false;
    for (let i = 0; i < s.length; i++) {
        const c = s.charCodeAt(i);
        if (c >= 0xd800 && c <= 0xdbff) { const n = s.charCodeAt(++i); if (!(n >= 0xdc00 && n <= 0xdfff)) return false; }
        else if (c >= 0xdc00 && c <= 0xdfff) return false;
    }
    return true;
};
const bounded = (s, n) => typeof s === 'string' && s.length <= n && validText(s) && new TextEncoder().encode(s).length <= n;
export function createRenderedExtentPort() {
    let active = null, epoch = 0, disposed = false, cancelledId = null;
    const reply = (requestId, workerEpoch, status, responseJson = null) => ({version: 1, requestId, workerEpoch, status, responseJson});
    function cancel(requestId) {
        if (disposed || !id(requestId)) return;
        if (active?.id === requestId) active.finish('cancelled');
        else cancelledId = requestId; // One bounded pre-dispatch cancellation tombstone.
    }
    function dispose() { disposed = true; cancelledId = null; active?.finish('disposed'); }
    function evaluate(requestJson) {
        let requestId = '';
        if (!bounded(requestJson, 1024)) return Promise.resolve(reply(requestId, 0, 'invalidInput'));
        try { requestId = JSON.parse(requestJson)?.requestId; } catch { return Promise.resolve(reply('', 0, 'invalidInput')); }
        if (!id(requestId)) return Promise.resolve(reply('', 0, 'invalidInput'));
        if (disposed) return Promise.resolve(reply(requestId, 0, 'disposed'));
        if (active) return Promise.resolve(reply(requestId, 0, 'busy'));
        if (cancelledId === requestId) { cancelledId = null; return Promise.resolve(reply(requestId, 0, 'cancelled')); }
        if (epoch >= Number.MAX_SAFE_INTEGER) return Promise.resolve(reply(requestId, 0, 'unavailable'));
        const ownEpoch = ++epoch;
        return new Promise(resolve => {
            let worker = null, timer = null, settled = false;
            const operation = { id: requestId, finish };
            function finish(status, responseJson = null) {
                if (settled) return;
                settled = true;
                if (timer !== null) clearTimeout(timer);
                try { if (worker) { worker.onmessage = null; worker.onerror = null; worker.terminate(); } }
                finally {
                    if (active === operation) active = null;
                    resolve(reply(requestId, ownEpoch, status, responseJson));
                }
            }
            active = operation;
            try {
                timer = setTimeout(() => finish('timedOut'), 5000);
                worker = new Worker(new URL('./rendered-extent-worker.js', import.meta.url), {type: 'module'});
                worker.onerror = () => finish('unavailable');
                worker.onmessage = ({data}) => {
                    if (settled || active !== operation) return;
                    const keys = data && typeof data === 'object' ? Object.keys(data).sort().join(',') : '';
                    if (keys !== 'requestId,responseJson,status,version,workerEpoch' || data.version !== 1
                        || data.requestId !== requestId || data.workerEpoch !== ownEpoch
                        || !['completed', 'unavailable', 'invalidResponse'].includes(data.status)
                        || (data.status === 'completed' ? !bounded(data.responseJson, 8192) : data.responseJson !== null)) {
                        finish('invalidResponse'); return;
                    }
                    finish(data.status, data.responseJson);
                };
                worker.postMessage({version: 1, requestId, workerEpoch: ownEpoch, requestJson});
            } catch { finish('unavailable'); }
        });
    }
    return {evaluate, cancel, dispose};
}
