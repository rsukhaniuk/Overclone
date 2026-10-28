// Minimal browser API bridge: wait for the immutable release's activated
// controller before reading its bundled files, avoiding a second first download.
const pending = new Set();
export function waitForRelease() {
    const sw = navigator.serviceWorker;
    if (!sw) return Promise.reject(new Error('Offline application support unavailable'));
    return new Promise((resolve, reject) => {
        let timer, watched;
        const cleanup = () => {
            clearTimeout(timer); sw.removeEventListener('controllerchange', changed);
            watched?.removeEventListener?.('statechange', changed); pending.delete(cancel);
        };
        const cancel = () => { cleanup(); reject(new Error('Bundled release preparation cancelled')); };
        const changed = () => {
            if (watched !== sw.controller) {
                watched?.removeEventListener?.('statechange', changed); watched = sw.controller;
                watched?.addEventListener?.('statechange', changed);
            }
            if (sw.controller?.state === 'activated' && sw.controller.scriptURL === location.origin + '/service-worker.js') {
                cleanup(); resolve();
            }
        };
        pending.add(cancel);
        sw.addEventListener('controllerchange', changed);
        timer = setTimeout(cancel, 600000);
        changed();
    });
}
export function cancelPending() { for (const cancel of [...pending]) cancel(); }
