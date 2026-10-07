import { createWorkerBridge } from "./worker-bridge.js";

export const { ready, countWords, dispose } = createWorkerBridge(
    new URL("./rust-worker.js", import.meta.url), "Rust");
