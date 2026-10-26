// Public technical metadata only. C# validates schema/identity before runtime IO.
// Separate DB: no access to workspace data or runtime package bytes.
const DB = 'overclone-local-runtime-selection-v1';
// Transport envelope for the explicit multi-engine inventory. C# retains each
// profile's stricter schema, closure and metadata limit before any resource IO.
const LIMIT = 4194304;
function bounded(value, limit = LIMIT) {
    if (typeof value !== 'string' || value.length > limit || new TextEncoder().encode(value).length > limit)
        throw new Error('SELECTION_IO:corrupt');
    return value;
}
async function transact(mode, value, dbName = DB, limit = LIMIT) {
    const db = await new Promise((resolve, reject) => {
        let abandoned = false;
        const request = indexedDB.open(dbName, 1);
        request.onupgradeneeded = () => request.result.createObjectStore('selection');
        request.onsuccess = () => { if (abandoned) request.result.close(); else resolve(request.result); };
        request.onerror = () => reject(ioError(request.error));
        request.onblocked = () => { abandoned = true; reject(new Error('SELECTION_IO:busy')); };
    });
    try {
        return await new Promise((resolve, reject) => {
            const tx = db.transaction('selection', mode);
            const store = tx.objectStore('selection');
            const request = mode === 'readonly' ? store.get('active') : store.put(bounded(value, limit), 'active');
            let result = null, corrupt = false;
            request.onsuccess = () => {
                if (mode === 'readonly') {
                    try { result = request.result === undefined ? null : bounded(request.result, limit); }
                    catch { corrupt = true; tx.abort(); }
                }
            };
            tx.oncomplete = () => resolve(result);
            tx.onerror = tx.onabort = () => reject(corrupt ? new Error('SELECTION_IO:corrupt') : ioError(tx.error));
        });
    } finally { db.close(); }
}
function ioError(error) {
    if (error?.name === 'QuotaExceededError') return new Error('SELECTION_IO:quota');
    if (error?.name === 'SecurityError' || error?.name === 'NotAllowedError') return new Error('SELECTION_IO:permission');
    return new Error('SELECTION_IO:storage');
}
export async function readSelection() {
    try { return await transact('readonly'); }
    catch (error) { throw error?.message?.startsWith('SELECTION_IO:') ? error : ioError(error); }
}
export function writeSelection(metadata) { return transact('readwrite', bounded(metadata)); }

// Fixed separate PDF record/database. Never selects or activates executable code.
const PDF_DB = 'overclone-local-pdf-resource-selection-v1';
const PDF_LIMIT = 16384;
export async function readPdfSelection() {
    try { return await transact('readonly', undefined, PDF_DB, PDF_LIMIT); }
    catch (error) { throw error?.message?.startsWith('SELECTION_IO:') ? error : ioError(error); }
}
export function writePdfSelection(metadata) { return transact('readwrite', bounded(metadata, PDF_LIMIT), PDF_DB, PDF_LIMIT); }
