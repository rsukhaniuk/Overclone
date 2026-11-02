// MODELED browser/worker replies through the actual transport admission code.
// No native execution/CSP/browser acceptance claim; real WASM is a separate test.
const assert = require('node:assert/strict');
const { harness, frame, origin } = require('./restricted-worker-test-helpers.cjs');
function request(version, engineId, tool = 'latex', profile = 8) {
    const r = frame(version, `${version}-${engineId}-${tool}`);
    Object.assign(r.tag, { profile, engineId, tool, ordinal: 7 });
    r.runtimeResourceNames = []; r.generatedInputNames = [];
    if (tool === 'dvipdfmx') {
        r.mainEntry = 'main.dvi'; r.files.push({ name: 'main.dvi', bytes: new Uint8Array([247, 2]) });
    }
    return r;
}
function settle(h, r, optional = {}) {
    const operation = h.api.execute(r), worker = h.stats.workers.at(-1);
    worker.onmessage({ data: { kind: 'ready' } });
    const reply = { tag: r.tag, outcome: 'native-return', nativeReturn: 0, entered: true, log: '',
        files: [{ name: r.outputs[0], bytes: new Uint8Array([37, 80, 68, 70, 45]) }], inputs: [], ...optional };
    worker.onmessage({ data: { kind: 'done', reply } });
    return { operation, worker };
}
(async () => {
    const h = harness();
    await h.api.provision(); await h.api.provisionDvi(); await h.api.provisionLuatex(); await h.api.provisionXetex();
    let admitted = 0;
    for (const [version, engine, tool, kind] of [[6, 'pdftex', 'latex', 'compile'], [12, 'latex-dvi', 'latex', 'compile'],
        [12, 'latex-dvi', 'bibtex', 'compile'], [12, 'latex-dvi', 'dvipdfmx', 'dvi'], [8, 'xelatex', 'latex', 'xetex'],
        [8, 'xelatex', 'dvipdfmx', 'dvi'], [14, 'lualatex', 'latex', 'luatex'], [14, 'lualatex', 'bibtex', 'compile']]) {
        const { operation, worker } = settle(h, request(version, engine, tool));
        assert.ok(worker.url.startsWith(origin + '/__oop/native-worker/' + kind + '?'));
        assert.equal((await operation).nativeReturn, 0); assert.equal(worker.terminated, 1); admitted++;
    }
    const before = h.stats.workers.length;
    for (const [v, e, t, p] of [[7, 'latex-dvi', 'latex', 8], [9, 'lualatex', 'latex', 8], [12, 'latex-dvi', 'latex', 7],
        [14, 'lualatex', 'latex', 7], [10, 'pdftex', 'bibtex', 8], [11, 'pdftex', 'latex', 8], [13, 'xelatex', 'latex', 8],
        [14, 'lualatex', 'dvipdfmx', 8], [14, 'xelatex', 'latex', 8], [12, 'lualatex', 'latex', 8], [14, 'lualatex', 'latex', 10]])
        assert.throws(() => h.api.execute(request(v, e, t, p)));
    assert.equal(h.stats.workers.length, before, 'closed rejection precedes Worker/URL');

    const map = new Uint8Array([1, 2]), sidecar = new Uint8Array([3, 4]);
    let r = request(14, 'lualatex');
    let attempt = settle(h, r, { sourceMapPayload: map, sourceMapGeometryPayload: sidecar });
    let result = await attempt.operation;
    map.fill(9); sidecar.fill(9);
    assert.deepEqual([...result.sourceMapPayload], [1, 2]); assert.deepEqual([...result.sourceMapGeometryPayload], [3, 4]);
    assert.equal(attempt.worker.terminated, 1);
    r = request(12, 'latex-dvi', 'dvipdfmx');
    result = await settle(h, r, { sourceMapGeometryPayload: new Uint8Array([7]) }).operation;
    assert.deepEqual([...result.sourceMapGeometryPayload], [7], 'converter geometry does not require its own raw map');
    assert.equal(result.sourceMapPayload, undefined);

    for (const bad of [new Uint8Array(131073), [], { length: 1 }, '!!!=', new Uint16Array([3])]) {
        result = await settle(h, request(12, 'latex-dvi', 'dvipdfmx'), { sourceMapGeometryPayload: bad }).operation;
        assert.equal(result.nativeReturn, 0); assert.equal(result.files.length, 1);
        assert.equal(result.sourceMapGeometryPayload, null, 'bad optional sidecar never serializes to .NET');
    }
    for (const bad of [new Uint8Array(1048576), [], new Uint16Array([3])]) {
        result = await settle(h, request(14, 'lualatex'), { sourceMapPayload: bad, sourceMapGeometryPayload: new Uint8Array([4]) }).operation;
        assert.equal(result.nativeReturn, 0); assert.equal(result.sourceMapPayload, null); assert.equal(result.sourceMapGeometryPayload, null);
    }
    for (const [v, e, t, optional] of [[12, 'latex-dvi', 'latex', { sourceMapGeometryPayload: new Uint8Array([3]) }],
        [12, 'latex-dvi', 'dvipdfmx', { sourceMapPayload: new Uint8Array([3]) }],
        [14, 'lualatex', 'bibtex', { sourceMapGeometryPayload: new Uint8Array([3]) }],
        [8, 'xelatex', 'latex', { sourceMapGeometryPayload: new Uint8Array([3]) }],
        [14, 'lualatex', 'latex', { nativeReturn: 1, sourceMapPayload: new Uint8Array([3]) }]]) {
        result = await settle(h, request(v, e, t), optional).operation;
        assert.equal(result.outcome, 'protocol-failure', 'wrong phase/tool/success optional data cannot cross interop');
    }
    const held = h.api.execute(request(14, 'lualatex')), worker = h.stats.workers.at(-1), saved = worker.onmessage;
    h.api.terminate(request(14, 'lualatex').tag.job, 1);
    assert.equal((await held).outcome, 'cancelled'); assert.equal(worker.terminated, 1);
    saved({ data: { kind: 'done', reply: { tag: request(14, 'lualatex').tag, sourceMapGeometryPayload: new Uint8Array([9]) } } });
    assert.equal(h.api.observations().active, 0);
    const timed = h.api.execute(request(12, 'latex-dvi', 'dvipdfmx'));
    h.timeoutPhase(); assert.equal((await timed).outcome, 'transport-timeout');
    assert.equal(h.api.observations().started, h.api.observations().settled);
    assert.equal(h.stats.urls.length, h.stats.revoked.length); h.api.dispose();
    console.log(`Navigation transport modeled PASS: ${admitted} exact cells, disjoint DVI tokens, bounded optional data, ownership, physical retirement and unchanged closed exclusions.`);
})().catch(error => { console.error(error); process.exitCode = 1; });
