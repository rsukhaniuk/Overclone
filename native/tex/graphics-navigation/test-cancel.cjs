'use strict';
const {Worker,isMainThread,parentPort,workerData}=require('node:worker_threads');
const {spawnSync}=require('node:child_process');const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
if(!isMainThread){
 if(workerData.kind==='lua'){process.argv[2]='runaway';require('../synctex-fixtures/lua/test-runtime.cjs');}
 else (async()=>{
  let active=false,witness=false;const wasm=fs.readFileSync(path.join(workerData.candidate,'oc-dvi.wasm'));
  const m=await require(path.join(workerData.candidate,'oc-dvi.js'))({instantiateWasm(imports,done){const grow=imports.env.emscripten_resize_heap;assert.equal(typeof grow,'function');imports.env.emscripten_resize_heap=function(...a){if(active&&!witness){witness=true;parentPort.postMessage('native-grow');}return grow(...a);};const i=new WebAssembly.Instance(new WebAssembly.Module(wasm),imports);done(i);return i.exports;}});
  const put=b=>{const p=m._malloc(b.length||1);m.HEAPU8.set(b,p);return p;},str=s=>put(Buffer.from(s+'\0'));
  const add=(n,b,r)=>{const p=str(n),q=put(b);assert.equal(m._oc_dvi_add_file(p,q,b.length,r),0);m._free(p);m._free(q);};
  for(const n of fs.readdirSync(workerData.resources))add(n,fs.readFileSync(path.join(workerData.resources,n)),1);
  add('input.bin',fs.readFileSync(workerData.fixture),0);assert.equal(m._oc_dvi_graphics_abi(),1);assert.equal(m._oc_dvi_geometry_configure(1,901),1);const p=str('input.bin');active=true;const rc=m._oc_dvi_run(p);active=false;parentPort.postMessage({completed:true,rc,witness});
 })().catch(e=>{console.error(e);process.exitCode=1;});
}else(async()=>{
 const out=process.env.RESULTS;assert.ok(out&&!fs.existsSync(out));fs.mkdirSync(out,{recursive:true});const rows=[];
 for(const kind of ['lua','dvi','xe']){
  const candidate=kind==='lua'?process.env.CANDIDATE:kind==='dvi'?process.env.DVI_CONVERTER:process.env.XE_CONVERTER;
  const resources=kind==='dvi'?'/control/r2-dvi-maps/evidence/regressions/resources':'/control/r2-xe-maps/build/cancel-resources';
  const fixture=kind==='dvi'?'/control/r2-dvi-maps/evidence/regressions/cap.dvi':'/control/r2-xe-maps/evidence/final/runtime/pages40000.xdv';
  let seen=false,completed=false,at=0;const start=performance.now();const w=new Worker(__filename,{workerData:{kind,candidate,resources,fixture},env:{...process.env,CANDIDATE:candidate,LUA_EXPECTED_ABI:'5',RESULTS:path.join(out,kind+'-cancelled')},resourceLimits:{maxOldGenerationSizeMb:1024}});
  const result=await new Promise((resolve,reject)=>{const timer=setTimeout(()=>{void w.terminate();reject(new Error('Actual native cancellation witness timeout'));},30000);w.on('error',e=>{clearTimeout(timer);reject(e);});w.on('message',v=>{if(v===(kind==='lua'?'runaway':'native-grow')){seen=true;at=performance.now();void w.terminate();}else completed=true;});w.on('exit',code=>{clearTimeout(timer);resolve({code,totalMs:performance.now()-start,terminationMs:at?performance.now()-at:null});});});
  assert.ok(seen);assert.equal(completed,false);assert.equal(result.code,1);
  const recovery=path.join(out,kind+'-recovery');const args=kind==='lua'?[path.join(__dirname,'test-lua-states.cjs'),'scoped-axis']:[path.join(__dirname,'reproduce.cjs'),kind,'convert',recovery,process.env.RECOVERY_ROOT+'/'+kind+'-pass3'];
  const p=spawnSync(process.execPath,args,{env:{...process.env,CANDIDATE:process.env.CANDIDATE,CONVERTER:candidate,RESULTS:recovery},encoding:'utf8',timeout:60000,maxBuffer:2*1024*1024});fs.writeFileSync(path.join(out,kind+'-recovery.log'),(p.stdout||'')+(p.stderr||''));assert.ifError(p.error);assert.equal(p.status,0);
  rows.push({kind,actualWitness:kind==='lua'?'Native TeX/Lua write followed by infinite native loop':'Native emscripten_resize_heap import DURING synchronous conversion',physicalTermination:true,...result,recoveryExit:p.status});
 }
 fs.writeFileSync(path.join(out,'receipt.json'),JSON.stringify({schemaVersion:1,status:'pass',rows},null,2)+'\n');console.log('all3 actual native cancellation/recovery pass');
})().catch(e=>{console.error(e);process.exitCode=1;});
