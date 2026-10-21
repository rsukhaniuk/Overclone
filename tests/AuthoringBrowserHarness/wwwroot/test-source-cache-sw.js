// TEST-only browser cache glue. Explicit provisioning; never caches other assets.
const cacheName = "authoring-test-source-v1";
const allowed = [
    "/js/rust-worker.js",
    "/js/worker-bridge.js",
    "/rust-generated/document_engine.js",
    "/rust-generated/document_engine_bg.wasm",
];
const digest = async (bytes) => Array.from(new Uint8Array(await crypto.subtle.digest("SHA-256", bytes)), (value) => value.toString(16).padStart(2, "0")).join("");
self.addEventListener("install", (event) => event.waitUntil(self.skipWaiting()));
self.addEventListener("activate", (event) => event.waitUntil(self.clients.claim()));
self.addEventListener("message", (event) => {
    if (event.data?.type !== "provision-source" || !event.ports[0]) return;
    const port = event.ports[0];
    event.waitUntil((async () => {
        try {
            if (!event.source || new URL(event.source.url).origin !== self.location.origin) throw new Error("Unknown TEST client");
            const manifest = event.data.assets;
            if (!Array.isArray(manifest) || manifest.length !== allowed.length || manifest.some((asset, index) => asset.path !== allowed[index] || !Number.isSafeInteger(asset.bytes) || asset.bytes <= 0 || asset.bytes > 2097152 || !/^[a-f0-9]{64}$/.test(asset.sha256))) throw new Error("Invalid source manifest");
            const responses = [];
            for (const asset of manifest) {
                const response = await fetch(asset.path, { credentials: "omit", cache: "no-store", redirect: "error" });
                if (!response.ok) throw new Error("Source fetch failed");
                const bytes = await response.clone().arrayBuffer();
                if (bytes.byteLength !== asset.bytes || await digest(bytes) !== asset.sha256) throw new Error("Source digest mismatch");
                responses.push(response);
            }
            await caches.delete(cacheName);
            const cache = await caches.open(cacheName);
            for (let index = 0; index < manifest.length; index++) await cache.put(manifest[index].path, responses[index]);
            const observed = [];
            for (const asset of manifest) {
                const response = await cache.match(asset.path);
                if (!response) throw new Error("Cached source missing");
                const bytes = await response.arrayBuffer();
                observed.push({ path: asset.path, bytes: bytes.byteLength, sha256: await digest(bytes) });
            }
            if (JSON.stringify(observed) !== JSON.stringify(manifest) || (await cache.keys()).length !== allowed.length) throw new Error("Cached source mismatch");
            port.postMessage({ success: true, assets: observed });
        } catch (error) {
            await caches.delete(cacheName);
            port.postMessage({ success: false, error: String(error) });
        }
    })());
});
self.addEventListener("fetch", (event) => {
    const url = new URL(event.request.url);
    if (event.request.method !== "GET" || url.origin !== self.location.origin || url.search || !allowed.includes(url.pathname)) return;
    event.respondWith((async () => {
        const cache = await caches.open(cacheName);
        return await cache.match(url.pathname) || new Response("TEST source not provisioned", { status: 503 });
    })());
});
