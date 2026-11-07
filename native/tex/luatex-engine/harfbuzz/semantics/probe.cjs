'use strict';
// Test-only native candidate loader. All source bytes remain unchanged.
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto'),assert=require('node:assert/strict');
const [runtime,component,format,source,upstream,overlay,out]=process.argv.slice(2);
if(!out)throw Error('usage: node probe.cjs RUNTIME COMPONENT FORMAT SOURCE HB_SOURCE OVERLAY|- NEW_OUT');
assert.ok(!fs.existsSync(out));fs.mkdirSync(out,{recursive:true});
const hash=b=>crypto.createHash('sha256').update(b).digest('hex'),pin=b=>({length:b.length,sha256:hash(b)});
const exact=(file,p)=>{const b=fs.readFileSync(file);assert.deepEqual(pin(b),{length:p.length,sha256:p.sha256});return b;};
(async()=>{
  const resources=[];
  const pins=JSON.parse(fs.readFileSync(path.join(runtime,'pins.json')));
  for(const p of pins)if(!/\.(js|wasm|fmt)$/.test(p.name)&&!p.name.startsWith('notices/')&&!['icudt68l.dat','xetexfontlist.txt'].includes(p.name))resources.push([p.name,exact(path.join(runtime,'files',p.name),p)]);
  resources.push(['lualatex.fmt',fs.readFileSync(format)]);
  if(overlay!=='-'){
    const manifest=JSON.parse(fs.readFileSync(path.join(overlay,'manifest.json')));
    for(const p of manifest.files){const i=resources.findIndex(([n])=>n===p.name);assert.ok(i>=0);resources[i]=[p.name,exact(path.join(overlay,p.name),p)];}
  }
  const hp=JSON.parse(fs.readFileSync(path.join(__dirname,'../fixtures/upstream-pins.json')));
  resources.push(['harfbuzz.lua',exact(path.join(upstream,'src/harfbuzz.lua'),hp['src/harfbuzz.lua'])]);
  const project=[['main.tex',fs.readFileSync(source)],['fonts/amiri-regular.ttf',exact(path.join(upstream,'fonts/amiri-regular.ttf'),hp['fonts/amiri-regular.ttf'])]];
  const loader=fs.readFileSync(path.join(component,'luatex.js')),wasm=fs.readFileSync(path.join(component,'luatex.wasm')),logs=[];
  const m=await require(path.join(component,'luatex.js'))({noInitialRun:true,wasmBinary:wasm,instantiateWasm(i,d){const w=new WebAssembly.Instance(new WebAssembly.Module(wasm),i);d(w);return w.exports;},print:s=>logs.push(s),printErr:s=>logs.push(s)});
  const call=(n,t=[],a=[],r='number')=>m.ccall('oc_luatex_'+n,r,t,a);
  assert.equal(m._oc_lua_harfbuzz_abi(),1);assert.equal(call('configure',['string','string'],['main.tex','r1-hb-semantics']),0);
  const semanticsAbi=typeof m._oc_lua_text_semantics_abi==='function'?m._oc_lua_text_semantics_abi():0;
  assert.ok(semanticsAbi===0||semanticsAbi===1);
  assert.equal(m.ccall('oc_mem_configure','number',['number','number','number'],[1,1048575,1]),1);
  for(const [kind,rows]of [[0,resources],[1,project]])for(const [n,b]of rows){const p=m._malloc(b.length||1);m.HEAPU8.set(b,p);const code=call('add_file',['number','string','number','number'],[kind,n,p,b.length]);m._free(p);assert.equal(code,0,n);}
  const code=call('compile_latex'),error=call('error',[],[],'string'),budget=JSON.parse(call('budget_json',[],[],'string')),outputs=[];
  for(const [stem,name]of [['','synctex.raw'],['geometry_','geometry.json']]){
    const n=m.ccall('oc_synctex_'+stem+'length','number',['number'],[1]);
    if(n){const p=m._malloc(n);assert.equal(m.ccall('oc_synctex_'+stem+'copy','number',['number','number','number'],[1,p,n]),n);const b=Buffer.from(m.HEAPU8.slice(p,p+n));m._free(p);fs.writeFileSync(path.join(out,name),b);outputs.push({name,...pin(b)});}
  }
  for(const name of m.FS.readdir('/work/output'))if(/\.(pdf|log|aux|fls)$/.test(name)){const b=Buffer.from(m.FS.readFile('/work/output/'+name));fs.writeFileSync(path.join(out,name),b);outputs.push({name,...pin(b)});}
  fs.writeFileSync(path.join(out,'stdout.log'),logs.join('\n')+'\n');
  const unchanged=Buffer.compare(project[0][1],m.FS.readFile('/work/project/main.tex'))===0;
  const receipt={schemaVersion:1,scope:'native resource candidate only; no product adoption',code,error,budget,semanticsAbi,source:{path:source,...pin(project[0][1]),unchanged},loader:pin(loader),wasm:pin(wasm),resources:resources.map(([name,b])=>({name,...pin(b)})),outputs,memory:{maxRssKiB:process.resourceUsage().maxRSS,heapBytes:m.HEAPU8.length}};
  fs.writeFileSync(path.join(out,'receipt.json'),JSON.stringify(receipt,null,2)+'\n');assert.equal(code,0,logs.slice(-20).join('\n'));assert.ok(unchanged);console.log(JSON.stringify({code,error,unchanged,outputs}));
})().catch(e=>{fs.writeFileSync(path.join(out,'error.log'),String(e.stack));console.error(e);process.exitCode=1;});
