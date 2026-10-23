// Offline test tooling, not an application runtime/CLI adapter.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
async function convert(candidate, resources, fixture, beforeRun = () => {}) {
  const create = require(path.join(candidate, 'oc-dvi.js'));
  const m = await create({wasmBinary: fs.readFileSync(path.join(candidate, 'oc-dvi.wasm'))});
  function alloc(bytes) { const p = m._malloc(Math.max(1, bytes.length)); assert.notEqual(p, 0); m.HEAPU8.set(bytes, p); return p; }
  function string(s) { return alloc(Buffer.from(s + '\0')); }
  function text(ptr) { let end = ptr; while (m.HEAPU8[end]) ++end; return Buffer.from(m.HEAPU8.subarray(ptr, end)).toString('utf8'); }
  function add(name, bytes, resource) {
    const n = string(name), b = alloc(bytes);
    const code = m._oc_dvi_add_file(n, b, bytes.length, resource);
    m._free(n); m._free(b);
    assert.equal(code, 0, name);
  }
  for (const name of fs.readdirSync(resources)) add(name, fs.readFileSync(path.join(resources, name)), 1);
  add('input.bin', fs.readFileSync(fixture), 0);
  const entry = string('input.bin');
  beforeRun(m);
  const code = m._oc_dvi_run(entry);
  m._free(entry);
  const receipt = {code, nativeReturn: m._oc_dvi_native_return(), error: text(m._oc_dvi_error()), missing: text(m._oc_dvi_missing_file()), pdfBytes: m._oc_dvi_pdf_size(), logBytes: m._oc_dvi_log_size(), logTruncated: m._oc_dvi_log_truncated()};
  const pdf = Buffer.from(m.HEAPU8.subarray(m._oc_dvi_pdf_data(), m._oc_dvi_pdf_data() + m._oc_dvi_pdf_size()));
  const log = Buffer.from(m.HEAPU8.subarray(m._oc_dvi_log_data(), m._oc_dvi_log_data() + m._oc_dvi_log_size()));
  return {receipt, pdf, log};
}
module.exports = {convert};
if (require.main === module) {
  const [candidate, resources, fixture, output] = process.argv.slice(2).map(p => path.resolve(p));
  convert(candidate, resources, fixture).then(({receipt, pdf, log}) => {
    fs.writeFileSync(output + '.json', JSON.stringify(receipt, null, 2) + '\n');
    fs.writeFileSync(output + '.log', log);
    if (receipt.code === 0) fs.writeFileSync(output + '.pdf', pdf);
    else if (fs.existsSync(output + '.pdf')) fs.unlinkSync(output + '.pdf');
    console.log(receipt);
    process.exitCode = receipt.code === 0 ? 0 : 1;
  }).catch(e => { console.error(e); process.exitCode = 1; });
}
