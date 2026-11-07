import { createRestrictedWorkerAdmission } from './restricted-worker-admission.js';
// Browser/worker/byte transport ONLY. Tool/pass selection belongs to C#.
let disposed=false;
const roles=new Map(['compile','dvi','xetex','luatex','biber'].map(kind=>[kind,{admission:null,preparing:null,admitter:null}]));
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
export function provisionBiber(){return provisionRole('biber');}
// Defensive transport checks precede byte conversion/Blob/Worker creation.
// C# remains authoritative for exact module/input hashes and revision acceptance.
const nativeViewLength=Object.getOwnPropertyDescriptor(Object.getPrototypeOf(Uint8Array.prototype),'byteLength').get;
const nativeBufferLength=Object.getOwnPropertyDescriptor(ArrayBuffer.prototype,'byteLength').get;
function transferLength(value,maximum){
 try{
  let n;
  if(value instanceof Uint8Array&&ArrayBuffer.isView(value))n=nativeViewLength.call(value);
  else if(value instanceof ArrayBuffer)n=nativeBufferLength.call(value);
  else if(typeof value==='string'){
   if(value.length%4||value.length>4*Math.ceil(maximum/3))return -1;
   const padding=value.endsWith('==')?2:value.endsWith('=')?1:0,end=value.length-padding;
   for(let i=0;i<end;i++){const c=value.charCodeAt(i);if(!(c>=65&&c<=90||c>=97&&c<=122||c>=48&&c<=57||c===43||c===47))return -1;}
   n=value.length/4*3-padding;
  }else return -1;
  return Number.isSafeInteger(n)&&n>=0&&n<=maximum?n:-1;
 }catch{return -1;}
}
const nativePath=name=>typeof name==='string'&&name.length>0&&name.length<=249&&name.isWellFormed()
 &&new TextEncoder().encode(name).length<=249&&name===name.trim()
 &&!/[\u0000-\u001f\u007f-\u009f\\:<>"|?*]/.test(name)
 &&name.split('/').length<=32&&name.split('/').every(p=>p&&p!=='.'&&p!=='..'&&!/[. ]$/.test(p));
const nativeAlias=name=>name.normalize('NFC').toUpperCase();
function biberTransportFrame(frame){
 const t=frame.tag,root=frame.selectedRoot;
 if(t.profile!==13||t.version!==10||frame.bibliographyTool!=='biber'||!['pdftex','latex-dvi','xelatex','lualatex'].includes(t.engineId)
  ||typeof t.runtimeIdentity!=='string'||!/^[0-9a-f]{64}$/.test(t.runtimeIdentity)||!nativePath(root)||!/\.tex$/i.test(root)
  ||frame.files.length>64||frame.outputs.length!==2||frame.maxLogBytes<1
  ||transferLength(frame.loader,1048576)<1||transferLength(frame.wasm,16777216)<1||transferLength(frame.bootstrapData,33554432)<1
  ||!Array.isArray(frame.runtimeResourceNames)||frame.runtimeResourceNames.length!==0
  ||!Array.isArray(frame.generatedInputNames)||frame.generatedInputNames.length!==1)return false;
 const leaf=root.slice(root.lastIndexOf('/')+1),job=t.engineId==='lualatex'?'result':leaf.slice(0,-4),bcf=job+'.bcf';
 if(frame.mainEntry!==bcf||frame.generatedInputNames[0]!==bcf||new Set(frame.outputs).size!==2
  ||!frame.outputs.includes(job+'.bbl')||!frame.outputs.includes(job+'.blg'))return false;
 let total=0,hasBcf=false;const names=new Set(),out=new Set(frame.outputs.map(nativeAlias));
 for(const f of frame.files){
  if(!f||!nativePath(f.name)||names.has(nativeAlias(f.name))||out.has(nativeAlias(f.name)))return false;
  const n=transferLength(f.bytes,8388608);if(n<0||(total+=n)>8388608)return false;
  names.add(nativeAlias(f.name));if(f.name===bcf){if(!n)return false;hasBcf=true;}
 }
 for(const n of names)for(let at=n.indexOf('/');at>=0;at=n.indexOf('/',at+1))if(names.has(n.slice(0,at)))return false;
 return hasBcf;
}
function transportFrame(frame){
 const t=frame?.tag;
 if(!t||!Number.isSafeInteger(t.epoch)||t.epoch<1||typeof t.job!=='string'||!t.job.length||t.job.length>128
  ||typeof t.nonce!=='string'||!t.nonce.length||t.nonce.length>128
  ||t.ordinal!==undefined&&(!Number.isSafeInteger(t.ordinal)||t.ordinal<1||t.ordinal>4294967295)
  ||!['latex','bibtex','biber','dvipdfmx'].includes(t.tool)
  ||!Array.isArray(frame.files)||frame.files.length<1||!Array.isArray(frame.outputs)
  ||frame.outputs.length<1||frame.outputs.length>128
  ||!Number.isSafeInteger(frame.maxLogBytes)||frame.maxLogBytes<0||frame.maxLogBytes>262144
  ||!Number.isSafeInteger(frame.maxOutputBytes)||frame.maxOutputBytes<1||frame.maxOutputBytes>8388608)return false;
 const opaque=v=>typeof v==='string'&&v.length>0&&v.length<=128;
 if(['request','capture','digest'].some(k=>t[k]!==undefined&&!opaque(t[k])))return false;
 const s=t.snapshot;
 if(s!=null&&(!opaque(s.captureId)||!opaque(s.serviceId)||!opaque(s.sessionId)||!opaque(s.projectId?.value)
  ||s.baseHeadRevisionId!=null&&!opaque(s.baseHeadRevisionId.value)
  ||['workingGeneration','localWorkspaceEpoch','selectionVersion'].some(k=>!Number.isSafeInteger(s[k])||s[k]<0)))return false;
 if(t.tool==='biber')return biberTransportFrame(frame);
 if(frame.bootstrapData!=null||(t.profile===13?!['none','bibtex','biber'].includes(frame.bibliographyTool):frame.bibliographyTool!=null))return false;
 const suite=[5,6,7,8,9,10,12,13].includes(t.profile),conversion=t.tool==='dvipdfmx',modern=t.version!==1;
 if(conversion&&!(t.version===7&&t.engineId==='latex-dvi'||[8,9].includes(t.profile)&&t.version===12&&t.engineId==='latex-dvi'||t.version===8&&t.engineId==='xelatex'||t.profile===9&&t.version===13&&t.engineId==='xelatex'||[10,12].includes(t.profile)&&(t.version===15&&t.engineId==='latex-dvi'||t.version===(t.profile===12?21:16)&&t.engineId==='xelatex')||t.profile===13&&(t.version===24&&t.engineId==='latex-dvi'||t.version===21&&t.engineId==='xelatex')))return false;
 if(frame.files.length>(conversion?4096:suite?8384:t.version>=4?768:512)
  ||transferLength(frame.loader,1048576)<1||transferLength(frame.wasm,16777216)<1)return false;
 const resources=frame.runtimeResourceNames??[],generated=frame.generatedInputNames??[];
 if(!Array.isArray(resources)||resources.length>8192||!Array.isArray(generated)||generated.length>128)return false;
 const r=new Set(resources),g=new Set(generated),names=new Map(),outputs=new Set(),prefix='__oop_runtime__/';
 if(r.size!==resources.length||g.size!==generated.length||resources.some(n=>!nativePath(n))||generated.some(n=>!nativePath(n)||r.has(n)))return false;
 let total=0,work=0,runtime=0,captured=0;
 for(const f of frame.files){
  if(!f||!nativePath(f.name)||names.has(nativeAlias(f.name)))return false;
  const n=transferLength(f.bytes,conversion?33554432:suite?67108864:modern?33554432:8388608);
  if(n<0)return false;total+=n;names.set(nativeAlias(f.name),f.name);
  if(total>(conversion?109051904:suite?268435456:modern?67108864:16777216))return false;
  if(suite||conversion){
   if(r.has(f.name))runtime+=n;
   else {if(suite&&f.name.startsWith(prefix)||n>8388608)return false;work+=n;if(!g.has(f.name))captured++;}
  }
 }
 if((suite||conversion)&&(captured>(conversion?65:64)||work>(conversion?8388608:33554432)
  ||runtime>(!conversion&&[12,13].includes(t.profile)&&t.version===22&&t.engineId==='lualatex'&&t.tool==='latex'?134217728:conversion||[9,14,17].includes(t.version)&&t.tool==='latex'?100663296:167772160)
  ||resources.some(n=>suite&&!n.startsWith(prefix)||names.get(nativeAlias(n))!==n||conversion&&(suite?n.slice(prefix.length):n).includes('/'))
  ||generated.some(n=>names.get(nativeAlias(n))!==n)||g.has(frame.selectedRoot)||r.has(frame.selectedRoot)))return false;
 if(suite&&!conversion&&(t.version===6||t.version===7||t.version===12||t.version===15||t.tool==='bibtex')&&total>167772160)return false;
 for(const n of frame.outputs){
  if(!nativePath(n)||outputs.has(nativeAlias(n))||r.has(n))return false;
  const input=names.get(nativeAlias(n));
  if(input!==undefined&&(suite?!g.has(n)||input!==n:input!==n||!(/\.(aux|bbl|toc|lof|lot|out)$/.test(n))))return false;
  outputs.add(nativeAlias(n));
 }
 const all=new Set([...names.keys(),...outputs]);
 for(const n of all)for(let at=n.indexOf('/');at>=0;at=n.indexOf('/',at+1))if(all.has(n.slice(0,at)))return false;
 if(!nativePath(frame.mainEntry)&&!(typeof frame.mainEntry==='string'&&frame.mainEntry.startsWith('"')&&frame.mainEntry.endsWith('"')&&nativePath(frame.mainEntry.slice(1,-1))))return false;
 if(frame.selectedRoot!=null&&(!nativePath(frame.selectedRoot)||!names.has(nativeAlias(frame.selectedRoot))))return false;
 return true;
}
function ownNativeTag(t){
 const tag={};for(const k of ['job','request','epoch','capture','digest','ordinal','tool','nonce','version','profile','runtimeIdentity','engineId'])tag[k]=t[k];
 const s=t.snapshot;
 tag.snapshot=s==null?s:{captureId:s.captureId,serviceId:s.serviceId,projectId:s.projectId==null?s.projectId:{value:s.projectId.value},
  sessionId:s.sessionId,workingGeneration:s.workingGeneration,baseHeadRevisionId:s.baseHeadRevisionId==null?s.baseHeadRevisionId:{value:s.baseHeadRevisionId.value},
  localWorkspaceEpoch:s.localWorkspaceEpoch,selectionVersion:s.selectionVersion};
 return tag;
}
function sameNativeTag(e,a){
 if(!a||typeof a!=='object')return false;
 for(const k of ['job','request','epoch','capture','digest','ordinal','tool','nonce','version','profile','runtimeIdentity','engineId'])if(a[k]!==e[k])return false;
 const x=a.snapshot,y=e.snapshot;if(y==null)return x==null;if(!x||typeof x!=='object')return false;
 for(const k of ['captureId','serviceId','sessionId','workingGeneration','localWorkspaceEpoch','selectionVersion'])if(x[k]!==y[k])return false;
 return x.projectId?.value===y.projectId?.value&&(y.baseHeadRevisionId==null?x.baseHeadRevisionId==null
  :x.baseHeadRevisionId!=null&&x.baseHeadRevisionId.value===y.baseHeadRevisionId.value);
}
function boundedNativeReply(reply,tag,caps){
 if(!reply||!sameNativeTag(tag,reply.tag)||!['native-return','setup-failure','protocol-failure','resource-limit','wasm-abort','font-semantics-unsupported'].includes(reply.outcome)
  ||reply.nativeReturn!==null&&!Number.isInteger(reply.nativeReturn))return null;
 const biber=tag.profile===13&&tag.version===10&&tag.tool==='biber';
 if(reply.bootstrapSha256!=null&&(!biber||typeof reply.bootstrapSha256!=='string'||!/^[0-9a-f]{64}$/.test(reply.bootstrapSha256)))return null;
 if(reply.outcome==='font-semantics-unsupported'&&!(tag.profile===13&&[23,24].includes(tag.version)&&(tag.tool==='latex'||tag.version===24&&tag.tool==='dvipdfmx')&&reply.nativeReturn===0&&(!reply.files||reply.files.length===0)&&reply.sourceMapPayload==null&&reply.sourceMapGeometryPayload==null))return null;
 const log=reply.log??'',files=reply.files??[],inputs=reply.inputs??[];
 if(typeof log!=='string'||!log.isWellFormed()||log.length>caps.log||new TextEncoder().encode(log).length>caps.log
  ||!Array.isArray(files)||files.length>caps.outputs.size||!Array.isArray(inputs)||inputs.length>caps.inputs)return null;
 let total=0;const names=new Set();
 for(const f of files){
  if(!f||!caps.outputs.has(f.name)||names.has(f.name))return null;
  const n=transferLength(f.bytes,caps.output);if(n<0||n>(biber?caps.output:2*caps.output)-total)return null;total+=n;names.add(f.name);
 }
 for(const f of inputs)if(!f||!nativePath(f.name)||!Number.isSafeInteger(f.length)||f.length<0||f.length>67108864||typeof f.sha256!=='string'||!/^[0-9a-f]{64}$/.test(f.sha256))return null;
 const navigation=[8,9,10,12,13].includes(tag.profile),xeMap=(tag.profile===9&&tag.version===13||tag.profile===10&&tag.version===16||[12,13].includes(tag.profile)&&tag.version===21)&&tag.engineId==='xelatex',dviMap=tag.version===(tag.profile===13?24:[10,12].includes(tag.profile)?15:12)&&tag.engineId==='latex-dvi',luaMap=tag.version===([12,13].includes(tag.profile)?22:tag.profile===10?17:14)&&tag.engineId==='lualatex',success=reply.outcome==='native-return'&&reply.nativeReturn===0;
 const mapCell=navigation&&tag.tool==='latex'&&(dviMap||luaMap||xeMap||tag.profile===13&&tag.version===23&&tag.engineId==='pdftex');
 const geometryCell=navigation&&((dviMap||xeMap)&&tag.tool==='dvipdfmx'||luaMap&&tag.tool==='latex');
 let sourceMapPayload=reply.sourceMapPayload,sourceMapGeometryPayload=reply.sourceMapGeometryPayload;
 if(sourceMapPayload!=null){
  if(mapCell&&success)sourceMapPayload=transferLength(sourceMapPayload,1048575)>0?bytes(sourceMapPayload).slice():null;
  else if(tag.version!==6||tag.tool!=='latex'||!success||transferLength(sourceMapPayload,1048575)<1)return null;
 }
 if(sourceMapGeometryPayload!=null){
  if(!geometryCell||!success)return null;
  sourceMapGeometryPayload=transferLength(sourceMapGeometryPayload,131072)>0?bytes(sourceMapGeometryPayload).slice():null;
 }
 if(mapCell&&!sourceMapPayload)sourceMapGeometryPayload=null;
 if(reply.entered!==undefined&&typeof reply.entered!=='boolean'
  ||reply.elapsedMs!=null&&(typeof reply.elapsedMs!=='number'||!Number.isFinite(reply.elapsedMs)||reply.elapsedMs<0)
  ||reply.heapBytes!=null&&(!Number.isSafeInteger(reply.heapBytes)||reply.heapBytes<0||reply.heapBytes>2147483648)
  ||['wasmSha256','loaderSha256'].some(k=>reply[k]!=null&&(typeof reply[k]!=='string'||!/^[0-9a-f]{64}$/.test(reply[k]))))return null;
 // Return only the typed transport fields: unknown worker fields cannot trigger
 // an unbounded .NET interop serialization. C# still checks exact hashes/facts.
 return {tag,outcome:reply.outcome,nativeReturn:reply.nativeReturn,entered:reply.entered,log,
  files:files.map(f=>({name:f.name,bytes:f.bytes})),inputs:inputs.map(f=>({name:f.name,length:f.length,sha256:f.sha256})),
  elapsedMs:reply.elapsedMs,heapBytes:reply.heapBytes,wasmSha256:reply.wasmSha256,loaderSha256:reply.loaderSha256,...(biber?{bootstrapSha256:reply.bootstrapSha256}:{}),sourceMapPayload,sourceMapGeometryPayload};
}
export function execute(frame){
 if(!transportFrame(frame))throw new Error('invalid bounded native transport frame');
 const legacy=frame.tag.version===1&&(frame.tag.profile===undefined||frame.tag.profile===0)&&frame.tag.runtimeIdentity==null;
 const modern=frame.tag.version===2&&frame.tag.profile===1&&typeof frame.tag.runtimeIdentity==='string'&&/^[0-9a-f]{64}$/.test(frame.tag.runtimeIdentity);
 const authoring=frame.tag.version===3&&frame.tag.profile===2&&typeof frame.tag.runtimeIdentity==='string'&&/^[0-9a-f]{64}$/.test(frame.tag.runtimeIdentity);
 // Additive authoring phase admission; same runtime/profile and physical cancellation.
 const authoring4=[4,5].includes(frame.tag.version)&&frame.tag.profile===2&&typeof frame.tag.runtimeIdentity==='string'&&/^[0-9a-f]{64}$/.test(frame.tag.runtimeIdentity);
 const identity=typeof frame.tag.runtimeIdentity==='string'&&/^[0-9a-f]{64}$/.test(frame.tag.runtimeIdentity);
 const mapped=(frame.tag.profile===13&&frame.tag.version===23&&frame.tag.engineId==='pdftex'||frame.tag.version===6&&([3,4].includes(frame.tag.profile)&&frame.tag.engineId==null
  ||[5,6,7,8,9,10,12].includes(frame.tag.profile)&&frame.tag.engineId==='pdftex'))&&identity;
 const dviPhase=(frame.tag.profile===13&&frame.tag.version===24||frame.tag.version===7&&[4,5,6,7].includes(frame.tag.profile)||frame.tag.version===([10,12].includes(frame.tag.profile)?15:12)&&[8,9,10,12].includes(frame.tag.profile))&&frame.tag.engineId==='latex-dvi'&&identity;
 const xe=(frame.tag.version===8&&[5,6,7,8].includes(frame.tag.profile)||frame.tag.version===([12,13].includes(frame.tag.profile)?21:frame.tag.profile===10?16:13)&&[9,10,12,13].includes(frame.tag.profile))&&frame.tag.engineId==='xelatex'&&identity;
 const lua=(frame.tag.version===9&&[5,6,7].includes(frame.tag.profile)||frame.tag.version===([12,13].includes(frame.tag.profile)?22:frame.tag.profile===10?17:14)&&[8,9,10,12,13].includes(frame.tag.profile))&&frame.tag.engineId==='lualatex'&&identity;
 const biber=frame.tag.profile===13&&frame.tag.version===10&&frame.tag.tool==='biber'&&identity;
 const converter=(dviPhase||xe)&&frame.tag.tool==='dvipdfmx';
 const kind=biber?'biber':converter?'dvi':frame.tag.tool==='latex'?(xe?'xetex':lua?'luatex':'compile'):'compile';
 const binding=roles.get(kind).admission;
 if(disposed||(!legacy&&!modern&&!authoring&&!authoring4&&!mapped&&!dviPhase&&!xe&&!lua&&!biber)||binding===null
  ||(dviPhase||xe)&&!['latex','bibtex','dvipdfmx'].includes(frame.tag.tool)||lua&&!['latex','bibtex'].includes(frame.tag.tool))throw new Error('transport not explicitly provisioned/versioned');
 checkController(binding,kind);
 if(active.size>=2||started>=Number.MAX_SAFE_INTEGER)throw new Error('transport operation bound');
 const key=frame.tag.job+'/'+frame.tag.epoch;if(active.has(key))throw new Error('duplicate logical job');
 const retired=trace.length>=retainedLimit?trace.findIndex(record=>record.terminated):-1;
 if(trace.length>=retainedLimit&&retired<0)throw new Error('trace invariant');
 const expectedTag=ownNativeTag(frame.tag),caps={log:frame.maxLogBytes,output:frame.maxOutputBytes,outputs:new Set(frame.outputs),inputs:frame.files.length};
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
 const record={tag:expectedTag,posted:false,entered:false,terminated:false,outcome:null,nativeReturn:null};trace.push(record);started++;
 return new Promise(resolve=>{
  let settled=false,timer=null;
  const unavailable=outcome=>({tag:expectedTag,outcome,nativeReturn:null,entered:record.entered,log:'',files:[],inputs:[]});
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
      if(!transportFrame(frame)||!sameNativeTag(expectedTag,frame.tag)||frame.maxLogBytes!==caps.log||frame.maxOutputBytes!==caps.output
       ||frame.outputs.length!==caps.outputs.size||frame.outputs.some(n=>!caps.outputs.has(n)))throw new Error('changed native frame');
      const phase={operation:expectedTag.tool,tag:expectedTag,mainEntry:frame.mainEntry,selectedRoot:frame.selectedRoot,files:frame.files.map(f=>({name:f.name,bytes:bytes(f.bytes)})),outputs:frame.outputs,maxLogBytes:frame.maxLogBytes,maxOutputBytes:frame.maxOutputBytes,runtimeResourceNames:frame.runtimeResourceNames,generatedInputNames:frame.generatedInputNames,...(frame.tag.profile===13?{bibliographyTool:frame.bibliographyTool}:{})};
      record.posted=true;worker.postMessage(phase);
     }catch{finish(unavailable('transport-send-error'));}
    }else if(d?.kind==='entered')record.entered=true;
    else if(d?.kind==='done'){
     const reply=boundedNativeReply(d.reply,expectedTag,caps);finish(reply??unavailable('protocol-failure'));
    }
    else if(d?.kind==='boot-error')finish(unavailable('boot-error'));
   };
   checkController(binding,kind);
   worker.postMessage({operation:'boot',loaderUrl,loader:bytes(frame.loader),wasm:bytes(frame.wasm),...(biber?{bootstrapData:bytes(frame.bootstrapData)}:{})});
  }catch{finish(unavailable('transport-startup-error'));}
 });
}
export function terminate(job,epoch){active.get(job+'/'+epoch)?.stop();}
export function observations(){return structuredClone({active:active.size,started,settled:settledCount,retainedLimit,evicted:started-trace.length,trace});}
export function dispose(){disposed=true;for(const role of roles.values()){role.admission=null;role.admitter?.dispose();}for(const operation of [...active.values()])operation.stop();}
