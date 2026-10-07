// Each language gets an independent worker and request queue.
export function createWorkerBridge(url, language) {
    let worker;
    let initialization;
    let sequence = 0;
    const pending = new Map();

    function failAll(error) {
        for (const request of pending.values()) {
            clearTimeout(request.timer);
            request.reject(error);
        }
        pending.clear();
    }

    function request(type, text) {
        const id = ++sequence;
        return new Promise((resolve, reject) => {
            const timer = setTimeout(() => {
                pending.delete(id);
                reject(new Error(`${language} worker request timed out.`));
            }, 30000);
            pending.set(id, { resolve, reject, timer });
            worker.postMessage({ id, type, text });
        });
    }

    function ready() {
        if (initialization) return initialization;
        worker = new Worker(url, { type: "module" });
        worker.onmessage = ({ data }) => {
            const request = pending.get(data.id);
            if (!request) return;
            clearTimeout(request.timer);
            pending.delete(data.id);
            if (data.error) request.reject(new Error(data.error));
            else request.resolve(data.result);
        };
        worker.onerror = () => failAll(new Error(`${language} worker failed. Check the browser console and build logs.`));
        initialization = request("ready");
        return initialization;
    }

    async function countWords(text) {
        await ready();
        return request("countWords", text);
    }

    function dispose() {
        worker?.terminate();
        failAll(new Error(`${language} worker was closed.`));
        worker = undefined;
        initialization = undefined;
    }

    return { ready, countWords, dispose };
}
