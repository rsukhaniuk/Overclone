// Workspace storage glue. Pure browser-API transport: OPFS immutable content
// objects, IndexedDB manifests/head/checkpoints, Web Locks and capability
// detection. No domain logic lives here; the C# WorkspaceStorage owns the
// commit protocol, stale-head detection and recovery decisions.

const DB_NAME = "oop-workspace";
const DB_VERSION = 1;
const STORE_PROJECTS = "projects"; // keyPath "id"
const STORE_REVISIONS = "revisions"; // keyPath "id", index "projectId"
const STORE_CHECKPOINTS = "checkpoints"; // keyPath "id", index "projectId"
const CONTENT_DIR = "content";

let dbPromise;
let opfsRoot;
let contentDir;
let locks; // held lock resolvers keyed by lockId
let lockSeq = 0;

// ---------- utilities ----------

function bytesToHex(bytes) {
    const hex = [];
    for (let i = 0; i < bytes.length; i++) {
        hex.push(bytes[i].toString(16).padStart(2, "0"));
    }
    return hex.join("");
}

async function sha256Hex(buffer) {
    const digest = await crypto.subtle.digest("SHA-256", buffer);
    return bytesToHex(new Uint8Array(digest));
}

// ---------- capabilities ----------

export function capabilities() {
    const opfs = !!(
        navigator.storage &&
        typeof navigator.storage.getDirectory === "function"
    );
    const indexedDB = !!self.indexedDB;
    const webLocks = !!(
        navigator.locks && typeof navigator.locks.request === "function"
    );
    const persist = !!(
        navigator.storage && typeof navigator.storage.persist === "function"
    );
    const estimate = !!(
        navigator.storage && typeof navigator.storage.estimate === "function"
    );
    return { opfs, indexedDB, webLocks, persist, estimate };
}

export async function requestPersistent() {
    if (!navigator.storage || typeof navigator.storage.persist !== "function")
        return false;
    try {
        return await navigator.storage.persist();
    } catch {
        return false;
    }
}

export async function storageEstimate() {
    if (
        !navigator.storage ||
        typeof navigator.storage.estimate !== "function"
    ) {
        return { quota: null, usage: null };
    }
    try {
        const est = await navigator.storage.estimate();
        return { quota: est.quota ?? null, usage: est.usage ?? null };
    } catch {
        return { quota: null, usage: null };
    }
}

// ---------- OPFS content objects ----------

async function getContentDir() {
    if (contentDir) return contentDir;
    if (
        !navigator.storage ||
        typeof navigator.storage.getDirectory !== "function"
    ) {
        throw new Error("OPFS is not available in this browser.");
    }
    opfsRoot = await navigator.storage.getDirectory();
    contentDir = await opfsRoot.getDirectoryHandle(CONTENT_DIR, {
        create: true,
    });
    return contentDir;
}

// Writes bytes to content/<hash>, then re-reads and independently verifies
// byte length and SHA-256 before returning. Returns { hash, byteLength, verified }.
export async function writeContent(hash, bytes) {
    const dir = await getContentDir();
    if (!(bytes instanceof Uint8Array)) bytes = new Uint8Array(bytes);
    const fileHandle = await dir.getFileHandle(hash, { create: true });
    const writable = await fileHandle.createWritable();
    await writable.write(bytes);
    await writable.close();
    // Re-read and verify. The object is immutable once published; a mismatch
    // means storage corruption and the manifest must not advance.
    const file = await fileHandle.getFile();
    const stored = new Uint8Array(await file.arrayBuffer());
    const verified =
        stored.length === bytes.length && (await sha256Hex(stored)) === hash;
    return { hash, byteLength: stored.length, verified };
}

export async function readContent(hash) {
    const dir = await getContentDir();
    const fileHandle = await dir.getFileHandle(hash);
    const file = await fileHandle.getFile();
    return new Uint8Array(await file.arrayBuffer());
}

export async function verifyContent(hash, expectedLength) {
    const dir = await getContentDir();
    let fileHandle;
    try {
        fileHandle = await dir.getFileHandle(hash);
    } catch {
        return { exists: false, length: 0, hashMatch: false };
    }
    const file = await fileHandle.getFile();
    const buf = new Uint8Array(await file.arrayBuffer());
    return {
        exists: true,
        length: buf.length,
        hashMatch:
            expectedLength === undefined
                ? (await sha256Hex(buf)) === hash
                : buf.length === expectedLength &&
                  (await sha256Hex(buf)) === hash,
    };
}

export async function contentExists(hash) {
    const dir = await getContentDir();
    try {
        await dir.getFileHandle(hash);
        return true;
    } catch {
        return false;
    }
}

// ---------- IndexedDB ----------

function openDb() {
    if (dbPromise) return dbPromise;
    dbPromise = new Promise((resolve, reject) => {
        if (!self.indexedDB) {
            reject(new Error("IndexedDB is not available in this browser."));
            return;
        }
        const req = indexedDB.open(DB_NAME, DB_VERSION);
        req.onupgradeneeded = () => {
            const db = req.result;
            if (!db.objectStoreNames.contains(STORE_PROJECTS)) {
                db.createObjectStore(STORE_PROJECTS, { keyPath: "id" });
            }
            if (!db.objectStoreNames.contains(STORE_REVISIONS)) {
                const s = db.createObjectStore(STORE_REVISIONS, {
                    keyPath: "id",
                });
                s.createIndex("projectId", "projectId", { unique: false });
            }
            if (!db.objectStoreNames.contains(STORE_CHECKPOINTS)) {
                const s = db.createObjectStore(STORE_CHECKPOINTS, {
                    keyPath: "id",
                });
                s.createIndex("projectId", "projectId", { unique: false });
            }
        };
        req.onsuccess = () => resolve(req.result);
        req.onerror = () =>
            reject(req.error || new Error("IndexedDB open failed"));
    });
    return dbPromise;
}

function txStore(db, store, mode) {
    return db.transaction(store, mode).objectStore(store);
}

function reqAsPromise(request) {
    return new Promise((resolve, reject) => {
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
    });
}

// ---- projects ----

export async function putProject(project) {
    const db = await openDb();
    await reqAsPromise(txStore(db, STORE_PROJECTS, "readwrite").put(project));
}

export async function getProject(id) {
    const db = await openDb();
    return await reqAsPromise(txStore(db, STORE_PROJECTS, "readonly").get(id));
}

export async function listProjects() {
    const db = await openDb();
    return await reqAsPromise(txStore(db, STORE_PROJECTS, "readonly").getAll());
}

export async function deleteProject(id) {
    const db = await openDb();
    await reqAsPromise(txStore(db, STORE_PROJECTS, "readwrite").delete(id));
}

// Name-only compare/exchange. Name policy is C#; this is only browser lock/
// IndexedDB transport. Cancellation belongs to THIS acquired lock entry.
export function cancelProjectRename(lockId, projectId) {
    const entry = locks?.get(lockId);
    if (!entry || entry.name !== "oop-project:" + projectId) return false;
    entry.renameCancelled = true;
    return true;
}

export async function renameProjectName(id, expectedName, expectedHead, expectedSchema, name, lockId) {
    if (typeof id !== "string" || typeof expectedName !== "string" ||
        !(expectedHead === null || typeof expectedHead === "string") ||
        !Number.isSafeInteger(expectedSchema) || typeof name !== "string")
        return { status: "stale" };
    const entry = locks?.get(lockId);
    const sameLock = () => entry && locks.get(lockId) === entry && entry.name === "oop-project:" + id;
    if (!sameLock() || !(await ownsLock(lockId, "oop-project:" + id)))
        return { status: "writerUnavailable" };
    if (entry.renameCancelled) return { status: "cancelled" };
    const db = await openDb(); // All foreign awaits are BEFORE active transaction.
    if (!sameLock()) return { status: "writerUnavailable" };
    if (entry.renameCancelled) return { status: "cancelled" };
    return new Promise((resolve, reject) => {
        const transaction = db.transaction([STORE_PROJECTS], "readwrite");
        let status = "stale", failed = false;
        transaction.oncomplete = () => {
            // Completion is actual settlement, even if an error's default abort
            // was prevented elsewhere. A successful put then commit is durable;
            // never relabel it rollback. Otherwise surface a fixed failure.
            if (failed && status !== "applied") reject(new Error("LocalProjectRenameTransactionCompletedWithError"));
            else resolve({ status });
        };
        // Error bubbles BEFORE abort settlement. Latch only; do not prevent the
        // default abort or release the C# writer through premature rejection.
        transaction.onerror = () => { failed = true; };
        transaction.onabort = () => reject(new Error("LocalProjectRenameTransactionAborted"));
        try {
            const store = transaction.objectStore(STORE_PROJECTS);
            const read = store.get(id);
            read.onsuccess = () => {
                try {
                    // Synchronous callback: no await/task can make this transaction idle.
                    if (!sameLock()) { status = "writerUnavailable"; return; }
                    if (entry.renameCancelled) { status = "cancelled"; return; }
                    const current = read.result;
                    if (!current) { status = "missing"; return; }
                    if (current.id !== id || current.name !== expectedName ||
                        (current.headRevisionId ?? null) !== expectedHead || current.schemaVersion !== expectedSchema)
                        return;
                    if (current.name === name) { status = "unchanged"; return; }
                    // Preserve every LIVE metadata field, including future fields;
                    // only name changes. No revision/checkpoint/OPFS/config writes.
                    const write = store.put({ ...current, name });
                    write.onsuccess = () => { status = "applied"; }; // NOT promise settlement.
                } catch {
                    failed = true;
                    try { transaction.abort(); } catch { /* Already terminal/pending: await its actual terminal event. */ }
                }
                // Late cancellation cannot claim rollback of a completed transaction.
            };
        } catch {
            failed = true;
            // Includes synchronous objectStore/get creation failure. Handlers are
            // already installed; only actual abort/complete settles this promise.
            try { transaction.abort(); } catch { /* Await the actual terminal event. */ }
        }
    });
}

// ---- revisions ----

export async function putRevision(manifest) {
    const db = await openDb();
    await reqAsPromise(txStore(db, STORE_REVISIONS, "readwrite").put(manifest));
}

export async function getRevision(id) {
    const db = await openDb();
    return await reqAsPromise(txStore(db, STORE_REVISIONS, "readonly").get(id));
}

// ---- checkpoints ----

export async function putCheckpoint(checkpoint) {
    const db = await openDb();
    await reqAsPromise(
        txStore(db, STORE_CHECKPOINTS, "readwrite").put(checkpoint),
    );
}

export async function listCheckpoints(projectId) {
    const db = await openDb();
    const idx = txStore(db, STORE_CHECKPOINTS, "readonly").index("projectId");
    return await reqAsPromise(idx.getAll(projectId));
}

// ---- atomic commit batch ----
// Stores pending ancestor manifests + the new manifest, optionally a
// checkpoint, and advances the project head only if its current head matches
// expectedHead. One readwrite transaction; on CAS mismatch the transaction
// is aborted and { ok: false, actualHead } is returned without side effects.
export async function commitBatch(
    projectId,
    expectedHead,
    newHeadRevisionId,
    manifests,
    checkpoint,
    injectFailure,
) {
    const db = await openDb();
    return new Promise((resolve, reject) => {
        const stores = [STORE_PROJECTS, STORE_REVISIONS, STORE_CHECKPOINTS];
        const transaction = db.transaction(stores, "readwrite");
        const projectStore = transaction.objectStore(STORE_PROJECTS);
        const revisionStore = transaction.objectStore(STORE_REVISIONS);
        const checkpointStore = transaction.objectStore(STORE_CHECKPOINTS);

        let actualHead = undefined;
        let failed = false;

        transaction.oncomplete = () => {
            if (failed) return; // resolved in onabort path
            resolve({ ok: true, actualHead: newHeadRevisionId });
        };
        transaction.onerror = () => reject(transaction.error);
        transaction.onabort = () =>
            reject(
                transaction.error || new Error("commit transaction aborted"),
            );

        const projectReq = projectStore.get(projectId);
        projectReq.onsuccess = () => {
            const project = projectReq.result;
            if (!project) {
                failed = true;
                transaction.abort();
                resolve({ ok: false, errorCode: "ProjectMissing" });
                return;
            }
            actualHead = project.headRevisionId ?? null;
            const matches =
                (expectedHead === null && actualHead === null) ||
                (expectedHead !== null &&
                    actualHead !== null &&
                    expectedHead === actualHead);
            if (!matches) {
                failed = true;
                transaction.abort();
                resolve({
                    ok: false,
                    errorCode: "RevisionConflict",
                    actualHead,
                });
                return;
            }
            // Store ancestor manifests first, then the selected head manifest.
            for (const m of manifests) {
                revisionStore.put(m);
            }
            // Advance the head inside the same transaction.
            project.headRevisionId = newHeadRevisionId;
            projectStore.put(project);
            if (checkpoint) {
                checkpointStore.put(checkpoint);
            }
            // Optional failure injection for the test suite: abort right before
            // the transaction would complete, simulating a head-transaction
            // failure. No head advance is committed.
            if (injectFailure === "beforeHeadComplete") {
                failed = true;
                transaction.abort();
                resolve({ ok: false, errorCode: "InjectedFailure" });
                return;
            }
        };
    });
}

// Recovery: replace a damaged head with a verified checkpoint revision, using
// CAS so concurrent changes are detected. The damaged revision manifest is
// retained (never deleted).
export async function repairHead(
    projectId,
    expectedCurrentHead,
    newHeadRevisionId,
) {
    return commitBatch(
        projectId,
        expectedCurrentHead,
        newHeadRevisionId,
        [],
        null,
        null,
    );
}

// Atomically publish a brand-new project from a validated staged import: store
// the project record, the initial revision manifest and an import checkpoint,
// and set the head — all in one readwrite transaction. A failure or abort leaves
// no project row, so the project list never shows an empty/partial import. There
// is no expectedHead CAS (the project is new). Content objects were already
// written and verified by the caller before this runs.
export async function publishImport(project, revision, checkpoint) {
    const db = await openDb();
    return new Promise((resolve) => {
        const stores = [STORE_PROJECTS, STORE_REVISIONS, STORE_CHECKPOINTS];
        const transaction = db.transaction(stores, "readwrite");
        const projectStore = transaction.objectStore(STORE_PROJECTS);
        const revisionStore = transaction.objectStore(STORE_REVISIONS);
        const checkpointStore = transaction.objectStore(STORE_CHECKPOINTS);

        transaction.oncomplete = () => {
            resolve({ published: true });
        };
        transaction.onerror = () =>
            resolve({
                published: false,
                errorCode: "StorageFailure",
                safeMessage: String(
                    transaction.error || "publish transaction failed",
                ),
            });
        transaction.onabort = () =>
            resolve({
                published: false,
                errorCode: "StorageFailure",
                safeMessage: String(
                    transaction.error || "publish transaction aborted",
                ),
            });

        // Order: manifest + checkpoint first, then the project row last so that a
        // failure before the project put never leaves a headless project row.
        revisionStore.put(revision);
        checkpointStore.put(checkpoint);
        project.headRevisionId = revision.id;
        projectStore.put(project);
    });
}

// ---------- Web Locks ----------

function ensureLocks() {
    if (locks) return locks;
    if (!navigator.locks || typeof navigator.locks.request !== "function") {
        throw new Error("Web Locks API is not available in this browser.");
    }
    locks = new Map();
    return locks;
}

// Try to acquire an exclusive lock without waiting. Returns a lockId or null.
export async function tryAcquireLock(name) {
    const held = ensureLocks();
    return new Promise((resolve) => {
        let lockId = null;
        let released;
        const heldPromise = new Promise((r) => {
            released = r;
        });
        navigator.locks
            .request(name, { mode: "exclusive", ifAvailable: true }, (lock) => {
                if (!lock) {
                    resolve(null);
                    return null;
                }
                lockId = "lock" + ++lockSeq;
                held.set(lockId, { name, released });
                resolve(lockId);
                return heldPromise; // hold until released
            })
            .catch(() => {
                // A browser-aborted/stolen request no longer owns its entry.
                if (lockId !== null) held.delete(lockId);
                resolve(null);
            });
        // If lock unavailable, navigator.locks.request resolves the callback
        // synchronously with null; the outer resolve(null) fires immediately.
    });
}

// Wait for the lock to become available (takeover). Resolves with lockId once
// acquired, not when the lock is later released.
export async function waitForLock(name) {
    const held = ensureLocks();
    let released;
    const heldPromise = new Promise((r) => {
        released = r;
    });
    const lockId = "lock" + ++lockSeq;
    return new Promise((resolve, reject) => {
        navigator.locks
            .request(name, { mode: "exclusive" }, () => {
                held.set(lockId, { name, released });
                resolve(lockId); // acquired: resolve the outer promise now
                return heldPromise; // hold the lock until released
            })
            .catch(() => {
                held.delete(lockId);
                reject(new Error("Writer lock request was interrupted."));
            });
    });
}

export function releaseLock(lockId) {
    const held = ensureLocks();
    const entry = held.get(lockId);
    if (!entry) return false;
    held.delete(lockId);
    entry.released(); // let the navigator.locks.request callback return
    return true;
}

// Browser API glue only: a lock held by another client is NOT ownership.
// Check the module's acquired entry before AND after the asynchronous query.
export async function ownsLock(lockId, name) {
    if (
        !locks ||
        !navigator.locks ||
        typeof navigator.locks.query !== "function"
    )
        return false;
    const entry = locks.get(lockId);
    if (!entry || entry.name !== name) return false;
    try {
        const snapshot = await navigator.locks.query();
        return (
            locks.get(lockId) === entry &&
            (snapshot.held || []).some(
                (l) => l.name === name && l.mode === "exclusive",
            )
        );
    } catch {
        return false;
    }
}

export async function lockHeld(name) {
    if (!navigator.locks || typeof navigator.locks.query !== "function")
        return false;
    try {
        const snapshot = await navigator.locks.query();
        const held = (snapshot.held || []).find(
            (l) => l.name === name && l.mode === "exclusive",
        );
        return !!held;
    } catch {
        return false;
    }
}

// Test helper: clear all workspace storage (OPFS content dir + IndexedDB DB).
// Used by the browser test suite between scenarios; never called from the app.
export async function clearAll() {
    if (self.indexedDB) {
        try {
            await new Promise((resolve) => {
                const req = indexedDB.deleteDatabase(DB_NAME);
                req.onsuccess = () => resolve();
                req.onerror = () => resolve();
                req.onblocked = () => resolve();
            });
        } catch {
            /* ignore */
        }
        dbPromise = null;
    }
    if (
        navigator.storage &&
        typeof navigator.storage.getDirectory === "function"
    ) {
        try {
            const root = await navigator.storage.getDirectory();
            await root
                .removeEntry(CONTENT_DIR, { recursive: true })
                .catch(() => {});
            contentDir = null;
        } catch {
            /* ignore */
        }
    }
}
