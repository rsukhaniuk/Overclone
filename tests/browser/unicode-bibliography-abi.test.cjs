// Actual worker glue with an instrumented C ABI; no successful native/PDF claim.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const { webcrypto } = require('node:crypto');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../../src/Client/wwwroot/js/unicode-tex-port-worker.js'), 'utf8');

async function attempt(abi, profile, extra = 'result-blx.bib') {
    const messages = [], declarations = [], added = [];
    let executions = 0;
    const module = {
        FS: {}, HEAPU8: new Uint8Array(1024), _malloc: () => 1, _free() {},
        ccall(name, _return, _types, args = []) {
            if (name === 'oc_luatex_abi') return abi;
            if (name === 'oc_luatex_configure') return 0;
            if (name === 'oc_luatex_declare_output') { declarations.push(args[0]); return 0; }
            if (name === 'oc_luatex_add_file') { added.push([args[0], args[1]]); return 0; }
            if (name === 'oc_luatex_compile_latex') { executions++; return 1; }
            throw new Error('unexpected C ABI operation: ' + name);
        }
    };
    const self = { postMessage: m => messages.push(m) };
    const context = {
        self, Uint8Array, TextEncoder, TextDecoder, crypto: webcrypto, performance,
        WebAssembly, importScripts() { self.createLuaTeX = async () => module; }
    };
    vm.runInNewContext(source, context);
    await self.onmessage({ data: { operation: 'boot', loaderUrl: 'blob:trusted-test-loader',
        loader: new Uint8Array([1]), wasm: new Uint8Array([2]) } });
    if (!messages.some(m => m.kind === 'ready')) return { messages, declarations, added, executions };
    const root = 'тези/my thesis.tex';
    const outputs = ['result.pdf', 'result.aux'];
    const files = [{ name: root, bytes: new Uint8Array([1]) },
        { name: '__oop_runtime__/lualatex.fmt', bytes: new Uint8Array([2]) }];
    const generatedInputNames = [];
    if (extra) {
        outputs.push(extra);
        files.push({ name: extra, bytes: new Uint8Array([3, 4]) });
        generatedInputNames.push(extra);
    }
    await self.onmessage({ data: {
        operation: 'latex', tag: { version: 9, profile, engineId: 'lualatex', tool: 'latex',
            nonce: 'fresh-test-nonce', runtimeIdentity: 'a'.repeat(64) },
        selectedRoot: root, mainEntry: root, outputs, files,
        runtimeResourceNames: ['__oop_runtime__/lualatex.fmt'], generatedInputNames,
        maxLogBytes: 65536, maxOutputBytes: 8388608
    } });
    return { messages, declarations, added, executions };
}

(async () => {
    for (const profile of [5, 6]) {
        const old = await attempt(2, profile, null);
        assert.equal(old.executions, 1, 'old ABI2 still reaches its actual worker entry');
        const wrong = await attempt(3, profile, null);
        assert.equal(wrong.executions, 0, 'ABI3 cannot impersonate an old profile');
        assert.equal(wrong.messages.at(-1).reply.entered, false);
        const changed = await attempt(2, profile);
        assert.equal(changed.executions, 0, 'old output allowlist remains unchanged');
    }
    const valid = await attempt(3, 7);
    assert.equal(valid.executions, 1);
    assert.ok(valid.declarations.includes('result-blx.bib'), 'BLX must cross the real declaration call before carry');
    assert.deepEqual(valid.added.find(a => a[1] === 'result-blx.bib'), [2, 'result-blx.bib']);
    assert.equal(valid.messages.at(-1).reply.nativeReturn, 1, 'instrumented failure is not a fake successful PDF');
    for (const [abi, profile, name] of [[2, 7, null], [3, 99, null],
        [3, 7, 'other-blx.bib'], [3, 7, 'result.bcf'], [3, 7, 'result.run.xml']]) {
        const invalid = await attempt(abi, profile, name);
        assert.equal(invalid.executions, 0);
        assert.equal(invalid.messages.at(-1).reply.entered, false);
        assert.deepEqual(invalid.declarations, []);
    }
    const unknown = await attempt(4, 7);
    assert.equal(unknown.executions, 0);
    assert.equal(unknown.messages.at(-1).kind, 'boot-error');
    console.log('Unicode bibliography ABI PASS: exact profile/ABI binding, declared byte carry and unchanged legacy boundaries.');
})().catch(error => { console.error(error); process.exitCode = 1; });
