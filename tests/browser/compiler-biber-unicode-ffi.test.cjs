'use strict';
// Actual product worker source, MODELED hashes/native APIs only. Real candidate
// modules, Unicode text and geometry are exercised by the separate WASM test.
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const {webcrypto}=require('node:crypto');
const pairs={lua:['3ebd6f09571509648e6ce88de8f1229f4709514f5fbc9a1b1029b8c59a3cba4d','2eefd3abfec7292f0a912b364693e830bc47792d5c6c56029246ebc3505e2a16'],
 xe:['38ba92890c5de6e957254dcda1c1345a75b35dabaa46d77fc4835f3ad4f33601','5a08c4d0a2109adc43e087889be208deb71cf4ba55692b41d58da2c8284f1e8e'],
 dvi:['2975efeae6dc759ccdce984f68350a250ed8bb8bedaa6584329de5901db23caf','4f4e13b462772c15184bf4279b533d598d446fa7a44c0b2bafda276baf91701f'],
 xecvt:['a961144a37546755175dfda0687c22bb79e67ebef495aa84c3922b01af6f2c57','ca030fe4077fc02338e4d92cdc5c0ef1b89c73bcb629b9d44f59708993fa15ef']};
const oldLua=['b66ac597a51b06a97598c95db54329005db54ff5631e461e36f1a841bec86d90','7122d44f4d63a4626a3bcc55bb6bb404cfb8d78fd5419dda38849e991e23b7c6'];
const encode=s=>new TextEncoder().encode(s),prefix='__oop_runtime__/';
async function attempt(kind,options={},mutate=()=>{}){
 const converter=['dvi','xecvt'].includes(kind),xe=['xe','xecvt'].includes(kind),root=xe?'тези/розділ 😀.tex':'тези/розділ з пробілом.tex';
 const leaf=root.slice(root.lastIndexOf('/')+1),job=kind==='lua'?'result':leaf.slice(0,-4),artifact=job+(kind==='xe'?'.xdv':'.pdf');
 const phase=options.version??(kind==='dvi'?24:xe?21:22),backend=options.backend??'none';
 const frame={operation:converter?'dvipdfmx':'latex',bibliographyTool:backend,tag:{profile:options.profile??13,version:phase,engineId:options.engine??(xe?'xelatex':kind==='dvi'?'latex-dvi':'lualatex'),
  tool:converter?'dvipdfmx':'latex',ordinal:7,runtimeIdentity:'a'.repeat(64),nonce:'model'},selectedRoot:root,mainEntry:converter?job+(xe?'.xdv':'.dvi'):root,
  files:[{name:root,bytes:encode('original source')},{name:prefix+(converter?'cmr10.tfm':xe?'xelatex.fmt':'lualatex.fmt'),bytes:encode('resource')}],
  runtimeResourceNames:[prefix+(converter?'cmr10.tfm':xe?'xelatex.fmt':'lualatex.fmt')],generatedInputNames:[],
  outputs:converter?[job+'.pdf']:[artifact,...(kind==='xe'?[job+'.pdf']:[]),job+'.aux',job+'.log',...(backend==='biber'?[job+'.bcf',job+'.run.xml']:[job.replaceAll(' ','_')+'-blx.bib'])],
  maxLogBytes:65536,maxOutputBytes:8388608};
 if(converter)frame.files.push({name:frame.mainEntry,bytes:new Uint8Array([247,xe?7:2,0])});
 if(frame.tag.profile!==13)delete frame.bibliographyTool;
 mutate(frame);
 const calls=[],messages=[],heap=new Uint8Array(65536),store=new Map(),map=encode('native-model-map'),geo=encode('{"schemaVersion":1,"pages":[]}'),pdf=encode('%PDF-model');let next=1024;
 heap.set(pdf,32768);
 const model={HEAPU8:heap,_malloc(n){const p=next;next+=n+8;assert.ok(next<32768);return p;},_free(){},
  _oc_xe_nonbmp_abi:()=>options.nonbmp??1,_oc_lua_harfbuzz_abi:()=>options.harfbuzz??1,_oc_lua_font_closure_abi:()=>options.fonts??1,
  _oc_lua_text_semantics_abi:()=>options.text??1,_oc_lua_graphics_abi:()=>options.graphics??1,_oc_dvi_graphics_abi:()=>options.graphics??1,
  _oc_xe_maps_abi:()=>options.maps??1,_oc_synctex_abi:()=>options.synctex??65536,_oc_synctex_status:()=>1,
  _oc_mem_configure(...a){calls.push(['configure',...a]);return 1;},
  _oc_synctex_length(n){calls.push(['map-length',n]);return map.length;},_oc_synctex_copy(n,p){calls.push(['map-copy',n]);heap.set(map,p);return map.length;},
  _oc_synctex_geometry_length(n){calls.push(['geometry-length',n]);return geo.length;},_oc_synctex_geometry_copy(n,p){calls.push(['geometry-copy',n]);heap.set(geo,p);return geo.length;},
  FS:{mkdir(){calls.push(['mkdir']);},mkdirTree(){calls.push(['mkdir']);},chdir(){},writeFile(n,b){calls.push(['write',n]);store.set(n,b.slice());},analyzePath:n=>({exists:store.has(n)}),
   stat:n=>({mode:32768,size:store.get(n).length}),isFile:()=>true,readFile:n=>store.get(n).slice()},
  _compileLaTeX(){calls.push(['native']);for(const n of frame.outputs)store.set('/work/'+n,n.endsWith('.xdv')?new Uint8Array([247,7,0]):n.endsWith('.pdf')?pdf:encode('output'));return options.nativeReturn??0;},
  ccall(n,_r,_t,a){switch(n){case'oc_luatex_abi':return options.nativeAbi??5;
   case'setMainEntry':case'oc_luatex_configure':case'oc_luatex_declare_output':case'oc_luatex_add_file':calls.push([n,...a]);return 0;
   case'oc_luatex_compile_latex':calls.push(['native']);return options.nativeReturn??0;
   case'oc_luatex_output_size':return a[0]===0?pdf.length:0;case'oc_luatex_copy_output':heap.set(pdf,a[1]);return pdf.length;
   case'oc_luatex_named_output_size':return 0;default:throw Error('unexpected modeled native call '+n);}},
  _oc_dvi_abi_version:()=>options.nativeAbi??(xe?3:2),_oc_dvi_font_semantics_abi:()=>options.semantic??1,_oc_dvi_font_semantics_status:()=>options.status??1,
  _oc_dvi_add_file(){calls.push(['add-file']);return 0;},_oc_dvi_geometry_configure(...a){calls.push(['configure',...a]);return 1;},
  _oc_dvi_run(){calls.push(['native']);return options.converterStatus??0;},_oc_dvi_native_return:()=>options.nativeReturn??0,_oc_dvi_status:()=>options.converterStatus??0,
  _oc_dvi_log_data:()=>0,_oc_dvi_log_size:()=>0,_oc_dvi_log_truncated:()=>0,_oc_dvi_pdf_data:()=>32768,_oc_dvi_pdf_size:()=>pdf.length,
  _oc_dvi_geometry_data:()=>0,_oc_dvi_geometry_size:()=>geo.length,_oc_dvi_geometry_length(n){calls.push(['geometry-length',n]);return geo.length;},
  _oc_dvi_geometry_copy(n,p){calls.push(['geometry-copy',n]);heap.set(geo,p);return geo.length;}};
 for(const name of options.missing??[])delete model[name];
 const hashes=options.hashes??pairs[kind],crypto={subtle:{digest(a,b){return b.length===1&&[1,2].includes(b[0])?Promise.resolve(Uint8Array.from(Buffer.from(hashes[b[0]-1],'hex')).buffer):webcrypto.subtle.digest(a,b);}}};
 const self={postMessage:m=>messages.push(m)};
 vm.runInNewContext(fs.readFileSync(path.join(__dirname,'../../src/Client/wwwroot/js',converter?'dvi-port-worker.js':'unicode-tex-port-worker.js'),'utf8'),
  {self,Uint8Array,TextEncoder,TextDecoder,WebAssembly,crypto,performance,importScripts(){self[converter?'createOcDvi':xe?'createXeMaps':'createLuaTeX']=async()=>model;}});
 await self.onmessage({data:{operation:'boot',loaderUrl:'blob:model',loader:new Uint8Array([1]),wasm:new Uint8Array([2])}});
 if(messages.some(m=>m.kind==='ready')){await self.onmessage({data:frame});if(options.replay)await self.onmessage({data:frame});}
 return{messages,calls,frame,heap};
}
module.exports={attempt};
if(require.main===module)(async()=>{
 let count=0;
 for(const kind of ['lua','xe','dvi','xecvt']){
  for(const backend of ['none','bibtex','biber']){
   const r=await attempt(kind,{backend,replay:true}),reply=r.messages.at(-1).reply;assert.equal(reply.outcome,'native-return');assert.equal(r.calls.filter(c=>c[0]==='native').length,1);
   for(const c of r.calls.filter(c=>/^(map|geometry)-(length|copy)$/.test(c[0])))assert.equal(c[1],7);count++;
  }
  const negatives=[{profile:11},{profile:14},{version:10},{version:23},{version:kind==='dvi'?21:24},{hashes:[pairs[kind][0],'0'.repeat(64)]},
   {hashes:['0'.repeat(64),pairs[kind][1]]}, ...(kind==='xe'?[]:[{nativeAbi:kind==='lua'?3:kind==='dvi'?3:2}])];
  if(kind==='lua')negatives.push({text:0},{text:2},{missing:['_oc_lua_text_semantics_abi']},{fonts:0},{harfbuzz:0},{graphics:0},{hashes:oldLua});
  if(kind==='xe')negatives.push({nonbmp:0},{missing:['_oc_xe_nonbmp_abi']},{maps:0});
  if(kind==='dvi')negatives.push({semantic:0},{semantic:2},{missing:['_oc_dvi_font_semantics_abi']},{missing:['_oc_dvi_font_semantics_status']},{hashes:pairs.xecvt});
  if(kind==='xecvt')negatives.push({hashes:pairs.dvi});
  for(const extra of negatives){const r=await attempt(kind,extra);assert.ok(!r.calls.some(c=>c[0]==='native'),JSON.stringify({kind,extra}));count++;}
  for(const mutate of [f=>delete f.bibliographyTool,f=>f.bibliographyTool='BIBER',f=>f.tag.ordinal=0,
   f=>f.files.push({...f.files[0]}),f=>f.generatedInputNames.push(f.runtimeResourceNames[0]),f=>f.selectedRoot='../escape.tex']){
   const r=await attempt(kind,{},mutate);assert.ok(!r.calls.some(c=>c[0]==='native'),kind+' '+mutate.toString());count++;
  }
  if(['lua','xe'].includes(kind))for(const [backend,mutate] of [['none',f=>f.outputs.push((kind==='lua'?'result':f.selectedRoot.slice(f.selectedRoot.lastIndexOf('/')+1,-4))+'.bcf')],
   ['biber',f=>{f.outputs.pop();}],['biber',f=>{const job=kind==='lua'?'result':f.selectedRoot.slice(f.selectedRoot.lastIndexOf('/')+1,-4);f.generatedInputNames.push(job+'.bcf');f.files.push({name:job+'.bcf',bytes:encode('stale')});}]]){
    const r=await attempt(kind,{backend},mutate);assert.ok(!r.calls.some(c=>c[0]==='native'));count++;
   }
  if(['lua','xe'].includes(kind))for(const backend of ['none','bibtex','biber'])for(const name of ['old.bcf','old.BCF','dir/old.run.xml']){
   const r=await attempt(kind,{backend},f=>{f.generatedInputNames.push(name);f.files.push({name,bytes:encode('stale control')});});
   assert.ok(!r.calls.some(c=>c[0]==='native'),kind+' '+backend+' forbidden carry '+name);assert.ok(!r.calls.some(c=>['write','oc_luatex_configure'].includes(c[0])));count++;
  }
  if(['dvi','xecvt'].includes(kind))for(const mutate of [f=>f.files.push({name:f.outputs[0],bytes:encode('captured PDF')}),
   f=>f.files.push({name:f.outputs[0].toUpperCase(),bytes:encode('alias PDF')}),
   f=>{const name=prefix+f.outputs[0];f.runtimeResourceNames.push(name);f.files.push({name,bytes:encode('resource PDF')});}]){
   const r=await attempt(kind,{},mutate);assert.ok(!r.calls.some(c=>c[0]==='add-file'||c[0]==='native'));count++;
  }
 }
 for(const status of [2,0,3]){const r=await attempt('dvi',{status}),reply=r.messages.at(-1).reply;assert.equal(reply.nativeReturn,0);assert.equal(reply.entered,true);assert.equal(reply.files.length,0);
  assert.equal(reply.sourceMapGeometryPayload,null);assert.equal(reply.outcome,status===2?'font-semantics-unsupported':'protocol-failure');count++;}
 console.log(`P13 Unicode/converter MODELED FFI PASS: ${count} cases; not native/browser proof.`);
})().catch(e=>{console.error(e);process.exitCode=1;});
