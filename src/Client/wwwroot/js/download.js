// Minimal browser-API glue: trigger downloads. No domain logic; the filename
// and bytes/text are provided by C#. downloadBytes preserves exact bytes
// (used for binary assets and exact-byte text downloads).

export function downloadText(filename, text) {
    const blob = new Blob([text], { type: "text/plain;charset=utf-8" });
    triggerDownload(filename, blob);
}

export function downloadBytes(filename, bytes /* Uint8Array | number[] */) {
    const view = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes);
    const blob = new Blob([view], { type: "application/octet-stream" });
    triggerDownload(filename, blob);
}

// Download a ZIP archive produced by the C# ZipTransferService.
export function downloadZip(filename, bytes /* Uint8Array | number[] */) {
    const view = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes);
    const blob = new Blob([view], { type: "application/zip" });
    triggerDownload(filename, blob);
}

function triggerDownload(filename, blob) {
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(url);
}
