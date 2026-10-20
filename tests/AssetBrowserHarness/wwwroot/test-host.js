// TEST ONLY: separate sentinel/PDF selection, actual worker observation and
// explicit corruption fixtures. Never used by production runtime storage policy.
let created = 0, terminated = 0, active = 0;
const NativeWorker = window.Worker;
window.Worker = class extends NativeWorker {
    constructor(...args) { super(...args); created++; active++; let closed = false; const stop = this.terminate.bind(this); this.terminate = () => { if (!closed) { closed = true; terminated++; active--; stop(); } }; }
};
export const workers = () => ({ created, terminated, active });
export function show(value) { document.getElementById('observed').textContent = value; }
async function documentIO(mode, action) {
    const database = await new Promise((resolve, reject) => { const request = indexedDB.open('asset-test-only-document-v1', 1); request.onupgradeneeded = () => request.result.createObjectStore('data'); request.onsuccess = () => resolve(request.result); request.onerror = () => reject(request.error); });
    try { return await new Promise((resolve, reject) => { const tx = database.transaction('data', mode); let value; tx.oncomplete = () => resolve(value); tx.onabort = () => reject(tx.error); action(tx.objectStore('data'), x => { value = x; }); }); }
    finally { database.close(); }
}
const get = key => documentIO('readonly', (store, done) => { const request = store.get(key); request.onsuccess = () => done(request.result); });
export const saveDocument = (pdf, sentinel) => documentIO('readwrite', (store, done) => { store.put(new Uint8Array(pdf), 'pdf'); store.put(sentinel, 'sentinel'); done(true); });
export const select = id => documentIO('readwrite', (store, done) => { store.put(id, 'selected-installation'); done(true); });
export const selected = () => get('selected-installation');
export const sentinel = () => get('sentinel');
export const pdf = () => get('pdf');
export async function cachedUrls() { const result = []; for (const key of await caches.keys()) for (const request of await (await caches.open(key)).keys()) result.push(request.url); return result.sort(); }
export async function shellReady() {
    const registration = await navigator.serviceWorker.register('./service-worker.js'); await navigator.serviceWorker.ready;
    if (!navigator.serviceWorker.controller) await new Promise(resolve => navigator.serviceWorker.addEventListener('controllerchange', resolve, { once: true }));
    return !!registration.active;
}
export async function inspectAsset(id) {
    const db = await new Promise((resolve,reject) => { const q=indexedDB.open('overclone-runtime-assets-v1',1); q.onsuccess=()=>resolve(q.result); q.onerror=()=>reject(q.error); });
    let catalog, stage;
    try { await new Promise((resolve,reject) => { const tx=db.transaction(['catalog','stages'],'readonly'); tx.oncomplete=resolve; tx.onabort=()=>reject(tx.error); const a=tx.objectStore('catalog').get(id),b=tx.objectStore('stages').get(id); a.onsuccess=()=>{catalog=a.result;}; b.onsuccess=()=>{stage=b.result;}; }); } finally { db.close(); }
    const files=[]; let directoryPresent=false;
    try { const dir=await (await (await navigator.storage.getDirectory()).getDirectoryHandle('overclone-runtime-assets-v1')).getDirectoryHandle(id); directoryPresent=true;
        for await (const [name,handle] of dir.entries()) { if(files.length>=4||handle.kind!=='file')throw new Error('bounded TEST asset inspection'); const file=await handle.getFile(); if(file.size>8388608)throw new Error('bounded TEST asset file'); const bytes=await file.arrayBuffer(); const sha256=[...new Uint8Array(await crypto.subtle.digest('SHA-256',bytes))].map(x=>x.toString(16).padStart(2,'0')).join(''); files.push({name,bytes:file.size,sha256}); }
    } catch(error) { if(error.name!=='NotFoundError')throw error; }
    return {catalogPresent:typeof catalog==='string',stageState:stage?.state??null,directoryPresent,files};
}
export async function mutate(id, index, mode) {
    if (!/^[0-9a-f]{32}$/.test(id) || !Number.isSafeInteger(index) || index < 0) throw new Error('TEST stage/index');
    if (mode === 'catalog') {
        const db = await new Promise((resolve, reject) => { const q = indexedDB.open('overclone-runtime-assets-v1', 1); q.onsuccess = () => resolve(q.result); q.onerror = () => reject(q.error); });
        try { await new Promise((resolve, reject) => { const tx = db.transaction('catalog', 'readwrite'); tx.oncomplete = resolve; tx.onabort = () => reject(tx.error); const store = tx.objectStore('catalog'), q = store.get(id); q.onsuccess = () => { let value; try { value = JSON.parse(q.result); } catch { tx.abort(); return; } value.version = 99; store.put(JSON.stringify(value), id); }; }); }
        finally { db.close(); }
        return;
    }
    const root = await (await navigator.storage.getDirectory()).getDirectoryHandle('overclone-runtime-assets-v1'); const stage = await root.getDirectoryHandle(id);
    if (mode === 'delete') { await stage.removeEntry(String(index)); return; }
    if (mode !== 'corrupt') throw new Error('Unknown TEST mutation');
    const handle = await stage.getFileHandle(String(index)); const bytes = new Uint8Array(await (await handle.getFile()).arrayBuffer()); bytes[0] ^= 1;
    const writer = await handle.createWritable(); await writer.write(bytes); await writer.close();
}
