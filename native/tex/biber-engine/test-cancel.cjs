'use strict';
// Test-only import observation provides a real native Biber witness before the
// parent physically terminates the worker. It does not alter any write/result.
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto'),assert=require('node:assert/strict');
const {Worker,isMainThread,parentPort,workerData}=require('node:worker_threads');
const sha=b=>crypto.createHash('sha256').update(b).digest('hex');
if(!isMainThread)(async()=>{
  const d=workerData,receipt=JSON.parse(fs.readFileSync(path.join(d.component,'receipt.json'))),artifacts={};
  for(const row of receipt.files){const b=fs.readFileSync(path.join(d.component,row.name));assert.equal(b.length,row.length);assert.equal(sha(b),row.sha256);artifacts[row.name]=b;}
  const fixture=JSON.parse(fs.readFileSync(d.fixture));let m,witness=false;
  globalThis.fetch=()=>{throw Error('unexpected network')};globalThis.XMLHttpRequest=class{constructor(){throw Error('unexpected XHR')}};globalThis.WebSocket=class{constructor(){throw Error('unexpected socket')}};
  m=await require(path.join(d.component,'biber.js'))({noInitialRun:true,wasmBinary:artifacts['biber.wasm'],
    getPreloadedPackage(name,n){const b=artifacts['biber.data'];assert.equal(n,b.length);return b.buffer.slice(b.byteOffset,b.byteOffset+b.length);},
    instantiateWasm(imports,done){let hooked=false;for(const ns of Object.values(imports))if(typeof ns.fd_write==='function'){
      const original=ns.fd_write;ns.fd_write=(fd,iov,count,num)=>{const result=original(fd,iov,count,num);
        if(m&&!witness&&result===0){const heap=new DataView(m.HEAPU8.buffer);let text='';for(let i=0;i<count;i++){const p=heap.getUint32(iov+8*i,true),n=heap.getUint32(iov+8*i+4,true);if(n<=262144)text+=Buffer.from(m.HEAPU8.subarray(p,p+n)).toString();}
          if(/This is Biber 2\.19/.test(text)){witness=true;const budget=m.ocBiberVfsSnapshot();assert.equal(budget.installed,true);assert.equal(budget.failed,false);parentPort.postMessage({kind:'native-witness',banner:'This is Biber 2.19',budget});}}
        return result;};hooked=true;}
      assert.ok(hooked,'pinned WASI fd_write import');const instance=new WebAssembly.Instance(new WebAssembly.Module(artifacts['biber.wasm']),imports);done(instance);return instance.exports;},print:()=>{},printErr:()=>{}});
  const blob=(b,fn)=>{b=Buffer.isBuffer(b)?b:Buffer.from(b);const p=m._malloc(b.length||1);m.HEAPU8.set(b,p);try{return fn(p,b.length);}finally{m._free(p);}};
  assert.equal(blob(fixture.entry,(p,n)=>blob('cancel-native-witness',(q,k)=>m._oc_biber_configure(p,n,q,k,262144,8388608))),0);
  let total=0;
  for(const row of fixture.files){let b=fs.readFileSync(path.resolve(path.dirname(d.fixture),row.source));assert.equal(sha(b),row.sha256);
    if(row.role===1&&row.name.endsWith('.bib')){const block='\n@book{cancel-padding-TOKEN,author={Synthetic Author},title={'+('x'.repeat(512))+'},year={2025}}\n';const padding=[];let bytes=b.length;for(let i=0;bytes<6*1048576;i++){const part=block.replace('TOKEN',String(i));padding.push(part);bytes+=Buffer.byteLength(part);}b=Buffer.concat([b,Buffer.from(padding.join(''))]);}
    total+=b.length;assert.ok(total<=8388608);assert.equal(blob(row.name,(p,n)=>blob(b,(q,k)=>m._oc_biber_add_file(row.role,p,n,q,k))),0);}
  const result=m._oc_biber_run();parentPort.postMessage({kind:'unexpected-completion',result});
})().catch(e=>parentPort.postMessage({kind:'error',message:String(e.stack)}));
else(async()=>{
  const[component,fixture,out]=process.argv.slice(2);if(!out)throw Error('usage: node test-cancel.cjs COMPONENT_DIR FIXTURE_JSON FRESH_RESULT_JSON');assert.ok(!fs.existsSync(out));
  const w=new Worker(__filename,{workerData:{component:path.resolve(component),fixture:path.resolve(fixture)},env:{}});
  const receipt=await new Promise((resolve,reject)=>{const timer=setTimeout(()=>{w.terminate();reject(Error('native witness timeout'));},60000);let cancelled=false;const unexpected=[];
    w.on('message',async r=>{if(r.kind!=='native-witness'){unexpected.push(r);if(!cancelled){clearTimeout(timer);await w.terminate();reject(Error(JSON.stringify(r)));}return;}
      assert.ok(!cancelled);cancelled=true;const started=performance.now();const exit=await w.terminate();clearTimeout(timer);
      if(unexpected.length){reject(Error('native operation settled before physical cancellation: '+JSON.stringify(unexpected)));return;}
      resolve({schemaVersion:1,status:'pass',scope:'actual-genuine-Biber-worker-physical-cancellation',nativeWitness:r,terminationMs:performance.now()-started,workerExit:exit,eligibleOutputMessages:0,browserAcceptance:false});});w.once('error',reject);});
  fs.writeFileSync(out,JSON.stringify(receipt,null,2)+'\n');console.log(JSON.stringify(receipt));
})().catch(e=>{console.error(e);process.exitCode=1;});
