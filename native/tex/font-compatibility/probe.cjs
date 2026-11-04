'use strict';
// Test-only retained-WASM/FS adapter. No project rewriting or product integration.
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const crypto=require('node:crypto'),assert=require('node:assert/strict');
const [runtime,engine,operation,fixture,format,out,overlay]=process.argv.slice(2);
if(!out||!['pdftex','latex'].includes(engine)||!['format','format-control','baseline','candidate'].includes(operation))
  throw Error('usage: node probe.cjs RUNTIME pdftex|latex format|format-control|baseline|candidate FIXTURE FORMAT|- OUT [ADDITIVE_OVERLAY]');
if(fs.existsSync(out))throw Error('fresh output directory required');
fs.mkdirSync(out,{recursive:true});
const hash=b=>crypto.createHash('sha256').update(b).digest('hex');
const pin=b=>({length:b.length,sha256:hash(b)});
const pins=JSON.parse(fs.readFileSync(path.join(runtime,'pins.json')));
const basePinCount=pins.length;
const extras=overlay?JSON.parse(fs.readFileSync(path.join(overlay,'manifest.json'))).files:[];
for(const p of extras){assert.ok(!pins.some(x=>x.name===p.name),'additive-only overlay');pins.push(p);}
const base=path.join(runtime,'files'),used=[];
function checked(name){
  const p=pins.find(x=>x.name===name);assert.ok(p,name);
  const root=extras.some(x=>x.name===name)?path.join(overlay,'files'):base;
  const b=fs.readFileSync(path.join(root,name));assert.equal(b.length,p.length);assert.equal(hash(b),p.sha256);
  used.push({name,...pin(b)});return b;
}
let F,context,status,source;
const generating=operation==='format'||operation==='format-control';
const logs=[];const print=s=>{assert.ok(logs.length<50000);logs.push(String(s));};
function mount(n,b){F.mkdirTree(path.posix.dirname(n));F.writeFile(n,b);}
(async()=>{
  const wasm=checked('engine.wasm');
  const localRequire=n=>n==='node:fs'?new Proxy(fs,{get(t,k){
    if(k!=='readFileSync')return t[k];
    return (n,o)=>String(n).endsWith('/swiftlatexpdftex.wasm')?wasm:fs.readFileSync(n,o);
  }}):require(n);
  context=vm.createContext({require:localRequire,process,console:{log:print,error:print},TextDecoder,TextEncoder,
    WebAssembly,setTimeout,clearTimeout,__dirname:base,__filename:base+'/engine.js'});
  context.self=context;context.postMessage=()=>{};
  context.fetch=()=>{throw Error('network forbidden')};
  context.XMLHttpRequest=class{constructor(){throw Error('network forbidden')}};
  await new Promise((resolve,reject)=>{
    context.ready=()=>{try{
      F=context.FS;F.chdir('/work');
      for(const p of pins){
        if(/\.(wasm|js|fmt|ttf|otf)$/.test(p.name)||p.name.startsWith('notices/'))continue;
        let b=checked(p.name);
        if(operation==='format'&&p.name==='latex.ltx'){
          const text=b.toString();assert.equal(text.split('\\dump\n').length,2,'one genuine kernel dump');
          b=Buffer.from(text.replace('\\dump\n',fs.readFileSync(path.join(__dirname,'actualtext.tex'),'utf8')+'\n\\dump\n'));
          fs.writeFileSync(path.join(out,'private-kernel.sha256'),hash(b)+'\n');
        }
        mount('/tex/'+p.name,b);
        const alias='/work/'+p.name;if(!F.analyzePath(alias).exists){F.mkdirTree(path.posix.dirname(alias));F.symlink('/tex/'+p.name,alias);}
      }
      if(generating){
        if(engine==='latex')mount('/tex/pdflatex.ini',fs.readFileSync(fixture));
      }else{
        source=fs.readFileSync(fixture);mount('/work/main.tex',source);
        const f=operation==='candidate'?fs.readFileSync(format):checked(engine==='latex'?'latex-dvi.fmt':'pdflatex.fmt');
        mount('/tex/swiftlatexpdftex.fmt',f);fs.writeFileSync(path.join(out,'format.sha256'),hash(f)+'\n');
        F.symlink('/tex/swiftlatexpdftex.fmt','/work/swiftlatexpdftex.fmt');
        context.prepareExecutionContext();assert.equal(context.cwrap('setMainEntry','number',['string'])('main.tex'),0);
        assert.equal(context.cwrap('oc_mem_configure','number',['number','number','number'])(0,0,1),1);
      }
      status=generating?context._compileFormat():context._compileLaTeX();
      for(const n of F.readdir('/work')){
        if(!/\.(fmt|log|pdf|dvi|aux)$/.test(n)||F.isDir(F.stat('/work/'+n).mode))continue;
        fs.writeFileSync(path.join(out,n),F.readFile('/work/'+n));
      }
      const log=String(context.memlog||logs.join('\n'));fs.writeFileSync(path.join(out,'stdout.log'),log);
      const expectedFaces=generating?[]:[...source.toString().matchAll(/^\\Face\{[^}]+\}\{[^}]+\}\{[^}]+\}\{[^}]+\}\{([^}]+)\}$/gm)].map(x=>x[1]);
      const observedFaces=log.split('\n').filter(s=>s.startsWith('R1-FONT:')).map(s=>{const x=/^R1-FONT:([^:]+):([^ ]+)(?: at .*)?$/.exec(s);assert.ok(x);assert.equal(x[1],x[2],'actual font must equal declared metric');return x[1];});
      assert.deepEqual(observedFaces,expectedFaces,'exact ordered declared face selections');
      const outputs=fs.readdirSync(out).filter(n=>/\.(fmt|pdf|dvi)$/.test(n)).map(n=>({name:n,...pin(fs.readFileSync(path.join(out,n)))}));
      fs.writeFileSync(path.join(out,'receipt.json'),JSON.stringify({schemaVersion:1,engine,operation,status,
        runtimePins:pin(fs.readFileSync(path.join(runtime,'pins.json'))),fixture:pin(fs.readFileSync(fixture)),
        sourceUnchanged:generating?null:Buffer.compare(source,F.readFile('/work/main.tex'))===0,
        marker:pin(fs.readFileSync(path.join(__dirname,'actualtext.tex'))),markerApplied:operation==='format',
        overlay:overlay?pin(fs.readFileSync(path.join(overlay,'manifest.json'))):null,basePinCount,used,outputs,
        fontWitnesses:{expected:expectedFaces.length,actual:observedFaces.length,allExact:true},
        productRuntimeChanged:false,scope:'Native experiment; not browser/font-closure acceptance'},null,2)+'\n');
      assert.equal(status,0,log.slice(-5000));assert.ok(outputs.length);
      assert.ok(!/Missing character:|Font Warning:/.test(log),'font warning/missing glyph');
      resolve();
    }catch(e){reject(e)}};
    vm.runInContext(checked('engine.js').toString()+`\nkpse_find_file_impl=()=>0;kpse_find_pk_impl=()=>0;Module.locateFile=()=>${JSON.stringify(base+'/engine.wasm')};Module.postRun=ready;`,context);
  });
  console.log(JSON.stringify({engine,operation,status,out}));
})().catch(e=>{fs.writeFileSync(path.join(out,'error.log'),String(e.stack));console.error(e);process.exitCode=1;});
