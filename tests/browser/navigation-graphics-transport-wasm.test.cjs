// Genuine CLOSED P10 native modules through actual product workers in Node VM.
// Not a browser, managed finite-pipeline, PDF renderer/query or authority proof.
'use strict';
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const {webcrypto,createHash}=require('node:crypto');
const root=process.env.GRAPHICS_NAVIGATION_RUNTIME_DIR;
if(!root){console.log('SKIP actual graphics WASM: exact P10 GRAPHICS_NAVIGATION_RUNTIME_DIR required.');process.exit(0);}
const hash=b=>createHash('sha256').update(b).digest('hex'),text=s=>new TextEncoder().encode(s),prefix='__oop_runtime__/';
const pins=JSON.parse(fs.readFileSync(path.join(root,'pins.json'))),byName=new Map(pins.map(p=>[p.name,p]));
const closure=hash(Buffer.from([...pins].sort((a,b)=>a.name<b.name?-1:1).map(p=>`${p.name}\t${p.length}\t${p.sha256}`).join('\n')));
assert.equal(closure,'d91e3ff8d2c7717344d6c954efc8172ac0296223ece425df7ec00aff73db20cf');assert.equal(pins.length,3709);assert.equal(pins.reduce((s,p)=>s+p.length,0),192577312);
// Verify complete immutable stage, not just the six replaced executables.
const cache=new Map();for(const p of pins){const b=new Uint8Array(fs.readFileSync(path.join(root,'files',p.name)));assert.equal(b.length,p.length,p.name);assert.equal(hash(b),p.sha256,p.name);cache.set(p.name,b);}
const resource=name=>{assert(cache.has(name),name);return cache.get(name);};
const selectedRoot='тези/my thesis.tex',fixture=path.join(__dirname,'../CompilerSuiteFixtures');
const pathVariant=process.env.GRAPHICS_NAVIGATION_PATHS==='1';
const include=pathVariant?'розділи/part one.tex':'chapter.tex';
function project(engine){
 const main=fs.readFileSync(path.join(fixture,engine==='latex-dvi'?'main-latin.tex':'main-unicode.tex'));
 const capturedMain=pathVariant?text(main.toString().replace('\\include{chapter}','\\input{розділи/part one.tex}')):new Uint8Array(main);
 if(pathVariant)assert.notEqual(hash(capturedMain),hash(main),'Separate explicitly labelled path variant, never relabelled unchanged portable fixture');
 return [{name:selectedRoot,bytes:capturedMain},{name:include,bytes:new Uint8Array(fs.readFileSync(path.join(fixture,'chapter.tex')))},
 ...['references.bib','figure.xbb'].map(name=>({name,bytes:new Uint8Array(fs.readFileSync(path.join(fixture,name)))})),
 {name:'figure.png',bytes:new Uint8Array(fs.readFileSync(path.join(__dirname,'../../src/Client/wwwroot/icons/icon-192.png')))}];
}
async function run(worker,stem,frame){
 const messages=[];let resolveReady,rejectReady;
 const ready=new Promise((resolve,reject)=>{resolveReady=resolve;rejectReady=reject;});
 const c=vm.createContext({Uint8Array,Int8Array,Uint16Array,Int16Array,Uint32Array,Int32Array,Float32Array,Float64Array,ArrayBuffer,TextEncoder,TextDecoder,WebAssembly,crypto:webcrypto,performance,
  console:{log(){},warn(){},error(){}},setTimeout,clearTimeout,URL,Response,WorkerGlobalScope:function(){}});
 c.self=c;c.location={href:'https://native.invalid/worker.js'};
 c.postMessage=m=>{messages.push(m);if(m.kind==='ready')resolveReady();if(m.kind==='boot-error')rejectReady(Error(JSON.stringify(m)));};
 c.importScripts=()=>vm.runInContext(Buffer.from(resource(stem+'.js')).toString(),c);
 vm.runInContext(fs.readFileSync(path.join(__dirname,'../../src/Client/wwwroot/js',worker),'utf8'),c);
 const exports=WebAssembly.Module.exports(new WebAssembly.Module(resource(stem+'.wasm'))).map(e=>e.name);
 for(const e of stem==='luatex'?['oc_lua_graphics_abi','oc_luatex_abi','oc_mem_configure','oc_synctex_length','oc_synctex_copy']
  :stem.includes('converter')?['oc_dvi_graphics_abi','oc_dvi_abi_version','oc_dvi_geometry_configure','oc_dvi_geometry_copy']:[])assert(exports.includes(e),e);
 assert(!exports.some(e=>/seal|finish|geo_begin|geo_end|pdf_close/.test(e)));
 const timer=setTimeout(()=>rejectReady(Error('Actual native boot deadline')),15000);
 try{await Promise.all([c.onmessage({data:{operation:'boot',loaderUrl:'blob:verified-loader',loader:resource(stem+'.js'),wasm:resource(stem+'.wasm')}}),ready]);}finally{clearTimeout(timer);}
 await c.onmessage({data:frame});
 const reply=messages.findLast(m=>m.kind==='done')?.reply;assert(reply,JSON.stringify(messages));
 assert.equal(reply.outcome,'native-return',JSON.stringify({outcome:reply.outcome,log:reply.log}));assert.equal(reply.nativeReturn,0,reply.log);assert.deepEqual(reply.tag,frame.tag);
 assert.equal(reply.loaderSha256,byName.get(stem+'.js').sha256);assert.equal(reply.wasmSha256,byName.get(stem+'.wasm').sha256);
 for(const f of frame.files){const actual=reply.inputs.find(i=>i.name===f.name);assert.deepEqual(actual&&[actual.length,actual.sha256],[f.bytes.length,hash(f.bytes)]);}
 return reply;
}
function frame(engine,ordinal,tool,files,runtime,generated,outputs,entry){return{operation:tool,tag:{profile:10,version:{'latex-dvi':15,xelatex:16,lualatex:17}[engine],engineId:engine,tool,ordinal,job:'job',epoch:1,
 request:'request',capture:'capture',digest:'a'.repeat(64),nonce:'native-'+ordinal,runtimeIdentity:'b'.repeat(64)},selectedRoot,mainEntry:entry,files,runtimeResourceNames:runtime,generatedInputNames:generated,outputs,maxLogBytes:262144,maxOutputBytes:8388608};}
function runtime(engine){const fmt={'latex-dvi':'latex-dvi.fmt',xelatex:'xelatex.fmt',lualatex:'lualatex.fmt',pdftex:'pdflatex.fmt'}[engine];return pins.filter(p=>p.name===fmt||!/\.(wasm|js|fmt)$/.test(p.name)&&(engine==='xelatex'||!['icudt68l.dat','xetexfontlist.txt'].includes(p.name)))
 .map(p=>({name:prefix+(['latex-dvi.fmt','pdflatex.fmt'].includes(p.name)?'swiftlatexpdftex.fmt':p.name),bytes:resource(p.name)}));}
(async()=>{
 const results=[];
 for(const engine of ['latex-dvi','xelatex','lualatex']){
  const p=project(engine),r=runtime(engine),pdfResources=runtime('pdftex'),job=engine==='lualatex'?'result':'my thesis',ext=engine==='latex-dvi'?'.dvi':engine==='xelatex'?'.xdv':'.pdf';
  const outputs=['.pdf','.aux','.bbl','.blg','.log','.toc','.lof','.lot','.out',...(ext==='.pdf'?[]:[ext])].map(e=>job+e).concat(job.replaceAll(' ','_')+'-blx.bib',include.replace(/\.tex$/,'.aux'));
  const state=files=>files.filter(f=>/\.(aux|bbl|toc|lof|lot|out)$/.test(f.name)||f.name===job.replaceAll(' ','_')+'-blx.bib');
  const producerWorker=engine==='latex-dvi'?'compile-port-worker.js':'unicode-tex-port-worker.js',producerStem={'latex-dvi':'dvi-map-engine',xelatex:'xetex',lualatex:'luatex'}[engine];
  let carry=[],producer,firstAux,finalOrdinal=0;const phases=[],backend=[];
  for(const ordinal of [1,3,5,6]){
   const request=frame(engine,ordinal,'latex',[...r,...p,...carry],r.map(f=>f.name),carry.map(f=>f.name),outputs,engine==='latex-dvi'?'"'+selectedRoot+'"':selectedRoot);
   producer=await run(producerWorker,producerStem,request);phases.push({tool:'latex',ordinal});carry=state(producer.files);finalOrdinal=ordinal;
   const aux=carry.find(f=>f.name===job+'.aux');assert(aux?.bytes.length);if(ordinal===1)firstAux=hash(aux.bytes);
   if(ordinal===3)assert.notEqual(hash(aux.bytes),firstAux,'Native bibcite AUX churn must exercise backend rerun, not assume a stable first control');
   if(ordinal===1||ordinal===3){
    const b=await run('compile-port-worker.js','engine',frame(engine,ordinal+1,'bibtex',[...pdfResources,...p,...carry],pdfResources.map(f=>f.name),carry.map(f=>f.name),outputs,engine==='lualatex'?'result.tex':'my thesis.tex'));
    assert.equal(b.sourceMapPayload,null);assert.equal(b.sourceMapGeometryPayload,undefined);const bbl=b.files.find(f=>f.name===job+'.bbl');assert(bbl?.bytes.length);
    carry=[...carry.filter(f=>f.name!==job+'.bbl'),bbl];phases.push({tool:'bibtex',ordinal:ordinal+1});backend.push({ordinal:ordinal+1,bbl:hash(bbl.bytes)});
   }
   global.gc?.();
  }
  const map=producer.sourceMapPayload;assert(map?.length>0&&map.length<=1048575);
  const mapNames=[...new TextDecoder().decode(map).matchAll(/^Input:\d+:(.*)$/gm)].map(m=>m[1].replace(/\r$/,''));
  for(const name of [selectedRoot,include])assert(mapNames.includes(name)||mapNames.includes('./'+name),JSON.stringify({engine,name,mapNames}));
  let pdf,sidecar,intermediate=null;
  if(engine==='lualatex'){pdf=producer.files.find(f=>f.name===job+'.pdf').bytes;sidecar=producer.sourceMapGeometryPayload;}
  else{
   assert(!producer.sourceMapGeometryPayload,'Producer has no later converter geometry');intermediate=producer.files.find(f=>f.name===job+ext).bytes;
   assert.equal(intermediate[0],247);assert.equal(intermediate[1],engine==='xelatex'?7:2);
   const cr=pins.filter(p=>['glyphlist.txt','pdfglyphlist.txt'].includes(p.name)||/\.(tfm|pfb|ttf|otf|vf|map|enc)$/.test(p.name)).map(p=>({name:prefix+p.name,bytes:resource(p.name)}));
   const converted=await run('dvi-port-worker.js',engine==='xelatex'?'xdv-map-converter':'dvi-map-converter',frame(engine,7,'dvipdfmx',[...cr,...p,{name:job+ext,bytes:intermediate}],cr.map(f=>f.name),[],[job+'.pdf'],job+ext));
   assert.equal(converted.sourceMapPayload,null);pdf=converted.files[0].bytes;sidecar=converted.sourceMapGeometryPayload;phases.push({tool:'dvipdfmx',ordinal:7});
  }
  assert.equal(Buffer.from(pdf.slice(0,5)).toString(),'%PDF-');assert(sidecar?.length>0&&sidecar.length<=131072);
  const geometry=JSON.parse(new TextDecoder().decode(sidecar));assert.equal(geometry.schemaVersion,1);assert(geometry.pages.length>0&&geometry.pages.length<=512);
  for(const page of geometry.pages){assert.equal(page.status,'available');assert.equal(page.writerToPdfRaw.length,6);assert(page.writerToPdfRaw.every(Number.isFinite));assert.equal(page.writerPageBoxBp.length,4);}
  const result={engine,kind:pathVariant?'explicit full-raw Ukrainian/spaced root+include variant':'UNCHANGED portable PNG/include/BibTeX source bytes',files:p.map(f=>({name:f.name,length:f.bytes.length,sha256:hash(f.bytes)})),phases,backend,finalLaTeX:finalOrdinal,
   producer:byName.get(producerStem+'.wasm'),pdf:hash(pdf),map:hash(map),sidecar:hash(sidecar),intermediate:intermediate&&hash(intermediate),pages:geometry.pages.length,openedCapturedNames:mapNames.filter(n=>p.some(f=>n===f.name||n==='./'+f.name))};results.push(result);
  if(process.env.GRAPHICS_NAVIGATION_RESULTS_DIR){const out=path.join(process.env.GRAPHICS_NAVIGATION_RESULTS_DIR,engine);fs.mkdirSync(out,{recursive:true});for(const [n,b]of [['output.pdf',pdf],['map.synctex',map],['geometry.json',sidecar],...(intermediate?[['output'+ext,intermediate]]:[])])fs.writeFileSync(path.join(out,n),b);}
  console.log(JSON.stringify(result));global.gc?.();
 }
 console.log(JSON.stringify({kind:'actual-P10-product-worker-WASM-NOT-browser-managed-query-or-renderer-acceptance',closure,results},null,2));
})().catch(e=>{console.error(e);process.exitCode=1;});
