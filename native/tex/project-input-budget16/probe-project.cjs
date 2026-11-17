'use strict';
// Read-only original-project admission probe; no TeX or converter run.
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict'),{createHash}=require('node:crypto');
const [converter,project,out]=process.argv.slice(2),sha=b=>createHash('sha256').update(b).digest('hex');
(async()=>{
 const wasm=fs.readFileSync(path.join(converter,'oc-dvi.wasm')),loader=fs.readFileSync(path.join(converter,'oc-dvi.js'));
 const m=await require(path.resolve(converter,'oc-dvi.js'))({noInitialRun:true,print(){},printErr(){},instantiateWasm(i,d){const w=new WebAssembly.Instance(new WebAssembly.Module(wasm),i);d(w);return w.exports;}});
 assert.equal(m._oc_dvi_input_budget_abi(),1);assert.equal(m._oc_dvi_project_input_limit(),16777216);assert.equal(m._oc_dvi_pdf_output_limit(),8388608);
 const files=[];function walk(dir){for(const e of fs.readdirSync(dir,{withFileTypes:true})){const p=path.join(dir,e.name);assert.ok(!e.isSymbolicLink());if(e.isDirectory())walk(p);else{assert.ok(e.isFile());files.push(p);}}}walk(project);files.sort();let total=0;const records=[];
 for(const file of files){const bytes=fs.readFileSync(file),name=path.relative(project,file).split(path.sep).join('/'),n=Buffer.from(name+'\0'),p=m._malloc(n.length),q=m._malloc(bytes.length||1);assert.ok(p&&q);m.HEAPU8.set(n,p);m.HEAPU8.set(bytes,q);const rc=m._oc_dvi_add_file(p,q,bytes.length,0);m._free(p);m._free(q);assert.equal(rc,0,name);total+=bytes.length;records.push({name,bytes:bytes.length,sha256:sha(bytes),nativeReturn:rc});}
 assert.equal(m._oc_dvi_pdf_size(),0);assert.ok(total<=16777216);
 const result={schemaVersion:1,status:'all-original-inputs-admitted-no-conversion',converter:{loaderSha256:sha(loader),wasmSha256:sha(wasm)},files:files.length,totalBytes:total,largestFileBytes:Math.max(...records.map(r=>r.bytes)),conversionCalls:0,records};
 fs.writeFileSync(out,JSON.stringify(result,null,2)+'\n');console.log(JSON.stringify({files:files.length,totalBytes:total,largestFileBytes:result.largestFileBytes,nativeAdmission:'pass',conversionCalls:0}));
})().catch(e=>{console.error(e);process.exitCode=1;});
