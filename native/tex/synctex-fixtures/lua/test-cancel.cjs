'use strict';
const {fs,path,assert,sha,candidate}=require('./runtime.cjs');
const {Worker}=require('node:worker_threads');
const {spawnSync}=require('node:child_process');
const out=process.env.RESULTS||'/control/r2-lua-maps/evidence';
(async()=>{
  const target=path.join(out,'cancelled-runtime');fs.mkdirSync(target,{recursive:true});
  assert.ok(!fs.existsSync(path.join(target,'runaway.pdf')),'use a fresh cancellation output');
  const began=Date.now();let entered=false,killed=0;
  let checks=1; // Fresh output-directory assertion above.
  const worker=new Worker(path.join(__dirname,'test-runtime.cjs'),{argv:['runaway'],env:{...process.env,RESULTS:target},resourceLimits:{maxOldGenerationSizeMb:1024}});
  const deadline=setTimeout(()=>void worker.terminate(),20000);
  worker.on('message',message=>{if(message==='runaway'){entered=true;setTimeout(()=>{killed=Date.now();void worker.terminate();},250);}});
  const code=await new Promise((resolve,reject)=>{worker.once('exit',resolve);worker.once('error',reject);});
  clearTimeout(deadline);
  assert.ok(entered&&killed>0,'actual Lua runaway reached WITH maps armed before physical termination');
  assert.equal(code,1);checks+=2; // Loop reached and physical termination exit.
  for(const extension of ['pdf','synctex','geometry.json','json']){assert.ok(!fs.existsSync(path.join(target,'runaway.'+extension)));++checks;}
  const terminatedMs=Date.now()-killed;
  const recovery=spawnSync(process.execPath,[path.join(__dirname,'test-runtime.cjs'),'positive'],{env:{...process.env,RESULTS:path.join(out,'recovered-runtime')},encoding:'utf8',timeout:30000,maxBuffer:2*1024*1024});
  fs.writeFileSync(path.join(out,'cancel-recovery.log'),(recovery.stdout||'')+(recovery.stderr||''));
  assert.ifError(recovery.error);assert.equal(recovery.status,0);checks+=2;
  const result={schemaVersion:1,status:'pass',checks,skips:0,enteredActualLuaLoop:true,mapsArmed:true,physicalWorkerTermination:true,workerExit:code,terminationMs:terminatedMs,totalMs:Date.now()-began,recoveredViaFreshModule:true,recoveryCommand:[process.execPath,path.join(__dirname,'test-runtime.cjs'),'positive'],recoveryExit:recovery.status,recoveryReceipt:'recovered-runtime/positive.json',wasmSha256:sha(fs.readFileSync(path.join(candidate,'luatex.wasm')))};
  fs.writeFileSync(path.join(out,'cancel.json'),JSON.stringify(result,null,2)+'\n');console.log(JSON.stringify(result));
})().catch(error=>{console.error(error);process.exitCode=1;});
