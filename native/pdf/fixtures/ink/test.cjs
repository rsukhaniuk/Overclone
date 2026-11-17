const fs=require('node:fs'),assert=require('node:assert/strict'),{createHash}=require('node:crypto');
const [runtime,fixtures,output]=process.argv.slice(2),path=require('node:path'),sha=b=>createHash('sha256').update(b).digest('hex');
(async()=>{
 const create=require(path.resolve(runtime,'e1.js'));const m=await create({wasmBinary:fs.readFileSync(path.join(runtime,'e1.wasm')),print:console.error,printErr:console.error});
 const alloc=bytes=>{const h=m._e1_alloc(bytes.length);assert.ok(h);m.HEAPU8.set(bytes,m._e1_data(h));return h;};
 const take=()=>{const h=m._e1_result(),n=m._e1_size(h),d=m._e1_alloc(n);assert.ok(d);assert.equal(m._e1_copy(h,d),0);const bytes=m.HEAPU8.slice(m._e1_data(d),m._e1_data(d)+n);m._e1_release(d);m._e1_release(h);return bytes;};
 const json=()=>JSON.parse(new TextDecoder('utf8',{fatal:true}).decode(take()));
 const results=[];
 for(const name of ['rect','curve','painted-space','painted-nbsp','actualtext-space','generated-space','cross-media','cross-crop','inside-crop','tight-crop','nested-form','page-group','rect-scale','rect-shear','curve-shear','rect-mirror','curve-rotate','clip','invisible','stroke','occluded','background','cancelled','deduplicated','overprint-objects']){
  const budget=Buffer.alloc(36);[1,36,8388608,512,100000,256,4000000,16777216,64].forEach((n,i)=>budget.writeUInt32LE(n,i*4));
  const pdf=fs.readFileSync(path.join(fixtures,name+'.pdf')),i=alloc(pdf),b=alloc(budget);
  assert.equal(m._e1_open(i,b),0);const doc=m._e1_result();m._e1_release(i);m._e1_release(b);assert.equal(m._e1_page(doc,0),0);const page=m._e1_result();
  assert.equal(m._e1_layout_capabilities(page),0);const capabilities=json();
  assert.equal(m._e1_layout_facts(page),0);const raw=take(),facts=JSON.parse(new TextDecoder().decode(raw));
  fs.writeFileSync(path.join(output,'native-v2-'+name+'-batch.json'),JSON.stringify({schemaVersion:1,documentSha256:sha(pdf),pages:[{capabilities,facts}]}));
  const a=facts.characters.find(c=>c.unicode===({'painted-space':32,'painted-nbsp':160,'actualtext-space':32}[name]??65));
  assert.equal(facts.schemaVersion,2);assert.equal(capabilities.schemaVersion,2);assert.equal(capabilities.glyphInkMethod,"embedded-static-font-outline-opaque-fill");
  for(const ch of facts.characters){if(ch.inkStatus==="available"){assert.ok(Number.isFinite(ch.inkUncertainty.absoluteErrorRaw)&&ch.inkUncertainty.absoluteErrorRaw>=0&&ch.inkUncertainty.absoluteErrorRaw<=1000000);assert.equal(ch.inkUncertainty.method,"native-outline-fill-witness");}else{assert.equal(ch.inkUncertainty,null);assert.equal(ch.inkBox,null);}}
  assert.equal(m._e1_raster(page,1000,2000),0);const raster=take();let x0=1000,y0=2000,x1=-1,y1=-1;
  for(let y=0;y<2000;y++)for(let x=0;x<1000;x++){const at=16+4*(y*1000+x);if(raster[at]<250||raster[at+1]<250||raster[at+2]<250){x0=Math.min(x0,x);x1=Math.max(x1,x);y0=Math.min(y0,y);y1=Math.max(y1,y);}}
  const pixels=x1<0?null:[x0*72/1000,144-(y1+1)*144/2000,(x1+1)*72/1000,144-y0*144/2000];
  results.push({name,pdfSha256:sha(pdf),capabilities,a,rasterInkBox:pixels,factsSha256:sha(raw)});
  const known={rect:[10,20,17.2,29.6],curve:[10,20,17.2,26],
    'inside-crop':[10,20,17.2,29.6],'rect-scale':[25,67,39.4,95.8],'rect-shear':[16,22,26.08,33.04],
    'curve-shear':[16,22,23.2,28.7416],'rect-mirror':[32.8,20,40,29.6],
    'curve-rotate':[34,20,40,27.2],background:[10,20,17.2,29.6]};
  if(name==='painted-space'||name==='painted-nbsp'){
    assert.equal(a.inkStatus,'available',name);assert.ok(a.inkBox);assert.ok(pixels);assert.equal(facts.characters.length,1);
  }else if(name==='generated-space'){
    const space=facts.characters.find(c=>c.unicode===32);assert.ok(space);assert.equal(space.inkStatus,'missing');assert.equal(space.inkBox,null);
    assert.ok(facts.characters.filter(c=>c.unicode===65).every(c=>c.inkStatus==='available'));
  }else if(known[name]){
    assert.equal(a.inkStatus,'available',name);assert.ok(a.inkBox);
    for(let j=0;j<4;j++){
      assert.ok(Math.abs(a.inkBox[j]-known[name][j])<=.012,`${name} analytic side${j}`);
      if(name!=='inside-crop')assert.ok(Math.abs(a.inkBox[j]-pixels[j])<=.144,`${name} independent raster side${j}`);
    }
  }else{assert.notEqual(a?.inkStatus,'available',name);assert.equal(a?.inkBox,null,name);}
  console.log(JSON.stringify(results.at(-1)));
  assert.equal(m._e1_close(page),0);assert.equal(m._e1_close(doc),0);assert.equal(m._e1_live(),0);
 }
 fs.writeFileSync(path.join(output,'ink-results.json'),JSON.stringify(results,null,2));
})().catch(e=>{console.error(e);process.exitCode=1;});
