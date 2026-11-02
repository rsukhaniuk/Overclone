'use strict';
// Exact root product-worker VM/bootstrap/frame reuse, statically retained from
// /control/root-portable-native-diag01/driver.cjs. New-component overrides are
// explicit native test variants, not published/C# P9 profile admission.
// Genuine staged P9 WASM through actual product worker glue, not a browser,
// managed orchestration, query/renderer or live navigation acceptance claim.
const assert = require('node:assert/strict'), fs = require('node:fs'), path = require('node:path'), vm = require('node:vm');
const { webcrypto, createHash } = require('node:crypto');
const repo=path.resolve(__dirname,'../../..');
const root=process.env.XE_NAVIGATION_RUNTIME_DIR||'/control/profile9-runtime';
if (!root) { console.log('SKIP real Xe transport WASM: set XE_NAVIGATION_RUNTIME_DIR to the exact complete P9 stage.'); process.exit(0); }
const hash = b => createHash('sha256').update(b).digest('hex');
let pins;try{pins=JSON.parse(fs.readFileSync(path.join(root,'pins.json')));}catch(error){throw new Error('bad supplied P9 pins',{cause:error});}
const byName=new Map(pins.map(p=>[p.name,p]));
const closure = hash(Buffer.from([...pins].sort((a,b) => a.name < b.name ? -1 : 1).map(p => `${p.name}\t${p.length}\t${p.sha256}`).join('\n')));
assert.equal(closure, 'e82466563a5865fb5c400b3fbda82518f709a78ed40ae6d8289998e75fbeb9f7');
assert.equal(pins.length, 3709); assert.equal(pins.reduce((n,p) => n + p.length, 0), 192565170);
const overrides=Object.create(null);
for(const [stem,env]of [['luatex','LUA_CANDIDATE'],['dvi-map-converter','DVI_CONVERTER'],['xdv-map-converter','XE_CONVERTER']]){
 const dir=process.env[env];if(!dir)continue;
 for(const ext of ['js','wasm']){const file=path.join(dir,(stem==='luatex'?'luatex':'oc-dvi')+'.'+ext),bytes=fs.readFileSync(file);overrides[stem+'.'+ext]=bytes;byName.set(stem+'.'+ext,{name:stem+'.'+ext,length:bytes.length,sha256:hash(bytes)});}
}
function resource(name) {
    if(overrides[name])return new Uint8Array(overrides[name]);
    const p = byName.get(name), b = fs.readFileSync(path.join(root, 'files', name));
    assert.equal(b.length, p.length); assert.equal(hash(b), p.sha256); return new Uint8Array(b);
}
async function run(worker, stem, frame) {
    const messages = []; let readyResolve, readyReject;
    const ready = new Promise((resolve,reject) => {readyResolve=resolve;readyReject=reject;});
    const context = vm.createContext({ Uint8Array, Int8Array, Uint16Array, Int16Array, Uint32Array, Int32Array,
        Float32Array, Float64Array, ArrayBuffer, TextEncoder, TextDecoder, WebAssembly, crypto: webcrypto,
        performance, console: {log(){},warn(){},error(){}}, setTimeout, clearTimeout, URL, Response, WorkerGlobalScope: function(){} });
    context.self = context; context.location = {href:'https://native-test.invalid/worker.js'};
    context.postMessage = message => {messages.push(message);if(message.kind==='ready')readyResolve();if(message.kind==='boot-error')readyReject(new Error(JSON.stringify(message)));};
    context.importScripts = () => vm.runInContext(fs.readFileSync(path.join(root,'files',stem+'.js'),'utf8'), context);
    vm.runInContext(fs.readFileSync(path.join(repo,'src/Client/wwwroot/js',worker),'utf8'), context);
    const wasm = resource(stem+'.wasm');
    const timer=setTimeout(()=>readyReject(new Error('Actual native worker boot timeout')),15000);
    try {await Promise.all([context.onmessage({data:{operation:'boot',loaderUrl:'blob:verified-test-loader',loader:resource(stem+'.js'),wasm}}),ready]);}
    finally {clearTimeout(timer);}
    assert.ok(messages.some(m => m.kind === 'ready'), JSON.stringify(messages));
    await context.onmessage({data:frame});
    const reply = messages.findLast(m => m.kind === 'done')?.reply; assert.ok(reply, JSON.stringify(messages));
    assert.equal(reply.outcome,'native-return',JSON.stringify({outcome:reply.outcome,log:reply.log}));
    assert.equal(reply.nativeReturn,0,reply.log); assert.deepEqual(reply.tag,frame.tag);
    assert.equal(reply.wasmSha256,byName.get(stem+'.wasm').sha256); assert.equal(reply.loaderSha256,byName.get(stem+'.js').sha256);
    for(const f of frame.files) {
        const fact = reply.inputs.find(i => i.name === f.name);
        assert.deepEqual(fact && [fact.length,fact.sha256],[f.bytes.length,hash(f.bytes)]);
    }
    return reply;
}
const selectedRoot='тези/my thesis.tex';
function frame(engine,version,tool,ordinal,files,resources,outputs,entry) {
 return {operation:tool,tag:{profile:9,version,engineId:engine,tool,ordinal,job:'job',epoch:1,request:'request',capture:'capture',digest:'a'.repeat(64),nonce:'operation-'+ordinal,runtimeIdentity:'b'.repeat(64)},selectedRoot,mainEntry:entry,files,runtimeResourceNames:resources,generatedInputNames:[],outputs,maxLogBytes:262144,maxOutputBytes:8388608};
}

async function pipeline(engine,out){
 assert.ok(['latex-dvi','xelatex','lualatex'].includes(engine));assert.ok(!fs.existsSync(out));fs.mkdirSync(out,{recursive:true});
 const version=engine==='latex-dvi'?12:engine==='xelatex'?13:14,stem=engine==='latex-dvi'?'dvi-map-engine':engine==='xelatex'?'xetex':'luatex',format=engine==='latex-dvi'?'latex-dvi.fmt':engine==='xelatex'?'xelatex.fmt':'lualatex.fmt',job=engine==='lualatex'?'result':'my thesis',prefix='__oop_runtime__/';
 const main=fs.readFileSync(path.join(repo,'tests/CompilerSuiteFixtures',engine==='latex-dvi'?'main-latin.tex':'main-unicode.tex'));
 const project=[{name:selectedRoot,bytes:new Uint8Array(main)},...['chapter.tex','references.bib','figure.xbb'].map(name=>({name,bytes:new Uint8Array(fs.readFileSync(path.join(repo,'tests/CompilerSuiteFixtures',name)))})),{name:'figure.png',bytes:new Uint8Array(fs.readFileSync(path.join(repo,'src/Client/wwwroot/icons/icon-192.png')))}];
 const outputs=['.pdf','.aux','.bbl','.blg','.log','.toc','.lof','.lot','.out',...(engine==='latex-dvi'?['.dvi']:engine==='xelatex'?['.xdv']:[])].map(x=>job+x).concat(job.replaceAll(' ','_')+'-blx.bib','chapter.aux');
 const dataResources=which=>pins.filter(p=>p.name===(which==='bibtex'?'pdflatex.fmt':format)||!/\.(wasm|js|fmt)$/.test(p.name)&&(which!=='bibtex'&&engine==='xelatex'||!['icudt68l.dat','xetexfontlist.txt'].includes(p.name))).map(p=>({name:prefix+(p.name==='latex-dvi.fmt'?'swiftlatexpdftex.fmt':p.name==='pdflatex.fmt'?'swiftlatexpdftex.fmt':p.name),bytes:resource(p.name)}));
 let state=[],ordinal=0,selected;const phases=[];
 for(const tool of ['latex','bibtex','latex','latex']){
  const resources=dataResources(tool),entry=tool==='bibtex'?job+'.tex':engine==='latex-dvi'?'"'+selectedRoot+'"':selectedRoot;
  const f=frame(engine,version,tool,++ordinal,[...resources,...project,...state],resources.map(p=>p.name),outputs,entry);f.generatedInputNames=state.map(p=>p.name);
  const reply=await run(tool==='bibtex'||engine==='latex-dvi'?'compile-port-worker.js':'unicode-tex-port-worker.js',tool==='bibtex'?'engine':stem,f);
  for(const file of reply.files)if(/\.(aux|bbl|blg|toc|out|dvi|xdv|pdf)$/.test(file.name))fs.writeFileSync(path.join(out,ordinal+'-'+file.name),file.bytes);
  state=reply.files.filter(p=>/\.(aux|bbl|toc|out)$/.test(p.name)).map(p=>({name:p.name,bytes:p.bytes}));
  phases.push({ordinal,tool,tag:reply.tag,inputs:reply.inputs,files:reply.files.map(p=>({name:p.name,length:p.bytes.length,sha256:hash(p.bytes)})),mapHash:reply.sourceMapPayload?hash(reply.sourceMapPayload):null,geometryHash:reply.sourceMapGeometryPayload?hash(reply.sourceMapGeometryPayload):null});
  if(tool==='bibtex')assert.equal(reply.sourceMapPayload??null,null);else selected=reply;
 }
 assert.ok(selected.sourceMapPayload?.length);let geometry=selected.sourceMapGeometryPayload,pdf=selected.files.find(f=>f.name===job+'.pdf')?.bytes;
 if(engine!=='lualatex'){
  const resources=pins.filter(p=>['glyphlist.txt','pdfglyphlist.txt'].includes(p.name)||/\.(tfm|pfb|ttf|otf|vf|map|enc)$/.test(p.name)).map(p=>({name:prefix+p.name,bytes:resource(p.name)}));
  const name=job+(engine==='latex-dvi'?'.dvi':'.xdv'),ir=selected.files.find(f=>f.name===name);assert.ok(ir?.bytes.length);
  const f=frame(engine,version,'dvipdfmx',++ordinal,[...resources,...project,ir],resources.map(p=>p.name),[job+'.pdf'],name);const reply=await run('dvi-port-worker.js',engine==='latex-dvi'?'dvi-map-converter':'xdv-map-converter',f);geometry=reply.sourceMapGeometryPayload;pdf=reply.files.find(f=>f.name===job+'.pdf')?.bytes;phases.push({ordinal,tool:'dvipdfmx',inputs:reply.inputs,files:reply.files.map(p=>({name:p.name,length:p.bytes.length,sha256:hash(p.bytes)}))});
 }
 assert.ok(pdf?.length);assert.ok(geometry?.length);fs.writeFileSync(path.join(out,'output.pdf'),pdf);fs.writeFileSync(path.join(out,'raw.synctex'),selected.sourceMapPayload);fs.writeFileSync(path.join(out,'geometry.json'),geometry);
 let g;try{g=JSON.parse(new TextDecoder().decode(geometry));}catch(error){throw new Error('bad native geometry',{cause:error});}
 const receipt={schemaVersion:1,scope:'Actual product-worker VM/native final-pass/BibTeX fixture; no C#/published profile adoption/browser acceptance',engine,root:selectedRoot,project:project.map(p=>({name:p.name,length:p.bytes.length,sha256:hash(p.bytes)})),phases,geometry:g,pdf:{length:pdf.length,sha256:hash(pdf)},map:{length:selected.sourceMapPayload.length,sha256:hash(selected.sourceMapPayload)}};fs.writeFileSync(path.join(out,'receipt.json'),JSON.stringify(receipt,null,2)+'\n');return receipt;
}
module.exports={pipeline};
if(require.main===module)pipeline(process.argv[2],process.argv[3]).then(x=>console.log(JSON.stringify({engine:x.engine,geometry:x.geometry}))).catch(e=>{console.error(e);process.exitCode=1;});
