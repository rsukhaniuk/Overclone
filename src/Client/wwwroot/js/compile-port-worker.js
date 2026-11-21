// Native compiler/VFS glue.
const pB=p=>p===13||pT(p);
const pT=p=>p===14||p===15||p===16||p===17||p===18;
const pU=p=>p===12||pB(p);
const selectedPdfIdentity="41d0bb841b4832771327e44c84ada913e75204e25c5f39d6e52204e7350bf109";
const courseworkIdentity="f4ad5c7f702efc4f90d7a803ad6c697be0b3664b4ca90f837c8e6bd74ca88a40";
const selectedPdfProfile=p=>p===17||p===18;
const pdf12=d=>selectedPdfProfile(d.tag?.profile)&&rid(d.tag)&&d.tag.version===23&&d.tag?.engineId==='pdftex'&&d.operation==='latex';
const pdfLeaf=d=>d.selectedRoot.split('/').at(-1).slice(0,-4)+'.pdf';
const emit=self.postMessage.bind(self);let phase='boot',lookups=[],wasmSha256=null,loaderSha256=null;
let dviModule=null,dviWriteGuard=null,semanticModule=false;
const sLoader='735c17494d6ec61c1b2f000f437f343ece9af3258f2f354f11745b7b5836dde2';
const rid=t=>selectedPdfProfile(t.profile)?t.runtimeIdentity===(t.profile===17?selectedPdfIdentity:courseworkIdentity):/^[0-9a-f]{64}$/.test(t.runtimeIdentity??'');
const sha=async bytes=>[...new Uint8Array(await crypto.subtle.digest('SHA-256',bytes))].map(v=>v.toString(16).padStart(2,'0')).join('');
self.postMessage=m=>{if(!m.cmd&&m.result==='ok')emit({kind:'ready'});};
self.onmessage=async({data:d})=>{
try{
if(d.operation!=='boot')throw new Error('boot');
wasmSha256=await sha(d.wasm);loaderSha256=await sha(d.loader);
self.fetch=async url=>{if(phase==='boot'&&String(url).endsWith('swiftlatexpdftex.wasm'))return new Response(d.wasm,{headers:{'Content-Type':'application/wasm'}});throw new Error('loader request denied');};
self.XMLHttpRequest=class{constructor(){this.status=301;this.response=null;}open(_method,url){this.url=String(url);}send(){if(lookups.length>=128)throw new Error('lookup log cap');lookups.push(this.url);}getResponseHeader(){return null;}};
importScripts(d.loaderUrl);console.log=()=>{};console.error=()=>{};
semanticModule=typeof self.createFontSemantics==='function'||loaderSha256===sLoader;
if(semanticModule||typeof self.createDviMaps==='function'){
const factory=semanticModule?self.createFontSemantics:self.createDviMaps;
if(typeof factory!=='function'||(semanticModule?loaderSha256!==sLoader||d.loader.byteLength!==141918||d.wasm.byteLength!==1952801
||wasmSha256!=='e3136893e75d5b6b3a7b4297349c28f51423dee2c014a607d9c1cd20f75357c3'
:loaderSha256!=='980bcf858794dc4679124df6fbf76a503d3020d96a7081537e9d1a4a5e7bb6f1'
||wasmSha256!=='6ad6db6625d28bdfaf83eba48b44a4202582fd84118c1e8ece3857535722a1b9'))throw new Error('module hash');
self.fetch=()=>Promise.reject(new Error('Network denied'));
dviModule=await new Promise((resolve,reject)=>{
Promise.resolve(factory({noInitialRun:true,print:captureDviLog,printErr:captureDviLog,
instantiateWasm(imports,receive){
const seen=new Set();for(const values of Object.values(imports))if(values&&typeof values.fd_write==='function'&&!seen.has(values)){
seen.add(values);const original=values.fd_write;
values.fd_write=(fd,iov,count,pnum)=>dviWriteGuard?dviWriteGuard(fd,iov,count,pnum,original):original(fd,iov,count,pnum);
}
WebAssembly.instantiate(d.wasm,imports).then(r=>receive(r.instance)).catch(reject);return {};
}})).then(resolve,reject);
});
if(dviModule._oc_dvi_maps_abi()!==1||dviModule._oc_synctex_abi()!==65536
||semanticModule&&(dviModule._oc_font_semantics_abi?.()!==1||typeof dviModule._oc_font_semantics_status!=='function')
||['_oc_mem_configure','_oc_synctex_length','_oc_synctex_copy','_oc_synctex_status'].some(n=>typeof dviModule[n]!=='function'))throw new Error('DVI ABI');
self.onmessage=executeDvi;emit({kind:'ready'});
}else self.onmessage=execute;
}catch(e){emit({kind:'boot-error',error:String(e)});}
};
const nativeVfsShapes={
'createNode':'27d22ff56cb97aef4c0876f468311a58b78c56dde6c39ba46740d63ca2d6579b',
'getFileDataAsTypedArray':'5db9a9766e823a9bab37c1224476bccf9836bd1ddd2a359b7294dd046036804e',
'expandFileStorage':'6a3f2a6e13407f891d0212e73b8d880a06aefd4b90c2c95c535a4a08678b4624',
'resizeFileStorage':'48ce81b372ae1d18569a30086e96b859cbc7cdac509d5186db8c2ddb14d433a5',
'node_ops.getattr':'4e5d31581570b18149425277e81fd78ff525456e9a46210b541a64d8fe8f3d48',
'node_ops.setattr':'a33cd149d4b7ffb0c3724c9ea197c00d9b02316ecbb6bf7d9720984d04e6fc07',
'node_ops.mknod':'b4ff43a4dfb3c22c673e0da3bcd5378aa493c7c0e803a6a06d55672c20372d7f',
'node_ops.rename':'14b636dbbb61fe2e78676bc9b6308e6f1c9f5281be9c2540984b67c09fb9eef9',
'node_ops.unlink':'c761759d8ea44b0245b048a28c5fb9361761d2e27c24830914a14048dd0190d4',
'node_ops.rmdir':'053f2e936851a273fc652f8b752068c643f8a77e529a9a76d38ef8ba7c1c997e',
'node_ops.symlink':'918aa61fd6884fcdc35842363ece4b35077f9605a8a13347e877e40504083e48',
'stream_ops.read':'2ee08b5798f3c7c58351c457d7a4e158aaa20155e83e3b1bd6e66fb97a6801fd',
'stream_ops.write':'2d8cf1638f2c6aed577676ece5d8bede75dc1eabc4660c3f0713206dd64a7d20',
'stream_ops.llseek':'4a4df7ffc8fc2f20fca2308ba6765689b8be43120cf663d19aa7b64dfab1f5ac',
'stream_ops.mmap':'8e7596d8e2ea7bd71c55cc6eda1b54cf6fa47809be9e8a75b4d172e89e339aee',
'stream_ops.msync':'6070bb63e9f0d9485c311d544ff3e80b28d5bb3723dbe62fc6db673a1012f57f',
'FS.read':'85ec208cf704f2c16264e41419bc86e6ffd4265a2c58a32058e2cff2039b51bb',
'FS.write':'c9e5581b0bed29fbdb34bca7ab18ce5d2e65b5f5cf386d65e1a5644e7a99d0f2',
'FS.open':'9fe6de4ee3119fb3a3703b639972b7eaab0ece86eb4202cd4d37e835e0985d4e',
'FS.doSetAttr':'83f535305659b13c781c9268a3f28c1e39a0524daf082b174a8ff17ae4549cf2',
'FS.doTruncate':'909fa9deb5f196a42632981c96cbaceb6ae8cf7b8f4cd81c3dd3b559ace06d7b',
'FS.mknod':'a503bfa4486280f6ea62d74f7a281064dd58f7e2c2173ce5f380035f7b7f9d0d',
'FS.rename':'cb1c31684c58b10b030a34f3d481b9a4925f3ea4db4a7032fc053d1a9dee602b',
'FS.unlink':'e1df6442ec1c2e13ecbd6b657927b1659639febf26a02e33616d15d79335d27d',
'FS.rmdir':'0a3d4dfcfa64443c11e1794488c2594c5c010a6e2ee99754b4a1970d703e16cd',
'FS.mmap':'ffea5d21d9bb609b40ca5f7dc83b1b5e7f9193a3df063bdf401eef6f57433adb',
'FS.msync':'f5432227f49327c5a3db969d10214e123855bad6aef608a59619e74dab44b555',
'FS.createNode':'a04175d058cd87fde14eec3b63815ca38223e815a3d25ffdc5cf74df5fa07e7a',
'FS.createStream':'53ddc276a996228af04ad6518bd7b5026b2866976315299dd6018f7ca56cdfa6',
'FS.readFile':'85b7855ae273c8c58b1765f05a93eab7c7fea85346b8dd2b35debc66f4da2cd6',
'FS.writeFile':'fbfa835dcb37247a91d1ce9bb9b0c3b2485e343e4033f7fdc2267bd4b08ddd06',
'FS.createDataFile':'0aa3bd705398912b818dc1d64ab4585265b7827072bd2b66c0db99715a3eb11b',
'FS.createLazyFile':'e03412f5831199bccd5931a12195045461b6ae4c20982974092ca99d00a88387'
};
async function installNativeVfsBudget(fs,mem,maximum,maximumLog=65536,nodeLimit=64,adapter=null,baselineLimit=8192,bufferedPdf=null){
const shape=(name='')=>{throw new Error('unsupported pinned VFS shape'+(name?': '+name:''));};
const isBytes=value=>value instanceof Uint8Array||value instanceof Int8Array;
if(!Number.isSafeInteger(maximum)||maximum<1||maximum>(bufferedPdf===null?8388608:12582912)||!Number.isSafeInteger(maximumLog)||maximumLog<0||maximumLog>8388608||![64,128].includes(nodeLimit)||![8192,12288].includes(baselineLimit))shape();
const standard=Math.min(8388608,maximum),cap=2*standard,workCap=baselineLimit===12288?67108864:cap,records=new Map(),tables=new Map();
const original={},root=fs?.root,ops=mem?.ops_table;
if(!root||!ops||!fs.streams||fs.streams.length>4096||fs.allocate!==undefined)shape();
for(const key of ['lookupPath','isFile','isDir','isLink','isChrdev','write','open','doSetAttr','doTruncate','mknod','rename','unlink','rmdir','mmap','msync'])if(typeof fs[key]!=='function')shape();
if(!adapter&&(typeof doWritev!=='function'||await sha(new TextEncoder().encode(Function.prototype.toString.call(doWritev)))!=='4adb7d836259f016e678f24ae02e9f10428ed78ff985401a4938f1ad5bc7b447'))shape();
const originalWritev=adapter?null:doWritev,shapes=[];
const heap=()=>adapter?adapter.heap():HEAPU8;
for(const [path,digest] of Object.entries(nativeVfsShapes)){
const keys=path.split('.');let object=path.startsWith('FS.')?fs:mem;
if(keys[0]==='FS')keys.shift();
for(const key of keys.slice(0,-1))object=object?.[key];
const fn=object?.[keys.at(-1)];if(typeof fn!=='function')shape();
const text=Function.prototype.toString.call(fn),actual=await sha(new TextEncoder().encode(text));
if(text.length>8192||!adapter&&actual!==digest)shape(path);
shapes.push(path+'\t'+actual);original[path]=fn;
}
if(adapter&&await sha(new TextEncoder().encode(shapes.join('\n')))!=='715ac9c688cf689885e12bec948aafb8d10dd2cad9f1762125d5392aaf721774')shape('DVI aggregate');
const remember=(table,keys,source)=>{
if(!table||Object.keys(table).length!==keys.length)shape();
for(const key of keys)if(table[key]!==source[key]||!Object.getOwnPropertyDescriptor(table,key)?.writable)shape();
tables.set(table,source);
};
const fileKeys=['llseek','read','write','mmap','msync'];
remember(mem.stream_ops,fileKeys,mem.stream_ops);
remember(mem.node_ops,['getattr','setattr','lookup','mknod','rename','unlink','rmdir','readdir','symlink','readlink'],mem.node_ops);
for(const kind of ['dir','file','link','chrdev']){
const keys=kind==='dir'?['getattr','setattr','lookup','mknod','rename','unlink','rmdir','readdir','symlink']:kind==='link'?['getattr','setattr','readlink']:['getattr','setattr'];
remember(ops[kind]?.node,keys,mem.node_ops);
}
remember(ops.file.stream,fileKeys,mem.stream_ops);
remember(ops.dir.stream,['llseek'],mem.stream_ops);
const work=fs.lookupPath('/work',{follow:true}).node;
if(!fs.isDir(work?.mode)||work.mounted)shape();
const inside=node=>{for(let n=0;node&&n<8192;n++,node=node.parent){if(node===work)return true;if(node===node.parent)break;}return false;};
const pending=[root];let baselineBytes=0,baselineCapacity=0;
while(pending.length){
const node=pending.pop();if(records.has(node)||records.size>=baselineLimit)shape();
const regular=fs.isFile(node.mode),directory=fs.isDir(node.mode);
const kind=regular?'file':directory?'dir':fs.isLink(node.mode)?'link':fs.isChrdev(node.mode)?'chrdev':null;
if(!kind||node.mounted&&inside(node))shape();
remember(node.node_ops,Object.keys(ops[kind].node),mem.node_ops);
if(regular){
if(!isBytes(node.contents)||!Number.isSafeInteger(node.usedBytes)||node.usedBytes<0||node.usedBytes>node.contents.length)shape();
remember(node.stream_ops,fileKeys,mem.stream_ops);baselineBytes+=node.usedBytes;baselineCapacity+=node.contents.buffer.byteLength;
}
records.set(node,{base:regular?node.usedBytes:0,high:0});
if(directory)for(const child of Object.values(node.contents)){if(child.parent!==node)shape();pending.push(child);}
}
if(!records.has(work)||!Number.isSafeInteger(baselineBytes)||!Number.isSafeInteger(baselineCapacity))shape();
for(const stream of fs.streams)if(stream&&fs.isFile(stream.node.mode)){
if(!records.has(stream.node))shape();remember(stream.stream_ops,fileKeys,mem.stream_ops);
}
let failed=false,backingBytes=0,copyBytes=0,deviceCopyBytes=0,growthBytes=0,newNodes=0;
const fail=reason=>{failed=true;throw new Error('VFS quota: '+reason);};
const uint=value=>{if(!Number.isSafeInteger(value)||value<0)fail('integer');return value;};
const add=(a,b)=>{uint(a);uint(b);if(b>Number.MAX_SAFE_INTEGER-a)fail('integer');return a+b;};
const project=node=>{if(failed)fail('latched');if(!records.has(node)||!inside(node))fail('project node');};
const regular=node=>{
project(node);if(!fs.isFile(node.mode)||!isBytes(node.contents)||uint(node.usedBytes)>node.contents.length)fail('regular node');
return records.get(node);
};
const logicalPlan=(record,size,ceiling=standard)=>{
uint(size);if(size>Math.max(record.base,ceiling))fail('file bytes');
const high=Math.max(record.high,Math.max(0,size-record.base)),delta=high-record.high;
if(delta>cap-growthBytes)fail('aggregate growth');return{record,high,delta};
};
const selectedPdf=node=>bufferedPdf!==null&&node.parent===work&&node.name===bufferedPdf&&records.get(node).base===0;
const plan=(node,size)=>logicalPlan(regular(node),size,selectedPdf(node)?maximum:standard);
const commit=p=>{growthBytes+=p.delta;p.record.high=p.high;};
const capacity=bytes=>{uint(bytes);if(bytes>workCap-backingBytes)fail('backing work');};
const reserve=bytes=>{capacity(bytes);backingBytes+=bytes;};
// Reserve the original allocator's real capacity. Check logical extent against
// size separately, so this does not add bytes to the admitted PDF. Scope the
// lower-churn growth to the fresh selected PDF on the new profile.
const allocationTarget=(node,size)=>{
const previous=node.contents.length;
if(!selectedPdf(node)||previous<1048576||previous>=size)return size;
return Math.max(size,Math.min(maximum,2*previous));
};
const expanded=(node,size)=>{
const previous=node.contents.length;if(previous>=size)return 0;
let next=Math.max(allocationTarget(node,size),Math.floor(previous*(previous<1048576?2:1.125)));
if(previous)next=Math.max(next,256);uint(next);return next;
};
const resizePlan=(node,size)=>{
const p=plan(node,size);const bytes=node.usedBytes===size?0:size;capacity(bytes);return{p,bytes};
};
const bytesOnly=buffer=>{if(!isBytes(buffer))fail('byte input only');};
const copy=bytes=>{uint(bytes);if(bytes>workCap-copyBytes)fail('read/vector copy work');copyBytes+=bytes;};
const allocating=(fn,...args)=>{try{return fn(...args);}catch(error){failed=true;throw error;}};
const wrap={
expandFileStorage(node,size){const p=plan(node,size),bytes=expanded(node,size);capacity(bytes);commit(p);reserve(bytes);return allocating(original.expandFileStorage,node,allocationTarget(node,size));},
resizeFileStorage(node,size){const {p,bytes}=resizePlan(node,size);commit(p);reserve(bytes);return allocating(original.resizeFileStorage,node,size);},
createNode(parent,name,mode,dev){
project(parent);if(!fs.isDir(parent.mode)||(!fs.isFile(mode)&&!fs.isDir(mode)))fail('new node type');
if(newNodes>=nodeLimit)fail('node count');newNodes++;
const node=allocating(original.createNode,parent,name,mode,dev);records.set(node,{base:0,high:0});return node;
},
setattr(node,attr){
project(node);
if(attr.mode!==undefined&&(!Number.isSafeInteger(attr.mode)||(attr.mode&61440)!==(node.mode&61440)))fail('node type change');
if(attr.size!==undefined)resizePlan(node,attr.size);
return original['node_ops.setattr'](node,attr);
},
write(stream,buffer,offset,length,position,canOwn){
const node=stream.node;regular(node);bytesOnly(buffer);uint(offset);uint(length);uint(position);
if(add(offset,length)>buffer.length)fail('byte range');
if(!length)return original['stream_ops.write'](stream,buffer,offset,length,position,canOwn);
const own=!!canOwn&&buffer.buffer!==heap().buffer,end=add(position,length);
const direct=own||(!node.usedBytes&&!position),size=direct?length:Math.max(node.usedBytes,end);
const p=plan(node,size),bytes=own?buffer.buffer.byteLength:direct?length:expanded(node,end);
capacity(bytes);commit(p);
if(direct)reserve(bytes);
return allocating(original['stream_ops.write'],stream,buffer,offset,length,position,canOwn);
},
mmap(){fail('mmap unsupported by pinned allocator');},
msync(stream,buffer,offset,length,flags){return original['stream_ops.msync'](stream,buffer,offset,length,flags);},
rename(node,parent,name){project(node);project(parent);if(node===work)fail('project root');return original['node_ops.rename'](node,parent,name);},
unlink(parent,name){project(parent);return original['node_ops.unlink'](parent,name);},
rmdir(parent,name){project(parent);return original['node_ops.rmdir'](parent,name);},
symlink(){fail('new node type');}
};
for(const [table,source] of tables)for(const key of Object.keys(table))if((source===mem.stream_ops||source===mem.node_ops)&&wrap[key])table[key]=wrap[key];
mem.expandFileStorage=wrap.expandFileStorage;mem.resizeFileStorage=wrap.resizeFileStorage;mem.createNode=wrap.createNode;
fs.readFile=(path,options={})=>{
if(options.encoding!==undefined&&options.encoding!=='binary'||options.flags!==undefined&&options.flags!==0)fail('binary read only');
const node=fs.lookupPath(path,{follow:true}).node;regular(node);const size=uint(node.usedBytes);
copy(size);return allocating(original['FS.readFile'],path,options);
};
const prepareWrite=(path,data,options={})=>{
bytesOnly(data);
let flags=options.flags??577;
if(typeof flags==='string')flags=({r:0,'r+':2,w:577,'w+':578,a:1089,'a+':1090})[flags];
uint(flags);
const node=fs.lookupPath(path,{follow:!(flags&131072),noent_okay:true}).node;
if(node)regular(node);else{project(fs.lookupPath(path,{parent:true}).node);if(newNodes>=nodeLimit)fail('node count');}
const used=node&&!(flags&512)?node.usedBytes:0,position=flags&1024?used:0,length=data.length;
const own=!!options.canOwn&&data.buffer!==heap().buffer,direct=own||(!used&&!position);
const size=direct?length:Math.max(used,add(position,length));
if(node)plan(node,size);else logicalPlan({base:0,high:0},size);
capacity(!length?0:own?data.buffer.byteLength:direct?length:expanded(node,add(position,length)));
};
fs.writeFile=(path,data,options)=>{prepareWrite(path,data,options);return original['FS.writeFile'](path,data,options);};
fs.createDataFile=(parent,name,data,...rest)=>{
let path=name;
if(parent){const base=typeof parent==='string'?parent:fs.getPath(parent);path=name?PATH.join2(base,name):base;}
if(data)prepareWrite(path,data,{canOwn:rest[2]});
return original['FS.createDataFile'](parent,name,data,...rest);
};
fs.createLazyFile=()=>fail('lazy file unsupported after initialization');
const guardedWritev=(stream,iov,count,offset,invoke)=>{
const HEAPU8=heap(),HEAPU32=new Uint32Array(HEAPU8.buffer);
uint(iov);uint(count);if(count>1024||iov%4||add(iov,8*count)>HEAPU32.byteLength)fail('vector table');
let total=0;
for(let i=0;i<count;i++){
const at=iov/4+2*i,ptr=HEAPU32[at],length=HEAPU32[at+1];
if(add(ptr,length)>HEAPU8.length)fail('vector bytes');total=add(total,length);
}
if(count!==1){
if(fs.isFile(stream.node.mode)){
const node=stream.node;regular(node);
const position=offset===undefined?(stream.seekable&&(stream.flags&1024)?node.usedBytes:stream.position):offset;uint(position);
const direct=!node.usedBytes&&!position,end=add(position,total);
plan(node,direct?total:Math.max(node.usedBytes,end));capacity(direct?total:expanded(node,end));copy(total);
}else{
if(!records.has(stream.node)||!fs.isChrdev(stream.node.mode)||total>2*maximumLog-deviceCopyBytes)fail('device vector copy work');
deviceCopyBytes+=total;
}
}
return invoke?invoke():originalWritev(stream,iov,count,offset);
};
if(adapter)adapter.install(guardedWritev);else doWritev=guardedWritev;
return{get failed(){return failed;},snapshot:()=>({maximum,nodeLimit,baselineBytes,baselineCapacity,growthBytes,backingBytes,copyBytes,deviceCopyBytes,newNodes,nodeCount:records.size,failed})};
}
let dviLog='',dviLogBytes=0,dviMaxLog=0,dviLogLimited=false;
function captureDviLog(value){
const text=String(value)+'\n',n=new TextEncoder().encode(text).length;
if(n>dviMaxLog-dviLogBytes){dviLogLimited=true;throw new Error('DVI log quota');}
dviLog+=text;dviLogBytes+=n;
}
async function executeDvi({data:d}){
if(phase!=='boot')return;phase='running';
const m=dviModule,F=m.FS;let entered=false,nativeReturn=null,elapsedMs=null,budget=null,sourceMapPayload=null;
let files=[],inputs=[],outcome='setup-failure';
try{
const t=d.tag,s=pB(t?.profile);
if((semanticModule?!s||!(t.version===23&&t.engineId==='pdftex'||t.version===24&&t.engineId==='latex-dvi')
:![8,9,10,12].includes(t?.profile)||t.version!==([10,12].includes(t.profile)?15:12)||t.engineId!=='latex-dvi')||t.tool!=='latex'||d.operation!=='latex'
||!Number.isSafeInteger(t.ordinal)||t.ordinal<1||t.ordinal>4294967295
||!rid(t))throw 0;
validateAuthoringV4Frame(d);
if(!Array.isArray(d.runtimeResourceNames)||!Array.isArray(d.generatedInputNames)
||d.runtimeResourceNames.length>8192||d.generatedInputNames.length>128)throw 0;
const resources=new Set(d.runtimeResourceNames),generated=new Set(d.generatedInputNames),prefix='__oop_runtime__/';
if(resources.size!==d.runtimeResourceNames.length||generated.size!==d.generatedInputNames.length
||[...resources].some(n=>!n.startsWith(prefix)||!d.files.some(f=>f.name===n))
||[...generated].some(n=>resources.has(n)||!d.files.some(f=>f.name===n))||resources.has(d.selectedRoot)||generated.has(d.selectedRoot))throw 0;
let resourceBytes=0,workBytes=0,captured=0,sourceBytes=0;
for(const f of d.files){
if(resources.has(f.name)){if(f.bytes.length>67108864)throw 0;resourceBytes+=f.bytes.length;}
else{
 if(f.name.startsWith(prefix)||f.bytes.length>8388608)throw 0;
 workBytes+=f.bytes.length;
 if(!generated.has(f.name)){
  captured++;
  if(selectedPdfProfile(t.profile)&&(f.bytes.length>2097152||(sourceBytes+=f.bytes.length)>16777216))throw 0;
 }
}
}
if(resourceBytes>(s?134217728:167772160)||workBytes>33554432||resourceBytes+workBytes>167772160||captured>(pT(t.profile)?128:64))throw 0;
if(s){const name=prefix+'swiftlatexpdftex.fmt',fmt=d.files.find(f=>f.name===name),pdf=t.version===23;
if(!resources.has(name)||!fmt||fmt.bytes.length!==(pdf?22404123:22404979)
||await sha(fmt.bytes)!==(pdf?'69e67945852ac84675d043b3f7a5ef02adce773e64129427edb8ce0ebdd84deb':'729d02f1e2cd5b63886cf03520634b8fc319fdabe3ff61dd224134b8baf40605'))throw new Error('format hash');}
dviLog='';dviLogBytes=0;dviMaxLog=d.maxLogBytes;dviLogLimited=false;
F.mkdir('/tex');F.mkdir('/work');F.chdir('/work');
for(const f of d.files){
const target=resources.has(f.name)?'/tex/'+f.name.slice(prefix.length):'/work/'+f.name;
F.mkdirTree(target.slice(0,target.lastIndexOf('/')));F.writeFile(target,f.bytes);
inputs.push({name:f.name,length:f.bytes.length,sha256:await sha(f.bytes)});
}
budget=await installNativeVfsBudget(F,m.MEMFS,d.maxOutputBytes,d.maxLogBytes,128,{
heap:()=>m.HEAPU8,
install:guard=>{dviWriteGuard=(fd,iov,count,pnum,original)=>guard(F.getStreamChecked(fd),iov,count,undefined,()=>original(fd,iov,count,pnum));}
},s?12288:8192,pdf12(d)?pdfLeaf(d):null);
if(m._oc_mem_configure(1,1048575,t.ordinal)!==1||m.ccall('setMainEntry','number',['string'],[d.mainEntry])!==0)throw 0;
emit({kind:'entered'});entered=true;const start=performance.now();nativeReturn=m._compileLaTeX();elapsedMs=performance.now()-start;
if(!Number.isInteger(nativeReturn)||budget.failed||dviLogLimited)throw 0;
if(s&&nativeReturn===0&&m._oc_font_semantics_status()!==1){
if(m._oc_font_semantics_status()!==2)throw new Error('semantic status');
outcome='font-semantics-unsupported';
}else{
let total=0;
for(const name of d.outputs){const p='/work/'+name;if(!F.analyzePath(p).exists)continue;const s=F.stat(p);
const selected=pdf12(d)&&name===pdfLeaf(d),standard=Math.min(8388608,d.maxOutputBytes);
if(selected&&nativeReturn!==0&&s.size>standard)continue;
const maxFile=selected?d.maxOutputBytes:standard;
if(!F.isFile(s.mode)||s.size>maxFile||s.size>2*Math.min(8388608,d.maxOutputBytes)-total)throw 0;
total+=s.size;files.push({name,bytes:F.readFile(p)});
}
if(nativeReturn===0&&files.some(f=>s&&t.version===23?f.name.endsWith('.pdf')&&f.bytes.length>=5&&[37,80,68,70,45].every((v,i)=>f.bytes[i]===v)
:f.name.endsWith('.dvi')&&f.bytes.length>0&&(!s||f.bytes[0]===247&&f.bytes[1]===2))){
let pointer=0;
try{
const n=m._oc_synctex_length(t.ordinal);
if(Number.isSafeInteger(n)&&n>0&&n<=1048575&&(!s||m._oc_synctex_status()===1)){
pointer=m._malloc(n);
if(Number.isSafeInteger(pointer)&&pointer>0&&pointer<=m.HEAPU8.length-n
&&m._oc_synctex_copy(t.ordinal,pointer,n)===n&&pointer<=m.HEAPU8.length-n)sourceMapPayload=m.HEAPU8.slice(pointer,pointer+n);
}
}catch{sourceMapPayload=null;}finally{if(Number.isSafeInteger(pointer)&&pointer>0&&pointer<m.HEAPU8.length)m._free(pointer);}
}
outcome='native-return';
}
}catch(error){files=[];sourceMapPayload=null;outcome=budget?.failed||dviLogLimited?'resource-limit':error instanceof WebAssembly.RuntimeError?'wasm-abort':entered?'protocol-failure':'setup-failure';}
phase='done';
emit({kind:'done',reply:{tag:d.tag,outcome,nativeReturn,entered,log:dviLog,files,inputs,elapsedMs,
heapBytes:m.HEAPU8.byteLength,wasmSha256,loaderSha256,sourceMapPayload}});
}
function validateAuthoringV4Frame(d){
const bad=()=>{throw new Error('v4 FFI');};
const key=n=>d.tag.version>=5?n.normalize('NFC').toUpperCase():n.normalize('NFC').replace(/[a-z]/g,c=>c.toUpperCase());
const p13=pB(d.tag.profile),backend=d.bibliographyTool;
if(p13?!['none','bibtex','biber'].includes(backend)||d.operation==='bibtex'&&backend!=='bibtex':backend!=null)bad();
const suite=[5,6,7,8,9,10,12,13,14,15,16,17,18].includes(d.tag.profile),lua=suite&&[9,14,17,22].includes(d.tag.version);
const selected=d.tag.version>=5,root=selected?d.selectedRoot:'main.tex';
if(typeof root!=='string')bad();
const leaf=root.slice(root.lastIndexOf('/')+1),job=lua?'result':leaf.slice(0,-4);
if(selected&&(!(pU(d.tag.profile)&&d.tag.engineId==='xelatex'?/^[\p{L}\p{Nd}\p{Mn}\p{Mc}\u{10000}-\u{10FFFF}._/ -]+$/u:/^[\p{L}\p{Nd}\p{Mn}\p{Mc}._/ -]+$/u).test(root)||!/^.+\.tex$/i.test(leaf)||/^[.-]/.test(leaf)||leaf.startsWith(' ')||job.endsWith(' ')))bad();
const latexEntry=selected&&root.includes(' ')?'"'+root+'"':root;
const bibliography=[7,8,9,10,12,13,14,15,16,17,18].includes(d.tag.profile),blx=job.replaceAll(' ','_')+'-blx.bib',blxEnabled=bibliography&&!(p13&&backend==='biber');
const state=n=>n.endsWith('.aux')||['.bbl','.toc','.lof','.lot','.out'].some(ext=>n===job+ext)||blxEnabled&&n===blx;
const fixed=['.pdf','.aux','.bbl','.blg','.log','.toc','.lof','.lot','.out',...([7,12,15,24].includes(d.tag.version)?['.dvi']:[]),...([8,13,16,21].includes(d.tag.version)?['.xdv']:[]),...(p13&&backend==='biber'&&d.operation==='latex'?['.bcf','.run.xml']:[])].map(ext=>job+ext).concat(blxEnabled?[blx]:[]);
const valid=n=>typeof n==='string'&&n.length>0&&n===n.trim()&&new TextEncoder().encode(n).length<=249
&&!/[\u0000-\u001f\u007f-\u009f<>:"|?*\\\\]/.test(n)&&!Array.from(n).some(c=>c.length===1&&c.charCodeAt(0)>=55296&&c.charCodeAt(0)<=57343)
&&n.split('/').length<=32&&n.split('/').every(s=>s&&s!=='.'&&s!=='..'&&!/[. ]$/.test(s)
&&!['.GIT','.SVN','.HG','.SSH','.AWS'].includes(s.toUpperCase())&&!/^(CON|PRN|AUX|NUL|COM[1-4]|COM[5-9]|LPT[1-4]|LPT[5-9])(?:\.|$)/i.test(s));
if(!Array.isArray(d.files)||!Array.isArray(d.outputs)||d.files.length<1||d.files.length>(suite?(pT(d.tag.profile)?8448:8384):768)||d.outputs.length>(pT(d.tag.profile)?256:128)
||d.mainEntry!==(selected&&d.operation==='bibtex'?(lua?'result.tex':leaf):latexEntry)||d.tag.tool!==d.operation||!Number.isSafeInteger(d.maxOutputBytes)||d.maxOutputBytes<1||d.maxOutputBytes>(pdf12(d)?12582912:8388608)
||!Number.isSafeInteger(d.maxLogBytes)||d.maxLogBytes<0||d.maxLogBytes>8388608)bad();
const carried=bibliography&&Array.isArray(d.generatedInputNames)?new Set(d.generatedInputNames):null;
if(bibliography&&(!carried||carried.size!==d.generatedInputNames.length||carried.size>128
||[...carried].some(n=>typeof n!=='string'||!state(n)||!d.outputs.includes(n))))bad();
const inputs=new Map(),outputs=new Set(),all=new Set();
for(const f of d.files){if(!f||!valid(f.name)||!(f.bytes instanceof Uint8Array)||inputs.has(key(f.name)))bad();inputs.set(key(f.name),f.name);all.add(key(f.name));}
if(!valid(root)||!d.files.some(f=>f.name===root))bad();
if(bibliography&&[...carried].some(n=>inputs.get(key(n))!==n))bad();
if(selected&&d.operation==='bibtex'&&!d.files.some(f=>f.name===job+'.aux'&&f.bytes.length>0))bad();
for(const n of d.outputs){
if(!valid(n)||(!fixed.includes(n)&&!n.endsWith('.aux'))||outputs.has(key(n)))bad();
if(inputs.has(key(n))&&(!state(n)||inputs.get(key(n))!==n||bibliography&&!carried.has(n)))bad();
outputs.add(key(n));all.add(key(n));
}
if(fixed.some(n=>!d.outputs.includes(n)))bad();
for(const n of all)for(let i=n.indexOf('/');i>=0;i=n.indexOf('/',i+1))if(all.has(n.slice(0,i)))bad();
}
async function execute({data:d}){
if(pB(d.tag?.profile)){if(phase!=='boot')return;phase='running';}
let entered=false,nativeReturn=null,elapsedMs=null,capturedLog=self.memlog,limited=false,budget=null;const files=[],inputs=[];
try{
const legacy=d.tag.version===1&&(d.tag.profile===undefined||d.tag.profile===0)&&d.tag.runtimeIdentity==null;
const modern=d.tag.version===2&&d.tag.profile===1&&rid(d.tag);
const authoring=d.tag.version===3&&d.tag.profile===2&&rid(d.tag);
const identity=rid(d.tag),suite=[5,6,7,8,9,10,12,13,14,15,16,17,18].includes(d.tag.profile),p13=pB(d.tag.profile);
const p13Bibtex=p13&&d.operation==='bibtex'&&[[23,'pdftex'],[24,'latex-dvi'],[21,'xelatex'],[22,'lualatex']].some(([v,e])=>d.tag.version===v&&d.tag.engineId===e)&&identity;
const mapped=d.tag.version===6&&([3,4].includes(d.tag.profile)&&d.tag.engineId==null
||suite&&!p13&&d.tag.engineId==='pdftex')&&identity;
const dvi=d.tag.version===7&&[4,5,6,7].includes(d.tag.profile)&&d.tag.engineId==='latex-dvi'&&identity;
const unicodeBibtex=suite&&d.operation==='bibtex'&&((d.tag.version===8&&[5,6,7,8].includes(d.tag.profile)||d.tag.version===(d.tag.profile===12?21:d.tag.profile===10?16:13)&&[9,10,12].includes(d.tag.profile))&&d.tag.engineId==='xelatex'||(d.tag.version===9&&[5,6,7].includes(d.tag.profile)||d.tag.version===(d.tag.profile===12?22:d.tag.profile===10?17:14)&&[8,9,10,12].includes(d.tag.profile))&&d.tag.engineId==='lualatex')&&identity;
const navigationBibtex=[8,9,10,12].includes(d.tag.profile)&&d.tag.version===([10,12].includes(d.tag.profile)?15:12)&&d.tag.engineId==='latex-dvi'&&d.operation==='bibtex'&&identity;
const v4=p13Bibtex||mapped||dvi||unicodeBibtex||navigationBibtex||[4,5].includes(d.tag.version)&&d.tag.profile===2&&identity;
if((!legacy&&!modern&&!authoring&&!v4)||!['latex','bibtex'].includes(d.operation)||d.files.length>(suite?(pT(d.tag.profile)?8448:8384):v4?768:512)||d.outputs.length>(pT(d.tag.profile)?256:v4?128:64))throw new Error('FFI frame');
if([8,9,10,12,13,14,15,16,17,18].includes(d.tag.profile)&&(loaderSha256!=='b514d2b72aa44ec47923735f8f1be43a93351931beb183d57a7429b73e74a9a9'
||wasmSha256!=='70008f66d8acb88ee40e8c17b5a48dc81003852e569833444dd3cda7539ce235'
||cwrap('oc_synctex_abi','number',[])()!==65536))throw new Error('legacy identity');
if(v4)validateAuthoringV4Frame(d);
const maxTotal=suite?167772160:modern||authoring||v4?67108864:16777216,maxFile=suite?67108864:modern||authoring||v4?33554432:8388608;
let total=0;for(const f of d.files){total+=f.bytes.byteLength;if(total>maxTotal||f.bytes.byteLength>maxFile)throw new Error('VFS bound');}
if(suite&&(!Array.isArray(d.runtimeResourceNames)||d.runtimeResourceNames.length>8192
||!Array.isArray(d.generatedInputNames)||d.generatedInputNames.length>128))throw new Error('generated projection');
const resourceNames=suite?new Set(d.runtimeResourceNames):new Set(),prefix='__oop_runtime__/';
const generatedNames=suite?new Set(d.generatedInputNames):new Set();
if(suite&&(generatedNames.size!==d.generatedInputNames.length||generatedNames.has(d.selectedRoot)
||d.generatedInputNames.some(n=>typeof n!=='string'||resourceNames.has(n)||!d.files.some(f=>f.name===n))))throw new Error('generated projection');
if(suite&&(!Array.isArray(d.runtimeResourceNames)||resourceNames.size!==d.runtimeResourceNames.length
||resourceNames.size>8192||d.runtimeResourceNames.some(n=>!n.startsWith(prefix)||!d.files.some(f=>f.name===n))
||d.files.some(f=>!resourceNames.has(f.name)&&f.name.startsWith(prefix))))throw new Error('resources');
if(selectedPdfProfile(d.tag.profile)){
let sourceBytes=0,workBytes=0,sourceFiles=0,resourceBytes=0;
for(const f of d.files){
 if(resourceNames.has(f.name)){resourceBytes+=f.bytes.length;if(f.bytes.length>67108864)throw new Error('resource bounds');continue;}
 workBytes+=f.bytes.length;
 if(f.bytes.length>8388608||workBytes>33554432)throw new Error('work bounds');
 if(!generatedNames.has(f.name)&&(++sourceFiles>128||f.bytes.length>2097152||(sourceBytes+=f.bytes.length)>16777216))throw new Error('source bounds');
}
if(resourceBytes+workBytes>167772160)throw new Error('phase bounds');
}
const destination=f=>resourceNames.has(f.name)?'/tex/'+f.name.slice(prefix.length):'/work/'+f.name;
for(const f of d.files){const p=destination(f);const slash=p.lastIndexOf('/');FS.mkdirTree(p.slice(0,slash));FS.writeFile(p,f.bytes);}
if(suite)for(const f of d.files)if(resourceNames.has(f.name)){
const alias='/work/'+f.name.slice(prefix.length);
if(!FS.analyzePath(alias).exists){
let blocked=false;for(let n=alias.lastIndexOf('/');n>5;n=alias.lastIndexOf('/',n-1)){
const parent=alias.slice(0,n);if(FS.analyzePath(parent).exists&&!FS.isDir(FS.stat(parent).mode)){blocked=true;break;}
}
if(!blocked){FS.mkdirTree(alias.slice(0,alias.lastIndexOf('/')));FS.symlink(destination(f),alias);}
}
}
Object.defineProperty(self,'memlog',{configurable:true,get:()=>capturedLog,set:value=>{if(value.length>Math.floor(d.maxLogBytes/3)){limited=true;throw new Error('log bound');}capturedLog=value;}});
prepareExecutionContext();phase=d.operation;
for(const f of d.files){const b=FS.readFile(destination(f)),hash=new Uint8Array(await crypto.subtle.digest('SHA-256',b));inputs.push({name:f.name,length:b.length,sha256:[...hash].map(v=>v.toString(16).padStart(2,'0')).join('')});}
budget=await installNativeVfsBudget(FS,MEMFS,d.maxOutputBytes,d.maxLogBytes,v4?128:64,null,pU(d.tag.profile)?12288:8192);
if((mapped||dvi)&&d.operation==='latex'){
if(!Number.isSafeInteger(d.tag.ordinal)||d.tag.ordinal<1||d.tag.ordinal>4294967295
||cwrap('oc_synctex_abi','number',[])()!==65536
||cwrap('oc_mem_configure','number',['number','number','number'])(mapped?1:0,mapped?1048575:0,d.tag.ordinal)!==1)throw new Error('map configuration');
}
cwrap('setMainEntry','number',['string'])(d.mainEntry);emit({kind:'entered'});entered=true;const start=performance.now();
nativeReturn=d.operation==='latex'?_compileLaTeX():_compileBibtex();elapsedMs=performance.now()-start;
if(limited||budget.failed)throw new Error('latched quota');
if(!Number.isInteger(nativeReturn))throw new Error('native result');
let size=0;for(const name of d.outputs){const p='/work/'+name;if(!FS.analyzePath(p).exists)continue;const n=FS.stat(p).size;if(n>d.maxOutputBytes||n>2*d.maxOutputBytes-size){limited=true;throw new Error('output bound');}size+=n;files.push({name,bytes:FS.readFile(p)});}
let sourceMapPayload=null;
if(mapped&&d.operation==='latex'&&nativeReturn===0){
const n=cwrap('oc_synctex_length','number',['number'])(d.tag.ordinal);
if(!Number.isInteger(n)||n<0||n>1048575)throw new Error('map length');
if(cwrap('oc_synctex_status','number',[])()===1){
if(n<1)throw new Error('map length');
const p=cwrap('malloc','number',['number'])(n);if(!p)throw new Error('map allocation');
try{if(cwrap('oc_synctex_copy','number',['number','number','number'])(d.tag.ordinal,p,n)!==n)throw new Error('map copy');sourceMapPayload=HEAPU8.slice(p,p+n);}
finally{cwrap('free',null,['number'])(p);}
}else if(n!==0)throw new Error('unsealed native map');
}
emit({kind:'done',reply:{tag:d.tag,outcome:'native-return',nativeReturn,entered,log:capturedLog,files,inputs,elapsedMs,heapBytes:HEAPU8.byteLength,wasmSha256,loaderSha256,sourceMapPayload}});
}catch(e){
const outcome=limited||budget?.failed?'resource-limit':e instanceof WebAssembly.RuntimeError?'wasm-abort':entered?'protocol-failure':'setup-failure';
emit({kind:'done',reply:{tag:d.tag,outcome,nativeReturn,entered,log:capturedLog||'',files:[],inputs,elapsedMs,heapBytes:typeof HEAPU8==='undefined'?null:HEAPU8.byteLength,wasmSha256,loaderSha256}});
}
}
