// Real pinned WASM through the actual product worker glue in Node VM contexts.
// No browser controller, .NET acceptance, renderer or navigation-authority claim.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { webcrypto, createHash } = require('node:crypto');
const root = process.env.NAVIGATION_RUNTIME_DIR;
if (!root) { console.log('SKIP real navigation WASM: NAVIGATION_RUNTIME_DIR not set; portable modeled checks do not establish native acceptance.'); process.exit(0); }
const hash = b => createHash('sha256').update(b).digest('hex');
const pins = JSON.parse(fs.readFileSync(path.join(root, 'pins.json')));
const inventory = new Map(pins.map(p => [p.name, p]));
function resource(name) {
    const b = fs.readFileSync(path.join(root, 'files', name)), pin = inventory.get(name);
    assert.equal(b.length, pin.length); assert.equal(hash(b), pin.sha256);
    return new Uint8Array(b);
}
const closure = hash(Buffer.from([...pins].sort((a, b) => a.name < b.name ? -1 : 1)
    .map(p => `${p.name}\t${p.length}\t${p.sha256}`).join('\n')));
assert.equal(closure, '85448c892b86acdb232ed51079db3a686d2c06bdf703572fccbcdc7fbc49adc0');
assert.equal(pins.length, 3707); assert.equal(pins.reduce((n, p) => n + p.length, 0), 191844694);
async function run(worker, stem, frame) {
    const messages = [];
    const context = vm.createContext({ Uint8Array, Int8Array, Uint16Array, Int16Array, Uint32Array, Int32Array,
        Float32Array, Float64Array, ArrayBuffer, TextEncoder, TextDecoder, WebAssembly,
        crypto: webcrypto, performance, console: { log() {}, error() {}, warn() {} },
        setTimeout, clearTimeout, URL, Response, WorkerGlobalScope: function () {} });
    context.self = context; context.location = { href: 'https://transport.invalid/worker.js' };
    context.postMessage = m => messages.push(m);
    context.importScripts = () => vm.runInContext(fs.readFileSync(path.join(root, 'files', stem + '.js'), 'utf8'), context);
    vm.runInContext(fs.readFileSync(path.join(__dirname, '../../src/Client/wwwroot/js', worker), 'utf8'), context);
    await context.onmessage({ data: { operation: 'boot', loaderUrl: 'blob:owned-pinned-loader',
        loader: resource(stem + '.js'), wasm: resource(stem + '.wasm') } });
    assert.ok(messages.some(m => m.kind === 'ready'), JSON.stringify(messages));
    await context.onmessage({ data: frame });
    const result = messages.findLast(m => m.kind === 'done')?.reply;
    assert.ok(result, JSON.stringify(messages));
    assert.equal(result.outcome, 'native-return', JSON.stringify({ outcome: result.outcome, log: result.log }));
    assert.equal(result.nativeReturn, 0, result.log);
    assert.deepEqual(result.tag, frame.tag);
    for (const f of frame.files) {
        const fact = result.inputs.find(i => i.name === f.name);
        assert.deepEqual(fact && [fact.length, fact.sha256], [f.bytes.length, hash(f.bytes)]);
    }
    return result;
}
const prefix = '__oop_runtime__/';
const selectedRoot = 'тези/my thesis.tex';
const source = new TextEncoder().encode('\\documentclass{article}\n\\begin{document}Genuine transport geometry.\\end{document}\n');
function frame(engine, version, ordinal, resources, files, outputs, mainEntry = selectedRoot, tool = 'latex') {
    return { operation: tool, tag: { profile: 8, version, ordinal, engineId: engine, tool, job: 'job', epoch: 1,
        request: 'request', capture: 'capture', digest: 'a'.repeat(64), nonce: 'fresh-context-' + ordinal, runtimeIdentity: 'b'.repeat(64) },
        selectedRoot, mainEntry, runtimeResourceNames: resources, generatedInputNames: [], files, outputs,
        maxLogBytes: 262144, maxOutputBytes: 8388608 };
}
function compileFiles(engine) {
    const selectedFormat = engine === 'lualatex' ? 'lualatex.fmt' : 'latex-dvi.fmt';
    const resources = pins.filter(p => p.name === selectedFormat || !/\.(wasm|js|fmt)$/.test(p.name)
        && p.name !== 'icudt68l.dat' && p.name !== 'xetexfontlist.txt');
    const files = resources.map(p => ({ name: prefix + (p.name === 'latex-dvi.fmt' ? 'swiftlatexpdftex.fmt' : p.name), bytes: resource(p.name) }));
    return { files: [...files, { name: selectedRoot, bytes: source }], resources: files.map(f => f.name) };
}
(async () => {
    const d = compileFiles('latex-dvi');
    const outputs = ['.pdf', '.aux', '.bbl', '.blg', '.log', '.toc', '.lof', '.lot', '.out', '.dvi'].map(x => 'my thesis' + x).concat('my_thesis-blx.bib');
    const dvi = await run('compile-port-worker.js', 'dvi-map-engine', frame('latex-dvi', 12, 7, d.resources, d.files, outputs, '"' + selectedRoot + '"'));
    assert.ok(dvi.sourceMapPayload?.length > 0); assert.ok(dvi.sourceMapPayload.length <= 1048575);
    assert.equal(dvi.sourceMapGeometryPayload, undefined, 'DVI engine carries only its raw map');
    const intermediate = dvi.files.find(f => f.name === 'my thesis.dvi').bytes;
    const converterResources = pins.filter(p => ['glyphlist.txt', 'pdfglyphlist.txt'].includes(p.name) || /\.(tfm|pfb|ttf|otf|vf|map|enc)$/.test(p.name));
    const converterFiles = converterResources.map(p => ({ name: prefix + p.name, bytes: resource(p.name) }));
    const converted = await run('dvi-port-worker.js', 'dvi-map-converter', frame('latex-dvi', 12, 8, converterFiles.map(f => f.name),
        [...converterFiles, { name: selectedRoot, bytes: source }, { name: 'my thesis.dvi', bytes: intermediate }], ['my thesis.pdf'], 'my thesis.dvi', 'dvipdfmx'));
    assert.equal(converted.sourceMapPayload, null, 'converter must not need or echo the earlier raw map');
    assert.ok(converted.sourceMapGeometryPayload?.length > 0);
    const geo = JSON.parse(new TextDecoder().decode(converted.sourceMapGeometryPayload));
    assert.equal(geo.schemaVersion, 1); assert.equal(geo.pages[0].status, 'available');
    assert.equal(geo.pages[0].writerToPdfRaw.length, 6);
    const luaFiles = compileFiles('lualatex');
    const luaOutputs = ['.pdf', '.aux', '.bbl', '.blg', '.log', '.toc', '.lof', '.lot', '.out'].map(x => 'result' + x).concat('result-blx.bib');
    const lua = await run('unicode-tex-port-worker.js', 'luatex', frame('lualatex', 14, 11, luaFiles.resources, luaFiles.files, luaOutputs));
    assert.ok(lua.sourceMapPayload?.length > 0); assert.ok(lua.sourceMapGeometryPayload?.length > 0);
    const luaGeo = JSON.parse(new TextDecoder().decode(lua.sourceMapGeometryPayload));
    assert.equal(luaGeo.pages[0].status, 'available');
    const receipt = { kind: 'real-WASM-product-worker-glue-not-browser-or-managed-acceptance', closure,
        dvi: { latexOrdinal: 7, converterOrdinal: 8, map: hash(dvi.sourceMapPayload), intermediate: hash(intermediate),
            pdf: hash(converted.files[0].bytes), geometry: hash(converted.sourceMapGeometryPayload), pages: geo.pages.length },
        lua: { latexOrdinal: 11, map: hash(lua.sourceMapPayload), pdf: hash(lua.files.find(f => f.name === 'result.pdf').bytes),
            geometry: hash(lua.sourceMapGeometryPayload), pages: luaGeo.pages.length } };
    console.log(JSON.stringify(receipt, null, 2));
})().catch(error => { console.error(error); process.exitCode = 1; });
