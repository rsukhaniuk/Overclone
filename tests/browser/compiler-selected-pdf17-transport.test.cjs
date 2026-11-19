// Actual request and response admission with modeled browser controller/Worker.
'use strict';
const assert = require('node:assert/strict');
const { frame, harness } = require('./restricted-worker-test-helpers.cjs');

const MiB = 1024 * 1024;
const identity = '41d0bb841b4832771327e44c84ada913e75204e25c5f39d6e52204e7350bf109';
const request = (overrides = {}) => {
    const value = frame(23, 'selected-pdf17');
    Object.assign(value.tag, { profile: 17, engineId: 'pdftex', runtimeIdentity: identity, ...overrides });
    value.selectedRoot = 'chapter/Main.tex';
    value.mainEntry = value.selectedRoot;
    value.files = [{ name: value.selectedRoot, bytes: new Uint8Array([1]) }];
    value.outputs = ['Main.pdf', 'Main.aux', 'chapter/facts.aux'];
    value.bibliographyTool = 'none';
    value.maxOutputBytes = 12 * MiB;
    return value;
};

function reply(h, value, outputs, nativeReturn = 0) {
    const result = h.api.execute(value);
    const worker = h.stats.workers.at(-1);
    worker.onmessage({ data: { kind: 'ready' } });
    assert.equal(worker.messages.at(-1).tag.runtimeIdentity, value.tag.runtimeIdentity);
    worker.onmessage({ data: { kind: 'done', reply: {
        tag: value.tag, outcome: 'native-return', nativeReturn, entered: true, log: '',
        files: outputs.map(([name, length]) => ({ name, bytes: new Uint8Array(length) })), inputs: [],
    } } });
    assert.equal(worker.terminated, 1);
    return result;
}

(async () => {
    const h = harness();
    await h.api.provision();
    const admitted = await reply(h, request(), [['Main.pdf', 12 * MiB], ['Main.aux', 4 * MiB]]);
    assert.equal(admitted.outcome, 'native-return');
    assert.equal(admitted.files[0].bytes.byteLength, 12 * MiB);
    for (const [outputs, nativeReturn] of [
        [[['Main.aux', 8 * MiB + 1]]],
        [[['Main.pdf', 12 * MiB + 1]]],
        [[['Main.pdf', 12 * MiB], ['Main.aux', 4 * MiB + 1]]],
        [[['main.pdf', 8 * MiB + 1]]],
        [[['Main.pdf', 8 * MiB + 1]], 1],
    ]) assert.equal((await reply(h, request(), outputs, nativeReturn)).outcome, 'protocol-failure');

    const before = h.stats.workers.length;
    for (const overrides of [
        { runtimeIdentity: '0'.repeat(64) }, { profile: 16 },
        { tool: 'bibtex' }, { tool: 'biber', version: 10 },
        { tool: 'dvipdfmx', engineId: 'latex-dvi', version: 24 },
        { engineId: 'latex-dvi', version: 24 }, { engineId: 'xelatex', version: 21 },
        { engineId: 'lualatex', version: 22 },
    ]) assert.throws(() => h.api.execute(request(overrides)), /invalid bounded native transport frame/);
    const wrongIdentity = request({ runtimeIdentity: '0'.repeat(64) });
    wrongIdentity.maxOutputBytes = 8 * MiB;
    const source = request();
    source.files[0].bytes = new Uint8Array(2 * MiB + 1);
    assert.throws(() => h.api.execute(wrongIdentity), /invalid bounded native transport frame/);
    assert.throws(() => h.api.execute(source), /invalid bounded native transport frame/);
    assert.equal(h.stats.workers.length, before);

    const mutable = request();
    mutable.files.push({ name: 'chapter/New.tex', bytes: new Uint8Array([1]) });
    const result = h.api.execute(mutable);
    const worker = h.stats.workers.at(-1);
    mutable.selectedRoot = mutable.mainEntry = 'chapter/New.tex';
    worker.onmessage({ data: { kind: 'ready' } });
    assert.equal((await result).outcome, 'transport-send-error');
    assert.equal(worker.terminated, 1);
    assert.equal(h.api.observations().started, h.api.observations().settled);
    assert.equal(h.stats.urls.length, h.stats.revoked.length);
    h.api.dispose();
    console.log('P17 modeled bridge PASS: exact selected successful PDF, unchanged sidecar and aggregate bounds, identity/phase/source refusals.');
})().catch(error => { console.error(error); process.exitCode = 1; });
