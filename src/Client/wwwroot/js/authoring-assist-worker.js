// Dedicated one-use local WASM loader/transport; no document interpretation.
import init, * as engine from "../rust-generated/document_engine.js";
let used = false;
const validText = (s) => {
    if (typeof s !== "string") return false;
    for (let i = 0; i < s.length; i++) {
        const c = s.charCodeAt(i);
        if (c >= 0xd800 && c <= 0xdbff) {
            const n = s.charCodeAt(++i);
            if (!(n >= 0xdc00 && n <= 0xdfff)) return false;
        } else if (c >= 0xdc00 && c <= 0xdfff) return false;
    }
    return true;
};
const bounded = (s, n) =>
    typeof s === "string" &&
    s.length <= n &&
    validText(s) &&
    new TextEncoder().encode(s).length <= n;
self.onmessage = async ({ data }) => {
    if (used) return;
    used = true;
    if (
        !data ||
        Object.keys(data).sort().join(",") !==
            "metadataJson,operation,packedBytes,requestId,version,workerEpoch" ||
        data.version !== 1 ||
        data.operation !== "authoringAssist" ||
        !bounded(data.requestId, 128) ||
        !data.requestId.length ||
        !Number.isSafeInteger(data.workerEpoch) ||
        data.workerEpoch < 1 ||
        !bounded(data.metadataJson, 4 * 1024 * 1024) ||
        !(data.packedBytes instanceof ArrayBuffer) ||
        data.packedBytes.byteLength > 64 * 1024 * 1024
    )
        return;
    const { requestId, workerEpoch, metadataJson, packedBytes } = data;
    const send = (status, responseJson = null) =>
        self.postMessage({
            version: 1,
            requestId,
            workerEpoch,
            status,
            responseJson,
        });
    try {
        const meta = JSON.parse(metadataJson); // Routing correlation only; preserve original JSON.
        if (
            meta?.requestId !== requestId ||
            meta?.protocolVersion !== 1 ||
            meta?.operation !== "authoringAssist"
        ) {
            send("invalidResponse");
            return;
        }
        await init(); // Bundled same-release WASM only; no CDN/provider/runtime downloads.
        if (typeof engine.authoring_assist !== "function") {
            send("unavailable");
            return;
        }
        const responseJson = engine.authoring_assist(
            metadataJson,
            new Uint8Array(packedBytes),
        );
        if (!bounded(responseJson, 262144)) {
            send("invalidResponse");
            return;
        }
        send("completed", responseJson);
    } catch {
        send("unavailable");
    }
};
