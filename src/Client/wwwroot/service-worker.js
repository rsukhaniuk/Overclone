// DEVELOPMENT service worker (no manifest). The Blazor SDK publishes
// service-worker.published.js as /service-worker.js in production, where the
// manifest-required immutable-release worker runs. This dev worker keeps the
// simple shell behavior for `dotnet run`: a small CORE_ASSETS precache,
// network-first revalidation of stable boot filenames, cache-first hashed
// assets, and runtime warming after startup. It is the legacy fallback that
// lets the deterministic Node contract harness exercise the warm protocol
// without a manifest; production never falls back to this non-atomic path.
//
// User documents are never cached here — they live in OPFS/IndexedDB.

const CACHE_VERSION = "v10";
const SHELL_CACHE = `oop-workspace-shell-${CACHE_VERSION}`;
const CACHE_PREFIX = "oop-workspace-";
const CORE_ASSETS = [
    "/",
    "/index.html",
    "/app.css",
    "/manifest.json",
    "icons/icon-192.png",
    "icons/icon-512.png",
];

self.addEventListener("install", (event) => {
    event.waitUntil(
        (async () => {
            const cache = await caches.open(SHELL_CACHE);
            // Precache each core asset independently so a single download failure
            // does not abort installation; the runtime warming + fetch handlers
            // retry missing assets on the next request.
            await Promise.all(
                CORE_ASSETS.map(async (url) => {
                    try {
                        await cache.add(new Request(url, { cache: "reload" }));
                        return true;
                    } catch (error) {
                        console.warn(`[sw] precache failed for ${url}:`, error);
                        return false;
                    }
                }),
            );
            // Do NOT skipWaiting: an editing session keeps using the old shell until
            // it is closed by the user. The new service worker waits to activate.
        })(),
    );
});

self.addEventListener("activate", (event) => {
    event.waitUntil(
        (async () => {
            // Cleanup is restricted to THIS app's named cache prefix; unrelated
            // Cache API entries are never deleted.
            const keys = await caches.keys();
            await Promise.all(
                keys
                    .filter((k) => k.startsWith(CACHE_PREFIX) && k !== SHELL_CACHE)
                    .map((k) => caches.delete(k)),
            );
            await self.clients.claim();
            const clients = await self.clients.matchAll({
                includeUncontrolled: true,
            });
            for (const client of clients) {
                client.postMessage({ type: "activated" });
            }
        })(),
    );
});

// Non-hashed shell assets whose URL does not change across deploys, so they must
// be revalidated over the network to avoid serving a stale version.
const REVALIDATE = new Set([
    "/app.css",
    "/manifest.json",
    "/index.html",
    "/",
    "/_framework/blazor.webassembly.js",
    "/_framework/dotnet.js",
]);

self.addEventListener("fetch", (event) => {
    const request = event.request;

    let url;
    try {
        url = new URL(request.url);
    } catch {
        return;
    }
    if (url.origin !== self.location.origin) return;
    // Native entry requires an immutable SDK release, never this dev cache.
    if (url.pathname.startsWith("/__oop/native-worker") ||
        (["/js/compile-port-worker.js", "/js/dvi-port-worker.js", "/js/unicode-tex-port-worker.js", "/js/pdf-port-worker.js", "/js/synctex-query-worker.js"].includes(url.pathname) && request.destination)) {
        event.respondWith(new Response("Native worker entry unsupported in development", { status: 403, headers: {
            "Content-Type": "text/plain; charset=utf-8", "Cache-Control": "no-store",
            "Content-Security-Policy": "default-src 'none'; script-src 'none'; connect-src 'none'; worker-src 'none'; object-src 'none'",
        } }));
        return;
    }
    if (request.method !== "GET") return;
    if (url.pathname === "/service-worker.js") return;

    // Navigation requests: network-first, fall back to cached index.html so the
    // SPA reloads offline. A failed navigation still opens the cached shell.
    if (request.mode === "navigate") {
        event.respondWith(
            (async () => {
                try {
                    const fresh = await fetch(request);
                    const cache = await caches.open(SHELL_CACHE);
                    cache.put("/index.html", fresh.clone()).catch(() => {});
                    return fresh;
                } catch {
                    const cache = await caches.open(SHELL_CACHE);
                    return (
                        (await cache.match("/index.html")) ||
                        (await cache.match("/")) ||
                        Response.error()
                    );
                }
            })(),
        );
        return;
    }

    if (REVALIDATE.has(url.pathname)) {
        event.respondWith(
            (async () => {
                try {
                    const fresh = await fetch(request);
                    const cache = await caches.open(SHELL_CACHE);
                    cache.put(request, fresh.clone()).catch(() => {});
                    return fresh;
                } catch {
                    const cache = await caches.open(SHELL_CACHE);
                    return (await cache.match(request)) || Response.error();
                }
            })(),
        );
        return;
    }

    // Other same-origin GET (Blazor _framework hashed assets, JS, icons):
    // cache-first. Missing assets are fetched, cached, and returned. Once cached,
    // the app starts fully offline. Hashed names change on rebuild, so a cache hit
    // is current; a miss triggers a fresh fetch.
    event.respondWith(
        (async () => {
            const cache = await caches.open(SHELL_CACHE);
            const cached = await cache.match(request);
            if (cached) return cached;
            try {
                const fresh = await fetch(request);
                if (fresh.ok) cache.put(request, fresh.clone()).catch(() => {});
                return fresh;
            } catch {
                return cached || Response.error();
            }
        })(),
    );
});

// Warm rounds share one four-worker IO budget. Identical overlapping requests
// share a round instead of scheduling duplicate downloads; each caller still gets
// its own ordered result/reply. Failed assets are retried only on a later message.
const WARM_CONCURRENCY = 4;
const warmRounds = new Map();
let warmTail = Promise.resolve();

function warmAssets(urls) {
    const requested = urls.slice();
    const key = requested.every((url) => typeof url === "string")
        ? JSON.stringify(requested)
        : null;
    if (key !== null && warmRounds.has(key)) return warmRounds.get(key);
    const round = warmTail.then(async () => {
        let cache;
        try {
            cache = await caches.open(SHELL_CACHE);
        } catch (error) {
            return requested.map((url) => ({ url, cached: false, error: String(error) }));
        }
        const results = new Array(requested.length);
        let next = 0;
        async function worker() {
            while (next < requested.length) {
                const index = next++;
                const url = requested[index];
                try {
                    const req = new Request(url, { cache: "reload" });
                    if (await cache.match(req)) {
                        results[index] = { url, cached: true };
                        continue;
                    }
                    const fresh = await fetch(req);
                    if (!fresh.ok) {
                        results[index] = { url, cached: false };
                        continue;
                    }
                    await cache.put(req, fresh.clone());
                    results[index] = { url, cached: true };
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
        return results;
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
    if (data && data.type === "native-worker-entry") {
        const ports = event.ports || [];
        try {
            if (ports.length === 1) ports[0].postMessage({ type: "native-worker-entry-result",
                protocol: 1, kind: data.kind === "compile" || data.kind === "pdf" ? data.kind : null,
                requestId: typeof data.requestId === "string" &&
                    /^[0-9a-f]{32}$/.test(data.requestId) ? data.requestId : null, ok: false });
        } finally { for (const port of ports) port.close(); }
        return;
    }
    if (data && data.type === "warm" && Array.isArray(data.urls)) {
        event.waitUntil(
            (async () => {
                const results = await warmAssets(data.urls);
                const ready = results.every((r) => r.cached);
                const source = event.source;
                if (source)
                    source.postMessage({ type: "warm-result", ready, results });
            })(),
        );
    }
});
