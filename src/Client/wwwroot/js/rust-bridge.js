import { createWorkerBridge } from "./worker-bridge.js";

const bridge = createWorkerBridge(
    new URL("./rust-worker.js", import.meta.url),
    "Rust",
);

export const {
    ready,
    countWords,
    dispose,
    analyzeSource,
    cancelSource,
    getWorkerEpoch,
    isCurrentWorkerEpoch,
} = bridge;
