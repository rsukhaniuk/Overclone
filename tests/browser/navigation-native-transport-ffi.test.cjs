// Actual Lua worker glue with MODELED C ABI and deliberately modeled boot hashes.
// Tests native call ordering/ownership/failure paths, not genuine native output.
const assert = require('node:assert/strict'), fs = require('node:fs'), vm = require('node:vm'), path = require('node:path');
const { webcrypto } = require('node:crypto');
const source = fs.readFileSync(path.join(__dirname, '../../src/Client/wwwroot/js/unicode-tex-port-worker.js'), 'utf8');
async function attempt(options = {}) {
    const messages = [], calls = [], freed = [], allocations = [];
    const pdf = new TextEncoder().encode('%PDF-modeled'), raw = new Uint8Array([11, 12, 13]), side = new Uint8Array([21, 22]);
    let next = 64, copying = false;
    const module = { FS: {}, HEAPU8: new Uint8Array(8192),
        _malloc(n) { const p = copying && options.badPointer ? 9000 : next; next += n + 8; allocations.push(p); return p; },
        _free(p) { freed.push(p); },
        ccall(name, _r, _t, args = []) {
            calls.push([name, ...args]);
            if (name === 'oc_luatex_abi') return options.abi ?? 5;
            if (['oc_luatex_configure','oc_luatex_declare_output','oc_luatex_add_file'].includes(name)) return 0;
            if (name === 'oc_luatex_compile_latex') return options.failure ? 1 : 0;
            if (name === 'oc_luatex_named_output_size') return 0;
            if (name === 'oc_luatex_output_size') return args[0] === 0 ? pdf.length : 0;
            if (name === 'oc_luatex_copy_output') { module.HEAPU8.set(pdf, args[1]); return pdf.length; }
            throw new Error('Unexpected ABI: ' + name);
        },
        _oc_synctex_abi: () => 65536,
        _oc_synctex_status: () => 1,
        _oc_mem_configure(...args) { calls.push(['configure-map', ...args]); return 1; },
        _oc_synctex_length(tag) { calls.push(['map-length', tag]); copying = true; return options.overMap ? 1048576 : raw.length; },
        _oc_synctex_copy(tag, p, n) { calls.push(['map-copy', tag, p, n]); module.HEAPU8.set(raw, p); return options.shortMap ? n - 1 : n; },
        _oc_synctex_geometry_length(tag) { calls.push(['geometry-length', tag]); return options.overGeometry ? 131073 : side.length; },
        _oc_synctex_geometry_copy(tag, p, n) { calls.push(['geometry-copy', tag, p, n]); module.HEAPU8.set(side, p); return options.shortGeometry ? n - 1 : n; }
    };
    if (options.missingExport) delete module._oc_synctex_geometry_copy;
    // Only two marker boot buffers receive fake known hashes. Input hashing uses
    // actual WebCrypto; this is explicitly a modeled module/ABI execution test.
    const hashes = ['7282eb2decde52603417b94d363a6d3258e8ef1451a0d20847bee70084c70a21',
        '1f5b9ca7742693299f1074c47e2a3b91d1df2d6c0381d5c4d1a628965cde542e'];
    const crypto = { subtle: { digest(algorithm, bytes) {
        if (bytes.length === 1 && [1, 2].includes(bytes[0])) return Promise.resolve(Uint8Array.from(Buffer.from(hashes[bytes[0] - 1], 'hex')).buffer);
        return webcrypto.subtle.digest(algorithm, bytes);
    } } };
    const self = { postMessage: m => messages.push(m) };
    vm.runInNewContext(source, { self, Uint8Array, TextEncoder, TextDecoder, crypto, WebAssembly, performance,
        importScripts() { self.createLuaTeX = async () => module; } });
    await self.onmessage({ data: { operation: 'boot', loaderUrl: 'blob:modeled', loader: new Uint8Array([1]), wasm: new Uint8Array([2]) } });
    if (!messages.some(m => m.kind === 'ready')) return { calls, messages, freed, allocations };
    const root = 'тези/my thesis.tex';
    await self.onmessage({ data: { operation: 'latex', tag: { version: options.version ?? 14, profile: options.profile ?? 8,
        engineId: 'lualatex', tool: 'latex', nonce: 'fresh', runtimeIdentity: 'a'.repeat(64), ordinal: options.ordinal ?? 7 },
        mainEntry: root, selectedRoot: root, files: [{ name: root, bytes: new Uint8Array([3, 4]) },
            { name: '__oop_runtime__/lualatex.fmt', bytes: new Uint8Array([5, 6]) }],
        runtimeResourceNames: ['__oop_runtime__/lualatex.fmt'], generatedInputNames: [],
        outputs: ['result.pdf','result.log','result-blx.bib'], maxLogBytes: 65536, maxOutputBytes: 8388608 } });
    return { calls, messages, freed, allocations, heap: module.HEAPU8 };
}
(async () => {
    const good = await attempt(), reply = good.messages.at(-1).reply;
    assert.equal(reply.nativeReturn, 0); assert.equal(reply.outcome, 'native-return');
    assert.deepEqual([...reply.sourceMapPayload], [11, 12, 13]); assert.deepEqual([...reply.sourceMapGeometryPayload], [21, 22]);
    assert.equal(good.calls.filter(c => c[0] === 'configure-map').length, 1);
    const config = good.calls.findIndex(c => c[0] === 'configure-map'), compile = good.calls.findIndex(c => c[0] === 'oc_luatex_compile_latex');
    assert.ok(config > good.calls.findLastIndex(c => c[0] === 'oc_luatex_add_file')); assert.ok(config < compile);
    for (const call of good.calls.filter(c => /^(map|geometry)-(length|copy)$/.test(c[0]))) assert.equal(call[1], 7);
    assert.deepEqual(good.freed, good.allocations); good.heap.fill(0);
    assert.deepEqual([...reply.sourceMapPayload], [11, 12, 13], 'no retained mutable heap views');
    for (const o of [{ overMap: true }, { shortMap: true }, { badPointer: true }, { overGeometry: true }, { shortGeometry: true }]) {
        const r = await attempt(o), result = r.messages.at(-1).reply;
        assert.equal(result.outcome, 'native-return'); assert.equal(result.files[0].name, 'result.pdf');
        assert.equal(result.sourceMapGeometryPayload, null);
        assert.ok(r.freed.every(p => p > 0 && p < r.heap.length), 'never free an invalid native pointer');
    }
    for (const o of [{ abi: 3 }, { missingExport: true }, { ordinal: 0 }, { ordinal: -1 }, { ordinal: 4294967296 },
        { ordinal: 7.5 }, { profile: 10 }, { version: 13 }, { profile: 7, version: 9 }]) {
        const r = await attempt(o); assert.ok(!r.calls.some(c => c[0] === 'oc_luatex_compile_latex'), JSON.stringify(o));
    }
    const failed = await attempt({ failure: true });
    assert.equal(failed.messages.at(-1).reply.sourceMapPayload, null);
    assert.ok(!failed.calls.some(c => c[0] === 'map-length'));
    console.log('Navigation FFI modeled PASS: ABI/export/token admission, post-setup one-shot configuration, bounded copied readers, optional PDF preservation and failure revocation.');
})().catch(error => { console.error(error); process.exitCode = 1; });
