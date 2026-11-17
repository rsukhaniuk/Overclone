'use strict';
// QA only: real pinned PDFium character facts and actual native SyncTeX calls.
// Run after compiling the exact CRLF fixture; never rewrites native functions.
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict'),{createHash}=require('node:crypto');
const [base,pdfRuntime,queryDir]=process.argv.slice(2),hash=b=>createHash('sha256').update(b).digest('hex');
(async()=>{
 const loader=fs.readFileSync(path.join(pdfRuntime,'e1.js')),wasm=fs.readFileSync(path.join(pdfRuntime,'e1.wasm'));
 assert.equal(hash(loader),'040bdfac50656b3993f622614b73feaaba31d8253d077ff50f925f316c2f5f6b');
 assert.equal(hash(wasm),'1637c1a7798cab443334e559045d88f33864d89e0ff02eeb57b3da4f08f37c20');
 const e=await require(path.join(pdfRuntime,'e1.js'))({wasmBinary:wasm,print(){},printErr(){}}),ok=(n,...a)=>assert.equal(e['_'+n](...a),0,n);
 const input=b=>{const h=e._e1_alloc(b.length);assert.ok(h);e.HEAPU8.set(b,e._e1_data(h));return h;};
 const take=()=>{const h=e._e1_result(),n=e._e1_size(h),d=input(Buffer.alloc(n));ok('e1_copy',h,d);const b=Buffer.from(e.HEAPU8.slice(e._e1_data(d),e._e1_data(d)+n));ok('e1_release',d);ok('e1_release',h);return JSON.parse(b);};
 const pdf=fs.readFileSync(path.join(base,'native01/output.pdf')),map=fs.readFileSync(path.join(base,'native01/raw.synctex'));
 const budget=Buffer.alloc(36);[1,36,8388608,512,100000,256,4000000,8388608,64].forEach((n,i)=>budget.writeUInt32LE(n,i*4));
 const d=input(pdf),b=input(budget);ok('e1_open',d,b);const doc=e._e1_result();ok('e1_release',d);ok('e1_release',b);const pages=[];
 for(let i=0;i<e._e1_document_pages(doc);i++){ok('e1_page',doc,i);const page=e._e1_result();ok('e1_facts',page);const facts=take();ok('e1_text',page);const text=take();pages.push({facts,text});ok('e1_close',page);}ok('e1_close',doc);assert.equal(e._e1_live(),0);
 const qloader=fs.readFileSync(path.join(queryDir,'synctex-query.js')),qwasm=fs.readFileSync(path.join(queryDir,'synctex-query.wasm'));
 assert.equal(hash(qloader),'42f9467bb0d03dc488decab6baec00c2a78f4f524dc18a5a1b912ca55cdcaafd');assert.equal(hash(qwasm),'974212d6edbdad96e9733d854e427883a7b16fc09202e8468baa9d3cdda410b8');
 const q=await require(path.join(queryDir,'synctex-query.js'))({noInitialRun:true,instantiateWasm(i,done){const w=new WebAssembly.Instance(new WebAssembly.Module(qwasm),i);done(w);return w.exports;}});
 assert.equal(q._sq_area64_abi(),1);const put=b=>{const p=q._malloc(b.length||1);q.HEAPU8.set(b,p);return p;};let p=put(map);assert.equal(q._sq_load(p,map.length),0);q._free(p);
 const result=()=>{const n=q._sq_result_length(),p=q._malloc(n);assert.ok(n>0&&n<=32768);assert.equal(q._sq_result_copy(p,n),0);const b=Buffer.from(q.HEAPU8.slice(p,p+n));q._free(p);const r=JSON.parse(b);assert.equal(r.truncated,false);return r;};
 const source=fs.readFileSync(path.join(base,'project/тези/my thesis.tex'),'utf8'),rows=[];
 for(const literal of ['Orbit comparison','Engine comparison']){
  const line=source.split(/\r\n/).findIndex(s=>s.includes('\\caption{'+literal+'}'))+1;assert.ok(line>0);
  const file='тези/my thesis.tex',name=Buffer.from(file);p=put(name);assert.equal(q._sq_forward(p,name.length,line,1,-1),0);q._free(p);const forward=result();
  const hits=pages.flatMap((pg,i)=>{const all=pg.text.characters.map(c=>String.fromCodePoint(c.unicode)).join(''),at=all.indexOf(literal);assert.equal(all.indexOf(literal,at+1),-1);return at<0?[]:[{pg,page:i+1,at}];});assert.equal(hits.length,1);
  const hit=hits[0];assert.equal(hit.pg.facts.media[0],0);assert.equal(hit.pg.facts.media[1],0);const chars=hit.pg.text.characters.slice(hit.at,hit.at+literal.length).filter(c=>!/^\s$/u.test(String.fromCodePoint(c.unicode)));
  const bounds=forward.matches[0].box;assert.equal(forward.matches[0].line,line);assert.equal(forward.matches[0].page,hit.page);
  const inverses=[];
  for(const c of [chars[0],chars.at(-1)]){assert.equal(c.boxStatus,'available');const box=c.box,h=(box[0]+box[2])/2,v=hit.pg.facts.media[3]-(box[1]+box[3])/2;assert.ok(h>=bounds.h&&h<=bounds.h+bounds.width&&v>=bounds.v-bounds.height&&v<=bounds.v+bounds.depth);assert.equal(q._sq_inverse(hit.page,h,v),0);const reply=result();assert.equal(reply.matches[0].line,line);assert.ok([file,'./'+file,'/work/'+file].includes(reply.matches[0].file));inverses.push({page:hit.page,h,v,reply});}
  rows.push({literal,file,line,page:hit.page,start:hit.at,end:hit.at+literal.length,forward,inverses});
 }
 assert.equal(q._sq_close(),0);
 const receipt={schemaVersion:1,status:'actual-native-fixture-recorded',scope:'Synthetic root-caption text/map correlation only; Unicode is in exact CRLF source context, not a rendered Cyrillic-font acceptance claim. No glyph-ink measurement.',pdf:{length:pdf.length,sha256:hash(pdf)},map:{length:map.length,sha256:hash(map)},pdfRuntime:{loader:hash(loader),wasm:hash(wasm)},query:{loader:hash(qloader),wasm:hash(qwasm)},source:hash(Buffer.from(source)),rustResponse:hash(fs.readFileSync(path.join(base,'response.json'))),pages,rows};
 fs.writeFileSync(path.join(base,'native-fixture.json'),JSON.stringify(receipt,null,2)+'\n');console.log(JSON.stringify({status:receipt.status,pdf:receipt.pdf,map:receipt.map,rows:rows.map(r=>({literal:r.literal,line:r.line,start:r.start,end:r.end}))}));
})().catch(error=>{console.error(error);process.exitCode=1;});
