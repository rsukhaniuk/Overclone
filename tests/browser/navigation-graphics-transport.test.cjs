// Actual bridge/admission with MODELED Worker/controller. NOT native/browser proof.
'use strict';
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const {createHash}=require('node:crypto');
const sha=b=>createHash('sha256').update(b).digest('hex');
function helper(){
 if(!process.env.GRAPHICS_NAVIGATION_BRIDGE_PROPOSAL)return require('./restricted-worker-test-helpers.cjs');
 // Worker owns NO product bridge write. Explicitly test the exact CONTROL-ONLY
 // proposal until root applies/reviews it; never relabel this as promoted code.
 const file=path.resolve(process.env.GRAPHICS_NAVIGATION_BRIDGE_PROPOSAL),base=path.resolve(__dirname,'../../src/Client/wwwroot/js/compile-port-bridge.js');
 const manifest=JSON.parse(fs.readFileSync(path.resolve(file,'../../../../../../preimages.json'),'utf8'));
 const entry=manifest.files['src/Client/wwwroot/js/compile-port-bridge.js'];assert(entry);
 assert.equal(sha(fs.readFileSync(base)),entry.preimageSha256);assert.equal(sha(fs.readFileSync(file)),entry.proposedSha256);
 const helperFile=path.join(__dirname,'restricted-worker-test-helpers.cjs'),m={exports:{}};
 const proxy={...fs,readFileSync(p,...args){return fs.readFileSync(path.resolve(p)===base?file:p,...args);}};
 const loader=vm.runInThisContext('(function(require,module,__dirname){'+fs.readFileSync(helperFile,'utf8')+'\n})',{filename:helperFile});
 loader(n=>n==='node:fs'?proxy:require(n),m,__dirname);console.log('MODELED explicit UNAPPLIED bridge proposal SHA256 '+entry.proposedSha256);return m.exports;
}
const {harness,frame,origin}=helper();
function request(phase,engine,tool='latex',profile=10){const r=frame(phase,`${profile}-${phase}-${engine}-${tool}`);Object.assign(r.tag,{profile,version:phase,engineId:engine,tool,ordinal:7});
 if(tool==='dvipdfmx'){r.mainEntry='main'+(engine==='xelatex'?'.xdv':'.dvi');r.files.push({name:r.mainEntry,bytes:new Uint8Array([247,engine==='xelatex'?7:2])});}return r;}
function settle(h,r,extra={}){const operation=h.api.execute(r),worker=h.stats.workers.at(-1);worker.onmessage({data:{kind:'ready'}});
 worker.onmessage({data:{kind:'done',reply:{tag:r.tag,outcome:'native-return',nativeReturn:0,entered:true,log:'',files:[{name:r.outputs[0],bytes:new Uint8Array([37,80,68,70,45])}],inputs:[],...extra}}});return{operation,worker};}
(async()=>{
 const h=harness();await h.api.provision();await h.api.provisionDvi();await h.api.provisionXetex();await h.api.provisionLuatex();let cells=0;
 for(const [phase,engine,role]of [[6,'pdftex','compile'],[15,'latex-dvi','compile'],[16,'xelatex','xetex'],[17,'lualatex','luatex']]){
  for(const tool of ['latex','bibtex']){const {operation,worker}=settle(h,request(phase,engine,tool));assert.equal((await operation).nativeReturn,0);assert(worker.url.startsWith(origin+'/__oop/native-worker/'+(tool==='bibtex'?'compile':role)+'?'));assert.equal(worker.terminated,1);cells++;}
 }
 for(const [phase,engine]of [[15,'latex-dvi'],[16,'xelatex']])assert.equal((await settle(h,request(phase,engine,'dvipdfmx')).operation).nativeReturn,0);
 const before=h.stats.workers.length;
 for(const [phase,engine,tool,profile]of [[12,'latex-dvi','latex',10],[13,'xelatex','latex',10],[14,'lualatex','latex',10],[15,'latex-dvi','latex',9],[16,'xelatex','latex',9],[17,'lualatex','latex',9],
  [15,'xelatex','latex',10],[16,'lualatex','latex',10],[17,'latex-dvi','dvipdfmx',10],[10,'pdftex','latex',10],[11,'pdftex','latex',10],[6,'pdftex','latex',11],[15,'latex-dvi','latex',2147483647]])assert.throws(()=>h.api.execute(request(phase,engine,tool,profile)));
 assert.equal(h.stats.workers.length,before,'Reject before Worker/URL allocation');
 const raw=new Uint8Array([1,2]),geo=new Uint8Array([3,4]);
 for(const [phase,engine,tool,extra]of [[15,'latex-dvi','latex',{sourceMapPayload:raw}],[16,'xelatex','latex',{sourceMapPayload:raw}],
  [17,'lualatex','latex',{sourceMapPayload:raw,sourceMapGeometryPayload:geo}],[15,'latex-dvi','dvipdfmx',{sourceMapGeometryPayload:geo}],[16,'xelatex','dvipdfmx',{sourceMapGeometryPayload:geo}]]){
  const r=await settle(h,request(phase,engine,tool),extra).operation;assert.equal(r.nativeReturn,0);if(extra.sourceMapPayload)assert.deepEqual([...r.sourceMapPayload],[1,2]);if(extra.sourceMapGeometryPayload)assert.deepEqual([...r.sourceMapGeometryPayload],[3,4]);
 }
 for(const extra of [{sourceMapGeometryPayload:new Uint8Array(131073)},{sourceMapGeometryPayload:[]}]){const r=await settle(h,request(16,'xelatex','dvipdfmx'),extra).operation;assert.equal(r.nativeReturn,0);assert.equal(r.sourceMapGeometryPayload,null);}
 for(const [phase,engine,tool,extra]of [[16,'xelatex','latex',{sourceMapGeometryPayload:geo}],[15,'latex-dvi','bibtex',{sourceMapPayload:raw}],
  [16,'xelatex','dvipdfmx',{sourceMapPayload:raw}],[17,'lualatex','latex',{nativeReturn:1,sourceMapPayload:raw}],[6,'pdftex','latex',{sourceMapGeometryPayload:geo}]])assert.equal((await settle(h,request(phase,engine,tool),extra).operation).outcome,'protocol-failure');
 const held=h.api.execute(request(17,'lualatex')),w=h.stats.workers.at(-1),late=w.onmessage;
 h.api.terminate(request(17,'lualatex').tag.job,1);assert.equal((await held).outcome,'cancelled');assert.equal(w.terminated,1);
 late({data:{kind:'done',reply:{tag:request(17,'lualatex').tag,sourceMapPayload:new Uint8Array([9])}}});assert.equal(h.api.observations().active,0);
 const timed=h.api.execute(request(16,'xelatex','dvipdfmx'));h.timeoutPhase();assert.equal((await timed).outcome,'transport-timeout');
 // Genuine old wire tuples still route, no unprofiled re-interpretation.
 for(const [profile,phase,engine]of [[5,7,'latex-dvi'],[5,8,'xelatex'],[5,9,'lualatex'],[8,12,'latex-dvi'],[8,8,'xelatex'],[8,14,'lualatex'],[9,12,'latex-dvi'],[9,13,'xelatex'],[9,14,'lualatex']])assert.equal((await settle(h,request(phase,engine,'latex',profile)).operation).nativeReturn,0);
 assert.equal(h.api.observations().started,h.api.observations().settled);assert.equal(h.stats.urls.length,h.stats.revoked.length);h.api.dispose();
 console.log('Graphics bridge MODELED PASS: '+cells+' P10 producer/backend cells plus2converter cells, old tuples, closed unknown11, optional/wrong/failure fields, physical Worker.cancel/timeout and late settlement.');
})().catch(e=>{console.error(e);process.exitCode=1;});
