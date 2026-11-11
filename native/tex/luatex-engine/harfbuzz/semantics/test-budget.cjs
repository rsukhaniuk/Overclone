'use strict';
// Native candidate QA only. Controlled-node units call the installed collector;
// they are not claims about original-font shaping or browser adoption.
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict'),{createHash}=require('node:crypto');
const [runtime,component,overlay,amiri,out,kind,targetText]=process.argv.slice(2),target=Number(targetText||0);
assert.ok(kind&&out,'runtime component overlay|- Amiri fresh-out kind [target]');assert.ok(!fs.existsSync(out));fs.mkdirSync(out,{recursive:true});
const hash=b=>createHash('sha256').update(b).digest('hex'),pin=b=>({length:b.length,sha256:hash(b)}),exact=(f,p)=>{const b=fs.readFileSync(f);assert.deepEqual(pin(b),{length:p.length,sha256:p.sha256});return b;};
function unit(depth,leading=true,ordinary=false){return `
local function up(fn,want) for i=1,64 do local n,v=debug.getupvalue(fn,i);if not n then break end;if n==want then return v end end end
local function named(fn,want,seen,l)
 if type(fn)~='function'or l>12 or seen[fn]then return end;seen[fn]=true
 for i=1,64 do local n,v=debug.getupvalue(fn,i);if not n then break end;if n==want and type(v)=='function'then return v end end
 for i=1,64 do local n,v=debug.getupvalue(fn,i);if not n then break end;if n~='_ENV'and type(v)=='function'then local f=named(v,want,seen,l+1);if f then return f end end end
end
local real=assert(up(luatexbase.add_to_callback,'realcallbacklist'));local cb
for _,c in ipairs(real.post_linebreak_filter)do if c.description=='luaotfload.harf.finalize_vlist'then cb=c.func end end
local collector=assert(named(assert(cb),'semantic_spans',{},0))
local plugins=assert(up(fonts.handlers.otf.registerplugin,'plugins'))
local producer=assert(named(assert(plugins.harf[2]),'semantic_cluster',{},0))
local function glyph(mark)
 local n=node.new('glyph');n.font=font.current();n.char=${ordinary?65:1576}
 if mark then producer(node.direct.todirect(n),'qa-controlled-real-node','ب',true)end;return n
end
local head=glyph(${ordinary?'false':'true'})
for i=1,${depth} do local d=node.new('disc');d.replace=head;head=d end
${leading&&!ordinary?"local n=glyph(true);n.next=head;head.prev=n;head=n":""}
${ordinary?"local box=node.new('hlist');box.list=glyph(false);head.next=box;box.prev=head":""}
texio.write_nl('term and log','ACTUAL-COLLECTOR-UNIT depth=${depth} leading=${leading} ordinary=${ordinary}')
local result=collector(node.direct.todirect(head))
local literals=0
for n in node.traverse(node.direct.tonode(result))do if n.id==node.id('whatsit')and n.subtype==node.subtype('pdf_literal')and n.data:match('/ActualText<FEFF')then literals=literals+1 end end
assert(literals==${ordinary||(!leading&&overlay==='-')?0:1},'actual installed collector literals')
node.flush_list(node.direct.tonode(result));texio.write_nl('term and log','ACTUAL-COLLECTOR-UNIT success=true')
`;}
(async()=>{
 const pins=JSON.parse(fs.readFileSync(path.join(runtime,'pins.json'))),resources=[];
 for(const p of pins)if(p.name==='lualatex.fmt'||!(/\.(js|wasm|fmt)$/.test(p.name))&&!['biber.data','icudt68l.dat','xetexfontlist.txt'].includes(p.name)&&!p.name.startsWith('notices/'))resources.push([p.name,exact(path.join(runtime,'files',p.name),p)]);
 if(overlay!=='-')for(const p of JSON.parse(fs.readFileSync(path.join(overlay,'manifest.json'))).files){const i=resources.findIndex(([n])=>n===p.name);assert.ok(i>=0);resources[i]=[p.name,exact(path.join(overlay,p.name),p)];}
 const font=fs.readFileSync(amiri);assert.deepEqual(pin(font),{length:548016,sha256:'6988222efdde090e0926cabcaf695c5f551269efbf751b29b097eeb3dbf27e39'});
 const preamble='\\documentclass{article}\n\\usepackage{fontspec}\n'+(kind==='family'?'\\setmainfont{Amiri}\n':'\\setmainfont{amiri-regular.ttf}[Path=fonts/,Renderer=HarfBuzz,Script=Arabic,Language=Arabic]\n');
 let lua=null,src;
 if(kind==='original')src=fs.readFileSync(path.join(__dirname,'../fixtures/harfbuzz.tex'));
 else if(['mixed','lines-pages','latin'].includes(kind))src=fs.readFileSync(path.join(__dirname,'fixtures',kind+'.tex'));
 else if(kind==='nodes'){lua=`luatexbase.add_to_callback('post_linebreak_filter',function(head) for line in node.traverse_id(node.id('hlist'),head)do local n=0;for p in node.traverse(line.list)do n=n+1 end;texio.write_nl('term and log','PHYSICAL-NODES '..n)end;return head end,'overclone.budget.readonly')`;src=Buffer.from(preamble+'\\directlua{dofile("qa.lua")}\n\\begin{document}\n\\noindent\\textdir TRT ب'+'\\kern0pt%\n'.repeat(target-7)+'\\par\n\\end{document}\n');}
 else if(['depth','deep-only','ordinary'].includes(kind)){lua=unit(target,kind!=='deep-only',kind==='ordinary');src=Buffer.from(preamble+'\\begin{document}\n\\noindent\\textdir TRT ب\\par\n\\directlua{dofile("qa.lua")}\n\\end{document}\n');}
 else if(kind==='units')src=Buffer.from(preamble+'\\begin{document}\n\\noindent\\textdir TRT '+'ب'.repeat(target)+'\\par\n\\end{document}\n');
 else if(kind==='family')src=Buffer.from(preamble+'\\begin{document}\nFamily lookup ب\n\\end{document}\n');
 else throw Error('unknown case');
 const project=[['main.tex',src],['fonts/amiri-regular.ttf',font],...(lua?[['qa.lua',Buffer.from(lua)]]:[])];
 for(const[n,b]of project){assert.ok(b.length<=1048576);fs.mkdirSync(path.join(out,path.dirname(n)),{recursive:true});fs.writeFileSync(path.join(out,n),b);}
 const loader=fs.readFileSync(path.join(component,'luatex.js')),wasm=fs.readFileSync(path.join(component,'luatex.wasm')),logs=[];
 const m=await require(path.join(component,'luatex.js'))({noInitialRun:true,wasmBinary:wasm,instantiateWasm(i,d){const w=new WebAssembly.Instance(new WebAssembly.Module(wasm),i);d(w);return w.exports;},print:s=>logs.push(s),printErr:s=>logs.push(s)}),call=(n,t=[],a=[],r='number')=>m.ccall('oc_luatex_'+n,r,t,a);
 assert.equal(call('configure',['string','string'],['main.tex','p14-budget-native-qa']),0);assert.equal(m.ccall('oc_mem_configure','number',['number','number','number'],[1,1048575,1]),1);
 for(const[k,rows]of [[0,resources],[1,project]])for(const[n,b]of rows){const p=m._malloc(b.length||1);m.HEAPU8.set(b,p);const c=call('add_file',['number','string','number','number'],[k,n,p,b.length]);m._free(p);assert.equal(c,0,n);}
 const code=call('compile_latex'),error=call('error',[],[],'string'),log=logs.join('\n');fs.writeFileSync(path.join(out,'stdout.log'),log);
 const outputs=[],maps=[];
 for(const[stem,name]of [['','synctex.raw'],['geometry_','geometry.json']]){const n=m.ccall('oc_synctex_'+stem+'length','number',['number'],[1]);if(n){const p=m._malloc(n);assert.equal(m.ccall('oc_synctex_'+stem+'copy','number',['number','number','number'],[1,p,n]),n);const b=Buffer.from(m.HEAPU8.slice(p,p+n));m._free(p);fs.writeFileSync(path.join(out,name),b);maps.push({name,...pin(b)});}}
 for(const name of m.FS.readdir('/work/output'))if(/\.(pdf|aux|log)$/.test(name)){const b=Buffer.from(m.FS.readFile('/work/output/'+name));fs.writeFileSync(path.join(out,name),b);outputs.push({name,...pin(b)});}
 const expected=kind==='family'?null:kind==='deep-only'?(overlay==='-'?0:1):kind==='nodes'?(target>65536?1:0):kind==='units'?(target>16384?1:0):kind==='depth'?(target>32?1:0):0;
 const result={schemaVersion:1,status:'observed',scope:'native candidate QA; controlled-depth units call actual installed producer and collector, not original-font E2E',kind,target,code,expected,error,testSource:pin(fs.readFileSync(__filename)),loader:pin(loader),wasm:pin(wasm),resourceClosure:hash(Buffer.from(resources.map(([n,b])=>n+'\t'+b.length+'\t'+hash(b)).join('\n'))),resourceCount:resources.length,resourceBytes:resources.reduce((a,[,b])=>a+b.length,0),source:project.map(([name,b])=>({name,...pin(b)})),outputs,maps,heapBytes:m.HEAPU8.length,maxRssKiB:process.resourceUsage().maxRSS};
 fs.writeFileSync(path.join(out,'receipt.json'),JSON.stringify(result,null,2)+'\n');
 if(expected!==null){assert.equal(code,expected,log.slice(-3500));if(code){assert.match(log,/Overclone HarfBuzz unsupported or oversized logical line provenance/);assert.equal(maps.length,0);assert.equal(outputs.filter(p=>p.name.endsWith('.pdf')).length,0);}else{assert.ok(outputs.some(p=>p.name.endsWith('.pdf')));if(['depth','ordinary'].includes(kind))assert.match(log,/ACTUAL-COLLECTOR-UNIT success=true/);if(kind==='nodes')assert.match(log,new RegExp('PHYSICAL-NODES '+target+'(?:\\D|$)'));}}
 if(kind!=='family')result.status='pass';fs.writeFileSync(path.join(out,'receipt.json'),JSON.stringify(result,null,2)+'\n');console.log(JSON.stringify({status:result.status,kind,target,code,error}));
})().catch(e=>{fs.writeFileSync(path.join(out,'assertion.log'),e.stack);console.error(e);process.exitCode=1;});
