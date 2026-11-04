'use strict';
// Build/test-only real C ABI driver. A controlled fixture is explicitly labelled
// and never counts as genuine Biber/program or filesystem-security acceptance.
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto'),assert=require('node:assert/strict');
const {Worker,isMainThread,parentPort,workerData}=require('node:worker_threads');
const sha=b=>crypto.createHash('sha256').update(b).digest('hex');
const MiB=1048576;
let diagnosticModule=null;

async function one(d){
  const loader=fs.readFileSync(path.join(d.component,'biber.js'));
  const wasm=fs.readFileSync(path.join(d.component,'biber.wasm'));
  const data=fs.readFileSync(path.join(d.component,'biber.data'));
  let packageCalls=0;
  globalThis.fetch=()=>{throw Error('unexpected network/bootstrap fetch')};
  globalThis.XMLHttpRequest=class{constructor(){throw Error('unexpected XHR')}};
  globalThis.WebSocket=class{constructor(){throw Error('unexpected WebSocket')}};
  const m=await require(path.join(d.component,'biber.js'))({noInitialRun:true,wasmBinary:wasm,
    getPreloadedPackage(name,size){assert.equal(path.basename(name),'biber.data');assert.equal(size,data.length);packageCalls++;return data.buffer.slice(data.byteOffset,data.byteOffset+data.length);},
    instantiateWasm(imports,done){const i=new WebAssembly.Instance(new WebAssembly.Module(wasm),imports);done(i);return i.exports;},
    print:()=>{},printErr:()=>{}});
  diagnosticModule=m;
  assert.equal(m._oc_biber_abi(),1);
  if(d.controlled){
    // Only the wrapper-only fake fixture needs a placeholder program path.
    m.FS.mkdirTree('/work/runtime/perl/bin');m.FS.writeFile('/work/runtime/perl/bin/biber',Buffer.from('CONTROLLED_WRAPPER_ONLY'));
  }else assert.ok(packageCalls>0,'actual trusted data bootstrap was consumed');
  const fixture=JSON.parse(fs.readFileSync(d.fixture)),fixtureRoot=path.dirname(d.fixture);
  const source=row=>{
    const b=fs.readFileSync(path.resolve(fixtureRoot,row.source));
    if(row.length!==undefined)assert.equal(b.length,row.length);
    if(row.sha256!==undefined)assert.equal(sha(b),row.sha256);return b;
  };
  const blob=(bytes,fn)=>{const b=Buffer.isBuffer(bytes)?bytes:Buffer.from(bytes);const p=m._malloc(Math.max(1,b.length));assert.ok(p);m.HEAPU8.set(b,p);try{return fn(p,b.length);}finally{m._free(p);}};
  const ctx='SYNTHETIC_CONTEXT_DO_NOT_LOG_Київ😀';
  const configure=(entry=fixture.entry,log=262144,output=8388608)=>blob(entry,(p,n)=>blob(ctx,(q,k)=>m._oc_biber_configure(p,n,q,k,log,output)));
  const add=(role,name,data=Buffer.alloc(0))=>blob(name,(p,n)=>blob(data,(q,k)=>{
    const status=m._oc_biber_add_file(role,p,n,q,k);m.HEAPU8.fill(0xcc,q,q+k);return status;
  }));
  const error=()=>blob(Buffer.alloc(64),(p)=>{const n=m._oc_biber_error(p,64);assert.ok(n>=0&&n<=64);const s=Buffer.from(m.HEAPU8.slice(p,p+n)).toString();assert.ok(['','invalid-input','invalid-state','quota','native-failure'].includes(s));return s;});
  const get=kind=>{
    const n=m._oc_biber_output_size(kind);assert.ok(n>=0);
    return blob(Buffer.alloc(n+1,0xa5),(p)=>{
      if(n){assert.equal(m._oc_biber_copy_output(kind,p,n-1),-1);assert.ok(m.HEAPU8.slice(p,p+n+1).every(x=>x===0xa5));}
      assert.equal(m._oc_biber_copy_output(kind,p,n),n);assert.equal(m.HEAPU8[p+n],0xa5);
      return Buffer.from(m.HEAPU8.slice(p,p+n));
    });
  };
  const seed=(entry=fixture.entry,changeBcf=null)=>{
    for(const row of fixture.files){const data=source(row),name=row.role===2?entry:row.name;
      assert.equal(add(row.role,name,row.role===2&&changeBcf?changeBcf(data):data),0,name);}
  };
  const reject=()=>{assert.ok(m._oc_biber_output_size(0)<0);assert.ok(m._oc_biber_copy_output(0,0,0)<0);error();};
  const success=(entry=fixture.entry)=>{
    assert.equal(configure(entry),0);seed(entry);const native=m._oc_biber_run();assert.equal(native,0,error());
    const bbl=get(0),blg=get(1),terminal=get(2),text=new TextDecoder('utf-8',{fatal:true}).decode(bbl);
    assert.ok(bbl.length);assert.ok(bbl.length+blg.length<=8388608);assert.ok(terminal.length<=262144);
    for(const key of fixture.expectedKeys||[])assert.ok(text.includes('\\entry{'+key+'}'),'genuine expected citekey');
    for(const value of fixture.expectedText||[])assert.ok(text.includes(value),'genuine expected bibliography text');
    for(const b of [bbl,blg,terminal])assert.ok(!b.includes(Buffer.from(ctx)),'opaque context must not be logged');
    if(!d.controlled)assert.match(blg.toString()+'\n'+terminal.toString(),/\bBiber\s+2\.19\b/,'genuine version banner');
    assert.equal(m._oc_biber_run(),-2);assert.equal(add(1,'late.bib'),-2);assert.equal(configure(),-2);
    assert.deepEqual(get(0),bbl);return{nativeReturn:native,bbl:{length:bbl.length,sha256:sha(bbl)},blg:{length:blg.length,sha256:sha(blg)},terminal:{length:terminal.length,sha256:sha(terminal)}};
  };
  let result={};
  switch(d.id){
    case 'success-owned-copies':result=success();break;
    case 'unicode-spaced-job':result=success('тези/розділ 😀.bcf');break;
    case 'leading-hyphen-job':result=success('-review.bcf');break;
    case 'state-before-configure':
      assert.equal(m._oc_biber_run(),-2);assert.equal(add(1,'a.bib'),-2);reject();break;
    case 'invalid-configure':{
      for(const n of ['','/a.bcf','../a.bcf','a/../b.bcf','a//b.bcf','a\\b.bcf','a:bcf','a/.bcf','no-extension','a.bcf/','a\0b.bcf',Buffer.from([0xed,0xa0,0x80]),Buffer.from([0xf4,0x90,0x80,0x80]),Buffer.from([0xc0,0xaf])])assert.equal(configure(n),-1);
      assert.equal(configure(Buffer.alloc(1025,97)),-1);assert.equal(configure(fixture.entry,0),-1);assert.equal(configure(fixture.entry,262145),-1);
      assert.equal(configure(fixture.entry,1,0),-1);assert.equal(configure(fixture.entry,1,8388609),-1);
      assert.equal(configure(),0);assert.equal(configure(),-2);break;}
    case 'invalid-file-paths':
      assert.equal(configure(),0);
      for(const n of ['','/bad','../bad','a/../bad','a//bad','a/./bad','a\\bad','a:bad','bad/','a\0bad',Buffer.from([0x80]),Buffer.from([0xf0,0x80,0x80,0x80])])assert.equal(add(1,n),-1);
      assert.equal(add(1,Buffer.alloc(1025,97)),-1);break;
    case 'roles-and-bcf':
      assert.equal(configure(),0);assert.equal(add(0,'bin/biber',Buffer.from('untrusted')), -1);
      assert.equal(add(3,'x'),-1);assert.equal(add(1,fixture.entry),-1);assert.equal(add(2,'wrong.bcf'),-1);break;
    case 'missing-bcf':
      assert.equal(configure(),0);assert.equal(add(1,'source.bib'),0);assert.equal(m._oc_biber_run(),-1);reject();assert.equal(add(2,fixture.entry),-2);break;
    case 'aliases':
      assert.equal(configure(),0);assert.equal(add(1,'Київ.bib'),0);assert.equal(add(1,'КИЇВ.BIB'),-1);
      assert.equal(add(1,'café.bib'),0);assert.equal(add(1,'cafe\u0301.bib'),-1);break;
    case 'directory-aliases':
      assert.equal(configure(),0);assert.equal(add(1,'chapters/a.bib'),0);assert.equal(add(1,'CHAPTERS/b.bib'),-1);break;
    case 'file-directory-collision':
      assert.equal(configure(),0);assert.equal(add(1,'chapter'),0);assert.equal(add(1,'chapter/source.bib'),-1);
      assert.equal(add(1,'other/source.bib'),0);assert.equal(add(1,'other'),-1);break;
    case 'input-count':
      assert.equal(configure(),0);for(let i=0;i<64;i++)assert.equal(add(1,'file'+i+'.bib'),0);
      assert.equal(add(1,'file65.bib'),-3);reject();break;
    case 'input-byte-boundary':
      assert.equal(configure(),0);assert.equal(add(1,'full.bib',Buffer.alloc(8*MiB)),0);assert.equal(add(2,fixture.entry,Buffer.from('x')),-3);reject();break;
    case 'input-byte-above':
      assert.equal(configure(),0);blob('oversize.bib',(p,n)=>assert.equal(m._oc_biber_add_file(1,p,n,0,8*MiB+1),-3));reject();break;
    case 'malformed-bcf':
      assert.equal(configure(),0);seed(fixture.entry,()=>Buffer.from('<broken-bcf>'));result.nativeReturn=m._oc_biber_run();assert.notEqual(result.nativeReturn,0);reject();break;
    case 'terminal-quota':
      assert.equal(configure(fixture.entry,1),0);seed();assert.equal(m._oc_biber_run(),-3);reject();break;
    case 'output-quota':
      assert.equal(configure(fixture.entry,262144,1),0);seed();assert.equal(m._oc_biber_run(),-3);reject();break;
    case 'remote-source':{
      assert.equal(configure(),0);const bib=fixture.files.find(r=>r.role===1&&r.name.endsWith('.bib'));assert.ok(bib);
      seed(fixture.entry,b=>{const s=b.toString();assert.ok(s.includes(bib.name));return Buffer.from(s.replaceAll(bib.name,'https://example.invalid/untrusted.bib'));});
      result.nativeReturn=m._oc_biber_run();assert.notEqual(result.nativeReturn,0);reject();break;}
    case 'traversing-source':{
      assert.equal(configure(),0);const bib=fixture.files.find(r=>r.role===1&&r.name.endsWith('.bib'));assert.ok(bib);
      seed(fixture.entry,b=>{const s=b.toString();assert.ok(s.includes(bib.name));return Buffer.from(s.replaceAll(bib.name,'../../outside.bib'));});
      result.nativeReturn=m._oc_biber_run();assert.notEqual(result.nativeReturn,0);reject();break;}
    case 'controlled-cleanup-early-zero':
    case 'controlled-exit-positive':
    case 'controlled-cleanup-positive':
    case 'controlled-caught-forbidden':
    case 'controlled-caught-quota':{
      assert.ok(d.controlled,'controlled-only lifecycle fixture');
      const expected={'controlled-cleanup-early-zero':0,'controlled-exit-positive':7,'controlled-cleanup-positive':9,
        'controlled-caught-forbidden':-4,'controlled-caught-quota':-3}[d.id];
      assert.equal(configure(),0);seed(fixture.entry,()=>Buffer.from(d.id));const actual=m._oc_biber_run();
      assert.equal(actual,expected);if(expected)reject();else assert.ok(get(0).length);result.nativeReturn=actual;break;}
    default:throw Error('unknown API test case');
  }
  return{id:d.id,status:'pass',scope:d.controlled?'controlled-wrapper-only-not-Biber-proof':'actual-genuine-Biber-candidate',...result,
    budget:typeof m.ocBiberVfsSnapshot==='function'?m.ocBiberVfsSnapshot():null,
    component:{loader:{length:loader.length,sha256:sha(loader)},wasm:{length:wasm.length,sha256:sha(wasm)},data:{length:data.length,sha256:sha(data)}},
    memory:{maxRssKiB:process.resourceUsage().maxRSS,heapBytes:m.HEAPU8.length}};
}

if(!isMainThread){one(workerData).then(r=>parentPort.postMessage(r)).catch(e=>{
  let budget=null,terminal=null;
  try{if(typeof diagnosticModule?.ocBiberVfsSnapshot==='function')budget=diagnosticModule.ocBiberVfsSnapshot();}catch(_){}
  try{terminal=Buffer.from(diagnosticModule.FS.readFile('/work/output/terminal.log')).subarray(0,262144).toString('utf8');}catch(_){}
  parentPort.postMessage({id:workerData.id,status:'fail',message:String(e.stack),budget,terminal});
});}
else(async()=>{
  const [component,fixture,out,mode]=process.argv.slice(2);
  if(!out||mode&&!['controlled'].includes(mode))throw Error('usage: node test-api.cjs COMPONENT_DIR FIXTURE_JSON RESULTS_DIR [controlled]');
  if(fs.existsSync(out))throw Error('fresh result directory required');fs.mkdirSync(out,{recursive:true});
  const cases=['success-owned-copies','unicode-spaced-job','leading-hyphen-job','state-before-configure','invalid-configure','invalid-file-paths','roles-and-bcf','missing-bcf','aliases','directory-aliases','file-directory-collision','input-count','input-byte-boundary','input-byte-above','malformed-bcf','terminal-quota','output-quota','remote-source','traversing-source'];
  if(mode==='controlled')cases.push('controlled-cleanup-early-zero','controlled-exit-positive','controlled-cleanup-positive','controlled-caught-forbidden','controlled-caught-quota');
  const selected=process.env.BIBER_API_CASE?[process.env.BIBER_API_CASE]:cases;assert.ok(selected.every(x=>cases.includes(x)));
  const results=[];
  for(const id of selected){
    const row=await new Promise((resolve,reject)=>{
      const w=new Worker(__filename,{workerData:{component:path.resolve(component),fixture:path.resolve(fixture),controlled:mode==='controlled',id},env:{}});
      const timer=setTimeout(()=>{w.terminate();reject(Error(id+' outer test timeout'));},60000);
      w.once('message',r=>{clearTimeout(timer);w.terminate();resolve(r);});w.once('error',e=>{clearTimeout(timer);reject(e);});
    });
    fs.writeFileSync(path.join(out,id+'.json'),JSON.stringify(row,null,2)+'\n');results.push(row);
    console.log(JSON.stringify({id,status:row.status,scope:row.scope,message:row.status==='fail'?row.message:undefined}));
    if(row.status!=='pass')break;
  }
  const summary={schemaVersion:1,status:results.length===selected.length&&results.every(r=>r.status==='pass')?'pass':'fail',
    scope:mode==='controlled'?'controlled-wrapper-only-not-Biber-proof':'actual-genuine-Biber-candidate',
    physicalCancellationOrBrowserAcceptance:false,results};
  fs.writeFileSync(path.join(out,'summary.json'),JSON.stringify(summary,null,2)+'\n');
  if(summary.status!=='pass')process.exitCode=1;
})().catch(e=>{console.error(e);process.exitCode=1;});
