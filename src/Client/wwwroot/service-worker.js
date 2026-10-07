// Workspace service worker. Caches the application shell and the full Blazor
// runtime so the PWA can reopen and edit offline after initial provisioning.
// User documents are never cached here — they live in OPFS/IndexedDB, not the
// HTTP cache. An update stages a new shell without force-reloading an active
// editing session; the page asks the user to reload.
//
// Versioning: bump CACHE_VERSION when the shell changes. A new version installs
// alongside the old; on activation the old caches are purged. Non-hashed shell
// assets (app.css, manifest, icons) use network-first so updates are picked up
// and offline still falls back to cache. Hashed Blazor _framework assets use
// cache-first (their names change on update, so a cache hit is never stale).
//
// Offline readiness: the page's first load usually happens before this worker
// controls the client, so the runtime assets are fetched from the network and
// NOT cached by the fetch handler. On activation the worker tells the page it
// is active; the page replies with the list of same-origin resources it loaded
// (performance.getEntriesByType("resource")) and the worker fetches+caches
// each one. Only then is offline provisioned. The page exposes
// window.__oopOfflineReady so callers can wait for a genuinely provisioned shell.

const CACHE_VERSION = "v2";
const SHELL_CACHE = `oop-workspace-shell-${CACHE_VERSION}`;
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
      // Precache each core asset independently so a single download failure does
      // not abort installation; the runtime warming + fetch handlers retry
      // missing assets on the next request.
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
      // Do NOT skipWaiting: an editing session keeps using the old shell until it
      // is closed by the user. The new service worker waits to activate.
    })(),
  );
});

self.addEventListener("activate", (event) => {
  event.waitUntil(
    (async () => {
      // Purge caches from any other (older or future) version.
      const keys = await caches.keys();
      await Promise.all(
        keys.filter((k) => k !== SHELL_CACHE).map((k) => caches.delete(k)),
      );
      // Claim the first client on a fresh install so the shell is served from
      // cache immediately. For updates, activation only happens once no clients
      // are controlled by the old worker, so this never interrupts an editor.
      await self.clients.claim();
      // Tell controlled clients to warm the runtime cache with the assets they
      // already loaded (which were not intercepted on the first load).
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
const REVALIDATE = new Set(["/app.css", "/manifest.json", "/index.html", "/"]);

self.addEventListener("fetch", (event) => {
  const request = event.request;
  if (request.method !== "GET") return;

  let url;
  try {
    url = new URL(request.url);
  } catch {
    return;
  }
  if (url.origin !== self.location.origin) return;
  // Never cache the service worker itself: a stale worker would hide updates.
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

  // Non-hashed shell assets: network-first (revalidate), fall back to cache so
  // offline still works. This prevents a fresh HTML from running with a
  // stale cached CSS/manifest.
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

self.addEventListener("message", (event) => {
  const data = event.data;
  if (data === "skipWaiting") {
    self.skipWaiting();
    return;
  }
  // Warm the cache with the runtime assets the page already loaded. This is the
  // provisioning path: the first load fetched these from the network before
  // this worker controlled the client, so they must be fetched and cached now
  // for offline cold start. Message events have no respondWith(); use waitUntil
  // for the async work and reply over the message source.
  if (data && data.type === "warm" && Array.isArray(data.urls)) {
    event.waitUntil(
      (async () => {
        const cache = await caches.open(SHELL_CACHE);
        const results = await Promise.all(
          data.urls.map(async (u) => {
            const req = new Request(u, { cache: "reload" });
            if (await cache.match(req)) return { url: u, cached: true };
            try {
              const fresh = await fetch(req);
              if (fresh.ok) {
                await cache.put(req, fresh.clone());
                return { url: u, cached: true };
              }
              return { url: u, cached: false };
            } catch (error) {
              return { url: u, cached: false, error: String(error) };
            }
          }),
        );
        const ready = results.every((r) => r.cached);
        const source = event.source;
        if (source) source.postMessage({ type: "warm-result", ready, results });
      })(),
    );
  }
});
