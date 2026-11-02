// Mechanical browser/WASM/VFS glue. C# selects resources, passes, bibliography,
// conversion and revision authority; native engines enforce their own IO limits.
(() => {
    const emit = self.postMessage.bind(self), encoder = new TextEncoder();
    let state = 'boot', module, engine, loaderSha256, wasmSha256, nativeAbi, xeMapModule = false;
    let log = '', logBytes = 0, maxLog = 262144, truncated = false;
    const hash = async bytes => [...new Uint8Array(await crypto.subtle.digest('SHA-256', bytes))]
        .map(value => value.toString(16).padStart(2, '0')).join('');
    const path = name => typeof name === 'string' && name.length > 0 && name.isWellFormed()
        && encoder.encode(name).length <= 249 && !/[\u0000-\u001f\u007f-\u009f\\:|]/.test(name)
        && name.split('/').length <= 16 && name.split('/').every(part => part && part !== '.' && part !== '..');
    const print = value => {
        const text = String(value) + '\n', size = encoder.encode(text).length;
        if (size <= maxLog - logBytes) { log += text; logBytes += size; }
        else truncated = true;
    };
    const prefix = '__oop_runtime__/';
    const lua = (name, types = [], values = []) => module.ccall('oc_luatex_' + name, 'number', types, values);
    self.onmessage = async ({ data }) => {
        if (state !== 'boot') return;
        state = 'loading';
        try {
            if (data.operation !== 'boot' || typeof data.loaderUrl !== 'string' || !data.loaderUrl.startsWith('blob:')
                || !(data.loader instanceof Uint8Array) || !(data.wasm instanceof Uint8Array)) throw 0;
            loaderSha256 = await hash(data.loader); wasmSha256 = await hash(data.wasm);
            self.fetch = () => Promise.reject(new Error('Compiler network unavailable'));
            self.XMLHttpRequest = class { constructor() { throw new Error('Compiler network unavailable'); } };
            importScripts(data.loaderUrl);
            const oldXe = typeof self.createXeTeX === 'function', lu = typeof self.createLuaTeX === 'function';
            xeMapModule = typeof self.createXeMaps === 'function';
            if (Number(oldXe) + Number(xeMapModule) + Number(lu) !== 1) throw 0;
            const xe = oldXe || xeMapModule;
            engine = xe ? 'xelatex' : 'lualatex';
            module = await new Promise((resolve, reject) => {
                try {
                    Promise.resolve((xeMapModule ? self.createXeMaps : xe ? self.createXeTeX : self.createLuaTeX)({
                        noInitialRun: true, wasmBinary: data.wasm, print, printErr: print,
                        instantiateWasm(imports, receive) {
                            WebAssembly.instantiate(data.wasm, imports).then(result => receive(result.instance)).catch(reject);
                            return {};
                        }
                    })).then(resolve, reject);
                } catch (error) { reject(error); }
            });
            if (!xe) {
                nativeAbi = lua('abi');
                if (![2, 3, 5].includes(nativeAbi)) throw 0;
            }
            state = 'ready'; self.onmessage = execute; emit({ kind: 'ready' });
        } catch { state = 'done'; emit({ kind: 'boot-error', error: 'CompilerInit' }); }
    };
    async function execute({ data: frame }) {
        if (state !== 'ready') return;
        state = 'running';
        let entered = false, nativeReturn = null, elapsedMs = null, files = [], inputs = [], outcome = 'setup-failure';
        let sourceMapPayload = null, sourceMapGeometryPayload = null;
        try {
            const xe = engine === 'xelatex', tag = frame.tag;
            const graphics = tag?.profile === 10;
            const xeNavigation = [9, 10].includes(tag?.profile) && xe;
            const navigation = xeNavigation || [8, 9, 10].includes(tag?.profile) && !xe;
            if (xe && xeMapModule !== xeNavigation
                || tag?.version !== (graphics ? xe ? 16 : 17 : xe ? xeNavigation ? 13 : 8 : navigation ? 14 : 9) || ![5, 6, 7, 8, 9, 10].includes(tag.profile) || tag.engineId !== engine || tag.tool !== 'latex'
                || !xe && nativeAbi !== (navigation ? 5 : tag.profile === 7 ? 3 : 2)
                || xe && tag.profile === 8 && (loaderSha256 !== '4ca80d7ed06d550632766dc3629f394f7f41b586d96682e98b87ca8fe8c72255'
                    || wasmSha256 !== 'b02ff092694071320785486618aa0de7df2d12fae7d3e92c5a32c5cda5980243')
                || graphics && !xe && (typeof module._oc_lua_graphics_abi !== 'function' || module._oc_lua_graphics_abi() !== 1)
                || navigation && (loaderSha256 !== (xeNavigation ? 'a2d90034e871d75d48596b26a5856ac6eed86b6396bd563e610b0ffa7a6be21c' : graphics ? '6fb762c18ea4719dd2157d5950303a5f498ad511df0665cca54af141ff0882b5' : '7282eb2decde52603417b94d363a6d3258e8ef1451a0d20847bee70084c70a21')
                    || wasmSha256 !== (xeNavigation ? '61ef6fe76724c7bd9532f05224d03829e40393c61acd68da0fd0589d8e60c7c2' : graphics ? '220999882c2ddb4057b6d798f21785fce35accb4c63c0436dfa62af9f3987dbc' : '1f5b9ca7742693299f1074c47e2a3b91d1df2d6c0381d5c4d1a628965cde542e')
                    || !Number.isSafeInteger(tag.ordinal) || tag.ordinal < 1 || tag.ordinal > 4294967295
                    || ['_oc_mem_configure','_oc_synctex_abi','_oc_synctex_status','_oc_synctex_length','_oc_synctex_copy',
                        ...(xeNavigation ? ['_oc_xe_maps_abi'] : ['_oc_synctex_geometry_length','_oc_synctex_geometry_copy'])].some(n => typeof module[n] !== 'function')
                    || module._oc_synctex_abi() !== 65536 || xeNavigation && module._oc_xe_maps_abi() !== 1)
                || frame.operation !== 'latex' || !/^[0-9a-f]{64}$/.test(tag.runtimeIdentity)
                || !path(frame.selectedRoot) || !/\.tex$/i.test(frame.selectedRoot) || frame.mainEntry !== frame.selectedRoot
                || !Array.isArray(frame.files) || frame.files.length < 1 || frame.files.length > 8384
                || !Array.isArray(frame.runtimeResourceNames) || !Array.isArray(frame.generatedInputNames)
                || !Array.isArray(frame.outputs) || frame.outputs.length < 1 || frame.outputs.length > 128
                || !Number.isSafeInteger(frame.maxLogBytes) || frame.maxLogBytes < 0 || frame.maxLogBytes > 262144
                || !Number.isSafeInteger(frame.maxOutputBytes) || frame.maxOutputBytes < 1 || frame.maxOutputBytes > 8388608) throw 0;
            const resources = new Set(frame.runtimeResourceNames), generated = new Set(frame.generatedInputNames), names = new Set();
            if (resources.size !== frame.runtimeResourceNames.length || generated.size !== frame.generatedInputNames.length
                || resources.size > (xe ? 8192 : 4608) || generated.size > 128
                || [...resources, ...generated].some(name => !path(name))
                || [...resources].some(name => !name.startsWith(prefix) || !path(name.slice(prefix.length)))
                || [...generated].some(name => resources.has(name))) throw 0;
            let resourceBytes = 0, projectBytes = 0, workBytes = 0, capturedCount = 0;
            for (const file of frame.files) {
                if (!file || !path(file.name) || !(file.bytes instanceof Uint8Array) || names.has(file.name)) throw 0;
                names.add(file.name);
                if (!resources.has(file.name) && file.name.startsWith(prefix)) throw 0;
                if (resources.has(file.name)) {
                    if (file.bytes.length > (xe ? 67108864 : 33554432)) throw 0;
                    resourceBytes += file.bytes.length;
                } else {
                    if (file.bytes.length > 8388608) throw 0;
                    workBytes += file.bytes.length;
                    if (!generated.has(file.name)) { projectBytes += file.bytes.length; capturedCount++; }
                }
            }
            if ([...resources, ...generated].some(name => !names.has(name)) || resources.has(frame.selectedRoot)
                || generated.has(frame.selectedRoot) || !names.has(frame.selectedRoot) || capturedCount > 64
                || projectBytes > 8388608 || workBytes > 33554432 || resourceBytes > (xe ? 167772160 : 100663296)) throw 0;
            const leaf = frame.selectedRoot.slice(frame.selectedRoot.lastIndexOf('/') + 1);
            const job = xe ? leaf.slice(0, -4) : 'result', artifact = job + (xe ? '.xdv' : '.pdf');
            const blx = job.replaceAll(' ', '_') + '-blx.bib';
            const outputNames = new Set(frame.outputs);
            if (outputNames.size !== frame.outputs.length || !outputNames.has(artifact)
                || frame.outputs.some(name => !path(name) || !(/\.(aux|bbl|blg|toc|lof|lot|out|log)$/i.test(name) || name === artifact || xe && name === job + '.pdf'
                    || [7, 8, 9, 10].includes(tag.profile) && name === blx)
                    || resources.has(name) || names.has(name) && !generated.has(name))) throw 0;
            maxLog = frame.maxLogBytes; log = ''; logBytes = 0; truncated = false;
            const F = module.FS;
            if (xe) {
                F.mkdir('/tex'); F.mkdir('/work'); F.chdir('/work');
                for (const file of frame.files) {
                    const target = resources.has(file.name) ? '/tex/' + file.name.slice(prefix.length) : '/work/' + file.name;
                    F.mkdirTree(target.slice(0, target.lastIndexOf('/'))); F.writeFile(target, file.bytes);
                }
                for (const name of frame.outputs) F.mkdirTree('/work/' + name.slice(0, Math.max(0, name.lastIndexOf('/'))));
                if (module.ccall('setMainEntry', 'number', ['string'], [frame.selectedRoot]) !== 0) throw 0;
            } else {
                if (module.ccall('oc_luatex_configure', 'number', ['string', 'string'], [frame.selectedRoot, tag.nonce]) !== 0) throw 0;
                for (const name of frame.outputs.filter(name => /\.(aux|bbl|blg|toc|lof|lot|out)$/i.test(name)
                    || [7, 8, 9, 10].includes(tag.profile) && name === blx))
                    if (module.ccall('oc_luatex_declare_output', 'number', ['string'], [name]) !== 0) throw 0;
                for (const file of frame.files) {
                    const pointer = module._malloc(Math.max(1, file.bytes.length));
                    if (!Number.isSafeInteger(pointer) || pointer <= 0 || pointer > module.HEAPU8.length - Math.max(1, file.bytes.length)) throw 0;
                    try {
                        module.HEAPU8.set(file.bytes, pointer);
                        if (lua('add_file', ['number', 'string', 'number', 'number'],
                            [resources.has(file.name) ? 0 : generated.has(file.name) ? 2 : 1,
                                resources.has(file.name) ? file.name.slice(prefix.length) : file.name, pointer, file.bytes.length]) !== 0) throw 0;
                    } finally { module._free(pointer); }
                }
            }
            for (const file of frame.files) inputs.push({ name: file.name, length: file.bytes.length, sha256: await hash(file.bytes) });
            if (navigation && module._oc_mem_configure(1, 1048575, tag.ordinal) !== 1) throw 0;
            emit({ kind: 'entered' }); entered = true;
            const start = performance.now(); nativeReturn = xe ? module._compileLaTeX() : lua('compile_latex');
            elapsedMs = performance.now() - start;
            if (!Number.isInteger(nativeReturn)) throw 0;
            outcome = 'native-return';
            if (nativeReturn === 0) {
                let total = 0;
                for (const name of frame.outputs) {
                    if (xe && name === job + '.pdf') continue;
                    let bytes;
                    if (xe) {
                        const target = '/work/' + name;
                        if (!F.analyzePath(target).exists) continue;
                        const info = F.stat(target);
                        if (!F.isFile(info.mode) || info.size > frame.maxOutputBytes - total) throw 0;
                        bytes = F.readFile(target);
                    } else {
                        const artifactIndex = name === 'result.pdf' ? 0 : name === 'result.log' ? 1 : null;
                        const length = artifactIndex === null ? lua('named_output_size', ['string'], [name]) : lua('output_size', ['number'], [artifactIndex]);
                        if (length === 0) continue;
                        if (!Number.isSafeInteger(length) || length < 0 || length > frame.maxOutputBytes - total) throw 0;
                        const pointer = module._malloc(length);
                        if (!Number.isSafeInteger(pointer) || pointer <= 0 || pointer > module.HEAPU8.length - length) throw 0;
                        try {
                            const copied = artifactIndex === null ? lua('copy_named_output', ['string', 'number', 'number'], [name, pointer, length])
                                : lua('copy_output', ['number', 'number', 'number'], [artifactIndex, pointer, length]);
                            if (copied !== length) throw 0;
                            bytes = module.HEAPU8.slice(pointer, pointer + length);
                        } finally { module._free(pointer); }
                    }
                    total += bytes.length; files.push({ name, bytes });
                }
                const output = files.find(file => file.name === artifact)?.bytes;
                if (!output?.length || (xe ? output[0] !== 247 || output[1] !== 7 : new TextDecoder().decode(output.slice(0, 5)) !== '%PDF-')) throw 0;
                if (navigation) {
                    // Optional readers cannot revoke an already admitted PDF.
                    // Native success/close owns sealing; no seal/finish calls here.
                    const sealedCopy = (lengthExport, copyExport, cap) => {
                        let pointer = 0;
                        try {
                            const n = module[lengthExport](tag.ordinal);
                            if (!Number.isSafeInteger(n) || n < 1 || n > cap) return null;
                            pointer = module._malloc(n);
                            if (!Number.isSafeInteger(pointer) || pointer <= 0 || pointer > module.HEAPU8.length - n) return null;
                            if (module[copyExport](tag.ordinal, pointer, n) !== n || pointer > module.HEAPU8.length - n) return null;
                            return module.HEAPU8.slice(pointer, pointer + n);
                        } catch { return null; }
                        finally { if (Number.isSafeInteger(pointer) && pointer > 0 && pointer < module.HEAPU8.length) module._free(pointer); }
                    };
                    sourceMapPayload = sealedCopy('_oc_synctex_length', '_oc_synctex_copy', 1048575);
                    if (sourceMapPayload && !xe) sourceMapGeometryPayload = sealedCopy('_oc_synctex_geometry_length', '_oc_synctex_geometry_copy', 131072);
                }
            }
        } catch (error) {
            files = []; sourceMapPayload = null; sourceMapGeometryPayload = null; outcome = error instanceof WebAssembly.RuntimeError ? 'wasm-abort' : entered ? 'protocol-failure' : 'setup-failure';
        }
        if (truncated) {
            const marker = '[Compiler log truncated]\n';
            if (encoder.encode(marker).length <= maxLog) {
                const bytes = encoder.encode(log).slice(0, Math.max(0, maxLog - encoder.encode(marker).length));
                log = marker + new TextDecoder('utf-8', { fatal: false }).decode(bytes);
                while (encoder.encode(log).length > maxLog) log = log.slice(0, -1);
            }
        }
        state = 'done';
        emit({ kind: 'done', reply: { tag: frame.tag, outcome, nativeReturn, entered, log, files, inputs, elapsedMs,
            heapBytes: module?.HEAPU8?.byteLength ?? null, wasmSha256, loaderSha256, sourceMapPayload, sourceMapGeometryPayload } });
    }
})();
