'use strict';
// Genuine pinned Perl/Biber WASM through the unchanged PRODUCT worker in Node VM.
// Fresh physical Node worker per case; this is not browser/C#/offline acceptance.
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto');
const{Worker,isMainThread,parentPort,workerData}=require('node:worker_threads');
const{createVm,frame,identity,workerPath}=require('./compiler-biber-worker-ffi.test.cjs');
const sha=b=>crypto.createHash('sha256').update(b).digest('hex');
async function attempt(d){
 const artifacts={};for(const [name,[hash,length]]of Object.entries(identity)){
  const filename=name==='data'?'biber.data':name==='loader'?'biber.js':'biber.wasm',b=fs.readFileSync(path.join(d.runtime,'files',filename));
  assert.equal(b.length,length);assert.equal(sha(b),hash);artifacts[name]=new Uint8Array(b);}
 const harness=createVm(d.source,{importScripts(url,context){assert.equal(url,'blob:integrity-checked-biber-loader');
  require('node:vm').runInContext(Buffer.from(artifacts.loader).toString(),context,{filename:'genuine-pinned-biber.js'});}});
 const boot={operation:'boot',loader:artifacts.loader,wasm:artifacts.wasm,bootstrapData:artifacts.data,loaderUrl:'blob:integrity-checked-biber-loader'};
 const badBoot=d.id.startsWith('boot-');
 if(badBoot){
  const [,kind,mode]=d.id.split('-'),key=kind==='data'?'bootstrapData':kind;
  if(mode==='missing')delete boot[key];else if(mode==='oversize')boot[key]=new Uint8Array(boot[key].length+1);
  else if(mode==='short')boot[key]=boot[key].slice(0,-1);else{boot[key]=boot[key].slice();boot[key][boot[key].length-1]^=1;}
 }
 const ready=await harness.message(boot);
 if(badBoot){assert.equal(ready.kind,'boot-error');assert.equal(harness.calls.imports,0);assert.equal(harness.calls.wasmModules,0);return{id:d.id,status:'pass',refusedBeforeModule:true,calls:harness.calls};}
 assert.equal(ready.kind,'ready',JSON.stringify(harness.calls));assert.equal(harness.calls.wasmModules,1);assert.equal(harness.calls.wasmInstances,1);
 const fixture=JSON.parse(fs.readFileSync(d.fixture));const request=frame();request.files=fixture.files.map(row=>{
  const b=fs.readFileSync(path.resolve(path.dirname(d.fixture),row.source));assert.equal(b.length,row.length);assert.equal(sha(b),row.sha256);return{name:row.name,bytes:new Uint8Array(b)};});
 if(d.id==='unicode-job'){request.tag.engineId='xelatex';request.selectedRoot='тези/розділ 😀.tex';request.mainEntry='розділ 😀.bcf';request.generatedInputNames=[request.mainEntry];request.outputs=['розділ 😀.bbl','розділ 😀.blg'];request.files.find(f=>f.name===fixture.entry).name=request.mainEntry;}
 if(d.id==='malformed-bcf')request.files.find(f=>f.name===request.mainEntry).bytes=new TextEncoder().encode('<broken>');
 if(d.id==='terminal-quota')request.maxLogBytes=1;
 if(d.id==='output-quota')request.maxOutputBytes=1;
 if(d.id==='remote-source'||d.id==='traversing-source'){
  const f=request.files.find(f=>f.name===request.mainEntry),text=Buffer.from(f.bytes).toString();assert.ok(text.includes('references.bib'));
  f.bytes=new TextEncoder().encode(text.replaceAll('references.bib',d.id==='remote-source'?'https://example.invalid/untrusted.bib':'../../outside.bib'));}
 const expectedInputs=request.files.map(f=>({name:f.name,length:f.bytes.length,sha256:sha(f.bytes)}));
 const done=await harness.message(request);assert.equal(done.kind,'done');const reply=done.reply;
 assert.deepEqual(reply.tag,request.tag);assert.equal(reply.entered,true);assert.deepEqual(reply.inputs,expectedInputs);
 assert.equal(reply.loaderSha256,identity.loader[0]);assert.equal(reply.wasmSha256,identity.wasm[0]);assert.equal(reply.bootstrapSha256,identity.data[0]);
 assert.ok(reply.elapsedMs>=0);assert.ok(reply.heapBytes<=536870912);assert.equal(harness.calls.network,0);
 const positive=['positive-one-shot','unicode-job'].includes(d.id);let bbl;
 if(positive){assert.equal(reply.outcome,'native-return');assert.equal(reply.nativeReturn,0);
  bbl=reply.files.find(f=>f.name.endsWith('.bbl')).bytes;const text=new TextDecoder('utf-8',{fatal:true}).decode(bbl);
  for(const key of fixture.expectedKeys)assert.ok(text.includes('\\entry{'+key+'}'));for(const value of fixture.expectedText)assert.ok(text.includes(value));
  assert.match(reply.log+'\n'+Buffer.from(reply.files.find(f=>f.name.endsWith('.blg')).bytes).toString(),/This is Biber 2\.19/);
  assert.equal(bbl.length,1493);assert.equal(sha(bbl),'6d33fa3a1479358fcbd9c828f53c689e560adb02adbeeb4ae4a668d40c0e0b6d');
  if(d.id==='positive-one-shot'){const duplicate=await harness.message(request);assert.equal(duplicate.reply.entered,false);assert.equal(duplicate.reply.outcome,'setup-failure');assert.deepEqual(duplicate.reply.files,[]);}
 }else{assert.deepEqual(reply.files,[]);assert.notEqual(reply.nativeReturn,0);
  if(d.id.endsWith('quota')){assert.equal(reply.outcome,'resource-limit');assert.equal(reply.nativeReturn,-3);}else{assert.equal(reply.outcome,'native-return');assert.ok(reply.nativeReturn>0);}}
 return{id:d.id,status:'pass',outcome:reply.outcome,nativeReturn:reply.nativeReturn,entered:reply.entered,inputs:reply.inputs,
  loaderSha256:reply.loaderSha256,wasmSha256:reply.wasmSha256,bootstrapSha256:reply.bootstrapSha256,
  outputs:reply.files.map(f=>({name:f.name,length:f.bytes.length,sha256:sha(f.bytes)})),bbl,
  elapsedMs:reply.elapsedMs,heapBytes:reply.heapBytes,calls:harness.calls};
}
if(!isMainThread)attempt(workerData).then(result=>parentPort.postMessage(result)).catch(e=>parentPort.postMessage({id:workerData.id,status:'fail',message:String(e.stack)}));
else(async()=>{
 const runtime=process.env.BIBER_COMPILER_RUNTIME_DIR,fixture=process.env.BIBER_ACTUAL_FIXTURE_JSON,out=process.env.BIBER_WORKER_RESULTS;
 if(!runtime||!fixture||!out){console.log('SKIP genuine Biber product-worker WASM: set BIBER_COMPILER_RUNTIME_DIR, BIBER_ACTUAL_FIXTURE_JSON and fresh BIBER_WORKER_RESULTS.');return;}
 assert.ok(!fs.existsSync(out),'fresh results directory required');fs.mkdirSync(out,{recursive:true});const source=fs.readFileSync(workerPath,'utf8');
 const cases=['positive-one-shot','unicode-job','malformed-bcf','terminal-quota','output-quota','remote-source','traversing-source',
  ...['loader','wasm','data'].flatMap(kind=>['missing','short','oversize','corrupt'].map(mode=>'boot-'+kind+'-'+mode))];
 const selected=process.env.BIBER_WORKER_CASE?[process.env.BIBER_WORKER_CASE]:cases;
 assert.ok(selected.every(id=>cases.includes(id)),'known focused case required');
 const results=[];
 for(const id of selected){const row=await new Promise((resolve,reject)=>{const w=new Worker(__filename,{workerData:{id,runtime:path.resolve(runtime),fixture:path.resolve(fixture),source},env:{}});
  const timer=setTimeout(()=>{w.terminate();reject(Error('case timeout '+id));},60000);w.once('message',async r=>{clearTimeout(timer);await w.terminate();resolve(r);});w.once('error',e=>{clearTimeout(timer);reject(e);});});
  if(row.bbl){fs.writeFileSync(path.join(out,id+'.bbl'),row.bbl);delete row.bbl;}fs.writeFileSync(path.join(out,id+'.json'),JSON.stringify(row,null,2)+'\n');results.push(row);
  console.log(JSON.stringify({id,status:row.status,message:row.message}));if(row.status!=='pass')break;}
 const summary={schemaVersion:1,status:results.length===selected.length&&results.every(r=>r.status==='pass')?'pass':'fail',requestedCases:selected,allCaseCount:cases.length,
  scope:'Genuine pinned Biber04 through unchanged product worker in isolated Node VM; not browser/C#/offline acceptance',
  workerSha256:sha(source),component:identity,results};fs.writeFileSync(path.join(out,'summary.json'),JSON.stringify(summary,null,2)+'\n');if(summary.status!=='pass')process.exitCode=1;
})().catch(e=>{console.error(e);process.exitCode=1;});
