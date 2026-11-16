// Dedicated local WASM transport. No rule thresholds, PDF bytes, source, DOM,
// credentials or provider calls. Only the fixed pure Rust export is invoked.
import init, { evaluate_rendered_review } from '../rust-generated/document_engine.js';
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
const typedLength = value => {
    try { return value instanceof Uint8Array && ArrayBuffer.isView(value)
        ? Object.getOwnPropertyDescriptor(Object.getPrototypeOf(Uint8Array.prototype), 'byteLength').get.call(value) : -1; }
    catch { return -1; }
};
self.onmessage = async ({data}) => {
    if (used) return;
    used = true;
    if (!data || Object.keys(data).sort().join(',') !== 'factsBytes,requestId,requestJson,version,workerEpoch'
        || data.version !== 1 || typeof data.requestId !== 'string' || !/^[0-9a-f]{32}$/.test(data.requestId)
        || !Number.isSafeInteger(data.workerEpoch) || data.workerEpoch < 1 || !bounded(data.requestJson, 262144)) return;
    const {requestId, workerEpoch, requestJson, factsBytes} = data;
    const send = (status, responseJson = null) => self.postMessage({version: 1, requestId, workerEpoch, status, responseJson});
    try {
        const request = JSON.parse(requestJson);
        if (request?.requestId !== requestId || request?.operation !== 'evaluateRenderedReview'
            || typedLength(factsBytes) < 1 || typedLength(factsBytes) > 4194304) { send('invalidResponse'); return; }
        const hash = [...new Uint8Array(await crypto.subtle.digest('SHA-256', factsBytes))].map(b => b.toString(16).padStart(2, '0')).join('');
        if (hash !== request?.payload?.factsSha256) { send('invalidResponse'); return; }
        const factsJson = new TextDecoder('utf-8', {fatal: true}).decode(factsBytes);
        await init(); // Bundled same-release document_engine_bg.wasm, never a CDN.
        const responseJson = evaluate_rendered_review(requestJson, factsJson);
        if (!bounded(responseJson, 524288)) { send('invalidResponse'); return; }
        send('completed', responseJson);
    } catch { send('unavailable'); } // Fixed status only; never reflect exception text.
};
