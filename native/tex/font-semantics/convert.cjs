'use strict';
// Test-only explicit semantic converter candidate, with exact built-output and resource verification.
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto'),assert=require('node:assert/strict');
const [runtime,input,out,overlay,component]=process.argv.slice(2);
const expectRefusal=process.env.EXPECT_REFUSAL==='1';
if(!out)throw Error('usage: node convert.cjs RUNTIME INPUT.dvi OUT.pdf');
if(fs.existsSync(out))throw Error('fresh output file required');
const hash=b=>crypto.createHash('sha256').update(b).digest('hex');
const pins=JSON.parse(fs.readFileSync(path.join(runtime,'pins.json'))),base=path.join(runtime,'files');
const extras=overlay?JSON.parse(fs.readFileSync(path.join(overlay,'manifest.json'))).files:[];
for(const p of extras){assert.ok(!pins.some(x=>x.name===p.name));pins.push(p);}
function checked(p){const root=extras.some(x=>x.name===p.name)?path.join(overlay,'files'):base;const b=fs.readFileSync(path.join(root,p.name));assert.equal(b.length,p.length);assert.equal(hash(b),p.sha256);return b;}
(async()=>{
  const stem=input.endsWith('.xdv')?'xdv-map-converter':'dvi-map-converter',entry=input.endsWith('.xdv')?'main.xdv':'main.dvi';
  let loader=path.join(base,stem+'.js'),wasm;
  if(component){
    const manifest=JSON.parse(fs.readFileSync(path.join(component,'manifest.json')));
    for(const n of ['oc-dvi.js','oc-dvi.wasm']){const b=fs.readFileSync(path.join(component,n));assert.equal(b.length,manifest.outputs[n].bytes);assert.equal(hash(b),manifest.outputs[n].sha256);}
    loader=path.join(component,'oc-dvi.js');wasm=fs.readFileSync(path.join(component,'oc-dvi.wasm'));
  }else{checked(pins.find(p=>p.name===stem+'.js'));wasm=checked(pins.find(p=>p.name===stem+'.wasm'));}
  const logs=[];
  const m=await require(loader)({instantiateWasm(imports,done){
    const instance=new WebAssembly.Instance(new WebAssembly.Module(wasm),imports);done(instance);return instance.exports;
  },print:s=>logs.push(String(s)),printErr:s=>logs.push(String(s))});
  if(component){assert.equal(m._oc_dvi_font_semantics_abi(),1);assert.equal(m._oc_dvi_graphics_abi(),1);}
  const alloc=b=>{const p=m._malloc(Math.max(1,b.length));assert.ok(p);m.HEAPU8.set(b,p);return p;};
  function add(n,b,k){const p=alloc(Buffer.from(n+'\0')),q=alloc(b);const r=m._oc_dvi_add_file(p,q,b.length,k);m._free(p);m._free(q);assert.equal(r,0,n);}
  const resources=pins.filter(p=>!p.name.includes('/')&&(/\.(map|tfm|pfb|enc|vf)$/.test(p.name)||['glyphlist.txt','pdfglyphlist.txt'].includes(p.name)));
  for(const p of resources)add(p.name,checked(p),1);
  const bytes=fs.readFileSync(input);add(entry,bytes,0);
  assert.equal(m._oc_dvi_geometry_configure(1,701),1);
  const p=alloc(Buffer.from(entry+'\0')),status=m._oc_dvi_run(p);m._free(p);
  const log=Buffer.from(m.HEAPU8.slice(m._oc_dvi_log_data(),m._oc_dvi_log_data()+m._oc_dvi_log_size()));fs.writeFileSync(out+'.log',log);
  if(!expectRefusal)assert.equal(status,0,log.toString()+'\n'+logs.join('\n'));
  const pdf=Buffer.from(m.HEAPU8.slice(m._oc_dvi_pdf_data(),m._oc_dvi_pdf_data()+m._oc_dvi_pdf_size()));
  if(!expectRefusal||pdf.length)assert.equal(pdf.subarray(0,5).toString(),'%PDF-');fs.writeFileSync(out,pdf);
  const size=m._oc_dvi_geometry_size();
  const geometry=size?JSON.parse(Buffer.from(m.HEAPU8.slice(m._oc_dvi_geometry_data(),m._oc_dvi_geometry_data()+size))):null;
  fs.writeFileSync(out+'.json',JSON.stringify({schemaVersion:1,status,input:{length:bytes.length,sha256:hash(bytes)},pdf:{length:pdf.length,sha256:hash(pdf)},
    semantic:component?{abi:m._oc_dvi_font_semantics_abi(),status:m._oc_dvi_font_semantics_status(),spans:m._oc_dvi_font_semantics_spans()}:null,
    converter:component?{loader:hash(fs.readFileSync(loader)),wasm:hash(wasm)}:pins.filter(p=>p.name.startsWith(stem+'.')),resources,geometry},null,2)+'\n');
  if(expectRefusal)assert.ok(!geometry||geometry.pages.every(p=>p.status==='unsupported'&&p.writerToPdfRaw===null),'unsafe geometry must refuse');
  console.log(JSON.stringify({out,status,pdfSha256:hash(pdf)}));
})().catch(e=>{console.error(e);process.exitCode=1;});
