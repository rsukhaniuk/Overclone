'use strict';
// Genuine candidate bytes through the unmodified product worker, in Node VM.
// This is not browser, managed-pipeline, release or navigation-authority proof.
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm'),assert=require('node:assert/strict');
const {webcrypto,createHash}=require('node:crypto');
const runtime=process.env.COMPILER_BIBER_SEMANTICS_RUNTIME_DIR;
if(!runtime){console.log('SKIP P13 actual semantic worker WASM: explicit private COMPILER_BIBER_SEMANTICS_RUNTIME_DIR required.');process.exit(0);}
const hash=b=>createHash('sha256').update(b).digest('hex'),bytes=s=>new TextEncoder().encode(s),prefix='__oop_runtime__/';
const pins=JSON.parse(fs.readFileSync(path.join(runtime,'pins.json'))),byName=new Map(pins.map(p=>[p.name,p]));
// Closed private runtime02, including the reviewed notice/Arabic-resource stage.
// This native fixture binding is not application/default adoption authority.
const closure=hash(Buffer.from([...pins].sort((a,b)=>a.name<b.name?-1:1).map(p=>`${p.name}\t${p.length}\t${p.sha256}`).join('\n')));
assert.equal(closure,'34540765dfd1b5d46666841378221cce0b40204014c4a8c2dc25ddddbe56d1ed');
assert.equal(pins.length,4357);assert.equal(pins.reduce((n,p)=>n+p.length,0),259559615);
const cache=new Map();for(const p of pins){const b=new Uint8Array(fs.readFileSync(path.join(runtime,'files',p.name)));assert.equal(b.length,p.length);assert.equal(hash(b),p.sha256,p.name);cache.set(p.name,b);}
const get=n=>{assert.ok(cache.has(n),n);return cache.get(n);};
for(const [n,h]of [['semantic-engine.js','735c17494d6ec61c1b2f000f437f343ece9af3258f2f354f11745b7b5836dde2'],['semantic-engine.wasm','e3136893e75d5b6b3a7b4297349c28f51423dee2c014a607d9c1cd20f75357c3'],
 ['engine.js','b514d2b72aa44ec47923735f8f1be43a93351931beb183d57a7429b73e74a9a9'],['engine.wasm','70008f66d8acb88ee40e8c17b5a48dc81003852e569833444dd3cda7539ce235']])assert.equal(hash(get(n)),h,n);
const worker=fs.readFileSync(path.join(__dirname,'../../src/Client/wwwroot/js/compile-port-worker.js'),'utf8');assert.ok(Buffer.byteLength(worker)<=32768);
const root='тези/розділ з пробілом.tex',include='розділи/частина з пробілом.tex',job='розділ з пробілом';
function project(backend='bibtex',reflected=false){
 const preamble='\\documentclass[10pt]{article}\n\\usepackage[utf8]{inputenc}\n\\usepackage[T2A]{fontenc}\n';
 const bibliography=backend==='biber'?'\\usepackage[backend=biber,style=numeric]{biblatex}\n\\addbibresource{references.bib}\n':'';
 const main=preamble+bibliography+(reflected?'\\TeXXeTstate=1\n':'')+'\\begin{document}\n\\section{Original source}\n'+
  '\\noindent Original Іі; Latin I i; mixed fі ffі fіlm.\\par\n'+(reflected?'\\noindent\\beginR fі ffі fіlm\\endR.\\par\n':'')+
  '\\input{'+include.slice(0,-4)+'}\nIncluded reference~\\ref{part}. Citation~\\cite{book}.\n'+
  (backend==='biber'?'\\printbibliography\n':'\\bibliographystyle{plain}\n\\bibliography{references}\n')+'\\end{document}\n';
 return[{name:root,bytes:bytes(main)},{name:include,bytes:bytes('\\noindent Included Іі fі ffі fіlm.\\label{part}\\par\n')},
  {name:'references.bib',bytes:bytes('@book{book,author={Zimmer, Zoe},title={{Portable Compiler Reference}},publisher={Offline Fixture Press},year={2025}}\n')}];
}
function resources(engine){const fmt=engine==='pdftex'?'pdflatex.fmt':'latex-dvi.fmt';
 return pins.filter(p=>p.name===fmt||!(/\.(wasm|js|fmt)$/.test(p.name))&&!['biber.data','icudt68l.dat','xetexfontlist.txt'].includes(p.name))
  .map(p=>({name:prefix+(p.name===fmt?'swiftlatexpdftex.fmt':p.name),bytes:get(p.name)}));}
function frame(engine,ordinal,tool,backend,projectFiles,carry=[]){
 const r=resources(tool==='bibtex'?'pdftex':engine),control=backend==='biber'&&tool==='latex';
 const outputs=['.pdf','.aux','.bbl','.blg','.log','.toc','.lof','.lot','.out',...(engine==='latex-dvi'?['.dvi']:[]),...(control?['.bcf','.run.xml']:[])].map(e=>job+e);
 if(!control)outputs.push(job.replaceAll(' ','_')+'-blx.bib');
 return{operation:tool,bibliographyTool:backend,tag:{profile:13,version:engine==='pdftex'?23:24,engineId:engine,tool,ordinal,runtimeIdentity:'b'.repeat(64),
  job:'job',epoch:1,request:'request',capture:'capture',digest:'a'.repeat(64),nonce:'semantic-'+engine+'-'+ordinal},selectedRoot:root,
  mainEntry:tool==='bibtex'?job+'.tex':'"'+root+'"',files:[...r,...projectFiles,...carry],runtimeResourceNames:r.map(f=>f.name),generatedInputNames:carry.map(f=>f.name),outputs,maxLogBytes:262144,maxOutputBytes:8388608};
}
async function run(stem,request,expected='native-return'){
 const messages=[];let resolveReady,rejectReady;const ready=new Promise((a,b)=>{resolveReady=a;rejectReady=b;});
 const c=vm.createContext({Uint8Array,Int8Array,Uint16Array,Int16Array,Uint32Array,Int32Array,Float32Array,Float64Array,ArrayBuffer,TextEncoder,TextDecoder,WebAssembly,
  crypto:webcrypto,performance,setTimeout,clearTimeout,URL,Response,WorkerGlobalScope:function(){},console:{log(){},error(){},warn(){}}});
 c.self=c;c.location={href:'https://semantic.invalid/worker.js'};c.postMessage=m=>{messages.push(m);if(m.kind==='ready')resolveReady();if(m.kind==='boot-error')rejectReady(Error(JSON.stringify(m)));};
 c.importScripts=()=>vm.runInContext(Buffer.from(get(stem+'.js')).toString(),c);
 vm.runInContext(worker,c);
 const timer=setTimeout(()=>rejectReady(Error('actual native worker boot deadline')),15000);
 try{await Promise.all([c.onmessage({data:{operation:'boot',loaderUrl:'blob:verified-component',loader:get(stem+'.js'),wasm:get(stem+'.wasm')}}),ready]);}finally{clearTimeout(timer);}
 await c.onmessage({data:request});
 const reply=messages.findLast(m=>m.kind==='done')?.reply;assert.ok(reply,JSON.stringify(messages));assert.equal(reply.outcome,expected,JSON.stringify({outcome:reply.outcome,entered:reply.entered,log:reply.log}));
 assert.equal(reply.nativeReturn,0,reply.log);assert.equal(reply.entered,true);assert.deepEqual(reply.tag,request.tag);
 assert.equal(reply.loaderSha256,byName.get(stem+'.js').sha256);assert.equal(reply.wasmSha256,byName.get(stem+'.wasm').sha256);
 assert.equal(reply.inputs.length,request.files.length);
 for(const f of request.files){const fact=reply.inputs.find(p=>p.name===f.name);assert.deepEqual(fact&&[fact.length,fact.sha256],[f.bytes.length,hash(f.bytes)]);}
 if(expected==='native-return'&&request.operation==='latex'){
  assert.ok(reply.sourceMapPayload?.length>0);assert.ok(reply.sourceMapPayload.length<=1048575);
  const names=[...new TextDecoder().decode(reply.sourceMapPayload).matchAll(/^Input:\d+:(.*)$/gm)].map(m=>m[1].trim());
  for(const n of [root,include])assert.ok(names.includes(n)||names.includes('./'+n),JSON.stringify(names));
 }else assert.equal(reply.sourceMapPayload,null);
 if(expected!=='native-return')assert.equal(reply.files.length,0);
 return reply;
}
async function convert(intermediate){
 const stem='dvi-map-converter';const m=await require(path.join(runtime,'files',stem+'.js'))({
  instantiateWasm(imports,done){const i=new WebAssembly.Instance(new WebAssembly.Module(get(stem+'.wasm')),imports);done(i);return i.exports;},print(){},printErr(){}});
 assert.equal(m._oc_dvi_font_semantics_abi(),1);assert.equal(m._oc_dvi_graphics_abi(),1);
 const alloc=b=>{const p=m._malloc(Math.max(b.length,1));assert.ok(p);m.HEAPU8.set(b,p);return p;};
 const add=(n,b,kind)=>{const p=alloc(bytes(n+'\0')),q=alloc(b),r=m._oc_dvi_add_file(p,q,b.length,kind);m._free(p);m._free(q);assert.equal(r,0,n);};
 for(const p of pins.filter(p=>['glyphlist.txt','pdfglyphlist.txt'].includes(p.name)||/\.(tfm|pfb|ttf|otf|vf|map|enc)$/.test(p.name)))add(p.name,get(p.name),1);
 add(job+'.dvi',intermediate,0);assert.equal(m._oc_dvi_geometry_configure(1,9),1);
 const p=alloc(bytes(job+'.dvi\0'));assert.equal(m._oc_dvi_run(p),0);m._free(p);assert.equal(m._oc_dvi_font_semantics_status(),1);
 const geometry=JSON.parse(Buffer.from(m.HEAPU8.slice(m._oc_dvi_geometry_data(),m._oc_dvi_geometry_data()+m._oc_dvi_geometry_size())));
 assert.ok(geometry.pages.every(p=>p.status==='available'));
 return Buffer.from(m.HEAPU8.slice(m._oc_dvi_pdf_data(),m._oc_dvi_pdf_data()+m._oc_dvi_pdf_size()));
}
async function extract(pdf){
 const dir=process.env.COMPILER_BIBER_SEMANTICS_PDF_RUNTIME_DIR;assert.ok(dir,'explicit exact E1 runtime is required for this fixture-backed extraction proof');
 const loader=fs.readFileSync(path.join(dir,'e1.js')),wasm=fs.readFileSync(path.join(dir,'e1.wasm'));
 assert.equal(hash(loader),'040bdfac50656b3993f622614b73feaaba31d8253d077ff50f925f316c2f5f6b');assert.equal(hash(wasm),'1637c1a7798cab443334e559045d88f33864d89e0ff02eeb57b3da4f08f37c20');
 const m=await require(path.join(dir,'e1.js'))({wasmBinary:wasm,print(){},printErr(){}});
 const check=(n,...a)=>assert.equal(m['_'+n](...a),0,n),input=b=>{const h=m._e1_alloc(b.length);assert.ok(h);m.HEAPU8.set(b,m._e1_data(h));return h;};
 const raw=Buffer.alloc(36);[1,36,8388608,32,100000,256,4000000,8388608,64].forEach((v,i)=>raw.writeUInt32LE(v,i*4));
 const d=input(pdf),b=input(raw);check('e1_open',d,b);const doc=m._e1_result();check('e1_release',d);check('e1_release',b);
 const take=()=>{const h=m._e1_result(),n=m._e1_size(h),v=input(Buffer.alloc(n));check('e1_copy',h,v);const data=Buffer.from(m.HEAPU8.slice(m._e1_data(v),m._e1_data(v)+n));check('e1_release',h);check('e1_release',v);return JSON.parse(data);};
 let text='';for(let i=0;i<m._e1_document_pages(doc);i++){check('e1_page',doc,i);const page=m._e1_result();check('e1_text',page);text+=take().text+'\n';check('e1_close',page);}
 check('e1_close',doc);assert.equal(m._e1_live(),0);return text;
}
(async()=>{
 const results=[];let invocations=0;
 for(const engine of ['pdftex','latex-dvi']){
  const p=project(),phases=[];let carry=[],reply;
  for(const [ordinal,tool]of [[1,'latex'],[2,'bibtex'],[3,'latex'],[4,'latex']]){
   const request=frame(engine,ordinal,tool,'bibtex',p,carry);reply=await run(tool==='bibtex'?'engine':'semantic-engine',request);invocations++;
   phases.push({ordinal,tool,component:tool==='bibtex'?'accepted P10 engine':'semantic dual03',nativeReturn:reply.nativeReturn});
   const state=reply.files.filter(f=>/\.(aux|bbl|toc|lof|lot|out)$/.test(f.name)||f.name===job.replaceAll(' ','_')+'-blx.bib');
   carry=tool==='bibtex'?[...carry.filter(f=>f.name!==job+'.bbl'),...state.filter(f=>f.name===job+'.bbl')]:state;
   if(tool==='bibtex')assert.ok(carry.find(f=>f.name===job+'.bbl')?.bytes.length);
   global.gc?.();
  }
  assert.ok(!/undefined references|Citation .* undefined|Label\(s\) may have changed/.test(reply.log),reply.log);
  const output=reply.files.find(f=>f.name===job+(engine==='pdftex'?'.pdf':'.dvi'));assert.ok(output?.bytes.length);
  const pdf=engine==='pdftex'?Buffer.from(output.bytes):await convert(output.bytes),text=await extract(pdf);
  assert.ok(text.includes('Original Іі; Latin I i; mixed fі ffі fіlm.'),text);assert.ok(text.includes('Included Іі fі ffі fіlm.'),text);assert.match(text,/Portable\s+Compiler\s+Reference/);
  const biber=await run('semantic-engine',frame(engine,5,'latex','biber',project('biber')));invocations++;
  for(const ext of ['.bcf','.run.xml'])assert.ok(biber.files.find(f=>f.name===job+ext)?.bytes.length,ext);
  const unsupported=await run('semantic-engine',frame(engine,6,'latex','bibtex',project('bibtex',true)),'font-semantics-unsupported');invocations++;
  assert.equal(unsupported.nativeReturn,0);assert.ok(unsupported.inputs.length);
  results.push({engine,phases,sourceFiles:p.map(f=>({name:f.name,length:f.bytes.length,sha256:hash(f.bytes)})),pdf:hash(pdf),map:hash(reply.sourceMapPayload),text,
   currentBiberControl:biber.files.filter(f=>/\.(bcf|run.xml)$/.test(f.name)).map(f=>({name:f.name,length:f.bytes.length,sha256:hash(f.bytes)})),unsupported:{outcome:unsupported.outcome,nativeReturn:unsupported.nativeReturn,files:unsupported.files.length}});
  console.log(engine+' actual semantic worker + old BibTeX convergence + Biber control + reflected refusal PASS');global.gc?.();
 }
 const receipt={kind:'actual-P13-worker-WASM-not-browser-or-managed-pipeline',closure,invocations,skips:0,worker:{length:Buffer.byteLength(worker),sha256:hash(Buffer.from(worker))},results};
 if(process.env.COMPILER_BIBER_SEMANTICS_RESULTS){fs.mkdirSync(process.env.COMPILER_BIBER_SEMANTICS_RESULTS,{recursive:true});fs.writeFileSync(path.join(process.env.COMPILER_BIBER_SEMANTICS_RESULTS,'receipt.json'),JSON.stringify(receipt,null,2)+'\n');}
 console.log(JSON.stringify(receipt,null,2));
})().catch(e=>{console.error(e);process.exitCode=1;});
