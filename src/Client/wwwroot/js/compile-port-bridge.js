import { createRestrictedWorkerAdmission } from './restricted-worker-admission.js';
// Browser/worker/byte transport ONLY. Tool/pass selection belongs to C#.
let disposed=false;
const roles=new Map(['compile','dvi','xetex','luatex'].map(kind=>[kind,{admission:null,preparing:null,admitter:null}]));
function controllerChanged(){
 for(const role of roles.values())role.admission=null;
 for(const operation of [...active.values()])operation.fail();
}
function checkController(binding,kind='compile'){
 const owner=roles.get(kind)?.admitter;
 if(disposed||!owner)throw new Error('matching activated production controller required');
 owner.check(binding);
}
const active=new Map(),trace=[];
const retainedLimit=64;
// Admission/counters begin only after successful Worker construction. Earlier
// failures throw after releasing their partial URLs, with no timer/map/trace.
let started=0,settledCount=0;
const bytes=v=>v instanceof Uint8Array?v:typeof v==='string'?Uint8Array.from(atob(v),c=>c.charCodeAt(0)):new Uint8Array(v);
function provisionRole(kind){
 if(disposed)throw new Error('disposed');
 const role=roles.get(kind);
 if(role.admission){checkController(role.admission,kind);return Promise.resolve(true);}
 if(role.preparing)return role.preparing;
 role.admitter??=createRestrictedWorkerAdmission(kind,controllerChanged);
 const round=role.admitter.provision().then(candidate=>{checkController(candidate,kind);role.admission=candidate;return true;});
 role.preparing=round;
 round.then(()=>{if(role.preparing===round)role.preparing=null;},()=>{if(role.preparing===round)role.preparing=null;});
 return round;
}
export function provision(){return provisionRole('compile');}
export function provisionDvi(){return provisionRole('dvi');}
export function provisionXetex(){return provisionRole('xetex');}
export function provisionLuatex(){return provisionRole('luatex');}
export function execute(frame){
 const legacy=frame.tag.version===1&&(frame.tag.profile===undefined||frame.tag.profile===0)&&frame.tag.runtimeIdentity==null;
 const modern=frame.tag.version===2&&frame.tag.profile===1&&typeof frame.tag.runtimeIdentity==='string'&&/^[0-9a-f]{64}$/.test(frame.tag.runtimeIdentity);
 const authoring=frame.tag.version===3&&frame.tag.profile===2&&typeof frame.tag.runtimeIdentity==='string'&&/^[0-9a-f]{64}$/.test(frame.tag.runtimeIdentity);
 // Additive authoring phase admission; same runtime/profile and physical cancellation.
 const authoring4=[4,5].includes(frame.tag.version)&&frame.tag.profile===2&&typeof frame.tag.runtimeIdentity==='string'&&/^[0-9a-f]{64}$/.test(frame.tag.runtimeIdentity);
 const identity=typeof frame.tag.runtimeIdentity==='string'&&/^[0-9a-f]{64}$/.test(frame.tag.runtimeIdentity);
 const mapped=frame.tag.version===6&&([3,4].includes(frame.tag.profile)&&frame.tag.engineId==null
  ||frame.tag.profile===5&&frame.tag.engineId==='pdftex')&&identity;
 const dviPhase=frame.tag.version===7&&[4,5].includes(frame.tag.profile)&&frame.tag.engineId==='latex-dvi'&&identity;
 const xe=frame.tag.version===8&&frame.tag.profile===5&&frame.tag.engineId==='xelatex'&&identity;
 const lua=frame.tag.version===9&&frame.tag.profile===5&&frame.tag.engineId==='lualatex'&&identity;
 const converter=(dviPhase||xe)&&frame.tag.tool==='dvipdfmx';
 const kind=converter?'dvi':frame.tag.tool==='latex'?(xe?'xetex':lua?'luatex':'compile'):'compile';
 const binding=roles.get(kind).admission;
 if(disposed||(!legacy&&!modern&&!authoring&&!authoring4&&!mapped&&!dviPhase&&!xe&&!lua)||binding===null
  ||(dviPhase||xe)&&!['latex','bibtex','dvipdfmx'].includes(frame.tag.tool)||lua&&!['latex','bibtex'].includes(frame.tag.tool))throw new Error('transport not explicitly provisioned/versioned');
 checkController(binding,kind);
 if(active.size>=2||started>=Number.MAX_SAFE_INTEGER)throw new Error('transport operation bound');
 const key=frame.tag.job+'/'+frame.tag.epoch;if(active.has(key))throw new Error('duplicate logical job');
 const retired=trace.length>=retainedLimit?trace.findIndex(record=>record.terminated):-1;
 if(trace.length>=retainedLimit&&retired<0)throw new Error('trace invariant');
 const ownedUrls=[];
 const ownUrl=blob=>{const url=URL.createObjectURL(blob);ownedUrls.push(url);return url;};
 const revokeUrls=()=>{
  // Remove ownership BEFORE revocation, including reentrant/error paths. Always
  // attempt every owned URL; a cleanup exception cannot skip the other release.
  let failure;
  for(const url of ownedUrls.splice(0)){try{URL.revokeObjectURL(url);}catch(error){failure??=error;}}
  if(failure)throw failure;
 };
 let loaderUrl,worker;
 try{
  loaderUrl=ownUrl(new Blob([bytes(frame.loader)],{type:'text/javascript'}));
  checkController(binding,kind);
  worker=new Worker(new URL(binding.entry,location.origin));
 }catch(error){try{revokeUrls();}catch(cleanup){throw new AggregateError([error,cleanup],'Transport startup and URL cleanup failed');}throw error;}
 if(retired>=0)trace.splice(retired,1);
 const record={tag:frame.tag,posted:false,entered:false,terminated:false,outcome:null,nativeReturn:null};trace.push(record);started++;
 return new Promise(resolve=>{
  let settled=false,timer=null;
  const unavailable=outcome=>({tag:frame.tag,outcome,nativeReturn:null,entered:record.entered,log:'',files:[],inputs:[]});
  const finish=result=>{
   if(settled)return;
   settled=true;
   if(timer!==null){clearTimeout(timer);timer=null;}
   active.delete(key);
   // Saved late callbacks also test settled below; detaching alone is not a
   // correlation/settlement guard. A failed phase cannot dispatch again.
   worker.onmessage=null;worker.onerror=null;
   try{worker.terminate();record.terminated=true;}
   finally{
    record.outcome=result.outcome;record.nativeReturn=result.nativeReturn;settledCount++;
    try{revokeUrls();}finally{resolve(result);}
   }
  };
  try{
   active.set(key,{stop:()=>finish(unavailable('cancelled')),fail:()=>finish(unavailable('worker-error'))});
   timer=setTimeout(()=>finish(unavailable('transport-timeout')),30000);
   worker.onerror=()=>{if(!settled)finish(unavailable('worker-error'));};
   worker.onmessage=({data:d})=>{
    if(settled)return;
    if(d?.kind==='ready'){
     if(record.posted)return;
     try{
      const phase={operation:frame.tag.tool,tag:frame.tag,mainEntry:frame.mainEntry,selectedRoot:frame.selectedRoot,files:frame.files.map(f=>({name:f.name,bytes:bytes(f.bytes)})),outputs:frame.outputs,maxLogBytes:frame.maxLogBytes,maxOutputBytes:frame.maxOutputBytes,runtimeResourceNames:frame.runtimeResourceNames,generatedInputNames:frame.generatedInputNames};
      record.posted=true;worker.postMessage(phase);
     }catch{finish(unavailable('transport-send-error'));}
    }else if(d?.kind==='entered')record.entered=true;
    else if(d?.kind==='done')finish(d.reply);
    else if(d?.kind==='boot-error')finish(unavailable('boot-error'));
   };
   checkController(binding,kind);
   worker.postMessage({operation:'boot',loaderUrl,loader:bytes(frame.loader),wasm:bytes(frame.wasm)});
  }catch{finish(unavailable('transport-startup-error'));}
 });
}
export function terminate(job,epoch){active.get(job+'/'+epoch)?.stop();}
export function observations(){return structuredClone({active:active.size,started,settled:settledCount,retainedLimit,evicted:started-trace.length,trace});}
export function dispose(){disposed=true;for(const role of roles.values()){role.admission=null;role.admitter?.dispose();}for(const operation of [...active.values()])operation.stop();}
