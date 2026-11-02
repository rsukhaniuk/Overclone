'use strict';
const {Worker,isMainThread,parentPort,workerData}=require('node:worker_threads');
const {spawnSync}=require('node:child_process');
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
if(!isMainThread){
 if(workerData.kind==='producer'){
  process.argv[2]='runaway';require('./test-runtime.cjs');
 }else (async()=>{
  let active=false,witnessed=false;
  const wasm=fs.readFileSync(path.join(workerData.candidate,'oc-dvi.wasm'));
  const m=await require(path.join(workerData.candidate,'oc-dvi.js'))({wasmBinary:wasm,instantiateWasm(imports,done){
   const grow=imports.env.emscripten_resize_heap;assert.equal(typeof grow,'function');
   imports.env.emscripten_resize_heap=function(...args){
    if(active&&!witnessed){witnessed=true;parentPort.postMessage('native-memory-grow');}
    return grow(...args);
   };
   const i=new WebAssembly.Instance(new WebAssembly.Module(wasm),imports);done(i);return i.exports;
  }});
  const alloc=b=>{const p=m._malloc(b.length||1);m.HEAPU8.set(b,p);return p;};
  const str=s=>alloc(Buffer.from(s+'\0'));
  const add=(n,b,r)=>{const p=str(n),q=alloc(b);assert.equal(m._oc_dvi_add_file(p,q,b.length,r),0);m._free(p);m._free(q);};
  for(const n of fs.readdirSync(workerData.resources))add(n,fs.readFileSync(path.join(workerData.resources,n)),1);
  add('cap.dvi',fs.readFileSync(workerData.fixture),0);
  assert.equal(m._oc_dvi_abi_version(),2);assert.equal(m._oc_dvi_geometry_configure(1,701),1);
  const entry=str('cap.dvi');active=true;const rc=m._oc_dvi_run(entry);active=false;
  parentPort.postMessage({completed:true,rc,witnessed});
 })().catch(e=>{console.error(e);process.exitCode=1;});
}else(async()=>{
 const out=process.env.RESULTS;assert.ok(out);fs.mkdirSync(out,{recursive:true});
 const candidate=process.env.CONVERTER;assert.ok(candidate);
 const receipts=[];
 for(const kind of ['producer','converter']){
  const data={kind,candidate,resources:process.env.DVI_RESOURCES,fixture:process.env.CAP_DVI};
  const start=performance.now();let witness=false,completed=false,killAt=0;
  const w=new Worker(__filename,{workerData:data,env:{...process.env,RESULTS:path.join(out,kind+'-cancelled')},resourceLimits:{maxOldGenerationSizeMb:1024}});
  const expected=kind==='producer'?'runaway':'native-memory-grow';
  const result=await new Promise((resolve,reject)=>{
   const timer=setTimeout(()=>{void w.terminate();reject(new Error('native cancellation fixture timed out'));},30000);
   w.on('error',e=>{clearTimeout(timer);reject(e);});
   w.on('message',message=>{
    if(message===expected&&!witness){witness=true;killAt=performance.now();void w.terminate();}
    else {completed=true;}
   });
   w.on('exit',code=>{clearTimeout(timer);resolve({code,totalMs:performance.now()-start,terminationMs:killAt?performance.now()-killAt:null});});
  });
  assert.ok(witness,'actual native work witness');assert.equal(completed,false);assert.equal(result.code,1);
  const recovery=path.join(out,kind+'-recovery');
  const argv=kind==='producer'?[path.join(__dirname,'test-runtime.cjs'),'positive']:[path.join(__dirname,'test-convert.cjs'),'positive'];
  const run=spawnSync(process.execPath,argv,{env:{...process.env,RESULTS:recovery,DVI_INPUT:process.env.RECOVERY_DVI},encoding:'utf8',timeout:60000,maxBuffer:2*1024*1024});
  fs.writeFileSync(path.join(out,kind+'-recovery.log'),(run.stdout||'')+(run.stderr||''));assert.ifError(run.error);assert.equal(run.status,0);
  receipts.push({kind,nativeWitness:expected,geometryOrMapsArmed:true,physicalTermination:true,...result,recoveryExit:run.status,recoveryReceipt:kind+'-recovery/positive.json',scope:kind==='producer'?'Native TeX write16 witness followed by actual infinite TeX loop':'emscripten_resize_heap import invoked DURING the synchronous actual native conversion call, not a pre-call signal'});
 }
 const receipt={schemaVersion:1,status:'pass',skips:0,receipts};fs.writeFileSync(path.join(out,'cancel.json'),JSON.stringify(receipt,null,2)+'\n');console.log(JSON.stringify(receipt));
})().catch(e=>{console.error(e);process.exitCode=1;});
