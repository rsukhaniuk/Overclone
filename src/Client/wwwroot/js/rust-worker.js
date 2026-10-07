import init, { count_words } from "../rust-generated/document_engine.js";

const engine = init();

self.onmessage = async ({ data }) => {
    try {
        await engine;
        let result;
        if (data.type === "ready") result = true;
        else if (data.type === "countWords") result = count_words(data.text);
        else throw new Error(`Unknown operation: ${data.type}`);
        self.postMessage({ id: data.id, result });
    } catch (error) {
        self.postMessage({ id: data.id, error: String(error.message ?? error) });
    }
};
