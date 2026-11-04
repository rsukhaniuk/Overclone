'use strict';
// Real native add-file admission boundaries. Fresh module per case, no TeX entry.
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto'),assert=require('node:assert/strict');
const [component,policy,out]=process.argv.slice(2);
if(!out||!['p10','fonts'].includes(policy))throw Error('usage: node quota.cjs COMPONENT_DIR p10|fonts OUT');
if(fs.existsSync(out))throw Error('fresh output required');
const hash=b=>crypto.createHash('sha256').update(b).digest('hex'),MiB=1048576;
const loader=fs.readFileSync(path.join(component,'luatex.js')),wasm=fs.readFileSync(path.join(component,'luatex.wasm'));
const font=policy==='fonts',total=(font?160:96)*MiB,runtime=(font?128:64)*MiB,work=32*MiB;
const cases=[['aggregate-below',runtime,work-1,0],['aggregate-at',runtime,work,0],['aggregate-above',runtime,work+1,-4]];
if(font)cases.push(['runtime-below',runtime-1,0,0],['runtime-at',runtime,0,0],['runtime-above',runtime+1,0,-4],
  ['work-below',0,work-1,0],['work-at',0,work,0],['work-above',0,work+1,-4]);
(async()=>{
  const rows=[];
  for(const [id,runtimeBytes,workBytes,expected]of cases){
    if(global.gc)global.gc(); // Test-process cleanup between independent modules.
    const m=await require(path.join(component,'luatex.js'))({wasmBinary:wasm,noInitialRun:true,print:()=>{},printErr:()=>{}});
    const call=(n,t=[],a=[],r='number')=>m.ccall('oc_luatex_'+n,r,t,a);
    assert.equal(call('configure',['string','string'],['main.tex','font-quota-'+id]),0);
    const capability=typeof m._oc_lua_font_closure_abi==='function'?m._oc_lua_font_closure_abi():0;
    assert.equal(capability,font?1:0);
    let status=0,accepted=0,count=0,last=null;
    outer:for(const [kind,bytes,cap]of [[0,runtimeBytes,32*MiB],[1,workBytes,8*MiB]]){
      let remaining=bytes;
      while(remaining){
        const size=Math.min(cap,remaining),ptr=m._malloc(size);assert.ok(ptr);
        m.HEAPU8.fill(0x5a,ptr,ptr+size);
        const name='quota/'+kind+'-'+count+'.bin';
        status=call('add_file',['number','string','number','number'],[kind,name,ptr,size]);m._free(ptr);
        last={kind,size,name};
        if(status!==0)break outer;
        accepted+=size;remaining-=size;count++;
      }
    }
    assert.equal(status,expected,id+': '+call('error',[],[],'string'));
    if(expected)assert.ok(!m.FS.analyzePath('/work/'+(last.kind===0?'runtime':'project')+'/'+last.name).exists,'failed file absent');
    rows.push({id,runtimeBytes,workBytes,requestedTotal:runtimeBytes+workBytes,acceptedBytes:accepted,status,
      nativeError:call('error',[],[],'string'),capability,nativeHeapBytes:m.HEAPU8.length,maxRssKiB:process.resourceUsage().maxRSS});
    console.log(JSON.stringify(rows.at(-1)));
  }
  fs.writeFileSync(out,JSON.stringify({schemaVersion:1,status:'pass',scope:'Native seeding only; worker admission/physical cancellation remain separate',
    policy,aggregateLimitBytes:total,loaderSha256:hash(loader),wasmSha256:hash(wasm),rows},null,2)+'\n');
})().catch(e=>{console.error(e);process.exitCode=1;});
