'use strict';
// Real pinned P13 native budget probes through unchanged product workers.
// QA DVI-special vectors exercise parser budgets, not original-font correctness.
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const {spawnSync}=require('node:child_process'),{webcrypto,createHash}=require('node:crypto');
const runtime=process.env.COMPILER_SEMANTICS_BOUNDS_RUNTIME_DIR,out=process.env.COMPILER_SEMANTICS_BOUNDS_RESULTS;
if(!runtime){console.log('SKIP actual semantic bounds: COMPILER_SEMANTICS_BOUNDS_RUNTIME_DIR required.');process.exit(0);}
assert.ok(out,'explicit fresh results directory required');
const hash=b=>createHash('sha256').update(b).digest('hex'),pin=b=>({length:b.length,sha256:hash(b)}),text=s=>new TextEncoder().encode(s),prefix='__oop_runtime__/';
const pins=JSON.parse(fs.readFileSync(path.join(runtime,'pins.json'))),inventory=new Map(pins.map(p=>[p.name,p]));
const closure=hash(Buffer.from([...pins].sort((a,b)=>a.name<b.name?-1:1).map(p=>`${p.name}\t${p.length}\t${p.sha256}`).join('\n')));
assert.equal(closure,'34540765dfd1b5d46666841378221cce0b40204014c4a8c2dc25ddddbe56d1ed');
assert.equal(pins.length,4357);assert.equal(pins.reduce((n,p)=>n+p.length,0),259559615);
const cache=new Map(),get=n=>{if(!cache.has(n)){const b=new Uint8Array(fs.readFileSync(path.join(runtime,'files',n)));const p=inventory.get(n);assert.ok(p,n);assert.deepEqual(pin(b),{length:p.length,sha256:p.sha256},n);cache.set(n,b);}return cache.get(n);};
const productDir=path.join(__dirname,'../../src/Client/wwwroot/js'),workers={
 'compile-port-worker.js':'535d78b02a315c818617c5a848f99a66a00dc0bf533642a0970683387b87fd9e',
 'dvi-port-worker.js':'7c031a92b5da9890fb93a15442f552c442d3507bf016dda2a892ae3b5519354e',
 'unicode-tex-port-worker.js':'26f9ef3ea473ed810fe7ad61ab49a34ff518dd52b16039d396700c5481fd4f09'};
const source=n=>{const b=fs.readFileSync(path.join(productDir,n));assert.equal(hash(b),workers[n],n);return b.toString();};
async function run(workerName,stem,request){
 const messages=[];let instance,resolveReady,rejectReady;const ready=new Promise((a,b)=>{resolveReady=a;rejectReady=b;});
 // Observe the real instantiated exports only. Imports, bytes and return values
 // pass through unchanged; no native function or product worker is replaced.
 const wasm=Object.create(WebAssembly);wasm.instantiate=async(...args)=>{const r=await WebAssembly.instantiate(...args);instance=r.instance??r;return r;};
 const c=vm.createContext({Uint8Array,Int8Array,Uint16Array,Int16Array,Uint32Array,Int32Array,Float32Array,Float64Array,ArrayBuffer,TextEncoder,TextDecoder,
  WebAssembly:wasm,crypto:webcrypto,performance,setTimeout,clearTimeout,URL,Response,WorkerGlobalScope:function(){},console:{log(){},warn(){},error(){}}});
 c.self=c;c.location={href:'https://semantic-bounds.invalid/worker.js'};
 c.postMessage=m=>{messages.push(m);if(m.kind==='ready')resolveReady();if(m.kind==='boot-error')rejectReady(Error(JSON.stringify(m)));};
 c.importScripts=()=>vm.runInContext(Buffer.from(get(stem+'.js')).toString(),c);vm.runInContext(source(workerName),c);
 const timer=setTimeout(()=>rejectReady(Error('native boot deadline')),15000);
 try{await Promise.all([c.onmessage({data:{operation:'boot',loaderUrl:'blob:pinned',loader:get(stem+'.js'),wasm:get(stem+'.wasm')}}),ready]);}finally{clearTimeout(timer);}
 await c.onmessage({data:request});const reply=messages.findLast(m=>m.kind==='done')?.reply;assert.ok(reply,JSON.stringify(messages));
 assert.equal(reply.entered,true,JSON.stringify(reply));assert.deepEqual(reply.tag,request.tag);
 assert.equal(reply.loaderSha256,inventory.get(stem+'.js').sha256);assert.equal(reply.wasmSha256,inventory.get(stem+'.wasm').sha256);
 assert.equal(reply.inputs.length,request.files.length);for(const f of request.files){const p=reply.inputs.find(p=>p.name===f.name);assert.deepEqual(p&&{length:p.length,sha256:p.sha256},pin(f.bytes),f.name);}
 return{reply,exports:instance.exports};
}
function appendSpecials(base,blob){
 assert.deepEqual([...base.subarray(0,2)],[247,2]);let tail=base.length;while(base[tail-1]===223)tail--;
 assert.equal(base[tail-1],2);assert.equal(base[tail-6],249);const post=base.readUInt32BE(tail-5);
 assert.equal(base[post],248);assert.equal(base.readUInt16BE(post+27),1);assert.equal(base[post-1],140);
 const b=Buffer.concat([base.subarray(0,post-1),blob,base.subarray(post-1,tail)]);b.writeUInt32BE(post+blob.length,tail-5+blob.length);
 return Buffer.concat([b,Buffer.alloc(4+(4-b.length%4)%4,223)]);
}
const special=s=>{const t=Buffer.from(s),h=Buffer.alloc(5);h[0]=242;h.writeUInt32BE(t.length,1);return Buffer.concat([h,t]);};
const pair=n=>Buffer.concat([special('pdf:literal direct /Span << /ActualText <FEFF'+'0066'.repeat(n-1)+'0456> >> BDC'),special('pdf:literal direct EMC')]);
function converterFrame(bytes){
 const r=pins.filter(p=>['glyphlist.txt','pdfglyphlist.txt'].includes(p.name)||/\.(tfm|pfb|ttf|otf|vf|map|enc)$/.test(p.name)).map(p=>({name:prefix+p.name,bytes:get(p.name)}));
 return{operation:'dvipdfmx',bibliographyTool:'none',tag:{profile:13,version:24,engineId:'latex-dvi',tool:'dvipdfmx',ordinal:1,runtimeIdentity:'b'.repeat(64),nonce:'native-semantic-budget'},
  selectedRoot:'main.tex',mainEntry:'main.dvi',files:[...r,{name:'main.dvi',bytes:new Uint8Array(bytes)}],runtimeResourceNames:r.map(f=>f.name),generatedInputNames:[],outputs:['main.pdf'],maxLogBytes:262144,maxOutputBytes:8388608};
}
const converterCases=[['control-before',0,0],['spans-below',65535,65535],['spans-at',65536,65536],['spans-above',65537,65537],
 ['scalars-below',4096,262143],['scalars-at',4096,262144],['scalars-above',4097,262145],['control-after',0,0]];
const controlSource=fs.readFileSync(path.join(__dirname,'../CompilerSemanticsFixtures/bounds/control.tex'));
assert.deepEqual(pin(controlSource),{length:159,sha256:'272c24592222e8ba4753e408687d1bd31ea3693d39134dded55b9245be5aca9a'});
async function produceControl(){
 const r=pins.filter(p=>p.name==='latex-dvi.fmt'||!(/\.(js|wasm|fmt)$/.test(p.name))&&!['biber.data','icudt68l.dat','xetexfontlist.txt'].includes(p.name)&&!p.name.startsWith('notices/'))
  .map(p=>({name:prefix+(p.name==='latex-dvi.fmt'?'swiftlatexpdftex.fmt':p.name),bytes:get(p.name)}));
 const request={operation:'latex',bibliographyTool:'none',tag:{profile:13,version:24,engineId:'latex-dvi',tool:'latex',ordinal:1,runtimeIdentity:'b'.repeat(64),nonce:'genuine-budget-control'},
  selectedRoot:'main.tex',mainEntry:'main.tex',files:[...r,{name:'main.tex',bytes:new Uint8Array(controlSource)}],runtimeResourceNames:r.map(f=>f.name),generatedInputNames:[],
  outputs:['.pdf','.aux','.bbl','.blg','.log','.toc','.lof','.lot','.out','.dvi','-blx.bib'].map(ext=>'main'+ext),maxLogBytes:262144,maxOutputBytes:8388608};
 const {reply}=await run('compile-port-worker.js','semantic-engine',request);
 fs.writeFileSync(path.join(out,'control-producer.native.log'),reply.log);
 assert.equal(reply.outcome,'native-return',reply.log);assert.equal(reply.nativeReturn,0,reply.log);
 assert.ok(reply.sourceMapPayload?.length,'genuine final-pass native map');
 const dvi=reply.files.find(f=>f.name==='main.dvi');assert.ok(dvi?.bytes.length,'genuine native DVI output');
 const receipt={kind:'genuine-frozen-P13-compile-worker-produced-control',source:pin(controlSource),dvi:pin(dvi.bytes),
  loader:inventory.get('semantic-engine.js'),wasm:inventory.get('semantic-engine.wasm'),format:inventory.get('latex-dvi.fmt'),
  nativeReturn:reply.nativeReturn,entered:reply.entered,verifiedInputFacts:reply.inputs.length,sourceMap:pin(reply.sourceMapPayload),heapBytes:reply.heapBytes,elapsedMs:reply.elapsedMs};
 fs.writeFileSync(path.join(out,'control.dvi'),dvi.bytes);fs.writeFileSync(path.join(out,'control-producer.json'),JSON.stringify(receipt,null,2)+'\n');
 return receipt;
}
const observer=`local glyph=node.id('glyph')
luatexbase.add_to_callback('post_linebreak_filter',function(head)
 for line in node.traverse_id(node.id('hlist'),head) do
  local nodes,units,rtl,emitted=0,0,false,0
  for n in node.traverse(line.list) do
   nodes=nodes+1
   local p=node.direct.get_properties_table()[node.direct.todirect(n)]
   p=p and rawget(p,'overclone_harf_logical_cluster_v1')
   if p and (n.id==glyph or n.id==node.id('glue')) then
    rtl=rtl or p.rtl
    for _,cp in utf8.codes(p.text) do units=units+(cp>65535 and 2 or 1) end
   end
   if n.id==node.id('whatsit') and n.subtype==node.subtype('pdf_literal') then
    local h=n.data:match('/ActualText<FEFF(%x+)>')
    if h then emitted=emitted+string.len(h)/4 end
   end
  end
  texio.write_nl('term and log','SEMANTIC-BOUNDARY nodes='..nodes..' units='..units..' rtl='..tostring(rtl)..' emitted='..emitted)
 end
 return head
end,'overclone.boundary.readonly')
luatexbase.declare_callback_rule('post_linebreak_filter','overclone.boundary.readonly','before','luaotfload.harf.finalize_vlist')
`;
const luaCases=[['lua-control-before',null],['utf16-below',16383],['utf16-at',16384],['utf16-above',16385],['lua-control-after',null]];
async function luaCase(name){
 const tuple=luaCases.find(c=>c[0]===name);assert.ok(tuple,name);const count=tuple[1];
 const font=fs.readFileSync(process.env.COMPILER_SEMANTICS_BOUNDS_AMIRI);assert.deepEqual(pin(font),{length:548016,sha256:'6988222efdde090e0926cabcaf695c5f551269efbf751b29b097eeb3dbf27e39'});
 const original=fs.readFileSync(path.join(__dirname,'../../native/tex/luatex-engine/harfbuzz/fixtures/harfbuzz.tex'));
 const src=count===null?original:Buffer.from('\\documentclass{article}\n\\usepackage{fontspec}\n\\setmainfont{amiri-regular.ttf}[Path=fonts/,Renderer=HarfBuzz,Script=Arabic,Language=Arabic]\n'+
  '\\directlua{dofile("observe.lua")}\n\\begin{document}\n\\fontsize{0.5}{0.6}\\selectfont\\hsize=16000pt\\noindent\\textdir TRT '+ 'ب'.repeat(count)+'\\par\n\\end{document}\n');
 const r=pins.filter(p=>p.name==='lualatex.fmt'||!(/\.(js|wasm|fmt)$/.test(p.name))&&!['biber.data','icudt68l.dat','xetexfontlist.txt'].includes(p.name)).map(p=>({name:prefix+p.name,bytes:get(p.name)}));
 const project=[{name:'main.tex',bytes:new Uint8Array(src)},{name:'fonts/amiri-regular.ttf',bytes:new Uint8Array(font)},...(count===null?[]:[{name:'observe.lua',bytes:text(observer)}])];
 const request={operation:'latex',bibliographyTool:'none',tag:{profile:13,version:22,engineId:'lualatex',tool:'latex',ordinal:1,runtimeIdentity:'b'.repeat(64),nonce:'semantic-boundary-lua'},
  selectedRoot:'main.tex',mainEntry:'main.tex',files:[...r,...project],runtimeResourceNames:r.map(f=>f.name),generatedInputNames:[],
  outputs:['result.pdf','result.aux','result.log','result-blx.bib'],maxLogBytes:262144,maxOutputBytes:8388608};
 const {reply}=await run('unicode-tex-port-worker.js','luatex',request);
 fs.writeFileSync(path.join(out,name+'.native.log'),reply.log);
 const observed=[...reply.log.matchAll(/SEMANTIC-BOUNDARY nodes=(\d+) units=(\d+) rtl=(true|false) emitted=(\d+)/g)].map(m=>({nodes:Number(m[1]),units:Number(m[2]),rtl:m[3]==='true',emitted:Number(m[4])}));
 if(count!==null&&name!=='utf16-above'){assert.equal(observed.length,1,reply.log);assert.equal(observed[0].units||observed[0].emitted,count);assert.ok(observed[0].nodes<65536,'UTF16 budget isolated from visited-node budget');}
 const good=name!=='utf16-above';assert.equal(reply.outcome,'native-return',reply.log);assert.equal(reply.nativeReturn,good?0:1,reply.log);
 if(good)assert.ok(reply.files.find(f=>f.name==='result.pdf')?.bytes.length);else{assert.match(reply.log,/Overclone HarfBuzz unsupported or oversized logical line provenance/);assert.equal(reply.files.length,0);assert.equal(reply.sourceMapPayload,null);assert.equal(reply.sourceMapGeometryPayload,null);}
 const pdf=reply.files.find(f=>f.name==='result.pdf');if(pdf)fs.writeFileSync(path.join(out,name+'.pdf'),pdf.bytes);
 const receipt={name,kind:'genuine-frozen-LuaHB-worker-physical-line-UTF16-budget',requestedUtf16Units:count,observedReadOnlyPhysicalLines:observed,
  originalControl:count===null,project:project.map(f=>({name:f.name,...pin(f.bytes)})),nativeReturn:reply.nativeReturn,outcome:reply.outcome,entered:reply.entered,verifiedInputFacts:reply.inputs.length,
  outputFiles:reply.files.map(f=>({name:f.name,...pin(f.bytes)})),sourceMap:reply.sourceMapPayload?pin(reply.sourceMapPayload):null,geometry:reply.sourceMapGeometryPayload?pin(reply.sourceMapGeometryPayload):null,
  heapBytes:reply.heapBytes,elapsedMs:reply.elapsedMs};
 fs.writeFileSync(path.join(out,name+'.json'),JSON.stringify(receipt,null,2)+'\n');console.log(name+' PASS '+JSON.stringify({return:reply.nativeReturn,observed}));
}
async function converterCase(name){
 const tuple=converterCases.find(c=>c[0]===name);assert.ok(tuple,name);const [,spans,scalars]=tuple;
 const producer=JSON.parse(fs.readFileSync(path.join(out,'control-producer.json'))),base=fs.readFileSync(path.join(out,'control.dvi'));
 assert.deepEqual(producer.source,pin(controlSource));assert.equal(producer.nativeReturn,0);assert.equal(producer.entered,true);
 assert.deepEqual(producer.loader,inventory.get('semantic-engine.js'));assert.deepEqual(producer.wasm,inventory.get('semantic-engine.wasm'));
 assert.deepEqual(producer.format,inventory.get('latex-dvi.fmt'));assert.deepEqual(pin(base),producer.dvi);
 let blob=Buffer.alloc(0);if(name.startsWith('spans-'))blob=Buffer.concat(Array(spans).fill(pair(1)));
 else if(name.startsWith('scalars-')){const n=Math.floor(scalars/64),remainder=scalars%64;blob=Buffer.concat([...Array(n).fill(pair(64)),...(remainder?[pair(remainder)]:[])]);}
 const input=spans?appendSpecials(base,blob):base;assert.ok(input.length<=8388608,'reachable within actual converter project byte cap');
 const {reply:r,exports:e}=await run('dvi-port-worker.js','dvi-map-converter',converterFrame(input));
 const status=e.oc_dvi_font_semantics_status(),seen=e.oc_dvi_font_semantics_spans(),good=!name.endsWith('above');
 assert.equal(r.nativeReturn,0,r.log);assert.equal(status,good?1:2);assert.equal(seen,name==='spans-above'?65536:name==='scalars-above'?4096:spans);
 assert.equal(r.outcome,good?'native-return':'font-semantics-unsupported',r.log);
 let pdf=null,geometry=null;if(good){assert.equal(r.files.length,1);pdf=pin(r.files[0].bytes);geometry=JSON.parse(Buffer.from(r.sourceMapGeometryPayload));assert.ok(geometry.pages.length&&geometry.pages.every(p=>p.status==='available'));}
 else{assert.equal(r.files.length,0);assert.equal(r.sourceMapPayload,null);assert.equal(r.sourceMapGeometryPayload,null);}
 const receipt={name,kind:'genuine-frozen-converter-worker-with-independent-QA-DVI-budget-vector',requested:{spans,scalars},native:{return:r.nativeReturn,semanticStatus:status,acceptedSpans:seen},
  outcome:r.outcome,entered:r.entered,verifiedInputFacts:r.inputs.length,input:pin(input),baseDvi:pin(base),pdf,geometry,outputFiles:r.files.length,
  sourceMapPayload:r.sourceMapPayload,heapBytes:r.heapBytes,elapsedMs:r.elapsedMs};
 fs.writeFileSync(path.join(out,name+'.json'),JSON.stringify(receipt,null,2)+'\n');console.log(name+' PASS '+JSON.stringify(receipt.native));
}
async function main(){
 if(process.argv[2]==='--case')return converterCase(process.argv[3]);
 if(process.argv[2]==='--lua-case')return luaCase(process.argv[3]);
 assert.ok(!fs.existsSync(out),'fresh results directory required');fs.mkdirSync(out,{recursive:true});
 for(const p of pins)get(p.name);const before=Object.keys(workers).map(name=>({name,...pin(Buffer.from(source(name)))})),results=[];
 const selected=process.argv[2]==='--lua'?luaCases:converterCases,flag=process.argv[2]==='--lua'?'--lua-case':'--case';
 const producer=flag==='--case'?await produceControl():null;global.gc?.();
 for(const [name]of selected){const r=spawnSync(process.execPath,[__filename,flag,name],{env:process.env,encoding:'utf8',timeout:180000});
  fs.writeFileSync(path.join(out,name+'.driver.log'),r.stdout+(r.stderr||''));assert.equal(r.status,0,name+': '+r.stdout+r.stderr);results.push(JSON.parse(fs.readFileSync(path.join(out,name+'.json'))));console.log(r.stdout.trim());}
 const [first,last]=[results[0],results.at(-1)];
 if(flag==='--case'){assert.deepEqual(first.input,last.input);assert.deepEqual(first.geometry,last.geometry);assert.deepEqual(first.pdf,last.pdf,'unchanged genuine control PDF bytes before/after');}
 else{assert.deepEqual(first.project,last.project);assert.deepEqual(first.sourceMap,last.sourceMap);assert.deepEqual(first.geometry,last.geometry);}
 const after=Object.keys(workers).map(name=>({name,...pin(Buffer.from(source(name)))}));assert.deepEqual(after,before);
 fs.writeFileSync(path.join(out,'receipt.json'),JSON.stringify({schemaVersion:1,status:'pass',scope:'native worker ABI/parser budget controls; no expanded font/script/browser coverage',closure,
  runtime:{resources:pins.length,bytes:pins.reduce((n,p)=>n+p.length,0)},workers:before,controlProducer:producer,nativeInvocations:results.length+(producer?1:0),skips:0,results},null,2)+'\n');
}
main().catch(e=>{console.error(e);process.exitCode=1;});
