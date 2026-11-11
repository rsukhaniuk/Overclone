'use strict';
// Native selected-output names and actual128+128 input admission are separate
// from the application's128 generated carry quota and the VFS new-node quota.
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict'),{createHash}=require('node:crypto');
const [component,runtime,out,targetText,limitText]=process.argv.slice(2),target=Number(targetText),limit=Number(limitText||256);
assert.ok(!fs.existsSync(out));fs.mkdirSync(out,{recursive:true});
const hash=b=>createHash('sha256').update(b).digest('hex'),pin=b=>({length:b.length,sha256:hash(b)});
(async()=>{
 const wasm=fs.readFileSync(path.join(component,'luatex.wasm')),loader=fs.readFileSync(path.join(component,'luatex.js')),logs=[];
 const m=await require(path.join(component,'luatex.js'))({wasmBinary:wasm,instantiateWasm(i,d){const w=new WebAssembly.Instance(new WebAssembly.Module(wasm),i);d(w);return w.exports;},print:s=>logs.push(s),printErr:s=>logs.push(s)}),call=(n,t=[],a=[],r='number')=>m.ccall('oc_luatex_'+n,r,t,a);
 if(limit===256)assert.equal(m._oc_lua_output_declaration_limit(),256);else assert.equal(typeof m._oc_lua_output_declaration_limit,'undefined');
 assert.equal(call('configure',['string','string'],['main.tex','p14-native-declarations']),0);
 const declared=[];let finalCode=0;
 for(let i=10;i<target;i++){const name='parts/chapter'+i+'.aux';finalCode=call('declare_output',['string'],[name]);assert.equal(finalCode,i<limit?0:-4);declared.push({name,nativeReturn:finalCode});if(finalCode)break;}
 const inputs=[];let compileCode=null;
 if(process.argv[7]==='--work'){
  assert.ok(target<=limit);const pins=JSON.parse(fs.readFileSync(path.join(runtime,'pins.json')));
  for(const p of pins)if(p.name==='lualatex.fmt'||!(/\.(js|wasm|fmt)$/.test(p.name))&&!['biber.data','icudt68l.dat','xetexfontlist.txt'].includes(p.name)&&!p.name.startsWith('notices/')){const b=fs.readFileSync(path.join(runtime,'files',p.name));assert.deepEqual(pin(b),{length:p.length,sha256:p.sha256});inputs.push([0,p.name,b]);}
  const includes=[];
  for(let i=0;i<127;i++){const name='parts/chapter'+i+'.tex';includes.push('\\input{'+name+'}');inputs.push([1,name,Buffer.from('% original captured '+i+'\n')]);}
  const source=Buffer.from('\\documentclass{article}\n\\begin{document}\nNative work budget.\\ref{known}\n'+includes.join('\n')+'\n\\end{document}\n');inputs.push([1,'main.tex',source]);
  inputs.push([2,'result.aux',Buffer.from('\\relax\n\\newlabel{known}{{1}{1}}\n')]);
  for(let i=1;i<128;i++)inputs.push([2,'parts/chapter'+(i+9)+'.aux',Buffer.from('\\relax\n')]);
  assert.equal(inputs.filter(([k])=>k===1).length,128);assert.equal(inputs.filter(([k])=>k===2).length,128);
  assert.equal(m.ccall('oc_mem_configure','number',['number','number','number'],[1,1048575,1]),1);
  for(const[k,n,b]of inputs){const p=m._malloc(b.length||1);m.HEAPU8.set(b,p);const c=call('add_file',['number','string','number','number'],[k,n,p,b.length]);m._free(p);assert.equal(c,0,n);}
  compileCode=call('compile_latex');fs.writeFileSync(path.join(out,'native.log'),logs.join('\n'));assert.equal(compileCode,0,logs.slice(-20).join('\n'));assert.ok(!logs.some(s=>s.includes('undefined references')));
  assert.ok(call('output_size',['number'],[0])>0);assert.ok(m.ccall('oc_synctex_length','number',['number'],[1])>0);assert.deepEqual(Buffer.from(m.FS.readFile('/work/project/main.tex')),source);
 }
 const receipt={status:'pass',scope:'native output declaration name capacity, with optional genuine128captured+128carry compile; generated carry policy remains128',target,limit,declaredIncludingTenDefaults:Math.min(target,limit),finalCode,compileCode,loader:pin(loader),wasm:pin(wasm),testSource:pin(fs.readFileSync(__filename)),declarations:declared,inputs:inputs.map(([kind,name,b])=>({kind,name,...pin(b)})),budget:JSON.parse(call('budget_json',[],[],'string'))};
 fs.writeFileSync(path.join(out,'receipt.json'),JSON.stringify(receipt,null,2)+'\n');console.log(JSON.stringify({status:'pass',target,limit,finalCode,compileCode}));
})().catch(e=>{console.error(e);process.exitCode=1;});
