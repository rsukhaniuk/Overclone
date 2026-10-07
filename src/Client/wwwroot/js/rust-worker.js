import init, {
    count_words,
    initialize,
    analyze_source,
} from "../rust-generated/document_engine.js";
import { isValidOpaqueId } from "./worker-bridge.js";

const engine = init();

// Shared glue validators live in worker-bridge.js (no document-policy
// duplication). The worker outer routing validates the wrapper requestId FIRST
// (recovering a valid bounded id, never echoing an invalid/huge/lone-surrogate
// id), then protocol/operation, then — for analyzeSource — the metadata
// requestId/protocolVersion/operation correlation, BEFORE invoking WASM. The
// packed-byte buffer is resolved to a Uint8Array VIEW respecting byteOffset/
// byteLength (a subarray sub-slice reaches WASM as exactly the selected bytes).
// The worker posts back
//   { canonical: true, requestId: <trusted wrapper requestId>, responseJson: <Rust output string> }
// and the bridge correlates by the wrapper requestId and forwards
// responseJson unchanged. cancel is honored as an ACK shape only; the bridge
// terminates the worker for real cancellation (synchronous WASM cannot read a
// cancel message).

function canonicalFailed(requestId, code, message) {
    return JSON.stringify({
        protocolVersion: 1,
        requestId: requestId ?? null,
        status: "failed",
        error: { code, safeMessage: message, retryable: false },
    });
}

// Resolve the transferred packed-byte buffer to a Uint8Array VIEW that respects
// byteOffset/byteLength. A transferred ArrayBuffer is the whole buffer
// (byteOffset 0); a transferred Uint8Array view keeps its byteOffset/byteLength
// across the structured clone, so a subarray (direct-worker) sub-slice reaches
// WASM as exactly the selected bytes, NOT the whole backing buffer. A plain
// ArrayBuffer (zero-length included) is valid. Missing/invalid -> null (typed
// InvalidInput upstream; no empty substitution).
function resolvePackedBuffer(packedBytes) {
    if (packedBytes instanceof ArrayBuffer) return new Uint8Array(packedBytes);
    if (packedBytes instanceof Uint8Array) return packedBytes;
    if (packedBytes && packedBytes.buffer instanceof ArrayBuffer) {
        return new Uint8Array(
            packedBytes.buffer,
            packedBytes.byteOffset ?? 0,
            packedBytes.byteLength ?? packedBytes.buffer.byteLength,
        );
    }
    return null;
}

const UNKNOWN_OP_MESSAGE = "unsupported operation";

async function handleCanonical(msg) {
    // Route by PRESENCE of protocolVersion (validated below), so a request
    // carrying { protocolVersion:"1" } (string) still reaches the canonical
    // path and is rejected as UnsupportedProtocol, not handled as legacy.
    if (!("protocolVersion" in msg)) {
        return canonicalFailed(
            null,
            "UnsupportedProtocol",
            "missing protocolVersion",
        );
    }
    // Recover the wrapper requestId FIRST and use it EVERYWHERE (including the
    // outer postMessage wrapper), never echoing an invalid/huge/lone-surrogate
    // id outside responseJson.
    const requestId = isValidOpaqueId(msg.requestId) ? msg.requestId : null;
    if (typeof msg.protocolVersion !== "number" || msg.protocolVersion !== 1) {
        return canonicalFailed(
            requestId,
            "UnsupportedProtocol",
            "unsupported protocolVersion",
        );
    }
    if (requestId == null) {
        return canonicalFailed(
            null,
            "InvalidInput",
            "missing or invalid requestId",
        );
    }
    const op = msg.operation;
    if (op !== "initialize" && op !== "analyzeSource" && op !== "cancel") {
        // CONSTANT safeMessage: never interpolate an arbitrary/untrusted op.
        return canonicalFailed(
            requestId,
            "UnsupportedCapability",
            UNKNOWN_OP_MESSAGE,
        );
    }
    if (op === "cancel") {
        // The bridge terminates the worker for real cancellation; if a cancel
        // message ever arrives, honor the ACK shape without WASM work.
        const target =
            msg.payload && typeof msg.payload === "object"
                ? msg.payload.targetRequestId
                : null;
        return JSON.stringify({
            protocolVersion: 1,
            requestId,
            status: "completed",
            payload: { cancelledTargetRequestId: target ?? null },
        });
    }
    // Load the WASM engine only after outer routing is valid.
    await engine;
    if (op === "initialize") {
        const initReq = {
            protocolVersion: 1,
            requestId,
            operation: "initialize",
            payload: {},
        };
        return initialize(JSON.stringify(initReq));
    }
    // analyzeSource: forward the ORIGINAL metadataJson string (never
    // restringified) and view the transferred buffer as bytes.
    const payload = msg.payload || {};
    const metadataJson =
        typeof payload.metadataJson === "string" ? payload.metadataJson : "";
    const buffer = resolvePackedBuffer(payload.packedBytes);
    if (!buffer) {
        return canonicalFailed(
            requestId,
            "InvalidInput",
            "missing or malformed packedBytes buffer",
        );
    }
    // Correlate wrapper requestId AND metadata requestId/protocolVersion/operation
    // BEFORE WASM (routing-only parse; the original string is forwarded unchanged).
    const meta = recoverWrapperRequest(metadataJson);
    if (!meta || meta.requestId !== requestId) {
        return canonicalFailed(
            requestId,
            "InvalidInput",
            "wrapper requestId does not match metadata requestId",
        );
    }
    if (meta.protocolVersion !== 1) {
        return canonicalFailed(
            requestId,
            "UnsupportedProtocol",
            "metadata protocolVersion does not match wrapper",
        );
    }
    if (meta.operation !== "analyzeSource") {
        return canonicalFailed(
            requestId,
            "InvalidInput",
            "metadata operation does not match wrapper",
        );
    }
    return analyze_source(metadataJson, buffer);
}

// Recover the requestId/protocolVersion/operation from the canonical metadata
// JSON string (routing only). Returns null if missing/invalid; the original
// string is never restringified (a workingGeneration above 2^53 survives
// byte-for-byte).
function recoverWrapperRequest(metadataJson) {
    let env;
    try {
        env = JSON.parse(metadataJson);
    } catch {
        return null;
    }
    if (!env || typeof env !== "object") return null;
    if (!isValidOpaqueId(env.requestId)) return null;
    return {
        requestId: env.requestId,
        protocolVersion: env.protocolVersion,
        operation: env.operation,
    };
}

self.onmessage = async ({ data }) => {
    // Route by PRESENCE of protocolVersion (canonical) vs legacy {id,type,text}.
    if (data && "protocolVersion" in data) {
        try {
            const responseJson = await handleCanonical(data);
            // Use the TRUSTED requestId (null if invalid) in the outer wrapper
            // too, never echoing an untrusted/huge id outside responseJson.
            const trustedId = isValidOpaqueId(data.requestId)
                ? data.requestId
                : null;
            self.postMessage({
                canonical: true,
                requestId: trustedId,
                responseJson,
            });
        } catch {
            // Defense in depth: synthesize a typed failed envelope rather than
            // surfacing a raw internal error as a successful result.
            const trustedId = isValidOpaqueId(data.requestId)
                ? data.requestId
                : null;
            const responseJson = JSON.stringify({
                protocolVersion: 1,
                requestId: trustedId,
                status: "failed",
                error: {
                    code: "WorkerCrashed",
                    safeMessage:
                        "Rust analysis worker raised an internal error.",
                    retryable: false,
                },
            });
            self.postMessage({
                canonical: true,
                requestId: trustedId,
                responseJson,
            });
        }
        return;
    }
    try {
        await engine;
        let result;
        if (data.type === "ready") result = true;
        else if (data.type === "countWords") result = count_words(data.text);
        else throw new Error(`Unknown operation: ${data.type}`);
        self.postMessage({ id: data.id, result });
    } catch (error) {
        self.postMessage({
            id: data.id,
            error: String(error.message ?? error),
        });
    }
};
