// Mechanical byte/C-ABI glue only. C# chooses the engine, passes, resources,
// convergence and revision authority; the native converter owns its virtual IO.
(() => {
    const pB=p=>p===13||p===14||p===15||p===16||p===17||p===18||p===19||p===20||p===21;
    const emit = self.postMessage.bind(self), encoder = new TextEncoder();
    let state = 'boot', module = null, loaderSha256 = null, wasmSha256 = null, nativeAbi;
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
            nativeAbi = module._oc_dvi_abi_version();
            if (![1, 2, 3].includes(nativeAbi)) throw 0;
            state = 'ready'; self.onmessage = execute; emit({ kind: 'ready' });
        } catch { state = 'done'; emit({ kind: 'boot-error', error: 'ConverterInit' }); }
    };
    async function execute({ data: frame }) {
        if (state !== 'ready') return;
        state = 'running';
        let entered = false, nativeReturn = null, elapsedMs = null, log = '', inputs = [], files = [], outcome = 'setup-failure';
        let sourceMapGeometryPayload = null;
        try {
            const tag = frame.tag, resources = frame.runtimeResourceNames;
            const expanded = [16,17,18,19,20,21].includes(tag?.profile), origins = expanded || tag?.profile === 15;
            const semantic = pB(tag?.profile) && tag.engineId === 'latex-dvi';
            const graphics = [10, 12, 13,14,15,16,17,18,19,20,21].includes(tag?.profile);
            const xeNavigation = (tag?.profile === 9 && tag.version === 13 || graphics && tag.version === ([12,13,14,15,16,17,18,19,20,21].includes(tag.profile) ? 21 : 16)) && tag.engineId === 'xelatex';
            const dviNavigation = ([8, 9].includes(tag?.profile) && tag.version === 12 || graphics && tag.version === (pB(tag.profile) ? 24 : 15)) && tag.engineId === 'latex-dvi';
            const navigation = xeNavigation || dviNavigation;
            const suite = [5, 6, 7, 8, 9, 10, 12, 13,14,15,16,17,18,19,20,21].includes(tag?.profile), xe = xeNavigation || [5, 6, 7, 8].includes(tag?.profile) && tag.version === 8 && tag.engineId === 'xelatex';
            const dvi = dviNavigation || tag?.version === 7 && [4, 5, 6, 7].includes(tag.profile) && tag.engineId === 'latex-dvi';
            const prefix = '__oop_runtime__/', resourceName = name => suite ? name.slice(prefix.length) : name;
            if (!(dvi || xe) || tag.tool !== 'dvipdfmx' || nativeAbi !== (xeNavigation ? 3 : navigation ? 2 : 1)
                || [8, 9].includes(tag.profile) && !navigation && (loaderSha256 !== 'dc31543194583c105228dc791040c2f09afd7aa64311ce254b215c9fcd24e54a'
                    || wasmSha256 !== '52085c656ec2680150519d39ecd8588326e123652d72294e7e19eedd0ec0171c')
                || origins && (module._oc_dvi_origin_abi?.() !== 1 || module._oc_dvi_neutral_abi?.() !== 1)
                || expanded && (module._oc_dvi_input_budget_abi?.() !== 1 || module._oc_dvi_project_input_limit?.() !== 16777216 || module._oc_dvi_pdf_output_limit?.() !== 8388608)
                || graphics && (typeof module._oc_dvi_graphics_abi !== 'function' || module._oc_dvi_graphics_abi() !== 1)
                || pB(tag.profile) && !['none','bibtex','biber'].includes(frame.bibliographyTool)
                || semantic && (typeof module._oc_dvi_font_semantics_abi !== 'function' || module._oc_dvi_font_semantics_abi() !== 1 || typeof module._oc_dvi_font_semantics_status !== 'function')
                || navigation && (loaderSha256 !== (expanded ? xeNavigation ? 'a61b06d8530fc0e2a09cc874c8e36260925cc15308b674e946634689d2d62bbc' : '75d1c4441867f3875f82d09409c3eb0ebb8ee0d31d73d8008fc150605c023b09' : origins ? xeNavigation ? 'c6feeaac32a9d23d03720de9c0d977506d5186b397c9f3ff221343e25ad3dd89' : '2aafbfe08a1a5a05d7fdb1980daf56781cadb67fbb16f1444d79898ffe956be9' : semantic ? '2975efeae6dc759ccdce984f68350a250ed8bb8bedaa6584329de5901db23caf' : graphics ? 'a961144a37546755175dfda0687c22bb79e67ebef495aa84c3922b01af6f2c57' : 'd79dca5dd87ab27ecb7dcc63cc96171092ea6a57c43054e4cabcf39a0f5c8c72')
                    || wasmSha256 !== (tag.profile === 21 && !xe ? '73c8714f530b6b7821cc2c3b4ba73b82baebc4958ab2b741ae84796f7705efc8' : [19,20].includes(tag.profile) && !xe ? 'd54c78b40e66b6bd12e8ff56396468b2b0a3e3d6ead36b73cf3c177cd02ab89e' : expanded ? xeNavigation ? '44ae512b65187816e6b43e40c1d94857734336721f30090ea2ed83f111001e12' : '44fe2d05227f7e8d0736ab929825088619e1f741afcb4b788180a6d4c743fe9c' : origins ? xeNavigation ? '182fa124810d48d1dbfe17ee82c3deebdeeb6fec8838c590a2b445389d2224d1' : '1d2bed683ca42adc712de9c4649b26a6bb7001451cc00915d46b213fe81e304d' : semantic ? '4f4e13b462772c15184bf4279b533d598d446fa7a44c0b2bafda276baf91701f' : graphics ? xeNavigation ? 'ca030fe4077fc02338e4d92cdc5c0ef1b89c73bcb629b9d44f59708993fa15ef' : '53bd8a8a98c509afa8b0e0e5451bf78d7e9d765da1c8f513d9c1a909e9d5270d' : xeNavigation ? '10031d2748be3f6fa6ba5bc99fd4ef597f06f66208c5b9a5aeba60d5dc3aecac' : 'fb6d4da9ee438d66d89a7a0ec36ab7dd243ea2a5c32c4a426179b935e383ca84')
                    || !Number.isSafeInteger(tag.ordinal) || tag.ordinal < 1 || tag.ordinal > 4294967295
                    || ['_oc_dvi_geometry_configure','_oc_dvi_geometry_length','_oc_dvi_geometry_copy','_oc_dvi_geometry_data','_oc_dvi_geometry_size']
                        .some(n => typeof module[n] !== 'function'))
                || frame.operation !== 'dvipdfmx' || !/^[0-9a-f]{64}$/.test(tag.runtimeIdentity)
                || tag.profile === 17 && tag.runtimeIdentity !== '41d0bb841b4832771327e44c84ada913e75204e25c5f39d6e52204e7350bf109'
                || tag.profile === 18 && tag.runtimeIdentity !== 'f4ad5c7f702efc4f90d7a803ad6c697be0b3664b4ca90f837c8e6bd74ca88a40'
                || tag.profile === 19 && tag.runtimeIdentity !== '1974b526f3a47b33f60d5a5384f14b3c618ac996d950ab514e13ef119d06bc79'
                || tag.profile === 20 && tag.runtimeIdentity !== '84abead5a2feb357d52ff01364f5b72ed099610c40a8c0428ac10f9a64b0e652'
                || tag.profile === 21 && tag.runtimeIdentity !== 'eb47ed92ac9efcdc7277c6b3dd1c5861abde63213e440d40aa7c201af6f4d692'
                || !path(frame.mainEntry) || !path(frame.selectedRoot) || !/\.tex$/i.test(frame.selectedRoot)
                || !Array.isArray(frame.files) || frame.files.length < 1 || frame.files.length > 4096
                || !Array.isArray(resources) || resources.length > frame.files.length
                || !Array.isArray(frame.outputs) || frame.outputs.length !== 1
                || !Number.isSafeInteger(frame.maxLogBytes) || frame.maxLogBytes < 0 || frame.maxLogBytes > 262144
                || !Number.isSafeInteger(frame.maxOutputBytes) || frame.maxOutputBytes < 1 || frame.maxOutputBytes > 8388608) throw 0;
            const leaf = frame.selectedRoot.slice(frame.selectedRoot.lastIndexOf('/') + 1), job = leaf.slice(0, -4);
            if (frame.mainEntry !== job + (xe ? '.xdv' : '.dvi') || frame.outputs[0] !== job + '.pdf') throw 0;
            if (pB(tag.profile) && (!Array.isArray(frame.generatedInputNames) || frame.generatedInputNames.length !== 0
                || frame.files.some(file => typeof file?.name === 'string' && file.name.normalize('NFC').toUpperCase() === (job + '.pdf').normalize('NFC').toUpperCase())
                || resources.some(name => typeof name === 'string' && resourceName(name).normalize('NFC').toUpperCase() === (job + '.pdf').normalize('NFC').toUpperCase()))) throw 0;
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
                else {
                    if ([17,18,19,20,21].includes(tag.profile) && file.name !== frame.mainEntry && file.bytes.length > 2097152) throw 0;
                    projectBytes += file.bytes.length;
                }
                if (projectBytes > (expanded ? 16777216 : 8388608) || resourceBytes > 100663296) throw 0;
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
            if (navigation && module._oc_dvi_geometry_configure(1, tag.ordinal) !== 1) throw 0;
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
            if (status === 0 && nativeReturn === 0 && semantic && module._oc_dvi_font_semantics_status() === 2) {
                outcome = 'font-semantics-unsupported';
            } else if (status === 0 && nativeReturn === 0) {
                if (semantic && module._oc_dvi_font_semantics_status() !== 1) throw 0;
                const pdf = copy(module._oc_dvi_pdf_data(), module._oc_dvi_pdf_size(), frame.maxOutputBytes);
                if (pdf.length < 5 || new TextDecoder().decode(pdf.subarray(0, 5)) !== '%PDF-') throw 0;
                files = [{ name: frame.outputs[0], bytes: pdf }];
                if (navigation) {
                    let pointer = 0;
                    try {
                        const n = module._oc_dvi_geometry_length(tag.ordinal);
                        if (Number.isSafeInteger(n) && n > 0 && n <= 131072) {
                            pointer = module._malloc(n);
                            if (Number.isSafeInteger(pointer) && pointer > 0 && pointer <= module.HEAPU8.length - n
                                && module._oc_dvi_geometry_copy(tag.ordinal, pointer, n) === n
                                && pointer <= module.HEAPU8.length - n)
                                sourceMapGeometryPayload = module.HEAPU8.slice(pointer, pointer + n);
                        }
                    } catch { sourceMapGeometryPayload = null; }
                    finally { if (Number.isSafeInteger(pointer) && pointer > 0 && pointer < module.HEAPU8.length) module._free(pointer); }
                }
            }
        } catch (error) { files = []; sourceMapGeometryPayload = null; outcome = error instanceof WebAssembly.RuntimeError ? 'wasm-abort' : entered ? 'protocol-failure' : 'setup-failure'; }
        state = 'done';
        emit({ kind: 'done', reply: { tag: frame.tag, outcome, nativeReturn, entered, log, files, inputs, elapsedMs,
            heapBytes: module?.HEAPU8?.byteLength ?? null, wasmSha256, loaderSha256, sourceMapPayload: null, sourceMapGeometryPayload } });
    }
})();
