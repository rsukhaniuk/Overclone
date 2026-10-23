// Real accepted-module harness only; JS is needed for Emscripten FS/ABI access.
// No product workflow or compiler implementation lives here.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const assert = require('node:assert/strict');
const [build, common, out, fixture, catalog] = process.argv.slice(2);
if (!fixture) throw new Error('usage: node test-xetex.cjs ACCEPTED_XE COMMON OUT FIXTURE.tex');
const hash = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
const readJSON = name => {
  try { return JSON.parse(fs.readFileSync(name, 'utf8')); }
  catch (error) { throw new Error('Invalid JSON input: ' + name, {cause: error}); }
};
(async () => {
  fs.mkdirSync(out, {recursive: true});
  const manifest = readJSON(path.join(common, 'common.json'));
  const pins = readJSON(path.join(build, 'manifest.json'));
  for (const name of ['xetex.js', 'xetex.wasm', 'xelatex.fmt']) {
    const data = fs.readFileSync(path.join(build, name));
    assert.equal(data.length, pins[name].length);
    assert.equal(hash(data), pins[name].sha256);
  }
  const lines = [];
  const print = line => { if (lines.length < 20000) lines.push(String(line)); };
  const create = require(path.resolve(build, 'xetex.js'));
  const m = await create({wasmBinary: fs.readFileSync(path.join(build, 'xetex.wasm')), print, printErr: print});
  const F = m.FS;
  F.mkdir('/tex'); F.mkdir('/work'); F.chdir('/work');
  const mount = (name, data) => { F.mkdirTree(path.posix.dirname(name)); F.writeFile(name, data); };
  for (const pin of manifest.files) {
    const data = fs.readFileSync(path.join(common, 'files', pin.name));
    assert.equal(data.length, pin.length); assert.equal(hash(data), pin.sha256);
    mount('/tex/' + pin.name, data);
  }
  mount('/tex/xelatex.fmt', fs.readFileSync(path.join(build, 'xelatex.fmt')));
  const required = readJSON(path.join(build, 'required-resources.json'));
  const icu = required.compilation.find(p => p.name === 'icudt68l.dat');
  const data = fs.readFileSync(icu.source);
  assert.equal(data.length, icu.length); assert.equal(hash(data), icu.sha256);
  mount('/tex/' + icu.name, data);
  let catalogSha256;
  if (catalog) {
    const bytes = fs.readFileSync(catalog);
    const pin = readJSON(path.join(path.dirname(catalog), 'catalog.json')).file;
    catalogSha256 = hash(bytes);
    assert.equal(bytes.length, pin.length); assert.equal(catalogSha256, pin.sha256);
    mount('/tex/xetexfontlist.txt', bytes);
  }
  const source = fs.readFileSync(fixture);
  mount('/work/main.tex', source);
  // Actual small EPS input, not a substituted output or mocked compiler result.
  mount('/work/sample.eps', '%!PS-Adobe-3.0 EPSF-3.0\n%%BoundingBox: 0 0 20 20\n0 0 moveto 20 20 lineto stroke\nshowpage\n');
  if (path.basename(fixture) === 'local-override.tex') {
    mount('/work/geometry.sty', '\\ProvidesPackage{geometry}[2099/01/01 synthetic local override]\n\\typeout{SUITE-LOCAL-OVERRIDE-READ}\n');
  }
  const opened = new Set();
  const open = F.open;
  F.open = function(name, ...args) {
    const stream = open.call(this, name, ...args);
    if (typeof name === 'string') opened.add(path.posix.resolve(F.cwd(), name));
    return stream;
  };
  assert.equal(m.ccall('setMainEntry', 'number', ['string'], ['main.tex']), 0);
  const status = m._compileLaTeX();
  const outputs = {};
  for (const name of F.readdir('/work')) {
    if (!/\.(xdv|log|aux|out)$/.test(name)) continue;
    const bytes = F.readFile('/work/' + name);
    fs.writeFileSync(path.join(out, name), bytes);
    outputs[name] = {length: bytes.length, sha256: hash(bytes)};
  }
  fs.writeFileSync(path.join(out, 'stdout.log'), lines.join('\n') + '\n');
  const report = {schema: 1, fixture: path.basename(fixture), sourceSha256: hash(source),
    moduleSha256: pins['xetex.wasm'].sha256, formatSha256: pins['xelatex.fmt'].sha256,
    commonSha256: hash(fs.readFileSync(path.join(common, 'common.json'))), catalogSha256,
    status, outputs, opened: [...opened].sort()};
  fs.writeFileSync(path.join(out, 'result.json'), JSON.stringify(report, null, 2) + '\n');
  process.stdout.write(JSON.stringify({fixture: report.fixture, status, outputs}) + '\n');
  assert.equal(status, 0);
  const xdv = F.readFile('/work/main.xdv');
  assert.equal(xdv[0], 247); assert.equal(xdv[1], 7); assert.ok(xdv.length > 100);
  assert.ok(!lines.some(s => /Missing character:/.test(s)), 'missing glyphs are not a font readiness pass');
  let expectedFonts = [];
  if (path.basename(fixture) === 'fontspec-unicode.tex') {
    expectedFonts = ['DejaVuSerif.ttf', 'DejaVuSerif-Bold.ttf', 'DejaVuSerif-Italic.ttf', 'DejaVuSerif-BoldItalic.ttf'];
  } else if (path.basename(fixture) === 'unicode-math.tex') {
    expectedFonts = ['lmroman10-regular.otf', 'lmroman10-bold.otf', 'lmroman10-italic.otf', 'lmroman10-bolditalic.otf', 'latinmodern-math.otf'];
  }
  for (const font of expectedFonts) {
    assert.ok(opened.has('/tex/' + font), 'font bytes must actually be read: ' + font);
    assert.ok(Buffer.from(xdv).includes(Buffer.from('/tex/' + font)), 'actual XDV must select font: ' + font);
  }
  if (path.basename(fixture) === 'local-override.tex') {
    assert.ok(opened.has('/work/geometry.sty'));
    assert.ok(!opened.has('/tex/geometry.sty'));
    assert.ok(lines.some(s => s.includes('SUITE-LOCAL-OVERRIDE-READ')));
  }
})().catch(error => { process.stderr.write(String(error.stack || error) + '\n'); process.exitCode = 1; });
