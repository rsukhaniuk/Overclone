// Dedicated local WASM transport. No rule thresholds, PDF bytes, source, DOM,
// credentials or provider calls. Only the fixed pure Rust export is invoked.
import init, { evaluate_rendered_extent } from '../rust-generated/document_engine.js';
let used = false;
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
self.onmessage = async ({data}) => {
    if (used) return;
    used = true;
    if (!data || Object.keys(data).sort().join(',') !== 'requestId,requestJson,version,workerEpoch'
        || data.version !== 1 || typeof data.requestId !== 'string' || !/^[0-9a-f]{32}$/.test(data.requestId)
        || !Number.isSafeInteger(data.workerEpoch) || data.workerEpoch < 1 || !bounded(data.requestJson, 1024)) return;
    const {requestId, workerEpoch, requestJson} = data;
    const send = (status, responseJson = null) => self.postMessage({version: 1, requestId, workerEpoch, status, responseJson});
    try {
        if (JSON.parse(requestJson)?.requestId !== requestId) { send('invalidResponse'); return; }
        await init(); // Bundled same-release document_engine_bg.wasm, never a CDN.
        const responseJson = evaluate_rendered_extent(requestJson);
        if (!bounded(responseJson, 8192)) { send('invalidResponse'); return; }
        send('completed', responseJson);
    } catch { send('unavailable'); } // Fixed status only; never reflect exception text.
};
