// Real converter-output/renderer compatibility check. Supply genuine generated
// PDFs and the exact approved renderer directory; no authored PDF substitute.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const [renderer, document, expectedJson] = process.argv.slice(2);
(async () => {
  const expected = JSON.parse(expectedJson);
  const m = await require(path.resolve(renderer, 'e1.js'))({
    wasmBinary: fs.readFileSync(path.resolve(renderer, 'e1.wasm')),
    print() {}, printErr() {}
  });
  const put = bytes => {
    const handle = m._e1_alloc(bytes.length);
    assert.notEqual(handle, 0);
    m.HEAPU8.set(bytes, m._e1_data(handle));
    return handle;
  };
  const take = () => {
    const result = m._e1_result(), length = m._e1_size(result);
    const copy = m._e1_alloc(length);
    assert.equal(m._e1_copy(result, copy), 0);
    const bytes = Buffer.from(m.HEAPU8.slice(m._e1_data(copy), m._e1_data(copy) + length));
    assert.equal(m._e1_release(copy), 0);
    assert.equal(m._e1_release(result), 0);
    return bytes;
  };
  const limits = Buffer.alloc(36);
  [1, 36, 8388608, 512, 100000, 256, 4000000, 16777216, 64]
    .forEach((value, index) => limits.writeUInt32LE(value, index * 4));
  const input = put(fs.readFileSync(document)), budget = put(limits);
  assert.equal(m._e1_open(input, budget), 0);
  const doc = m._e1_result();
  assert.equal(m._e1_document_pages(doc), expected.length);
  const pages = [];
  for (let index = 0; index < expected.length; index++) {
    assert.equal(m._e1_page(doc, index), 0);
    const page = m._e1_result();
    assert.equal(m._e1_facts(page), 0, 'native page-box facts');
    const facts = JSON.parse(take().toString('utf8'));
    for (let axis = 0; axis < 2; axis++)
      assert.ok(Math.abs(facts.displayPoints[axis] - expected[index][axis]) < 0.02,
        `page ${index + 1}, axis ${axis}: ${facts.displayPoints[axis]}`);
    assert.equal(m._e1_text(page), 0, 'native text extraction');
    const text = take();
    assert.ok(text.length > 0);
    pages.push({page: index + 1, dimensions: facts.displayPoints, textBytes: text.length});
    assert.equal(m._e1_close(page), 0);
  }
  assert.equal(m._e1_close(doc), 0);
  assert.equal(m._e1_release(input), 0);
  assert.equal(m._e1_release(budget), 0);
  console.log(JSON.stringify({passed: true, pages}));
})().catch(error => { console.error(error); process.exitCode = 1; });
