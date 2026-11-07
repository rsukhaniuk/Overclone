'use strict';
// Genuine pinned P13 Unicode/semantic/converter components through the actual
// unchanged product workers in Node VM. No browser/managed-authority claim.
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const {webcrypto,createHash}=require('node:crypto');
const runtime=process.env.COMPILER_BIBER_UNICODE_RUNTIME_DIR;
if(!runtime){console.log('SKIP P13 actual Unicode/converter WASM: exact COMPILER_BIBER_UNICODE_RUNTIME_DIR required.');process.exit(0);}
const hash=b=>createHash('sha256').update(b).digest('hex'),text=s=>new TextEncoder().encode(s),prefix='__oop_runtime__/';
const pins=JSON.parse(fs.readFileSync(path.join(runtime,'pins.json'))),inventory=new Map(pins.map(p=>[p.name,p]));
const closure=hash(Buffer.from([...pins].sort((a,b)=>a.name<b.name?-1:1).map(p=>`${p.name}\t${p.length}\t${p.sha256}`).join('\n')));
assert.equal(closure,'34540765dfd1b5d46666841378221cce0b40204014c4a8c2dc25ddddbe56d1ed');assert.equal(pins.length,4357);assert.equal(pins.reduce((n,p)=>n+p.length,0),259559615);
const cache=new Map();for(const p of pins){const b=new Uint8Array(fs.readFileSync(path.join(runtime,'files',p.name)));assert.equal(b.length,p.length);assert.equal(hash(b),p.sha256,p.name);cache.set(p.name,b);}
const get=n=>{assert.ok(cache.has(n),n);return cache.get(n);};
for(const [n,h]of [['luatex.js','3ebd6f09571509648e6ce88de8f1229f4709514f5fbc9a1b1029b8c59a3cba4d'],['luatex.wasm','2eefd3abfec7292f0a912b364693e830bc47792d5c6c56029246ebc3505e2a16'],
 ['xetex.wasm','5a08c4d0a2109adc43e087889be208deb71cf4ba55692b41d58da2c8284f1e8e'],['dvi-map-converter.wasm','4f4e13b462772c15184bf4279b533d598d446fa7a44c0b2bafda276baf91701f']])assert.equal(hash(get(n)),h,n);
const include='розділи/частина з пробілом.tex',roots={xelatex:'тези/розділ 😀.tex',lualatex:'тези/розділ з пробілом.tex','latex-dvi':'тези/семантика з пробілом.tex'};
const jobFor=engine=>engine==='lualatex'?'result':roots[engine].slice(roots[engine].lastIndexOf('/')+1,-4);
function project(engine,backend='bibtex',unbalanced=false){
 const font=engine==='latex-dvi'?'\\usepackage[utf8]{inputenc}\n\\usepackage[T2A]{fontenc}\n':'\\usepackage{fontspec}\n\\setmainfont{DejaVuSerif.ttf}'+(engine==='lualatex'?'[Renderer=HarfBuzz]':'')+'\n';
 const bib=backend==='biber'?'\\usepackage[backend=biber,style=numeric]{biblatex}\n\\addbibresource{references.bib}\n':'';
 const source='\\documentclass[10pt]{article}\n'+font+bib+'\\begin{document}\n\\section{Original Unicode}\n'+
  '\\noindent Привіт Київ. Іі Її Єє Ґґ. ASCII I i.\\par\n\\input{'+include.slice(0,-4)+'}\n'+
  (backend==='none'?'':'Included reference~\\ref{part}. Citation~\\cite{book}.\n'+(backend==='biber'?'\\printbibliography\n':'\\bibliographystyle{plain}\n\\bibliography{references}\n'))+
  (unbalanced?'\\special{pdf:literal direct /Span << /ActualText <FEFF0456> >> BDC}\n':'')+'\\end{document}\n';
 return[{name:roots[engine],bytes:text(source)},{name:include,bytes:text('\\noindent Included Іі; Latin I i.\\label{part}\\par\n')},
  {name:'references.bib',bytes:text('@book{book,author={Zimmer, Zoe},title={{Portable Compiler Reference}},publisher={Offline Fixture Press},year={2025}}\n')}];
}
function resources(engine){const fmt={xelatex:'xelatex.fmt',lualatex:'lualatex.fmt','latex-dvi':'latex-dvi.fmt',pdftex:'pdflatex.fmt'}[engine];
 return pins.filter(p=>p.name===fmt||!(/\.(js|wasm|fmt)$/.test(p.name))&&p.name!=='biber.data'&&(engine==='xelatex'||!['icudt68l.dat','xetexfontlist.txt'].includes(p.name)))
  .map(p=>({name:prefix+(['pdflatex.fmt','latex-dvi.fmt'].includes(p.name)?'swiftlatexpdftex.fmt':p.name),bytes:get(p.name)}));}
function frame(engine,ordinal,tool,backend,p,carry=[]){
 const job=jobFor(engine),r=resources(tool==='bibtex'?'pdftex':engine),control=backend==='biber'&&tool==='latex';
 const outputs=['.pdf','.aux','.bbl','.blg','.log','.toc','.lof','.lot','.out',...(engine==='xelatex'?['.xdv']:engine==='latex-dvi'?['.dvi']:[]),...(control?['.bcf','.run.xml']:[])].map(e=>job+e);
 if(!control)outputs.push(job.replaceAll(' ','_')+'-blx.bib');
 return{operation:tool,bibliographyTool:backend,tag:{profile:13,version:{xelatex:21,lualatex:22,'latex-dvi':24}[engine],engineId:engine,tool,ordinal,runtimeIdentity:'b'.repeat(64),
  nonce:'genuine-'+engine+'-'+ordinal,job:'job',epoch:1,request:'request',capture:'capture',digest:'a'.repeat(64)},selectedRoot:roots[engine],
  mainEntry:tool==='bibtex'?job+'.tex':engine==='latex-dvi'?'"'+roots[engine]+'"':roots[engine],files:[...r,...p,...carry],runtimeResourceNames:r.map(f=>f.name),generatedInputNames:carry.map(f=>f.name),outputs,maxLogBytes:262144,maxOutputBytes:8388608};
}
async function run(workerName,stem,request,expected='native-return'){
 const messages=[];let readyResolve,readyReject;const ready=new Promise((a,b)=>{readyResolve=a;readyReject=b;});
 const c=vm.createContext({Uint8Array,Int8Array,Uint16Array,Int16Array,Uint32Array,Int32Array,Float32Array,Float64Array,ArrayBuffer,TextEncoder,TextDecoder,WebAssembly,
  crypto:webcrypto,performance,setTimeout,clearTimeout,URL,Response,WorkerGlobalScope:function(){},console:{log(){},warn(){},error(){}}});
 c.self=c;c.location={href:'https://unicode.invalid/worker.js'};c.postMessage=m=>{messages.push(m);if(m.kind==='ready')readyResolve();if(m.kind==='boot-error')readyReject(Error(JSON.stringify(m)));};
 c.importScripts=()=>vm.runInContext(Buffer.from(get(stem+'.js')).toString(),c);
 const source=fs.readFileSync(path.join(__dirname,'../../src/Client/wwwroot/js',workerName),'utf8');vm.runInContext(source,c);
 const timer=setTimeout(()=>readyReject(Error('genuine worker boot deadline')),15000);
 try{await Promise.all([c.onmessage({data:{operation:'boot',loaderUrl:'blob:verified-loader',loader:get(stem+'.js'),wasm:get(stem+'.wasm')}}),ready]);}finally{clearTimeout(timer);}
 await c.onmessage({data:request});const reply=messages.findLast(m=>m.kind==='done')?.reply;
 assert.ok(reply,JSON.stringify(messages));assert.equal(reply.outcome,expected,JSON.stringify({workerName,stem,outcome:reply.outcome,entered:reply.entered,log:reply.log}));
 assert.equal(reply.nativeReturn,0,reply.log);assert.equal(reply.entered,true);assert.deepEqual(reply.tag,request.tag);
 assert.equal(reply.loaderSha256,inventory.get(stem+'.js').sha256);assert.equal(reply.wasmSha256,inventory.get(stem+'.wasm').sha256);
 assert.equal(reply.inputs.length,request.files.length);
 for(const f of request.files){const fact=reply.inputs.find(p=>p.name===f.name);assert.deepEqual(fact&&[fact.length,fact.sha256],[f.bytes.length,hash(f.bytes)],f.name);}
 if(expected!=='native-return'){assert.equal(reply.files.length,0);assert.equal(reply.sourceMapPayload,null);assert.equal(reply.sourceMapGeometryPayload,null);}
 if(expected==='native-return'&&request.operation==='latex'){
  assert.ok(reply.sourceMapPayload?.length>0&&reply.sourceMapPayload.length<=1048575);
  const names=[...new TextDecoder().decode(reply.sourceMapPayload).matchAll(/^Input:\d+:(.*)$/gm)].map(x=>x[1].trim());
  for(const n of [request.selectedRoot,include])assert.ok(names.includes(n)||names.includes('./'+n),JSON.stringify({n,names}));
 }
 return reply;
}
function converterFrame(engine,ordinal,p,intermediate,backend='bibtex'){
 const job=jobFor(engine),r=pins.filter(p=>['glyphlist.txt','pdfglyphlist.txt'].includes(p.name)||/\.(tfm|pfb|ttf|otf|vf|map|enc)$/.test(p.name)).map(p=>({name:prefix+p.name,bytes:get(p.name)}));
 const entry=job+(engine==='xelatex'?'.xdv':'.dvi');
 return{operation:'dvipdfmx',bibliographyTool:backend,tag:{profile:13,version:engine==='xelatex'?21:24,engineId:engine,tool:'dvipdfmx',ordinal,runtimeIdentity:'b'.repeat(64),nonce:'convert-'+ordinal},
  selectedRoot:roots[engine],mainEntry:entry,files:[...r,...p,{name:entry,bytes:intermediate}],runtimeResourceNames:r.map(f=>f.name),generatedInputNames:[],outputs:[job+'.pdf'],maxLogBytes:262144,maxOutputBytes:8388608};
}
async function nativeGeometry(stem,request){
 const m=await require(path.join(runtime,'files',stem+'.js'))({instantiateWasm(imports,done){const i=new WebAssembly.Instance(new WebAssembly.Module(get(stem+'.wasm')),imports);done(i);return i.exports;},print(){},printErr(){}});
 const alloc=b=>{const p=m._malloc(Math.max(1,b.length));assert.ok(p);m.HEAPU8.set(b,p);return p;},r=new Set(request.runtimeResourceNames);
 for(const f of request.files){const n=alloc(text((r.has(f.name)?f.name.slice(prefix.length):f.name)+'\0')),b=alloc(f.bytes);assert.equal(m._oc_dvi_add_file(n,b,f.bytes.length,r.has(f.name)?1:0),0,f.name);m._free(n);m._free(b);}
 assert.equal(m._oc_dvi_geometry_configure(1,request.tag.ordinal),1);const p=alloc(text(request.mainEntry+'\0'));assert.equal(m._oc_dvi_run(p),0);m._free(p);
 const size=m._oc_dvi_geometry_length(request.tag.ordinal);assert.ok(size>0&&size<=131072);const q=m._malloc(size);assert.equal(m._oc_dvi_geometry_copy(request.tag.ordinal,q,size),size);
 const bytes=Buffer.from(m.HEAPU8.slice(q,q+size));m._free(q);return bytes;
}
async function inspect(pdf){
 const dir=process.env.COMPILER_BIBER_UNICODE_PDF_RUNTIME_DIR;assert.ok(dir,'explicit accepted E1 runtime required');
 const wasm=fs.readFileSync(path.join(dir,'e1.wasm'));assert.equal(hash(wasm),'1637c1a7798cab443334e559045d88f33864d89e0ff02eeb57b3da4f08f37c20');
 assert.equal(hash(fs.readFileSync(path.join(dir,'e1.js'))),'040bdfac50656b3993f622614b73feaaba31d8253d077ff50f925f316c2f5f6b');
 const m=await require(path.join(dir,'e1.js'))({wasmBinary:wasm,print(){},printErr(){}}),check=(n,...a)=>assert.equal(m['_'+n](...a),0,n);
 const input=b=>{const h=m._e1_alloc(b.length);assert.ok(h);m.HEAPU8.set(b,m._e1_data(h));return h;};
 const take=()=>{const h=m._e1_result(),n=m._e1_size(h),v=input(Buffer.alloc(n));check('e1_copy',h,v);const b=Buffer.from(m.HEAPU8.slice(m._e1_data(v),m._e1_data(v)+n));check('e1_release',h);check('e1_release',v);return JSON.parse(b);};
 const budget=Buffer.alloc(36);[1,36,8388608,32,100000,256,4000000,8388608,64].forEach((n,i)=>budget.writeUInt32LE(n,i*4));
 const data=input(pdf),b=input(budget);check('e1_open',data,b);const doc=m._e1_result();check('e1_release',data);check('e1_release',b);
 const pages=[];for(let i=0;i<m._e1_document_pages(doc);i++){check('e1_page',doc,i);const page=m._e1_result();check('e1_text',page);const text=take();const search={};
  for(const s of ['Привіт','Іі','ASCII I i']){const q=input(Buffer.from(s+'\0','utf16le'));check('e1_search',page,q);search[s]=take();check('e1_release',q);}
  pages.push({text:text.text,search});check('e1_close',page);}
 check('e1_close',doc);assert.equal(m._e1_live(),0);return pages;
}
(async()=>{
 const results=[];let workerInvocations=0;
 for(const engine of ['xelatex','lualatex']){
  const p=project(engine),job=jobFor(engine),phases=[];let carry=[],reply;
  for(const [ordinal,tool]of [[1,'latex'],[2,'bibtex'],[3,'latex'],[4,'latex']]){
   reply=await run(tool==='bibtex'?'compile-port-worker.js':'unicode-tex-port-worker.js',tool==='bibtex'?'engine':engine==='xelatex'?'xetex':'luatex',frame(engine,ordinal,tool,'bibtex',p,carry));workerInvocations++;
   const next=reply.files.filter(f=>/\.(aux|bbl|toc|lof|lot|out)$/.test(f.name)||f.name===job.replaceAll(' ','_')+'-blx.bib');
   carry=tool==='bibtex'?[...carry.filter(f=>f.name!==job+'.bbl'),...next.filter(f=>f.name===job+'.bbl')]:next;
   if(tool==='bibtex')assert.ok(carry.find(f=>f.name===job+'.bbl')?.bytes.length);phases.push({ordinal,tool});global.gc?.();
  }
  assert.ok(!/undefined references|Citation .* undefined|Label\(s\) may have changed/.test(reply.log),reply.log);
  let pdf,geometry=null;const raw=reply.sourceMapPayload;
  if(engine==='xelatex'){
   const intermediate=reply.files.find(f=>f.name===job+'.xdv').bytes,request=converterFrame(engine,8,p,intermediate);
   const converted=await run('dvi-port-worker.js','xdv-map-converter',request);workerInvocations++;pdf=converted.files[0].bytes;geometry=converted.sourceMapGeometryPayload;
   assert.deepEqual(Buffer.from(geometry),await nativeGeometry('xdv-map-converter',request));
  }else{pdf=reply.files.find(f=>f.name==='result.pdf').bytes;geometry=reply.sourceMapGeometryPayload;}
  const measured=JSON.parse(Buffer.from(geometry));assert.ok(measured.pages.length&&measured.pages.every(p=>p.status==='available'&&p.writerToPdfRaw.every(Number.isFinite)));
  const pages=await inspect(pdf),full=pages.map(p=>p.text).join('\n');assert.ok(full.includes('Привіт Київ. Іі Її Єє Ґґ. ASCII I i.'),full);assert.ok(full.includes('Included Іі; Latin I i.'),full);assert.match(full,/Portable\s+Compiler\s+Reference/);
  for(const q of ['Привіт','Іі','ASCII I i'])assert.ok(pages.some(p=>p.search[q].length),q);
  const biber=await run('unicode-tex-port-worker.js',engine==='xelatex'?'xetex':'luatex',frame(engine,9,'latex','biber',project(engine,'biber')));workerInvocations++;
  const control=biber.files.filter(f=>f.name===job+'.bcf'||f.name===job+'.run.xml');assert.equal(control.length,2);assert.ok(control.every(f=>f.bytes.length));
  results.push({engine,root:roots[engine],phases,sourceFiles:p.map(f=>({name:f.name,length:f.bytes.length,sha256:hash(f.bytes)})),map:hash(raw),pdf:hash(pdf),geometry:hash(geometry),pages,
   currentBiberControl:control.map(f=>({name:f.name,length:f.bytes.length,sha256:hash(f.bytes)}))});console.log(engine+' actual Unicode/fontspec/BibTeX/BCF/PDF/search/maps PASS');global.gc?.();
 }
 const engine='latex-dvi',p=project(engine,'none'),request=frame(engine,10,'latex','none',p),producer=await run('compile-port-worker.js','semantic-engine',request);workerInvocations++;
 const intermediate=producer.files.find(f=>f.name===jobFor(engine)+'.dvi').bytes,convertedRequest=converterFrame(engine,11,p,intermediate,'none');
 const converted=await run('dvi-port-worker.js','dvi-map-converter',convertedRequest);workerInvocations++;
 assert.deepEqual(Buffer.from(converted.sourceMapGeometryPayload),await nativeGeometry('dvi-map-converter',convertedRequest));
 const pages=await inspect(converted.files[0].bytes);assert.ok(pages.map(p=>p.text).join('\n').includes('Привіт Київ. Іі Її Єє Ґґ. ASCII I i.'));
 results.push({engine,root:roots[engine],map:hash(producer.sourceMapPayload),pdf:hash(converted.files[0].bytes),geometry:hash(converted.sourceMapGeometryPayload),pages});
 const unsafeProject=project(engine,'none',true),unsafeProducer=await run('compile-port-worker.js','semantic-engine',frame(engine,12,'latex','none',unsafeProject));workerInvocations++;
 const unsafeDvi=unsafeProducer.files.find(f=>f.name===jobFor(engine)+'.dvi').bytes;
 const unsupported=await run('dvi-port-worker.js','dvi-map-converter',converterFrame(engine,13,unsafeProject,unsafeDvi,'none'),'font-semantics-unsupported');workerInvocations++;
 const unsupportedProof={kind:'genuine explicitly authored unbalanced semantic-special fixture',sourceFiles:unsafeProject.map(f=>({name:f.name,length:f.bytes.length,sha256:hash(f.bytes)})),
  intermediate:hash(unsafeDvi),outcome:unsupported.outcome,nativeReturn:unsupported.nativeReturn,entered:unsupported.entered,files:unsupported.files.length,
  sourceMapPayload:unsupported.sourceMapPayload,sourceMapGeometryPayload:unsupported.sourceMapGeometryPayload,verifiedInputFacts:unsupported.inputs.length};
 const workers=['unicode-tex-port-worker.js','dvi-port-worker.js','compile-port-worker.js'].map(n=>{const b=fs.readFileSync(path.join(__dirname,'../../src/Client/wwwroot/js',n));return{name:n,length:b.length,sha256:hash(b)};});
 const receipt={kind:'actual-P13-Unicode/converter-worker-WASM-not-browser-managed-authority',closure,workerInvocations,independentNativeGeometryRuns:2,skips:0,workers,results,unsupportedProof};
 if(process.env.COMPILER_BIBER_UNICODE_RESULTS){fs.mkdirSync(process.env.COMPILER_BIBER_UNICODE_RESULTS,{recursive:true});fs.writeFileSync(path.join(process.env.COMPILER_BIBER_UNICODE_RESULTS,'receipt.json'),JSON.stringify(receipt,null,2)+'\n');}
 console.log(JSON.stringify(receipt,null,2));
})().catch(e=>{console.error(e);process.exitCode=1;});
