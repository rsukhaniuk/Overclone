import { pdfWorkerMain } from "./pdf-port-worker.js";

// Dedicated per-adapter port; no global worker, unbounded queue or runtime URLs.
export function createPdfPort() {
    let disposed = false,
        active = null,
        earlyCancel = null;
    const workerUrl = URL.createObjectURL(
        new Blob(["(" + pdfWorkerMain.toString() + ")();"], {
            type: "text/javascript",
        }),
    );
    const digest = async (bytes) =>
        [...new Uint8Array(await crypto.subtle.digest("SHA-256", bytes))]
            .map((v) => v.toString(16).padStart(2, "0"))
            .join("");
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
        if (entry.worker) {
            entry.worker.onmessage = null;
            entry.worker.onerror = null;
            entry.worker.onmessageerror = null;
            try {
                entry.worker.terminate();
            } catch {
                response = terminal(entry.request, "transport-error");
            }
        }
        entry.resolve(response);
    }
    function inspect(metadata, documentBytes, loaderBytes, wasmBytes) {
        let request;
        try {
            request = JSON.parse(metadata);
        } catch {
            return Promise.reject(new Error("Invalid PDF transport envelope"));
        }
        if (
            request.version !== 2 || typeof request.inspectActions !== "boolean" ||
            typeof request.requestId !== "string" ||
            request.requestId.length > 64 ||
            !Number.isSafeInteger(request.epoch) ||
            request.epoch <= 0 ||
            !request.budget ||
            !Number.isInteger(request.budget.deadlineMs) ||
            request.budget.deadlineMs <= 0 ||
            request.budget.deadlineMs > 30000
        )
            return Promise.resolve(terminal(request, "transport-error"));
        if (disposed) return Promise.resolve(terminal(request, "disposed"));
        if (earlyCancel === request.requestId) {
            earlyCancel = null;
            return Promise.resolve(terminal(request, "cancelled"));
        }
        if (active) return Promise.resolve(terminal(request, "unavailable"));
        // Snapshot identity and owned arrays BEFORE any async hash/loading work.
        const document = new Uint8Array(documentBytes),
            loader = new Uint8Array(loaderBytes),
            wasm = new Uint8Array(wasmBytes);
        return new Promise((resolve) => {
            const entry = {
                request,
                requestId: request.requestId,
                documentSha256: request.documentSha256,
                epoch: request.epoch,
                resolve,
                worker: null,
                settled: false,
                timer: null,
            };
            active = entry;
            entry.timer = setTimeout(
                () => settle(entry, terminal(request, "timed-out")),
                request.budget.deadlineMs,
            );
            (async () => {
                try {
                    const hashes = await Promise.all([
                        digest(document),
                        digest(loader),
                        digest(wasm),
                    ]);
                    if (active !== entry || entry.settled || disposed) return;
                    if (
                        hashes[0] !== entry.documentSha256 ||
                        hashes[1] !== request.loaderSha256 ||
                        hashes[2] !== request.wasmSha256
                    ) {
                        settle(entry, terminal(request, "unavailable"));
                        return;
                    }
                    const worker = new Worker(workerUrl);
                    entry.worker = worker;
                    worker.onmessage = ({ data }) => {
                        if (active !== entry || entry.settled) return;
                        if (data.kind === "before-pdf-entry") return; // never called blocking-preemption evidence
                        const value = data.response;
                        if (
                            data.kind !== "terminal" ||
                            !value ||
                            value.requestId !== entry.requestId ||
                            value.documentSha256 !== entry.documentSha256 ||
                            value.epoch !== entry.epoch ||
                            value.loaderSha256 !== entry.request.loaderSha256 ||
                            value.wasmSha256 !== entry.request.wasmSha256
                        ) {
                            settle(entry, terminal(request, "transport-error"));
                            return;
                        }
                        settle(entry, value);
                    };
                    worker.onerror = () => {
                        if (active === entry)
                            settle(entry, terminal(request, "transport-error"));
                    };
                    worker.onmessageerror = () => {
                        if (active === entry)
                            settle(entry, terminal(request, "transport-error"));
                    };
                    worker.postMessage({ request, document, loader, wasm }, [
                        document.buffer,
                        loader.buffer,
                        wasm.buffer,
                    ]);
                } catch {
                    if (active === entry)
                        settle(entry, terminal(request, "transport-error"));
                }
            })();
        });
    }
    function cancel(id) {
        if (typeof id !== "string" || id.length > 64 || disposed) return;
        if (active && active.requestId === id)
            settle(active, terminal(active.request, "cancelled"));
        else if (!active) earlyCancel = id; // single admission means at most one pre-dispatch cancel tombstone
    }
    function dispose() {
        if (disposed) return;
        disposed = true;
        earlyCancel = null;
        if (active) settle(active, terminal(active.request, "disposed"));
        URL.revokeObjectURL(workerUrl);
    }
    return { inspect, cancel, dispose };
}
