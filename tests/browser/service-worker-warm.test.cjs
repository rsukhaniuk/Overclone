// Finite actual-handler regression; fake fetch/cache IO, not browser startup proof.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const { createHash } = require("node:crypto");
const source = fs.readFileSync(
    process.argv[2] ||
        path.join(__dirname, "../../src/Client/wwwroot/service-worker.js"),
    "utf8",
);
// The release-snapshot (manifest) path is exercised against the PRODUCTION
// worker, which requires a valid manifest (no legacy fail-open fallback).
const publishedSource = fs.readFileSync(
    path.join(__dirname, "../../src/Client/wwwroot/service-worker.published.js"),
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
        match: (request, options) =>
            io(async () => {
                assert.notEqual(
                    options?.ignoreSearch,
                    true,
                    "service worker must keep exact query-key isolation",
                );
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
        h.stats.opens.every((name) => name === "oop-workspace-shell-v10"),
        "changed shell uses bumped cache version",
    );
    h.handlers.get("message")({ data: "skipWaiting" });
    assert.equal(
        h.stats.skipWaiting,
        1,
        "existing explicit update protocol preserved",
    );
    return { shellVersion: "v10", installDidNotSkipWaiting: true };
}
async function bootRevalidation() {
    const h = harness();
    for (const path of [
        "/_framework/blazor.webassembly.js?v=v10",
        "/_framework/dotnet.js?v=v10",
    ]) {
        const url = h.absolute(path);
        h.stored.add(url);
        const request = () => {
            let reply;
            h.handlers.get("fetch")({
                request: { method: "GET", url },
                respondWith: (value) => {
                    reply = value;
                },
            });
            return reply;
        };
        assert.equal((await request()).ok, true);
        assert.equal(
            h.stats.fetches.at(-1),
            url,
            "stable boot filenames revalidate despite a cached copy",
        );
        await tick();
        assert.ok(
            h.stats.puts.includes(url),
            "new boot bytes retain their exact versioned cache key",
        );
        h.control.fetchFailure.add(url);
        assert.equal(
            (await request()).ok,
            true,
            "offline startup uses the same version's cached boot file",
        );
    }
    return { networkFirst: true, versionedOfflineFallback: true };
}
async function frameworkVersionIsolation() {
    const h = harness();
    const paths = [
        "/_framework/blazor.webassembly.js",
        "/_framework/dotnet.js",
        "/_framework/dotnet.runtime.azrfhwb804.js",
        "/_framework/dotnet.native.q38003gvqi.js",
    ];
    async function request(url) {
        let reply;
        h.handlers.get("fetch")({
            request: { method: "GET", url },
            respondWith: (value) => {
                reply = value;
            },
        });
        assert.ok(reply, "actual service worker must handle framework GET");
        return reply;
    }
    for (const path of paths) {
        const current = h.absolute(`${path}?v=v10`);
        // Reproduce the original cold-start hole: an unversioned key exists,
        // but it must NOT satisfy a requested versioned runtime dependency.
        h.stored.add(h.absolute(path));
        h.stored.add(h.absolute(`${path}?v=v9`));
        h.control.fetchFailure.add(current);
        assert.equal(
            (await request(current)).ok,
            false,
            "unversioned/old boot bytes cannot masquerade as v10",
        );
        assert.equal(h.stats.fetches.at(-1), current);
        assert.ok(!h.stored.has(current));
        h.control.fetchFailure.delete(current);
        const warm = h.warm([`${path}?v=v10`]);
        await warm.completion;
        assert.equal(warm.replies[0].ready, true);
        assert.ok(
            h.stored.has(current),
            "warm retains the exact runtime dependency URL, including its version",
        );
        assert.ok(h.stats.puts.includes(current));
        h.control.fetchFailure.add(current);
        assert.equal(
            (await request(current)).ok,
            true,
            "exact warmed key serves cold offline requests",
        );
        const future = h.absolute(`${path}?v=v11`);
        h.control.fetchFailure.add(future);
        assert.equal(
            (await request(future)).ok,
            false,
            "v10/old/unversioned keys cannot satisfy an arbitrary other version",
        );
        assert.equal(h.stats.fetches.at(-1), future);
        assert.ok(!h.stored.has(future));
    }
    assert(h.stats.peak <= 4);
    return {
        exactCurrentWarmKeys: paths.length,
        unversionedAndOldKeysRejected: true,
        arbitraryVersionRejected: true,
    };
}

// ---- release-snapshot (manifest) path --------------------------------
// A second harness that provides importScripts and an injected assetsManifest
// (the publish-only path), with integrity-aware fetch. It exercises the
// immutable, integrity-checked per-release cache: a complete snapshot must
// install; an interrupted/integrity-failed install must reject and retain the
// prior complete release; cleanup is prefix-restricted; authenticated/
// cross-origin/private-query responses are never cached; warm never poisons the
// immutable cache. Legacy negatives above remain unchanged.
// Role-complete synthetic IO fixture: paths/shape follow the captured SDK
// manifest. Digests are well-formed SHA-256, not 'sha256-' placeholder tokens.
// Mock fetch below simulates byte verification; real SRI remains a browser check.
const digest = text => "sha256-" + createHash("sha256").update(text).digest("base64");
const RELEASE_CORE = [
    "index.html", "app.css", "manifest.json",
    "icons/icon-192.png", "icons/icon-512.png",
    "_framework/blazor.webassembly.js", "_framework/dotnet.js",
    "_framework/Client.9e6gtdlnf6.wasm",
    "_framework/dotnet.runtime.azrfhwb804.js",
    "_framework/dotnet.native.q38003gvqi.js",
    "_framework/dotnet.native.3zpaws0drs.wasm",
    "js/workspace-storage.js", "js/editor-select.js",
    "rust-generated/document_engine.js", "rust-generated/document_engine_bg.wasm",
].map(url => ({ hash: digest(url), url }));
const RELEASE_NON_CORE = ["optional/native-texlive.tar", "native-generated/compiler.wasm"]
    .map(url => ({ hash: digest(url), url }));
const RELEASE_VERSION_A = "release-aaaaaaaa";
const RELEASE_VERSION_B = "release-bbbbbbbb";

function createReleaseBackend() {
    // Per-name cache stores shared across manifest harnesses so two distinct
    // releases (A/B) coexist like a real browser Cache API. Each cache
    // normalizes keys to absolute URLs against the origin it was opened with.
    const stores = new Map(); // cacheName -> { origin, set }
    return {
        open: async (name, origin) => {
            if (!stores.has(name)) stores.set(name, { origin, set: new Set() });
            const entry = stores.get(name);
            const abs = (value) => absoluteUrl(value, entry.origin);
            return {
                match: async (request) => entry.set.has(abs(request)) ? { ok: true, clone() { return this; } } : undefined,
                put: async (key, _response) => { entry.set.add(abs(key)); },
            };
        },
        keys: async () => [...stores.keys()],
        delete: async (name) => stores.delete(name),
        has: async (name) => stores.has(name),
        hasEntry: (name, url) => stores.has(name) && stores.get(name).set.has(url),
        entryCount: (name) => (stores.has(name) ? stores.get(name).set.size : 0),
        deleteEntry: (name, url) => stores.get(name)?.set.delete(url),
    };
}
function absoluteUrl(value, origin) {
    return new URL(typeof value === "string" ? value : value.url, origin).href;
}
function manifestHarness(manifest, backend, stats = { fetches: [], opens: [], skipWaiting: 0, deletes: [] }) {
    const shared = backend || createReleaseBackend();
    const handlers = new Map();
    const control = { fetchFailure: new Set(), badStatus: new Set(), badIntegrity: new Set() };
    const ORIGIN = "https://rel.test";
    const absolute = (value) => absoluteUrl(value, ORIGIN);
    let pendingManifest = manifest;
    const importFails = !!manifest.importFails;
    function maybeFail(request) {
        const url = absolute(request);
        if (control.fetchFailure.has(url)) throw new Error("injected fetch");
        if (control.badIntegrity.has(request.integrity)) throw new Error("integrity mismatch");
        return { ok: !control.badStatus.has(url), clone() { return this; } };
    }
    const self = {
        location: { origin: ORIGIN },
        addEventListener: (name, action) => handlers.set(name, action),
        skipWaiting: () => { stats.skipWaiting++; },
        clients: { claim: async () => {}, matchAll: async () => [] },
        importScripts: (url) => {
            if (importFails || !pendingManifest) throw new Error("manifest not found");
            if (url === "/service-worker-assets.js") self.assetsManifest = pendingManifest;
            else throw new Error("manifest not found");
        },
    };
    class Request {
        constructor(url, options) {
            this.url = absolute(url);
            this.integrity = options?.integrity || null;
            this.cache = options?.cache || null;
            this.method = "GET";
            this.headers = new Map();
        }
    }
    const context = vm.createContext({
        self, URL, Request, Response: { error: () => ({ ok: false }) },
        caches: {
            open: async (name) => { stats.opens.push(name); return shared.open(name, ORIGIN); },
            keys: async () => shared.keys(),
            delete: async (name) => { stats.deletes.push(name); return shared.delete(name); },
            has: async (name) => shared.has(name),
        },
        fetch: (request) => {
            stats.fetches.push(absolute(request));
            return Promise.resolve().then(() => maybeFail(request));
        },
        console: { warn() {} },
    });
    vm.runInContext(publishedSource, context, { filename: "service-worker.published.js" });
    return { handlers, stats, absolute, control, ORIGIN, backend: shared, releaseName: `oop-workspace-release-${manifest.version}`, auxName: `oop-workspace-aux-${manifest.version}`, setManifest: (m) => { pendingManifest = m; } };
}
async function manifestInstall(h) {
    let installed;
    h.handlers.get("install")({ waitUntil: (p) => { installed = p; } });
    await installed;
}
async function manifestActivate(h) {
    let activated;
    h.handlers.get("activate")({ waitUntil: (p) => { activated = p; } });
    await activated;
}
async function releaseCompleteInstallIntegrityChecked() {
    const h = manifestHarness({ version: RELEASE_VERSION_A, assets: [...RELEASE_CORE, ...RELEASE_NON_CORE] });
    const backend = h.backend;
    await manifestInstall(h);
    for (const asset of RELEASE_CORE) {
        const key = h.absolute("/" + asset.url);
        assert.ok(backend.hasEntry(h.releaseName, key), `core asset precached under canonical key: ${asset.url}`);
    }
    assert.ok(!h.stats.fetches.includes(h.absolute("/optional/native-texlive.tar")), "non-core native payload not precached");
    assert.ok(!backend.hasEntry(h.releaseName, h.absolute("/optional/native-texlive.tar")), "non-core native payload not cached");
    assert.ok(h.stats.fetches.length >= RELEASE_CORE.length);
    return { complete: true, precached: backend.entryCount(h.releaseName), nonCoreExcluded: true };
}
async function releaseInterruptedInstallRejectsAndPriorRetained() {
    const shared = createReleaseBackend();
    const hA = manifestHarness({ version: RELEASE_VERSION_A, assets: RELEASE_CORE }, shared);
    await manifestInstall(hA);
    for (const asset of RELEASE_CORE) {
        const key = hA.absolute("/" + asset.url);
        assert.ok(shared.hasEntry(hA.releaseName, key), `release A precached: ${asset.url}`);
    }
    // Candidate B (distinct release) with one core asset failing integrity.
    const hB = manifestHarness({ version: RELEASE_VERSION_B, assets: RELEASE_CORE }, shared);
    hB.control.badIntegrity.add(RELEASE_CORE.find(asset => asset.url.includes("dotnet.runtime.")).hash);
    let installError = null;
    try { await manifestInstall(hB); } catch (error) { installError = error; }
    assert.ok(installError, "an integrity-failed core asset must reject install");
    // The prior complete release A entries remain (not evicted by a failed B).
    for (const asset of RELEASE_CORE) {
        const key = hA.absolute("/" + asset.url);
        assert.ok(shared.hasEntry(hA.releaseName, key), `prior release A retained after failed B install: ${asset.url}`);
    }
    return { failedInstallRejected: true, priorRetained: true };
}
async function releaseMissingAssetRejectsInstall() {
    const h = manifestHarness({ version: RELEASE_VERSION_A, assets: RELEASE_CORE });
    h.control.fetchFailure.add(h.absolute("/_framework/dotnet.runtime.azrfhwb804.js"));
    let installError = null;
    try { await manifestInstall(h); } catch (error) { installError = error; }
    assert.ok(installError, "a missing core asset must reject install");
    return { missingRejected: true };
}
async function releaseFetchCoreCacheFirstImmutable() {
    const h = manifestHarness({ version: RELEASE_VERSION_A, assets: RELEASE_CORE });
    await manifestInstall(h);
    const aliasUrl = h.absolute("/_framework/dotnet.runtime.azrfhwb804.js?v=v10");
    let reply;
    h.handlers.get("fetch")({
        request: { method: "GET", url: aliasUrl, headers: new Map() },
        respondWith: (p) => { reply = p; },
    });
    const response = await reply;
    assert.ok(response && response.ok, "cached core alias served cache-first from the immutable release");
    assert.ok(!h.stats.fetches.includes(aliasUrl), "immutable core alias not re-fetched");
    // A private query on a framework JS is never cached as core or aux.
    let privateReply;
    const privateUrl = h.absolute("/_framework/dotnet.runtime.private.js?v=v10&token=x");
    h.handlers.get("fetch")({
        request: { method: "GET", url: privateUrl, headers: new Map() },
        respondWith: (p) => { privateReply = p; },
    });
    await privateReply;
    assert.ok(!h.backend.hasEntry(h.releaseName, privateUrl), "private query never cached in release");
    assert.ok(!h.backend.hasEntry(h.auxName, privateUrl), "private query never cached in aux");
    return { immutableServed: true, privateQueryExcluded: true };
}
async function releaseAuthenticatedNeverCached() {
    const h = manifestHarness({ version: RELEASE_VERSION_A, assets: RELEASE_CORE });
    await manifestInstall(h);
    const releaseBefore = h.backend.entryCount(h.releaseName);
    let reply;
    const authUrl = h.absolute("/js/editor-select.js");
    h.handlers.get("fetch")({
        request: { method: "GET", url: authUrl, headers: new Map([["authorization", "Bearer x"]]) },
        respondWith: (p) => { reply = p; },
    });
    await reply;
    assert.ok(!h.backend.hasEntry(h.auxName, authUrl), "authenticated response never cached in aux");
    assert.equal(h.backend.entryCount(h.releaseName), releaseBefore, "authenticated request never writes the immutable release cache");
    return { authExcluded: true };
}
async function releaseWarmDoesNotPoisonImmutable() {
    const h = manifestHarness({ version: RELEASE_VERSION_A, assets: RELEASE_CORE });
    await manifestInstall(h);
    const before = h.backend.entryCount(h.releaseName);
    let completion;
    const replies = [];
    h.handlers.get("message")({
        data: { type: "warm", urls: ["/_framework/dotnet.runtime.azrfhwb804.js?v=v10", "/js/editor-select.js"] },
        source: { postMessage: (v) => { try { replies.push(JSON.parse(JSON.stringify(v))); } catch {} } },
        waitUntil: (p) => { completion = p; },
    });
    await completion;
    assert.equal(replies.length, 1);
    assert.equal(replies[0].ready, true, "warm ready when release complete + aux present");
    assert.equal(h.backend.entryCount(h.releaseName), before, "warm never writes the immutable release cache");
    return { warmReadOnly: true };
}
async function releaseCleanupRestrictedToAppPrefix() {
    const h = manifestHarness({ version: RELEASE_VERSION_A, assets: RELEASE_CORE });
    await manifestInstall(h);
    // Inject an unrelated sentinel namespace plus an old app release cache into
    // the shared backend so activate cleanup runs against real per-name stores.
    await h.backend.open("sentinel-cache", h.ORIGIN);
    await h.backend.open("oop-workspace-release-old", h.ORIGIN);
    await h.backend.open(h.releaseName, h.ORIGIN);
    await h.backend.open(h.auxName, h.ORIGIN);
    h.stats.deletes.length = 0; // observe only activate-time cleanup, not install's partial clear
    await manifestActivate(h);
    // Sentinel survives; old app release is purged; current release + aux kept.
    assert.ok(!h.stats.deletes.includes("sentinel-cache"), "unrelated sentinel cache survives cleanup");
    assert.ok(h.stats.deletes.includes("oop-workspace-release-old"), "old app release purged");
    assert.ok(!h.stats.deletes.includes(h.releaseName), "current release retained");
    assert.ok(!h.stats.deletes.includes(h.auxName), "current aux cache retained");
    return { sentinelSurvived: true, oldReleasePurged: true };
}
// A missing/corrupt manifest import or invalid manifest shape must fail the
// published worker load so no install ever runs (old complete worker retained).
// There is no legacy fail-open fallback in the published worker.
function manifestLoadThrows(setup) {
    try {
        manifestHarness(setup);
        return false;
    } catch {
        return true;
    }
}
async function releaseMissingManifestRejectsLoad() {
    // importScripts throws (manifest 404 / corrupt syntax).
    const threw = manifestLoadThrows({ version: RELEASE_VERSION_A, assets: RELEASE_CORE, importFails: true });
    assert.ok(threw, "a missing/corrupt manifest import must reject the published worker load");
    return { missingManifestRejected: true };
}
async function releaseInvalidManifestShapeRejectsLoad() {
    const index = RELEASE_CORE[0];
    const invalidEntries = [
        null, {}, { ...index, url: null }, { ...index, url: 12 },
        ...["", "/index.html", "//rel.test/index.html", "https://rel.test/index.html",
            "a/../index.html", "./index.html", "a//index.html", "a\\index.html",
            "index.html?private=x", "index.html#fragment", "%69ndex.html", " index.html", "x".repeat(2049)]
            .map(url => ({ ...index, url })),
        { url: index.url },
        ...[null, 42, "", "sha256-", "sha256-idx", "not-sri",
            "sha384-" + "A".repeat(64), "sha256-" + "A".repeat(43),
            "sha256-" + "A".repeat(43) + "==", "sha256-" + "_".repeat(43) + "=",
            "sha256-" + "A".repeat(42) + "B=", // noncanonical padding bits
            index.hash + " sha256-" + "A".repeat(43) + "=", " " + index.hash]
            .map(hash => ({ ...index, hash })),
    ];
    const invalidManifests = [
        { assets: RELEASE_CORE }, { version: "", assets: RELEASE_CORE },
        { version: RELEASE_VERSION_A, assets: [] },
        ...invalidEntries.map(entry => ({ version: RELEASE_VERSION_A, assets: [...RELEASE_CORE, entry] })),
        { version: RELEASE_VERSION_A, assets: [...RELEASE_CORE, { ...index }] },
    ];
    for (const setup of invalidManifests) {
        const stats = { fetches: [], opens: [], skipWaiting: 0, deletes: [] };
        assert.throws(() => manifestHarness(setup, undefined, stats));
        assert.deepEqual(stats, { fetches: [], opens: [], skipWaiting: 0, deletes: [] },
            "bad metadata must reject before network or cache mutation");
    }
    return { invalidShapeRejectedBeforeIO: invalidManifests.length };
}
async function releaseMandatoryRoles() {
    const positive = manifestHarness({ version: "QKC38+po/", assets: RELEASE_CORE });
    await manifestInstall(positive);
    assert.equal(positive.backend.entryCount(positive.releaseName), RELEASE_CORE.length);
    for (const omitted of RELEASE_CORE) {
        const setup = { version: RELEASE_VERSION_A, assets: RELEASE_CORE.filter(asset => asset !== omitted) };
        const stats = { fetches: [], opens: [], skipWaiting: 0, deletes: [] };
        assert.throws(() => manifestHarness(setup, undefined, stats), /mandatory/);
        assert.equal(stats.fetches.length + stats.opens.length + stats.deletes.length, 0);
    }
    assert.ok(manifestLoadThrows({ version: RELEASE_VERSION_A, assets: [RELEASE_CORE[1]] }),
        "app.css alone must not displace a complete release");
    if (process.env.SDK_MANIFEST_PROOF) {
        // Optional real producer proof, not a fabricated manifest schema. This
        // evaluates only a caller-selected local SDK output in an empty VM.
        const holder = { self: {} };
        vm.runInNewContext(fs.readFileSync(process.env.SDK_MANIFEST_PROOF, "utf8"), holder, { timeout: 1000 });
        const manifest = holder.self.assetsManifest;
        const real = manifestHarness(manifest);
        await manifestInstall(real);
        assert.ok(real.backend.entryCount(real.releaseName) >= RELEASE_CORE.length);
        return { mandatoryRoles: RELEASE_CORE.length, sdkManifestEntries: manifest.assets.length,
            selectedCoreEntries: real.backend.entryCount(real.releaseName) };
    }
    return { mandatoryRoles: RELEASE_CORE.length, sdkManifestEntries: "not supplied" };
}
async function releaseInstallHoldsUntilLastPut() {
    // Gate the LAST required asset's cache.put; install must stay pending until
    // it settles, proving all fetches AND puts complete before install resolves.
    const h = manifestHarness({ version: RELEASE_VERSION_A, assets: RELEASE_CORE });
    let putGate = (function () { let r; const p = new Promise((d) => { r = d; }); return { promise: p, resolve: r }; })();
    const originalOpen = h.backend.open;
    const lastKey = h.absolute("/" + RELEASE_CORE[RELEASE_CORE.length - 1].url);
    h.backend.open = async (name, origin) => {
        const cache = await originalOpen.call(h.backend, name, origin);
        const origPut = cache.put;
        if (name === h.releaseName) {
            cache.put = async (key, resp) => {
                if (h.absolute(key) === lastKey) await putGate.promise;
                return origPut.call(cache, key, resp);
            };
        }
        return cache;
    };
    let resolved = false;
    const installPromise = manifestInstall(h).then(() => { resolved = true; });
    await tick(); await tick();
    assert.equal(resolved, false, "install stays pending while the last asset's cache.put is unsettled");
    putGate.resolve();
    await installPromise;
    assert.equal(resolved, true, "install resolves only after the last put settles");
    for (const asset of RELEASE_CORE) assert.ok(h.backend.hasEntry(h.releaseName, h.absolute("/" + asset.url)));
    return { holdsUntilLastPut: true };
}
async function releaseFailedLastAssetRejects() {
    // Make the LAST required asset fail; install must reject and the incomplete
    // staging cache must not be left as a complete release.
    const h = manifestHarness({ version: RELEASE_VERSION_A, assets: RELEASE_CORE });
    h.control.fetchFailure.add(h.absolute("/" + RELEASE_CORE[RELEASE_CORE.length - 1].url));
    let installError = null;
    try { await manifestInstall(h); } catch (error) { installError = error; }
    assert.ok(installError, "the last required asset failing must reject install");
    assert.ok(!h.backend.hasEntry(h.releaseName, h.absolute("/" + RELEASE_CORE[RELEASE_CORE.length - 1].url)), "failed last asset leaves no complete entry");
    return { failedLastAssetRejected: true };
}
async function releaseRetryAfterPartialRebuildsAll() {
    // A partial (incomplete) release from a failed attempt must be cleared and
    // ALL required assets rebuilt with integrity on a later complete install.
    const h = manifestHarness({ version: RELEASE_VERSION_A, assets: RELEASE_CORE });
    h.control.fetchFailure.add(h.absolute("/_framework/dotnet.runtime.azrfhwb804.js"));
    let firstError = null;
    try { await manifestInstall(h); } catch (e) { firstError = e; }
    assert.ok(firstError, "first (partial) install rejects");
    // The partial release exists but is incomplete.
    assert.ok(!h.backend.hasEntry(h.releaseName, h.absolute("/_framework/dotnet.runtime.azrfhwb804.js")));
    // Retry with all assets available: clears partial and rebuilds ALL.
    h.control.fetchFailure.clear();
    await manifestInstall(h);
    for (const asset of RELEASE_CORE) assert.ok(h.backend.hasEntry(h.releaseName, h.absolute("/" + asset.url)), `rebuilt after partial: ${asset.url}`);
    return { retryAfterPartialRebuildsAll: true };
}
async function releaseSameReleaseReinstallDoesNotDelete() {
    // A same-release reinstall with an already-complete cache must not delete it.
    const h = manifestHarness({ version: RELEASE_VERSION_A, assets: RELEASE_CORE });
    await manifestInstall(h);
    const before = h.backend.entryCount(h.releaseName);
    h.stats.deletes.length = 0;
    await manifestInstall(h);
    assert.equal(h.backend.entryCount(h.releaseName), before, "same-release reinstall keeps the complete cache");
    assert.ok(!h.stats.deletes.includes(h.releaseName), "same-release reinstall does not delete the active cache");
    return { sameReleaseReinstallKeepsCache: true };
}
async function releasePrivateAndApiNeverCached() {
    const h = manifestHarness({ version: RELEASE_VERSION_A, assets: RELEASE_CORE });
    await manifestInstall(h);
    for (const privateUrl of [
        "/api/private?token=x",
        "/drive/file?token=x",
        "/providers/response.json",
        "/_framework/dotnet.runtime.unknown.js?v=v10&token=x",
    ]) {
        let reply;
        h.handlers.get("fetch")({
            request: { method: "GET", url: h.absolute(privateUrl), headers: new Map() },
            respondWith: (p) => { reply = p; },
        });
        await reply;
        assert.ok(!h.backend.hasEntry(h.releaseName, h.absolute(privateUrl)), `private not in release: ${privateUrl}`);
        assert.ok(!h.backend.hasEntry(h.auxName, h.absolute(privateUrl)), `private not in aux: ${privateUrl}`);
    }
    return { privateAndApiNeverCached: true };
}
async function releaseUnknownCoreUrlNotFetchedIntoSnapshot() {
    // A core PATH not in this release's manifest must not be served from or
    // fetched into the immutable snapshot (no future-Client-asset mixing).
    const h = manifestHarness({ version: RELEASE_VERSION_A, assets: RELEASE_CORE });
    await manifestInstall(h);
    const futureUrl = h.absolute("/_framework/Client.future000000.wasm");
    let reply;
    h.handlers.get("fetch")({
        request: { method: "GET", url: futureUrl, headers: new Map() },
        respondWith: (p) => { reply = p; },
    });
    const response = await reply;
    assert.ok(!response || !response.ok, "unknown future core URL is not served");
    assert.ok(!h.backend.hasEntry(h.releaseName, futureUrl), "unknown core URL never written to release");
    assert.ok(!h.stats.fetches.includes(futureUrl), "unknown core URL never fetched into the snapshot");
    return { unknownCoreNotFetched: true };
}
async function releaseNavigationAdmission() {
    const h = manifestHarness({ version: RELEASE_VERSION_A, assets: RELEASE_CORE });
    await manifestInstall(h);
    const negatives = [
        ["/", [["authorization", "Bearer test"]]],
        ["/index.html", [["authorization", "Bearer test"]]],
        ["/smoke", [["proxy-authorization", "test"]]],
        ["/api/private", []], ["/api", []], ["/drive/file", []], ["/providers/response", []],
        ["/oauth/callback", []], ["/token", []],
        ["/smoke?private=test", []], ["/?private=test", []],
        ["/?v=v10", []], ["/index.html?v=v10", []],
        ["/_framework/dotnet.js?v=v9", []], ["/_framework/dotnet.js?v=v10&token=test", []],
        ["/smoke?", []], ["/%61pi/private", []],
    ];
    for (const [route, headers] of negatives) {
        const before = JSON.stringify(h.stats);
        let intercepted = false;
        h.handlers.get("fetch")({
            request: { method: "GET", mode: "navigate", url: h.absolute(route), headers: new Map(headers) },
            respondWith: () => { intercepted = true; },
        });
        assert.equal(intercepted, false, `navigation must bypass cache: ${route}`);
        assert.equal(JSON.stringify(h.stats), before, "no cache or fetch IO by worker on bypass");
    }
    for (const route of ["/", "/index.html", "/smoke", "/project/one"]) {
        let reply;
        const beforeFetch = h.stats.fetches.length;
        h.handlers.get("fetch")({
            request: { method: "GET", mode: "navigate", url: h.absolute(route), headers: new Map() },
            respondWith: value => { reply = value; },
        });
        assert.equal((await reply).ok, true, `public SPA navigation uses cached shell: ${route}`);
        assert.equal(h.stats.fetches.length, beforeFetch);
    }
    return { privateAuthNavigationsBypass: negatives.length, publicSpaNavigations: 4 };
}
async function checkedWarm(h, urls) {
    const replies = [];
    let completion;
    h.handlers.get("message")({
        data: { type: "warm", urls },
        source: { postMessage: value => replies.push(value) },
        waitUntil: value => { completion = value; },
    });
    await completion;
    assert.equal(replies.length, 1);
    return replies[0];
}
async function releaseWarmBoundsAndReadOnly() {
    const h = manifestHarness({ version: RELEASE_VERSION_A, assets: RELEASE_CORE });
    await manifestInstall(h);
    const invalid = [null, "/index.html", {}, new Array(1000000),
        new Array(513).fill("/index.html"), ["x".repeat(2049)], [""], [null], [42],
        [{ url: "/index.html" }], new Array(2)];
    for (const urls of invalid) {
        const before = JSON.stringify(h.stats);
        const response = await checkedWarm(h, urls);
        assert.equal(response.ready, false);
        assert.equal(response.error, "invalid-warm-request");
        assert.equal(response.results.length, 0, "constant-size negative; never echo untrusted input");
        assert.equal(JSON.stringify(h.stats), before, "invalid warm must not open caches or fetch");
    }
    const boundary = await checkedWarm(h, new Array(512).fill("/index.html"));
    assert.equal(boundary.ready, true);
    assert.equal(boundary.results.length, 512);
    const before = h.stats.fetches.length;
    const badUrls = ["https://elsewhere.invalid/index.html", "https://user:pass@rel.test/index.html",
        "/api/private", "/index.html?token=test", "/_framework/dotnet.js?v=v9", "/index.html?",
        "/_framework/Client.future.wasm", "http://["];
    const rejected = await checkedWarm(h, badUrls);
    assert.equal(rejected.ready, false);
    assert.ok(rejected.results.every(result => result.cached === false));
    assert.equal(h.stats.fetches.length, before, "unsupported warm URLs cannot trigger fetches");
    // Removing required content must not be hidden by a mutable aux copy.
    h.backend.deleteEntry(h.releaseName, h.absolute("/index.html"));
    const aux = await h.backend.open(h.auxName, h.ORIGIN);
    await aux.put("/index.html", { ok: true });
    const missing = await checkedWarm(h, ["/index.html"]);
    assert.equal(missing.ready, false);
    assert.equal(missing.results[0].cached, false);
    assert.equal(h.stats.fetches.length, before, "warm never repairs core through unsigned aux fetches");
    assert.equal(h.backend.hasEntry(h.releaseName, h.absolute("/index.html")), false);
    return { invalidPayloadsWithoutIO: invalid.length, boundaryUrls: 512,
        unsupportedUrlsReadOnly: badUrls.length, missingCoreCannotClaimReady: true };
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
        frameworkVersionIsolation: await frameworkVersionIsolation(),
        releaseCompleteInstall: await releaseCompleteInstallIntegrityChecked(),
        releaseInterruptedInstall: await releaseInterruptedInstallRejectsAndPriorRetained(),
        releaseMissingAsset: await releaseMissingAssetRejectsInstall(),
        releaseFetchImmutable: await releaseFetchCoreCacheFirstImmutable(),
        releaseAuthenticated: await releaseAuthenticatedNeverCached(),
        releaseWarm: await releaseWarmDoesNotPoisonImmutable(),
        releaseCleanup: await releaseCleanupRestrictedToAppPrefix(),
        releaseMissingManifest: await releaseMissingManifestRejectsLoad(),
        releaseInvalidShape: await releaseInvalidManifestShapeRejectsLoad(),
        releaseMandatoryRoles: await releaseMandatoryRoles(),
        releaseLastPut: await releaseInstallHoldsUntilLastPut(),
        releaseFailedLastAsset: await releaseFailedLastAssetRejects(),
        releaseRetryAfterPartial: await releaseRetryAfterPartialRebuildsAll(),
        releaseSameReinstall: await releaseSameReleaseReinstallDoesNotDelete(),
        releasePrivateApi: await releasePrivateAndApiNeverCached(),
        releaseUnknownCore: await releaseUnknownCoreUrlNotFetchedIntoSnapshot(),
        releaseNavigation: await releaseNavigationAdmission(),
        releaseWarmBounds: await releaseWarmBoundsAndReadOnly(),
    };
    console.log(JSON.stringify({ success: true, ...result }));
})()
    .catch((error) => {
        console.error(error);
        process.exitCode = 1;
    })
    .finally(() => clearTimeout(deadline));
