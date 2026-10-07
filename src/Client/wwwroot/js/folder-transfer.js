// Folder transport glue. Pure browser-API transport for directory import and
// export. C# owns all path/size/count/depth validation and the staged→publish
// workflow; this module only enumerates browser-granted handles, reads bytes
// and writes bytes through writable streams. Folder handles stay session-local:
// they are never persisted or reused for writes across sessions.
//
// Picker invocation: the C# side invokes these from a user-gesture handler. In
// real desktop Chrome/Edge the activation carries through the first await for
// showDirectoryPicker. Native chooser automation is not available in headless
// Playwright, so callers that need to exercise the real OS picker must use a
// manual test (recorded separately); the error/permission paths are covered
// here without a real picker.

const FORBIDDEN = new Set([".git", ".svn", ".hg", ".ssh", ".aws"]);

// Session-local registry of browser file handles. Native FileSystemFileHandle
// objects cannot be returned inside a plain DTO and then passed back through
// JS interop (Blazor requires a tracked IJSObjectReference with __jsObjectId for
// that, which nested fields do not get). So enumerateTree registers each handle
// here under an opaque integer id and returns only { path, handleId }; the C#
// side transports the integer id and readFileHandleById(id) looks it up. The
// registry is cleared after the import is consumed.
const handleRegistry = new Map();
let handleSeq = 0;

export function folderCapabilities() {
    const picker =
        typeof window !== "undefined" &&
        typeof window.showDirectoryPicker === "function";
    let writable = false;
    try {
        // Feature-detect createWritable via a FileSystemFileHandle prototype
        // check (does not require an actual handle).
        writable =
            picker &&
            typeof FileSystemFileHandle !== "undefined" &&
            typeof FileSystemFileHandle.prototype.createWritable === "function";
    } catch {
        writable = false;
    }
    return { picker, writable };
}

// Pick a directory. Must be called from a user gesture. Returns a handle the
// C# side keeps session-local. Throws AbortError on cancel; SecurityError /
// NotAllowedError on denial — the C# side maps these to a recoverable outcome.
export async function pickDirectory() {
    if (!folderCapabilities().picker) {
        const err = new Error(
            "Directory picker is unavailable in this browser.",
        );
        err.code = "PickerUnavailable";
        throw err;
    }
    return await window.showDirectoryPicker({ mode: "readwrite" });
}

// Enumerate a directory handle into project-relative entries. Traverses only
// browser-granted handles (no host OS APIs). Skips source-control/credential
// trees and records them as exclusions. Enforces entry count, depth and
// per-file size limits during traversal so a malicious tree cannot exhaust
// memory before C# validation runs. Symlink detection is not claimed where the
// browser does not expose it.
export async function enumerateTree(rootHandle, limits) {
    const files = [];
    const excludedTrees = [];
    const maxEntries = limits?.maxEntries ?? 10000;
    const maxDepth = limits?.maxDepth ?? 32;
    const maxSegment = limits?.maxSegmentBytes ?? 255;
    const singleFile = limits?.singleFile ?? 32 * 1024 * 1024;

    async function walk(handle, prefix, depth) {
        if (files.length > maxEntries) {
            const err = new Error("Too many entries in the selected folder.");
            err.code = "TooManyEntries";
            throw err;
        }
        if (depth > maxDepth) {
            const err = new Error("Folder depth exceeds the limit.");
            err.code = "DepthExceeded";
            throw err;
        }
        for await (const entry of handle.values()) {
            const name = entry.name;
            if (FORBIDDEN.has(name)) {
                excludedTrees.push((prefix ? prefix + "/" : "") + name);
                continue;
            }
            const segBytes = new TextEncoder().encode(name).length;
            if (segBytes > maxSegment) {
                const err = new Error(
                    `Segment too long: ${(prefix ? prefix + "/" : "") + name}`,
                );
                err.code = "SegmentTooLong";
                throw err;
            }
            const rel = prefix ? prefix + "/" + name : name;
            if (entry.kind === "directory") {
                await walk(entry, rel, depth + 1);
            } else if (entry.kind === "file") {
                const size = await entry.getFile().then((f) => f.size);
                if (size > singleFile) {
                    const err = new Error(
                        `File exceeds single-file limit: ${rel}`,
                    );
                    err.code = "FileTooLarge";
                    throw err;
                }
                const id = ++handleSeq;
                handleRegistry.set(id, entry);
                files.push({ path: rel, handleId: id });
            }
        }
    }

    await walk(rootHandle, "", 1);
    return { files, excludedTrees };
}

// Read a file's exact bytes by its opaque handle id (registered during
// enumeration). Drops the handle from the registry after reading.
export async function readFileHandleById(handleId) {
    const handle = handleRegistry.get(handleId);
    if (!handle) throw new Error(`Unknown file handle id: ${handleId}`);
    const file = await handle.getFile();
    const bytes = new Uint8Array(await file.arrayBuffer());
    handleRegistry.delete(handleId);
    return bytes;
}

// Release any remaining registered handles (e.g. after a cancelled import).
export function releaseHandles(handleIds) {
    for (const id of handleIds || []) handleRegistry.delete(id);
    return true;
}

// Read a file handle's exact bytes (kept for direct callers that already hold a
// tracked IJSObjectReference, not used by folder import staging).
export async function readFileHandle(handle) {
    const file = await handle.getFile();
    return new Uint8Array(await file.arrayBuffer());
}

// Export an immutable revision's files into a NEW child directory under the
// selected parent, named overclone-export-<UTC yyyyMMddTHHmmssfffZ>-<8 hex>.
// Checks for an existing name and generates another; never overwrites an
// existing tree. Writes exact bytes and closes every writer. Browser writes are
// not an atomic multi-file transaction: on interruption the partial directory
// is retained and the result reports completed files and the directory name.
export async function exportToDirectory(parentHandle, entries) {
    const cap = folderCapabilities();
    if (!cap.writable) {
        const err = new Error("Writable directory access is unavailable.");
        err.code = "WritableUnavailable";
        throw err;
    }

    let name = makeExportName();
    while (await existsChild(parentHandle, name)) {
        name = makeExportName();
    }
    const root = await parentHandle.getDirectoryHandle(name, { create: true });

    const completed = [];
    const failed = [];
    for (const e of entries) {
        try {
            const dir = await ensureDirectory(root, dirPart(e.path));
            const fileHandle = await dir.getFileHandle(baseName(e.path), {
                create: true,
            });
            const writable = await fileHandle.createWritable();
            try {
                // e.bytes is a Uint8Array produced by the C# caller via interop.
                await writable.write(e.bytes);
            } finally {
                await writable.close();
            }
            completed.push(e.path);
        } catch (error) {
            failed.push({ path: e.path, error: String(error) });
        }
    }
    return {
        directoryName: name,
        completed,
        failed,
        complete: failed.length === 0,
    };
}

function makeExportName() {
    const ts = new Date()
        .toISOString()
        .replace(/[-:]/g, "")
        .replace(/\.\d+Z$/, "Z");
    const hex = Array.from(crypto.getRandomValues(new Uint8Array(4)))
        .map((b) => b.toString(16).padStart(2, "0"))
        .join("");
    return `overclone-export-${ts}-${hex}`;
}

async function existsChild(parentHandle, name) {
    try {
        await parentHandle.getDirectoryHandle(name);
        return true;
    } catch {
        return false;
    }
}

async function ensureDirectory(root, relPath) {
    if (!relPath) return root;
    let dir = root;
    for (const part of relPath.split("/")) {
        if (!part) continue;
        dir = await dir.getDirectoryHandle(part, { create: true });
    }
    return dir;
}

function dirPart(path) {
    const i = path.lastIndexOf("/");
    return i < 0 ? "" : path.substring(0, i);
}

function baseName(path) {
    const i = path.lastIndexOf("/");
    return i < 0 ? path : path.substring(i + 1);
}

// Release a directory handle reference held by the C# side. Handles are GC'd;
// this only clears the JS-side reference so it is not reused for writes.
export function releaseHandle(_handleRef) {
    // No-op structurally: handles are not persistable across sessions and
    // are dropped when the JS reference is released. The parameter is kept
    // for explicit lifecycle symmetry with the C# side caller.
    return true;
}
