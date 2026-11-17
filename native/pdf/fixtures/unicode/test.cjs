const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict'),{createHash}=require('node:crypto');
const [runtime,dir,out]=process.argv.slice(2),sha=b=>createHash('sha256').update(b).digest('hex');
(async()=>{
 const m=await require(path.resolve(runtime,'e1.js'))({wasmBinary:fs.readFileSync(path.join(runtime,'e1.wasm')),print:()=>{},printErr:()=>{}});
 const input=b=>{const h=m._e1_alloc(b.length);assert.ok(h);m.HEAPU8.set(b,m._e1_data(h));return h;};
 const check=(name,...args)=>assert.equal(m['_'+name](...args),0,name);
 const take=()=>{const h=m._e1_result(),n=m._e1_size(h),d=input(Buffer.alloc(n));check('e1_copy',h,d);const b=Buffer.from(m.HEAPU8.slice(m._e1_data(d),m._e1_data(d)+n));check('e1_release',d);check('e1_release',h);return b;};
 const raw=Buffer.alloc(36);[1,36,8388608,32,100000,256,4000000,16777216,64].forEach((v,i)=>raw.writeUInt32LE(v,i*4));
 const runtimeIdentity={loaderSha256:sha(fs.readFileSync(path.join(runtime,'e1.js'))),wasmSha256:sha(fs.readFileSync(path.join(runtime,'e1.wasm')))};
 const manifest=JSON.parse(fs.readFileSync(path.join(dir,'manifest.json'))),results={};
 for(const [name,fixture] of Object.entries(manifest)){
  const pdf=fs.readFileSync(path.join(dir,name+'.pdf'));assert.equal(sha(pdf),fixture.sha256);
  const p=input(pdf),b=input(raw);check('e1_open',p,b);const doc=m._e1_result();check('e1_release',p);check('e1_release',b);check('e1_page',doc,0);const page=m._e1_result();
  check('e1_facts',page);const facts=JSON.parse(take());check('e1_text',page);const text=JSON.parse(take());const q=input(Buffer.from(fixture.expected+'\0','utf16le'));check('e1_search',page,q);const search=JSON.parse(take());check('e1_release',q);
  check('e1_raster',page,600,200);const raster=take();
  results[name]={pdfSha256:fixture.sha256,text:text.text,utf16:text.utf16,characterCount:text.characterCount,characters:text.characters,search,rasterSha256:sha(raster),expected:fixture.expected,pass:text.text===fixture.expected&&search.length>0};
  if(name==='normal-single-emoji'){
   assert.equal(text.characterCount,1);assert.equal(text.characters.length,1);assert.equal(text.characters[0].unicode,0x1f642);
   assert.deepEqual(text.utf16,[0xd83d,0xde42]);assert.deepEqual(search,[{start:0,count:1}]);
   fs.writeFileSync(path.join(path.dirname(out),'native-unicode-transport-v2.json'),JSON.stringify({schemaVersion:1,runtime:runtimeIdentity,pdfSha256:sha(pdf),pdfBase64:pdf.toString('base64'),facts,text,search},null,2)+'\n');
  }
  check('e1_close',page);check('e1_close',doc);assert.equal(m._e1_live(),0);
 }
 for(const name of ['malformed-high','malformed-low','malformed-pair']){
  const pdf=fs.readFileSync(path.join(dir,name+'.pdf')),p=input(pdf),b=input(raw);check('e1_open',p,b);const doc=m._e1_result();check('e1_release',p);check('e1_release',b);check('e1_page',doc,0);const page=m._e1_result();
  const textStatus=m._e1_text(page);assert.equal(textStatus,6,name+' must reject malformed Unicode, never truncate or replace');
  const layoutStatus=m._e1_layout_facts(page);assert.ok([6,8].includes(layoutStatus),name+' must reject invalid scalar facts');
  check('e1_close',page);check('e1_close',doc);assert.equal(m._e1_live(),0);
 }
 for(const [name,r] of Object.entries(results))assert.ok(r.pass,name);fs.writeFileSync(out,JSON.stringify(results,null,2)+'\n');console.log(JSON.stringify(Object.fromEntries(Object.entries(results).map(([k,v])=>[k,{pass:v.pass,text:v.text,expected:v.expected,search:v.search}]))));
})().catch(e=>{console.error(e);process.exitCode=1;});
