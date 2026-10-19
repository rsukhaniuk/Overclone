// Finite actual-handler regression; fake fetch/cache IO, not browser startup proof.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const source = fs.readFileSync(
    process.argv[2] ||
        path.join(__dirname, "../../src/Client/wwwroot/service-worker.js"),
    "utf8",
);
const tick = () => new Promise((resolve) => setImmediate(resolve));
function signal() {
    let resolve;
    const promise = new Promise((done) => {
        resolve = done;
    });
    return { promise, resolve };
}
function harness() {
    const handlers = new Map(),
        stored = new Set();
    const stats = {
        active: 0,
        peak: 0,
        fetches: [],
        puts: [],
        opens: [],
        skipWaiting: 0,
    };
    const control = {
        holdPut: false,
        putGate: signal(),
        fetchGates: new Map(),
        fetchFailure: new Set(),
        badStatus: new Set(),
        putFailure: new Set(),
        matchFailure: new Set(),
        openFailure: false,
    };
    const absolute = (value) =>
        new URL(
            typeof value === "string" ? value : value.url,
            "https://warm.test/",
        ).href;
    const response = (ok) => ({
        ok,
        clone() {
            return response(ok);
        },
    });
    async function io(action) {
        stats.active++;
        stats.peak = Math.max(stats.peak, stats.active);
        try {
            return await action();
        } finally {
            stats.active--;
        }
    }
    const cache = {
        match: (request) =>
            io(async () => {
                const url = absolute(request);
                if (control.matchFailure.has(url))
                    throw new Error("injected cache read");
                return stored.has(url) ? response(true) : undefined;
            }),
        put: (request) =>
            io(async () => {
                const url = absolute(request);
                stats.puts.push(url);
                if (control.holdPut) await control.putGate.promise;
                if (control.putFailure.has(url))
                    throw new Error("injected cache write");
                stored.add(url);
            }),
        add: async (request) => {
            stored.add(absolute(request));
        },
    };
    const self = {
        location: { origin: "https://warm.test" },
        addEventListener: (name, action) => handlers.set(name, action),
        skipWaiting: () => {
            stats.skipWaiting++;
        },
        clients: { claim: async () => {}, matchAll: async () => [] },
    };
    class Request {
        constructor(url, options) {
            this.url = absolute(url);
            this.cache = options?.cache;
            this.method = "GET";
        }
    }
    const context = vm.createContext({
        self,
        URL,
        Request,
        Response: { error: () => response(false) },
        caches: {
            open: (name) =>
                io(async () => {
                    stats.opens.push(name);
                    if (control.openFailure)
                        throw new Error("injected cache open");
                    return cache;
                }),
            keys: async () => [],
            delete: async () => true,
        },
        fetch: (request) =>
            io(async () => {
                const url = absolute(request);
                stats.fetches.push(url);
                const gate = control.fetchGates.get(url);
                if (gate) await gate.promise;
                if (control.fetchFailure.has(url))
                    throw new Error("injected fetch");
                return response(!control.badStatus.has(url));
            }),
        console: { warn() {} },
    });
    vm.runInContext(source, context, { filename: "actual-service-worker.js" });
    function warm(urls) {
        const replies = [];
        let completion;
        handlers.get("message")({
            data: { type: "warm", urls },
            source: {
                postMessage: (value) =>
                    replies.push(JSON.parse(JSON.stringify(value))),
            },
            waitUntil: (promise) => {
                completion = promise;
            },
        });
        assert(completion, "actual message handler must retain its async work");
        return { replies, completion };
    }
    function release() {
        control.holdPut = false;
        control.putGate.resolve();
        for (const gate of control.fetchGates.values()) gate.resolve();
    }
    return { stats, control, stored, absolute, warm, handlers, release };
}
async function concurrentRounds() {
    const h = harness();
    h.control.holdPut = true;
    const a = Array.from({ length: 12 }, (_, i) => "/a" + i),
        b = Array.from({ length: 8 }, (_, i) => "/b" + i);
    const first = h.warm(a),
        second = h.warm(b),
        duplicate = h.warm(a);
    try {
        await tick();
        await tick();
        assert(
            h.stats.peak <= 4,
            "warm IO peak must be <=4, actual=" + h.stats.peak,
        );
        assert.equal(
            h.stats.fetches.length,
            4,
            "pending cache writes retain the four IO slots",
        );
        assert.equal(
            first.replies.length +
                second.replies.length +
                duplicate.replies.length,
            0,
            "no early ready while cache writes are pending",
        );
    } finally {
        h.release();
        await Promise.all([
            first.completion,
            second.completion,
            duplicate.completion,
        ]);
    }
    assert(h.stats.peak <= 4, "overlapping messages share the same IO limit");
    assert.equal(
        h.stats.fetches.length,
        20,
        "identical overlapping round must not duplicate downloads",
    );
    assert.equal(h.stats.active, 0);
    for (const [run, urls] of [
        [first, a],
        [second, b],
        [duplicate, a],
    ]) {
        assert.equal(run.replies.length, 1);
        assert.equal(run.replies[0].type, "warm-result");
        assert.equal(run.replies[0].ready, true);
        assert.deepEqual(
            run.replies[0].results.map((r) => r.url),
            urls,
        );
        assert(run.replies[0].results.every((r) => r.cached));
    }
    assert.equal(h.stored.size, 20);
    return { peak: h.stats.peak, assets: h.stored.size, replies: 3 };
}
async function completionOrder() {
    const h = harness(),
        urls = ["/slow0", "/slow1", "/slow2", "/slow3"];
    for (const url of urls) h.control.fetchGates.set(h.absolute(url), signal());
    const run = h.warm(urls);
    await tick();
    try {
        for (const url of [...urls].reverse()) {
            h.control.fetchGates.get(h.absolute(url)).resolve();
            await tick();
        }
        await run.completion;
    } finally {
        h.release();
    }
    assert.deepEqual(
        run.replies[0].results.map((r) => r.url),
        urls,
        "reply order is request order, not completion order",
    );
    assert.equal(run.replies[0].ready, true);
    assert(h.stats.peak <= 4);
    return { requestOrderPreserved: true };
}
async function failuresAndRecovery() {
    const h = harness(),
        urls = [
            "/ok",
            "/fetch-error",
            "/http-error",
            "/write-error",
            "/read-error",
            "/already-cached",
            "http://[",
        ];
    h.control.fetchFailure.add(h.absolute("/fetch-error"));
    h.control.badStatus.add(h.absolute("/http-error"));
    h.control.putFailure.add(h.absolute("/write-error"));
    h.control.matchFailure.add(h.absolute("/read-error"));
    h.stored.add(h.absolute("/already-cached"));
    const run = h.warm(urls);
    await run.completion;
    const reply = run.replies[0];
    assert.equal(reply.ready, false);
    assert.deepEqual(
        reply.results.map((r) => r.url),
        urls,
    );
    assert.deepEqual(
        reply.results.map((r) => r.cached),
        [true, false, false, false, false, true, false],
    );
    assert.equal(
        h.stats.active,
        0,
        "one failed asset must not leave other operations pending",
    );
    assert(h.stats.peak <= 4);
    h.control.fetchFailure.clear();
    h.control.badStatus.clear();
    h.control.putFailure.clear();
    h.control.matchFailure.clear();
    const retry = h.warm(urls.slice(0, -1));
    await retry.completion;
    assert.equal(
        retry.replies[0].ready,
        true,
        "explicit later warm recovers failed assets",
    );
    assert.equal(
        h.stats.fetches.filter((url) => url === h.absolute("/ok")).length,
        1,
        "successful cached assets are not fetched again",
    );
    assert.equal(
        h.stats.fetches.filter((url) => url === h.absolute("/already-cached"))
            .length,
        0,
    );
    return {
        failedRoundReady: reply.ready,
        allResults: reply.results.length,
        explicitRecovery: true,
    };
}
async function cacheOpenFailure() {
    const h = harness();
    h.control.openFailure = true;
    const first = h.warm(["/x", "/y"]);
    await first.completion;
    assert.equal(first.replies[0].ready, false);
    assert.equal(first.replies[0].results.length, 2);
    assert(first.replies[0].results.every((r) => !r.cached));
    h.control.openFailure = false;
    const retry = h.warm(["/x", "/y"]);
    await retry.completion;
    assert.equal(retry.replies[0].ready, true);
    return { openFailureReadyFalse: true, laterRoundNotPoisoned: true };
}
async function updateSafety() {
    const h = harness();
    let installed;
    h.handlers.get("install")({
        waitUntil: (value) => {
            installed = value;
        },
    });
    await installed;
    assert.equal(
        h.stats.skipWaiting,
        0,
        "install must not force activation of a new editing-session shell",
    );
    assert(
        h.stats.opens.every((name) => name === "oop-workspace-shell-v9"),
        "changed shell uses bumped cache version",
    );
    h.handlers.get("message")({ data: "skipWaiting" });
    assert.equal(
        h.stats.skipWaiting,
        1,
        "existing explicit update protocol preserved",
    );
    return { shellVersion: "v9", installDidNotSkipWaiting: true };
}
async function bootRevalidation() {
    const h = harness();
    for (const path of ["/_framework/blazor.webassembly.js?v=v9", "/_framework/dotnet.js?v=v9"]) {
        const url = h.absolute(path);
        h.stored.add(url);
        const request = () => {
            let reply;
            h.handlers.get("fetch")({ request: { method: "GET", url }, respondWith: value => { reply = value; } });
            return reply;
        };
        assert.equal((await request()).ok, true);
        assert.equal(h.stats.fetches.at(-1), url, "stable boot filenames revalidate despite a cached copy");
        await tick();
        assert.ok(h.stats.puts.includes(url), "new boot bytes retain their exact versioned cache key");
        h.control.fetchFailure.add(url);
        assert.equal((await request()).ok, true, "offline startup uses the same version's cached boot file");
    }
    return { networkFirst: true, versionedOfflineFallback: true };
}
const deadline = setTimeout(() => {
    console.error("Finite service-worker regression deadline exceeded");
    process.exit(1);
}, 10000);
(async () => {
    const result = {
        scope: "ACTUAL_SERVICE_WORKER_HANDLER_MOCK_IO_NOT_STARTUP_CAUSAL_PROOF",
        concurrent: await concurrentRounds(),
        ordering: await completionOrder(),
        failures: await failuresAndRecovery(),
        cacheOpen: await cacheOpenFailure(),
        update: await updateSafety(),
        boot: await bootRevalidation(),
    };
    console.log(JSON.stringify({ success: true, ...result }));
})()
    .catch((error) => {
        console.error(error);
        process.exitCode = 1;
    })
    .finally(() => clearTimeout(deadline));
