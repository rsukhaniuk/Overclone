// Asset-only browser IO/transaction/lock transport. No downloads, trust engine,
// document DB, runtime selection or automatic GC. C# owns install/read policy.
const DB = 'overclone-runtime-assets-v1', ROOT = 'overclone-runtime-assets-v1', LOCK = 'overclone-runtime-assets-v1-writer';
const validId = value => typeof value === 'string' && /^[0-9a-f]{32}$/.test(value);
const fail = status => { throw Object.assign(new Error('Asset IO'), { assetStatus: status }); };
function status(error) {
    if (error?.assetStatus) return error.assetStatus;
    if (error?.name === 'QuotaExceededError') return 'quota';
    if (['NotAllowedError', 'SecurityError', 'PermissionDeniedError'].includes(error?.name)) return 'permission';
    if (error?.name === 'NotFoundError') return 'missing';
    return 'failure';
}
const attempt = async action => { try { return { status: 'ok', ...await action() }; } catch (error) { return { status: status(error) }; } };
export function createAssetStorage(owner) {
    if (!validId(owner)) throw new Error('Asset owner ID');
    const supported = !!navigator.storage?.getDirectory && !!globalThis.indexedDB && !!navigator.locks?.request;
    let database = null, closed = false;
    const check = () => { if (!supported) throw Object.assign(new Error('ASSET_UNSUPPORTED'), { assetStatus: 'unsupported' }); if (closed) fail('unavailable'); };
    const id = value => { if (!validId(value)) fail('corrupt'); return value; };
    const index = value => { if (!Number.isSafeInteger(value) || value < 0 || value > 2147483647) fail('corrupt'); return String(value); };
    const db = () => {
        check();
        if (!database) database = new Promise((resolve, reject) => {
            const request = indexedDB.open(DB, 1);
            request.onupgradeneeded = () => { request.result.createObjectStore('catalog'); request.result.createObjectStore('stages'); };
            request.onsuccess = () => resolve(request.result); request.onerror = () => reject(request.error);
        }).catch(error => { database = null; throw error; });
        return database;
    };
    const directory = async (stage, create = false) => (await (await navigator.storage.getDirectory()).getDirectoryHandle(ROOT, { create })).getDirectoryHandle(id(stage), { create });
    const leaseCheck = lease => { check(); if (!lease?.active || lease.owner !== owner) fail('not-owned'); };
    const transaction = async (mode, action) => {
        const database = await db();
        return new Promise((resolve, reject) => {
            const tx = database.transaction(['catalog', 'stages'], mode); let value, failure;
            tx.oncomplete = () => resolve(value); tx.onerror = () => { failure ||= tx.error; }; tx.onabort = () => reject(failure || tx.error || Object.assign(new Error('Aborted'), { assetStatus: 'failure' }));
            // Settle only on actual commit/abort, not the earlier request error.
            try { action(tx, result => { value = result; }, error => { failure = error; tx.abort(); }); }
            catch (error) { failure = error; tx.abort(); }
        });
    };
    const get = (table, key) => transaction('readonly', (tx, done, abort) => {
        const request = tx.objectStore(table).get(id(key)); request.onsuccess = () => done(request.result); request.onerror = () => abort(request.error);
    });
    async function tryAcquire() {
        check();
        return new Promise((resolve, reject) => {
            const lifetime = navigator.locks.request(LOCK, { mode: 'exclusive', ifAvailable: true }, async lock => {
                if (!lock) { resolve(null); return; }
                let release; const held = new Promise(done => { release = done; });
                const lease = { active: true, owner, release: async () => { if (lease.active) { lease.active = false; release(); } await lifetime; return { status: 'ok' }; } };
                resolve(lease); await held;
            }).catch(error => reject(new Error('ASSET_IO:' + status(error))));
        });
    }
    const createStage = (lease, stage, maximum) => attempt(async () => {
        leaseCheck(lease); id(stage); if (!Number.isSafeInteger(maximum) || maximum <= 0 || maximum > 1024) fail('corrupt');
        try { await directory(stage); fail('not-owned'); } catch (error) { if (error.name !== 'NotFoundError') throw error; }
        await transaction('readwrite', (tx, done, abort) => {
            const request = tx.objectStore('stages').get(stage);
            request.onsuccess = () => { if (request.result) { abort(Object.assign(new Error('Owned stage exists'), { assetStatus: 'not-owned' })); return; } const count = tx.objectStore('stages').count(); count.onsuccess = () => { if (count.result >= maximum) { abort(Object.assign(new Error('Stage record bound'), { assetStatus: 'quota' })); return; } tx.objectStore('stages').add({ owner, state: 'writing' }, stage); done({}); }; };
        });
        await directory(stage, true); return {};
    });
    const writeAsset = (lease, stage, number, bytes) => attempt(async () => {
        leaseCheck(lease); const state = await get('stages', stage); if (state?.owner !== owner || state.state !== 'writing') fail('not-owned');
        const handle = await (await directory(stage)).getFileHandle(index(number), { create: true });
        const writer = await handle.createWritable({ keepExistingData: false });
        try { await writer.write(bytes); await writer.close(); } catch (error) { try { await writer.abort(); } catch { } throw error; }
        return {};
    });
    const readAsset = (stage, number, expected, maximum) => attempt(async () => {
        check(); if (!Number.isSafeInteger(expected) || expected < 0 || !Number.isSafeInteger(maximum) || maximum <= 0 || expected > maximum) fail('corrupt');
        const file = await (await (await directory(stage)).getFileHandle(index(number))).getFile();
        if (file.size !== expected || file.size > maximum) fail('corrupt'); // BEFORE arrayBuffer/transport allocation
        const bytes = new Uint8Array(await file.arrayBuffer()); if (bytes.length !== expected) fail('corrupt');
        return { length: file.size, bytes };
    });
    const catalog = (stage, maximum) => attempt(async () => {
        check(); if (!Number.isSafeInteger(maximum) || maximum <= 0 || maximum > 1048576) fail('corrupt');
        const metadata = await get('catalog', stage); if (metadata === undefined) return { metadata: null };
        if (typeof metadata !== 'string' || metadata.length > maximum || new TextEncoder().encode(metadata).length > maximum) fail('corrupt');
        return { metadata };
    });
    const exists = stage => attempt(async () => { check(); const state = await get('stages', stage); try { await directory(stage); return { exists: true }; } catch (error) { if (error.name === 'NotFoundError') return { exists: state?.state === 'writing' }; throw error; } });
    const publish = (lease, stage, metadata, maximum) => attempt(async () => {
        leaseCheck(lease); if (typeof metadata !== 'string' || !Number.isSafeInteger(maximum) || maximum <= 0) fail('corrupt');
        await transaction('readwrite', (tx, done, abort) => {
            const stageRequest = tx.objectStore('stages').get(id(stage));
            stageRequest.onsuccess = () => {
                const state = stageRequest.result; if (state?.owner !== owner || state.state !== 'writing') { abort(Object.assign(new Error('Stage state'), { assetStatus: 'not-owned' })); return; }
                const count = tx.objectStore('catalog').count(); count.onsuccess = () => {
                    if (count.result >= maximum) { abort(Object.assign(new Error('Catalog bound'), { assetStatus: 'quota' })); return; }
                    tx.objectStore('catalog').add(metadata, stage); tx.objectStore('stages').put({ owner, state: 'installed' }, stage); done({});
                };
            };
        }); return {};
    });
    const discard = (lease, stage) => attempt(async () => {
        leaseCheck(lease);
        const allowed = await transaction('readwrite', (tx, done) => {
            const reference = tx.objectStore('catalog').get(id(stage)); reference.onsuccess = () => {
                if (reference.result !== undefined) { done(false); return; }
                const request = tx.objectStore('stages').get(stage); request.onsuccess = () => {
                    if (request.result?.owner !== owner || request.result.state === 'installed') { done(false); return; }
                    tx.objectStore('stages').put({ owner, state: 'discarded' }, stage); done(true);
                };
            };
        });
        if (allowed) { try { const root = await (await navigator.storage.getDirectory()).getDirectoryHandle(ROOT); await root.removeEntry(stage, { recursive: true }); } catch (error) { if (error.name !== 'NotFoundError') throw error; } }
        return { exists: allowed };
    });
    const dispose = async () => { closed = true; if (database) { try { (await database).close(); } catch { } } };
    return { tryAcquire, createStage, writeAsset, readAsset, catalog, exists, publish, discard, dispose };
}
