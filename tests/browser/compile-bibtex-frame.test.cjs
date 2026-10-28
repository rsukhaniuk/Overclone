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
function frame(version) {
    const root = 'розділ/my thesis.tex', job = version === 9 ? 'result' : 'my thesis';
    const extensions = ['.pdf', '.aux', '.bbl', '.blg', '.log', '.toc', '.lof', '.lot', '.out'];
    if (version === 7) extensions.push('.dvi');
    if (version === 8) extensions.push('.xdv');
    return {
        operation: 'bibtex', tag: { version, profile: 5, tool: 'bibtex' }, selectedRoot: root,
        mainEntry: version === 9 ? 'result.tex' : 'my thesis.tex',
        files: [{ name: root, bytes: new Uint8Array([1]) }, { name: job + '.aux', bytes: new Uint8Array([2]) }],
        outputs: extensions.map(ext => job + ext), maxOutputBytes: 8388608, maxLogBytes: 65536,
    };
}
for (const version of [6, 7, 8, 9]) {
    assert.doesNotThrow(() => validate(frame(version)));
    const collision = frame(version);
    collision.files.push({ name: collision.outputs[0], bytes: new Uint8Array([3]) });
    assert.throws(() => validate(collision), /FFI/);
    const missingAux = frame(version); missingAux.files.pop();
    assert.throws(() => validate(missingAux), /FFI/);
    if (version !== 8) {
        const wrongFormat = frame(version); wrongFormat.outputs.push('my thesis.xdv');
        assert.throws(() => validate(wrongFormat), /FFI/);
    }
}
const unknown = frame(8); unknown.outputs.push('my thesis.unapproved');
assert.throws(() => validate(unknown), /FFI/);
console.log('BibTeX worker FFI PASS: phase-specific DVI/XDV output namespaces, required AUX and collision refusal.');
