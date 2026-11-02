// Focused real-WASM converter tests. No mock engine or authored DVI grammar.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const [candidate, resources, fixture, results] = process.argv.slice(2).map(p => path.resolve(p));
const create = require(path.join(candidate, 'oc-dvi.js'));
const wasmBinary = fs.readFileSync(path.join(candidate, 'oc-dvi.wasm'));
const dvi = fs.readFileSync(fixture);
const resourceNames = fs.readdirSync(resources).filter(n => /\.(map|tfm|pfb|enc|vf|otf|ttf)$/.test(n) || ['glyphlist.txt', 'pdfglyphlist.txt'].includes(n));
const provisioned = [];
function alloc(m, bytes) {
  const ptr = m._malloc(Math.max(1, bytes.length));
  assert.notEqual(ptr, 0);
  m.HEAPU8.set(bytes, ptr);
  return ptr;
}
function string(m, s) { return alloc(m, Buffer.from(s + '\0')); }
function text(m, ptr) {
  let end = ptr;
  while (m.HEAPU8[end]) ++end;
  return Buffer.from(m.HEAPU8.subarray(ptr, end)).toString('utf8');
}
function add(m, name, bytes, resource = 0) {
  const p = string(m, name), b = alloc(m, bytes);
  const code = m._oc_dvi_add_file(p, b, bytes.length, resource);
  m._free(p); m._free(b);
  return code;
}
function run(m, name) {
  const p = string(m, name), code = m._oc_dvi_run(p); m._free(p);
  return code;
}
async function fresh(seed = true) {
  const m = await create({wasmBinary, print: () => { throw new Error('Unexpected native stdout'); }, printErr: () => { throw new Error('Unexpected native stderr'); }});
  const expectedAbi = Number(process.env.DVI_EXPECTED_ABI || 1);
  assert.ok(expectedAbi === 1 || expectedAbi === 2);
  assert.equal(m._oc_dvi_abi_version(), expectedAbi);
  if (seed) for (const name of resourceNames) {
    const bytes = fs.readFileSync(path.join(resources, name));
    assert.equal(add(m, name, bytes, 1), 0, name);
  }
  return m;
}
function receipt(m, code) {
  return {code, status: m._oc_dvi_status(), nativeReturn: m._oc_dvi_native_return(),
    error: text(m, m._oc_dvi_error()), missing: text(m, m._oc_dvi_missing_file()),
    pdfBytes: m._oc_dvi_pdf_size(), logBytes: m._oc_dvi_log_size(), logTruncated: m._oc_dvi_log_truncated()};
}
function log(m) { return Buffer.from(m.HEAPU8.subarray(m._oc_dvi_log_data(), m._oc_dvi_log_data() + m._oc_dvi_log_size())); }
(async () => {
  fs.mkdirSync(results, {recursive: true});
  for (const name of resourceNames) {
    const b = fs.readFileSync(path.join(resources, name));
    provisioned.push({name, bytes: b.length, sha256: crypto.createHash('sha256').update(b).digest('hex')});
  }
  const receipts = [];
  let m = await fresh();
  const entry = 'тести/real input ;$not-a-command.dvi';
  assert.equal(add(m, entry, dvi), 0);
  // A preexisting caller PDF must never be reused as converter output.
  assert.equal(add(m, 'result.pdf', Buffer.from('%PDF-1.5\nSTALE')), 0);
  const code = run(m, entry);
  receipts.push({test: 'real DVI, Unicode/spaces/metacharacters, stale output', ...receipt(m, code)});
  fs.writeFileSync(path.join(results, 'convert.log'), log(m));
  console.log(receipts.at(-1));
  assert.equal(code, 0);
  const pdf = Buffer.from(m.HEAPU8.subarray(m._oc_dvi_pdf_data(), m._oc_dvi_pdf_data() + m._oc_dvi_pdf_size()));
  assert.ok(pdf.length > 1000 && pdf.subarray(0, 5).equals(Buffer.from('%PDF-')));
  assert.ok(pdf.subarray(-40).includes(Buffer.from('%%EOF')));
  fs.writeFileSync(path.join(results, 'example.pdf'), pdf);
  const firstNativeReturn = m._oc_dvi_native_return();
  const firstLog = log(m);
  assert.equal(run(m, entry), 3);
  assert.equal(m._oc_dvi_status(), 3);
  assert.equal(m._oc_dvi_pdf_size(), 0);
  assert.equal(m._oc_dvi_pdf_data(), 0);
  assert.equal(m._oc_dvi_native_return(), firstNativeReturn);
  assert.deepEqual(log(m), firstLog);
  receipts.push({test: 'second run latches used status and hides old PDF; native return/log preserved', passed: true});
  assert.equal(add(m, 'late.dvi', dvi), 3);
  m = await fresh();
  assert.equal(add(m, 'bad.dvi', Buffer.from('not a DVI file')), 0);
  assert.equal(add(m, 'result.pdf', pdf), 0);
  let c = run(m, 'bad.dvi');
  receipts.push({test: 'malformed input cannot expose stale PDF', ...receipt(m, c)});
  assert.equal(c, 5); assert.equal(m._oc_dvi_native_return(), 99); assert.equal(m._oc_dvi_pdf_size(), 0);
  m = await fresh(false);
  assert.equal(add(m, 'input.dvi', dvi), 0);
  c = run(m, 'input.dvi');
  receipts.push({test: 'missing fonts/maps fail offline', ...receipt(m, c)});
  fs.writeFileSync(path.join(results, 'missing-resources.log'), log(m));
  assert.notEqual(c, 0); assert.equal(m._oc_dvi_pdf_size(), 0);
  m = await fresh(false);
  for (const bad of ['../escape', '/etc/passwd', 'https://host/file', 'a/../b', 'a\\b', 'a//b', 'a\u0001b']) assert.equal(add(m, bad, dvi), 1, bad);
  assert.equal(add(m, 'sub/resource.tfm', dvi, 1), 1);
  assert.equal(add(m, 'limit', Buffer.alloc(8 * 1024 * 1024)), 0);
  assert.equal(add(m, 'over', Buffer.from('x')), 2);
  receipts.push({test: '8MiB project admission and path boundaries', passed: true});
  m = await fresh(false);
  const big = Buffer.alloc(32 * 1024 * 1024);
  const oversizedName = string(m, 'oversize');
  const tiny = alloc(m, Buffer.from('x'));
  // Invalid size must reject before attempting to read the tiny pointer.
  assert.equal(m._oc_dvi_add_file(oversizedName, tiny, big.length + 1, 1), 2);
  m._free(oversizedName); m._free(tiny);
  for (let i = 0; i < 3; ++i) assert.equal(add(m, 'resource' + i, big, 1), 0);
  assert.equal(add(m, 'resource-over', Buffer.from('x'), 1), 2);
  assert.equal(add(m, 'independent-project', Buffer.alloc(8 * 1024 * 1024)), 0);
  assert.equal(add(m, 'project-over', Buffer.from('x')), 2);
  receipts.push({test: '32MiB file / 96MiB resource caps; independent 8MiB project counter', passed: true});
  m = await fresh(false);
  for (let i = 0; i < 4096; ++i) assert.equal(add(m, 'file' + i, Buffer.alloc(0), i % 2), 0);
  assert.equal(add(m, 'file-over', Buffer.alloc(0)), 2);
  receipts.push({test: '4096 combined file admission cap', passed: true});
  m = await fresh(false);
  assert.equal(run(m, 'absent.dvi'), 1);
  assert.equal(run(m, 'absent.dvi'), 3);
  receipts.push({test: 'one attempt even after invalid run', passed: true});
  fs.writeFileSync(path.join(results, 'tests.json'), JSON.stringify({receipts, provisioned, pdf: {bytes: pdf.length, sha256: crypto.createHash('sha256').update(pdf).digest('hex')}}, null, 2) + '\n');
  console.log('PASS: real DVI conversion and focused negative cases');
})().catch(e => { console.error(e); process.exitCode = 1; });
