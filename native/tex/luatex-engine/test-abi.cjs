'use strict';
const fs = require('node:fs');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const candidate = process.env.CANDIDATE || '/control/candidate';
const create = require(candidate+'/luatex.js');
const wasmBytes = fs.readFileSync(candidate+'/luatex.wasm');
const wasm = new WebAssembly.Module(wasmBytes);
const sha = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
let checks = 0;
async function fresh() {
  return create({noInitialRun:true, print(){}, printErr(){},
    instantiateWasm(imports, done) { const i = new WebAssembly.Instance(wasm,imports); done(i); return i.exports; }});
}
const call = (m,n,t,a) => m.ccall('oc_luatex_'+n,'number',t,a);
(async () => {
  for (const name of ['', '../bad.tex','/host.tex','a/../bad.tex','a//b.tex','a\\b.tex','a"b.tex','a|b.tex','x\n.tex','.','a/'.repeat(125), ...Array.from("{}%#^~$&'!*?[]", c => 'a'+c+'.tex')]) {
    const m = await fresh();
    assert.equal(call(m,'configure',['string','string'],[name,'revision']),-1,name); ++checks;
  }
  for (const bytes of [[0x80], [0xc0,0xaf], [0xc1,0xbf], [0xe0,0x80,0xaf], [0xed,0xa0,0x80], [0xf0,0x80,0x80,0xaf], [0xf4,0x90,0x80,0x80], [0xf5,0x80,0x80,0x80], [0xe2,0x82]]) {
    const invalid = await fresh(); const p = invalid._malloc(bytes.length+1);
    invalid.HEAPU8.set([...bytes,0],p);
    assert.equal(call(invalid,'configure',['number','string'],[p,'revision']),-1); ++checks;
    invalid._free(p);
  }
  const declared = await fresh();
  assert.equal(call(declared,'abi',[],[]),2); ++checks;
  assert.equal(call(declared,'configure',['string','string'],['розділ/теза 😀.tex','revision']),0); ++checks;
  for (let i=0;i<118;++i) { assert.equal(call(declared,'declare_output',['string'],['chapters/part'+i+'.aux']),0); ++checks; }
  assert.equal(call(declared,'declare_output',['string'],['one-too-many.aux']),-4); ++checks;
  assert.equal(call(declared,'declare_output',['string'],['arbitrary.pdf']),-1); ++checks;
  assert.equal(call(declared,'add_file',['number','string','number','number'],[2,'undeclared.aux',0,0]),-1); ++checks;
  assert.equal(call(declared,'add_file',['number','string','number','number'],[2,'chapters/part0.aux',0,0]),0); ++checks;
  assert.ok(declared.FS.analyzePath('/work/output/chapters').exists); ++checks;
  const m = await fresh();
  assert.equal(call(m,'configure',['string','string'],['розділ/my thesis.tex','revision']),0); ++checks;
  assert.equal(call(m,'declare_output',['string'],['file.aux']),0); ++checks;
  assert.equal(call(m,'declare_output',['string'],['file.aux/sub.aux']),-1); ++checks;
  assert.equal(call(m,'configure',['string','string'],['other.tex','new']),-2); ++checks;
  assert.equal(call(m,'output_size',['number'],[0]),-2); ++checks;
  const seed = (kind,name,length=0) => call(m,'add_file',['number','string','number','number'],[kind,name,0,length]);
  assert.equal(seed(1,'valid.tex'),0); ++checks;
  for (const name of ['result.aux','result.toc','result.out','result.bbl','result.blg','result.lof','result.lot']) {
    assert.equal(seed(2,name),0,name); ++checks;
  }
  assert.equal(seed(1,'valid.tex'),-1); ++checks;
  assert.equal(seed(1,'../host'),-4); ++checks;
  assert.equal(seed(2,'result.pdf'),-1); ++checks;
  assert.equal(seed(9,'other'),-1); ++checks;
  assert.equal(seed(1,'too-big',32*1024*1024+1),-4); ++checks;
  const big=m._malloc(8388609);
  assert.ok(big); ++checks;
  assert.equal(call(m,'add_file',['number','string','number','number'],[1,'too-large-project.tex',big,8388609]),-4); ++checks;
  assert.equal(call(m,'add_file',['number','string','number','number'],[2,'result.aux',big,8388609]),-4); ++checks;
  assert.equal(call(m,'add_file',['number','string','number','number'],[0,'large-runtime.bin',big,8388609]),0); ++checks;
  m._free(big);
  m.FS.writeFile('/work/output/result.pdf',Buffer.from('%PDF-1.7\nSTALE\n%%EOF'));
  assert.notEqual(call(m,'compile_latex',[],[]),0); ++checks;
  assert.equal(call(m,'output_size',['number'],[0]),0); ++checks;
  assert.equal(call(m,'compile_format',[],[]),-2); ++checks;
  assert.equal(call(m,'declare_output',['string'],['late.aux']),-2); ++checks;
  assert.equal(seed(1,'late.tex'),-2); ++checks;
  assert.equal(m.FS.analyzePath('/etc/passwd').exists,false); ++checks;
  console.log(JSON.stringify({passed:checks, suite:'actual WASM ABI', wasmSha256:sha(wasmBytes),loaderSha256:sha(fs.readFileSync(candidate+'/luatex.js')),memoryBytes:m.HEAPU8.byteLength}));
})().catch(error => { console.error(error); process.exitCode=1; });
