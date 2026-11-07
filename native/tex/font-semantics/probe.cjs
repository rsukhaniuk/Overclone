'use strict';
// Native-only QA. The project is mounted byte-for-byte. Every source/resource
// input is fingerprinted; no fixture-controlled strings enter the implementation.
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto'),assert=require('node:assert/strict');
const [runtime,component,operation,fixture,format,out,overlay]=process.argv.slice(2);
if(!out||!['format-control','format','compile','compile-reject','format-reject','compile-unsupported'].includes(operation))
  throw Error('usage: node probe.cjs RUNTIME COMPONENT format-control|format|compile FIXTURE FORMAT|- OUT [OVERLAY]');
if(fs.existsSync(out))throw Error('fresh output required');fs.mkdirSync(out,{recursive:true});
const hash=b=>crypto.createHash('sha256').update(b).digest('hex'),pin=b=>({length:b.length,sha256:hash(b)});
const manifest=JSON.parse(fs.readFileSync(path.join(component,'manifest.json')));
function componentBytes(n){const b=fs.readFileSync(path.join(component,n));assert.deepEqual(pin(b),manifest.outputs[n]);return b;}
const loader=componentBytes('engine.js'),wasm=componentBytes('engine.wasm');
const pins=JSON.parse(fs.readFileSync(path.join(runtime,'pins.json'))),baseNames=new Set(pins.map(p=>p.name));
const extras=overlay?JSON.parse(fs.readFileSync(path.join(overlay,'manifest.json'))).files:[];
for(const p of extras){assert.ok(!baseNames.has(p.name),'additive resource overlay');pins.push(p);}
const used=[],logs=[];
const print=s=>{assert.ok(logs.length<50000);logs.push(String(s));};
(async()=>{
 const m=await require(path.join(component,'engine.js'))({noInitialRun:true,
  instantiateWasm(imports,done){const i=new WebAssembly.Instance(new WebAssembly.Module(wasm),imports);done(i);return i.exports;},print,printErr:print});
 const F=m.FS,seed=(n,b)=>{F.mkdirTree(path.posix.dirname(n));F.writeFile(n,b);};
 F.mkdir('/tex');F.mkdir('/work');F.chdir('/work');
 assert.equal(m._oc_font_semantics_abi(),1);
 const generating=operation.startsWith('format');
 for(const p of pins){
  if(/\.(js|wasm|fmt|ttf|otf)$/.test(p.name)||p.name.startsWith('notices/'))continue;
  const root=baseNames.has(p.name)?runtime:overlay;
  let b=fs.readFileSync(path.join(root,'files',p.name));assert.deepEqual(pin(b),{length:p.length,sha256:p.sha256});
  used.push({name:p.name,...pin(b)});
  if(operation==='format'&&p.name==='latex.ltx'){
   const s=b.toString();assert.equal(s.split('\\dump\n').length,2);
   b=Buffer.from(s.replace('\\dump\n',fs.readFileSync(path.join(__dirname,'format.tex'),'utf8')+'\n\\dump\n'));
   fs.writeFileSync(path.join(out,'private-kernel.sha256'),hash(b)+'\n');
  }
  seed('/tex/'+p.name,b);
 }
 const source=fs.readFileSync(fixture);
 if(generating)seed('/tex/pdflatex.ini',source);
 else{
  seed('/work/main.tex',source);seed('/tex/swiftlatexpdftex.fmt',fs.readFileSync(format));
  assert.equal(m.ccall('setMainEntry','number',['string'],['main.tex']),0);
  assert.equal(m._oc_mem_configure(1,1048575,71),1);
 }
 const status=generating?m._compileFormat():m._compileLaTeX();
 const semantic={abi:m._oc_font_semantics_abi(),status:m._oc_font_semantics_status(),
  spans:m._oc_font_semantics_spans(),copies:m._oc_font_semantics_copies(),
  reconstructions:m._oc_font_semantics_reconstructions(),current:m._oc_font_semantics_current()};
 const outputs=[];
 for(const n of F.readdir('/work'))if(/\.(pdf|dvi|log|aux|fmt)$/.test(n)){
  const b=Buffer.from(F.readFile('/work/'+n));fs.writeFileSync(path.join(out,n),b);outputs.push({name:n,...pin(b)});
 }
 const mapLength=m._oc_synctex_length(71),nativeMap={status:m._oc_synctex_status(),length:mapLength};
 if(mapLength){const p=m._malloc(mapLength);assert.equal(m._oc_synctex_copy(71,p,mapLength),mapLength);
  const b=Buffer.from(m.HEAPU8.slice(p,p+mapLength));m._free(p);fs.writeFileSync(path.join(out,'native.synctex'),b);nativeMap.sha256=hash(b);}
 fs.writeFileSync(path.join(out,'stdout.log'),logs.join('\n')+'\n');
 const expectedFaces=generating?[]:[...source.toString().matchAll(/^\\Face\{[^}]+\}\{[^}]+\}\{[^}]+\}\{[^}]+\}\{([^}]+)\}$/gm)].map(x=>x[1]);
 const observedFaces=logs.filter(s=>s.startsWith('R1-FONT:')).map(s=>{
  const x=/^R1-FONT:([^:]+):([^ ]+)(?: at .*)?$/.exec(s);assert.ok(x);assert.equal(x[1],x[2]);return x[1];
 });
 assert.deepEqual(observedFaces,expectedFaces,'actual ordered font selections');
 const receipt={schemaVersion:1,operation,status,semantic,nativeMap,
  source:pin(source),sourceUnchanged:generating?null:Buffer.compare(source,F.readFile('/work/main.tex'))===0,
  component:{loader:pin(loader),wasm:pin(wasm),manifest:pin(fs.readFileSync(path.join(component,'manifest.json')))},
  format:generating?null:pin(fs.readFileSync(format)),resources:used,outputs,
  fontWitnesses:{expected:expectedFaces.length,actual:observedFaces.length,allExact:true},
  formatHook:pin(fs.readFileSync(path.join(__dirname,'format.tex'))),formatHookApplied:operation==='format',
  productRuntimeChanged:false,browserAcceptance:false};
 fs.writeFileSync(path.join(out,'receipt.json'),JSON.stringify(receipt,null,2)+'\n');
 console.log(JSON.stringify({out,status,semantic,nativeMap}));
 if(operation.endsWith('-reject')){
  assert.notEqual(status,0,'negative native invocation must refuse');
  assert.ok(!outputs.some(p=>/\.(pdf|dvi|fmt)$/.test(p.name)),'no successful negative artifact');
  return;
 }
 if(operation==='compile-unsupported'){
  assert.equal(status,0);assert.equal(semantic.status,2,'unsupported semantic domain must be visible');
  assert.ok(receipt.sourceUnchanged);return;
 }
 assert.equal(status,0,logs.slice(-35).join('\n'));assert.equal(semantic.status,1,'explicit semantic unsupported/failure');
 assert.ok(generating||receipt.sourceUnchanged);assert.ok(outputs.some(p=>/\.(pdf|dvi|fmt)$/.test(p.name)));
 assert.ok(!/Missing character:|Font Warning:/.test(logs.join('\n')),'font substitution/missing glyph');
})().catch(e=>{fs.writeFileSync(path.join(out,'error.log'),String(e.stack));console.error(e);process.exitCode=1;});
