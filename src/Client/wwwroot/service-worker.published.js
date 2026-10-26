// PRODUCTION service worker (manifest-required). At publish the Blazor
// WebAssembly SDK emits this file as /service-worker.js (a manifest-version
// comment is appended) and emits /service-worker-assets.js with per-asset SRI
// integrity hashes and a content-derived version. This worker REQUIRES a
// valid manifest: a missing/corrupt manifest import, an invalid
// version/hash/asset list, or any missing/damaged required asset rejects
// installation, so an incomplete or mixed release NEVER activates and the
// prior complete release is retained. There is NO legacy fail-open fallback
// here. The dev worker (service-worker.js) handles the development server.
//
// A release is an immutable per-version snapshot built at install by fetching
// every required core asset with Request.integrity. Core assets are served
// cache-first from that snapshot (including navigation to the cached index).
// Stable URLs are never opportunistically overwritten inside an existing
// release, so old and new framework modules cannot mix. Cleanup is restricted
// to this app's oop-workspace- cache prefix; unrelated Cache API entries and
// user documents (OPFS/IndexedDB) are never touched. The existing save-aware
// C# update guard still owns the reload decision; no force activation/reload.

// ---- manifest import + shape validation (publish only) -----------------
// importScripts is synchronous; a 404 or syntax error here fails the worker
// load, so no install ever runs and the old complete worker stays active.
self.importScripts("/service-worker-assets.js");
const manifest = self.assetsManifest;
if (
    !manifest ||
    typeof manifest !== "object" ||
    typeof manifest.version !== "string" ||
    manifest.version.length === 0 ||
    manifest.version.length > 128 ||
    !/^[A-Za-z0-9+/_-]+={0,2}$/.test(manifest.version) ||
    !Array.isArray(manifest.assets) ||
    manifest.assets.length === 0 ||
    manifest.assets.length > 200000
) {
    throw new Error("[sw] invalid service-worker-assets manifest");
}

const RELEASE_PREFIX = "oop-workspace-release-";
const AUX_PREFIX = "oop-workspace-aux-";
const CACHE_PREFIX = "oop-workspace-";
const releaseCacheName = `${RELEASE_PREFIX}${manifest.version}`;
const auxCacheName = `${AUX_PREFIX}${manifest.version}`;

// Core public static assets immutable within a release. Optional provisioned
// native compiler payloads are deliberately excluded (production native
// compilation is disabled; huge distributions are not default core).
function isCorePath(path) {
    if (
        path === "/index.html" ||
        path === "/" ||
        path === "/app.css" ||
        path === "/manifest.json"
    )
        return true;
    if (/^\/icons\/icon-(192|512)\.png$/.test(path)) return true;
    if (/^\/_framework\/[A-Za-z0-9_.-]+\.(?:js|wasm|json|dat|blat|pdb)$/.test(path))
        return true;
    if (/^\/js\/[A-Za-z0-9_-]+\.js$/.test(path)) return true;
    if (/^\/rust-generated\/[A-Za-z0-9_.-]+\.(?:js|wasm)$/.test(path))
        return true;
    return false;
}

// The only query admitted into the immutable release cache is the exact current
// shell-version query on framework JavaScript dependencies (accepted v10 Blazor
// bootstrap). Every other query is excluded (no general ignoreSearch).
function isFrameworkJsAlias(path) {
    return /^\/_framework\/(?:blazor\.webassembly|dotnet(?:\.(?:runtime|native))?)(?:\.[A-Za-z0-9_-]+)?\.js$/.test(
        path,
    );
}

// One admission policy BEFORE either asset or SPA-navigation cache lookup.
// API/provider routes, explicit credentials and unsupported queries bypass the
// worker entirely, even when the request is a navigation. Extensionless public
// SPA routes (e.g. /smoke) still use the release's index document.
function isPublicRequest(url, request) {
    if (url.origin !== self.location.origin || url.username || url.password || url.hash)
        return false;
    if (request.headers?.get("authorization") || request.headers?.get("proxy-authorization"))
        return false;
    if (!/^\/(?:[A-Za-z0-9_-][A-Za-z0-9_.-]*\/?)*$/.test(url.pathname))
        return false;
    if (/(?:^|\/)(?:api|drive|providers?|oauth|token)(?:\/|$)/i.test(url.pathname))
        return false;
    if (url.search || url.href.includes("?"))
        return url.search === "?v=v10" && isFrameworkJsAlias(url.pathname);
    return true;
}

function canonicalCoreKey(url, request) {
    if (!isPublicRequest(url, request)) return null;
    const key = url.pathname === "/" ? "/index.html" : url.pathname;
    if (!isCorePath(key)) return null;
    if (url.search) {
        if (url.search !== "?v=v10" || !isFrameworkJsAlias(url.pathname)) return null;
    }
    return key;
}

// SDK URLs are canonical relative public file paths, not arbitrary URL inputs.
// Reject malformed entries (even excluded assets) BEFORE any cache/network IO.
// SHA-256 encodes 32 bytes as 43 Base64 characters + '='. The final character
// has two zero padding bits; a mere 'sha256-' prefix is not valid SRI metadata.
const SHA256_SRI = /^sha256-[A-Za-z0-9+/]{42}[AEIMQUYcgkosw048]=$/;
const MANIFEST_URL = /^[A-Za-z0-9_-][A-Za-z0-9_.-]*(?:\/[A-Za-z0-9_-][A-Za-z0-9_.-]*)*$/;

// Validate every entry, but precache only explicitly allowlisted core paths.
const requiredAssets = [];
const requiredByKey = new Map();
const manifestIndex = new Map();
const manifestKeys = new Set();
for (const asset of manifest.assets) {
    if (!asset || typeof asset !== "object" || typeof asset.url !== "string" ||
        asset.url.length > 2048 || !MANIFEST_URL.test(asset.url) ||
        typeof asset.hash !== "string" || !SHA256_SRI.test(asset.hash)) {
        throw new Error("[sw] malformed manifest asset URL or SHA-256 SRI");
    }
    const key = "/" + asset.url;
    if (manifestKeys.has(key)) throw new Error("[sw] duplicate manifest asset URL");
    manifestKeys.add(key);
    if (!isCorePath(key)) continue; // optional/native compiler payloads excluded
    requiredByKey.set(key, asset);
    manifestIndex.set(key, asset);
    requiredAssets.push({ key, url: asset.url, hash: asset.hash });
}
// Truncated metadata must not replace the working release with just app.css.
// These are basic startup/offline-editor roles observed in the SDK manifest,
// not a replacement for the SDK's complete dependency inventory. Every listed
// core asset (including all remaining framework assemblies) is still precached.
const requiredPaths = [
    "/index.html", "/app.css", "/manifest.json",
    "/icons/icon-192.png", "/icons/icon-512.png",
    "/_framework/blazor.webassembly.js", "/_framework/dotnet.js",
    "/js/workspace-storage.js", "/js/editor-select.js",
    "/rust-generated/document_engine.js", "/rust-generated/document_engine_bg.wasm",
];
const requiredRoles = [
    /^\/_framework\/dotnet\.runtime\.[A-Za-z0-9_-]+\.js$/,
    /^\/_framework\/dotnet\.native\.[A-Za-z0-9_-]+\.js$/,
    /^\/_framework\/dotnet\.native\.[A-Za-z0-9_-]+\.wasm$/,
    /^\/_framework\/Client\.[A-Za-z0-9_-]+\.wasm$/,
];
if (requiredPaths.some(path => !requiredByKey.has(path)) ||
    requiredRoles.some(role => !requiredAssets.some(asset => role.test(asset.key)))) {
    throw new Error("[sw] manifest is missing a mandatory startup/offline-editor role");
}

const PRECACHE_CONCURRENCY = 4;

// Fetch all required assets with SRI integrity, bounded to four concurrent
// fetch+put operations. Every fetch AND cache.put must settle before this
// resolves. Any failure sets `aborted` so other workers stop starting new
// fetches, then waits for all in-flight operations to settle before rejecting,
// so a late put can never resurrect an incomplete staging cache. No retry
// budget or infinite loop: one pass per asset.
async function precacheWithIntegrity(cache, assets) {
    let cursor = 0;
    let aborted = false;
    let failure = null;
    async function worker() {
        while (cursor < assets.length && !aborted) {
            const index = cursor++;
            const asset = assets[index];
            try {
                const fresh = await fetch(
                    new Request(asset.url, {
                        integrity: asset.hash,
                        cache: "reload",
                    }),
                );
                if (!fresh.ok) throw new Error(`bad status ${fresh.status} for ${asset.url}`);
                await cache.put(asset.key, fresh.clone());
            } catch (error) {
                if (!aborted) {
                    aborted = true;
                    failure = error;
                }
            }
        }
    }
    const workers = Array.from(
        { length: Math.min(PRECACHE_CONCURRENCY, assets.length) },
        () => worker(),
    );
    await Promise.all(workers); // all in-flight fetches+puts settle
    if (failure) throw failure;
}

async function releaseComplete(cache) {
    for (const asset of requiredAssets) {
        if (!(await cache.match(asset.key))) return false;
    }
    return true;
}

// Fixed native ENTRY roles only, not runtime activation. Neither optional role
// becomes mandatory source-editor startup metadata. Page CSP is unchanged.
const NATIVE_ENTRY_TYPE = "native-worker-entry";
const NATIVE_ROLES = Object.freeze({
    compile: Object.freeze({ entry: "/__oop/native-worker/compile", source: "/js/compile-port-worker.js" }),
    dvi: Object.freeze({ entry: "/__oop/native-worker/dvi", source: "/js/dvi-port-worker.js" }),
    xetex: Object.freeze({ entry: "/__oop/native-worker/xetex", source: "/js/unicode-tex-port-worker.js" }),
    luatex: Object.freeze({ entry: "/__oop/native-worker/luatex", source: "/js/unicode-tex-port-worker.js" }),
    pdf: Object.freeze({ entry: "/__oop/native-worker/pdf", source: "/js/pdf-port-worker.js" }),
    synctex: Object.freeze({ entry: "/__oop/native-worker/synctex", source: "/js/synctex-query-worker.js" }),
});
const NATIVE_WORKER_POLICY = "default-src 'none'; script-src blob: 'wasm-unsafe-eval'; connect-src 'none'; worker-src 'none'; object-src 'none'";
const NATIVE_DENY_POLICY = "default-src 'none'; script-src 'none'; connect-src 'none'; worker-src 'none'; object-src 'none'";
const NATIVE_SOURCE_LIMIT = 32768;
const NATIVE_PENDING_LIMIT = 4;
let nativePending = 0;

function nativeRefusal() {
    return new Response("Native worker entry unavailable", { status: 403, headers: {
        "Content-Type": "text/plain; charset=utf-8",
        "Content-Security-Policy": NATIVE_DENY_POLICY,
        "Cache-Control": "no-store",
        "X-Content-Type-Options": "nosniff",
    } });
}

function nativeEntryQuery(kind, release, digest) {
    return new URLSearchParams({ protocol: "1", kind, release, digest }).toString();
}

async function nativeReleaseSource(kind) {
    if (!Object.hasOwn(NATIVE_ROLES, kind)) throw new Error("native-role-unavailable");
    const role = NATIVE_ROLES[kind];
    const asset = manifestIndex.get(role.source);
    if (!asset) throw new Error("native-role-unavailable");
    // No network recovery or mutable aux cache for this execution path.
    const cache = await caches.open(releaseCacheName);
    if (!(await releaseComplete(cache))) throw new Error("incomplete-release");
    const response = await cache.match(role.source);
    if (!response?.ok || !response.body) throw new Error("missing-native-source");
    const reader = response.body.getReader();
    const chunks = [];
    let size = 0, complete = false;
    try {
        for (;;) {
            const { done, value } = await reader.read();
            if (done) { complete = true; break; }
            size += value.byteLength;
            if (size > NATIVE_SOURCE_LIMIT) throw new Error("native-source-bound");
            chunks.push(value);
        }
    } finally {
        if (!complete) reader.cancel().catch(() => {}); // cancellation must not stall refusal on a tee/closed stream
        reader.releaseLock();
    }
    if (size === 0) throw new Error("empty-native-source");
    const bytes = new Uint8Array(size);
    let offset = 0;
    for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
    const hash = new Uint8Array(await crypto.subtle.digest("SHA-256", bytes));
    const sri = "sha256-" + btoa(String.fromCharCode(...hash));
    if (sri !== asset.hash) throw new Error("native-source-integrity");
    const digest = Array.from(hash, value => value.toString(16).padStart(2, "0")).join("");
    return { bytes, sri, digest };
}

async function nativeWorkerResponse(url, request) {
    if (nativePending >= NATIVE_PENDING_LIMIT) return nativeRefusal();
    const kind = url.searchParams.get("kind");
    const role = Object.hasOwn(NATIVE_ROLES, kind) ? NATIVE_ROLES[kind] : null;
    if (!role || request.method !== "GET" || request.destination !== "worker" ||
        url.pathname !== role.entry || url.href.length > 512 || url.username || url.password || url.hash ||
        request.headers?.get("authorization") || request.headers?.get("proxy-authorization")) return nativeRefusal();
    const fields = [...url.searchParams.keys()];
    if (fields.length !== 4 || new Set(fields).size !== 4 ||
        url.searchParams.get("protocol") !== "1" ||
        url.searchParams.get("release") !== manifest.version ||
        !/^[0-9a-f]{64}$/.test(url.searchParams.get("digest") || "")) return nativeRefusal();
    nativePending++;
    try {
        const source = await nativeReleaseSource(kind);
        if (url.search !== "?" + nativeEntryQuery(kind, manifest.version, source.digest)) return nativeRefusal();
        return new Response(source.bytes, { headers: {
            "Content-Type": "text/javascript; charset=utf-8",
            "Content-Security-Policy": NATIVE_WORKER_POLICY,
            "Cache-Control": "no-store",
            "X-Content-Type-Options": "nosniff",
            "X-Oop-Worker-Protocol": "1", "X-Oop-Worker-Kind": kind,
            "X-Oop-Worker-Release": manifest.version, "X-Oop-Worker-Sha256": source.digest,
        } });
    } catch { return nativeRefusal(); }
    finally { nativePending--; }
}

async function nativeEntryMessage(event, data) {
    const port = event.ports?.length === 1 ? event.ports[0] : null;
    if (!port) { for (const extra of event.ports || []) extra.close(); return; }
    const requestId = typeof data.requestId === "string" && /^[0-9a-f]{32}$/.test(data.requestId) ? data.requestId : null;
    const kind = Object.hasOwn(NATIVE_ROLES, data.kind) ? data.kind : null;
    const reply = { type: "native-worker-entry-result", protocol: 1, kind, requestId, ok: false };
    let ownsSlot = false;
    try {
        let clientUrl;
        try { clientUrl = new URL(event.source?.url); } catch { return; }
        if (Object.keys(data).sort().join(",") !== "kind,protocol,requestId,type" ||
            data.protocol !== 1 || !kind || !requestId ||
            event.source?.type !== "window" || clientUrl.origin !== self.location.origin ||
            nativePending >= NATIVE_PENDING_LIMIT) return;
        nativePending++; ownsSlot = true;
        const source = await nativeReleaseSource(kind);
        Object.assign(reply, { ok: true, release: manifest.version, source: NATIVE_ROLES[kind].source,
            digest: source.digest, sri: source.sri, policy: NATIVE_WORKER_POLICY,
            entry: NATIVE_ROLES[kind].entry + "?" + nativeEntryQuery(kind, manifest.version, source.digest) });
    } catch { /* Fixed-size rejection, not implementation/provider error text. */ }
    finally {
        if (ownsSlot) nativePending--;
        try { port.postMessage(reply); } finally { port.close(); }
    }
}

self.addEventListener("install", (event) => {
    event.waitUntil(
        (async () => {
            const releaseCache = await caches.open(releaseCacheName);
            // Same-release reinstall must not delete an actively-used complete
            // cache: if every required entry is already present, install is done.
            if (await releaseComplete(releaseCache)) return;
            // A partial (failed earlier) staging cache is cleared and ALL
            // required assets are rebuilt with integrity. The aux cache is
            // ensured to exist for warming.
            await caches.delete(releaseCacheName);
            const fresh = await caches.open(releaseCacheName);
            await caches.open(auxCacheName);
            await precacheWithIntegrity(fresh, requiredAssets); // throws on any failure
            // Do NOT skipWaiting: the save-aware C# guard owns activation.
        })(),
    );
});

self.addEventListener("activate", (event) => {
    event.waitUntil(
        (async () => {
            // Cleanup is restricted to THIS app's cache prefix. The current
            // release and aux caches are retained; unrelated entries survive.
            const keep = new Set([releaseCacheName, auxCacheName]);
            const keys = await caches.keys();
            await Promise.all(
                keys
                    .filter((k) => k.startsWith(CACHE_PREFIX) && !keep.has(k))
                    .map((k) => caches.delete(k)),
            );
            await self.clients.claim();
            const clients = await self.clients.matchAll({ includeUncontrolled: true });
            for (const client of clients) client.postMessage({ type: "activated" });
        })(),
    );
});

self.addEventListener("fetch", (event) => {
    const request = event.request;
    let url;
    try {
        url = new URL(request.url);
    } catch {
        return;
    }
    if (url.origin !== self.location.origin) return; // never cache cross-origin
    // Reserved protocol routes never fall through to SPA/network handling.
    if (url.pathname.startsWith("/__oop/native-worker")) {
        event.respondWith(nativeWorkerResponse(url, request));
        return;
    }
    // Integrity fetches remain possible; executing this source directly does not.
    if (request.destination && Object.values(NATIVE_ROLES).some(role => url.pathname === role.source)) {
        event.respondWith(nativeRefusal());
        return;
    }
    if (request.method !== "GET") return;
    if (url.pathname === "/service-worker.js") return;
    if (url.pathname === "/service-worker-assets.js") return;
    if (!isPublicRequest(url, request)) return; // includes private/authenticated navigations

    const coreKey = canonicalCoreKey(url, request);

    // Core assets: immutable release cache-first. A KNOWN core asset missing
    // from the cache may recover ONLY its exact manifest/SRI bytes; there is
    // no unverified fetch fallback. A core PATH not in this release's manifest
    // is never served from or fetched into the snapshot.
    if (coreKey !== null) {
        event.respondWith(
            (async () => {
                const cache = await caches.open(releaseCacheName);
                const cached = await cache.match(coreKey);
                if (cached) return cached;
                const asset = manifestIndex.get(coreKey);
                if (!asset) return Response.error(); // unknown/future core URL
                try {
                    const fresh = await fetch(
                        new Request(coreKey, { integrity: asset.hash, cache: "reload" }),
                    );
                    if (!fresh.ok) return Response.error();
                    await cache.put(coreKey, fresh.clone());
                    return fresh.clone();
                } catch {
                    return Response.error();
                }
            })(),
        );
        return;
    }

    // Navigation: serve the cached index from the immutable release only. A
    // cache miss never fetches a new index (no mixed-version shell).
    if (request.mode === "navigate") {
        event.respondWith(
            (async () => {
                const cache = await caches.open(releaseCacheName);
                return (
                    (await cache.match("/index.html")) ||
                    (await cache.match("/")) ||
                    Response.error()
                );
            })(),
        );
        return;
    }

    // Other resources bypass this worker. Optional runtime provisioning owns
    // its own storage; no opportunistic asset/API caching into release caches.
});

// Warming is release-aware and READ-ONLY for the immutable release cache: a URL
// present in the release is reported cached without re-fetching or writing, so
// warm messages can never poison the snapshot. Readiness is true only when the
// release snapshot is complete AND every requested URL is cached — never merely
// after six shell URLs. Warm checks never fetch or populate an aux cache.
// 512 URLs x 2048 characters fits the captured 236-entry SDK publication plus
// explicit bootstrap glue, with a fixed-size negative response for bad input.
const MAX_WARM_URLS = 512;
const MAX_WARM_URL_LENGTH = 2048;
const WARM_CONCURRENCY = 4;
const warmRounds = new Map();
let warmTail = Promise.resolve();

function warmAssets(urls) {
    const requested = urls.slice();
    const key = requested.every((u) => typeof u === "string")
        ? JSON.stringify(requested)
        : null;
    if (key !== null && warmRounds.has(key)) return warmRounds.get(key);
    const round = warmTail.then(async () => {
        const releaseCache = await caches.open(releaseCacheName).catch(() => null);
        const results = new Array(requested.length);
        let cursor = 0;
        async function worker() {
            while (cursor < requested.length) {
                const index = cursor++;
                const url = requested[index];
                try {
                    let parsed;
                    try {
                        parsed = new URL(url, self.location.origin);
                    } catch {
                        results[index] = { url, cached: false };
                        continue;
                    }
                    if (!isPublicRequest(parsed, { headers: new Map() })) {
                        results[index] = { url, cached: false };
                        continue;
                    }
                    const coreKey = canonicalCoreKey(parsed, { headers: new Map() });
                    const cached = !!(releaseCache && coreKey && manifestIndex.has(coreKey) &&
                        (await releaseCache.match(coreKey)));
                    results[index] = { url, cached };
                } catch (error) {
                    results[index] = { url, cached: false, error: String(error) };
                }
            }
        }
        await Promise.all(
            Array.from(
                { length: Math.min(WARM_CONCURRENCY, requested.length) },
                () => worker(),
            ),
        );
        const snapshotComplete = releaseCache ? await releaseComplete(releaseCache) : false;
        return { results, ready: snapshotComplete && results.every((r) => r.cached) };
    });
    if (key !== null) warmRounds.set(key, round);
    warmTail = round.then(
        () => {
            if (key !== null) warmRounds.delete(key);
        },
        () => {
            if (key !== null) warmRounds.delete(key);
        },
    );
    return round;
}

self.addEventListener("message", (event) => {
    const data = event.data;
    if (data === "skipWaiting") {
        self.skipWaiting();
        return;
    }
    if (data && data.type === NATIVE_ENTRY_TYPE) {
        event.waitUntil(nativeEntryMessage(event, data));
        return;
    }
    if (data && data.type === "warm") {
        // Validate before slice/stringify/results allocation or any cache IO.
        if (!Array.isArray(data.urls) || data.urls.length > MAX_WARM_URLS) {
            event.source?.postMessage({ type: "warm-result", ready: false, results: [], error: "invalid-warm-request" });
            return;
        }
        for (let i = 0; i < data.urls.length; i++) {
            const url = data.urls[i];
            if (typeof url !== "string" || url.length === 0 || url.length > MAX_WARM_URL_LENGTH) {
                event.source?.postMessage({ type: "warm-result", ready: false, results: [], error: "invalid-warm-request" });
                return;
            }
        }
        event.waitUntil(
            (async () => {
                const { results, ready } = await warmAssets(data.urls);
                if (event.source)
                    event.source.postMessage({ type: "warm-result", ready, results });
            })(),
        );
    }
});
