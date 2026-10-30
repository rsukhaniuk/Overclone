// Actual worker FFI validation, without booting or modeling a successful engine.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../../src/Client/wwwroot/js/compile-port-worker.js'), 'utf8');
const start = source.indexOf('function validateAuthoringV4Frame(');
const end = source.indexOf('async function execute(', start);
assert.ok(start >= 0 && end > start);
const validate = vm.runInNewContext(source.slice(start, end) + ';validateAuthoringV4Frame;', { TextEncoder, Uint8Array });
function frame(version, profile = 5) {
    const root = 'розділ/my thesis.tex', job = version === 9 ? 'result' : 'my thesis';
    const extensions = ['.pdf', '.aux', '.bbl', '.blg', '.log', '.toc', '.lof', '.lot', '.out'];
    if (version === 7) extensions.push('.dvi');
    if (version === 8) extensions.push('.xdv');
    return {
        operation: 'bibtex', tag: { version, profile, tool: 'bibtex' }, selectedRoot: root,
        mainEntry: version === 9 ? 'result.tex' : 'my thesis.tex',
        files: [{ name: root, bytes: new Uint8Array([1]) }, { name: job + '.aux', bytes: new Uint8Array([2]) }],
        outputs: extensions.map(ext => job + ext).concat(profile === 7 ? [job.replaceAll(' ', '_') + '-blx.bib'] : []),
        generatedInputNames: [job + '.aux'], maxOutputBytes: 8388608, maxLogBytes: 65536,
    };
}
for (const profile of [5, 6, 7]) for (const version of [6, 7, 8, 9]) {
    assert.doesNotThrow(() => validate(frame(version, profile)));
    const collision = frame(version, profile);
    collision.files.push({ name: collision.outputs[0], bytes: new Uint8Array([3]) });
    assert.throws(() => validate(collision), /FFI/);
    const missingAux = frame(version, profile); missingAux.files.pop();
    assert.throws(() => validate(missingAux), /FFI/);
    if (version !== 8) {
        const wrongFormat = frame(version, profile); wrongFormat.outputs.push('my thesis.xdv');
        assert.throws(() => validate(wrongFormat), /FFI/);
    }
}
for (const version of [6, 7, 8, 9]) {
    const fresh = frame(version, 7), job = version === 9 ? 'result' : 'my thesis';
    const blx = job.replaceAll(' ', '_') + '-blx.bib';
    fresh.files.push({ name: blx, bytes: new Uint8Array([3, 4]) });
    assert.throws(() => validate(fresh), /FFI/, 'captured project cannot impersonate generated BLX');
    fresh.generatedInputNames.push(blx);
    assert.doesNotThrow(() => validate(fresh), 'declared byte carry is admitted for the exact selected job');
    const unrelated = frame(version, 7); unrelated.outputs.push('other-blx.bib');
    assert.throws(() => validate(unrelated), /FFI/);
    const legacy = frame(version, 6); legacy.outputs.push(blx);
    assert.throws(() => validate(legacy), /FFI/, 'old profiles retain their original output allowlist');
    if (job.includes(' ')) {
        const guessed = frame(version, 7); guessed.outputs.push(job + '-blx.bib');
        assert.throws(() => validate(guessed), /FFI/, 'only the native biblatex control basename is admitted');
    }
}
const unknown = frame(8); unknown.outputs.push('my thesis.unapproved');
assert.throws(() => validate(unknown), /FFI/);
console.log('BibTeX worker FFI PASS: phase-specific DVI/XDV output namespaces, required AUX and collision refusal.');
