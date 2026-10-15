// Static worker implementation exported for CSP-compatible Blob provisioning.
// JS only loads caller-provisioned bytes and invokes/copies the actual C++ E1 ABI.
export function pdfWorkerMain() {
    let used = false;
    self.onmessage = async ({ data: d }) => {
        if (used) return;
        used = true;
        const request = d.request,
            requestId = request.requestId,
            documentSha256 = request.documentSha256,
            epoch = request.epoch;
        let m,
            entered = false,
            document = 0,
            page = 0,
            response;
        const calls = [],
            buffers = new Set();
        const envelope = (status) => ({
            version: 2,
            loaderSha256: request.loaderSha256,
            wasmSha256: request.wasmSha256,
            actionState: null,
            hasCapabilitiesApi: false,
            hasActionsApi: false,
            capabilitiesJson: null,
            actionsJson: null,
            requestId,
            documentSha256,
            epoch,
            status,
            nativeStatus: null,
            nativeError: 0,
            enteredNative: null,
            liveAfter: 0,
            pageCount: 0,
            factsJson: null,
            textJson: null,
            searchJson: null,
            raster: null,
            nativeCalls: calls,
        });
        try {
            const loaderUrl = URL.createObjectURL(
                new Blob([d.loader], { type: "text/javascript" }),
            );
            try {
                importScripts(loaderUrl);
                if (typeof self.E1 !== "function")
                    throw new Error("Runtime factory unavailable");
                m = await self.E1({
                    wasmBinary: d.wasm,
                    print: () => {},
                    printErr: () => {},
                });
            } finally {
                URL.revokeObjectURL(loaderUrl);
            }
            const call = (name, ...args) => {
                calls.push(name);
                return m["_" + name](...args);
            };
            const checked = (name, ...args) => {
                const status = call(name, ...args);
                if (status !== 0)
                    throw {
                        nativeStatus: status,
                        nativeError: m._e1_native_error(),
                    };
            };
            const input = (bytes) => {
                const h = m._e1_alloc(bytes.length);
                if (!h)
                    throw {
                        nativeStatus: m._e1_status(),
                        nativeError: m._e1_native_error(),
                    };
                buffers.add(h);
                const pointer = m._e1_data(h);
                if (!pointer) throw new Error("Input allocation unavailable");
                m.HEAPU8.set(bytes, pointer);
                return h;
            };
            const release = (h) => {
                if (h) {
                    if (m._e1_release(h) !== 0)
                        throw new Error("Buffer release failed");
                    buffers.delete(h);
                }
            };
            let outputBytes = 0;
            const take = () => {
                const h = m._e1_result();
                if (!h) throw new Error("Missing native output");
                buffers.add(h);
                const size = m._e1_size(h);
                if (!size || size > request.budget.outputBytes - outputBytes)
                    throw { outputLimit: true };
                outputBytes += size;
                const dest = input(new Uint8Array(size));
                checked("e1_copy", h, dest);
                const bytes = m.HEAPU8.slice(
                    m._e1_data(dest),
                    m._e1_data(dest) + size,
                );
                release(dest);
                release(h);
                return bytes;
            };
            if (call("e1_version") !== 1)
                throw new Error("Unsupported runtime ABI");
            const b = request.budget,
                rawBudget = new Uint8Array(36),
                view = new DataView(rawBudget.buffer);
            [
                1,
                36,
                b.inputBytes,
                b.pages,
                b.textUnits,
                b.searchHits,
                b.rasterPixels,
                b.outputBytes,
                b.liveHandles,
            ].forEach((value, i) => view.setUint32(i * 4, value, true));
            const pdf = input(d.document),
                budget = input(rawBudget);
            // This notification is before the PDF call. It is NOT blocking-call
            // preemption evidence; canceled/terminated bridge replies stay unknown.
            self.postMessage({
                kind: "before-pdf-entry",
                requestId,
                documentSha256,
                epoch,
            });
            entered = true;
            checked("e1_open", pdf, budget);
            document = m._e1_result();
            release(pdf);
            release(budget);
            const pages = call("e1_document_pages", document);
            if (!pages || m._e1_status() !== 0)
                throw new Error("Page count unavailable");
            checked("e1_page", document, request.pageIndex);
            page = m._e1_result();
            const decoder = new TextDecoder("utf-8", { fatal: true });
            checked("e1_facts", page);
            const factsJson = decoder.decode(take());
            checked("e1_text", page);
            const textJson = decoder.decode(take());
            let searchJson = "[]";
            if (request.search !== null) {
                const queryBytes = new Uint8Array(
                        (request.search.length + 1) * 2,
                    ),
                    queryView = new DataView(queryBytes.buffer);
                for (let i = 0; i < request.search.length; i++)
                    queryView.setUint16(
                        i * 2,
                        request.search.charCodeAt(i),
                        true,
                    );
                const query = input(queryBytes);
                checked("e1_search", page, query);
                searchJson = decoder.decode(take());
                release(query);
            }
            let raster = null;
            if (request.raster) {
                checked("e1_raster", page, request.width, request.height);
                raster = take();
            }
            let actionState = "not-requested", hasCapabilitiesApi = false, hasActionsApi = false, capabilitiesJson = null, actionsJson = null;
            if (request.inspectActions) {
                hasCapabilitiesApi = typeof m._e1_capabilities === "function";
                hasActionsApi = typeof m._e1_actions === "function";
                actionState = "unsupported";
                if (hasCapabilitiesApi && hasActionsApi) {
                    checked("e1_capabilities", page); capabilitiesJson = decoder.decode(take());
                    checked("e1_actions", page); actionsJson = decoder.decode(take());
                    actionState = "available";
                }
            }
            response = {
                ...envelope("completed"),
                actionState, hasCapabilitiesApi, hasActionsApi, capabilitiesJson, actionsJson,
                nativeStatus: 0,
                enteredNative: true,
                pageCount: pages,
                factsJson,
                textJson,
                searchJson,
                raster,
            };
        } catch (error) {
            response = envelope(
                error && Number.isInteger(error.nativeStatus) && entered
                    ? "native-failure"
                    : error?.outputLimit === true ? "output-limit" : "transport-error",
            );
            if (response.status === "output-limit") response.enteredNative = entered ? true : null;
            if (response.status === "native-failure") {
                response.nativeStatus = error.nativeStatus;
                response.nativeError = error.nativeError;
                response.enteredNative = true;
            }
        } finally {
            if (m) {
                if (page) m._e1_close(page);
                if (document) m._e1_close(document);
                for (const h of buffers) m._e1_release(h);
                calls.push("e1_reset");
                m._e1_reset();
                if (response) response.liveAfter = m._e1_live();
            }
        }
        if (response)
            self.postMessage(
                { kind: "terminal", response },
                response.raster ? [response.raster.buffer] : [],
            );
    };
}
