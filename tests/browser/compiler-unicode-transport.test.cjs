// Actual bridge/admission with modeled Worker/controller; not native/browser proof.
'use strict';
const assert = require('node:assert/strict');
const { harness, frame, origin } = require('./restricted-worker-test-helpers.cjs');

function request(version, engineId, tool = 'latex', profile = 12) {
  const value = frame(version, `${profile}-${version}-${engineId}-${tool}`);
  Object.assign(value.tag, { profile, version, engineId, tool, ordinal: 3 });
  if (tool === 'dvipdfmx') {
    value.mainEntry = 'main' + (engineId === 'xelatex' ? '.xdv' : '.dvi');
    value.files.push({ name: value.mainEntry, bytes: new Uint8Array([247, engineId === 'xelatex' ? 7 : 2]) });
  }
  return value;
}

function settle(h, value, extra = {}) {
  const operation = h.api.execute(value), worker = h.stats.workers.at(-1);
  worker.onmessage({ data: { kind: 'ready' } });
  worker.onmessage({ data: { kind: 'done', reply: { tag: value.tag, outcome: 'native-return',
    nativeReturn: 0, entered: true, log: '', files: [{ name: value.outputs[0],
      bytes: new Uint8Array([37, 80, 68, 70, 45]) }], inputs: [], ...extra } } });
  return { operation, worker };
}

(async () => {
  const h = harness();
  await h.api.provision(); await h.api.provisionDvi();
  await h.api.provisionXetex(); await h.api.provisionLuatex();
  for (const [phase, engine, role] of [[6, 'pdftex', 'compile'], [15, 'latex-dvi', 'compile'],
    [21, 'xelatex', 'xetex'], [22, 'lualatex', 'luatex']]) {
    for (const tool of ['latex', 'bibtex']) {
      const { operation, worker } = settle(h, request(phase, engine, tool));
      assert.equal((await operation).nativeReturn, 0);
      assert.ok(worker.url.startsWith(origin + '/__oop/native-worker/' + (tool === 'bibtex' ? 'compile' : role) + '?'));
      assert.equal(worker.terminated, 1);
    }
  }
  for (const [phase, engine] of [[15, 'latex-dvi'], [21, 'xelatex']])
    assert.equal((await settle(h, request(phase, engine, 'dvipdfmx')).operation).nativeReturn, 0);
  const before = h.stats.workers.length;
  for (const [phase, engine, profile] of [[16, 'xelatex', 12], [17, 'lualatex', 12],
    [21, 'lualatex', 12], [22, 'xelatex', 12], [21, 'xelatex', 10], [22, 'lualatex', 10],
    [6, 'pdftex', 11], [18, 'latex-dvi', 11], [19, 'xelatex', 11], [20, 'lualatex', 11],
    [6, 'pdftex', 13], [23, 'xelatex', 12], [10, 'pdftex', 12], [11, 'pdftex', 12]])
    assert.throws(() => h.api.execute(request(phase, engine, 'latex', profile)));
  assert.equal(h.stats.workers.length, before, 'mixed/reserved tuples must refuse before Worker allocation');
  const map = new Uint8Array([1, 2]), geometry = new Uint8Array([3, 4]);
  for (const [phase, engine, tool, extra] of [[21, 'xelatex', 'latex', { sourceMapPayload: map }],
    [22, 'lualatex', 'latex', { sourceMapPayload: map, sourceMapGeometryPayload: geometry }],
    [21, 'xelatex', 'dvipdfmx', { sourceMapGeometryPayload: geometry }]]) {
    const result = await settle(h, request(phase, engine, tool), extra).operation;
    if (extra.sourceMapPayload) assert.deepEqual([...result.sourceMapPayload], [...map]);
    if (extra.sourceMapGeometryPayload) assert.deepEqual([...result.sourceMapGeometryPayload], [...geometry]);
  }
  for (const extra of [{ sourceMapGeometryPayload: geometry }, { sourceMapPayload: map, nativeReturn: 1 }])
    assert.equal((await settle(h, request(21, 'xelatex'), extra).operation).outcome, 'protocol-failure');
  assert.equal(h.api.observations().started, h.api.observations().settled);
  assert.equal(h.stats.urls.length, h.stats.revoked.length);
  h.api.dispose();
  console.log('P12 modeled bridge PASS: exact 6/15/21/22 producer/backend/converter cells, reserved/mixed refusals and bounded map channels.');
})().catch(error => { console.error(error); process.exitCode = 1; });
