// Browser/worker/byte transport ONLY. Tool/pass selection belongs to C#.
let workerText=null,preparing=null,disposed=false;
const active=new Map(),trace=[];
const retainedLimit=64;
// Admission/counters begin only after successful Worker construction. Earlier
// failures throw after releasing their partial URLs, with no timer/map/trace.
let started=0,settledCount=0;
const bytes=v=>v instanceof Uint8Array?v:typeof v==='string'?Uint8Array.from(atob(v),c=>c.charCodeAt(0)):new Uint8Array(v);
export function provision(){
 if(disposed)throw new Error('disposed');
 return preparing??=(async()=>{const r=await fetch(new URL('./compile-port-worker.js',import.meta.url));if(!r.ok)throw new Error('explicit worker provisioning failed');const b=await r.arrayBuffer();if(b.byteLength>32768)throw new Error('worker byte bound');workerText=new TextDecoder('utf8',{fatal:true}).decode(b);return true;})();
}
export function execute(frame){
 if(disposed||workerText===null||frame.tag.version!==1)throw new Error('transport not explicitly provisioned/versioned');
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
 let sourceUrl,loaderUrl,worker;
 try{
  sourceUrl=ownUrl(new Blob([workerText],{type:'text/javascript'}));
  loaderUrl=ownUrl(new Blob([bytes(frame.loader)],{type:'text/javascript'}));
  worker=new Worker(sourceUrl);
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
   active.set(key,{stop:()=>finish(unavailable('cancelled'))});
   timer=setTimeout(()=>finish(unavailable('transport-timeout')),30000);
   worker.onerror=()=>{if(!settled)finish(unavailable('worker-error'));};
   worker.onmessage=({data:d})=>{
    if(settled)return;
    if(d?.kind==='ready'){
     if(record.posted)return;
     try{
      const phase={operation:frame.tag.tool,tag:frame.tag,mainEntry:frame.mainEntry,files:frame.files.map(f=>({name:f.name,bytes:bytes(f.bytes)})),outputs:frame.outputs,maxLogBytes:frame.maxLogBytes,maxOutputBytes:frame.maxOutputBytes};
      record.posted=true;worker.postMessage(phase);
     }catch{finish(unavailable('transport-send-error'));}
    }else if(d?.kind==='entered')record.entered=true;
    else if(d?.kind==='done')finish(d.reply);
    else if(d?.kind==='boot-error')finish(unavailable('boot-error'));
   };
   worker.postMessage({operation:'boot',loaderUrl,loader:bytes(frame.loader),wasm:bytes(frame.wasm)});
  }catch{finish(unavailable('transport-startup-error'));}
 });
}
export function terminate(job,epoch){active.get(job+'/'+epoch)?.stop();}
export function observations(){return structuredClone({active:active.size,started,settled:settledCount,retainedLimit,evicted:started-trace.length,trace});}
export function dispose(){disposed=true;for(const operation of [...active.values()])operation.stop();workerText=null;}
