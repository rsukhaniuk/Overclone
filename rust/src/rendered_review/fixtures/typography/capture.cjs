// Test-only native capture. No mocked layout replies or runtime text rewriting.
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict'),{createHash}=require('node:crypto');
const [runtime,file,out]=process.argv.slice(2),sha=b=>createHash('sha256').update(b).digest('hex');
(async()=>{
 const wasm=fs.readFileSync(path.join(runtime,'e1.wasm')),loader=fs.readFileSync(path.join(runtime,'e1.js'));
 const m=await require(path.resolve(runtime,'e1.js'))({wasmBinary:wasm,print:()=>{},printErr:()=>{}});
 const input=b=>{const h=m._e1_alloc(b.length);assert.ok(h);m.HEAPU8.set(b,m._e1_data(h));return h;};
 const check=(n,...args)=>assert.equal(m['_'+n](...args),0,n);
 const take=()=>{const h=m._e1_result(),n=m._e1_size(h),d=input(Buffer.alloc(n));check('e1_copy',h,d);const b=Buffer.from(m.HEAPU8.slice(m._e1_data(d),m._e1_data(d)+n));check('e1_release',d);check('e1_release',h);return JSON.parse(b);};
 const pdf=fs.readFileSync(file),raw=Buffer.alloc(36);[1,36,8388608,32,100000,256,4000000,16777216,64].forEach((v,i)=>raw.writeUInt32LE(v,i*4));
 const p=input(pdf),b=input(raw);check('e1_open',p,b);const doc=m._e1_result();check('e1_release',p);check('e1_release',b);check('e1_page',doc,0);const page=m._e1_result();
 check('e1_layout_capabilities',page);const capabilities=take();check('e1_layout_facts',page);const facts=take();
 const batch={schemaVersion:1,documentSha256:sha(pdf),pages:[{capabilities,facts}]};
 assert.equal(facts.characters.filter(c=>c.unicode===65&&c.inkStatus==='available').length,4);
 const bytes=Buffer.from(JSON.stringify(batch));fs.writeFileSync(out,bytes);
 fs.writeFileSync(out+'.lineage.json',JSON.stringify({fixtureKind:'synthetic-pdf-genuine-native-facts',pdf:{bytes:pdf.length,sha256:sha(pdf)},facts:{bytes:bytes.length,sha256:sha(bytes)},runtime:{loaderSha256:sha(loader),wasmSha256:sha(wasm)}},null,2)+'\n');
 check('e1_close',page);check('e1_close',doc);assert.equal(m._e1_live(),0);
})().catch(e=>{console.error(e);process.exitCode=1;});
