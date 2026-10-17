// Accepted normal Swift loader/VFS/public entry-ABI glue, no engine patch/policy.
/* global FS, HEAPU8, cwrap, prepareExecutionContext, _compileLaTeX, _compileBibtex */
const emit=self.postMessage.bind(self);let phase='boot',lookups=[],wasmSha256=null,loaderSha256=null;
const sha=async bytes=>[...new Uint8Array(await crypto.subtle.digest('SHA-256',bytes))].map(v=>v.toString(16).padStart(2,'0')).join('');
self.postMessage=m=>{if(!m.cmd&&m.result==='ok')emit({kind:'ready'});};
self.onmessage=async({data:d})=>{
 try{
  if(d.operation!=='boot')throw new Error('boot');
  wasmSha256=await sha(d.wasm);loaderSha256=await sha(d.loader);
  self.fetch=async url=>{if(phase==='boot'&&String(url).endsWith('swiftlatexpdftex.wasm'))return new Response(d.wasm,{headers:{'Content-Type':'application/wasm'}});throw new Error('no loader request available');};
  // Existing accepted local missing-response pattern, never a network retry.
  self.XMLHttpRequest=class{constructor(){this.status=301;this.response=null;}open(_method,url){this.url=String(url);}send(){if(lookups.length>=128)throw new Error('lookup log cap');lookups.push(this.url);}getResponseHeader(){return null;}};
  importScripts(d.loaderUrl);console.log=()=>{};console.error=()=>{};self.onmessage=execute;
 }catch(e){emit({kind:'boot-error',error:String(e)});}
};
async function execute({data:d}){
 let entered=false,nativeReturn=null,elapsedMs=null,capturedLog=self.memlog,limited=false;const files=[],inputs=[];
 try{
  if(d.tag.version!==1||!['latex','bibtex'].includes(d.operation)||d.files.length>512||d.outputs.length>64)throw new Error('FFI frame');
  let total=0;for(const f of d.files){total+=f.bytes.byteLength;if(total>16777216||f.bytes.byteLength>8388608)throw new Error('VFS transport bound');}
  for(const f of d.files){const p='/work/'+f.name;const slash=p.lastIndexOf('/');FS.mkdirTree(p.slice(0,slash));FS.writeFile(p,f.bytes);}
  Object.defineProperty(self,'memlog',{configurable:true,get:()=>capturedLog,set:value=>{if(value.length>Math.floor(d.maxLogBytes/3)){limited=true;throw new Error('log transport bound');}capturedLog=value;}});
  prepareExecutionContext();phase=d.operation;
  for(const f of d.files){const b=FS.readFile('/work/'+f.name),hash=new Uint8Array(await crypto.subtle.digest('SHA-256',b));inputs.push({name:f.name,length:b.length,sha256:[...hash].map(v=>v.toString(16).padStart(2,'0')).join('')});}
  cwrap('setMainEntry','number',['string'])(d.mainEntry);emit({kind:'entered'});entered=true;const start=performance.now();
  nativeReturn=d.operation==='latex'?_compileLaTeX():_compileBibtex();elapsedMs=performance.now()-start;
  if(!Number.isInteger(nativeReturn))throw new Error('native return');
  let size=0;for(const name of d.outputs){const p='/work/'+name;if(!FS.analyzePath(p).exists)continue;const n=FS.stat(p).size;if(n>d.maxOutputBytes||n>2*d.maxOutputBytes-size){limited=true;throw new Error('output transport bound');}size+=n;files.push({name,bytes:FS.readFile(p)});}
  emit({kind:'done',reply:{tag:d.tag,outcome:'native-return',nativeReturn,entered,log:capturedLog,files,inputs,elapsedMs,heapBytes:HEAPU8.byteLength,wasmSha256,loaderSha256}});
 }catch(e){
  const outcome=limited?'resource-limit':e instanceof WebAssembly.RuntimeError?'wasm-abort':entered?'protocol-failure':'setup-failure';
  emit({kind:'done',reply:{tag:d.tag,outcome,nativeReturn,entered,log:capturedLog||'',files:[],inputs,elapsedMs,heapBytes:typeof HEAPU8==='undefined'?null:HEAPU8.byteLength,wasmSha256,loaderSha256}});
 }
}
