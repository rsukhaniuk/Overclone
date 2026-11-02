'use strict';
// Native regression tooling only. Uses the UNCHANGED CompilerSuiteFixtures and
// exact supplied profile9 bytes. No application orchestration/policy or mocks.
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict'),crypto=require('node:crypto');
const [kind,operation,out,carry]=process.argv.slice(2);assert.ok(['dvi','xe','lua'].includes(kind));assert.ok(['latex','bibtex','convert'].includes(operation));assert.ok(out&&!fs.existsSync(out));fs.mkdirSync(out,{recursive:true});
const runtime=process.env.RUNTIME||'/control/profile9-runtime',dir=path.join(runtime,'files'),root='тези/my thesis.tex',tag=Number(process.env.PASS_TAG||501);
const hash=b=>crypto.createHash('sha256').update(b).digest('hex');
let resources;try{resources=JSON.parse(fs.readFileSync(path.join(runtime,'pins.json')));}catch(error){throw new Error('Invalid supplied runtime pins',{cause:error});}
const project={};for(const [n,p]of[[root,kind==='dvi'?'main-latin.tex':'main-unicode.tex'],['chapter.tex','chapter.tex'],['references.bib','references.bib'],['figure.xbb','figure.xbb']])project[n]=fs.readFileSync(path.join(__dirname,'../../../tests/CompilerSuiteFixtures',p));project['figure.png']=fs.readFileSync(path.join(__dirname,'../../../src/Client/wwwroot/icons/icon-192.png'));
const names=kind==='dvi'?['dvi-map-engine.js','dvi-map-engine.wasm']:['xetex.js','xetex.wasm'];
if(kind==='lua'&&operation==='latex')names.splice(0,2,'luatex.js','luatex.wasm');
if(operation==='bibtex')names.splice(0,2,'xetex.js','xetex.wasm');
if(operation==='convert')names.splice(0,2,kind==='xe'?'xdv-map-converter.js':'dvi-map-converter.js',kind==='xe'?'xdv-map-converter.wasm':'dvi-map-converter.wasm');
(async()=>{
 const moduleDir=operation==='convert'&&process.env.CONVERTER?process.env.CONVERTER:kind==='lua'&&operation==='latex'&&process.env.LUA_CANDIDATE?process.env.LUA_CANDIDATE:dir;
 if(operation==='convert'&&moduleDir!==dir)names.splice(0,2,'oc-dvi.js','oc-dvi.wasm');
 const lines=[],wasm=fs.readFileSync(path.join(moduleDir,names[1])),loader=fs.readFileSync(path.join(moduleDir,names[0]));let instantiated=0;
 const m=await require(path.join(moduleDir,names[0]))({noInitialRun:true,print:s=>lines.push(s),printErr:s=>lines.push(s),instantiateWasm(imports,done){++instantiated;const i=new WebAssembly.Instance(new WebAssembly.Module(wasm),imports);done(i);return i.exports;}});assert.equal(instantiated,1);
 const call=(n,t=[],a=[])=>m.ccall(n,'number',t,a);
 const alloc=b=>{const p=m._malloc(b.length||1);assert.ok(p);m.HEAPU8.set(b,p);return p;};
 const string=s=>alloc(Buffer.from(s+'\0'));
 const readToken=(len,copy,t,max)=>{const n=call(len,['number'],[t]);if(!n)return Buffer.alloc(0);assert.ok(n<=max);const p=m._malloc(n);assert.equal(call(copy,['number','number','number'],[t,p,n]),n);const b=Buffer.from(m.HEAPU8.subarray(p,p+n));m._free(p);return b;};
 const outputs={};let rc,raw=Buffer.alloc(0),geometry=Buffer.alloc(0);
 const state={};if(carry)for(const n of fs.readdirSync(carry)){if(n.endsWith('.carry')&&!n.endsWith('.blg.carry'))state[n.slice(0,-6)]=fs.readFileSync(path.join(carry,n));}
 if(operation==='convert'){
  const add=(n,b,r)=>{const p=string(n),q=alloc(b);assert.equal(m._oc_dvi_add_file(p,q,b.length,r),0,n);m._free(p);m._free(q);};
  for(const x of resources){if(/\.(js|wasm|fmt)$/.test(x.name)||x.name.includes('/'))continue;const b=fs.readFileSync(path.join(dir,x.name));assert.equal(hash(b),x.sha256);add(x.name,b,1);}
  for(const[n,b]of Object.entries(project))add(n,b,0);
  const ir=fs.readFileSync(path.join(carry,kind==='xe'?'output.xdv':'output.dvi'));add('input.bin',ir,0);assert.equal(m._oc_dvi_geometry_configure(1,tag),1);const p=string('input.bin');rc=m._oc_dvi_run(p);m._free(p);
  outputs.pdf=Buffer.from(m.HEAPU8.subarray(m._oc_dvi_pdf_data(),m._oc_dvi_pdf_data()+m._oc_dvi_pdf_size()));outputs.log=Buffer.from(m.HEAPU8.subarray(m._oc_dvi_log_data(),m._oc_dvi_log_data()+m._oc_dvi_log_size()));geometry=Buffer.from(m.HEAPU8.subarray(m._oc_dvi_geometry_data(),m._oc_dvi_geometry_data()+m._oc_dvi_geometry_size()));
 }else if(kind==='lua'&&operation==='latex'){
  assert.equal(call('oc_luatex_abi'),Number(process.env.LUA_ABI||5));assert.equal(call('oc_luatex_configure',['string','string'],[root,'graphics-native-fixture']),0);
  assert.equal(call('oc_luatex_declare_output',['string'],['chapter.aux']),0);
  const seed=(k,n,b)=>{const p=alloc(b);assert.equal(call('oc_luatex_add_file',['number','string','number','number'],[k,n,p,b.length]),0,n);m._free(p);};
  for(const x of resources){if(/\.(js|wasm|fmt)$/.test(x.name)||x.name.startsWith('notices/'))continue;const b=fs.readFileSync(path.join(dir,x.name));assert.equal(hash(b),x.sha256);seed(0,x.name,b);}seed(0,'lualatex.fmt',fs.readFileSync(path.join(dir,'lualatex.fmt')));
  for(const [n,b]of Object.entries(project))seed(1,n,b);for(const[n,b]of Object.entries(state))seed(2,n,b);
  assert.equal(call('oc_mem_configure',['number','number','number'],[1,1048575,tag]),1);rc=call('oc_luatex_compile_latex');
  for(const [n,index]of[['pdf',0],['log',1],['aux',3],['bbl',6],['blg',7]]){const z=call('oc_luatex_output_size',['number'],[index]);if(z>0){const p=m._malloc(z);assert.equal(call('oc_luatex_copy_output',['number','number','number'],[index,p,z]),z);outputs[n]=Buffer.from(m.HEAPU8.subarray(p,p+z));m._free(p);}}
  for(const n of ['result.aux','chapter.aux','result.bbl','result.blg']){const z=call('oc_luatex_named_output_size',['string'],[n]);if(z>0){const p=m._malloc(z);assert.equal(call('oc_luatex_copy_named_output',['string','number','number'],[n,p,z]),z);fs.writeFileSync(path.join(out,n+'.carry'),Buffer.from(m.HEAPU8.subarray(p,p+z)));m._free(p);}}
  raw=readToken('oc_synctex_length','oc_synctex_copy',tag,1048575);geometry=readToken('oc_synctex_geometry_length','oc_synctex_geometry_copy',tag,131072);
 }else{
  m.FS.mkdir('/work');m.FS.mkdir('/tex');m.FS.chdir('/work');const seed=(n,b)=>{m.FS.mkdirTree(path.posix.dirname(n));m.FS.writeFile(n,b);};
  for(const x of resources){if(/\.(js|wasm|fmt)$/.test(x.name)||x.name.startsWith('notices/'))continue;const b=fs.readFileSync(path.join(dir,x.name));assert.equal(hash(b),x.sha256);seed('/tex/'+x.name,b);}
  for(const[n,b]of Object.entries(project))seed('/work/'+n,b);
  if(operation==='latex'){const fmt=kind==='dvi'?'latex-dvi.fmt':'xelatex.fmt';seed('/tex/'+(kind==='dvi'?'swiftlatexpdftex.fmt':'xelatex.fmt'),fs.readFileSync(path.join(dir,fmt)));}
  for(const[n,b]of Object.entries(state))seed('/work/'+n,b);
  const entry=operation==='bibtex'?(kind==='lua'?'result.tex':'my thesis.tex'):(kind==='dvi'?'"'+root+'"':root);
  assert.equal(call('setMainEntry',['string'],[entry]),0);assert.equal(call('oc_mem_configure',['number','number','number'],[1,1048575,tag]),1);rc=call(operation==='bibtex'?'compileBibtex':'compileLaTeX');
  for(const n of m.FS.readdir('/work')){if(/\.(aux|bbl|blg)$/.test(n))fs.writeFileSync(path.join(out,n+'.carry'),Buffer.from(m.FS.readFile('/work/'+n)));if(/\.(dvi|xdv|log|pdf)$/.test(n))outputs[n.split('.').pop()]=Buffer.from(m.FS.readFile('/work/'+n));}
  raw=readToken('oc_synctex_length','oc_synctex_copy',tag,1048575);
 }
 for(const[n,b]of Object.entries(outputs))fs.writeFileSync(path.join(out,'output.'+n),b);fs.writeFileSync(path.join(out,'raw.synctex'),raw);fs.writeFileSync(path.join(out,'geometry.json'),geometry);fs.writeFileSync(path.join(out,'console.txt'),lines.join('\n'));
 let g=null;if(geometry.length){try{g=JSON.parse(geometry);}catch(error){throw new Error('Invalid actual native geometry',{cause:error});}}
 const receipt={schemaVersion:1,kind,operation,nativeReturn:rc,tag,inputs:Object.fromEntries(Object.entries(project).map(([n,b])=>[n,{length:b.length,sha256:hash(b)}])),state:Object.fromEntries(Object.entries(state).map(([n,b])=>[n,hash(b)])),candidate:{loader:hash(loader),wasm:hash(wasm)},outputs:Object.fromEntries(Object.entries(outputs).map(([n,b])=>[n,{length:b.length,sha256:hash(b)}])),rawMap:{length:raw.length,sha256:hash(raw)},geometry:{length:geometry.length,sha256:hash(geometry),statuses:g?.pages.map(p=>({page:p.page,status:p.status,reason:p.reason}))||null}};
 fs.writeFileSync(path.join(out,'receipt.json'),JSON.stringify(receipt,null,2)+'\n');assert.equal(rc,0);console.log(JSON.stringify(receipt));
})().catch(e=>{console.error(e);process.exitCode=1;});
