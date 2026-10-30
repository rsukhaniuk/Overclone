// Browser/WASM transport only: no lexical/package/catalog/source policy.
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
const id = (s) => bounded(s, 128) && s.length > 0;
const viewOf = (b) =>
    b instanceof ArrayBuffer
        ? new Uint8Array(b)
        : b instanceof Uint8Array
          ? b
          : null;
export function createAuthoringAssistPort() {
    let active = null,
        epoch = 0,
        disposed = false,
        cancelledId = null;
    const reply = (requestId, workerEpoch, status, responseJson = null) => ({
        version: 1,
        requestId,
        workerEpoch,
        status,
        responseJson,
    });
    function cancel(requestId) {
        if (disposed || !id(requestId)) return;
        if (active?.id === requestId) active.finish("cancelled");
        else cancelledId = requestId; // One bounded pre-dispatch tombstone, no queue.
    }
    function dispose() {
        disposed = true;
        cancelledId = null;
        active?.finish("disposed");
    }
    function evaluate(metadataJson, packedBytes) {
        if (!bounded(metadataJson, 4 * 1024 * 1024))
            return Promise.resolve(reply("", 0, "invalidInput"));
        let meta;
        try {
            meta = JSON.parse(metadataJson);
        } catch {
            return Promise.resolve(reply("", 0, "invalidInput"));
        }
        const requestId = meta?.requestId;
        if (
            !id(requestId) ||
            meta?.protocolVersion !== 1 ||
            meta?.operation !== "authoringAssist"
        )
            return Promise.resolve(
                reply(id(requestId) ? requestId : "", 0, "invalidInput"),
            );
        if (disposed) return Promise.resolve(reply(requestId, 0, "disposed"));
        if (active) return Promise.resolve(reply(requestId, 0, "busy"));
        if (cancelledId === requestId) {
            cancelledId = null;
            return Promise.resolve(reply(requestId, 0, "cancelled"));
        }
        const bytes = viewOf(packedBytes);
        if (!bytes || bytes.byteLength > 64 * 1024 * 1024)
            return Promise.resolve(reply(requestId, 0, "invalidInput"));
        if (epoch >= Number.MAX_SAFE_INTEGER)
            return Promise.resolve(reply(requestId, 0, "unavailable"));
        const ownEpoch = ++epoch;
        return new Promise((resolve) => {
            let worker = null,
                timer = null,
                settled = false;
            const operation = { id: requestId, finish };
            function finish(status, responseJson = null) {
                if (settled) return;
                settled = true;
                if (timer !== null) clearTimeout(timer);
                if (worker) {
                    worker.onmessage = null;
                    worker.onerror = null;
                    worker.onmessageerror = null;
                    try {
                        worker.terminate();
                    } catch {
                        /* Already retired. Never reflect exception text. */
                    }
                }
                if (active === operation) active = null;
                resolve(reply(requestId, ownEpoch, status, responseJson));
            }
            active = operation;
            try {
                timer = setTimeout(() => finish("timedOut"), 5000);
                // Bounds and admission precede the sole owned copy. Keep caller bytes attached.
                const copy = bytes.slice();
                worker = new Worker(
                    new URL("./authoring-assist-worker.js", import.meta.url),
                    { type: "module" },
                );
                worker.onerror = () => finish("unavailable");
                worker.onmessageerror = () => finish("invalidResponse");
                worker.onmessage = ({ data }) => {
                    if (settled || active !== operation) return;
                    const keys =
                        data && typeof data === "object"
                            ? Object.keys(data).sort().join(",")
                            : "";
                    if (
                        keys !==
                            "requestId,responseJson,status,version,workerEpoch" ||
                        data.version !== 1 ||
                        data.requestId !== requestId ||
                        data.workerEpoch !== ownEpoch ||
                        ![
                            "completed",
                            "unavailable",
                            "invalidResponse",
                        ].includes(data.status) ||
                        (data.status === "completed"
                            ? !bounded(data.responseJson, 262144)
                            : data.responseJson !== null)
                    ) {
                        finish("invalidResponse");
                        return;
                    }
                    finish(data.status, data.responseJson);
                };
                worker.postMessage(
                    {
                        version: 1,
                        operation: "authoringAssist",
                        requestId,
                        workerEpoch: ownEpoch,
                        metadataJson,
                        packedBytes: copy.buffer,
                    },
                    [copy.buffer],
                );
            } catch {
                finish("unavailable");
            }
        });
    }
    return { evaluate, cancel, dispose };
}
