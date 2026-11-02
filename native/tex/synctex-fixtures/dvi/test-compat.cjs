'use strict';
// Exact-byte old approved pdfTeX vs new opt-in DVI producer. Both execute actual
// native WASM; fixture clock is an explicit input, never output normalization.
const vm=require('node:vm');
const {fs,path,assert,hashBytes,engine}=require('./runtime.cjs');
const out=process.env.RESULTS;assert.ok(out);
const mode=process.argv[2]||'pdf',enabled=process.env.MAPS!=='0';
const sourceFile=process.env.SOURCE_FILE||path.join(__dirname,'../../synctex-producer/tests/fixtures/positive/main.tex');
let source=fs.readFileSync(sourceFile);
if(mode==='dvi')source=Buffer.concat([Buffer.from('\\pdfoutput=0\n'),source]);
const reference=process.env.REFERENCE==='1';
async function oldEngine(){
 const resources='/inputs/suite/files';
 return new Promise((resolve,reject)=>{
  let finished=false;
  const timer=setTimeout(()=>{if(!finished)reject(new Error('reference engine deadline'));},30000);
  const localRequire=name=>name==='node:fs'?new Proxy(fs,{get(target,key){
   if(key==='readFileSync')return(file,options)=>fs.readFileSync(file===path.join(resources,'swiftlatexpdftex.wasm')?path.join(resources,'engine.wasm'):file,options);
   return target[key];
  }}):require(name);
  const processView=new Proxy(process,{get(target,key){return key==='env'?{LC_ALL:'C',TZ:'UTC'}:target[key];}});
  const context=vm.createContext({require:localRequire,process:processView,console,Date,performance,TextDecoder,TextEncoder,WebAssembly,setTimeout,clearTimeout,__dirname:resources,__filename:path.join(resources,'engine.js')});
  context.self=context;context.postMessage=()=>{};
  context.fetch=()=>{throw new Error('fixture network disabled');};context.XMLHttpRequest=class{constructor(){throw new Error('fixture network disabled');}};
  context.sourceBytes=source;context.mode=mode;context.mapEnabled=enabled;
  context.done=result=>{finished=true;clearTimeout(timer);resolve(result);};
  context.failed=e=>{finished=true;clearTimeout(timer);reject(e);};
  const suffix=String.raw`
Module.locateFile=()=>require('node:path').join('/inputs/suite/files','engine.wasm');
kpse_find_file_impl=()=>0;kpse_find_pk_impl=()=>0;
Module.postRun=function(){try{
 FS.chdir('/work');const host=require('node:fs'),p=require('node:path');
 for(const n of host.readdirSync('/inputs/suite/files')){
  const f=p.join('/inputs/suite/files',n);if(n.endsWith('.js')||n.endsWith('.wasm')||!host.statSync(f).isFile())continue;
  FS.writeFile('/work/'+n,new Uint8Array(host.readFileSync(f)));
 }
 FS.writeFile('/work/swiftlatexpdftex.fmt',new Uint8Array(host.readFileSync('/inputs/suite/files/pdflatex.fmt')));
 FS.writeFile('/work/main.tex',new Uint8Array(sourceBytes));
 if(cwrap('oc_mem_configure','number',['number','number','number'])(mapEnabled?1:0,mapEnabled?1048575:0,801)!==1)throw new Error('reference config');
 cwrap('setMainEntry','number',['string'])('main.tex');const rc=_compileLaTeX();
 const files={};for(const ext of ['pdf','dvi','log','aux'])if(FS.analyzePath('/work/main.'+ext).exists)files[ext]=new Uint8Array(FS.readFile('/work/main.'+ext));
 const n=cwrap('oc_synctex_length','number',['number'])(801);let raw=new Uint8Array();
 if(n){const alloc=cwrap('malloc','number',['number'])(n);if(cwrap('oc_synctex_copy','number',['number','number','number'])(801,alloc,n)!==n)throw new Error('reference copy');raw=HEAPU8.slice(alloc,alloc+n);cwrap('free',null,['number'])(alloc);}
 done({rc,files,raw});
}catch(e){failed(e);}};
`;
  try{vm.runInContext(fs.readFileSync('/inputs/suite/files/engine.js','utf8')+suffix,context,{filename:'retained-pdftex-compat.vm.js'});}catch(e){clearTimeout(timer);reject(e);}
 });
}
(async()=>{
 fs.mkdirSync(out,{recursive:true});let result,identities;
 if(reference){result=await oldEngine();identities={loader:hashBytes(fs.readFileSync('/inputs/suite/files/engine.js')),wasm:hashBytes(fs.readFileSync('/inputs/suite/files/engine.wasm')),format:hashBytes(fs.readFileSync('/inputs/suite/files/pdflatex.fmt')),suitePins:hashBytes(fs.readFileSync('/inputs/suite/pins.json'))};}
 else{
  const lines=[],x=await engine(lines);const {m,call,seed,map}=x;identities={...x.identities,initialSeedFormat:x.identities.format,format:hashBytes(fs.readFileSync('/inputs/suite/files/pdflatex.fmt')),formatAlias:'swiftlatexpdftex.fmt (explicit final PDF-format bytes)'};
  // Match the approved VM reference's explicit local resource aliases. No
  // basename fallback for project files and no network/host lookup by TeX.
  for(const n of m.FS.readdir('/tex'))if(n!=='.'&&n!=='..'&&!m.FS.isDir(m.FS.stat('/tex/'+n).mode))m.FS.symlink('/tex/'+n,'/work/'+n);
  seed('/tex/pdflatex.fmt',fs.readFileSync('/inputs/suite/files/pdflatex.fmt'));
  seed('/tex/swiftlatexpdftex.fmt',fs.readFileSync('/inputs/suite/files/pdflatex.fmt'));
  seed('/work/main.tex',source);
  assert.equal(call('oc_mem_configure',['number','number','number'],[enabled?1:0,enabled?1048575:0,801]),1);
  assert.equal(call('setMainEntry',['string'],['main.tex']),0);
  const rc=call('compileLaTeX');const files={};for(const ext of ['pdf','dvi','log','aux'])if(m.FS.analyzePath('/work/main.'+ext).exists)files[ext]=Buffer.from(m.FS.readFile('/work/main.'+ext));
  result={rc,files,raw:map(801)};fs.writeFileSync(path.join(out,'console.txt'),lines.join('\n'));
 }
 assert.equal(result.rc,0);
 const hashes={};for(const [ext,b]of Object.entries(result.files)){const bytes=Buffer.from(b);fs.writeFileSync(path.join(out,'main.'+ext),bytes);hashes[ext]={length:bytes.length,sha256:hashBytes(bytes)};}
 const raw=Buffer.from(result.raw);fs.writeFileSync(path.join(out,'map.synctex'),raw);
 if(enabled&&(!reference||mode==='pdf'))assert.ok(raw.length);else assert.equal(raw.length,0);
 if(process.env.COMPARE_TO){
  for(const ext of ['pdf','dvi','log','aux']){const other=path.join(process.env.COMPARE_TO,'main.'+ext);if(fs.existsSync(other))assert.deepEqual(fs.readFileSync(path.join(out,'main.'+ext)),fs.readFileSync(other),'exact '+ext);}
  if(mode==='pdf')assert.deepEqual(raw,fs.readFileSync(path.join(process.env.COMPARE_TO,'map.synctex')),'exact accepted PDF raw map');
 }
 const receipt={schemaVersion:1,status:'pass',mode,mapsEnabled:enabled,reference,sourceSha256:hashBytes(source),identities,outputs:hashes,map:{length:raw.length,sha256:hashBytes(raw)},comparison:process.env.COMPARE_TO||null,clock:'Date injected before initialization into both Emscripten and reference VM; see fixed-clock.cjs binding. No output byte normalization.'};
 fs.writeFileSync(path.join(out,'receipt.json'),JSON.stringify(receipt,null,2)+'\n');console.log(JSON.stringify(receipt));
})().catch(e=>{console.error(e);process.exitCode=1;});
