// Test tooling only: actual retained Emscripten ABI/FS access; no product logic.
'use strict';
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const crypto = require('node:crypto');
const assert = require('node:assert/strict');
const [engine, mode, fixture] = process.argv.slice(2);
assert.ok(['pdftex','dvi','xetex','luatex'].includes(engine));
assert.ok(['format','compile'].includes(mode));
const suite = '/inputs/suite';
const overlay = '/control/ukrainian-overlay';
const out = path.join(overlay, 'checks', engine, mode === 'format' ? mode : path.basename(fixture, '.tex'));
fs.mkdirSync(out, {recursive: true});
const sha = b => crypto.createHash('sha256').update(b).digest('hex');
const pin = b => ({length: b.length, sha256: sha(b)});
function json(file) {
  try { return JSON.parse(fs.readFileSync(file, 'utf8')); }
  catch (error) { throw new Error('Invalid JSON: ' + file, {cause: error}); }
}
(async () => {
  const pinsBytes = fs.readFileSync(path.join(suite, 'pins.json'));
  const pins = new Map(json(path.join(suite, 'pins.json')).map(p => [p.name, p]));
  const provenance = json(path.join(overlay, 'provenance.json'));
  const lock = json(path.join(__dirname, 'source.lock.json'));
  assert.deepEqual(pin(pinsBytes), lock.suitePins);
  assert.deepEqual(pin(pinsBytes), provenance.basePins);
  const used = [];
  function readBase(name) {
    const bytes = fs.readFileSync(path.join(suite, 'files', name));
    assert.deepEqual({name, ...pin(bytes)}, pins.get(name), name);
    used.push({name, ...pin(bytes)});
    return bytes;
  }
  const loader = engine === 'luatex' ? 'luatex' : engine === 'xetex' ? 'xetex' : 'engine';
  const loaderBytes = readBase(loader + '.js');
  const wasm = readBase(loader + '.wasm');
  const lines = [];
  const print = line => { assert.ok(lines.length < 50000, 'bounded log'); lines.push(String(line)); };
  let m, context;
  if (loader === 'engine') {
    // Worker-form upstream loader has no CommonJS factory. Evaluate unchanged
    // bytes in an isolated VM with a pre-supplied WASM module; networking denied.
    let ready;
    const initialized = new Promise(resolve => { ready = resolve; });
    const self = {postMessage: () => ready(), close() {}, location: {href: 'file:///engine.js'}};
    const deny = () => { throw new Error('Network denied'); };
    context = vm.createContext({self, console: {log: print, error: print}, WebAssembly,
      TextDecoder, TextEncoder, Uint8Array, ArrayBuffer, setTimeout, clearTimeout,
      URL, fetch: deny, XMLHttpRequest: class {
        open(method, url) { this.url = url; }
        send() { this.status = 301; } // Offline missing-resource response; no network.
      },
      location: {href: 'file:///engine.js'}, WorkerGlobalScope: function(){},
      wasmBinaryFile: 'engine.wasm'});
    // Loader resets Module, so supply its normal fetch with ONLY the pinned WASM.
    context.fetch = async url => {
      assert.ok(String(url).endsWith('engine.wasm'), 'only pinned WASM fetch');
      return {ok: true, arrayBuffer: async () => wasm.buffer.slice(wasm.byteOffset, wasm.byteOffset + wasm.length)};
    };
    vm.runInContext(loaderBytes.toString('utf8'), context, {filename: loader + '.js'});
    await initialized;
    m = context.Module;
    m.FS = context.FS;
  } else {
    const create = require(path.join(suite, 'files', loader + '.js'));
    m = await create({wasmBinary: wasm, print, printErr: print, noInitialRun: true});
  }
  const F = m.FS;
  const lua = engine === 'luatex';
  const call = (name, types = [], args = [], result = 'number') => m.ccall('oc_luatex_' + name, result, types, args);
  const seed = (name, bytes) => {
    if (lua) {
      const p = m._malloc(bytes.length || 1);
      m.HEAPU8.set(bytes, p);
      const code = call('add_file', ['number','string','number','number'], [0,name,p,bytes.length]);
      m._free(p);
      assert.equal(code, 0, name + ': ' + call('error', [], [], 'string'));
    } else {
      F.mkdirTree(path.posix.dirname('/tex/' + name));
      F.writeFile('/tex/' + name, bytes);
      if (context) {
        // Upstream SwiftLaTeX resolves through format-keyed cache aliases.
        context.aliasName = name;
        vm.runInContext('for(let f=0;f<64;f++) { texlive200_cache[f+"/"+aliasName]="/tex/"+aliasName; if(aliasName.endsWith(".tfm") || aliasName.endsWith(".tex")) texlive200_cache[f+"/"+aliasName.slice(0,-4)]="/tex/"+aliasName; }', context);
      }
    }
  };
  if (lua) assert.equal(call('configure', ['string','string'], ['main.tex','ukrainian-test-1']), 0);
  else {
    if (!F.analyzePath('/tex').exists) F.mkdir('/tex');
    if (!F.analyzePath('/work').exists) F.mkdir('/work');
    F.chdir('/work');
  }
  const files = json(path.join(suite, 'views.json'))[lua ? 'luatex' : engine === 'xetex' ? 'xetex' : 'pdftex'].files;
  const additions = new Map(provenance.files.filter(p => !p.name.startsWith('notices/')).map(p => [p.name,p]));
  const candidateFormats = ['pdflatex.fmt', 'latex-dvi.fmt', 'xelatex.fmt', 'lualatex.fmt'];
  for (const name of files) {
    if (additions.has(name) || candidateFormats.includes(name)) continue;
    seed(name, readBase(name));
  }
  for (const [name, p] of additions) {
    if (candidateFormats.includes(name)) continue;
    const bytes = fs.readFileSync(path.join(overlay, 'files', name));
    assert.equal(sha(bytes), p.sha256); assert.equal(bytes.length, p.length);
    seed(name, bytes);
  }
  const fmtName = {pdftex: 'pdflatex.fmt', dvi: 'latex-dvi.fmt', xetex: 'xelatex.fmt', luatex: 'lualatex.fmt'}[engine];
  if (engine === 'dvi' && mode === 'format') {
    const initializer = '/control/root-latex-dvi.ini';
    const bytes = fs.readFileSync(initializer);
    assert.deepEqual(pin(bytes), lock.dviInitializer);
    used.push({name: 'pdflatex.ini', source: initializer, ...pin(bytes)});
    seed('pdflatex.ini', bytes);
  }
  let chosenFormat;
  if (mode !== 'format') {
    const bytes = fs.readFileSync(path.join(overlay, 'files', fmtName));
    chosenFormat = {name: fmtName, ...pin(bytes)};
    // Production's pdfTeX ABI alias maps the chosen format to swiftlatexpdftex.fmt.
    seed(context ? 'swiftlatexpdftex.fmt' : fmtName, bytes);
    const source = fs.readFileSync(fixture);
    fs.writeFileSync(path.join(out, 'source.tex'), source);
    if (lua) {
      const p = m._malloc(source.length); m.HEAPU8.set(source,p);
      assert.equal(call('add_file', ['number','string','number','number'], [1,'main.tex',p,source.length]), 0);
      m._free(p);
    } else {
      F.writeFile('/work/main.tex', source);
      const setMain = m.ccall || context.ccall;
      assert.equal(setMain('setMainEntry', 'number', ['string'], ['main.tex']), 0);
    }
  }
  let status;
  try {
    if (lua) status = call(mode === 'format' ? 'compile_format' : 'compile_latex');
    else status = m[mode === 'format' ? '_compileFormat' : '_compileLaTeX']();
  } catch (error) {
    status = -999;
    fs.writeFileSync(path.join(out, 'exception.txt'), String(error.stack || error));
  }
  const outputs = {};
  const directory = lua ? '/work/output' : '/work';
  for (const name of F.readdir(directory)) {
    if (!/\.(fmt|log|aux|pdf|xdv|dvi)$/.test(name)) continue;
    let bytes;
    if (lua) {
      const id = {pdf:0, log:1, fmt:2, aux:3}[name.split('.').pop()];
      if (id === undefined) continue;
      const size = call('output_size', ['number'], [id]);
      if (!size) continue;
      const p = m._malloc(size);
      assert.equal(call('copy_output', ['number','number','number'], [id,p,size]), size);
      bytes = Buffer.from(m.HEAPU8.slice(p,p+size)); m._free(p);
    } else bytes = F.readFile(directory + '/' + name);
    fs.writeFileSync(path.join(out, name), bytes);
    outputs[name] = pin(bytes);
    if (mode === 'format' && name.endsWith('.fmt') && status === 0)
      fs.writeFileSync(path.join(overlay, 'files', fmtName), bytes);
  }
  const log = (loader === 'engine' ? context.self.memlog + '\n' : '') + lines.join('\n');
  fs.writeFileSync(path.join(out, 'stdout.log'), log);
  fs.writeFileSync(path.join(out, 'result.json'), JSON.stringify({engine, mode, status,
    loader: pin(loaderBytes), wasm: pin(wasm), basePins: pin(pinsBytes), chosenFormat,
    view: pin(fs.readFileSync(path.join(suite, 'views.json'))),
    fixture: fixture ? {path: fixture, ...pin(fs.readFileSync(fixture))} : undefined,
    network: 'No socket access: pinned WASM bytes only; all XHR misses return offline 301.', used, outputs}, null, 2) + '\n');
  console.log(JSON.stringify({engine, mode, status, outputs}));
  assert.equal(status, 0, log.slice(-5000));
})().catch(error => { console.error(error); process.exitCode = 1; });
