import { createRestrictedWorkerAdmission } from './restricted-worker-admission.js';
const pB=p=>p===13||p===14||p===15||p===16||p===17||p===18||p===19||p===20||p===21;
const selectedPdfIdentity="41d0bb841b4832771327e44c84ada913e75204e25c5f39d6e52204e7350bf109";
const courseworkIdentity="f4ad5c7f702efc4f90d7a803ad6c697be0b3664b4ca90f837c8e6bd74ca88a40";
const dviGraphicsIdentity="1974b526f3a47b33f60d5a5384f14b3c618ac996d950ab514e13ef119d06bc79";
const pdfOriginIdentity="84abead5a2feb357d52ff01364f5b72ed099610c40a8c0428ac10f9a64b0e652";
const pageLabelIdentity="eb47ed92ac9efcdc7277c6b3dd1c5861abde63213e440d40aa7c201af6f4d692";
const termesIdentity="902f858d535c6a4fa273a24bec9c8a0fee7093e67ea6dd8da062bff1e43bbbe5";
const compatibleFontIdentities=["f6a4fd79dfe6453b58b64d3a2322fcb4f359f5d3fa0cedadc90abdb3ac9297dc","382c1f851ba3d3de852d1a7fbea499820a7a68de08bbdcb333eaf4d841afd0ec"];
const selectedPdfProfile=p=>p===17||p===18||p===19||p===20||p===21;
const selectedPdfRuntime=p=>p===17?selectedPdfIdentity:p===18?courseworkIdentity:p===19?dviGraphicsIdentity:p===20?pdfOriginIdentity:pageLabelIdentity;
const matchesSelectedPdfRuntime=t=>t.runtimeIdentity===selectedPdfRuntime(t.profile)||t.profile===21&&[termesIdentity,...compatibleFontIdentities].includes(t.runtimeIdentity);
const selectedPdfPhase=t=>selectedPdfProfile(t.profile)
 &&matchesSelectedPdfRuntime(t)
 &&t.version===23&&t.engineId==='pdftex'&&t.tool==='latex';
const pT=p=>p===14||p===15||p===16||p===17||p===18||p===19||p===20||p===21;
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
 if(!pB(t.profile)||t.version!==10||frame.bibliographyTool!=='biber'||!['pdftex','latex-dvi','xelatex','lualatex'].includes(t.engineId)
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
  ||selectedPdfProfile(t.profile)&&!matchesSelectedPdfRuntime(t)
  ||!Array.isArray(frame.files)||frame.files.length<1||!Array.isArray(frame.outputs)
  ||frame.outputs.length<1||frame.outputs.length>(pT(t.profile)?256:128)
  ||!Number.isSafeInteger(frame.maxLogBytes)||frame.maxLogBytes<0||frame.maxLogBytes>262144
  ||!Number.isSafeInteger(frame.maxOutputBytes)||frame.maxOutputBytes<1||frame.maxOutputBytes>(selectedPdfPhase(t)?12582912:8388608))return false;
 const opaque=v=>typeof v==='string'&&v.length>0&&v.length<=128;
 if(['request','capture','digest'].some(k=>t[k]!==undefined&&!opaque(t[k])))return false;
 const s=t.snapshot;
 if(s!=null&&(!opaque(s.captureId)||!opaque(s.serviceId)||!opaque(s.sessionId)||!opaque(s.projectId?.value)
  ||s.baseHeadRevisionId!=null&&!opaque(s.baseHeadRevisionId.value)
  ||['workingGeneration','localWorkspaceEpoch','selectionVersion'].some(k=>!Number.isSafeInteger(s[k])||s[k]<0)))return false;
 if(frame.dviImageSidecars!=null&&!([19,20,21].includes(t.profile)&&t.version===24&&t.engineId==='latex-dvi'&&t.tool==='latex'))return false;
 if(t.tool==='biber')return biberTransportFrame(frame);
 if(frame.bootstrapData!=null||(pB(t.profile)?!['none','bibtex','biber'].includes(frame.bibliographyTool):frame.bibliographyTool!=null))return false;
 const suite=[5,6,7,8,9,10,12,13,14,15,16,17,18,19,20,21].includes(t.profile),conversion=t.tool==='dvipdfmx',modern=t.version!==1;
 if(conversion&&!(t.version===7&&t.engineId==='latex-dvi'||[8,9].includes(t.profile)&&t.version===12&&t.engineId==='latex-dvi'||t.version===8&&t.engineId==='xelatex'||t.profile===9&&t.version===13&&t.engineId==='xelatex'||[10,12].includes(t.profile)&&(t.version===15&&t.engineId==='latex-dvi'||t.version===(t.profile===12?21:16)&&t.engineId==='xelatex')||pB(t.profile)&&(t.version===24&&t.engineId==='latex-dvi'||t.version===21&&t.engineId==='xelatex')))return false;
 if(frame.files.length>(conversion?4096:suite?(pT(t.profile)?8448:8384):t.version>=4?768:512)
  ||transferLength(frame.loader,1048576)<1||transferLength(frame.wasm,16777216)<1)return false;
 const resources=frame.runtimeResourceNames??[],generated=frame.generatedInputNames??[];
 if(!Array.isArray(resources)||resources.length>8192||!Array.isArray(generated)||generated.length>128)return false;
 const r=new Set(resources),g=new Set(generated),names=new Map(),outputs=new Set(),prefix='__oop_runtime__/';
 if(r.size!==resources.length||g.size!==generated.length||resources.some(n=>!nativePath(n))||generated.some(n=>!nativePath(n)||r.has(n)))return false;
 const leases=frame.dviImageSidecars??[],sidecars=new Set();
 if(!Array.isArray(leases)||leases.length>128)return false;
 for(const x of leases){
  if(!x||!nativePath(x.name)||!nativePath(x.sourceName)||!/\.(?:png|PNG)$/.test(x.sourceName)
   ||x.name!==x.sourceName.slice(0,-4)+'.xbb'||typeof x.sourceSha256!=='string'||!/^[0-9a-f]{64}$/.test(x.sourceSha256)
   ||sidecars.has(x.name)||r.has(x.name)||g.has(x.name)||r.has(x.sourceName)||g.has(x.sourceName))return false;
  sidecars.add(x.name);
 }
 let total=0,work=0,runtime=0,captured=0,sourceBytes=0;
 for(const f of frame.files){
  if(!f||!nativePath(f.name)||names.has(nativeAlias(f.name)))return false;
  const n=transferLength(f.bytes,conversion?33554432:suite?67108864:modern?33554432:8388608);
  if(n<0)return false;total+=n;names.set(nativeAlias(f.name),f.name);
  if(sidecars.has(f.name)&&(n<1||n>128))return false;
  if(total>(conversion?([16,17,18,19,20,21].includes(t.profile)?117440512:109051904):suite?268435456:modern?67108864:16777216))return false;
  if(suite||conversion){
   if(r.has(f.name))runtime+=n;
   else {
    if(suite&&f.name.startsWith(prefix)||n>8388608)return false;
    work+=n;
    if(!g.has(f.name)&&!sidecars.has(f.name)){
     captured++;
     if(selectedPdfProfile(t.profile)&&!(conversion&&f.name===frame.mainEntry)
      &&(n>2097152||(sourceBytes+=n)>16777216))return false;
    }
   }
  }
 }
 if(leases.some(x=>names.get(nativeAlias(x.name))!==x.name||names.get(nativeAlias(x.sourceName))!==x.sourceName))return false;
 if((suite||conversion)&&(captured>(conversion?(pT(t.profile)?129:65):pT(t.profile)?128:64)||work>(conversion?([16,17,18,19,20,21].includes(t.profile)?16777216:8388608):33554432)
  ||runtime>(!conversion&&[12,13,14,15,16,17,18,19,20,21].includes(t.profile)&&t.version===22&&t.engineId==='lualatex'&&t.tool==='latex'?134217728:conversion||[9,14,17].includes(t.version)&&t.tool==='latex'?100663296:167772160)
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
 if(selectedPdfPhase(t)&&(!nativePath(frame.selectedRoot)||!/^.+\.tex$/i.test(frame.selectedRoot.split('/').at(-1))))return false;
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
 const biber=pB(tag.profile)&&tag.version===10&&tag.tool==='biber';
 if(reply.bootstrapSha256!=null&&(!biber||typeof reply.bootstrapSha256!=='string'||!/^[0-9a-f]{64}$/.test(reply.bootstrapSha256)))return null;
 if(reply.outcome==='font-semantics-unsupported'&&!(pB(tag.profile)&&[23,24].includes(tag.version)&&(tag.tool==='latex'||tag.version===24&&tag.tool==='dvipdfmx')&&reply.nativeReturn===0&&(!reply.files||reply.files.length===0)&&reply.sourceMapPayload==null&&reply.sourceMapGeometryPayload==null))return null;
 const log=reply.log??'',files=reply.files??[],inputs=reply.inputs??[];
 if(typeof log!=='string'||!log.isWellFormed()||log.length>caps.log||new TextEncoder().encode(log).length>caps.log
  ||!Array.isArray(files)||files.length>caps.outputs.size||!Array.isArray(inputs)||inputs.length>caps.inputs)return null;
 let total=0;const names=new Set();
 const standard=selectedPdfProfile(tag.profile)?Math.min(8388608,caps.output):caps.output;
 for(const f of files){
  if(!f||!caps.outputs.has(f.name)||names.has(f.name))return null;
  const selected=reply.outcome==='native-return'&&reply.nativeReturn===0&&f.name===caps.selectedPdf;
  const n=transferLength(f.bytes,selected?caps.output:standard);
  if(n<0||n>(biber?standard:2*standard)-total)return null;
  total+=n;names.add(f.name);
 }
 for(const f of inputs)if(!f||!nativePath(f.name)||!Number.isSafeInteger(f.length)||f.length<0||f.length>67108864||typeof f.sha256!=='string'||!/^[0-9a-f]{64}$/.test(f.sha256))return null;
 const navigation=[8,9,10,12,13,14,15,16,17,18,19,20,21].includes(tag.profile),xeMap=(tag.profile===9&&tag.version===13||tag.profile===10&&tag.version===16||[12,13,14,15,16,17,18,19,20,21].includes(tag.profile)&&tag.version===21)&&tag.engineId==='xelatex',dviMap=tag.version===(pB(tag.profile)?24:[10,12].includes(tag.profile)?15:12)&&tag.engineId==='latex-dvi',luaMap=tag.version===([12,13,14,15,16,17,18,19,20,21].includes(tag.profile)?22:tag.profile===10?17:14)&&tag.engineId==='lualatex',success=reply.outcome==='native-return'&&reply.nativeReturn===0;
 const directPdf=[20,21].includes(tag.profile)&&tag.version===23&&tag.engineId==='pdftex'&&tag.tool==='latex';
 const mapCell=directPdf||navigation&&tag.tool==='latex'&&(dviMap||luaMap||xeMap||pB(tag.profile)&&tag.version===23&&tag.engineId==='pdftex');
 const geometryCell=directPdf||navigation&&((dviMap||xeMap)&&tag.tool==='dvipdfmx'||luaMap&&tag.tool==='latex');
 if(reply.sourceMapLegacyZeroOrigin!=null&&(!directPdf||!success))return null;
 let sourceMapPayload=reply.sourceMapPayload,sourceMapGeometryPayload=reply.sourceMapGeometryPayload;
 let sourceMapLegacyZeroOrigin=null;
 if(sourceMapPayload!=null){
  if(mapCell&&success)sourceMapPayload=transferLength(sourceMapPayload,1048575)>0?bytes(sourceMapPayload).slice():null;
  else if(tag.version!==6||tag.tool!=='latex'||!success||transferLength(sourceMapPayload,1048575)<1)return null;
 }
 if(sourceMapGeometryPayload!=null){
  if(!geometryCell||!success)return null;
  sourceMapGeometryPayload=transferLength(sourceMapGeometryPayload,131072)>0?bytes(sourceMapGeometryPayload).slice():null;
 }
 if(mapCell&&!sourceMapPayload)sourceMapGeometryPayload=null;
 if(directPdf){
  if(reply.sourceMapLegacyZeroOrigin===true&&reply.sourceMapGeometryPayload==null&&sourceMapPayload)sourceMapLegacyZeroOrigin=true;
  else if(reply.sourceMapLegacyZeroOrigin!=null||!sourceMapGeometryPayload)sourceMapPayload=sourceMapGeometryPayload=null;
 }
 if(reply.entered!==undefined&&typeof reply.entered!=='boolean'
  ||reply.elapsedMs!=null&&(typeof reply.elapsedMs!=='number'||!Number.isFinite(reply.elapsedMs)||reply.elapsedMs<0)
  ||reply.heapBytes!=null&&(!Number.isSafeInteger(reply.heapBytes)||reply.heapBytes<0||reply.heapBytes>2147483648)
  ||['wasmSha256','loaderSha256'].some(k=>reply[k]!=null&&(typeof reply[k]!=='string'||!/^[0-9a-f]{64}$/.test(reply[k]))))return null;
 // Return only the typed transport fields: unknown worker fields cannot trigger
 // an unbounded .NET interop serialization. C# still checks exact hashes/facts.
 return {tag,outcome:reply.outcome,nativeReturn:reply.nativeReturn,entered:reply.entered,log,
  files:files.map(f=>({name:f.name,bytes:f.bytes})),inputs:inputs.map(f=>({name:f.name,length:f.length,sha256:f.sha256})),
  elapsedMs:reply.elapsedMs,heapBytes:reply.heapBytes,wasmSha256:reply.wasmSha256,loaderSha256:reply.loaderSha256,...(biber?{bootstrapSha256:reply.bootstrapSha256}:{}),sourceMapPayload,sourceMapGeometryPayload,
  ...(directPdf?{sourceMapLegacyZeroOrigin}:{})};
}
export function execute(frame){
 if(!transportFrame(frame))throw new Error('invalid bounded native transport frame');
 const legacy=frame.tag.version===1&&(frame.tag.profile===undefined||frame.tag.profile===0)&&frame.tag.runtimeIdentity==null;
 const modern=frame.tag.version===2&&frame.tag.profile===1&&typeof frame.tag.runtimeIdentity==='string'&&/^[0-9a-f]{64}$/.test(frame.tag.runtimeIdentity);
 const authoring=frame.tag.version===3&&frame.tag.profile===2&&typeof frame.tag.runtimeIdentity==='string'&&/^[0-9a-f]{64}$/.test(frame.tag.runtimeIdentity);
 // Additive authoring phase admission; same runtime/profile and physical cancellation.
 const authoring4=[4,5].includes(frame.tag.version)&&frame.tag.profile===2&&typeof frame.tag.runtimeIdentity==='string'&&/^[0-9a-f]{64}$/.test(frame.tag.runtimeIdentity);
 const identity=typeof frame.tag.runtimeIdentity==='string'&&/^[0-9a-f]{64}$/.test(frame.tag.runtimeIdentity);
 const mapped=(pB(frame.tag.profile)&&frame.tag.version===23&&frame.tag.engineId==='pdftex'||frame.tag.version===6&&([3,4].includes(frame.tag.profile)&&frame.tag.engineId==null
  ||[5,6,7,8,9,10,12].includes(frame.tag.profile)&&frame.tag.engineId==='pdftex'))&&identity;
 const dviPhase=(pB(frame.tag.profile)&&frame.tag.version===24||frame.tag.version===7&&[4,5,6,7].includes(frame.tag.profile)||frame.tag.version===([10,12].includes(frame.tag.profile)?15:12)&&[8,9,10,12].includes(frame.tag.profile))&&frame.tag.engineId==='latex-dvi'&&identity;
 const xe=(frame.tag.version===8&&[5,6,7,8].includes(frame.tag.profile)||frame.tag.version===([12,13,14,15,16,17,18,19,20,21].includes(frame.tag.profile)?21:frame.tag.profile===10?16:13)&&[9,10,12,13,14,15,16,17,18,19,20,21].includes(frame.tag.profile))&&frame.tag.engineId==='xelatex'&&identity;
 const lua=(frame.tag.version===9&&[5,6,7].includes(frame.tag.profile)||frame.tag.version===([12,13,14,15,16,17,18,19,20,21].includes(frame.tag.profile)?22:frame.tag.profile===10?17:14)&&[8,9,10,12,13,14,15,16,17,18,19,20,21].includes(frame.tag.profile))&&frame.tag.engineId==='lualatex'&&identity;
 const biber=pB(frame.tag.profile)&&frame.tag.version===10&&frame.tag.tool==='biber'&&identity;
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
 const expectedTag=ownNativeTag(frame.tag),caps={log:frame.maxLogBytes,output:frame.maxOutputBytes,
  outputs:new Set(frame.outputs),inputs:frame.files.length,selectedRoot:frame.selectedRoot,
  selectedPdf:selectedPdfPhase(expectedTag)?frame.selectedRoot.split('/').at(-1).slice(0,-4)+'.pdf':null};
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
       ||caps.selectedPdf!==null&&frame.selectedRoot!==caps.selectedRoot
       ||frame.outputs.length!==caps.outputs.size||frame.outputs.some(n=>!caps.outputs.has(n)))throw new Error('changed native frame');
      const phase={operation:expectedTag.tool,tag:expectedTag,mainEntry:frame.mainEntry,selectedRoot:frame.selectedRoot,files:frame.files.map(f=>({name:f.name,bytes:bytes(f.bytes)})),outputs:frame.outputs,maxLogBytes:frame.maxLogBytes,maxOutputBytes:frame.maxOutputBytes,runtimeResourceNames:frame.runtimeResourceNames,generatedInputNames:frame.generatedInputNames,...(pB(frame.tag.profile)?{bibliographyTool:frame.bibliographyTool}:{}),
       ...(frame.dviImageSidecars!=null?{dviImageSidecars:frame.dviImageSidecars.map(({name,sourceName,sourceSha256})=>({name,sourceName,sourceSha256}))}:{})};
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
