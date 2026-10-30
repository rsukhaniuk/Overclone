// MODELED browser/controller/lifecycle contracts for the actual shared helper
// and compile bridge. Fake Workers/replies are NOT native/CSP compatibility proof.
const assert = require('node:assert/strict');
const { harness, frame, tick, origin } = require('./restricted-worker-test-helpers.cjs');
function assertPristine(h) {
    const state = h.api.observations(); assert.equal(state.started, 0); assert.equal(state.active, 0); assert.equal(state.trace.length, 0);
    assert.equal(h.stats.urls.length, 0); assert.equal(h.stats.workers.length, 0); assert.equal(h.timers.size, 0);
}
function assertDisposed(h) {
    h.api.dispose(); assert.equal(h.sw.count(), 0); assert.equal(h.controller.count(), 0); assert.equal(h.timers.size, 0);
    assert.ok(h.stats.ports.every(port => port.closed === 1));
}
const deadline = setTimeout(() => { console.error('Bridge admission test deadline exceeded'); process.exit(1); }, 10000);
(async () => {
    for (const options of [{ noController: true }, { badScope: true }, { body: Buffer.from('corrupt source') }, { body: Buffer.alloc(32769) },
        { changeReply: r => ({ ...r, protocol: 2 }) }, { changeReply: r => ({ ...r, requestId: 'b'.repeat(32) }) },
        { changeReply: r => ({ ...r, policy: "script-src * 'unsafe-eval'" }) }, { changeReply: r => ({ ...r, entry: 'blob:unapproved' }) },
        { changeReply: r => ({ ...r, kind: 'pdf' }) }, { changeReply: r => ({ ...r, source: '/js/pdf-port-worker.js' }) },
        { changeReply: r => ({ ...r, extra: true }) }, { dropReply: true }]) {
        const h = harness(options); await assert.rejects(h.api.provision()); assertPristine(h); assertDisposed(h);
    }
    const activating = harness({ activating: true });
    const pending = activating.api.provision(); await tick();
    assert.equal(activating.stats.messages, 0, 'no request before actual activated state');
    activating.controller.state = 'activated'; activating.controller.emit('statechange');
    assert.equal(await pending, true); assertDisposed(activating);
    const stuck = harness({ activating: true }); await assert.rejects(stuck.api.provision()); assertPristine(stuck); assertDisposed(stuck);
    const changed = harness({ dropReply: true });
    const changePending = changed.api.provision(); await tick(); changed.changeController();
    await assert.rejects(changePending); assertPristine(changed); assertDisposed(changed);
    const disposing = harness({ dropReply: true });
    const disposePending = disposing.api.provision(); await tick(); disposing.api.dispose();
    await assert.rejects(disposePending); assertPristine(disposing); assertDisposed(disposing);

    const abandoned = harness();
    abandoned.sw.getRegistration = () => { abandoned.changeController(); return Promise.reject(new Error('modeled late registration rejection')); };
    await assert.rejects(abandoned.api.provision()); await tick(); assertPristine(abandoned); assertDisposed(abandoned);

    const h = harness();
    const a = h.api.provision(), b = h.api.provision(); assert.equal(a, b); await a;
    assert.equal(h.stats.messages, 1); assert.equal(h.stats.fetches, 1); assert.equal(h.stats.urls.length, 0);
    for (const version of [1, 2, 3]) {
        const request = frame(version, 'phase-' + version), result = h.api.execute(request), worker = h.stats.workers.at(-1);
        assert.ok(worker.url.startsWith(origin + '/__oop/native-worker/compile?'));
        assert.ok(!worker.url.startsWith('blob:'));
        assert.equal(h.stats.urls.length, version, 'one owned loader URL, never a source Blob');
        assert.equal(worker.messages[0].operation, 'boot');
        const savedCallback = worker.onmessage;
        savedCallback({ data: { kind: 'ready' } }); savedCallback({ data: { kind: 'ready' } });
        assert.equal(worker.messages.length, 2, 'only one phase dispatch');
        assert.deepEqual(JSON.parse(JSON.stringify(worker.messages[1].tag)), request.tag);
        savedCallback({ data: { kind: 'done', reply: { tag: request.tag, outcome: 'native-return', nativeReturn: 0 } } });
        assert.equal((await result).nativeReturn, 0); savedCallback({ data: { kind: 'ready' } });
        assert.equal(worker.messages.length, 2, 'late saved callbacks cannot dispatch');
        assert.deepEqual(h.stats.order.slice(-2), ['terminate', 'revoke']);
    }
    const before4 = { started: h.api.observations().started, urls: h.stats.urls.length };
    for (const tag of [{ profile: 0 }, { profile: 1 }, { profile: 3 }, { runtimeIdentity: null },
        { runtimeIdentity: 'A'.repeat(64) }, { runtimeIdentity: 'a'.repeat(63) }, { version: 6 }]) {
        const bad = frame(4, 'bad4'); Object.assign(bad.tag, tag); assert.throws(() => h.api.execute(bad));
    }
    assert.equal(h.api.observations().started, before4.started); assert.equal(h.stats.urls.length, before4.urls);
    const explicit4 = frame(4, 'phase4'), wire4 = h.api.execute(explicit4), worker4 = h.stats.workers.at(-1);
    worker4.onmessage({ data: { kind: 'ready' } });
    assert.deepEqual(JSON.parse(JSON.stringify(worker4.messages[1].tag)), explicit4.tag); assert.equal(worker4.messages[1].tag.version, 4);
    assert.equal(worker4.messages[1].tag.profile, 2); h.api.terminate('phase4', 1);
    assert.equal((await wire4).outcome, 'cancelled'); // wire admission only, never actual worker4 behavior

    const held = h.api.execute(frame(1, 'held'));
    assert.throws(() => h.api.execute(frame(1, 'held')));
    const other = h.api.execute(frame(1, 'other'));
    assert.throws(() => h.api.execute(frame(1, 'third')));
    h.api.terminate('held', 1); assert.equal((await held).outcome, 'cancelled');
    h.timeoutPhase(); assert.equal((await other).outcome, 'transport-timeout');
    for (let i = 0; i < 70; i++) {
        const result = h.api.execute(frame(1, 'trace-' + i));
        h.stats.workers.at(-1).onerror(); assert.equal((await result).outcome, 'worker-error');
    }
    assert.equal(h.api.observations().trace.length, 64);
    assert.equal(h.api.observations().active, 0); assert.equal(h.api.observations().started, h.api.observations().settled);
    assert.equal(h.stats.urls.length, h.stats.revoked.length); assertDisposed(h);

    const failed = harness({ workerThrows: true }); await failed.api.provision();
    assert.throws(() => failed.api.execute(frame()));
    assert.equal(failed.stats.urls.length, 1); assert.equal(failed.stats.revoked.length, 1);
    assert.equal(failed.api.observations().started, 0); assert.equal(failed.api.observations().trace.length, 0); assertDisposed(failed);
    const revoke = harness({ workerThrows: true, revokeThrows: true }); await revoke.api.provision();
    assert.throws(() => revoke.api.execute(frame()), /startup and URL cleanup failed/);
    assert.equal(revoke.stats.revoked.length, 1); assert.equal(revoke.api.observations().started, 0); assertDisposed(revoke);
    const invalidated = harness(); await invalidated.api.provision(); invalidated.changeController();
    assert.throws(() => invalidated.api.execute(frame())); assertPristine(invalidated); assertDisposed(invalidated);
    const live = harness(); await live.api.provision(); const running = live.api.execute(frame()); live.changeController();
    assert.equal((await running).outcome, 'worker-error'); assert.equal(live.stats.workers[0].terminated, 1);
    assert.equal(live.stats.revoked.length, 1); assert.equal(live.stats.messages, 1, 'never auto-retry contract'); assertDisposed(live);
    const dvi = harness(), conversion = frame(7, 'convert');
    Object.assign(conversion.tag, { profile: 4, engineId: 'latex-dvi', tool: 'dvipdfmx' });
    conversion.runtimeResourceNames = ['cmr10.tfm'];
    conversion.mainEntry = 'main.dvi';
    conversion.files.push({ name: 'main.dvi', bytes: new Uint8Array([247, 2]) }, { name: 'cmr10.tfm', bytes: new Uint8Array([5]) });
    assert.throws(() => dvi.api.execute(conversion)); assertPristine(dvi);
    await dvi.api.provisionDvi(); assert.deepEqual(dvi.stats.kinds, ['dvi']);
    const converted = dvi.api.execute(conversion), dviWorker = dvi.stats.workers.at(-1);
    assert.ok(dviWorker.url.startsWith(origin + '/__oop/native-worker/dvi?'));
    dviWorker.onmessage({ data: { kind: 'ready' } });
    assert.deepEqual(dviWorker.messages[1].runtimeResourceNames, conversion.runtimeResourceNames);
    assert.throws(() => dvi.api.execute(frame()), 'converter admission cannot authorize ordinary compilation');
    await dvi.api.provision();
    const compileFrame = frame(7, 'dvi-compile'); Object.assign(compileFrame.tag, { profile: 4, engineId: 'latex-dvi' });
    const compiled = dvi.api.execute(compileFrame);
    assert.ok(dvi.stats.workers.at(-1).url.startsWith(origin + '/__oop/native-worker/compile?'));
    dvi.changeController();
    assert.equal((await converted).outcome, 'worker-error'); assert.equal((await compiled).outcome, 'worker-error');
    assert.ok(dvi.stats.workers.every(worker => worker.terminated === 1));
    assert.equal(dvi.stats.urls.length, dvi.stats.revoked.length); assertDisposed(dvi);
    for (const profile of [5, 6, 7]) for (const [version, engineId, kind, provision] of [[6, 'pdftex', 'compile', 'provision'], [7, 'latex-dvi', 'compile', 'provision'], [8, 'xelatex', 'xetex', 'provisionXetex'], [9, 'lualatex', 'luatex', 'provisionLuatex']]) {
        const h = harness(), request = frame(version, kind);
        Object.assign(request.tag, { profile, engineId, tool: 'latex' });
        request.runtimeResourceNames = ['__oop_runtime__/geometry.sty'];
        request.generatedInputNames = ['chapters/one.aux'];
        request.files.push({ name: '__oop_runtime__/geometry.sty', bytes: new Uint8Array([5]) },
            { name: 'chapters/one.tex', bytes: new Uint8Array([6]) }, { name: 'chapters/one.aux', bytes: new Uint8Array([7]) });
        request.outputs.push('chapters/one.aux');
        if (profile === 7) request.outputs.push((version === 9 ? 'result' : 'main') + '-blx.bib');
        assert.throws(() => h.api.execute(request)); assertPristine(h);
        await h.api[provision]();
        assert.deepEqual(h.stats.kinds, [kind]);
        const unknown = { ...request, tag: { ...request.tag, profile: 99 } };
        assert.throws(() => h.api.execute(unknown), 'unknown bundle profile must remain rejected');
        const running = h.api.execute(request), worker = h.stats.workers.at(-1);
        assert.ok(worker.url.startsWith(origin + '/__oop/native-worker/' + kind + '?'));
        worker.onmessage({ data: { kind: 'ready' } });
        assert.deepEqual(worker.messages[1].generatedInputNames, request.generatedInputNames);
        assert.deepEqual(worker.messages[1].runtimeResourceNames, request.runtimeResourceNames);
        const bib = frame(version, kind + '-bib'); Object.assign(bib.tag, { profile, engineId, tool: 'bibtex' });
        if (kind !== 'compile') assert.throws(() => h.api.execute(bib), 'native engine admission cannot authorize BibTeX role');
        h.changeController();
        assert.equal((await running).outcome, 'worker-error');
        assert.equal(worker.terminated, 1); assertDisposed(h);
    }
    console.log('Compile bridge modeled contracts PASS: fixed native role admission, unchanged legacy tags, suite8/9 routing and resource/state wire fields, shared cancellation/ownership bounds.');
})().catch(error => { console.error(error); process.exitCode = 1; }).finally(() => clearTimeout(deadline));
