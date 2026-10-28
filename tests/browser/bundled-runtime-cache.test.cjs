const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../../src/Client/wwwroot/js/bundled-runtime-cache.js'), 'utf8').replaceAll('export function ', 'function ');
function create(controller = null) {
    const listeners = new Set(), timers = new Set();
    const sw = { controller, addEventListener: (_, f) => listeners.add(f), removeEventListener: (_, f) => listeners.delete(f) };
    const c = vm.createContext({ navigator: { serviceWorker: sw }, location: { origin: 'https://app.test' },
        setTimeout(f) { timers.add(f); return f; }, clearTimeout(f) { timers.delete(f); } });
    vm.runInContext(source, c);
    return { c, sw, listeners, timers, change(value) { sw.controller = value; for (const f of [...listeners]) f(); } };
}
const active = { state: 'activated', scriptURL: 'https://app.test/service-worker.js' };
(async () => {
    const ready = create(active); await ready.c.waitForRelease();
    assert.equal(ready.listeners.size, 0); assert.equal(ready.timers.size, 0);
    const first = create(); let done = false;
    const loading = first.c.waitForRelease().then(() => { done = true; });
    await Promise.resolve(); assert.equal(done, false);
    first.change({ ...active, state: 'activating' }); await Promise.resolve(); assert.equal(done, false);
    first.change({ ...active, scriptURL: 'https://other.test/service-worker.js' }); await Promise.resolve(); assert.equal(done, false);
    first.change(active); await loading;
    assert.equal(first.listeners.size, 0); assert.equal(first.timers.size, 0);
    const stateListeners = new Set();
    const activating = { ...active, state: 'activating', addEventListener: (_, f) => stateListeners.add(f), removeEventListener: (_, f) => stateListeners.delete(f) };
    const claim = create(activating); const claimed = claim.c.waitForRelease();
    assert.equal(stateListeners.size, 1);
    activating.state = 'activated'; for (const f of [...stateListeners]) f();
    await claimed; assert.equal(stateListeners.size, 0); assert.equal(claim.listeners.size, 0);
    const cancelled = create(); const pending = cancelled.c.waitForRelease();
    cancelled.c.cancelPending(); await assert.rejects(pending, /cancelled/);
    assert.equal(cancelled.listeners.size, 0); assert.equal(cancelled.timers.size, 0);
    const timed = create(); const expired = timed.c.waitForRelease();
    [...timed.timers][0](); await assert.rejects(expired, /cancelled/);
    assert.equal(timed.listeners.size, 0); assert.equal(timed.timers.size, 0);
    console.log('Bundled release browser bridge PASS: activated same-origin controller, first-install wait, cancellation/timeout and listener cleanup.');
})().catch(error => { console.error(error); process.exitCode = 1; });
