'use strict';
// Test-only use of the actual accepted E1 ABI, independent of expected source text.
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto'),assert=require('node:assert/strict');
const [runtime,pdf,out]=process.argv.slice(2);
if(!out)throw Error('usage: node renderer.cjs PDF_RUNTIME_FILES PDF OUT');
if(fs.existsSync(out))throw Error('fresh output file required');
const hash=b=>crypto.createHash('sha256').update(b).digest('hex');
(async()=>{
  const loader=fs.readFileSync(path.join(runtime,'e1.js')),wasm=fs.readFileSync(path.join(runtime,'e1.wasm'));
  assert.equal(hash(loader),'040bdfac50656b3993f622614b73feaaba31d8253d077ff50f925f316c2f5f6b');
  assert.equal(hash(wasm),'1637c1a7798cab443334e559045d88f33864d89e0ff02eeb57b3da4f08f37c20');
  const m=await require(path.join(runtime,'e1.js'))({wasmBinary:wasm,print:()=>{},printErr:()=>{}});
  const check=(n,...a)=>assert.equal(m['_'+n](...a),0,n);
  function input(b){const h=m._e1_alloc(b.length);assert.ok(h);m.HEAPU8.set(b,m._e1_data(h));return h;}
  function take(){const h=m._e1_result(),n=m._e1_size(h),d=input(Buffer.alloc(n));check('e1_copy',h,d);const b=Buffer.from(m.HEAPU8.slice(m._e1_data(d),m._e1_data(d)+n));check('e1_release',d);check('e1_release',h);return b;}
  const raw=Buffer.alloc(36);[1,36,8388608,32,100000,256,4000000,8388608,64].forEach((n,i)=>raw.writeUInt32LE(n,i*4));
  const bytes=fs.readFileSync(pdf),data=input(bytes),budget=input(raw);
  check('e1_open',data,budget);const doc=m._e1_result();check('e1_release',data);check('e1_release',budget);
  const count=m._e1_document_pages(doc),pages=[];
  for(let i=0;i<count;i++){
    check('e1_page',doc,i);const page=m._e1_result();check('e1_facts',page);const facts=JSON.parse(take());
    check('e1_text',page);const text=JSON.parse(take());const search={};
    for(const word of ['Привіт','Іі','ASCII I i']){
      const query=input(Buffer.from(word+'\0','utf16le'));check('e1_search',page,query);search[word]=JSON.parse(take());check('e1_release',query);
    }
    check('e1_raster',page,612,792);const raster=take();
    pages.push({facts,...text,search,raster:{length:raster.length,sha256:hash(raster)}});check('e1_close',page);
  }
  check('e1_close',doc);assert.equal(m._e1_live(),0);
  fs.writeFileSync(out,JSON.stringify({schemaVersion:1,pdf:{length:bytes.length,sha256:hash(bytes)},runtime:{loader:hash(loader),wasm:hash(wasm)},pageCount:count,pages},null,2)+'\n');
  console.log(JSON.stringify({out,pageCount:count,text:pages.map(x=>x.text)}));
})().catch(e=>{console.error(e);process.exitCode=1;});
