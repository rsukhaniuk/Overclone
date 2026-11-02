// Actual worker JS with MODELED APIs/boot hashes. Explicitly NOT native proof.
'use strict';
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const {webcrypto}=require('node:crypto');
const pairs={lua:['6fb762c18ea4719dd2157d5950303a5f498ad511df0665cca54af141ff0882b5','220999882c2ddb4057b6d798f21785fce35accb4c63c0436dfa62af9f3987dbc'],
 dvi:['a961144a37546755175dfda0687c22bb79e67ebef495aa84c3922b01af6f2c57','53bd8a8a98c509afa8b0e0e5451bf78d7e9d765da1c8f513d9c1a909e9d5270d'],
 xe:['a961144a37546755175dfda0687c22bb79e67ebef495aa84c3922b01af6f2c57','ca030fe4077fc02338e4d92cdc5c0ef1b89c73bcb629b9d44f59708993fa15ef']};
const prior={lua:['7282eb2decde52603417b94d363a6d3258e8ef1451a0d20847bee70084c70a21','1f5b9ca7742693299f1074c47e2a3b91d1df2d6c0381d5c4d1a628965cde542e'],
 dvi:['d79dca5dd87ab27ecb7dcc63cc96171092ea6a57c43054e4cabcf39a0f5c8c72','fb6d4da9ee438d66d89a7a0ec36ab7dd243ea2a5c32c4a426179b935e383ca84'],
 xe:['d79dca5dd87ab27ecb7dcc63cc96171092ea6a57c43054e4cabcf39a0f5c8c72','10031d2748be3f6fa6ba5bc99fd4ef597f06f66208c5b9a5aeba60d5dc3aecac']};
async function attempt(kind,options={}){
 const messages=[],calls=[],alloc=[],freed=[],heap=new Uint8Array(8192);let next=64,copying=false;
 const pdf=new TextEncoder().encode('%PDF-modeled'),map=new Uint8Array([11,12]),geo=new Uint8Array([21,22]);heap.set(pdf,4096);
 const module={HEAPU8:heap,_malloc(n){const p=copying&&options.badPointer?9000:next;next+=n+8;alloc.push(p);return p;},_free(p){freed.push(p);},
  _oc_lua_graphics_abi:()=>options.capability??1,_oc_dvi_graphics_abi:()=>options.capability??1,
  _oc_synctex_abi:()=>65536,_oc_synctex_status:()=>1,_oc_mem_configure(...a){calls.push(['map-configure',...a]);return 1;},
  _oc_synctex_length(t){calls.push(['map-length',t]);copying=true;return options.noMap?0:options.over?1048576:map.length;},
  _oc_synctex_copy(t,p,n){calls.push(['map-copy',t]);heap.set(map,p);return options.short?n-1:n;},
  _oc_synctex_geometry_length(t){calls.push(['geometry-length',t]);return geo.length;},
  _oc_synctex_geometry_copy(t,p,n){calls.push(['geometry-copy',t]);heap.set(geo,p);return n;},
  ccall(name,_r,_types,args){switch(name){case'oc_luatex_abi':return options.baseAbi??5;case'oc_luatex_configure':case'oc_luatex_declare_output':case'oc_luatex_add_file':calls.push([name,...args]);return 0;
   case'oc_luatex_compile_latex':calls.push(['native']);return options.failure?1:0;case'oc_luatex_output_size':return args[0]===0?pdf.length:0;case'oc_luatex_copy_output':heap.set(pdf,args[1]);return pdf.length;case'oc_luatex_named_output_size':return 0;default:throw Error('Unexpected modeled ABI '+name);}},
  _oc_dvi_abi_version:()=>options.baseAbi??(kind==='xe'?3:2),_oc_dvi_add_file(){calls.push(['add-file']);return 0;},
  _oc_dvi_geometry_configure(...a){calls.push(['geometry-configure',...a]);return 1;},
  _oc_dvi_run(){calls.push(['native']);return options.failure?1:0;},_oc_dvi_native_return:()=>options.failure?1:0,_oc_dvi_status:()=>options.failure?1:0,
  _oc_dvi_log_data:()=>0,_oc_dvi_log_size:()=>0,_oc_dvi_log_truncated:()=>0,_oc_dvi_pdf_data:()=>4096,_oc_dvi_pdf_size:()=>pdf.length,
  _oc_dvi_geometry_data:()=>0,_oc_dvi_geometry_size:()=>geo.length,
  _oc_dvi_geometry_length(t){calls.push(['geometry-length',t]);copying=true;return options.noMap?0:options.over?131073:geo.length;},
  _oc_dvi_geometry_copy(t,p,n){calls.push(['geometry-copy',t]);heap.set(geo,p);return options.short?n-1:n;}};
 if(options.missingCapability)delete module[kind==='lua'?'_oc_lua_graphics_abi':'_oc_dvi_graphics_abi'];
 const hashes=options.hashes??pairs[kind];
 const crypto={subtle:{digest(algorithm,b){return b.length===1&&[1,2].includes(b[0])?Promise.resolve(Uint8Array.from(Buffer.from(hashes[b[0]-1],'hex')).buffer):webcrypto.subtle.digest(algorithm,b);}}};
 const self={postMessage:m=>messages.push(m)};
 vm.runInNewContext(fs.readFileSync(path.join(__dirname,'../../src/Client/wwwroot/js',kind==='lua'?'unicode-tex-port-worker.js':'dvi-port-worker.js'),'utf8'),{self,Uint8Array,TextEncoder,TextDecoder,WebAssembly,crypto,performance,importScripts(){self[kind==='lua'?'createLuaTeX':'createOcDvi']=async()=>module;}});
 await self.onmessage({data:{operation:'boot',loaderUrl:'blob:modeled',loader:new Uint8Array([1]),wasm:new Uint8Array([2])}});
 if(!messages.some(m=>m.kind==='ready'))return{messages,calls,alloc,freed,heap};
 const root='тези/my thesis.tex',files=[{name:root,bytes:new Uint8Array([3,4])},{name:'__oop_runtime__/'+(kind==='lua'?'lualatex.fmt':'cmr10.tfm'),bytes:new Uint8Array([5,6])}];
 const entry='my thesis'+(kind==='xe'?'.xdv':'.dvi');if(kind!=='lua')files.push({name:entry,bytes:new Uint8Array([247,kind==='xe'?7:2])});
 const t={profile:options.profile??10,version:options.phase??{lua:17,dvi:15,xe:16}[kind],engineId:options.engine??{lua:'lualatex',dvi:'latex-dvi',xe:'xelatex'}[kind],tool:kind==='lua'?'latex':'dvipdfmx',ordinal:options.ordinal??7,runtimeIdentity:'a'.repeat(64),nonce:'fresh'};
 await self.onmessage({data:{operation:t.tool,tag:t,selectedRoot:root,mainEntry:kind==='lua'?root:entry,files,runtimeResourceNames:[files[1].name],generatedInputNames:[],outputs:[kind==='lua'?'result.pdf':'my thesis.pdf'],maxLogBytes:65536,maxOutputBytes:8388608}});
 return{messages,calls,alloc,freed,heap};
}
(async()=>{
 let negative=0;
 for(const kind of ['lua','dvi','xe']){
  const good=await attempt(kind),r=good.messages.at(-1).reply;assert.equal(r.outcome,'native-return');assert.equal(r.nativeReturn,0);
  const config=kind==='lua'?'map-configure':'geometry-configure';assert(good.calls.findIndex(c=>c[0]===config)<good.calls.findIndex(c=>c[0]==='native'));
  for(const c of good.calls.filter(c=>/^(map|geometry)-(length|copy)$/.test(c[0])))assert.equal(c[1],7,'Copy EXACT request token, not prior/final inference');
  const owned=kind==='lua'?r.sourceMapPayload:r.sourceMapGeometryPayload;good.heap.fill(0);assert.deepEqual([...owned],kind==='lua'?[11,12]:[21,22]);
  assert.deepEqual([...good.alloc].sort((a,b)=>a-b),[...good.freed].sort((a,b)=>a-b));
  for(const options of [{missingCapability:true},{capability:0},{capability:2},{baseAbi:kind==='dvi'?3:kind==='xe'?2:3},{ordinal:0},{ordinal:4294967296},{profile:11},{profile:2147483647},
   {phase:10},{phase:11},{phase:12},{phase:13},{phase:14},{engine:kind==='xe'?'latex-dvi':'xelatex'},
   {hashes:prior[kind]},{hashes:[pairs[kind][0],prior[kind][1]]},{hashes:[prior[kind][0],pairs[kind][1]]},
   {profile:9,phase:{lua:14,dvi:12,xe:13}[kind]},{profile:8,phase:{lua:14,dvi:12,xe:13}[kind]}]){
   const got=await attempt(kind,options);assert(!got.calls.some(c=>c[0]==='native'),JSON.stringify({kind,options}));assert.equal(got.messages.at(-1).reply?.outcome,'setup-failure');negative++;
  }
  if(kind!=='lua'){const crossed=await attempt(kind,{hashes:pairs[kind==='xe'?'dvi':'xe']});assert(!crossed.calls.some(c=>c[0]==='native'));negative++;}
  for(const options of [{over:true},{short:true},{badPointer:true},{noMap:true}]){const got=await attempt(kind,options),reply=got.messages.at(-1).reply;assert.equal(reply.outcome,'native-return');assert(reply.files.length>0);assert.equal(kind==='lua'?reply.sourceMapPayload:reply.sourceMapGeometryPayload,null);}
  const failed=await attempt(kind,{failure:true});assert(failed.calls.some(c=>c[0]==='native'));assert(!failed.calls.some(c=>/^(map|geometry)-length$/.test(c[0])));
 }
 console.log(`Graphics FFI MODELED PASS: 3 exact P10 cells, ${negative} setup-failure hash/profile/phase/base-capability negatives; exact7 tokens, configure-before-native, owned/capped copies, PDF-preserving optional failures, no sealing APIs.`);
})().catch(e=>{console.error(e);process.exitCode=1;});
