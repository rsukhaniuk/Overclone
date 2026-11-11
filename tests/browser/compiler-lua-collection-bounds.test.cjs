'use strict';
// Real pinned P13 native budget probes through unchanged product workers.
// QA DVI-special vectors exercise parser budgets, not original-font correctness.
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const {spawnSync}=require('node:child_process'),{webcrypto,createHash}=require('node:crypto');
const runtime=process.env.COMPILER_SEMANTICS_BOUNDS_RUNTIME_DIR,out=process.env.COMPILER_LUA_COLLECTION_BOUNDS_RESULTS;
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

const tuples=[['original-before','original',0],['tex-nodes-below','tex-nodes',65535],['tex-nodes-at','tex-nodes',65536],['tex-nodes-above','tex-nodes',65537],
 ['unit-depth-below','unit-depth',31],['unit-depth-at','unit-depth',32],['unit-depth-above','unit-depth',33],['original-after','original',0]];
const observe=`luatexbase.add_to_callback('post_linebreak_filter',function(head)
 for line in node.traverse_id(node.id('hlist'),head)do
 local n,literals,units=0,0,0
 for p in node.traverse(line.list)do n=n+1;if p.id==node.id('whatsit')and p.subtype==node.subtype('pdf_literal')then local h=p.data:match('/ActualText<FEFF(%x+)>');if h then literals=literals+1;units=units+string.len(h)/4 end end end
 texio.write_nl('term and log','PLAIN-NODES nodes='..n..' actualtext='..literals..' units='..units)
 end;return head
end,'overclone.collection.readonly')
luatexbase.declare_callback_rule('post_linebreak_filter','overclone.collection.readonly','after','luaotfload.harf.finalize_vlist')`;
function unitProgram(depth){return `
local function up(fn,want)
 for i=1,64 do local name,val=debug.getupvalue(fn,i);if not name then break end;if name==want then return val end end
end
local function named(fn,want,seen,level)
 if type(fn)~='function'or level>12 or seen[fn]then return end;seen[fn]=true
 for i=1,64 do local name,val=debug.getupvalue(fn,i);if not name then break end;if name==want and type(val)=='function'then return val end end
 for i=1,64 do local name,val=debug.getupvalue(fn,i);if not name then break end;if name~='_ENV'and type(val)=='function'then local found=named(val,want,seen,level+1);if found then return found end end end
end
local real=assert(up(luatexbase.add_to_callback,'realcallbacklist'))
local callback
for _,c in ipairs(real.post_linebreak_filter)do if c.description=='luaotfload.harf.finalize_vlist'then callback=c.func end end
local collector=assert(named(assert(callback),'semantic_spans',{},0),'actual installed collector upvalue')
local plugins=assert(up(fonts.handlers.otf.registerplugin,'plugins'))
local producer=assert(named(assert(plugins.harf[2]),'semantic_cluster',{},0),'actual installed producer upvalue')
local glyph=node.new('glyph');glyph.font=font.current();glyph.char=1576
producer(node.direct.todirect(glyph),'qa-collector-unit','ب',true)
local head=glyph
for i=1,${depth} do local d=node.new('disc');d.replace=head;head=d end
local function scan(list,level)
 local count,maxlevel=0,level
 for n in node.traverse(list)do count=count+1;if n.id==node.id('disc')and n.replace then local c,l=scan(n.replace,level+1);count=count+c;maxlevel=math.max(maxlevel,l)end end
 return count,maxlevel
end
local leading=node.new('glyph');leading.font=font.current();leading.char=1576
producer(node.direct.todirect(leading),'qa-visible-rtl','ب',true)
leading.next=head;head.prev=leading;head=leading
local visited,maxlevel=scan(head,0)
texio.write_nl('term and log','ACTUAL-COLLECTOR-UNIT visited='..visited..' depth='..maxlevel)
local result=collector(node.direct.todirect(head))
local count=0
for n in node.traverse(node.direct.tonode(result))do if n.id==node.id('whatsit')and n.subtype==node.subtype('pdf_literal')and n.data:match('/ActualText<FEFF06280628>')then count=count+1 end end
assert(count==1,'actual installed collector emits expected literal')
node.flush_list(node.direct.tonode(result))
texio.write_nl('term and log','ACTUAL-COLLECTOR-UNIT success=true')
`}
async function one(name){
 const [,kind,target]=tuples.find(t=>t[0]===name)||[];assert.ok(kind)
 const font=fs.readFileSync(process.env.COMPILER_SEMANTICS_BOUNDS_AMIRI);assert.deepEqual(pin(font),{length:548016,sha256:'6988222efdde090e0926cabcaf695c5f551269efbf751b29b097eeb3dbf27e39'});
 const original=fs.readFileSync(path.join(__dirname,'../../native/tex/luatex-engine/harfbuzz/fixtures/harfbuzz.tex'));
 let src=original,lua=null;
 if(kind!=='original'){
  const preamble='\\documentclass{article}\n\\usepackage{fontspec}\n\\setmainfont{amiri-regular.ttf}[Path=fonts/,Renderer=HarfBuzz,Script=Arabic,Language=Arabic]\n';
  if(kind==='tex-nodes'){lua=observe;src=Buffer.from(preamble+'\\directlua{dofile("qa.lua")}\n\\begin{document}\n\\noindent\\textdir TRT ب'+ '\\kern0pt%\n'.repeat(target-7)+'\\par\n\\end{document}\n');}
  else {lua=unitProgram(target);src=Buffer.from(preamble+'\\begin{document}\n\\noindent\\textdir TRT ب\\par\n\\directlua{dofile("qa.lua")}\n\\end{document}\n');}
 }
 const r=pins.filter(p=>p.name==='lualatex.fmt'||!(/\.(js|wasm|fmt)$/.test(p.name))&&!['biber.data','icudt68l.dat','xetexfontlist.txt'].includes(p.name)&&!p.name.startsWith('notices/')).map(p=>({name:prefix+p.name,bytes:get(p.name)}));
 const project=[{name:'main.tex',bytes:new Uint8Array(src)},{name:'fonts/amiri-regular.ttf',bytes:new Uint8Array(font)},...(lua===null?[]:[{name:'qa.lua',bytes:text(lua)}])];for(const f of project)assert.ok(f.bytes.length<=1048576);
 const req={operation:'latex',bibliographyTool:'none',tag:{profile:13,version:22,engineId:'lualatex',tool:'latex',ordinal:1,runtimeIdentity:'b'.repeat(64),nonce:'genuine-lua-collection-limit'},selectedRoot:'main.tex',mainEntry:'main.tex',files:[...r,...project],runtimeResourceNames:r.map(f=>f.name),generatedInputNames:[],outputs:['result.pdf','result.aux','result.log','result-blx.bib'],maxLogBytes:262144,maxOutputBytes:8388608};
 const {reply}=await run('unicode-tex-port-worker.js','luatex',req);fs.writeFileSync(path.join(out,name+'.native.log'),reply.log);
 const count=[...reply.log.matchAll(/PLAIN-NODES nodes=(\d+) actualtext=(\d+) units=(\d+)/g)].map(m=>({physicalNodes:Number(m[1]),actualTextSpans:Number(m[2]),utf16Units:Number(m[3])}));
 const unit=[...reply.log.matchAll(/ACTUAL-COLLECTOR-UNIT visited=(\d+) depth=(\d+)/g)].map(m=>({visited:Number(m[1]),depth:Number(m[2])}));
 const good=!name.endsWith('above');assert.equal(reply.outcome,'native-return',reply.log);assert.equal(reply.nativeReturn,good?0:1,reply.log);
 if(good){assert.ok(reply.files.some(f=>f.name==='result.pdf'));if(kind==='tex-nodes'){assert.equal(count.length,1,reply.log);assert.equal(count[0].physicalNodes,target);assert.equal(count[0].actualTextSpans,0);assert.equal(count[0].utf16Units,0);}if(kind==='unit-depth'){assert.equal(unit.length,1);assert.equal(unit[0].depth,target);assert.equal(unit[0].visited,target+2);assert.match(reply.log,/ACTUAL-COLLECTOR-UNIT success=true/);}}
 else{assert.match(reply.log,/Overclone HarfBuzz unsupported or oversized logical line provenance/);assert.equal(reply.files.length,0);assert.equal(reply.sourceMapPayload,null);assert.equal(reply.sourceMapGeometryPayload,null);if(kind==='unit-depth'){assert.equal(unit[0].depth,target);assert.equal(unit[0].visited,target+2);}}
 const record={name,kind,target,source:project.map(f=>({name:f.name,...pin(f.bytes)})),nativeReturn:reply.nativeReturn,output:reply.files.map(f=>({name:f.name,...pin(f.bytes)})),sourceMap:reply.sourceMapPayload?pin(reply.sourceMapPayload):null,geometry:reply.sourceMapGeometryPayload?pin(reply.sourceMapGeometryPayload):null,physicalObservations:count,actualInstalledCollectorUnitObservations:unit,elapsedMs:reply.elapsedMs,heapBytes:reply.heapBytes,scope:kind==='unit-depth'?'QA controlled real LuaTeX discretionary nodes passed to ACTUAL installed producer/collector functions; not original-font end-to-end':'Genuine original Arabic or plain-TeX-authored RTL and zero-width kern source through unchanged worker'};
 fs.writeFileSync(path.join(out,name+'.json'),JSON.stringify(record,null,2)+'\n');console.log(name+' PASS '+JSON.stringify({count,unit}));
}
async function main(){
 if(process.argv[2]==='--case')return one(process.argv[3]);assert.ok(!fs.existsSync(out));fs.mkdirSync(out,{recursive:true});for(const p of pins)get(p.name);const workerBefore=pin(Buffer.from(source('unicode-tex-port-worker.js')));global.gc?.();const selected=process.argv[2]==='--pilot'?[tuples[0],tuples[1],tuples[4]]:tuples,results=[];
 for(const [name]of selected){const p=spawnSync(process.execPath,[__filename,'--case',name],{env:process.env,encoding:'utf8',timeout:120000});fs.writeFileSync(path.join(out,name+'.driver.log'),p.stdout+(p.stderr||''));assert.equal(p.status,0,name+': '+p.stdout+p.stderr);results.push(JSON.parse(fs.readFileSync(path.join(out,name+'.json'))));console.log(p.stdout.trim());}
 if(process.argv[2]!=='--pilot'){assert.deepEqual(results[0].source,results.at(-1).source);assert.deepEqual(results[0].sourceMap,results.at(-1).sourceMap);assert.deepEqual(results[0].geometry,results.at(-1).geometry);}
 assert.deepEqual(pin(Buffer.from(source('unicode-tex-port-worker.js'))),workerBefore);
 fs.writeFileSync(path.join(out,'receipt.json'),JSON.stringify({status:'pass',closure,worker:{name:'unicode-tex-port-worker.js',...workerBefore},nativeInvocations:results.length,skips:0,scope:'native collector node/depth adjacent limits; plain TeX node vectors plus explicitly-labelled QA actual installed collector-unit depth vectors with visible top-level RTL; deep-only-RTL reachability edge remains separately documented; no product/runtime change',results},null,2)+'\n');
}
main().catch(e=>{console.error(e);process.exitCode=1;});
