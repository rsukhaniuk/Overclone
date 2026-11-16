'use strict';
// Reuse the real maps-armed Lua runaway. Recovery uses the current unchanged
// package workflow, preserving the historical raw-filename-format test intact.
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict'),{Worker}=require('node:worker_threads'),{spawnSync}=require('node:child_process'),{createHash}=require('node:crypto');
const [candidate,runtime,suite,carry,out]=process.argv.slice(2);assert.ok(!fs.existsSync(out));fs.mkdirSync(out,{recursive:true});
const pin=b=>({length:b.length,sha256:createHash('sha256').update(b).digest('hex')});
(async()=>{
 const target=path.join(out,'cancelled');fs.mkdirSync(target);const loop=path.join(__dirname,'../synctex-fixtures/lua/test-runtime.cjs');
 const worker=new Worker(loop,{argv:['runaway'],env:{...process.env,CANDIDATE:candidate,SUITE_INPUT:suite,FORMAT_FILE:path.join(runtime,'files/lualatex.fmt'),LUA_EXPECTED_ABI:'5',RESULTS:target},resourceLimits:{maxOldGenerationSizeMb:1024}});
 let entered=false,killed=0;const deadline=setTimeout(()=>void worker.terminate(),20000);
 worker.on('message',s=>{if(s==='runaway'){entered=true;setTimeout(()=>{killed=Date.now();void worker.terminate();},250);}});
 const exit=await new Promise((resolve,reject)=>{worker.once('exit',resolve);worker.once('error',reject);});clearTimeout(deadline);assert.ok(entered&&killed);assert.equal(exit,1);const terminatedMs=Date.now()-killed;
 for(const suffix of ['pdf','synctex','geometry.json','json'])assert.ok(!fs.existsSync(path.join(target,'runaway.'+suffix)));
 const recovered=path.join(out,'recovered'),script=path.join(__dirname,'run-current-project.cjs'),clock=path.join(__dirname,'../synctex-fixtures/dvi/fixed-clock.cjs');
 const command=[process.execPath,'-r',clock,script,'lua','latex',recovered,carry];const r=spawnSync(command[0],command.slice(1),{env:{...process.env,RUNTIME:runtime,LUA_CANDIDATE:candidate,CORPUS_CASE:'packages',REQUIRE_ORIGIN:'1'},encoding:'utf8',timeout:90000,maxBuffer:2*1024*1024});fs.writeFileSync(path.join(out,'recovery.log'),r.stdout+(r.stderr||''));assert.ifError(r.error);assert.equal(r.status,0);
 const receipt=JSON.parse(fs.readFileSync(path.join(recovered,'receipt.json')));assert.equal(receipt.nativeReturn,0);assert.ok(receipt.rawMap.length>0);assert.ok(receipt.geometry.statuses.every(p=>p.status==='available'));assert.ok(fs.statSync(path.join(recovered,'output.pdf')).size>0);
 fs.writeFileSync(path.join(out,'receipt.json'),JSON.stringify({status:'pass',scope:'actual native maps-armed infinite Lua loop, physical worker termination without eligible output, fresh current-package module recovery',enteredActualLuaLoop:true,workerExit:exit,physicalTerminationMs:terminatedMs,recoveryNativeReturn:receipt.nativeReturn,recoveryCommand:command,loader:pin(fs.readFileSync(path.join(candidate,'luatex.js'))),wasm:pin(fs.readFileSync(path.join(candidate,'luatex.wasm'))),testSource:pin(fs.readFileSync(__filename))},null,2)+'\n');console.log('Actual native cancellation and current-package fresh recovery pass');
})().catch(e=>{console.error(e);process.exitCode=1;});
