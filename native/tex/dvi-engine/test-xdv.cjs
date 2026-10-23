// Genuine retained upstream XDV, including /tex/ font aliases. This does NOT
// replace acceptance with fresh XDV from the current XeTeX worker.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const {convert} = require('./convert-fixture.cjs');
const [candidate, resources, fixture, output] = process.argv.slice(2).map(p => path.resolve(p));
(async () => {
  fs.mkdirSync(output, {recursive: true});
  const fontNames = fs.readdirSync(resources).filter(n => n.endsWith('.otf'));
  assert.equal(fontNames.length, 6);
  const positive = await convert(candidate, resources, fixture);
  assert.equal(positive.receipt.code, 0);
  fs.writeFileSync(path.join(output, 'retained-xdv.pdf'), positive.pdf);
  fs.writeFileSync(path.join(output, 'retained-xdv.log'), positive.log);
  const withoutFonts = path.join(output, 'nonfont-resources');
  fs.mkdirSync(withoutFonts);
  for (const name of fs.readdirSync(resources).filter(n => !n.endsWith('.otf'))) fs.symlinkSync(path.join(resources, name), path.join(withoutFonts, name));
  // /tex/ alias MUST NOT fall back to a same-basename project font.
  const negative = await convert(candidate, withoutFonts, fixture, m => {
    for (const name of fontNames) {
      const b = fs.readFileSync(path.join(resources, name));
      const n = Buffer.from(name + '\0'), p = m._malloc(n.length), data = m._malloc(b.length);
      m.HEAPU8.set(n, p); m.HEAPU8.set(b, data);
      assert.equal(m._oc_dvi_add_file(p, data, b.length, 0), 0);
      m._free(p); m._free(data);
    }
  });
  assert.equal(negative.receipt.code, 5);
  assert.equal(negative.pdf.length, 0);
  fs.writeFileSync(path.join(output, 'resource-only-refusal.log'), negative.log);
  // Keep real XDV structure; mutate only exact 5-byte virtual path prefix.
  const badPath = path.join(output, 'arbitrary-absolute.xdv');
  const bytes = fs.readFileSync(fixture);
  const mutated = Buffer.from(bytes);
  for (let i = 0; i <= mutated.length - 5; ++i) if (mutated.subarray(i, i + 5).equals(Buffer.from('/tex/'))) mutated.set(Buffer.from('/etc/'), i);
  fs.writeFileSync(badPath, mutated);
  const absolute = await convert(candidate, resources, badPath);
  assert.equal(absolute.receipt.code, 5);
  assert.equal(absolute.pdf.length, 0);
  const receipt = {
    provenance: 'retained upstream test.xdv; fresh current-worker XDV acceptance remains separate',
    inputSha256: crypto.createHash('sha256').update(bytes).digest('hex'),
    positive: positive.receipt, noProjectFallback: negative.receipt, arbitraryAbsoluteRefusal: absolute.receipt,
    resources: fs.readdirSync(resources).map(name => {
      const b = fs.readFileSync(path.join(resources, name));
      return {name, bytes: b.length, sha256: crypto.createHash('sha256').update(b).digest('hex')};
    })
  };
  fs.writeFileSync(path.join(output, 'receipt.json'), JSON.stringify(receipt, null, 2) + '\n');
  console.log('PASS: retained genuine XDV, resource-only /tex/ aliases, arbitrary absolute refusal');
})().catch(e => { console.error(e); process.exitCode = 1; });
