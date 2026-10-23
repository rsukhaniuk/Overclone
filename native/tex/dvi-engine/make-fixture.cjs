// Test tooling only: execute the existing provisioned genuine pdfTeX in a VM.
// No rewritten/generated TeX engine source; resolver is offline and local-only.
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const [resources, source, output] = process.argv.slice(2).map(p => path.resolve(p));
const localRequire = name => name === 'node:fs' ? new Proxy(fs, {
  get(target, key) {
    if (key !== 'readFileSync') return target[key];
    return (file, options) => fs.readFileSync(file === path.join(resources, 'swiftlatexpdftex.wasm') ? path.join(resources, 'engine.wasm') : file, options);
  }
}) : require(name);
const context = vm.createContext({require: localRequire, process, console, TextDecoder, TextEncoder, WebAssembly, setTimeout, clearTimeout, __dirname: resources, __filename: path.join(resources, 'engine.js')});
context.self = context;
context.fixtureSource = fs.readFileSync(source);
context.resourceRoot = resources;
context.fixtureOutput = output;
context.postMessage = () => {};
context.fetch = () => { throw new Error('Network forbidden'); };
context.XMLHttpRequest = class { constructor() { throw new Error('Network forbidden'); } };
const script = fs.readFileSync(path.join(resources, 'engine.js'), 'utf8');
vm.runInContext(script + `
Module.locateFile = () => require('node:path').join(resourceRoot, 'engine.wasm');
kpse_find_file_impl = function(ptr, format) {
  const name = UTF8ToString(ptr);
  if (name.includes('/') || name.includes('\\\\')) return 0;
  const fs = require('node:fs');
  // Same explicit format alias as the accepted application runtime profile.
  const p = require('node:path').join(resourceRoot, name === 'swiftlatexpdftex.fmt' ? 'pdflatex.fmt' : name);
  if (!fs.existsSync(p) || !fs.statSync(p).isFile()) return 0;
  // All approved files are seeded locally before the engine; never allocate
  // through the legacy missing _malloc export or use its network resolver.
  throw new Error('Unexpected local resolver miss: ' + name);
};
kpse_find_pk_impl = () => 0;
Module.postRun = function() {
  FS.chdir('/work');
  const hostfs = require('node:fs');
  for (const name of hostfs.readdirSync(resourceRoot)) {
    if (name.endsWith('.js') || name.endsWith('.wasm')) continue;
    const file = require('node:path').join(resourceRoot, name);
    if (hostfs.statSync(file).isFile()) FS.writeFile('/work/' + name, new Uint8Array(hostfs.readFileSync(file)));
  }
  FS.writeFile('/work/swiftlatexpdftex.fmt', new Uint8Array(hostfs.readFileSync(require('node:path').join(resourceRoot, 'pdflatex.fmt'))));
  FS.writeFile('/work/main.tex', new Uint8Array(fixtureSource));
  Module.cwrap('setMainEntry', 'number', ['string'])('main.tex');
  const result = _compileLaTeX();
  require('node:fs').writeFileSync(fixtureOutput + '.log', self.memlog);
  if (result !== 0) throw new Error('Genuine pdfTeX returned ' + result);
  const data = FS.readFile('/work/main.dvi');
  require('node:fs').writeFileSync(fixtureOutput, data);
  console.log(JSON.stringify({fixture: fixtureOutput, bytes: data.length, nativeReturn: result}));
};
`, context, {filename: 'existing-pdftex-fixture.vm.js'});
