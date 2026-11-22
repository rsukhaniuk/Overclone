const pB=p=>p===13||p===14||p===15||p===16||p===17||p===18||p===19||p===20||p===21;
// Browser/WASM/owned-byte glue only. C# selects the backend and admits BCF.
// Genuine Perl/Biber, filesystem confinement and output limits live in C++/WASM.
const send=self.postMessage.bind(self),encode=new TextEncoder(),decode=new TextDecoder('utf-8',{fatal:true});
const pins={loader:'e8cece7433da70dc83372d18ce314eea7c911467cdfc9b5cb3a877cf80d1dee7',
 wasm:'f68864e55dafda71bf25e6903f0f9490a7c4fb14302cb3f0650e56dbc36d0201',
 data:'28eaeec67f837530073d1de54ba3ab92a7fc8e96bcd83255eb6aa1a0a037c10b'};
const sha=async bytes=>[...new Uint8Array(await crypto.subtle.digest('SHA-256',bytes))].map(v=>v.toString(16).padStart(2,'0')).join('');
let module,loaderSha256,wasmSha256,bootstrapSha256,used=false,bootUsed=false,bootFailed=false;
const path=n=>typeof n==='string'&&n.length>0&&n.length<=249&&n.isWellFormed()&&encode.encode(n).length<=249
 &&n===n.trim()&&!/[\u0000-\u001f\u007f-\u009f\\:<>"|?*]/.test(n)&&n.split('/').length<=32
 &&n.split('/').every(p=>p&&p!=='.'&&p!=='..'&&!/[. ]$/.test(p));
const alias=n=>n.normalize('NFC').toUpperCase();
function validate(d){
 const t=d?.tag;
 if(!t||!pB(t.profile)||t.version!==10||t.tool!=='biber'||d.operation!=='biber'||d.bibliographyTool!=='biber'
  ||!['pdftex','latex-dvi','xelatex','lualatex'].includes(t.engineId)||!Number.isSafeInteger(t.ordinal)||t.ordinal<1||t.ordinal>4294967295
  ||typeof t.nonce!=='string'||t.nonce.length<1||t.nonce.length>128||!t.nonce.isWellFormed()
  ||typeof t.runtimeIdentity!=='string'||!/^[0-9a-f]{64}$/.test(t.runtimeIdentity)
  ||t.profile===17&&t.runtimeIdentity!=='41d0bb841b4832771327e44c84ada913e75204e25c5f39d6e52204e7350bf109'
  ||t.profile===18&&t.runtimeIdentity!=='f4ad5c7f702efc4f90d7a803ad6c697be0b3664b4ca90f837c8e6bd74ca88a40'
  ||t.profile===19&&t.runtimeIdentity!=='1974b526f3a47b33f60d5a5384f14b3c618ac996d950ab514e13ef119d06bc79'
  ||t.profile===20&&t.runtimeIdentity!=='84abead5a2feb357d52ff01364f5b72ed099610c40a8c0428ac10f9a64b0e652'
  ||t.profile===21&&t.runtimeIdentity!=='eb47ed92ac9efcdc7277c6b3dd1c5861abde63213e440d40aa7c201af6f4d692' && t.runtimeIdentity !== '902f858d535c6a4fa273a24bec9c8a0fee7093e67ea6dd8da062bff1e43bbbe5' && t.runtimeIdentity !== 'f6a4fd79dfe6453b58b64d3a2322fcb4f359f5d3fa0cedadc90abdb3ac9297dc' && t.runtimeIdentity !== '382c1f851ba3d3de852d1a7fbea499820a7a68de08bbdcb333eaf4d841afd0ec'
  ||!path(d.selectedRoot)||!/\.tex$/i.test(d.selectedRoot)||!path(d.mainEntry)
  ||!Array.isArray(d.files)||d.files.length<1||d.files.length>64||!Array.isArray(d.outputs)||d.outputs.length!==2
  ||!Number.isSafeInteger(d.maxLogBytes)||d.maxLogBytes<1||d.maxLogBytes>262144
  ||!Number.isSafeInteger(d.maxOutputBytes)||d.maxOutputBytes<1||d.maxOutputBytes>8388608
  ||!Array.isArray(d.runtimeResourceNames)||d.runtimeResourceNames.length!==0
  ||!Array.isArray(d.generatedInputNames)||d.generatedInputNames.length!==1||d.generatedInputNames[0]!==d.mainEntry)throw Error('Biber frame');
 const leaf=d.selectedRoot.slice(d.selectedRoot.lastIndexOf('/')+1),job=t.engineId==='lualatex'?'result':leaf.slice(0,-4);
 if(d.mainEntry!==job+'.bcf'||new Set(d.outputs).size!==2||!d.outputs.includes(job+'.bbl')||!d.outputs.includes(job+'.blg'))throw Error('Biber namespace');
 const names=new Set();let total=0,bcf=false;
 for(const f of d.files){
  if(!f||!path(f.name)||names.has(alias(f.name))||!(f.bytes instanceof Uint8Array)||f.bytes.length>8388608)throw Error('Biber input');
  names.add(alias(f.name));total+=f.bytes.length;if(total>8388608)throw Error('Biber input quota');
  if(f.name===d.mainEntry){if(!f.bytes.length)throw Error('Biber BCF');bcf=true;}
  if(d.outputs.some(n=>alias(n)===alias(f.name)))throw Error('Biber output collision');
 }
 if(!bcf)throw Error('Biber BCF');
 for(const n of names)for(let at=n.indexOf('/');at>=0;at=n.indexOf('/',at+1))if(names.has(n.slice(0,at)))throw Error('Biber input collision');
}
self.onmessage=async({data:d})=>{
 try{
  if(bootUsed){bootFailed=true;throw Error('Biber bootstrap one shot');}bootUsed=true;
  if(d.operation!=='boot'||module||!(d.loader instanceof Uint8Array)||d.loader.length!==777719
   ||!(d.wasm instanceof Uint8Array)||d.wasm.length!==8698666||!(d.bootstrapData instanceof Uint8Array)||d.bootstrapData.length!==18216642)throw Error('Biber boot');
  [loaderSha256,wasmSha256,bootstrapSha256]=await Promise.all([sha(d.loader),sha(d.wasm),sha(d.bootstrapData)]);
  if(bootFailed||loaderSha256!==pins.loader||wasmSha256!==pins.wasm||bootstrapSha256!==pins.data)throw Error('Biber identity');
  self.fetch=()=>Promise.reject(Error('Biber network unavailable'));
  self.XMLHttpRequest=class{constructor(){throw Error('Biber network unavailable');}};
  importScripts(d.loaderUrl);
  if(typeof self.createBiber!=='function')throw Error('Biber factory');
  let preloaded=0;
  module=await self.createBiber({noInitialRun:true,wasmBinary:d.wasm,print(){},printErr(){},
   locateFile(name){if(name==='biber.data'||name==='biber.wasm')return name;throw Error('Biber loader path');},
   getPreloadedPackage(name,size){
    if(name!=='biber.data'||size!==18216642||preloaded++)throw Error('Biber bootstrap');
    return d.bootstrapData.buffer.slice(d.bootstrapData.byteOffset,d.bootstrapData.byteOffset+d.bootstrapData.byteLength);
   },
   instantiateWasm(imports,done){const instance=new WebAssembly.Instance(new WebAssembly.Module(d.wasm),imports);done(instance);return instance.exports;}
  });
  if(bootFailed||preloaded!==1||module._oc_biber_abi()!==1||['_malloc','_free','_oc_biber_configure','_oc_biber_add_file','_oc_biber_run',
   '_oc_biber_output_size','_oc_biber_copy_output','_oc_biber_error'].some(n=>typeof module[n]!=='function'))throw Error('Biber ABI');
  self.onmessage=execute;send({kind:'ready'});
 }catch{send({kind:'boot-error',error:'Biber bootstrap unavailable'});}
};
async function execute({data:d}){
 let entered=false,nativeReturn=null,elapsedMs=null,outcome='setup-failure',files=[],inputs=[],log='';
 const blob=(value,fn)=>{
  const bytes=typeof value==='string'?encode.encode(value):value,p=module._malloc(Math.max(1,bytes.length));
  if(!Number.isSafeInteger(p)||p<1||p>module.HEAPU8.length-bytes.length)throw Error('Biber allocation');
  try{module.HEAPU8.set(bytes,p);return fn(p,bytes.length);}finally{module._free(p);}
 };
 const take=(kind,maximum)=>{
  const n=module._oc_biber_output_size(kind);if(!Number.isInteger(n)||n<0||n>maximum)throw Error('Biber output');
  return blob(new Uint8Array(n),(p)=>{if(module._oc_biber_copy_output(kind,p,n)!==n)throw Error('Biber output copy');return module.HEAPU8.slice(p,p+n);});
 };
 try{
  if(used)throw Error('Biber one shot');used=true;validate(d);
  if(blob(d.mainEntry,(p,n)=>blob(d.tag.nonce,(q,k)=>module._oc_biber_configure(p,n,q,k,d.maxLogBytes,d.maxOutputBytes)))!==0)throw Error('Biber configuration');
  for(const f of d.files){
   inputs.push({name:f.name,length:f.bytes.length,sha256:await sha(f.bytes)});
   if(blob(f.name,(p,n)=>blob(f.bytes,(q,k)=>module._oc_biber_add_file(f.name===d.mainEntry?2:1,p,n,q,k)))!==0)throw Error('Biber file');
  }
  send({kind:'entered'});entered=true;const start=performance.now();nativeReturn=module._oc_biber_run();elapsedMs=performance.now()-start;
  if(!Number.isInteger(nativeReturn))throw Error('Biber return');
  outcome=nativeReturn===-3?'resource-limit':'native-return';
  if(nativeReturn===0){
   const bbl=take(0,d.maxOutputBytes);if(!bbl.length)throw Error('Biber BBL');
   const blg=take(1,d.maxOutputBytes-bbl.length);log=decode.decode(take(2,d.maxLogBytes));
   const job=d.mainEntry.slice(0,-4);files=[{name:job+'.bbl',bytes:bbl}];if(blg.length)files.push({name:job+'.blg',bytes:blg});
  }else {const message='Biber failed with native status '+nativeReturn+'.';if(encode.encode(message).length<=d.maxLogBytes)log=message;}
 }catch(error){files=[];log='';outcome=error instanceof WebAssembly.RuntimeError?'wasm-abort':entered?'protocol-failure':'setup-failure';}
 send({kind:'done',reply:{tag:d?.tag,outcome,nativeReturn,entered,log,files,inputs,elapsedMs,
  heapBytes:module?.HEAPU8.length,loaderSha256,wasmSha256,bootstrapSha256}});
}
