// Test-only capture. Preserve the exact native JSON of every operation.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const {createHash} = require('node:crypto');
const [runtime, file, output] = process.argv.slice(2);
const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const fingerprint = bytes => ({bytes: bytes.length, sha256: sha(bytes)});

(async () => {
    const loader = fs.readFileSync(path.join(runtime, 'e1.js'));
    const wasm = fs.readFileSync(path.join(runtime, 'e1.wasm'));
    const module = await require(path.resolve(runtime, 'e1.js'))({
        wasmBinary: wasm, print: () => {}, printErr: () => {},
    });
    const input = bytes => {
        const handle = module._e1_alloc(bytes.length);
        assert.ok(handle);
        module.HEAPU8.set(bytes, module._e1_data(handle));
        return handle;
    };
    const check = (operation, ...arguments_) => assert.equal(module['_' + operation](...arguments_), 0, operation);
    const take = operation => {
        check(operation, page);
        const source = module._e1_result();
        const size = module._e1_size(source);
        const destination = input(Buffer.alloc(size));
        check('e1_copy', source, destination);
        const bytes = Buffer.from(module.HEAPU8.slice(module._e1_data(destination), module._e1_data(destination) + size));
        check('e1_release', destination);
        check('e1_release', source);
        return bytes;
    };
    const budget = Buffer.alloc(36);
    [1, 36, 8388608, 32, 100000, 256, 4000000, 16777216, 64]
        .forEach((value, index) => budget.writeUInt32LE(value, index * 4));
    const pdf = fs.readFileSync(file);
    const pdfHandle = input(pdf), budgetHandle = input(budget);
    check('e1_open', pdfHandle, budgetHandle);
    const document = module._e1_result();
    check('e1_release', pdfHandle);
    check('e1_release', budgetHandle);
    check('e1_page', document, 0);
    const page = module._e1_result();
    const capabilities = take('e1_layout_capabilities');
    const layout = take('e1_layout_facts');
    const objects = take('e1_object_layout_facts');
    check('e1_close', page);
    check('e1_close', document);
    assert.equal(module._e1_live(), 0);

    assert.equal(JSON.parse(capabilities).schemaVersion, 2);
    const text = JSON.parse(layout), inventory = JSON.parse(objects);
    assert.deepEqual(text.characters.map(c => [c.unicode, c.inkStatus]), Array(4).fill([65, 'available']));
    assert.deepEqual(inventory.images.map(i => [i.objectIndex, i.status, i.rawBox]), [
        [0, 'available', [18, 20, 118, 44]], [1, 'available', [24, 110, 104, 132]],
    ]);
    const batch = Buffer.from(`{"schemaVersion":2,"documentSha256":"${sha(pdf)}",`
        + `"pages":[{"capabilities":${capabilities},"facts":${layout}}],"objectLayout":${objects}}`);
    fs.mkdirSync(output, {recursive: true});
    for (const [name, data] of Object.entries({capabilities, layout, objects, 'paired-batch': batch})) {
        fs.writeFileSync(path.join(output, name + '.json'), data);
    }
    const lineage = {
        fixtureKind: 'synthetic-pdf-genuine-native-facts',
        pdf: fingerprint(pdf), batch: fingerprint(batch), capabilities: fingerprint(capabilities),
        layout: fingerprint(layout), objects: fingerprint(objects),
        runtime: {loader: fingerprint(loader), wasm: fingerprint(wasm)},
    };
    fs.writeFileSync(path.join(output, 'lineage.json'), JSON.stringify(lineage, null, 2) + '\n');
})().catch(error => { console.error(error); process.exitCode = 1; });
