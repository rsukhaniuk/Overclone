// Build-time Node/Emscripten filesystem bridge; all font metadata extraction is C++.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const [tool, common, out] = process.argv.slice(2);
const hash = data => crypto.createHash('sha256').update(data).digest('hex');
function readJSON(name) {
  try { return JSON.parse(fs.readFileSync(name, 'utf8')); }
  catch (error) { throw new Error('Invalid JSON input: ' + name, {cause: error}); }
}
(async () => {
  if (!out) throw new Error('usage: node make-font-catalog.cjs EXTRACTOR.js COMMON NEW_OUT');
  if (fs.existsSync(out)) throw new Error('refusing existing output');
  const manifest = readJSON(path.join(common, 'common.json'));
  const fonts = manifest.files.filter(pin => /^(DejaVuSerif.*\.ttf|lm[^/]*\.otf|latinmodern-math\.otf)$/.test(pin.name));
  if (fonts.length !== 77) throw new Error('expected supplied 4 DejaVu and 73 Latin Modern fonts');
  const lines = [], metadata = [];
  const create = require(path.resolve(tool));
  const m = await create({wasmBinary: fs.readFileSync(tool.replace(/\.js$/, '.wasm')),
    print: s => lines.push(String(s)), printErr: s => metadata.push(String(s))});
  m.FS.mkdir('/fonts');
  for (const pin of fonts) {
    const data = fs.readFileSync(path.join(common, 'files', pin.name));
    if (hash(data) !== pin.sha256 || data.length !== pin.length) throw new Error('font pin mismatch: ' + pin.name);
    m.FS.writeFile('/fonts/' + pin.name, data);
  }
  const status = m.callMain(fonts.map(pin => pin.name));
  if (status !== 0 || metadata.length !== fonts.length) throw new Error('font extraction failed: ' + metadata.join('\n'));
  const catalog = Buffer.from(lines.join('\n') + '\n');
  const receipt = {schema: 1, kind: 'xetex-explicit-font-catalog', mount: '/tex/xetexfontlist.txt',
    file: {name: 'xetexfontlist.txt', length: catalog.length, sha256: hash(catalog)},
    fonts, metadataFields: ['filename', 'postscript', 'family', 'style', 'weight', 'width', 'slant',
      'designDecipoints', 'minDecipoints', 'maxDecipoints', 'subFamilyID', 'nameCode'],
    metadata: metadata.map(line => line.split('\t')),
    readerAbi: 'Accepted XeTeX reader consumes subFamilyID twice; no nameCode field. No engine modification.'};
  fs.mkdirSync(out, {recursive: true});
  fs.writeFileSync(path.join(out, 'xetexfontlist.txt'), catalog);
  fs.writeFileSync(path.join(out, 'catalog.json'), JSON.stringify(receipt, null, 2) + '\n');
  process.stdout.write(JSON.stringify({fonts: fonts.length, ...receipt.file}) + '\n');
})().catch(error => { process.stderr.write(String(error.stack || error) + '\n'); process.exitCode = 1; });
