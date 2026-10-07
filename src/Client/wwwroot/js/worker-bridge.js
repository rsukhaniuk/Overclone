// Each language gets an independent bridge-owned worker and request queue. The
// bridge preserves the legacy smoke shapes byte-for-byte
//   request { id, type, text } -> response { id, result | error }
// and adds the canonical v1 worker envelope on the SAME worker:
//   request  { protocolVersion:1, requestId, operation, payload }
//   response { protocolVersion:1, requestId, status, payload?, error? }
// Canonical REPLIES from the worker carry `canonical:true`
//   { canonical:true, requestId, responseJson:<canonical JSON string> }
// so the bridge routes replies separately from the legacy { id, result|error } shape.
//
// JS is browser/WASM/Promise glue ONLY: no parser, no rule logic, no source
// mapping, no product policy. C# owns orchestration; Rust owns analysis.
//
// Lifecycle invariants (independently probed):
//  - A monotonic epoch is incremented on every replacement/reset/dispose/crash.
//    Each pending entry captures the epoch AT REGISTRATION (before any await);
//    the terminal result carries THAT captured epoch — never the current
//    global epoch read after an await (prevents a dispose/reset between
//    dispatch and reply from relabeling a completed result to a fresh epoch).
//  - Each worker's onmessage/onerror/onmessageerror closure captures the epoch
//    at worker creation, so a stale pre-reset reply (or a crashed worker's
//    late error) cannot resolve a fresh request, and a pending initialization
//    failure/timeout/cancel from an old continuation cannot reset a NEW
//    epoch's healthy worker (guarded by capturedEpoch). The onmessage closure
//    guards BOTH canonical and legacy replies.
//  - An accepted job is registered BEFORE awaiting readiness/initialization,
//    so a cancel during readiness/init settles it promptly and prevents
//    later dispatch into a fresh worker.
//  - Canonical initialization is single-flight BEFORE any async yield
//    (one initialize request per epoch), with retryable failure cleanup. The
//    initialize REPLY is validated (object/protocol/requestId/status/capability
//    payload) BEFORE allowing dispatch: a failed/malformed/wrong-id init, or
//    one missing the analyzeSource capability, settles accepted analyze jobs as a
//    typed failure and resets the worker so the next SAME-bridge request
//    re-initializes a healthy worker (no automatic retry / no infinite loop).
//  - ONE whole-job 5000ms deadline for analyzeSource starts at dispatch
//    (after readiness); initialization keeps its separate 30s budget.
//  - cancelSource terminates the worker from the MAIN thread (synchronous WASM
//    cannot read a cancel message); the target settles cancelled, all other
//    pending settle too, the next request reinitializes a fresh worker.
//  - Synchronous Worker.postMessage failures settle+reset promptly (no orphan
//    5s timer later killing a healthy new worker). Worker construction /
//    legacy ready timeout clear the cached initialization (retryable, never a
//    permanently cached rejection).
//  - Pending is bounded; excess callers get a typed QuotaExceeded (no unbounded backlog).
//  - Legacy ready/countWords shapes and callers are byte-identical (C++ worker untouched).

const PROTOCOL_VERSION = 1;
const READY_DEADLINE_MS = 30000;
const ANALYSIS_DEADLINE_MS = 5000;
const MAX_METADATA_BYTES = 4 * 1024 * 1024;
const MAX_ID_BYTES = 128;
const MAX_PENDING = 64;

export function createWorkerBridge(url, language) {
    let worker;
    let initialization; // legacy "ready" handshake promise
    let canonicalInit; // shared canonical initialize promise (single-flight)
    let epoch = 0;
    let sequence = 0;
    const pending = new Map(); // requestId -> entry

    function newId() {
        return ++sequence;
    }

    // ---- shared opaque-id validation (used by bridge + worker, no duplication) ----

    // Build a canonical envelope object (bridge-synthesized terminal). The
    // response has no large numbers, so JSON.stringify is safe.
    function canonicalEnvelope(requestId, status, code, message) {
        const env = { protocolVersion: PROTOCOL_VERSION, requestId, status };
        if (code) env.error = { code, safeMessage: message, retryable: false };
        return env;
    }

    // Settle ONE entry exactly once (idempotent via entry.settled), clear its timer,
    // remove it from pending, and resolve/reject per kind. Legacy entries reject
    // with the original Error shape; canonical entries resolve with the
    // canonical response JSON string carrying the entry's CAPTURED epoch.
    function settleEntry(entry, status, code, message) {
        if (entry.settled) return;
        entry.settled = true;
        if (entry.timer) {
            clearTimeout(entry.timer);
            entry.timer = null;
        }
        pending.delete(entry.requestId);
        if (entry.kind === "legacy") {
            entry.reject(new Error(message));
        } else {
            entry.resolve({
                responseJson: JSON.stringify(
                    canonicalEnvelope(entry.requestId, status, code, message),
                ),
            });
        }
    }

    // Bump the epoch, terminate the worker, clear cached init, and settle every
    // UNSETTLED pending entry (each with its own captured epoch). A single target
    // settled before this call is skipped (idempotent).
    function resetWorker(status, code, message) {
        epoch++;
        if (worker) {
            try {
                worker.terminate();
            } catch {
                /* already gone */
            }
            worker = undefined;
        }
        initialization = undefined;
        canonicalInit = undefined;
        const entries = Array.from(pending.values());
        pending.clear();
        for (const entry of entries) settleEntry(entry, status, code, message);
    }

    // Settle one target, then reset the worker so remaining pending also settle.
    function settleAndReset(target, status, code, message) {
        settleEntry(target, status, code, message);
        resetWorker(status, code, message);
    }

    function installHandlers(capturedEpoch) {
        worker.onmessage = ({ data }) => {
            // A stale reply from a terminated/replaced worker must not settle a
            // fresh request. The captured-epoch guard covers BOTH canonical and
            // legacy replies (the old worker's closure holds the old epoch).
            if (capturedEpoch !== epoch) return;
            // Canonical replies carry `canonical:true`; legacy replies are {id,result|error}.
            if (data && data.canonical === true) {
                handleCanonicalReply(data, capturedEpoch);
            } else {
                handleLegacyReply(data);
            }
        };
        worker.onerror = () => {
            // A crash terminates all pending. The captured-epoch guard prevents a
            // stale (pre-reset) worker's error from settling fresh requests.
            if (capturedEpoch !== epoch) return;
            resetWorker(
                "failed",
                "WorkerCrashed",
                `${language} worker failed. Check the browser console and build logs.`,
            );
        };
        worker.onmessageerror = () => {
            if (capturedEpoch !== epoch) return;
            resetWorker(
                "failed",
                "WorkerCrashed",
                `${language} worker received an unserializable message.`,
            );
        };
    }

    function handleLegacyReply(data) {
        const request = pending.get(data.id);
        if (!request) return;
        clearTimeout(request.timer);
        request.timer = null;
        pending.delete(data.id);
        if (data.error) request.reject(new Error(data.error));
        else request.resolve(data.result);
    }

    function handleCanonicalReply(data, capturedEpoch) {
        if (capturedEpoch !== epoch) return; // stale pre-reset reply (defense-in-depth)
        const requestId = data.requestId;
        if (typeof requestId !== "string") return;
        const entry = pending.get(requestId);
        if (!entry || entry.settled) return; // unknown / duplicate / late
        if (entry.kind === "init") {
            handleInitReply(entry, data, capturedEpoch);
            return;
        }
        // analyze: resolve with the REAL canonical response JSON string
        // (payload intact), NOT the bridge-synthesized envelope. Settle
        // exactly once: clear the timer, remove from pending, mark settled.
        if (entry.timer) {
            clearTimeout(entry.timer);
            entry.timer = null;
        }
        entry.settled = true;
        pending.delete(requestId);
        entry.resolve({
            responseJson: data.responseJson,
            workerEpoch: entry.epoch,
        });
    }

    // Validate the initialize reply BEFORE allowing dispatch. A failed,
    // malformed, wrong-id, or missing-analyzeSource-capability init settles the
    // accepted analyze jobs as a typed failure, resets the worker (so the next
    // request on the SAME bridge re-initializes a healthy worker), and rejects
    // the shared canonicalInit so analyzeDispatch never dispatches into a bad
    // worker. No automatic retry / no infinite loop. The captured-epoch guard
    // ensures a STALE init reply cannot kill a NEW healthy worker.
    function handleInitReply(entry, data, capturedEpoch) {
        if (entry.timer) {
            clearTimeout(entry.timer);
            entry.timer = null;
        }
        entry.settled = true;
        pending.delete(entry.requestId);
        const v = validateInitEnvelope(data.responseJson, entry.requestId);
        if (v.ok) {
            entry.resolve(v.payload); // canonicalInit resolves (ready)
            return;
        }
        // failed/malformed/missing capability: reset the worker iff it is still
        // the current one, then reject the shared canonicalInit.
        if (capturedEpoch === epoch) resetWorker("failed", v.code, v.message);
        entry.reject({ code: v.code, message: v.message });
    }

    // Reject canonicalInit + reset (guarded) for an init timeout / ready
    // failure / postMessage failure. Shared by the timer and initDispatch.
    function failInit(entry, code, message, capturedEpoch) {
        if (entry.settled) return;
        if (entry.timer) {
            clearTimeout(entry.timer);
            entry.timer = null;
        }
        entry.settled = true;
        pending.delete(entry.requestId);
        if (capturedEpoch === epoch) resetWorker("failed", code, message);
        entry.reject({ code, message });
    }

    function validateInitEnvelope(responseJson, expectedRequestId) {
        let env;
        try {
            env = JSON.parse(responseJson);
        } catch {
            return {
                ok: false,
                code: "InvalidInput",
                message: `${language} initialize returned malformed JSON.`,
            };
        }
        if (!env || typeof env !== "object")
            return {
                ok: false,
                code: "InvalidInput",
                message: `${language} initialize returned a non-object response.`,
            };
        if (env.protocolVersion !== 1)
            return {
                ok: false,
                code: "UnsupportedProtocol",
                message: `${language} initialize returned an unsupported protocolVersion.`,
            };
        if (env.requestId !== expectedRequestId)
            return {
                ok: false,
                code: "InvalidInput",
                message: `${language} initialize reply did not match the request.`,
            };
        if (env.status !== "completed") {
            const code =
                env.error && typeof env.error.code === "string"
                    ? env.error.code
                    : "WorkerCrashed";
            const message =
                env.error && typeof env.error.safeMessage === "string"
                    ? env.error.safeMessage
                    : `${language} analyzer initialize failed.`;
            return { ok: false, code, message };
        }
        const ops =
            env.payload && Array.isArray(env.payload.operations)
                ? env.payload.operations
                : null;
        if (!ops || !ops.includes("analyzeSource"))
            return {
                ok: false,
                code: "UnsupportedCapability",
                message: `${language} analyzer does not support analyzeSource.`,
            };
        return { ok: true, payload: env.payload };
    }

    // ---- legacy path (byte-identical to the prior smoke bridge) ----

    function request(type, text) {
        const id = newId();
        return new Promise((resolve, reject) => {
            const timer = setTimeout(() => {
                if (pending.delete(id))
                    reject(new Error(`${language} worker request timed out.`));
            }, READY_DEADLINE_MS);
            pending.set(id, {
                requestId: id,
                resolve,
                reject,
                timer,
                kind: "legacy",
                epoch,
                settled: false,
            });
            worker.postMessage({ id, type, text });
        });
    }

    function ready() {
        if (initialization) return initialization;
        try {
            worker = new Worker(url, { type: "module" });
        } catch (err) {
            // Worker construction failed; initialization stays undefined so a later
            // ready() retries (never a permanently cached rejection).
            throw err;
        }
        installHandlers(epoch);
        // Chain the rejection cleanup onto `p` so `p` is the CHAINED promise
        // (both cached and awaited by initDispatch). A discarded `.catch()` child
        // would reject unawaited -> an unhandled rejection on every canceled/
        // failed readiness. Clear the cache ONLY if this promise is still the
        // cached one AND the epoch is unchanged (a reset/replacement that bumped
        // the epoch and cached a NEW ready must not be clobbered by this OLD
        // rejection). The rethrow is kept so the caller still receives the
        // readiness failure.
        const capturedEpoch = epoch;
        const p = request("ready").catch((err) => {
            if (initialization === p && capturedEpoch === epoch)
                initialization = undefined;
            throw err;
        });
        initialization = p;
        return p;
    }

    async function countWords(text) {
        await ready();
        return request("countWords", text);
    }

    // ---- canonical path ----

    function ensureInitialized() {
        if (canonicalInit) return canonicalInit;
        // Single-flight BEFORE any async yield: create the init entry and promise
        // synchronously so a concurrent first caller shares one initialize request.
        const requestId = `init-${newId()}`;
        const capturedEpoch = epoch;
        const entry = {
            requestId,
            resolve: null,
            reject: null,
            timer: null,
            kind: "init",
            epoch: capturedEpoch,
            settled: false,
        };
        const promise = new Promise((resolve, reject) => {
            entry.resolve = resolve;
            entry.reject = reject;
        });
        pending.set(requestId, entry);
        canonicalInit = promise;
        entry.timer = setTimeout(
            () =>
                failInit(
                    entry,
                    "Timeout",
                    `${language} initialize exceeded the readiness deadline.`,
                    capturedEpoch,
                ),
            READY_DEADLINE_MS,
        );
        // Kick off readiness + initialize dispatch in the background; the promise
        // is resolved by the worker reply, a cancel, a timeout, or a crash.
        // Return the LOCAL promise (never the mutable global canonicalInit): if
        // initDispatch synchronously fails (e.g. Worker construction throws),
        // failInit sets canonicalInit=undefined and rejects THIS promise before
        // we return, so returning the global would hand the caller `undefined`
        // and leave the rejected promise unawaited (unhandled rejection).
        initDispatch(entry, capturedEpoch);
        return promise;
    }

    async function initDispatch(entry, capturedEpoch) {
        try {
            await ready();
            if (entry.settled) return; // cancel/dispose during readiness
            if (capturedEpoch !== epoch) return; // a reset happened; stale
            try {
                worker.postMessage({
                    protocolVersion: PROTOCOL_VERSION,
                    requestId: entry.requestId,
                    operation: "initialize",
                    payload: {},
                });
            } catch {
                // postMessage failed for initialize: fail the init (typed), reset
                // iff current, reject canonicalInit.
                failInit(
                    entry,
                    "WorkerCrashed",
                    `${language} worker could not receive the initialize request.`,
                    capturedEpoch,
                );
            }
        } catch {
            // ready() failed (worker construction / legacy ready error). Fail the
            // init (typed) and reset iff still current, so the next request
            // retries on a fresh worker (never a permanently cached rejection).
            failInit(
                entry,
                "WorkerCrashed",
                `${language} worker failed to initialize.`,
                capturedEpoch,
            );
        }
    }

    function handlePostFailure(entry, _err) {
        // postMessage failed for an ANALYZE (e.g. DataCloneError / detached
        // buffer). Settle + reset promptly so no orphan 5s timer later kills a
        // healthy new worker. (Init postMessage failures use failInit instead.)
        settleAndReset(
            entry,
            "failed",
            "WorkerCrashed",
            `${language} worker could not receive the request.`,
        );
    }

    // Exact transport: metadata JSON string + ONE packed byte buffer. The
    // incoming bytes are COPIED (slice, respecting byteOffset/byteLength) before
    // transferring the copy's ArrayBuffer so the caller's buffer stays attached
    // and unmodified. The wrapper requestId is recovered from the metadata
    // (routing-only parse; the ORIGINAL metadata string is forwarded
    // unchanged so a workingGeneration above 2^53 is never parse/restringified)
    // so wrapper and metadata cannot mismatch. The job is registered BEFORE
    // awaiting readiness so a cancel during readiness/init settles promptly.
    function analyzeSource(metadataJson, packedBytes) {
        if (typeof metadataJson !== "string" || metadataJson.length === 0) {
            return {
                responseJson: JSON.stringify(
                    canonicalEnvelope(
                        null,
                        "failed",
                        "InvalidInput",
                        "analyzeSource requires a non-empty metadataJson string",
                    ),
                ),
                workerEpoch: epoch,
            };
        }
        if (metadataJson.length > MAX_METADATA_BYTES) {
            return {
                responseJson: JSON.stringify(
                    canonicalEnvelope(
                        null,
                        "failed",
                        "QuotaExceeded",
                        "analyzeSource metadata exceeded the 4 MiB limit",
                    ),
                ),
                workerEpoch: epoch,
            };
        }
        const requestId = recoverRequestId(metadataJson);
        if (requestId == null) {
            return {
                responseJson: JSON.stringify(
                    canonicalEnvelope(
                        null,
                        "failed",
                        "InvalidInput",
                        "analyzeSource metadata is missing a valid requestId",
                    ),
                ),
                workerEpoch: epoch,
            };
        }
        const view = asBytesView(packedBytes);
        if (!view) {
            return {
                responseJson: JSON.stringify(
                    canonicalEnvelope(
                        requestId,
                        "failed",
                        "InvalidInput",
                        "packedBytes must be a Uint8Array or ArrayBuffer view",
                    ),
                ),
                workerEpoch: epoch,
            };
        }
        if (pending.has(requestId) || pending.size >= MAX_PENDING) {
            return {
                responseJson: JSON.stringify(
                    canonicalEnvelope(
                        requestId,
                        "failed",
                        "QuotaExceeded",
                        "too many concurrent analysis requests",
                    ),
                ),
                workerEpoch: epoch,
            };
        }
        const copy = view.slice(0); // owned copy; respects byteOffset/byteLength
        const capturedEpoch = epoch;
        const entry = {
            requestId,
            resolve: null,
            reject: null,
            timer: null,
            kind: "analyze",
            epoch: capturedEpoch,
            settled: false,
        };
        const promise = new Promise((resolve, reject) => {
            entry.resolve = resolve;
            entry.reject = reject;
        });
        pending.set(requestId, entry);
        // Kick off readiness + dispatch in the background; the main promise is
        // resolved by the worker reply, a cancel, a timeout, or a crash — NOT
        // by ensureInitialized — so a cancel during readiness settles promptly.
        analyzeDispatch(entry, copy, metadataJson, capturedEpoch);
        return (async () => {
            const reply = await promise;
            return {
                responseJson: reply.responseJson,
                workerEpoch: entry.epoch,
            };
        })();
    }

    async function analyzeDispatch(entry, copy, metadataJson, capturedEpoch) {
        try {
            await ensureInitialized();
            if (entry.settled) return; // cancel/dispose during readiness
            if (capturedEpoch !== epoch) return; // a reset happened; stale
            // ONE whole-job deadline timer starts at dispatch (after readiness).
            entry.timer = setTimeout(
                () =>
                    settleAndReset(
                        entry,
                        "failed",
                        "Timeout",
                        `${language} analyzeSource exceeded the whole-job deadline.`,
                    ),
                ANALYSIS_DEADLINE_MS,
            );
            try {
                worker.postMessage(
                    {
                        protocolVersion: PROTOCOL_VERSION,
                        requestId: entry.requestId,
                        operation: "analyzeSource",
                        payload: { metadataJson, packedBytes: copy.buffer },
                    },
                    [copy.buffer],
                );
            } catch (e) {
                handlePostFailure(entry, e);
            }
        } catch (initErr) {
            // ensureInitialized rejected (failed/malformed/missing-capability
            // init, or init crash). The init's reset already settled shared
            // analyze jobs with the typed code; settle this entry iff still
            // unsettled, using the typed init failure code/message.
            if (!entry.settled) {
                const code =
                    initErr && typeof initErr.code === "string"
                        ? initErr.code
                        : "WorkerCrashed";
                const message =
                    initErr && typeof initErr.message === "string"
                        ? initErr.message
                        : `${language} worker initialize failed.`;
                settleEntry(entry, "failed", code, message);
            }
        }
    }

    // Cancel terminates the worker from the MAIN thread. A cancel message is NOT
    // sent to the worker: synchronous WASM cannot process it. The target
    // settles cancelled; all other pending settle cancelled (the worker is
    // gone). An unknown/repeated target is a no-op ACK that never touches
    // the worker or unrelated requests. A canceled waiter does not kill an
    // unrelated LATER job (only the target's worker is reset).
    function cancelSource(targetRequestId) {
        const ack = (cancelled) =>
            JSON.stringify({
                protocolVersion: PROTOCOL_VERSION,
                requestId: `cancel-${newId()}`,
                status: "completed",
                payload: { cancelledTargetRequestId: cancelled ?? null },
            });
        if (typeof targetRequestId !== "string" || targetRequestId.length === 0)
            return ack(null);
        const entry = pending.get(targetRequestId);
        if (!entry || entry.kind !== "analyze" || entry.settled)
            return ack(null);
        settleAndReset(
            entry,
            "cancelled",
            "Cancelled",
            `${language} analyzeSource was cancelled.`,
        );
        return ack(targetRequestId);
    }

    function getWorkerEpoch() {
        return epoch;
    }

    function isCurrentWorkerEpoch(candidateEpoch) {
        return typeof candidateEpoch === "number" && candidateEpoch === epoch;
    }

    function dispose() {
        resetWorker("cancelled", "Cancelled", `${language} worker was closed.`);
    }

    return {
        ready,
        countWords,
        dispose,
        analyzeSource,
        cancelSource,
        getWorkerEpoch,
        isCurrentWorkerEpoch,
    };
}

// ---- shared glue validators (used by bridge + worker; no document policy duplication) ----

// Accept a Uint8Array or an ArrayBuffer view (respecting byteOffset/byteLength);
// reject null/ArrayBuffer-backed-but-zero-length as null.
function asBytesView(packedBytes) {
    if (packedBytes instanceof Uint8Array) return packedBytes;
    if (packedBytes && packedBytes.buffer instanceof ArrayBuffer) {
        return new Uint8Array(
            packedBytes.buffer,
            packedBytes.byteOffset ?? 0,
            packedBytes.byteLength ?? packedBytes.buffer.byteLength,
        );
    }
    if (packedBytes instanceof ArrayBuffer) return new Uint8Array(packedBytes);
    return null;
}

// True iff s is nonempty, strict-valid UTF-8 (no lone surrogate) and encodes
// to at most maxBytes UTF-8 SCALAR bytes (a surrogate pair counts 4, not 6).
export function isValidOpaqueId(s, maxBytes = MAX_ID_BYTES) {
    if (typeof s !== "string" || s.length === 0) return false;
    let bytes = 0;
    let i = 0;
    while (i < s.length) {
        const c = s.charCodeAt(i);
        let cp;
        if (c >= 0xd800 && c <= 0xdbff) {
            const low = s.charCodeAt(i + 1);
            if (low >= 0xdc00 && low <= 0xdfff) {
                cp = 0x10000 + ((c - 0xd800) << 10) + (low - 0xdc00);
                i += 2;
            } else {
                return false; // lone high surrogate
            }
        } else if (c >= 0xdc00 && c <= 0xdfff) {
            return false; // lone low surrogate
        } else {
            cp = c;
            i += 1;
        }
        bytes += cp <= 0x7f ? 1 : cp <= 0x7ff ? 2 : cp <= 0xffff ? 3 : 4;
        if (bytes > maxBytes) return false;
    }
    return true;
}

// Recover the requestId from a canonical metadata JSON string for
// wrapper/metadata correlation. Routing-only parse: the ORIGINAL string is
// forwarded unchanged (never restringified). Returns null for a missing,
// non-string, empty or invalid (>128 byte / lone-surrogate) id.
export function recoverRequestId(metadataJson) {
    let env;
    try {
        env = JSON.parse(metadataJson);
    } catch {
        return null;
    }
    if (!env || typeof env !== "object") return null;
    const id = env.requestId;
    return isValidOpaqueId(id) ? id : null;
}
