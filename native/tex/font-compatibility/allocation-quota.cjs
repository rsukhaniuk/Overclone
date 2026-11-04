'use strict';
// Real installed SDK guard boundaries after genuine native TeX compilation.
// This exercises allocation accounting, not hostile-TeX/cancellation acceptance.
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto'),assert=require('node:assert/strict');
const [runtime,overlay,component,format,capText,out]=process.argv.slice(2);
if(!out||![16777216,67108864].includes(Number(capText)))throw Error('usage: node allocation-quota.cjs RUNTIME OVERLAY COMPONENT FORMAT CAP_BYTES OUT');
if(fs.existsSync(out))throw Error('fresh output required');
const cap=Number(capText),hash=b=>crypto.createHash('sha256').update(b).digest('hex');
const pins=JSON.parse(fs.readFileSync(path.join(runtime,'pins.json'))),byName=new Map(pins.map(p=>[p.name,p]));
const extra=JSON.parse(fs.readFileSync(path.join(overlay,'manifest.json'))).files;
for(const p of extra){assert.ok(!byName.has(p.name));pins.push(p);}
const selected=pins.filter(p=>p.name==='lualatex.fmt'||!(/\.(wasm|js|fmt)$/.test(p.name))&&!['icudt68l.dat','xetexfontlist.txt'].includes(p.name));
const resources=selected.map(p=>{
  const b=p.name==='lualatex.fmt'?fs.readFileSync(format):fs.readFileSync(path.join(byName.has(p.name)?runtime:overlay,'files',p.name));
  if(p.name!=='lualatex.fmt'){assert.equal(b.length,p.length);assert.equal(hash(b),p.sha256);}return[p.name,b];
});
const loader=fs.readFileSync(path.join(component,'luatex.js')),wasm=fs.readFileSync(path.join(component,'luatex.wasm'));
(async()=>{
  const rows=[];
  for(const point of ['below','at','above','per-file-above']){
    if(global.gc)global.gc();
    const m=await require(path.join(component,'luatex.js'))({wasmBinary:wasm,noInitialRun:true,print:()=>{},printErr:()=>{}});
    const call=(n,t=[],a=[],r='number')=>m.ccall('oc_luatex_'+n,r,t,a),budget=()=>JSON.parse(call('budget_json',[],[],'string'));
    assert.equal(call('configure',['string','string'],['main.tex','allocation-'+point]),0);
    for(const x of ['aux','toc','out','bbl','blg','lof','lot'])assert.equal(call('declare_output',['string'],['result.'+x]),0);
    const source=Buffer.from('\\documentclass{article}\n\\begin{document}X\\end{document}\n');
    for(const [kind,entries]of [[0,resources],[1,[['main.tex',source]]]])for(const[n,b]of entries){
      const p=m._malloc(Math.max(1,b.length));m.HEAPU8.set(b,p);const status=call('add_file',['number','string','number','number'],[kind,n,p,b.length]);m._free(p);assert.equal(status,0,n);
    }
    assert.equal(call('compile_latex'),0,call('error',[],[],'string'));
    const before=budget();assert.equal(before.cap,cap);assert.equal(before.maximum,8388608);assert.equal(before.failed,false);
    let caught=null,n=0;
    if(point==='per-file-above'){
      try{m.FS.writeFile('/work/output/too-large.bin',new Uint8Array(8388609));}catch(e){caught=e.errno;}
      assert.ok(caught);assert.equal(budget().reason,'file bytes');
    }else{
      const target=cap-(point==='below'?1:0);
      let remain=target-before.allocationWork;assert.ok(remain>0);
      while(remain){const size=Math.min(8388608,remain);m.FS.writeFile('/work/output/quota-'+n+++'.bin',new Uint8Array(size));remain-=size;}
      assert.equal(budget().allocationWork,target);assert.equal(budget().failed,false);
      if(point==='above'){
        try{m.FS.writeFile('/work/output/quota-over.bin',new Uint8Array(1));}catch(e){caught=e.errno;}
        assert.ok(caught);assert.equal(budget().reason,'backing/copy allocation work');assert.equal(budget().allocationWork,cap);
      }
    }
    rows.push({point,before,after:budget(),caughtErrno:caught,nativeHeapBytes:m.HEAPU8.length,maxRssKiB:process.resourceUsage().maxRSS});
    console.log(JSON.stringify({point,allocationWork:rows.at(-1).after.allocationWork,failed:rows.at(-1).after.failed,reason:rows.at(-1).after.reason}));
  }
  fs.writeFileSync(out,JSON.stringify({schemaVersion:1,status:'pass',cap,loaderSha256:hash(loader),wasmSha256:hash(wasm),formatSha256:hash(fs.readFileSync(format)),
    scope:'Genuine compiled-module SDK allocation guard only; not browser/native hostile-project acceptance',rows},null,2)+'\n');
})().catch(e=>{console.error(e);process.exitCode=1;});
