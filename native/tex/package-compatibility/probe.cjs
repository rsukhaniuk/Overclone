// Build/test-only bridge to the supplied Emscripten ABIs; no product logic.
'use strict';
const fs = require('node:fs'), path = require('node:path'), vm = require('node:vm');
const crypto = require('node:crypto'), assert = require('node:assert/strict');
const [engine, fixture, out, overlay, carry, operation = 'latex'] = process.argv.slice(2);
if (!overlay) throw new Error('usage: node probe.cjs ENGINE FIXTURE OUT OVERLAY [CARRY|-] [latex|bibtex]');
const suite = '/inputs/suite', base = suite + '/files';
const hash = b => crypto.createHash('sha256').update(b).digest('hex');
const pin = (name,b) => ({name,length:b.length,sha256:hash(b)});
function readJSON(file) { try { return JSON.parse(fs.readFileSync(file)); } catch (cause) { throw new Error('Invalid JSON: '+file,{cause}); } }
const pins = readJSON(suite + '/pins.json');
const views = readJSON(suite + '/views.json');
const checked = name => {
  const p = pins.find(p => p.name === name); assert.ok(p, name);
  const b = fs.readFileSync(base + '/' + name);
  assert.equal(b.length,p.length); assert.equal(hash(b),p.sha256); return b;
};
const extras = readJSON(overlay + '/manifest.json').files;
const runtime = views[engine].files.map(name => [name,checked(name)]);
for (const p of extras) {
  assert.ok(!pins.some(a => a.name === p.name), 'overlay must be additive: '+p.name);
  const b = fs.readFileSync(overlay+'/files/'+p.name);
  assert.equal(b.length,p.length); assert.equal(hash(b),p.sha256); runtime.push([p.name,b]);
}
const source = fs.readFileSync(fixture), root = 'розділ з пробілом/головний.tex';
const job = engine === 'luatex' ? 'result' : 'головний';
const project = [[root,source]];
const side = fixture + '.files';
if (fs.existsSync(side)) for (const name of fs.readdirSync(side)) project.push([name,fs.readFileSync(side+'/'+name)]);
const carried = [];
if (carry && carry !== '-') for (const name of fs.readdirSync(carry)) {
  if (/\.(aux|out|toc|bbl|bcf|run\.xml)$/.test(name)) carried.push([name,fs.readFileSync(carry+'/'+name)]);
}
fs.mkdirSync(out,{recursive:true});
let lines=[], opened=new Set(), m, F, status, nativeError;
const print = s => { if(lines.join('').length < 1000000) lines.push(String(s)); };
function trace(F) {
  const open=F.open;
  F.open=function(name,...args){ const r=open.call(this,name,...args); if(typeof name==='string') opened.add(path.posix.resolve(F.cwd(),name)); return r; };
}
function mount(name,b){ F.mkdirTree(path.posix.dirname(name)); F.writeFile(name,b); }
function save() {
  const outputDir=engine==='luatex'&&operation!=='bibtex'?'/work/output':'/work';
  const outputs=[];
  for(const name of F.readdir(outputDir)) {
    if(!/\.(pdf|dvi|xdv|log|aux|out|toc|bbl|blg|bcf|xml)$/.test(name))continue;
    const bytes=F.readFile(outputDir+'/'+name); fs.writeFileSync(out+'/'+name,bytes); outputs.push(pin(name,bytes));
  }
  fs.writeFileSync(out+'/stdout.log',lines.join('\n')+'\n');
  const extension=engine==='latex'?'dvi':engine==='xetex'?'xdv':'pdf';
  const result={schema:1,engine,operation,root,status,nativeError,source:pin(path.basename(fixture),source),
    suitePinsSha256:hash(fs.readFileSync(suite+'/pins.json')),overlaySha256:hash(fs.readFileSync(overlay+'/manifest.json')),
    runtime:runtime.map(([n,b])=>pin(n,b)),project:project.map(([n,b])=>pin(n,b)),carry:carried.map(([n,b])=>pin(n,b)),
    outputs,opened:[...opened].sort(),missingGlyphs:lines.filter(s=>/Missing character:/.test(s)),
    packageErrors:lines.filter(s=>/^!|LaTeX Error:|Package .* Error:/.test(s)),
    success:status===0 && (operation==='bibtex'?outputs.some(p=>p.name.endsWith('.bbl')):outputs.some(p=>p.name===job+'.'+extension))};
  if(result.success && operation==='latex') {
    const b=fs.readFileSync(out+'/'+job+'.'+extension);
    if(extension==='pdf') { assert.equal(b.subarray(0,5).toString(),'%PDF-');assert.ok(b.subarray(-1024).includes(Buffer.from('%%EOF'))); }
    else {assert.equal(b[0],247);assert.equal(b[1],extension==='xdv'?7:2);}
    assert.equal(result.missingGlyphs.length,0);assert.equal(result.packageErrors.length,0);
  }
  fs.writeFileSync(out+'/result.json',JSON.stringify(result,null,2)+'\n');
  console.log(JSON.stringify({engine,operation,status,success:result.success,outputs,errors:result.packageErrors}));
}
(async()=>{
  if(engine==='pdftex'||engine==='latex'||operation==='bibtex') {
    // Retained loader is a worker script, not a CommonJS factory. This VM only
    // adapts its real ABI; host files are explicitly seeded, never host-mounted.
    const localRequire = name => name === 'node:fs' ? new Proxy(fs,{get(t,k){
      if(k!=='readFileSync')return t[k];
      return (f,o)=>fs.readFileSync(String(f).endsWith('/swiftlatexpdftex.wasm')?base+'/engine.wasm':f,o);
    }}) : require(name);
    checked('engine.js');checked('engine.wasm');
    const ctx=vm.createContext({require:localRequire,process,console:{log:print,error:print},TextDecoder,TextEncoder,WebAssembly,setTimeout,clearTimeout,__dirname:base,__filename:base+'/engine.js'});
    ctx.self=ctx;ctx.postMessage=()=>{};ctx.fetch=()=>{throw new Error('network forbidden')};
    ctx.XMLHttpRequest=class{constructor(){throw new Error('network forbidden')}};
    ctx.runProbe=()=>{
      F=ctx.FS;F.chdir('/work');
      for(const [name,b] of runtime) mount('/tex/'+name,b);
      const fmt=engine==='latex'?'latex-dvi.fmt':'pdflatex.fmt';
      mount('/tex/swiftlatexpdftex.fmt',checked(fmt));
      for(const [name,b] of [...project,...carried])mount('/work/'+name,b);
      for(const [name] of [...runtime,['swiftlatexpdftex.fmt']]) {
        const alias='/work/'+name;if(!F.analyzePath(alias).exists){F.mkdirTree(path.posix.dirname(alias));F.symlink('/tex/'+name,alias);}
      }
      trace(F);ctx.prepareExecutionContext();ctx.cwrap('setMainEntry','number',['string'])(operation==='bibtex'?job+'.tex':'"'+root+'"');
      if(operation==='latex') assert.equal(ctx.cwrap('oc_mem_configure','number',['number','number','number'])(0,0,1),1);
      status=operation==='bibtex'?ctx._compileBibtex():ctx._compileLaTeX();lines=ctx.memlog.split('\n');save();
    };
    await new Promise((resolve,reject)=>{
      ctx.done=()=>{try{ctx.runProbe();resolve();}catch(e){reject(e)}};
      vm.runInContext(checked('engine.js').toString()+`\nkpse_find_file_impl=()=>0;kpse_find_pk_impl=()=>0;Module.locateFile=()=>${JSON.stringify(base+'/engine.wasm')};Module.postRun=done;`,ctx);
    });
  } else {
    const stem=engine==='xetex'?'xetex':'luatex';
    checked(stem+'.js');const wasm=checked(stem+'.wasm');
    m=await require(base+'/'+stem+'.js')({wasmBinary:wasm,noInitialRun:true,print,printErr:print});F=m.FS;
    if(engine==='xetex') {
      F.mkdir('/tex');F.mkdir('/work');F.chdir('/work');
      for(const [n,b]of runtime)mount('/tex/'+n,b);
      for(const [n,b]of [...project,...carried])mount('/work/'+n,b);
      trace(F);assert.equal(m.ccall('setMainEntry','number',['string'],[root]),0);status=m._compileLaTeX();
    } else {
      const call=(n,t=[],a=[],r='number')=>m.ccall('oc_luatex_'+n,r,t,a);
      assert.equal(call('abi'),2);assert.equal(call('configure',['string','string'],[root,'package-compatibility']),0);
      for(const ext of ['aux','out','toc','bbl','blg','lof','lot']) assert.equal(call('declare_output',['string'],[job+'.'+ext]),0);
      for(const [kind,entries] of [[0,runtime],[1,project],[2,carried]])for(const [n,b]of entries){
        const p=m._malloc(Math.max(1,b.length));m.HEAPU8.set(b,p);
        const code=call('add_file',['number','string','number','number'],[kind,n,p,b.length]);m._free(p);
        assert.equal(code,0,n+': '+call('error',[],[],'string'));
      }
      // ABI2 validates exact SDK hook identities; do not monkey-patch FS.open.
      status=call('compile_latex');nativeError=call('error',[],[],'string');
      const fls='/work/output/result.fls';
      if(F.analyzePath(fls).exists){const b=F.readFile(fls);fs.writeFileSync(out+'/result.fls',b);for(const line of Buffer.from(b).toString().split('\n'))if(line.startsWith('INPUT '))opened.add(line.slice(6));}
    }
    save();
  }
})().catch(e=>{fs.writeFileSync(out+'/harness-error.log',String(e.stack));console.error(e);process.exitCode=1;});
