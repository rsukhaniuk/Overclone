'use strict';
// Explicit malformed/unsafe fixed-length SPECIAL mutations of genuine portable
// intermediates. These are negative controls, NEVER same-pass positive claims.
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict'),crypto=require('node:crypto');
const {convert}=require('../dvi-engine/convert-fixture.cjs');
const hash=b=>crypto.createHash('sha256').update(b).digest('hex');
const programs={
 'raw-balanced':'pdf:literal direct q 1 0 0 1 0 0 cm Q',
 'raw-unbalanced':'pdf:literal direct q',
 'unknown-special':'untrusted:program changes future BOP',
 'postscript':'ps: 1 0 0 1 20 30 concat',
 'image-dictionary':'pdf:image width 12mm (figure.png) << /OCUnsafe 1 >>',
 'image-tail':'pdf:image width 12mm (figure.png) q Q',
 'image-singular':'pdf:image matrix 0 0 0 0 0 0 (figure.png)',
 'userunit':'pdf:put @thispage << /UserUnit 2 >>',
 'media-origin':'pdf:put @thispage << /MediaBox [-20 -30 592 762] >>',
};
(async()=>{
 const out=process.env.RESULTS;assert.ok(out);assert.ok(!fs.existsSync(out));fs.mkdirSync(out,{recursive:true});const kind=process.env.KIND,source=fs.readFileSync(process.env.INTERMEDIATE);assert.ok(['dvi','xe'].includes(kind));
 const start=source.indexOf(Buffer.from('pdf:image '));assert.ok(start>2);let len,at;
 if(source[start-2]===239){len=source[start-1];at=start-2;}else if(source[start-3]===240){len=source.readUInt16BE(start-2);at=start-3;}else throw new Error('unexpected actual portable special encoding');assert.ok(source.subarray(start,start+len).includes(Buffer.from('figure.png')));
 const rows=[];
 for(const [name,program]of Object.entries(programs)){
  const b=Buffer.from(source),bytes=Buffer.from(program);assert.ok(bytes.length<=len);b.fill(32,start,start+len);bytes.copy(b,start);assert.equal(b.length,source.length);const file=path.join(out,name+'.'+(kind==='xe'?'xdv':'dvi'));fs.writeFileSync(file,b);let m;
  const result=await convert(process.env.CONVERTER,process.env.RESOURCES,file,x=>{m=x;assert.equal(x._oc_dvi_graphics_abi(),1);const n=x._malloc(11),image=fs.readFileSync(path.join(__dirname,'../../../src/Client/wwwroot/icons/icon-192.png')),p=x._malloc(image.length);x.HEAPU8.set(Buffer.from('figure.png\0'),n);x.HEAPU8.set(image,p);assert.equal(x._oc_dvi_add_file(n,p,image.length,0),0);x._free(n);x._free(p);assert.equal(x._oc_dvi_geometry_configure(1,701),1);});
  const n=m._oc_dvi_geometry_size();let geometry=null;if(n){try{geometry=JSON.parse(Buffer.from(m.HEAPU8.subarray(m._oc_dvi_geometry_data(),m._oc_dvi_geometry_data()+n)));}catch(e){throw new Error('bad native sidecar',{cause:e});}assert.ok(geometry.pages.every(p=>p.status==='unsupported'&&p.writerToPdfRaw===null));}
  assert.ok(n===0||geometry);fs.writeFileSync(path.join(out,name+'.log'),result.log);if(result.pdf.length)fs.writeFileSync(path.join(out,name+'.pdf'),result.pdf);
  assert.equal(m._oc_dvi_run(0),3);assert.equal(m._oc_dvi_geometry_size(),0);rows.push({name,program,actualNative:result.receipt,geometry,inputSha256:hash(b),sourceIntermediateSha256:hash(source),negativeMutation:{opcode:source[at],offset:at,originalSpecialLength:len},pdfBytes:result.pdf.length});
 }
 fs.writeFileSync(path.join(out,'receipt.json'),JSON.stringify({schemaVersion:1,status:'pass',kind,rows,scope:'Actual parsed negative native specials; no guessed/partial available geometry.'},null,2)+'\n');console.log(kind+' all9 unsafe native special controls refused');
})().catch(e=>{console.error(e);process.exitCode=1;});
