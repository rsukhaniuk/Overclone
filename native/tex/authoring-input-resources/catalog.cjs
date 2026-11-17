'use strict';
// Build-time filesystem bridge. The retained C++/FreeType extractor owns metadata.
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict'),crypto=require('node:crypto');
const [tool,base,extra,output]=process.argv.slice(2),hash=b=>crypto.createHash('sha256').update(b).digest('hex');
const lock=JSON.parse(fs.readFileSync(path.join(__dirname,'resources.lock.json'))),contract=lock.catalogue;
function checked(file,pin){const b=fs.readFileSync(file);assert.equal(b.length,pin.length);assert.equal(hash(b),pin.sha256);return b;}
(async()=>{
 assert.ok(output&&!fs.existsSync(output),'fresh output file required');
 for(const pin of contract.extractorComponents)checked(path.join(path.dirname(tool),pin.name),pin);
 assert.equal(path.basename(tool),'font-catalog.js');
 const old=checked(path.join(base,'files',contract.file),contract.parent),names=[];
 const lines=old.toString('utf8').trimEnd().split('\n');let at=0;
 while(at<lines.length){assert.equal(Number(lines[at++]),names.length+1);const font=lines[at++];assert.ok(font.startsWith('/tex/'));names.push(font.slice(5));assert.equal(lines[at++],'0');for(let k=0;k<3;k++){const count=Number(lines[at++]);assert.ok(count>0&&count<10);at+=count;}at+=13;}
 assert.equal(at,lines.length);assert.equal(names.length,contract.originalFontCount);
 const originalNames=new Set(names);names.push(...contract.fonts.filter(p=>!originalNames.has(p.name)).map(p=>p.name));assert.equal(names.length,contract.fontCount);
 const printed=[],metadata=[],m=await require(path.resolve(tool))({wasmBinary:checked(path.join(path.dirname(tool),'font-catalog.wasm'),contract.extractorComponents[1]),print:s=>printed.push(String(s)),printErr:s=>metadata.push(String(s))});m.FS.mkdir('/fonts');
 for(const name of names){const pin=contract.fonts.find(p=>p.name===name);assert.ok(pin);m.FS.writeFile('/fonts/'+name,checked(path.join(originalNames.has(name)?base:extra,'files',name),pin));}
 assert.equal(m.callMain([...names]),0);assert.equal(metadata.length,contract.fontCount);
 const known=JSON.parse(fs.readFileSync(path.join(__dirname,contract.metadataReceiptFile)));assert.deepEqual(metadata.map(s=>s.split('\t')),known.metadata);
 const result=Buffer.from(printed.join('\n')+'\n');assert.ok(result.subarray(0,old.length).equals(old),'old catalogue prefix changed');assert.equal(result.length,contract.candidate.length);assert.equal(hash(result),contract.candidate.sha256);fs.writeFileSync(output,result);
 process.stdout.write(JSON.stringify({fontCount:names.length,originalPrefixByteExact:true,aliases:0,length:result.length,sha256:hash(result)})+'\n');
})().catch(e=>{process.stderr.write(String(e.stack||e)+'\n');process.exitCode=1;});
