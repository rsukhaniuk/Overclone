'use strict';
// Native test-only full-resource font probe. Component/format overrides are
// explicit private candidates, never trusted package/profile admission.
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto'),assert=require('node:assert/strict');
const [runtime,overlay,engine,fixture,out,component,formatOverride]=process.argv.slice(2);
if(!out||!['xetex','luatex'].includes(engine))throw Error('usage: node unicode-probe.cjs RUNTIME OVERLAY xetex|luatex FIXTURE OUT [CANDIDATE_COMPONENT_DIR] [CANDIDATE_FORMAT]');
if(fs.existsSync(out))throw Error('fresh output directory required');fs.mkdirSync(out,{recursive:true});
const hash=b=>crypto.createHash('sha256').update(b).digest('hex'),pin=b=>({length:b.length,sha256:hash(b)});
const pins=JSON.parse(fs.readFileSync(path.join(runtime,'pins.json'))),extras=JSON.parse(fs.readFileSync(path.join(overlay,'manifest.json'))).files;
const original=new Map(pins.map(x=>[x.name,x]));
for(const p of extras){assert.ok(!original.has(p.name));pins.push(p);}
const fmt=engine==='xetex'?'xelatex.fmt':'lualatex.fmt';
const checked=p=>{const root=original.has(p.name)?runtime:overlay,b=fs.readFileSync(path.join(root,'files',p.name));assert.deepEqual(pin(b),{length:p.length,sha256:p.sha256});return b;};
const selected=pins.filter(p=>p.name===fmt||!(/\.(wasm|js|fmt)$/.test(p.name))&&(engine==='xetex'||!['icudt68l.dat','xetexfontlist.txt'].includes(p.name)));
const resources=selected.map(p=>[p.name,p.name===fmt&&formatOverride?fs.readFileSync(formatOverride):checked(p)]);
const componentRoot=component||path.join(runtime,'files');
const loaderPath=path.join(componentRoot,engine+'.js'),wasmPath=path.join(componentRoot,engine+'.wasm');
const loader=fs.readFileSync(loaderPath),wasm=fs.readFileSync(wasmPath),source=fs.readFileSync(fixture),logs=[];
function print(x){assert.ok(logs.length<50000);logs.push(String(x));}
(async()=>{
  if(!component){checked(original.get(engine+'.js'));checked(original.get(engine+'.wasm'));}
  const m=await require(loaderPath)({wasmBinary:wasm,instantiateWasm(imports,done){const i=new WebAssembly.Instance(new WebAssembly.Module(wasm),imports);done(i);return i.exports;},noInitialRun:true,print,printErr:print});
  const F=m.FS,call=(n,t=[],a=[],r='number')=>m.ccall('oc_luatex_'+n,r,t,a);
  let status,setupError=null;
  if(engine==='xetex'){
    F.mkdir('/tex');F.mkdir('/work');F.chdir('/work');
    for(const [n,b]of resources){F.mkdirTree(path.posix.dirname('/tex/'+n));F.writeFile('/tex/'+n,b);}
    F.writeFile('/work/main.tex',source);assert.equal(m.ccall('setMainEntry','number',['string'],['main.tex']),0);status=m._compileLaTeX();
  }else{
    assert.ok(call('abi')>=3);assert.equal(call('configure',['string','string'],['main.tex','r1-font-full-resource']),0);
    for(const ext of ['aux','toc','out','bbl','blg','lof','lot'])assert.equal(call('declare_output',['string'],['result.'+ext]),0);
    for(const [kind,rows]of [[0,resources],[1,[['main.tex',source]]]]){
      for(const [n,b]of rows){const p=m._malloc(Math.max(1,b.length));assert.ok(p);m.HEAPU8.set(b,p);
        const code=call('add_file',['number','string','number','number'],[kind,n,p,b.length]);m._free(p);
        if(code!==0){setupError={name:n,code,error:call('error',[],[],'string')};break;}}
      if(setupError)break;
    }
    if(!setupError)status=call('compile_latex');
  }
  const nativeHeapBytes=m.HEAPU8.length,outputs=[];
  // FS.readFile for private artifact collection itself spends copy-work budget.
  // Capture the actual compile-return state before any such QA reads.
  const nativeBudgetAtReturn=engine==='luatex'?JSON.parse(call('budget_json',[],[],'string')):null;
  const nativeErrorAtReturn=engine==='luatex'?call('error',[],[],'string'):null;
  const directory=engine==='luatex'?'/work/output':'/work';
  if(!setupError){
    for(const n of F.readdir(directory))if(/\.(log|pdf|xdv|aux|fls)$/.test(n)){
      const b=Buffer.from(F.readFile(directory+'/'+n));fs.writeFileSync(path.join(out,n),b);outputs.push({name:n,...pin(b)});
    }
  }
  fs.writeFileSync(path.join(out,'stdout.log'),logs.join('\n')+'\n');
  const expectedFaces=[...source.toString().matchAll(/^\\Face\{[^}]+\}\{[^}]+\}\{[^}]+\}\{[^}]+\}\{([^}]+)\}$/gm)].map(x=>x[1]);
  const observedFaces=logs.filter(s=>s.startsWith('R1-FONT:')).map(s=>{const x=/^R1-FONT:([^:]+):([^ ]+)(?: at .*)?$/.exec(s);assert.ok(x,'font witness syntax');assert.equal(x[1],x[2],'actual font must equal declared metric');return x[1];});
  assert.deepEqual(observedFaces,expectedFaces,'exact ordered declared face selections');
  const identity=resources.find(([n])=>n===fmt)[1];
  const report={schemaVersion:1,engine,status:status??null,setupError,nativeError:nativeErrorAtReturn,nativeBudgetAtReturn,
    nativeBudgetAfterQaReads:engine==='luatex'?JSON.parse(call('budget_json',[],[],'string')):null,candidateComponent:!!component,candidateFormat:!!formatOverride,
    loader:pin(loader),wasm:pin(wasm),format:{name:fmt,...pin(identity)},source:pin(source),outputs,
    fontWitnesses:{expected:expectedFaces.length,actual:observedFaces.length,allExact:true},
    resources:{count:resources.length,totalBytes:resources.reduce((n,[,b])=>n+b.length,0),files:resources.map(([name,b])=>({name,...pin(b)}))},
    memory:{maxRssKiB:process.resourceUsage().maxRSS,nativeHeapBytes,node:process.memoryUsage(),
      retainedResourceBufferBytes:resources.reduce((n,[,b])=>n+b.length,0),note:'Native test process only. Actual browser/C# copy coexistence is not measured here.'},
    productRuntimeChanged:false,sourceUnchanged:engine==='xetex'?Buffer.compare(source,F.readFile('/work/main.tex'))===0:!setupError&&Buffer.compare(source,F.readFile('/work/project/main.tex'))===0};
  fs.writeFileSync(path.join(out,'receipt.json'),JSON.stringify(report,null,2)+'\n');
  console.log(JSON.stringify({engine,status,setupError,resourceBytes:report.resources.totalBytes,memory:report.memory}));
  assert.equal(setupError,null);assert.equal(status,0,logs.slice(-30).join('\n'));assert.ok(report.sourceUnchanged);
  assert.ok(!/Missing character:|Font Warning:/.test(logs.join('\n')),'missing glyph/substitution');
})().catch(e=>{fs.writeFileSync(path.join(out,'error.log'),String(e.stack));console.error(e);process.exitCode=1;});
