import { createWorkerBridge } from "./worker-bridge.js";

export const { ready, countWords, dispose } = createWorkerBridge(
    new URL("./native-worker.js", import.meta.url), "C++");
