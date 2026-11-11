'use strict';
// Genuine retained engine-produced map + independently PDF-selected requests.
// Preserve every native candidate; no map/source/coordinate/output rewriting.
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict'),{createHash}=require('node:crypto');
const [baseline,candidate,map,requests,out]=process.argv.slice(2);assert.ok(!fs.existsSync(out));fs.mkdirSync(out,{recursive:true});
const pin=b=>({length:b.length,sha256:createHash('sha256').update(b).digest('hex')});
async function query(dir){
 const loader=fs.readFileSync(path.join(dir,'synctex-query.js')),wasm=fs.readFileSync(path.join(dir,'synctex-query.wasm'));
 const m=await require(path.join(dir,'synctex-query.js'))({noInitialRun:true,instantiateWasm(i,d){const w=new WebAssembly.Instance(new WebAssembly.Module(wasm),i);d(w);return w.exports;}});
 assert.equal(m._sq_version(),1);if(dir===candidate)assert.equal(m._sq_area64_abi(),1);else assert.equal(typeof m._sq_area64_abi,'undefined');
 const put=b=>{const p=m._malloc(b.length||1);assert.ok(p);m.HEAPU8.set(b,p);return p;},raw=fs.readFileSync(map),p=put(raw);assert.equal(m._sq_load(p,raw.length),0);m._free(p);const rows=[];
 for(const q of JSON.parse(fs.readFileSync(requests))){
  let rc;if(q.direction==='inverse')rc=m._sq_inverse(q.page,q.h,q.v);else{const b=Buffer.from(q.file),p=put(b);rc=m._sq_forward(p,b.length,q.line,q.column??1,q.pageHint??-1);m._free(p);}assert.equal(rc,0,q.name);
  const n=m._sq_result_length();assert.ok(n>0&&n<=32768);const p=m._malloc(n);assert.equal(m._sq_result_copy(p,n),0);const bytes=Buffer.from(m.HEAPU8.subarray(p,p+n));m._free(p);const result=JSON.parse(bytes);assert.equal(result.truncated,false);rows.push({request:q,result,raw:pin(bytes)});
  fs.writeFileSync(path.join(out,(dir===candidate?'candidate-':'baseline-')+q.name+'.json'),bytes);
 }
 // Actual input/copy/lifecycle guards retain no stale result.
 assert.equal(m._sq_inverse(0,0,0),-1);assert.equal(m._sq_result_length(),0);assert.equal(m._sq_inverse(1,NaN,0),-1);assert.equal(m._sq_result_length(),0);
 assert.equal(m._sq_close(),0);assert.equal(m._sq_close(),0);assert.equal(m._sq_inverse(1,0,0),-2);assert.equal(m._sq_result_length(),0);
 return{loader:pin(loader),wasm:pin(wasm),rows};
}
(async()=>{
 const before=await query(baseline),after=await query(candidate);assert.equal(before.rows.length,after.rows.length);let corrected=0;
 for(let i=0;i<after.rows.length;i++){
  const a=before.rows[i],b=after.rows[i],q=b.request;assert.deepEqual(a.request,q);
  if(q.direction==='forward')assert.deepEqual(a.result,b.result,'forward candidate bytes/order unchanged');
  else{assert.ok(b.result.matches.length);const got=b.result.matches[0];assert.ok([q.expectedFile,'./'+q.expectedFile,'/work/'+q.expectedFile].includes(got.file));assert.equal(got.line,q.expectedLine,'exact expected native filename/line, no tolerance relaxation');if(a.result.matches[0].line!==got.line)corrected++;}
 }
 assert.ok(corrected>=2,'both independently observed root points expose retained overflow');
 const receipt={status:'pass',scope:'actual retained package map; fixed-width native arithmetic only, no browser or producer change',map:pin(fs.readFileSync(map)),requests:pin(fs.readFileSync(requests)),testSource:pin(fs.readFileSync(__filename)),correctedRootPoints:corrected,before,after};
 fs.writeFileSync(path.join(out,'receipt.json'),JSON.stringify(receipt,null,2)+'\n');console.log(JSON.stringify({status:'pass',correctedRootPoints:corrected,queries:after.rows.length}));
})().catch(e=>{console.error(e);process.exitCode=1;});
