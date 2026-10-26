// Mechanical byte/C-ABI glue only. C# chooses the engine, passes, resources,
// convergence and revision authority; the native converter owns its virtual IO.
(() => {
    const emit = self.postMessage.bind(self), encoder = new TextEncoder();
    let state = 'boot', module = null, loaderSha256 = null, wasmSha256 = null;
    const hash = async bytes => [...new Uint8Array(await crypto.subtle.digest('SHA-256', bytes))]
        .map(value => value.toString(16).padStart(2, '0')).join('');
    const path = name => typeof name === 'string' && name.length > 0 && name.isWellFormed()
        && encoder.encode(name).length <= 249 && !/[\u0000-\u001f\u007f-\u009f\\:|]/.test(name)
        && name.split('/').every(part => part && part !== '.' && part !== '..');
    const allocate = bytes => {
        const pointer = module._malloc(Math.max(1, bytes.length));
        if (!Number.isInteger(pointer) || pointer <= 0 || pointer > module.HEAPU8.length - Math.max(1, bytes.length)) throw 0;
        module.HEAPU8.set(bytes, pointer);
        return pointer;
    };
    const string = name => allocate(encoder.encode(name + '\0'));
    const copy = (pointer, length, maximum) => {
        if (!Number.isSafeInteger(length) || length < 0 || length > maximum || !Number.isSafeInteger(pointer)
            || pointer < 0 || length && pointer === 0 || pointer > module.HEAPU8.length - length) throw 0;
        return module.HEAPU8.slice(pointer, pointer + length);
    };
    self.onmessage = async ({ data }) => {
        if (state !== 'boot') return;
        state = 'loading';
        try {
            if (data.operation !== 'boot' || typeof data.loaderUrl !== 'string' || !data.loaderUrl.startsWith('blob:')
                || !(data.loader instanceof Uint8Array) || !(data.wasm instanceof Uint8Array)) throw 0;
            loaderSha256 = await hash(data.loader); wasmSha256 = await hash(data.wasm);
            self.fetch = () => Promise.reject(new Error('Converter network unavailable'));
            self.XMLHttpRequest = class { constructor() { throw new Error('Converter network unavailable'); } };
            importScripts(data.loaderUrl);
            if (typeof self.createOcDvi !== 'function') throw 0;
            module = await new Promise((resolve, reject) => {
                try {
                    Promise.resolve(self.createOcDvi({
                        wasmBinary: data.wasm, print: () => {}, printErr: () => {},
                        instantiateWasm(imports, receive) {
                            WebAssembly.instantiate(data.wasm, imports).then(result => receive(result.instance)).catch(reject);
                            return {};
                        }
                    })).then(resolve, reject);
                } catch (error) { reject(error); }
            });
            if (module._oc_dvi_abi_version() !== 1) throw 0;
            state = 'ready'; self.onmessage = execute; emit({ kind: 'ready' });
        } catch { state = 'done'; emit({ kind: 'boot-error', error: 'ConverterInit' }); }
    };
    async function execute({ data: frame }) {
        if (state !== 'ready') return;
        state = 'running';
        let entered = false, nativeReturn = null, elapsedMs = null, log = '', inputs = [], files = [], outcome = 'setup-failure';
        try {
            const tag = frame.tag, resources = frame.runtimeResourceNames;
            const suite = tag?.profile === 5, xe = suite && tag.version === 8 && tag.engineId === 'xelatex';
            const dvi = tag?.version === 7 && [4, 5].includes(tag.profile) && tag.engineId === 'latex-dvi';
            const prefix = '__oop_runtime__/', resourceName = name => suite ? name.slice(prefix.length) : name;
            if (!(dvi || xe) || tag.tool !== 'dvipdfmx'
                || frame.operation !== 'dvipdfmx' || !/^[0-9a-f]{64}$/.test(tag.runtimeIdentity)
                || !path(frame.mainEntry) || !path(frame.selectedRoot) || !/\.tex$/i.test(frame.selectedRoot)
                || !Array.isArray(frame.files) || frame.files.length < 1 || frame.files.length > 4096
                || !Array.isArray(resources) || resources.length > frame.files.length
                || !Array.isArray(frame.outputs) || frame.outputs.length !== 1
                || !Number.isSafeInteger(frame.maxLogBytes) || frame.maxLogBytes < 0 || frame.maxLogBytes > 262144
                || !Number.isSafeInteger(frame.maxOutputBytes) || frame.maxOutputBytes < 1 || frame.maxOutputBytes > 8388608) throw 0;
            const leaf = frame.selectedRoot.slice(frame.selectedRoot.lastIndexOf('/') + 1), job = leaf.slice(0, -4);
            if (frame.mainEntry !== job + (xe ? '.xdv' : '.dvi') || frame.outputs[0] !== job + '.pdf') throw 0;
            const resourceNames = new Set(resources), names = new Set();
            if (resourceNames.size !== resources.length || resources.some(name => !path(name)
                || suite && !name.startsWith(prefix) || !path(resourceName(name)) || resourceName(name).includes('/'))) throw 0;
            let projectBytes = 0, resourceBytes = 0;
            for (const file of frame.files) {
                if (!file || !path(file.name) || !(file.bytes instanceof Uint8Array) || names.has(file.name)
                    || file.bytes.length > 33554432) throw 0;
                names.add(file.name);
                if (suite && !resourceNames.has(file.name) && file.name.startsWith(prefix)) throw 0;
                if (resourceNames.has(file.name)) resourceBytes += file.bytes.length;
                else projectBytes += file.bytes.length;
                if (projectBytes > 8388608 || resourceBytes > 100663296) throw 0;
            }
            if (resources.some(name => !names.has(name)) || resourceNames.has(frame.mainEntry)
                || !frame.files.some(file => file.name === frame.mainEntry && file.bytes.length > 0)) throw 0;
            for (const file of frame.files) {
                const name = string(resourceNames.has(file.name) ? resourceName(file.name) : file.name);
                let bytes = 0;
                try {
                    bytes = allocate(file.bytes);
                    if (module._oc_dvi_add_file(name, bytes, file.bytes.length, resourceNames.has(file.name) ? 1 : 0) !== 0) throw 0;
                    inputs.push({ name: file.name, length: file.bytes.length, sha256: await hash(file.bytes) });
                } finally { if (bytes) module._free(bytes); module._free(name); }
            }
            const entry = string(frame.mainEntry);
            let status;
            try {
                emit({ kind: 'entered' }); entered = true;
                const start = performance.now(); status = module._oc_dvi_run(entry); elapsedMs = performance.now() - start;
            } finally { module._free(entry); }
            const returned = module._oc_dvi_native_return();
            if (!Number.isInteger(returned) || module._oc_dvi_status() !== status) throw 0;
            nativeReturn = returned === -1 ? null : returned;
            const rawLog = copy(module._oc_dvi_log_data(), module._oc_dvi_log_size(), 262144);
            const truncated = module._oc_dvi_log_truncated() !== 0 || rawLog.length > Math.floor(frame.maxLogBytes / 3);
            const marker = truncated ? '[Converter log truncated]\n' : '';
            const remaining = Math.max(0, frame.maxLogBytes - encoder.encode(marker).length);
            log = frame.maxLogBytes >= encoder.encode(marker).length ? marker : '';
            log += new TextDecoder().decode(rawLog.slice(0, Math.floor(remaining / 3)));
            outcome = status === 2 ? 'resource-limit' : nativeReturn === null ? 'protocol-failure' : 'native-return';
            if (status === 0 && nativeReturn === 0) {
                const pdf = copy(module._oc_dvi_pdf_data(), module._oc_dvi_pdf_size(), frame.maxOutputBytes);
                if (pdf.length < 5 || new TextDecoder().decode(pdf.subarray(0, 5)) !== '%PDF-') throw 0;
                files = [{ name: frame.outputs[0], bytes: pdf }];
            }
        } catch (error) { files = []; outcome = error instanceof WebAssembly.RuntimeError ? 'wasm-abort' : entered ? 'protocol-failure' : 'setup-failure'; }
        state = 'done';
        emit({ kind: 'done', reply: { tag: frame.tag, outcome, nativeReturn, entered, log, files, inputs, elapsedMs,
            heapBytes: module?.HEAPU8?.byteLength ?? null, wasmSha256, loaderSha256, sourceMapPayload: null } });
    }
})();
