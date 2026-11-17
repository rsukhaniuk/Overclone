'use strict';
// Genuine converter execution on retained original native intermediates. Test
// tooling only; no engine/data/source rewrites or synthetic geometry response.
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict'),{createHash}=require('node:crypto');
const [runtime,converter,ir,project,out,expected]=process.argv.slice(2),sha=b=>createHash('sha256').update(b).digest('hex');assert.ok(!fs.existsSync(out));fs.mkdirSync(out,{recursive:true});
(async()=>{
 const wasm=fs.readFileSync(path.join(converter,'oc-dvi.wasm')),loader=fs.readFileSync(path.join(converter,'oc-dvi.js'));
 const m=await require(path.resolve(converter,'oc-dvi.js'))({noInitialRun:true,print(){},printErr(){},instantiateWasm(i,d){const w=new WebAssembly.Instance(new WebAssembly.Module(wasm),i);d(w);return w.exports;}});
 const input=b=>{const p=m._malloc(b.length||1);assert.ok(p);m.HEAPU8.set(b,p);return p;},str=s=>input(Buffer.from(s+'\0'));
 const inputs=[];let projectBytes=0;
 const add=(name,bytes,resource)=>{const p=str(name),q=input(bytes),rc=m._oc_dvi_add_file(p,q,bytes.length,resource);m._free(p);m._free(q);assert.equal(rc,0,name);if(!resource)projectBytes+=bytes.length;inputs.push({name,bytes:bytes.length,sha256:sha(bytes),resource});};
 const pins=JSON.parse(fs.readFileSync(path.join(runtime,'pins.json')));
 for(const p of pins){if(!(/\.(tfm|pfb|ttf|otf|vf|map|enc)$/.test(p.name)||['glyphlist.txt','pdfglyphlist.txt'].includes(p.name)))continue;const b=fs.readFileSync(path.join(runtime,'files',p.name));assert.equal(sha(b),p.sha256);add(p.name,b,1);}
 function walk(dir){for(const e of fs.readdirSync(dir,{withFileTypes:true})){const p=path.join(dir,e.name);assert.ok(!e.isSymbolicLink());if(e.isDirectory())walk(p);else{assert.ok(e.isFile());add(path.relative(project,p).split(path.sep).join('/'),fs.readFileSync(p),0);}}}walk(project);
 add('input.bin',fs.readFileSync(ir),0);assert.equal(m._oc_dvi_geometry_configure(1,501),1);const p=str('input.bin'),rc=m._oc_dvi_run(p);m._free(p);
 const read=(data,size)=>{const n=m[size]();assert.ok(n<=8388608);return Buffer.from(m.HEAPU8.slice(m[data](),m[data]()+n));};
 const pdf=read('_oc_dvi_pdf_data','_oc_dvi_pdf_size'),geometry=read('_oc_dvi_geometry_data','_oc_dvi_geometry_size'),log=read('_oc_dvi_log_data','_oc_dvi_log_size');
 const receipt={schemaVersion:1,converter:{loaderSha256:sha(loader),wasmSha256:sha(wasm)},inputBudgetAbi:m._oc_dvi_input_budget_abi?.()??null,projectBytes,inputs,nativeReturn:rc,output:{pdfBytes:pdf.length,pdfSha256:sha(pdf),geometryBytes:geometry.length,geometrySha256:sha(geometry)},geometry:geometry.length?JSON.parse(geometry):null};
 fs.writeFileSync(path.join(out,'output.pdf'),pdf);fs.writeFileSync(path.join(out,'geometry.json'),geometry);fs.writeFileSync(path.join(out,'output.log'),log);fs.writeFileSync(path.join(out,'receipt.json'),JSON.stringify(receipt,null,2)+'\n');
 if(expected==='output-limit'){assert.equal(rc,2);assert.equal(pdf.length,0);assert.equal(geometry.length,0);assert.equal(m._oc_dvi_status(),2);assert.equal(m._oc_dvi_run(str('input.bin')),3);assert.equal(m._oc_dvi_pdf_size(),0);}
 else if(expected==='pass')assert.equal(rc,0);
 else assert.equal(expected,'compare','explicit outcome required');
 console.log(JSON.stringify({nativeReturn:rc,projectBytes,pdfBytes:pdf.length,geometryBytes:geometry.length}));
})().catch(e=>{console.error(e);process.exitCode=1;});
