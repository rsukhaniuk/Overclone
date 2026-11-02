// Actual worker JS, MODELED native APIs and boot hashes. No real PDF/native claim.
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const {webcrypto}=require('node:crypto');
async function attempt(converter=false,options={}){
 const messages=[],calls=[],freed=[],allocations=[],store=new Map();let next=64,copying=false;
 const heap=new Uint8Array(8192),xdv=new Uint8Array([247,7,0]),pdf=new TextEncoder().encode('%PDF-modeled'),raw=new Uint8Array([11,12]),sidecar=new Uint8Array([21,22]);
 heap.set(pdf,4096);
 const m={HEAPU8:heap,_malloc(n){const p=copying&&options.badPointer?9000:next;next+=n+8;allocations.push(p);return p;},_free(p){freed.push(p);},
  FS:{mkdir(){},mkdirTree(){},chdir(p){calls.push(['cwd',p]);},writeFile(n,b){calls.push(['write',n]);store.set(n,b.slice());},
   analyzePath:n=>({exists:store.has(n)}),stat:n=>({mode:32768,size:store.get(n).length}),isFile:()=>true,readFile:n=>store.get(n).slice()},
  ccall(name,_r,_t,args){if(name!=='setMainEntry')throw new Error('unexpected C ABI '+name);calls.push(['entry',...args]);return 0;},
  _oc_xe_maps_abi:()=>options.abi??1,_oc_synctex_abi:()=>65536,_oc_synctex_status:()=>1,
  _oc_mem_configure(...a){calls.push(['configure-map',...a]);return 1;},
  _compileLaTeX(){calls.push(['compile']);store.set('/work/my thesis.xdv',xdv);return options.failure?1:0;},
  _oc_synctex_length(t){calls.push(['map-length',t]);copying=true;return options.overMap?1048576:raw.length;},
  _oc_synctex_copy(t,p,n){calls.push(['map-copy',t,p,n]);heap.set(raw,p);return options.shortMap?n-1:n;},
  _oc_dvi_abi_version:()=>options.abi??3,
  _oc_dvi_add_file(...a){calls.push(['add',...a]);return 0;},
  _oc_dvi_geometry_configure(...a){calls.push(['configure-geometry',...a]);return 1;},
  _oc_dvi_run(){calls.push(['convert']);return options.failure?1:0;},_oc_dvi_native_return:()=>options.failure?1:0,_oc_dvi_status:()=>options.failure?1:0,
  _oc_dvi_log_data:()=>0,_oc_dvi_log_size:()=>0,_oc_dvi_log_truncated:()=>0,_oc_dvi_pdf_data:()=>4096,_oc_dvi_pdf_size:()=>pdf.length,
  _oc_dvi_geometry_data:()=>0,_oc_dvi_geometry_size:()=>sidecar.length,
  _oc_dvi_geometry_length(t){calls.push(['geometry-length',t]);copying=true;return options.overGeometry?131073:sidecar.length;},
  _oc_dvi_geometry_copy(t,p,n){calls.push(['geometry-copy',t,p,n]);heap.set(sidecar,p);return options.shortGeometry?n-1:n;}
 };
 if(options.missingExport)delete m[converter?'_oc_dvi_geometry_copy':'_oc_synctex_copy'];
 const hashes=converter?['d79dca5dd87ab27ecb7dcc63cc96171092ea6a57c43054e4cabcf39a0f5c8c72','10031d2748be3f6fa6ba5bc99fd4ef597f06f66208c5b9a5aeba60d5dc3aecac']
  :['a2d90034e871d75d48596b26a5856ac6eed86b6396bd563e610b0ffa7a6be21c','61ef6fe76724c7bd9532f05224d03829e40393c61acd68da0fd0589d8e60c7c2'];
 const crypto={subtle:{digest(algorithm,b){return b.length===1&&[1,2].includes(b[0])?Promise.resolve(Uint8Array.from(Buffer.from(hashes[b[0]-1],'hex')).buffer):webcrypto.subtle.digest(algorithm,b);}}};
 const self={postMessage:x=>messages.push(x)};
 vm.runInNewContext(fs.readFileSync(path.join(__dirname,'../../src/Client/wwwroot/js',converter?'dvi-port-worker.js':'unicode-tex-port-worker.js'),'utf8'),
  {self,Uint8Array,TextEncoder,TextDecoder,WebAssembly,crypto,performance,importScripts(){if(converter)self.createOcDvi=async()=>m;else self[options.oldFactory?'createXeTeX':'createXeMaps']=async()=>m;}});
 await self.onmessage({data:{operation:'boot',loaderUrl:'blob:modeled',loader:new Uint8Array([1]),wasm:new Uint8Array([2])}});
 if(!messages.some(x=>x.kind==='ready'))return{messages,calls,freed,allocations,heap};
 const root='тези/my thesis.tex',files=[{name:root,bytes:new Uint8Array([3,4])},{name:'__oop_runtime__/'+(converter?'cmr10.tfm':'xelatex.fmt'),bytes:new Uint8Array([5,6])}];
 if(converter)files.push({name:'my thesis.xdv',bytes:xdv});
 const t={profile:options.profile??9,version:options.version??13,engineId:'xelatex',tool:converter?'dvipdfmx':'latex',ordinal:options.ordinal??(converter?8:7),runtimeIdentity:'a'.repeat(64),nonce:'fresh'};
 await self.onmessage({data:{operation:t.tool,tag:t,selectedRoot:root,mainEntry:converter?'my thesis.xdv':root,files,runtimeResourceNames:[files[1].name],generatedInputNames:[],
  outputs:converter?['my thesis.pdf']:['my thesis.pdf','my thesis.xdv'],maxLogBytes:65536,maxOutputBytes:8388608}});
 return{messages,calls,freed,allocations,heap};
}
(async()=>{
 for(const converter of [false,true]){
  const good=await attempt(converter),reply=good.messages.at(-1).reply;assert.equal(reply.outcome,'native-return');assert.equal(reply.nativeReturn,0);
  const configure=converter?'configure-geometry':'configure-map',run=converter?'convert':'compile',token=converter?8:7;
  assert.equal(good.calls.filter(c=>c[0]===configure).length,1);
  const at=good.calls.findIndex(c=>c[0]===configure);assert.ok(at<good.calls.findIndex(c=>c[0]===run));
  assert.ok(at>good.calls.findLastIndex(c=>c[0]===(converter?'add':'write')));
  for(const c of good.calls.filter(c=>/^(map|geometry)-(length|copy)$/.test(c[0])))assert.equal(c[1],token);
  assert.deepEqual([...good.freed].sort((a,b)=>a-b),[...good.allocations].sort((a,b)=>a-b));
  const owned=converter?reply.sourceMapGeometryPayload:reply.sourceMapPayload;good.heap.fill(0);assert.deepEqual([...owned],converter?[21,22]:[11,12]);
  assert.equal(converter?reply.sourceMapPayload:reply.sourceMapGeometryPayload,null,'converter geometry and earlier map are separate replies');
  for(const options of converter?[{overGeometry:true},{shortGeometry:true},{badPointer:true}]:[{overMap:true},{shortMap:true},{badPointer:true}]){
   const got=await attempt(converter,options),r=got.messages.at(-1).reply;assert.equal(r.outcome,'native-return');assert.ok(r.files.length>0);
   assert.equal(converter?r.sourceMapGeometryPayload:r.sourceMapPayload,null);assert.ok(got.freed.every(p=>p>0&&p<got.heap.length));
  }
  for(const options of [{profile:8},{profile:10},{version:8},{version:12},{ordinal:0},{ordinal:4294967296},{missingExport:true},converter?{abi:2}:{abi:0}]){
   const got=await attempt(converter,options);assert.ok(!got.calls.some(c=>c[0]===run),JSON.stringify({converter,options}));
  }
  const failed=await attempt(converter,{failure:true});assert.ok(!failed.calls.some(c=>/^(map|geometry)-length$/.test(c[0])));
 }
 const wrongFactory=await attempt(false,{oldFactory:true});assert.ok(!wrongFactory.calls.some(c=>c[0]==='compile'));
 console.log('Xe FFI modeled PASS: exact createXeMaps/ABI1 and XDV ABI3, setup/configure/native ordering, distinct7/8 tokens, owned bounded copies, optional output preservation, invalid cell/ABI/export rejection.');
})().catch(e=>{console.error(e);process.exitCode=1;});
