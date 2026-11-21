// Actual request and response admission with modeled browser controller/Worker.
'use strict';
const assert = require('node:assert/strict');
const { frame, harness } = require('./restricted-worker-test-helpers.cjs');

const MiB = 1024 * 1024;
const identities = {
    17: '41d0bb841b4832771327e44c84ada913e75204e25c5f39d6e52204e7350bf109',
    18: 'f4ad5c7f702efc4f90d7a803ad6c697be0b3664b4ca90f837c8e6bd74ca88a40',
};
const request = (profile, overrides = {}) => {
    const value = frame(23, 'selected-pdf' + profile);
    Object.assign(value.tag, { profile, engineId: 'pdftex', runtimeIdentity: identities[profile], ...overrides });
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

async function verify(profile, otherProfile) {
    const h = harness();
    await h.api.provision();
    const admitted = await reply(h, request(profile), [['Main.pdf', 12 * MiB], ['Main.aux', 4 * MiB]]);
    assert.equal(admitted.outcome, 'native-return');
    assert.equal(admitted.files[0].bytes.byteLength, 12 * MiB);
    for (const [outputs, nativeReturn] of [
        [[['Main.aux', 8 * MiB + 1]]],
        [[['Main.pdf', 12 * MiB + 1]]],
        [[['Main.pdf', 12 * MiB], ['Main.aux', 4 * MiB + 1]]],
        [[['main.pdf', 8 * MiB + 1]]],
        [[['Main.pdf', 8 * MiB + 1]], 1],
    ]) assert.equal((await reply(h, request(profile), outputs, nativeReturn)).outcome, 'protocol-failure');

    const before = h.stats.workers.length;
    for (const overrides of [
        { runtimeIdentity: '0'.repeat(64) }, { profile: 16 }, { profile: otherProfile },
        { runtimeIdentity: identities[otherProfile] },
        { tool: 'bibtex' }, { tool: 'biber', version: 10 },
        { tool: 'dvipdfmx', engineId: 'latex-dvi', version: 24 },
        { engineId: 'latex-dvi', version: 24 }, { engineId: 'xelatex', version: 21 },
        { engineId: 'lualatex', version: 22 },
    ]) assert.throws(() => h.api.execute(request(profile, overrides)), /invalid bounded native transport frame/);
    const wrongIdentity = request(profile, { runtimeIdentity: '0'.repeat(64) });
    wrongIdentity.maxOutputBytes = 8 * MiB;
    const source = request(profile);
    source.files[0].bytes = new Uint8Array(2 * MiB + 1);
    assert.throws(() => h.api.execute(wrongIdentity), /invalid bounded native transport frame/);
    assert.throws(() => h.api.execute(source), /invalid bounded native transport frame/);
    assert.equal(h.stats.workers.length, before);

    const mutable = request(profile);
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
    console.log(`P${profile} modeled bridge PASS: exact selected successful PDF, unchanged sidecar and aggregate bounds, identity/phase/source refusals.`);
}
(async () => {
    await verify(17, 18);
    await verify(18, 17);
})().catch(error => { console.error(error); process.exitCode = 1; });
