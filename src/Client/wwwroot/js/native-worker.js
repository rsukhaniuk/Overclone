// A regular module worker owns the Emscripten instance; no shared memory needed.
const engine = import("../native-generated/engine.mjs")
    .then(({ default: createEngine }) => createEngine());

self.onmessage = async ({ data }) => {
    try {
        const module = await engine;
        let result;
        if (data.type === "ready") result = true;
        else if (data.type === "countWords") {
            result = module.ccall("count_words", "number", ["string"], [data.text]);
        } else throw new Error(`Unknown operation: ${data.type}`);
        self.postMessage({ id: data.id, result });
    } catch (error) {
        self.postMessage({ id: data.id, error: String(error.message ?? error) });
    }
};
