// Fixed CLASSIC worker entry admitted by the production service worker.
// No project filesystem, host mount, fetch, parser, policy or DOM coordinate conversion.
(() => {
    function validQueryFields(r) {
        const coordinate=v=>typeof v==='number'&&Number.isFinite(v)&&Math.abs(v)<=1000000;
        if(!r||r.version!==1||typeof r.requestId!=='string'||!/^[0-9a-f]{32}$/.test(r.requestId)
            ||!coordinate(r.h)||!coordinate(r.v)||!Number.isSafeInteger(r.page))return false;
        if(r.direction==='inverse')return r.file===null&&r.line===0&&r.page>=1&&r.page<=512;
        if(r.direction!=='forward'||!Number.isSafeInteger(r.line)||r.line<1||r.line>2147483647||r.page<0||r.page>512)return false;
        const f=r.file;
        return typeof f==='string'&&f.length>0&&f.length<=1024&&f.isWellFormed()
            &&new TextEncoder().encode(f).length<=1024&&!/[\u0000-\u001f\u007f-\u009f\u2028\u2029\\:]/.test(f)
            &&f.split('/').every(p=>p&&p!=='.'&&p!=='..');
    }
    const typedLength=value=>{
        try {
            if(!(value instanceof Uint8Array)||!ArrayBuffer.isView(value))return -1;
            return Object.getOwnPropertyDescriptor(Object.getPrototypeOf(Uint8Array.prototype),'byteLength').get.call(value);
        } catch { return -1; }
    };
    let used = false;
    self.onmessage = async ({ data }) => {
        if (used) return;
        used = true;
        const request = data?.request ?? {};
        let module = null, response;
        const allocations = new Set();
        const envelope = (status, nativeStatus = null, json = null) => ({
            version: 1, requestId: request.requestId, status, nativeStatus, json
        });
        try {
            const mapLength=typedLength(data.map),wasmLength=typedLength(data.wasm);
            if(!validQueryFields(request)||mapLength<1||mapLength>1048575||wasmLength<8||wasmLength>1048576)
                throw new Error('Invalid bounded query frame');
            // URL created from the verified provider bytes by this query's parent.
            // The parent revokes it on EVERY settlement, even forced termination.
            if (typeof data.loaderUrl !== 'string' || !data.loaderUrl.startsWith('blob:')) throw 0;
            importScripts(data.loaderUrl);
            if (typeof self.createSyncTeXQuery !== 'function') throw 0;
            // This pinned loader uses instantiateWasm rather than wasmBinary
            // for supplied bytes. Instantiate directly: no URL/fetch fallback.
            module = await new Promise((resolve, reject) => {
                try {
                    const loading = self.createSyncTeXQuery({
                        wasmBinary: data.wasm, print: () => {}, printErr: () => {},
                        instantiateWasm(imports, receive) {
                            WebAssembly.instantiate(data.wasm, imports).then(result => receive(result.instance)).catch(reject);
                            return {};
                        }
                    });
                    Promise.resolve(loading).then(resolve, reject);
                } catch (error) { reject(error); }
            });
            if (module._sq_version() !== 1) throw 0;
            const allocate = size => {
                const pointer = module._malloc(size);
                if (!Number.isInteger(pointer) || pointer <= 0 || pointer > module.HEAPU8.length - size) throw 0;
                allocations.add(pointer);
                return pointer;
            };
            const input = bytes => {
                const pointer = allocate(bytes.length);
                module.HEAPU8.set(bytes, pointer);
                return pointer;
            };
            let status = module._sq_load(input(data.map), data.map.length);
            if (status === 0) {
                if (request.direction === 'forward') {
                    const name = new TextEncoder().encode(request.file);
                    status = module._sq_forward(input(name), name.length, request.line, 0, request.page === 0 ? -1 : request.page);
                } else if (request.direction === 'inverse') {
                    status = module._sq_inverse(request.page, request.h, request.v);
                } else throw 0;
                if (status === 0) {
                    // Only this mechanical FFI byte bound belongs in JS. C# parses
                    // strict UTF-8, schema, counts, names, numbers and immutable results.
                    const size = module._sq_result_length();
                    if (!Number.isInteger(size) || size < 1 || size > 32768) throw 0;
                    const pointer = allocate(size);
                    const copied = module._sq_result_copy(pointer, size);
                    response = copied === 0
                        ? envelope('native', 0, module.HEAPU8.slice(pointer, pointer + size))
                        : envelope('native', copied);
                } else response = envelope('native', status);
            } else {
                // SQ_EMPTY belongs only to a query, never a failed load.
                response = status === 1 ? envelope('failed') : envelope('native', status);
            }
        } catch { response = envelope('failed'); }
        finally {
            if (module) {
                try { if (module._sq_close() !== 0) response = envelope('failed'); }
                catch { response = envelope('failed'); }
                for (const pointer of allocations) {
                    try { module._free(pointer); } catch { response = envelope('failed'); }
                }
            }
        }
        self.postMessage({ kind: 'terminal', response }, response.json ? [response.json.buffer] : []);
    };
})();
