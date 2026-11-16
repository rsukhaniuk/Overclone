'use strict';
// Real E1 PDF glyph observations precede query selection. Unsupported native
// geometry remains unsupported; no inferred/fitted matrix is manufactured.
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict'),{createHash}=require('node:crypto');
const [pdfRuntime,queryDir,casesFile,out]=process.argv.slice(2);assert.ok(!fs.existsSync(out));fs.mkdirSync(out,{recursive:true});
const pin=b=>({length:b.length,sha256:createHash('sha256').update(b).digest('hex')});
(async()=>{
 const loader=fs.readFileSync(path.join(pdfRuntime,'e1.js')),wasm=fs.readFileSync(path.join(pdfRuntime,'e1.wasm'));
 assert.equal(pin(loader).sha256,'040bdfac50656b3993f622614b73feaaba31d8253d077ff50f925f316c2f5f6b');assert.equal(pin(wasm).sha256,'1637c1a7798cab443334e559045d88f33864d89e0ff02eeb57b3da4f08f37c20');
 const e=await require(path.join(pdfRuntime,'e1.js'))({wasmBinary:wasm,print(){},printErr(){}}),ok=(n,...a)=>assert.equal(e['_'+n](...a),0,n);
 const input=b=>{const h=e._e1_alloc(b.length);assert.ok(h);e.HEAPU8.set(b,e._e1_data(h));return h;};
 const take=()=>{const h=e._e1_result(),n=e._e1_size(h),d=input(Buffer.alloc(n));ok('e1_copy',h,d);const b=Buffer.from(e.HEAPU8.slice(e._e1_data(d),e._e1_data(d)+n));ok('e1_release',d);ok('e1_release',h);return JSON.parse(b);};
 const rows=[];
 for(const c of JSON.parse(fs.readFileSync(casesFile))){
  const pdf=fs.readFileSync(path.join(c.folder,'output.pdf')),map=fs.readFileSync(c.map),source=fs.readFileSync(c.source),budget=Buffer.alloc(36);[1,36,8388608,32,100000,256,4000000,8388608,64].forEach((n,i)=>budget.writeUInt32LE(n,i*4));
  const d=input(pdf),b=input(budget);ok('e1_open',d,b);const doc=e._e1_result();ok('e1_release',d);ok('e1_release',b);const pages=[];
  for(let i=0;i<e._e1_document_pages(doc);i++){ok('e1_page',doc,i);const pg=e._e1_result();ok('e1_facts',pg);const facts=take();ok('e1_text',pg);const text=take();pages.push({facts,...text});ok('e1_close',pg);}ok('e1_close',doc);
  const qmWasm=fs.readFileSync(path.join(queryDir,'synctex-query.wasm')),q=await require(path.join(queryDir,'synctex-query.js'))({noInitialRun:true,instantiateWasm(i,d){const w=new WebAssembly.Instance(new WebAssembly.Module(qmWasm),i);d(w);return w.exports;}});assert.equal(q._sq_area64_abi(),1);
  const put=b=>{const p=q._malloc(b.length||1);q.HEAPU8.set(b,p);return p;};let p=put(map);assert.equal(q._sq_load(p,map.length),0);q._free(p);
  const result=()=>{const n=q._sq_result_length();assert.ok(n>0&&n<=32768);const p=q._malloc(n);assert.equal(q._sq_result_copy(p,n),0);const bytes=Buffer.from(q.HEAPU8.slice(p,p+n));q._free(p);const value=JSON.parse(bytes);assert.equal(value.truncated,false);return value;};
  const geometryBytes=fs.readFileSync(path.join(c.folder,'geometry.json')),geometry=geometryBytes.length?JSON.parse(geometryBytes):null,observations=[];
  const targets=[['resolved','тези/my thesis.tex',source.toString().split('\n').findIndex(s=>s.includes('Portable compiler fixture'))+1],['Included file','chapter.tex',2]];
  for(const[word,file,line]of targets){assert.ok(line>0);const hits=pages.flatMap((pg,i)=>{const at=pg.text.indexOf(word);return at>=0?[{pg,page:i+1,index:at}]:[];});assert.equal(hits.length,1,word);const hit=hits[0],char=hit.pg.characters.find(x=>x.index===hit.index);assert.equal(char.boxStatus,'available');const[x0,y0,x1,y1]=char.box;assert.ok(x0<x1&&y0<y1);const pdfPoint=[(x0+x1)/2,(y0+y1)/2];
   const name=Buffer.from(file);p=put(name);assert.equal(q._sq_forward(p,name.length,line,1,-1),0);q._free(p);const forward=result();assert.ok(forward.matches.length);
   const native=geometry?.pages.find(p=>p.page===hit.page);let inverse=null,queryPoint=null,status='unsupported-native-geometry';
   if(c.kind==='pdf'){
    const f=hit.pg.facts;assert.deepEqual(f.media,[0,0,612,792]);assert.equal(f.rotation,0);assert.equal(f.cropDefaulted,true);queryPoint=[pdfPoint[0],f.media[3]-pdfPoint[1]];status='legacy-zero-origin-pdf';
   }else if(native?.status==='available'){
    const m=native.writerToPdfRaw,det=m[0]*m[3]-m[1]*m[2];assert.ok(Number.isFinite(det)&&det!==0);queryPoint=[(m[3]*(pdfPoint[0]-m[4])-m[2]*(pdfPoint[1]-m[5]))/det,(-m[1]*(pdfPoint[0]-m[4])+m[0]*(pdfPoint[1]-m[5]))/det];status='native-writer-geometry';
   }
   if(queryPoint){assert.equal(q._sq_inverse(hit.page,...queryPoint),0);inverse=result();assert.ok(inverse.matches.length);const got=inverse.matches[0];assert.ok([file,'./'+file,'/work/'+file,'/work/project/'+file,'/work/project/./'+file].includes(got.file),got.file);assert.equal(got.line,line,'exact source line; no tolerance widened');}
   observations.push({word,file,line,page:hit.page,glyph:char,pdfPoint,queryPoint,status,forward,inverse});
  }
  assert.equal(q._sq_close(),0);const row={name:c.name,kind:c.kind,fixture:c.fixture,pdf:pin(pdf),map:pin(map),source:pin(source),geometry:geometry?{...pin(geometryBytes),pages:geometry.pages}:null,pageFacts:pages.map(p=>p.facts),observations};rows.push(row);fs.writeFileSync(path.join(out,c.name+'.json'),JSON.stringify(row,null,2)+'\n');console.log(c.name+' '+observations.map(o=>o.status).join(','));
 }
 assert.equal(e._e1_live(),0);const receipt={schemaVersion:1,status:'evidence-complete',scope:'actual pinned E1 glyph points + unfiltered exact area64 query; unsupported geometry remains an explicit unclosed cell',testSource:pin(fs.readFileSync(__filename)),pdfRuntime:{loader:pin(loader),wasm:pin(wasm)},query:{loader:pin(fs.readFileSync(path.join(queryDir,'synctex-query.js'))),wasm:pin(fs.readFileSync(path.join(queryDir,'synctex-query.wasm')))},rows};fs.writeFileSync(path.join(out,'receipt.json'),JSON.stringify(receipt,null,2)+'\n');
})().catch(e=>{console.error(e);process.exitCode=1;});
