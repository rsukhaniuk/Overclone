// Actual bridge with MODELED workers/controllers. Not native/browser proof.
const assert=require('node:assert/strict');
const {harness,frame,origin}=require('./restricted-worker-test-helpers.cjs');
function request(version,engineId,tool='latex',profile=9){
 const r=frame(version,`${profile}-${version}-${engineId}-${tool}`);Object.assign(r.tag,{version,profile,engineId,tool,ordinal:7});
 r.runtimeResourceNames=[];r.generatedInputNames=[];
 if(tool==='dvipdfmx'){r.mainEntry='main'+(engineId==='xelatex'?'.xdv':'.dvi');r.files.push({name:r.mainEntry,bytes:new Uint8Array([247,7])});}
 return r;
}
function settle(h,r,fields={}){
 const operation=h.api.execute(r),worker=h.stats.workers.at(-1);worker.onmessage({data:{kind:'ready'}});
 const reply={tag:r.tag,outcome:'native-return',nativeReturn:0,entered:true,log:'',files:[{name:r.outputs[0],bytes:new Uint8Array([37,80,68,70,45])}],inputs:[],...fields};
 worker.onmessage({data:{kind:'done',reply}});return {operation,worker};
}
(async()=>{
 const h=harness();await h.api.provision();await h.api.provisionDvi();await h.api.provisionXetex();await h.api.provisionLuatex();
 let cells=0;
 for(const [version,engine,kind]of [[6,'pdftex','compile'],[12,'latex-dvi','compile'],[13,'xelatex','xetex'],[14,'lualatex','luatex']]){
  for(const tool of ['latex','bibtex']){
   const {operation,worker}=settle(h,request(version,engine,tool));
   assert.equal((await operation).nativeReturn,0);assert.ok(worker.url.startsWith(origin+'/__oop/native-worker/'+(tool==='bibtex'?'compile':kind)+'?'));assert.equal(worker.terminated,1);cells++;
  }
 }
 for(const [v,e]of [[12,'latex-dvi'],[13,'xelatex']]){const {operation,worker}=settle(h,request(v,e,'dvipdfmx'));assert.equal((await operation).nativeReturn,0);assert.ok(worker.url.includes('/dvi?'));cells++;}
 const before=h.stats.workers.length;
 for(const [v,e,t,p]of [[13,'xelatex','latex',8],[13,'xelatex','dvipdfmx',8],[8,'xelatex','latex',9],[13,'lualatex','latex',9],
  [13,'pdftex','latex',9],[13,'latex-dvi','dvipdfmx',9],[10,'xelatex','latex',9],[11,'pdftex','latex',9],[13,'xelatex','latex',10],
  [13,'xelatex','latex',2147483647],[7,'latex-dvi','latex',9],[9,'lualatex','latex',9]])assert.throws(()=>h.api.execute(request(v,e,t,p)));
 assert.equal(h.stats.workers.length,before,'all closed rejections occur before URL/Worker allocation');
 for(const p of [5,6,7,8])assert.equal((await settle(h,request(8,'xelatex','latex',p)).operation).nativeReturn,0);
 const map=new Uint8Array([1,2]),sidecar=new Uint8Array([3,4]);
 let r=await settle(h,request(13,'xelatex'),{sourceMapPayload:map}).operation;map.fill(0);assert.deepEqual([...r.sourceMapPayload],[1,2]);
 r=await settle(h,request(13,'xelatex','dvipdfmx'),{sourceMapGeometryPayload:sidecar}).operation;sidecar.fill(0);assert.deepEqual([...r.sourceMapGeometryPayload],[3,4]);assert.equal(r.sourceMapPayload,undefined);
 for(const bad of [new Uint8Array(131073),[],{},new Uint16Array([1]),'!!!=']){
  r=await settle(h,request(13,'xelatex','dvipdfmx'),{sourceMapGeometryPayload:bad}).operation;assert.equal(r.nativeReturn,0);assert.equal(r.files.length,1);assert.equal(r.sourceMapGeometryPayload,null);
 }
 for(const [tool,fields]of [['latex',{sourceMapGeometryPayload:new Uint8Array([1])}],['bibtex',{sourceMapPayload:new Uint8Array([1])}],
  ['dvipdfmx',{sourceMapPayload:new Uint8Array([1])}],['latex',{nativeReturn:1,sourceMapPayload:new Uint8Array([1])}]])assert.equal((await settle(h,request(13,'xelatex',tool),fields).operation).outcome,'protocol-failure');
 const held=h.api.execute(request(13,'xelatex')),worker=h.stats.workers.at(-1),saved=worker.onmessage;
 h.api.terminate(request(13,'xelatex').tag.job,1);assert.equal((await held).outcome,'cancelled');assert.equal(worker.terminated,1);
 saved({data:{kind:'done',reply:{tag:request(13,'xelatex').tag,sourceMapPayload:new Uint8Array([7])}}});assert.equal(h.api.observations().active,0);
 const timeout=h.api.execute(request(13,'xelatex','dvipdfmx'));h.timeoutPhase();assert.equal((await timeout).outcome,'transport-timeout');
 assert.equal(h.api.observations().started,h.api.observations().settled);assert.equal(h.stats.urls.length,h.stats.revoked.length);h.api.dispose();
 console.log(`Xe bridge modeled PASS: ${cells} exact P9 tool cells; old Xe8 profiles5..8; bounded optional replies, wrong-cell refusal, closed10/intmax, physical cancel/timeout/late settlement.`);
})().catch(e=>{console.error(e);process.exitCode=1;});
