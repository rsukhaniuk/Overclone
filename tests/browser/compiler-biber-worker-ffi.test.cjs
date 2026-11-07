'use strict';
// MODELED API tests of the unchanged product worker. No native/Biber proof.
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),vm=require('node:vm'),crypto=require('node:crypto');
const workerPath=path.join(__dirname,'../../src/Client/wwwroot/js/biber-port-worker.js');
const identity={loader:['e8cece7433da70dc83372d18ce314eea7c911467cdfc9b5cb3a877cf80d1dee7',777719],
 wasm:['f68864e55dafda71bf25e6903f0f9490a7c4fb14302cb3f0650e56dbc36d0201',8698666],
 data:['28eaeec67f837530073d1de54ba3ab92a7fc8e96bcd83255eb6aa1a0a037c10b',18216642]};
function createVm(source,environment={}){
 const messages=[],calls={imports:0,wasmModules:0,wasmInstances:0,network:0};let context;
 const wasm=Object.fromEntries(Object.getOwnPropertyNames(WebAssembly).map(k=>[k,WebAssembly[k]]));
 wasm.Module=function(bytes){calls.wasmModules++;return new WebAssembly.Module(bytes);};
 wasm.Instance=function(module,imports){calls.wasmInstances++;return new WebAssembly.Instance(module,imports);};
 const sandbox={TextEncoder,TextDecoder,Uint8Array,Int8Array,Uint16Array,Int16Array,Uint32Array,Int32Array,Float32Array,Float64Array,
  BigInt64Array,BigUint64Array,ArrayBuffer,DataView,WebAssembly:wasm,crypto:environment.crypto||crypto.webcrypto,performance,
  setTimeout,clearTimeout,WorkerGlobalScope:class{},location:{href:'blob:trusted-biber-worker',pathname:'/biber-port-worker.js'},
  console:{log(){},warn(){},error(){}},postMessage(message){messages.push(structuredClone(message));},
  fetch(){calls.network++;throw Error('test forbids network');},
  XMLHttpRequest:class{constructor(){calls.network++;throw Error('test forbids XHR');}},
  WebSocket:class{constructor(){calls.network++;throw Error('test forbids sockets');}},
  importScripts(url){calls.imports++;environment.importScripts(url,context);}};
 // Count attempted network APIs even after the product installs its deny stubs.
 let fetch=sandbox.fetch,xhr=sandbox.XMLHttpRequest;
 Object.defineProperty(sandbox,'fetch',{configurable:true,get:()=>fetch,set(fn){fetch=(...args)=>{calls.network++;return fn(...args);};}});
 Object.defineProperty(sandbox,'XMLHttpRequest',{configurable:true,get:()=>xhr,set(Type){xhr=class{constructor(...args){calls.network++;return Reflect.construct(Type,args);}};}});
 sandbox.self=sandbox;context=vm.createContext(sandbox);vm.runInContext(source,context,{filename:'biber-port-worker.js'});
 return{context,messages,calls,async message(data){await context.onmessage({data:structuredClone(data)});return messages.at(-1);}};
}
function frame(engineId='lualatex'){
 const selectedRoot=engineId==='lualatex'?'тези/my thesis.tex':'тези/my thesis.tex',job=engineId==='lualatex'?'result':'my thesis';
 return{operation:'biber',bibliographyTool:'biber',tag:{profile:13,version:10,tool:'biber',engineId,ordinal:2,nonce:'owned-nonce',runtimeIdentity:'1'.repeat(64)},
  selectedRoot,mainEntry:job+'.bcf',files:[{name:job+'.bcf',bytes:new Uint8Array([1])},{name:'references.bib',bytes:new Uint8Array([2])}],
  outputs:[job+'.bbl',job+'.blg'],generatedInputNames:[job+'.bcf'],runtimeResourceNames:[],maxLogBytes:262144,maxOutputBytes:8388608};
}
async function modeled(source,change,options={}){
 const calls={configure:0,add:0,run:0,copies:0},heap=new Uint8Array(32*1048576);let next=32;
 let factoryEntered,releaseFactory;const entered=new Promise(resolve=>factoryEntered=resolve),released=new Promise(resolve=>releaseFactory=resolve);
 const output=[Buffer.from('MODELED BBL'),Buffer.from('MODELED BLG'),options.badUtf8?Buffer.from([255]):Buffer.from('MODELED terminal')];
 const module={HEAPU8:heap,_malloc(n){const p=next;next+=n+8;return options.badAllocation?heap.length:p;},_free(){},_oc_biber_abi:()=>options.abi??1,
  _oc_biber_configure(){calls.configure++;return 0;},_oc_biber_add_file(){calls.add++;return 0;},_oc_biber_run(){calls.run++;return options.status??0;},
  _oc_biber_output_size(k){return options.badSize&&k===0?8388609:output[k].length;},
  _oc_biber_copy_output(k,p,n){calls.copies++;heap.set(output[k],p);return options.shortCopy?n-1:n;},_oc_biber_error(){return 0;}};
 if(options.missing)delete module[options.missing];
 const fakeCrypto={subtle:{async digest(algorithm,data){const known=Object.values(identity).find(([,length])=>length===data.byteLength);
  return known?Uint8Array.from(Buffer.from(known[0],'hex')).buffer:crypto.webcrypto.subtle.digest(algorithm,data);}}};
 const harness=createVm(source,{crypto:fakeCrypto,importScripts(url,ctx){ctx.createBiber=async args=>{
  const preload=args.getPreloadedPackage('biber.data',identity.data[1]);assert.equal(preload.byteLength,identity.data[1]);factoryEntered();
  if(options.concurrentDuringFactory)await released;return module;};}});
 const boot={operation:'boot',loader:new Uint8Array(identity.loader[1]),wasm:new Uint8Array(identity.wasm[1]),bootstrapData:new Uint8Array(identity.data[1]),loaderUrl:'blob:modeled-loader'};
 if(options.failedBootRetry){
  await harness.message({...boot,bootstrapData:undefined});await harness.message(boot);
  assert.equal(harness.calls.imports,0);assert.ok(harness.messages.every(m=>m.kind==='boot-error'));return{harness,calls};
 }
 if(options.concurrentDuringFactory){
  const first=harness.message(boot);await entered;await harness.message(boot);releaseFactory();await first;
  assert.equal(harness.calls.imports,1);assert.ok(harness.messages.every(m=>m.kind==='boot-error'));return{harness,calls};
 }
 if(options.concurrentBoot){
  await Promise.all([harness.message(boot),harness.message(boot)]);
  assert.ok(harness.calls.imports<=1,'concurrent boot must initialize at most one module');
  assert.ok(harness.messages.filter(m=>m.kind==='ready').length<=1);
  assert.ok(harness.messages.some(m=>m.kind==='boot-error'),'repeated bootstrap is refused');
  return{harness,calls};
 }
 await harness.message(boot);
 if(options.missing||options.abi!==undefined&&options.abi!==1){assert.equal(harness.messages.at(-1).kind,'boot-error');assert.equal(calls.run,0);return;}
 assert.equal(harness.messages.at(-1).kind,'ready');const request=Object.hasOwn(options,'rawFrame')?options.rawFrame:frame();change?.(request);
 const done=await harness.message(request);return{harness,calls,done,request};
}
async function run(){
 const source=fs.readFileSync(workerPath,'utf8');let count=0;
 for(const engine of ['pdftex','latex-dvi','xelatex','lualatex']){
  const result=await modeled(source,d=>Object.assign(d,frame(engine)));assert.equal(result.done.reply.outcome,'native-return');assert.equal(result.calls.run,1);count++;}
 const invalid=[d=>d.tag.profile=12,d=>d.tag.profile=11,d=>d.tag.version=9,d=>d.tag.version=11,d=>d.tag.tool='bibtex',d=>d.operation='latex',
  d=>d.bibliographyTool='none',d=>d.tag.engineId='untrusted',d=>d.tag.nonce='',d=>d.tag.runtimeIdentity='0',d=>d.tag.ordinal=0,
  d=>d.selectedRoot='../main.tex',d=>d.mainEntry='other.bcf',d=>d.generatedInputNames=[],d=>d.generatedInputNames.push('other.bcf'),
  d=>d.runtimeResourceNames.push('biber.data'),d=>d.maxLogBytes=0,d=>d.maxLogBytes=262145,d=>d.maxOutputBytes=0,d=>d.maxOutputBytes=8388609,
  d=>d.outputs.push('result.pdf'),d=>d.outputs[1]=d.outputs[0],d=>d.files=[],d=>d.files[0].bytes=new Uint8Array(),
  d=>d.files[1].name='../outside.bib',d=>d.files[1].name='https://example.invalid/a.bib',d=>d.files[1].bytes=[1],
  d=>d.files.push({name:'REFERENCES.BIB',bytes:new Uint8Array()}),
  d=>d.files.push({name:'café.bib',bytes:new Uint8Array()},{name:'cafe\u0301.bib',bytes:new Uint8Array()}),
  d=>d.files.push({name:'directory',bytes:new Uint8Array()},{name:'directory/item.bib',bytes:new Uint8Array()}),
  d=>d.files.push({name:'result.bbl',bytes:new Uint8Array()}),
  d=>d.files.push(...Array.from({length:63},(_,i)=>({name:'file'+i+'.bib',bytes:new Uint8Array()}))),
  d=>d.files[1].bytes=new Uint8Array(8388608),d=>d.files[1].bytes=new Uint8Array(8388609)];
 for(const change of invalid){const r=await modeled(source,change);assert.equal(r.calls.run,0);assert.equal(r.done.reply.entered,false);assert.deepEqual(r.done.reply.files,[]);count++;}
 for(const option of [{abi:0},{abi:2},{missing:'_oc_biber_run'},{missing:'_oc_biber_copy_output'}]){await modeled(source,null,option);count++;}
 for(const option of [{badAllocation:true},{badSize:true},{shortCopy:true},{badUtf8:true}]){
  const r=await modeled(source,null,option);assert.notEqual(r.done.reply.outcome,'native-return');assert.deepEqual(r.done.reply.files,[]);count++;}
 for(const status of [2,-3,-4]){const r=await modeled(source,null,{status});assert.equal(r.done.reply.nativeReturn,status);assert.deepEqual(r.done.reply.files,[]);assert.equal(r.calls.copies,0);count++;}
 const once=await modeled(source);const second=await once.harness.message(once.request);assert.equal(second.reply.entered,false);assert.deepEqual(second.reply.files,[]);assert.equal(once.calls.run,1);count++;
 await modeled(source,null,{concurrentBoot:true});count++;
 await modeled(source,null,{failedBootRetry:true});count++;
 await modeled(source,null,{concurrentDuringFactory:true});count++;
 for(const rawFrame of [null,undefined]){const r=await modeled(source,null,{rawFrame});assert.equal(r.done.reply.outcome,'setup-failure');assert.equal(r.calls.run,0);assert.deepEqual(r.done.reply.files,[]);count++;}
 console.log(JSON.stringify({status:'pass',scope:'MODELED API, unchanged Biber product worker; not native/browser proof',cases:count,workerSha256:crypto.createHash('sha256').update(source).digest('hex')}));
}
module.exports={createVm,frame,identity,workerPath};
if(require.main===module)run().catch(e=>{console.error(e);process.exitCode=1;});
