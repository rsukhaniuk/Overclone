'use strict';
// Actual worker, MODELED module/hash/VFS APIs. The separate genuine-WASM test
// exercises real loader/format/native/budget behavior; these are finite FFI cases.
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm'),assert=require('node:assert/strict');
const {webcrypto,createHash}=require('node:crypto');
const source=fs.readFileSync(path.join(__dirname,'../../src/Client/wwwroot/js/compile-port-worker.js'),'utf8');
assert.ok(Buffer.byteLength(source)<=32768,'unchanged hard worker source ceiling');
const hash=b=>createHash('sha256').update(b).digest('hex'),bytes=s=>new TextEncoder().encode(s);
const semantic=['735c17494d6ec61c1b2f000f437f343ece9af3258f2f354f11745b7b5836dde2','e3136893e75d5b6b3a7b4297349c28f51423dee2c014a607d9c1cd20f75357c3'];
const oldDvi=['980bcf858794dc4679124df6fbf76a503d3020d96a7081537e9d1a4a5e7bb6f1','6ad6db6625d28bdfaf83eba48b44a4202582fd84118c1e8ece3857535722a1b9'];
const legacy=['b514d2b72aa44ec47923735f8f1be43a93351931beb183d57a7429b73e74a9a9','70008f66d8acb88ee40e8c17b5a48dc81003852e569833444dd3cda7539ce235'];
const pdfFmt=new Uint8Array(22404123),dviFmt=new Uint8Array(22404979),loader=new Uint8Array(141918),wasm=new Uint8Array(1952801);
const prefix='__oop_runtime__/',root='тези/розділ з пробілом.tex',job='розділ з пробілом';
function frame(extra={}){
 const phase=extra.version??23,engine=extra.engine??(phase===24?'latex-dvi':'pdftex'),tool=extra.operation??'latex',backend=extra.backend??(tool==='bibtex'?'bibtex':'none');
 const selectedJob=phase===22?'result':job;
 const value={operation:tool,tag:{profile:extra.profile??13,version:phase,engineId:engine,tool,ordinal:3,runtimeIdentity:'a'.repeat(64)},bibliographyTool:backend,
  selectedRoot:root,mainEntry:tool==='bibtex'?selectedJob+'.tex':'"'+root+'"',files:[{name:prefix+'swiftlatexpdftex.fmt',bytes:phase===24?dviFmt:pdfFmt},{name:root,bytes:bytes('unchanged original Іі fі ffі fіlm')}],
  outputs:['.pdf','.aux','.bbl','.blg','.log','.toc','.lof','.lot','.out',...(phase===24||phase===15?['.dvi']:[]),...(phase===21?['.xdv']:[])].map(e=>selectedJob+e),
  runtimeResourceNames:[prefix+'swiftlatexpdftex.fmt'],generatedInputNames:[],maxLogBytes:262144,maxOutputBytes:8388608};
 if(backend==='biber'&&tool==='latex')value.outputs.push(selectedJob+'.bcf',selectedJob+'.run.xml');else value.outputs.push(selectedJob.replaceAll(' ','_')+'-blx.bib');
 if(tool==='bibtex'){value.files.push({name:selectedJob+'.aux',bytes:bytes('actual controlled AUX')});value.generatedInputNames.push(selectedJob+'.aux');}
 if(![13,14,15,16].includes(value.tag.profile))delete value.bibliographyTool;
 return value;
}
async function attempt(options={},mutate=()=>{}){
 const f=frame(options);mutate(f);const calls=[],messages=[],store=new Map();let writes=0,entry;
 const heap=new Uint8Array(8192),map=bytes('actual-modeled-map');
 const F={mkdir(){writes++;},mkdirTree(){writes++;},chdir(){},writeFile(n,b){writes++;store.set(n,b);},readFile(n){assert.ok(store.has(n),n);return store.get(n).slice();},
  analyzePath:n=>({exists:store.has(n)}),stat:n=>({mode:32768,size:store.get(n).length}),isFile:m=>m===32768,isDir:()=>false,symlink(n,a){writes++;store.set(a,store.get(n));},getStreamChecked:()=>null};
 const output=()=>{for(const n of f.outputs)if(/\.(pdf|dvi|bbl)$/.test(n))store.set('/work/'+n,n.endsWith('.dvi')?new Uint8Array([247,2,0]):bytes(n.endsWith('.pdf')?'%PDF-model':'genuine-modeled-bbl'));};
 const m={FS:F,MEMFS:{},HEAPU8:heap,_oc_dvi_maps_abi:()=>options.maps??1,_oc_synctex_abi:()=>options.synctex??65536,
  _oc_font_semantics_abi:()=>options.semantic??1,_oc_font_semantics_status:()=>options.status??1,
  _oc_mem_configure:(...a)=>{calls.push(['configure',...a]);return 1;},ccall:(n,t,ts,a)=>{assert.equal(n,'setMainEntry');entry=a[0];calls.push(['entry',entry]);return 0;},
  _compileLaTeX(){calls.push(['latex']);output();return options.nativeReturn??0;},_oc_synctex_status:()=>options.sealed??1,
  _oc_synctex_length:n=>{calls.push(['map-length',n]);return map.length;},_oc_synctex_copy:(n,p,c)=>{calls.push(['map-copy',n]);heap.set(map,p);return map.length;},_malloc:()=>1024,_free(){}};
 for(const name of options.missing??[])delete m[name];
 const kind=options.module??'semantic',pair=options.hashes??(kind==='legacy'?legacy:kind==='dvi'?oldDvi:semantic);
 const crypt={subtle:{async digest(algorithm,b){let h=b===loader?pair[0]:b===wasm||options.badWasmLength&&b.byteLength===wasm.length-1?pair[1]:b===pdfFmt?'69e67945852ac84675d043b3f7a5ef02adce773e64129427edb8ce0ebdd84deb':b===dviFmt?'729d02f1e2cd5b63886cf03520634b8fc319fdabe3ff61dd224134b8baf40605':null;
  return h?Uint8Array.from(Buffer.from(h,'hex')).buffer:webcrypto.subtle.digest(algorithm,b);}}};
 const c=vm.createContext({Uint8Array,Int8Array,Uint32Array,ArrayBuffer,TextEncoder,TextDecoder,WebAssembly,crypto:crypt,performance,Response,
  console:{log(){},error(){}},FS:F,MEMFS:{},PATH:{join2:(a,b)=>a+'/'+b},HEAPU8:heap,prepareExecutionContext(){},
  cwrap:n=>n==='setMainEntry'?v=>{entry=v;calls.push(['entry',v]);return 0;}:n==='oc_synctex_abi'?()=>65536:()=>0,
  _compileBibtex(){calls.push(['bibtex']);output();return 0;},_compileLaTeX(){throw Error('legacy TeX forbidden in P13');}});
 c.self=c;c.postMessage=v=>messages.push(v);c.memlog='';
 c.importScripts=()=>{if(!options.missingFactory&&kind!=='legacy')c[kind==='semantic'?'createFontSemantics':'createDviMaps']=()=>Promise.resolve(m);else if(kind==='legacy')c.postMessage({result:'ok'});};
 vm.runInContext(source,c);
 // Explicit MODEL seam: budget enforcement is covered through real MEMFS in
 // compiler-biber-semantics-wasm.test.cjs and existing allocator regressions.
 vm.runInContext('installNativeVfsBudget=async()=>({failed:false});',c);
 await c.onmessage({data:{operation:'boot',loaderUrl:'blob:modeled',loader,wasm:options.badWasmLength?wasm.slice(1):wasm}});
 if(messages.some(m=>m.kind==='boot-error'))return{calls,messages,writes};
 await c.onmessage({data:f});if(options.replay)await c.onmessage({data:f});
 return{calls,messages,writes,frame:f,entry};
}
(async()=>{
 let count=0;
 for(const profile of [13,14,15,16])for(const version of [23,24])for(const backend of ['none','bibtex','biber']){
  const good=await attempt({profile,version,backend,replay:true});assert.equal(good.messages.at(-1).reply.outcome,'native-return');
  assert.equal(good.calls.filter(c=>c[0]==='latex').length,1);assert.equal(good.entry,'"'+root+'"');
  assert.ok(good.messages.at(-1).reply.sourceMapPayload);assert.ok(good.calls.some(c=>c[0]==='map-copy'&&c[1]===3));count++;
 }
 for(const profile of [13,14,15,16])for(const countFiles of (profile>=14?[127,128,129]:[63,64,65])) {
  const r=await attempt({profile},f=>{for(let i=1;i<countFiles;i++)f.files.push({name:'asset-'+i+'.txt',bytes:bytes('a')});});
  assert.equal(r.calls.some(c=>c[0]==='latex'),countFiles<=(profile>=14?128:64),'captured count'+countFiles+' profile'+profile);count++;
 }
 for(const options of [{maps:0},{synctex:0},{semantic:0},{semantic:2},{missing:['_oc_font_semantics_abi']},{missing:['_oc_font_semantics_status']},
  {missingFactory:true},{badWasmLength:true},{hashes:[semantic[0],'0'.repeat(64)]},{hashes:['0'.repeat(64),semantic[1]]}]){
  const r=await attempt(options);assert.ok(r.messages.some(m=>m.kind==='boot-error'));assert.equal(r.writes,0);assert.equal(r.calls.length,0);count++;
 }
 for(const extra of [{profile:12},{profile:11},{profile:17},{version:6},{version:15},{version:10},{version:21,engine:'xelatex'},
  {version:22,engine:'lualatex'},{version:23,engine:'latex-dvi'},{version:24,engine:'pdftex'},{operation:'bibtex'},
  {module:'dvi'},{module:'legacy'},{module:'legacy',operation:'bibtex',backend:'biber'}]){
  const r=await attempt(extra);assert.ok(!r.calls.some(c=>c[0]==='latex'||c[0]==='bibtex'));assert.equal(r.writes,0);count++;
 }
 const mutations=[f=>delete f.bibliographyTool,f=>f.bibliographyTool='Biber',f=>f.bibliographyTool=null,
  f=>f.outputs.push(job+'.bcf'),f=>f.outputs.push('other.run.xml'),f=>f.files.push({name:root.toUpperCase(),bytes:bytes('collision')}),
  f=>f.selectedRoot='../escape.tex',f=>f.selectedRoot='bad\ud800.tex',f=>f.files[0].bytes=new Uint8Array(1),f=>f.files[0].bytes=pdfFmt.slice(),
  f=>f.runtimeResourceNames=[],f=>f.tag.ordinal=0,f=>f.tag.runtimeIdentity='bad'];
 for(const mutate of mutations){const r=await attempt({},mutate);assert.equal(r.writes,0);assert.ok(!r.calls.some(c=>c[0]==='latex'));count++;}
 for(const mutate of [f=>f.outputs.pop(),f=>f.outputs.push(job.replaceAll(' ','_')+'-blx.bib'),
  f=>{f.generatedInputNames.push(job+'.bcf');f.files.push({name:job+'.bcf',bytes:bytes('stale BCF')});},
  f=>{f.generatedInputNames.push(job+'.run.xml');f.files.push({name:job+'.run.xml',bytes:bytes('stale XML')});}]){
  const r=await attempt({backend:'biber'},mutate);assert.equal(r.writes,0);assert.ok(!r.calls.some(c=>c[0]==='latex'));count++;
 }
 for(const status of [2,0,3]){
  const r=await attempt({status}),reply=r.messages.at(-1).reply;
  assert.equal(reply.outcome,status===2?'font-semantics-unsupported':'protocol-failure');assert.equal(reply.nativeReturn,0);assert.equal(reply.entered,true);
  assert.equal(reply.files.length,0);assert.equal(reply.sourceMapPayload,null);assert.equal(reply.inputs.length,r.frame.files.length);count++;
 }
 for(const sealed of [0,2]){const r=await attempt({sealed});assert.equal(r.messages.at(-1).reply.sourceMapPayload,null);count++;}
 for(const [version,engine]of [[23,'pdftex'],[24,'latex-dvi'],[21,'xelatex'],[22,'lualatex']]){
  const r=await attempt({module:'legacy',operation:'bibtex',version,engine,replay:true});assert.equal(r.calls.filter(c=>c[0]==='bibtex').length,1);assert.equal(r.messages.at(-1).reply.outcome,'native-return');count++;
 }
 const old=await attempt({module:'dvi',profile:12,version:15,engine:'latex-dvi'});assert.equal(old.messages.at(-1).reply.outcome,'native-return');count++;
 console.log(`P13 semantic worker FFI modeled PASS: ${count} cases; no native/browser acceptance claim.`);
})().catch(e=>{console.error(e);process.exitCode=1;});
