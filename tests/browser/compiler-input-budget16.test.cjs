'use strict';
// MODELED transport admission only. Evaluate the actual source-owned bridge
// helpers; do not copy the validator, run WASM, or claim native/browser proof.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { createHash } = require('node:crypto');

const sourcePath = path.resolve(__dirname, '../../src/Client/wwwroot/js/compile-port-bridge.js');
const original = fs.readFileSync(sourcePath, 'utf8');
const boundary = '\nfunction ownNativeTag(';
const end = original.indexOf(boundary);
assert.ok(end > original.indexOf('function transportFrame(frame)'), 'actual helper boundary exists');
assert.equal(original.indexOf(boundary, end + 1), -1, 'unambiguous helper boundary');
// Preserve all producer helper definitions, including pB/pT, path aliases and
// native byteLength getters. Remove ESM syntax only; no function substitution.
const prefix = original.slice(0, end)
    .replace(/^import \{ createRestrictedWorkerAdmission \} from '\.\/restricted-worker-admission\.js';\r?\n/, '')
    .replace(/^export (?=function )/gm, '');
assert.notEqual(prefix.length, end, 'ESM syntax was removed');
const context = vm.createContext({ Uint8Array, ArrayBuffer, TextEncoder });
vm.runInContext(prefix + '\nglobalThis.admit = transportFrame;', context, { timeout: 1000, filename: sourcePath });
const admit = context.admit;

const MiB = 1024 * 1024, prefixName = '__oop_runtime__/';
// Real bounded typed-array views, never byteLength proxies/sentinels. Views
// may share a backing buffer, which is valid at this predicate-only boundary.
// Maximum logical frame bytes tested: 112MiB+1. Physical backing: 32MiB.
const backing = new Uint8Array(32 * MiB);
const bytes = length => {
    assert.ok(Number.isSafeInteger(length) && length >= 0 && length <= backing.length, 'test allocation bound');
    return backing.subarray(0, length);
};
let assertions = 0;
function expect(frame, accepted, label) {
    assert.equal(admit(frame), accepted, label); assertions++;
}
function frame(profile = 16, engine = 'latex-dvi') {
    const xetex = engine === 'xelatex';
    return { tag: { profile, version: xetex ? 21 : 24, engineId: engine, tool: 'dvipdfmx',
        ordinal: 1, epoch: 1, job: 'modeled-input-budget', nonce: 'owned-nonce', runtimeIdentity: 'a'.repeat(64) },
        bibliographyTool: 'none', loader: bytes(1), wasm: bytes(1),
        selectedRoot: 'тези/main.tex', mainEntry: xetex ? 'main.xdv' : 'main.dvi',
        files: [{ name: 'тези/main.tex', bytes: bytes(1) }, { name: xetex ? 'main.xdv' : 'main.dvi', bytes: bytes(1) }],
        runtimeResourceNames: [], generatedInputNames: [], outputs: ['main.pdf'],
        maxLogBytes: 262144, maxOutputBytes: 8 * MiB };
}
function work(frame, target) {
    assert.ok(target >= 2);
    let remaining = target - 2, index = 0;
    while (remaining) {
        const size = Math.min(8 * MiB, remaining);
        frame.files.push({ name: `project-${index++}.bin`, bytes: bytes(size) }); remaining -= size;
    }
    return frame;
}
function resources(frame, target) {
    let remaining = target, index = 0;
    while (remaining) {
        const size = Math.min(32 * MiB, remaining), name = prefixName + `font-${index++}.tfm`;
        frame.runtimeResourceNames.push(name); frame.files.push({ name, bytes: bytes(size) }); remaining -= size;
    }
    return frame;
}
function captured(frame, sourceCount) {
    // One selected source and one freshly generated DVI/XDV already exist.
    // Converter admission reserves that extra slot outside the source quota.
    for (let i = 1; i < sourceCount; i++) frame.files.push({ name: `source-${i}.tex`, bytes: bytes(0) });
    return frame;
}

test('exact P16 doubles only conversion project input; legacy profiles retain 8MiB', () => {
    for (const engine of ['latex-dvi', 'xelatex']) for (const profile of [13, 14, 15, 16]) {
        const maximum = (profile === 16 ? 16 : 8) * MiB;
        for (const delta of [-1, 0, 1])
            expect(work(frame(profile, engine), maximum + delta), delta <= 0, `${engine} P${profile} project ${maximum + delta}`);
        if (profile === 16) expect(work(frame(profile, engine), 8 * MiB + 1), true, `${engine} P16 crosses old cap`);
    }
});

test('8MiB PDF output and 96MiB aggregate runtime resources remain bounded', () => {
    for (const engine of ['latex-dvi', 'xelatex']) for (const profile of [13, 14, 15, 16]) {
        for (const delta of [-1, 0, 1]) {
            const value = frame(profile, engine); value.maxOutputBytes = 8 * MiB + delta;
            expect(value, delta <= 0, `${engine} P${profile} output ${value.maxOutputBytes}`);
            expect(resources(frame(profile, engine), 96 * MiB + delta), delta <= 0,
                `${engine} P${profile} resources ${96 * MiB + delta}`);
        }
        const maximum = (profile === 16 ? 16 : 8) * MiB;
        expect(resources(work(frame(profile, engine), maximum), 96 * MiB), true,
            `${engine} P${profile} exact project+resources ${maximum + 96 * MiB}`);
        expect(resources(work(frame(profile, engine), maximum), 96 * MiB + 1), false,
            `${engine} P${profile} combined aggregate above limit`);
    }
    for (const maximum of [0, -1, 1.5, NaN, Infinity, Number.MAX_SAFE_INTEGER + 1]) {
        const value = frame(); value.maxOutputBytes = maximum;
        expect(value, false, `unsafe/invalid output cap ${maximum}`);
    }
});

test('captured source quota plus converted slot and carried-input quotas stay finite', () => {
    for (const engine of ['latex-dvi', 'xelatex']) for (const profile of [13, 14, 15, 16]) {
        const maximum = profile === 13 ? 64 : 128;
        for (const delta of [-1, 0, 1])
            expect(captured(frame(profile, engine), maximum + delta), delta <= 0,
                `${engine} P${profile} ${maximum + delta} source files plus converted input`);
        for (const carried of [127, 128, 129]) {
            const value = captured(frame(profile, engine), maximum);
            for (let i = 0; i < carried; i++) {
                const name = `carry-${i}.aux`; value.generatedInputNames.push(name); value.files.push({ name, bytes: bytes(0) });
            }
            expect(value, carried <= 128, `${engine} P${profile} ${carried} carried with full source quota`);
        }
        const outputMaximum = profile === 13 ? 128 : 256;
        for (const delta of [-1, 0, 1]) {
            const value = frame(profile, engine);
            value.outputs = Array.from({ length: outputMaximum + delta }, (_, i) => `output-${i}.pdf`);
            expect(value, delta <= 0, `${engine} P${profile} declared outputs ${outputMaximum + delta}`);
        }
    }
});

test('profile16 is an exact finite tuple and unsafe numeric tags refuse', () => {
    for (const engine of ['latex-dvi', 'xelatex']) {
        for (const profile of [17, 0, 16.5, '16', undefined, NaN, Infinity, Number.MAX_SAFE_INTEGER + 1]) {
            const candidate = frame(16, engine); candidate.tag.profile = profile;
            expect(candidate, false, `${engine} unrecognized profile ${profile}`);
        }
        for (const field of ['epoch', 'ordinal', 'version']) for (const value of [-1, 0, 1.5, NaN, Infinity, Number.MAX_SAFE_INTEGER + 1]) {
            const candidate = frame(16, engine); candidate.tag[field] = value;
            expect(candidate, false, `${engine} ${field}=${value}`);
        }
        const wrong = frame(16, engine); wrong.tag.version = engine === 'xelatex' ? 24 : 21;
        expect(wrong, false, `${engine} mixed converter version`);
        const snapshot = { captureId: 'capture', serviceId: 'service', sessionId: 'session', projectId: { value: 'project' },
            workingGeneration: 0, localWorkspaceEpoch: 0, selectionVersion: 0, baseHeadRevisionId: null };
        const valid = frame(16, engine); valid.tag.snapshot = snapshot;
        expect(valid, true, `${engine} current finite snapshot`);
        for (const field of ['workingGeneration', 'localWorkspaceEpoch', 'selectionVersion']) for (const value of [-1, 0.5, NaN, Number.MAX_SAFE_INTEGER + 1]) {
            const candidate = frame(16, engine); candidate.tag.snapshot = { ...snapshot, [field]: value };
            expect(candidate, false, `${engine} snapshot ${field}=${value}`);
        }
    }
});

test('duplicate aliases, mixed resource/generated ownership and unsafe paths refuse', () => {
    const mutations = [
        ['duplicate path', f => f.files.push({ ...f.files[0] })],
        ['case alias', f => f.files.push({ name: 'ТЕЗИ/MAIN.TEX', bytes: bytes(0) })],
        ['NFC alias', f => f.files.push({ name: 'café.tex', bytes: bytes(0) }, { name: 'cafe\u0301.tex', bytes: bytes(0) })],
        ['file/directory collision', f => f.files.push({ name: 'тези', bytes: bytes(0) })],
        ['duplicate output alias', f => f.outputs.push('MAIN.PDF')],
        ['captured output collision', f => f.files.push({ name: 'MAIN.PDF', bytes: bytes(0) })],
        ['unsafe path', f => f.files.push({ name: '../outside.tex', bytes: bytes(0) })],
        ['undeclared runtime file', f => f.files.push({ name: prefixName + 'extra.tfm', bytes: bytes(0) })],
        ['mixed resource case', f => { resources(f, 1); f.runtimeResourceNames[0] = prefixName + 'FONT-0.TFM'; }],
        ['missing resource file', f => f.runtimeResourceNames.push(prefixName + 'missing.tfm')],
        ['duplicate resource declaration', f => { resources(f, 1); f.runtimeResourceNames.push(f.runtimeResourceNames[0]); }],
        ['resource/source namespace mismatch', f => f.runtimeResourceNames.push(f.selectedRoot)],
        ['mixed resource/carry ownership', f => { resources(f, 1); f.generatedInputNames.push(f.runtimeResourceNames[0]); }],
        ['missing carry file', f => f.generatedInputNames.push('missing.aux')],
        ['mixed carry case', f => { f.files.push({ name: 'carry.aux', bytes: bytes(1) }); f.generatedInputNames.push('CARRY.AUX'); }],
        ['selected root cannot be carry', f => f.generatedInputNames.push(f.selectedRoot)],
        ['duplicate carry declaration', f => { f.files.push({ name: 'carry.aux', bytes: bytes(1) }); f.generatedInputNames.push('carry.aux', 'carry.aux'); }],
    ];
    for (const engine of ['latex-dvi', 'xelatex']) for (const profile of [13, 14, 15, 16]) for (const [name, mutate] of mutations) {
        const value = frame(profile, engine); mutate(value);
        expect(value, false, `${engine} P${profile} ${name}`);
    }
});

test.after(() => {
    const hash = value => createHash('sha256').update(value).digest('hex');
    console.log(JSON.stringify({ evidence: 'modeled-transport-admission', scope: 'MODELED actual bridge transportFrame; no native/WASM/browser execution',
        assertions, bridgeSha256: hash(original), evaluatedHelperSha256: hash(prefix), backingBytes: backing.byteLength,
        maximumLogicalFrameBytes: 112 * MiB + 1 }));
});
