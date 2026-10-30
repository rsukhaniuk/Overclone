// Real production glue bodies with fake Worker/timer/interop boundaries. These
// are transport contracts, NOT browser/offline or physical process evidence.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const { pathToFileURL } = require("node:url");
const root = path.resolve(__dirname, "../../src/Client/wwwroot/js");
const bridgeSource = fs.readFileSync(
    path.join(root, "authoring-assist-bridge.js"),
    "utf8",
);
const workerSource = fs.readFileSync(
    path.join(root, "authoring-assist-worker.js"),
    "utf8",
);
const cases = [];
const metadata = (requestId = "request-1", operation = "authoringAssist") =>
    JSON.stringify({ protocolVersion: 1, requestId, operation, payload: {} });
function bridgeHarness() {
    const workers = [],
        timers = new Map();
    let sequence = 0,
        failConstruct = false,
        failPost = false;
    class Worker {
        constructor(url, options) {
            if (failConstruct)
                throw new Error("synthetic constructor detail never reflected");
            this.url = String(url);
            this.options = options;
            this.terminated = 0;
            workers.push(this);
        }
        postMessage(message, transfer) {
            if (failPost)
                throw new Error("synthetic transfer detail never reflected");
            this.message = structuredClone(message, { transfer });
        }
        terminate() {
            this.terminated++;
        }
        send(changes = {}) {
            this.onmessage?.({
                data: {
                    version: 1,
                    requestId: this.message.requestId,
                    workerEpoch: this.message.workerEpoch,
                    status: "completed",
                    responseJson: "{}",
                    ...changes,
                },
            });
        }
    }
    const context = vm.createContext({
        Uint8Array,
        ArrayBuffer,
        TextEncoder,
        URL,
        Worker,
        setTimeout(action, ms) {
            assert.equal(ms, 5000);
            const id = ++sequence;
            timers.set(id, action);
            return id;
        },
        clearTimeout(id) {
            timers.delete(id);
        },
    });
    // Only ES module registration/import.meta resolution are adapted for vm;
    // the implementation body under test is unchanged.
    const script = bridgeSource
        .replace(
            "export function createAuthoringAssistPort",
            "function createAuthoringAssistPort",
        )
        .replaceAll(
            "import.meta.url",
            JSON.stringify(
                pathToFileURL(path.join(root, "authoring-assist-bridge.js"))
                    .href,
            ),
        );
    vm.runInContext(script, context);
    const port = context.createAuthoringAssistPort();
    return {
        port,
        workers,
        timers,
        expire() {
            for (const action of [...timers.values()]) action();
        },
        failConstruct() {
            failConstruct = true;
        },
        failPost() {
            failPost = true;
        },
    };
}
function workerHarness(
    engine = {
        authoring_assist() {
            return "{}";
        },
    },
    init = async () => {},
) {
    const replies = [],
        self = {
            postMessage(reply) {
                replies.push(reply);
            },
        };
    const context = vm.createContext({
        self,
        engine,
        init,
        Uint8Array,
        ArrayBuffer,
        TextEncoder,
    });
    // Adapt only the fixed module registration, not a quote/whitespace style.
    // Formatter changes must not break execution of the actual worker body.
    const importStatement =
        /^import\s+init\s*,\s*\*\s+as\s+engine\s+from\s+(['"])\.\.\/rust-generated\/document_engine\.js\1\s*;\s*$/gm;
    assert.equal(workerSource.match(importStatement)?.length, 1);
    vm.runInContext(workerSource.replace(importStatement, ""), context);
    const data = (changes) => ({
        version: 1,
        operation: "authoringAssist",
        requestId: "request-1",
        workerEpoch: 1,
        metadataJson: metadata(),
        packedBytes: new Uint8Array([1, 2]).buffer,
        ...changes,
    });
    return {
        send: (changes) => self.onmessage({ data: data(changes) }),
        replies,
    };
}
async function test(name, work) {
    await work();
    cases.push({ name, status: "pass" });
}
(async () => {
    await test("exact owned subarray transfer, caller remains attached, one worker, no queue", async () => {
        const h = bridgeHarness();
        const bytes = new Uint8Array([99, 1, 2, 77]);
        const job = h.port.evaluate(metadata(), bytes.subarray(1, 3));
        assert.equal(h.workers.length, 1);
        assert.equal(
            (await h.port.evaluate(metadata("other"), bytes)).status,
            "busy",
        );
        const w = h.workers[0];
        assert.ok(w.url.endsWith("/authoring-assist-worker.js"));
        assert.equal(w.options.type, "module");
        assert.deepEqual([...new Uint8Array(w.message.packedBytes)], [1, 2]);
        assert.deepEqual([...bytes], [99, 1, 2, 77]);
        assert.equal(w.message.metadataJson, metadata());
        assert.equal(w.message.operation, "authoringAssist");
        w.send();
        const result = await job;
        assert.equal(result.status, "completed");
        assert.equal(result.workerEpoch, 1);
        assert.equal(w.terminated, 1);
        assert.equal(h.timers.size, 0);
        assert.equal(w.onmessage, null);
    });
    await test("cancel terminates worker and a saved late handler cannot settle a newer epoch", async () => {
        const h = bridgeHarness();
        const first = h.port.evaluate(metadata(), new Uint8Array());
        const old = h.workers[0];
        const late = old.onmessage;
        h.port.cancel("request-1");
        assert.equal((await first).status, "cancelled");
        assert.equal(old.terminated, 1);
        const next = h.port.evaluate(metadata("next"), new Uint8Array());
        const worker = h.workers[1];
        late({
            data: {
                version: 1,
                requestId: "next",
                workerEpoch: 2,
                status: "completed",
                responseJson: "{}",
            },
        });
        assert.equal(worker.terminated, 0);
        worker.send();
        assert.equal((await next).workerEpoch, 2);
        assert.equal(worker.terminated, 1);
    });
    await test("five-second timer physically retires modeled Worker, disposal cannot duplicate cleanup", async () => {
        const h = bridgeHarness();
        const job = h.port.evaluate(metadata(), new Uint8Array());
        h.expire();
        assert.equal((await job).status, "timedOut");
        assert.equal(h.workers[0].terminated, 1);
        h.port.dispose();
        assert.equal(h.workers[0].terminated, 1);
        assert.equal(
            (await h.port.evaluate(metadata("next"), new Uint8Array())).status,
            "disposed",
        );
    });
    await test("pre-dispatch cancellation is bounded and no worker is created", async () => {
        const h = bridgeHarness();
        h.port.cancel("request-1");
        assert.equal(
            (await h.port.evaluate(metadata(), new Uint8Array())).status,
            "cancelled",
        );
        assert.equal(h.workers.length, 0);
        h.port.dispose();
    });
    await test("invalid request operation/version/ID/encoding/size fail before a worker or copy", async () => {
        const h = bridgeHarness();
        for (const text of [
            "",
            "{",
            metadata("", "authoringAssist"),
            metadata("r", "analyzeSource"),
            JSON.stringify({
                protocolVersion: 2,
                requestId: "r",
                operation: "authoringAssist",
            }),
            metadata("\ud800"),
            " ".repeat(4 * 1024 * 1024 + 1),
        ])
            assert.equal(
                (await h.port.evaluate(text, new Uint8Array())).status,
                "invalidInput",
            );
        assert.equal(
            (await h.port.evaluate(metadata(), null)).status,
            "invalidInput",
        );
        assert.equal(
            (
                await h.port.evaluate(
                    metadata(),
                    new Uint8Array(64 * 1024 * 1024 + 1),
                )
            ).status,
            "invalidInput",
        );
        assert.equal(h.workers.length, 0);
    });
    for (const [name, changes] of Object.entries({
        wrongEpoch: { workerEpoch: 9 },
        wrongId: { requestId: "wrong" },
        version: { version: 2 },
        extra: { extra: 1 },
        badStatus: { status: "okay" },
        payloadOnError: { status: "unavailable", responseJson: "secret" },
        oversized: { responseJson: "x".repeat(262145) },
        malformedUnicode: { responseJson: "\ud800" },
    })) {
        await test(`bridge refuses ${name} and terminates once`, async () => {
            const h = bridgeHarness();
            const job = h.port.evaluate(metadata(), new Uint8Array());
            h.workers[0].send(changes);
            assert.equal((await job).status, "invalidResponse");
            assert.equal(h.workers[0].terminated, 1);
        });
    }
    for (const failure of ["failConstruct", "failPost"])
        await test(`${failure} is small safe unavailable without leaked admission`, async () => {
            const h = bridgeHarness();
            h[failure]();
            const reply = await h.port.evaluate(metadata(), new Uint8Array());
            assert.equal(reply.status, "unavailable");
            assert.equal(reply.responseJson, null);
            assert.equal(h.timers.size, 0);
            assert.ok(!JSON.stringify(reply).includes("synthetic"));
            if (h.workers.length) assert.equal(h.workers[0].terminated, 1);
        });
    await test("dedicated worker calls only the fixed export and forwards exact metadata/bytes once", async () => {
        let calls = 0;
        const h = workerHarness({
            authoring_assist(text, bytes) {
                calls++;
                assert.equal(text, metadata());
                assert.deepEqual([...bytes], [1, 2]);
                return "{}";
            },
        });
        await h.send();
        await h.send();
        assert.equal(calls, 1);
        assert.equal(h.replies.length, 1);
        assert.equal(h.replies[0].status, "completed");
    });
    await test("worker denies arbitrary export routing and mismatched metadata correlation", async () => {
        let calls = 0;
        const engine = {
            authoring_assist() {
                calls++;
                return "{}";
            },
        };
        for (const changes of [
            { operation: "analyzeSource" },
            { version: 2 },
            { extra: true },
            { workerEpoch: 0 },
        ]) {
            const h = workerHarness(engine);
            await h.send(changes);
            assert.equal(h.replies.length, 0);
        }
        const h = workerHarness(engine);
        await h.send({ metadataJson: metadata("wrong") });
        assert.equal(h.replies[0].status, "invalidResponse");
        assert.equal(calls, 0);
    });
    await test("missing/bad WASM export and loader errors never reflect exception text", async () => {
        for (const h of [
            workerHarness({}),
            workerHarness({
                authoring_assist() {
                    throw Error("private detail");
                },
            }),
            workerHarness({}, async () => {
                throw Error("private detail");
            }),
        ]) {
            await h.send();
            assert.equal(h.replies[0].status, "unavailable");
            assert.equal(h.replies[0].responseJson, null);
            assert.ok(!JSON.stringify(h.replies).includes("private"));
        }
    });
    if (process.env.AUTHORING_ASSIST_WASM_JS) {
        await test("actual accepted Rust WASM export through worker body matches all eight exact vectors", async () => {
            const module = await import(
                pathToFileURL(
                    path.resolve(process.env.AUTHORING_ASSIST_WASM_JS),
                )
            );
            const bytes = fs.readFileSync(
                path.join(
                    path.dirname(process.env.AUTHORING_ASSIST_WASM_JS),
                    "document_engine_bg.wasm",
                ),
            );
            module.initSync({ module: bytes });
            const fixtures = path.resolve(
                __dirname,
                "../../rust/tests/fixtures/r2/assist",
            );
            for (const item of fs
                .readdirSync(fixtures, { withFileTypes: true })
                .filter((e) => e.isDirectory())) {
                const dir = path.join(fixtures, item.name),
                    meta = fs.readFileSync(
                        path.join(dir, "metadata.json"),
                        "utf8",
                    );
                const packed = fs.readFileSync(path.join(dir, "packed.utf8"));
                const h = workerHarness(module);
                await h.send({
                    requestId: JSON.parse(meta).requestId,
                    metadataJson: meta,
                    packedBytes: packed.buffer.slice(
                        packed.byteOffset,
                        packed.byteOffset + packed.byteLength,
                    ),
                });
                assert.equal(h.replies[0].status, "completed");
                assert.deepEqual(
                    JSON.parse(h.replies[0].responseJson),
                    JSON.parse(
                        fs.readFileSync(path.join(dir, "result.json"), "utf8"),
                    ),
                    item.name,
                );
            }
        });
        await test("actual Rust includegraphics binary-path metadata needs zero asset bytes and has no source definition", async () => {
            const { createHash } = require("node:crypto");
            const module = await import(
                pathToFileURL(
                    path.resolve(process.env.AUTHORING_ASSIST_WASM_JS),
                )
            );
            const source = "\\includegraphics{fig}";
            const packed = Buffer.from(source, "utf8");
            const hash = (bytes) =>
                createHash("sha256").update(bytes).digest("hex");
            const meta = JSON.stringify({
                protocolVersion: 1,
                requestId: "binary-path",
                operation: "authoringAssist",
                payload: {
                    snapshotId: "snapshot",
                    projectId: "project",
                    sessionId: "session",
                    workingGeneration: 1,
                    baseHeadRevisionId: null,
                    rootFileId: "main",
                    fileId: "main",
                    caretByte: packed.length - 1,
                    context: "inputPath",
                    query: "fig",
                    catalogVersion: "1.0",
                    files: [
                        {
                            fileId: "main",
                            relativePath: "main.tex",
                            mediaKind: "text",
                            hasBom: false,
                            sha256: hash(packed),
                            byteOffset: 0,
                            byteLength: packed.length,
                        },
                        {
                            fileId: "image",
                            relativePath: "figures/графік😀.png",
                            mediaKind: "binary",
                            hasBom: false,
                            sha256: hash(Buffer.alloc(0)),
                            byteOffset: packed.length,
                            byteLength: 0,
                        },
                    ],
                    limits: {},
                },
            });
            const h = workerHarness(module);
            await h.send({
                requestId: "binary-path",
                metadataJson: meta,
                packedBytes: packed.buffer.slice(
                    packed.byteOffset,
                    packed.byteOffset + packed.byteLength,
                ),
            });
            assert.equal(h.replies[0].status, "completed");
            const result = JSON.parse(h.replies[0].responseJson);
            assert.equal(result.status, "completed");
            assert.equal(result.payload.candidates.length, 1);
            const candidate = result.payload.candidates[0];
            assert.equal(candidate.kind, "path");
            assert.equal(candidate.label, "figures/графік😀.png");
            assert.equal(candidate.insertText, candidate.label);
            assert.equal(candidate.definition, null);
            assert.ok(
                result.payload.limitations.includes(
                    "runtimeResourcesUnverified",
                ),
            );
        });
    }
    console.log(
        JSON.stringify(
            {
                suite: "authoring-assist-transport",
                cases,
                passed: cases.length,
                failed: 0,
                actualWasmVectors: process.env.AUTHORING_ASSIST_WASM_JS
                    ? 8
                    : "not-run: optional accepted artifact not supplied",
                exclusions: [
                    "modeled Worker termination, not browser-process proof",
                    "no managed tests",
                    "no published/cold-offline UI acceptance",
                ],
            },
            null,
            2,
        ),
    );
})().catch((error) => {
    console.error(error);
    process.exitCode = 1;
});
