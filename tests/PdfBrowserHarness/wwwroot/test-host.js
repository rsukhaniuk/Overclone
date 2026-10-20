// TEST-ONLY static asset warming and real Worker lifecycle observation.
// The actual product modules below are linked, not reimplemented.
import "./js/pdf-port-bridge.js";
const NativeWorker = window.Worker;
let created = 0, terminated = 0, active = 0, held = 0, holdNext = false, awaitingHeld = false;
let heldWaiters = [], idleWaiters = [];
const frames = [];
const notifyIdle = () => { if (active === 0) { for (const resolve of idleWaiters) resolve(); idleWaiters = []; } };
window.Worker = class extends NativeWorker {
    constructor(...args) {
        super(...args); created++; active++;
        let closed = false;
        const originalPost = this.postMessage.bind(this), originalTerminate = this.terminate.bind(this);
        this.postMessage = (...values) => {
            if (values[0]?.request) {
                if (frames.length >= 64) throw new Error("test worker frame bound");
                const r = values[0].request; frames.push({ requestId: r.requestId, documentSha256: r.documentSha256, epoch: r.epoch, loaderSha256: r.loaderSha256, wasmSha256: r.wasmSha256, heldBeforeDelivery: holdNext });
            }
            if (holdNext && values[0] && values[0].request) {
                holdNext = false; awaitingHeld = false; held++; for (const resolve of heldWaiters) resolve(); heldWaiters = [];
                // Actual Worker exists, but payload is deliberately NOT delivered.
                // This proves only pre-PDF-entry termination, never blocking preemption.
                return;
            }
            return originalPost(...values);
        };
        this.terminate = () => {
            if (!closed) { closed = true; terminated++; active--; originalTerminate(); notifyIdle(); }
        };
    }
};
function boundedWait(kind) {
    if (kind === "idle" && active === 0 || kind === "held" && !awaitingHeld && held > 0) return Promise.resolve();
    return new Promise((resolve, reject) => {
        const timer = setTimeout(() => reject(new Error("test lifecycle barrier deadline")), 10000);
        const done = () => { clearTimeout(timer); resolve(); };
        if (kind === "held") heldWaiters.push(done); else idleWaiters.push(done);
    });
}
export function armHold() { holdNext = true; awaitingHeld = true; }
export function show(text) { document.getElementById("observed").textContent = text; }
export function waitHeld() { return boundedWait("held"); }
export function waitIdle() { return boundedWait("idle"); }
export function metrics() { return { created, terminated, active, held, frames: frames.map((frame) => ({ ...frame })) }; }
export function draw(bytes, width, height) {
    const canvas = document.getElementById("raster"); canvas.width = width; canvas.height = height;
    if (!width || !height) return;
    const rgba = new Uint8ClampedArray(bytes.length);
    for (let i = 0; i < bytes.length; i += 4) { rgba[i] = bytes[i + 2]; rgba[i + 1] = bytes[i + 1]; rgba[i + 2] = bytes[i]; rgba[i + 3] = bytes[i + 3]; }
    canvas.getContext("2d").putImageData(new ImageData(rgba, width, height), 0, 0);
}
