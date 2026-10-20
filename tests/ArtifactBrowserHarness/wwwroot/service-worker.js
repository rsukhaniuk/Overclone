// TEST-ONLY published shell PWA. Native runtime/PDF/private inputs NEVER cached.
const CACHE = 'overclone-artifact-test-shell-v1';
let allowed;
async function manifest() {
    if (!allowed) {
        const response = await caches.match('./shell-manifest.json') || await fetch('./shell-manifest.json', { cache: 'no-store' });
        const data = await response.json();
        if (!Array.isArray(data) || data.length > 1024 || data.some(url => typeof url !== 'string' || url.includes('private-inputs') || url.includes('test-inputs'))) throw new Error('TEST shell manifest');
        allowed = data;
    }
    return allowed;
}
self.addEventListener('install', event => event.waitUntil((async () => {
    const cache = await caches.open(CACHE); const response = await fetch('./shell-manifest.json', { cache: 'no-store' });
    await cache.put('./shell-manifest.json', response.clone());
    const list = await manifest();
    for (const url of list) { const asset = await fetch(url, { cache: 'reload' }); if (!asset.ok) throw new Error('Missing TEST shell asset'); await cache.put(url, asset); }
    await self.skipWaiting();
})()));
self.addEventListener('activate', event => event.waitUntil(self.clients.claim()));
self.addEventListener('fetch', event => {
    const url = new URL(event.request.url);
    if (url.origin !== self.location.origin || url.pathname.includes('private-inputs') || url.pathname.includes('test-inputs')) { event.respondWith(fetch(event.request, { cache: 'no-store' })); return; }
    event.respondWith((async () => {
        const cache = await caches.open(CACHE);
        if (event.request.mode === 'navigate') return await cache.match('./index.html') || fetch(event.request);
        const list = await manifest();
        if (list.some(value => new URL(value, self.location).href === url.href) || url.pathname.endsWith('/shell-manifest.json')) return await cache.match(event.request) || fetch(event.request);
        return fetch(event.request, { cache: 'no-store' });
    })());
});
