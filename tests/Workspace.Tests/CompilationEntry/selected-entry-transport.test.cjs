// MODELED browser APIs around the actual product bridge/admission code.
// No worker/native execution; complements C# late-operation admission checks.
const assert = require('node:assert/strict');
const { harness, frame } = require('../../browser/restricted-worker-test-helpers.cjs');
function selected(job, root) {
    const f = frame(5, job), leaf = root.slice(root.lastIndexOf('/') + 1), name = leaf.slice(0, -4);
    return { ...f, selectedRoot: root, mainEntry: root.includes(' ') ? '"' + root + '"' : root,
        files: [{ name: root, bytes: new Uint8Array([239,187,191,65,13,10]) }],
        outputs: ['.pdf','.aux','.bbl','.blg','.log','.toc','.lof','.lot','.out'].map(ext => name + ext) };
}
(async () => {
    const h = harness(); await h.api.provision();
    for (const bad of [{ version: 6 }, { profile: 1 }, { runtimeIdentity: null }, { runtimeIdentity: 'A'.repeat(64) }]) {
        const f = selected('bad', 'dir/paper.tex'); Object.assign(f.tag, bad);
        assert.throws(() => h.api.execute(f));
    }
    assert.equal(h.api.observations().started, 0); assert.equal(h.stats.urls.length, 0);
    for (const root of ['my thesis.tex', 'dir/my thesis.tex']) {
        for (const tool of ['latex', 'bibtex']) {
            const f = selected(root + '/' + tool, root); f.tag.tool = tool;
            if (tool === 'bibtex') {
                f.mainEntry = 'my thesis.tex';
                f.files.push({ name: 'my thesis.aux', bytes: new Uint8Array([65,85,88]) });
            }
            const result = h.api.execute(f), owner = h.stats.workers.at(-1);
            owner.onmessage({ data: { kind: 'ready' } }); const sent = owner.messages[1];
            assert.equal(sent.selectedRoot, root); assert.equal(sent.mainEntry, tool === 'latex' ? '"' + root + '"' : 'my thesis.tex');
            assert.equal(sent.files[0].name, root); assert.equal(sent.outputs[0], 'my thesis.pdf');
            assert.deepEqual(Array.from(sent.files[0].bytes), Array.from(f.files[0].bytes));
            if (tool === 'bibtex') assert.equal(sent.files[1].name, 'my thesis.aux');
            h.api.terminate(f.tag.job, f.tag.epoch); assert.equal((await result).outcome, 'cancelled');
            assert.equal(owner.terminated, 1);
        }
    }
    const a = selected('a-original', 'space dir/paper.tex'), aWait = h.api.execute(a), oldWorker = h.stats.workers.at(-1), old = oldWorker.onmessage;
    const b = selected('b', 'тези/праця.tex'), bWait = h.api.execute(b), middleWorker = h.stats.workers.at(-1), middle = middleWorker.onmessage;
    assert.throws(() => h.api.execute(selected('a-too-early', a.selectedRoot)), /operation bound/);
    h.api.terminate(a.tag.job, 999); assert.equal(h.api.observations().active, 2, 'wrong epoch cannot cancel');
    h.api.terminate(a.tag.job, a.tag.epoch); assert.equal((await aWait).outcome, 'cancelled');
    assert.equal(oldWorker.terminated, 1); assert.equal(h.api.observations().active, 1);
    const newest = selected('a-newest', a.selectedRoot), latest = h.api.execute(newest), worker = h.stats.workers.at(-1), current = worker.onmessage;
    old({ data: { kind: 'ready' } }); old({ data: { kind: 'done', reply: { tag: a.tag, outcome: 'native-return' } } });
    assert.equal(oldWorker.messages.length, 1, 'cancel before ready cannot enter even via saved callback');
    current({ data: { kind: 'ready' } }); current({ data: { kind: 'ready' } });
    assert.equal(worker.messages.length, 2); const wire = worker.messages[1];
    assert.equal(wire.selectedRoot, newest.selectedRoot); assert.equal(wire.mainEntry, newest.mainEntry);
    assert.deepEqual(Array.from(wire.files[0].bytes), Array.from(newest.files[0].bytes));
    assert.equal(wire.files[0].name, newest.selectedRoot); assert.equal(wire.tag, newest.tag);
    h.api.terminate(b.tag.job, b.tag.epoch); assert.equal((await bWait).outcome, 'cancelled');
    middle({ data: { kind: 'done', reply: { tag: b.tag, outcome: 'native-return' } } });
    assert.equal(middleWorker.terminated, 1); assert.equal(h.api.observations().active, 1);
    h.api.terminate(newest.tag.job, newest.tag.epoch); assert.equal((await latest).outcome, 'cancelled');
    current({ data: { kind: 'entered' } }); current({ data: { kind: 'ready' } });
    assert.equal(worker.messages.length, 2); assert.equal(worker.terminated, 1);
    assert.equal(h.api.observations().active, 0); assert.equal(h.api.observations().started, h.api.observations().settled);
    assert.equal(h.stats.urls.length, h.stats.revoked.length); assert.equal(h.timers.size, 0);
    h.api.dispose(); assert.equal(h.sw.count(), 0); assert.equal(h.controller.count(), 0);
    console.log('Selected-root transport MODELED contracts PASS; actual bridge only, no native/browser acceptance.');
})().catch(error => { console.error(error); process.exitCode = 1; });
