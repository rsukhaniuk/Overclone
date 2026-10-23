// Real 60,000-page TeX-produced DVI exercises output/log limits in real engine.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const [candidate, resources, fixture, result] = process.argv.slice(2).map(p => path.resolve(p));
const create = require(path.join(candidate, 'oc-dvi.js'));
(async () => {
  const m = await create({wasmBinary: fs.readFileSync(path.join(candidate, 'oc-dvi.wasm'))});
  function alloc(b) { const p = m._malloc(Math.max(1, b.length)); assert.notEqual(p, 0); m.HEAPU8.set(b, p); return p; }
  function string(s) { return alloc(Buffer.from(s + '\0')); }
  function add(name, b, resource) {
    const n = string(name), p = alloc(b);
    assert.equal(m._oc_dvi_add_file(n, p, b.length, resource), 0, name);
    m._free(n); m._free(p);
  }
  for (const name of fs.readdirSync(resources)) add(name, fs.readFileSync(path.join(resources, name)), 1);
  add('cap.dvi', fs.readFileSync(fixture), 0);
  const p = string('cap.dvi');
  const code = m._oc_dvi_run(p);
  const receipt = {code, nativeReturn: m._oc_dvi_native_return(), pdfBytes: m._oc_dvi_pdf_size(), logBytes: m._oc_dvi_log_size(), logTruncated: m._oc_dvi_log_truncated(), heapBytes: m.HEAPU8.length};
  fs.writeFileSync(result, JSON.stringify(receipt, null, 2) + '\n');
  console.log(receipt);
  assert.equal(code, 2, 'real output exceeds 8MiB and latches failure');
  assert.equal(receipt.pdfBytes, 0, 'no partial output released');
  assert.equal(receipt.logBytes, 262144);
  assert.equal(receipt.logTruncated, 1);
  assert.ok(receipt.heapBytes <= 536870912);
})().catch(e => { console.error(e); process.exitCode = 1; });
