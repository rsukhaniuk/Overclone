'use strict';
// Genuine WASM admission calls only; no conversion or output substitutions.
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const [parent,candidate,kind,out]=process.argv.slice(2),MiB=1024*1024;
async function instance(dir,modern){
 const wasm=fs.readFileSync(path.join(dir,'oc-dvi.wasm'));const m=await require(path.resolve(dir,'oc-dvi.js'))({noInitialRun:true,print(){},printErr(){},instantiateWasm(i,d){const w=new WebAssembly.Instance(new WebAssembly.Module(wasm),i);d(w);return w.exports;}});
 assert.equal(m._oc_dvi_abi_version(),kind==='xe'?3:2);assert.equal(m._oc_dvi_origin_abi(),1);assert.equal(m._oc_dvi_neutral_abi(),1);assert.equal(m._oc_dvi_graphics_abi(),1);
 if(kind==='dvi')assert.equal(m._oc_dvi_font_semantics_abi(),1);
 if(modern){assert.equal(m._oc_dvi_input_budget_abi(),1);assert.equal(m._oc_dvi_project_input_limit(),16*MiB);assert.equal(m._oc_dvi_pdf_output_limit(),8*MiB);}else assert.equal(typeof m._oc_dvi_input_budget_abi,'undefined');
 for(const name of ['oc_dvi_geo_neutral','oc_dvi_geo_neutral_end','oc_dvi_neutral_color_depth','oc_dvi_origin_unsafe','oc_dvi_origin_final','oc_dvi_reset'])assert.equal(typeof m['_'+name],'undefined');
 assert.equal(m._oc_dvi_pdf_size(),0);assert.equal(m._oc_dvi_geometry_length(997),0);assert.equal(m.FS,undefined);
 return m;
}
function add(m,name,n,resource=0){const bytes=typeof name==='string'?Buffer.from(name+'\0'):name;const p=m._malloc(bytes.length),q=m._malloc(n||1);assert.ok(p&&q);m.HEAPU8.set(bytes,p);m.HEAPU8.fill(0,q,q+n);const rc=m._oc_dvi_add_file(p,q,n,resource);m._free(p);m._free(q);return rc;}
(async()=>{
 const records=[];
 const before=await instance(parent,false),after=await instance(candidate,true);
 const nativeNames=m=>Object.keys(m).filter(n=>n.startsWith('_oc_')).sort();
 assert.deepEqual(nativeNames(after),nativeNames(before).concat(['_oc_dvi_input_budget_abi','_oc_dvi_project_input_limit','_oc_dvi_pdf_output_limit']).sort(),'only three narrow public getters added');
 for(const[dir,modern,limit]of[[parent,false,8*MiB],[candidate,true,16*MiB]]){
  for(const size of [...new Set([8*MiB-1,8*MiB,8*MiB+1,limit-1,limit,limit+1])]){
   const m=await instance(dir,modern),actual=add(m,'input.bin',size);assert.equal(actual,size<=limit?0:2);
   assert.equal(m._oc_dvi_pdf_size(),0);records.push({modern,operation:'single-adjacent',size,actual});
  }
  const m=await instance(dir,modern);assert.equal(add(m,'prefix.bin',limit-1),0);assert.equal(add(m,'too-big.bin',2),2);assert.equal(add(m,'retry.bin',2),2);
  // Refusal preserves old accounting; it neither spends bytes nor resets the
  // accepted prefix. Existing nonfatal admission semantics stay unchanged.
  assert.equal(add(m,'last.bin',1),0);assert.equal(add(m,'over.bin',1),2);assert.equal(add(m,'empty.bin',0),0);
  records.push({modern,operation:'aggregate-retry-accounting',limit,passed:true});
 }
 let m=await instance(candidate,true);assert.equal(add(m,'fresh.bin',16*MiB),0);records.push({operation:'fresh-instance-reset',passed:true});
 m=await instance(candidate,true);
 for(const name of ['../escape','/tmp/escape','a//b','./a','a/../b','C:\\escape',Buffer.from([0xc0,0xaf,0])])assert.equal(add(m,name,1),1,String(name));
 assert.equal(add(m,'folder/font.ttf',1,1),1);assert.equal(add(m,'a.bin',1),0);assert.equal(add(m,'a.bin',1),1);
 assert.equal(add(m,'runtime.bin',32*MiB,1),0);assert.equal(add(m,'runtime2.bin',32*MiB,1),0);assert.equal(add(m,'runtime3.bin',32*MiB,1),0);
 assert.equal(add(m,'runtime-over.bin',1,1),2);assert.equal(add(m,'file-over.bin',32*MiB+1,1),2);
 records.push({operation:'unchanged-path-resource-and-file-limits',passed:true});
 m=await instance(candidate,true);for(let i=0;i<4096;i++)assert.equal(add(m,'f'+i,0),0);assert.equal(add(m,'too-many',0),2);
 records.push({operation:'unchanged-file-count',passed:true});
 fs.writeFileSync(out,JSON.stringify({schemaVersion:1,status:'pass',kind,records,conversionCalls:0,privateProofExportsAbsent:true},null,2)+'\n');console.log('Exact native '+kind+' admission/limits pass: '+records.length+' cases');
})().catch(e=>{console.error(e);process.exitCode=1;});
