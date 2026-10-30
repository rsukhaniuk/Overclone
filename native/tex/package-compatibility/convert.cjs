// Test-only access to the accepted native DVI/XDV converter ABI.
'use strict';
const fs=require('node:fs'),assert=require('node:assert/strict'),crypto=require('node:crypto');
const [input,out,overlay]=process.argv.slice(2),base='/inputs/suite/files';
const sha=b=>crypto.createHash('sha256').update(b).digest('hex');
function json(file){try{return JSON.parse(fs.readFileSync(file));}catch(cause){throw new Error('Invalid JSON: '+file,{cause});}}
const pins=json('/inputs/suite/pins.json');
function bytes(p,root){const b=fs.readFileSync(root+'/'+p.name);assert.equal(b.length,p.length);assert.equal(sha(b),p.sha256);return b;}
const check=n=>bytes(pins.find(p=>p.name===n),base);
(async()=>{
  check('dvi-converter.js');
  const wasm=check('dvi-converter.wasm');
  const m=await require(base+'/dvi-converter.js')({instantiateWasm(imports,success){const instance=new WebAssembly.Instance(new WebAssembly.Module(wasm),imports);success(instance);return instance.exports;},print:()=>{},printErr:()=>{}});
  const alloc=b=>{const p=m._malloc(Math.max(1,b.length));m.HEAPU8.set(b,p);return p;};
  function add(n,b,k){const p=alloc(Buffer.from(n+'\0')),q=alloc(b);const r=m._oc_dvi_add_file(p,q,b.length,k);m._free(p);m._free(q);assert.equal(r,0,n);}
  const resources=[...pins.filter(p=>!p.name.includes('/')&&(/\.(map|tfm|pfb|enc|vf|otf|ttf)$/.test(p.name)||['glyphlist.txt','pdfglyphlist.txt'].includes(p.name))).map(p=>[p,base]),...json(overlay+'/manifest.json').files.filter(p=>/\.(map|tfm|pfb|enc|vf|otf|ttf)$/.test(p.name)).map(p=>[p,overlay+'/files'])];
  for(const [p,root]of resources)add(p.name,bytes(p,root),1);
  add('real.'+(input.endsWith('.xdv')?'xdv':'dvi'),fs.readFileSync(input),0);
  const p=alloc(Buffer.from('real.'+(input.endsWith('.xdv')?'xdv':'dvi')+'\0'));
  const status=m._oc_dvi_run(p);m._free(p);
  const log=Buffer.from(m.HEAPU8.slice(m._oc_dvi_log_data(),m._oc_dvi_log_data()+m._oc_dvi_log_size()));
  fs.writeFileSync(out+'.log',log);
  const pdf=Buffer.from(m.HEAPU8.slice(m._oc_dvi_pdf_data(),m._oc_dvi_pdf_data()+m._oc_dvi_pdf_size()));
  const report={status,nativeReturn:m._oc_dvi_native_return(),input:{length:fs.statSync(input).size,sha256:sha(fs.readFileSync(input))},pdf:{length:pdf.length,sha256:sha(pdf)},converter:pins.filter(p=>p.name.startsWith('dvi-converter.')),resources:resources.map(([p])=>p)};
  fs.writeFileSync(out+'.json',JSON.stringify(report,null,2)+'\n');console.log(JSON.stringify({status,pdf:report.pdf}));
  assert.equal(status,0,log.toString());assert.equal(pdf.subarray(0,5).toString(),'%PDF-');assert.ok(pdf.subarray(-1024).includes(Buffer.from('%%EOF')));
  fs.writeFileSync(out+'.pdf',pdf);
})().catch(e=>{console.error(e);process.exitCode=1;});
