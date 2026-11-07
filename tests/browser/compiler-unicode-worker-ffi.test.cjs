// Actual worker source with MODELED native APIs and boot hashes. Native/browser
// acceptance is separate. Reuse the established API models, adding only the new
// candidate capabilities; the product worker source is never changed by tests.
'use strict';
const assert = require('node:assert/strict'), fs = require('node:fs'), path = require('node:path');
function model(file, additions) {
  let source = fs.readFileSync(path.join(__dirname, file), 'utf8');
  source = source.slice(0, source.indexOf('(async()=>{'));
  for (const [old, replacement] of additions) {
    assert.equal(source.split(old).length, 2, 'one explicit modeled API seam');
    source = source.replace(old, replacement);
  }
  return new Function('require', '__dirname', source + '\nreturn attempt;')(require, __dirname);
}
const lua = model('navigation-graphics-transport-ffi.test.cjs', [
  ['const module={HEAPU8:heap,', `const module={
    _oc_lua_harfbuzz_abi:()=>options.harf??1,
    _oc_lua_font_closure_abi:()=>options.fonts??1,HEAPU8:heap,`],
  ['if(options.missingCapability)', `if(options.missingHarf)delete module._oc_lua_harfbuzz_abi;
   if(options.missingFonts)delete module._oc_lua_font_closure_abi;
   if(options.missingCapability)`]
]);
const xe = model('navigation-xe-transport-ffi.test.cjs', [
  ['const m={HEAPU8:heap,', 'const m={_oc_xe_nonbmp_abi:()=>options.nonbmp??1,HEAPU8:heap,'],
  ['if(options.missingExport)', 'if(options.missingNonbmp)delete m._oc_xe_nonbmp_abi;\n if(options.missingExport)'],
  ['const hashes=converter?', 'const hashes=options.hashes??(converter?'],
  ["'61ef6fe76724c7bd9532f05224d03829e40393c61acd68da0fd0589d8e60c7c2'];",
    "'61ef6fe76724c7bd9532f05224d03829e40393c61acd68da0fd0589d8e60c7c2']);"]
]);
const luaHashes = ['b66ac597a51b06a97598c95db54329005db54ff5631e461e36f1a841bec86d90',
  '7122d44f4d63a4626a3bcc55bb6bb404cfb8d78fd5419dda38849e991e23b7c6'];
const xeHashes = ['38ba92890c5de6e957254dcda1c1345a75b35dabaa46d77fc4835f3ad4f33601',
  '5a08c4d0a2109adc43e087889be208deb71cf4ba55692b41d58da2c8284f1e8e'];
(async () => {
  for (const [attempt, options, native, negatives] of [
    [extra=>lua('lua',extra), {profile:12,phase:22,hashes:luaHashes}, 'native',
      [{missingHarf:true},{harf:0},{harf:2},{missingFonts:true},{fonts:0},{fonts:2},{missingCapability:true},{phase:17},{profile:10}]],
    [extra=>xe(false,extra), {profile:12,version:21,hashes:xeHashes}, 'compile',
      [{missingNonbmp:true},{nonbmp:0},{nonbmp:2},{version:16},{profile:10},{missingExport:true},{oldFactory:true}]]
  ]) {
    const good = await attempt(options);
    assert.ok(good.calls.some(call=>call[0]===native), 'positive must reach actual worker native-entry branch');
    assert.equal(good.messages.at(-1).reply.outcome, 'native-return');
    for (const extra of [...negatives, {profile:11}, {profile:13}, {hashes:[options.hashes[0],'0'.repeat(64)]}]) {
      const refused = await attempt({...options,...extra});
      assert.ok(!refused.calls.some(call=>call[0]===native), JSON.stringify(extra));
    }
  }
  console.log('P12 worker FFI modeled PASS: exact native hash/base/capability positives; missing, wrong, old, reserved and mixed components refuse before native entry.');
})().catch(error=>{console.error(error);process.exitCode=1;});
