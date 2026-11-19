'use strict';
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{createHash}=require('node:crypto');
const [runtime, pdfFile, out]=process.argv.slice(2);
const sha=bytes=>createHash('sha256').update(bytes).digest('hex');
const pin=bytes=>({bytes:bytes.length,sha256:sha(bytes)});
(async()=>{
    const loader=fs.readFileSync(path.join(runtime,'e1.js')),wasm=fs.readFileSync(path.join(runtime,'e1.wasm'));
    const m=await require(path.resolve(runtime,'e1.js'))({wasmBinary:wasm,print:()=>{},printErr:()=>{}});
    const call=(name,...args)=>assert.equal(m['_'+name](...args),0,name);
    const input=b=>{const h=m._e1_alloc(b.length);assert.ok(h);m.HEAPU8.set(b,m._e1_data(h));return h;};
    function take(operation,page){
        call(operation,page);const h=m._e1_result(),length=m._e1_size(h),copy=input(Buffer.alloc(length));
        call('e1_copy',h,copy);const b=Buffer.from(m.HEAPU8.slice(m._e1_data(copy),m._e1_data(copy)+length));
        call('e1_release',copy);call('e1_release',h);return b;
    }
    const pdf=fs.readFileSync(pdfFile),budget=Buffer.alloc(36);
    [1,36,8388608,32,100000,256,4000000,8388608,64].forEach((n,i)=>budget.writeUInt32LE(n,i*4));
    const p=input(pdf),b=input(budget);call('e1_open',p,b);const doc=m._e1_result();call('e1_release',p);call('e1_release',b);
    assert.equal(m._e1_document_pages(doc),4);
    const captures=[],summary=[];
    for(let i=0;i<4;i++){
        call('e1_page',doc,i);const page=m._e1_result();
        const capabilities=take('e1_layout_capabilities',page),facts=take('e1_layout_facts',page);
        const text=JSON.parse(facts);
        assert.equal(text.userUnit.value,2);
        const numbers=text.characters.filter(ch=>ch.unicode>=48&&ch.unicode<=57);
        assert.ok(numbers.every(ch=>ch.inkStatus==='available'));
        assert.deepEqual(numbers.map(ch=>String.fromCodePoint(ch.unicode)),i===3?['4','1']:[['1','2','1'][i]]);
        summary.push({pageIndex:i,numbers:numbers.map(ch=>({index:ch.index,literal:String.fromCodePoint(ch.unicode),ink:ch.inkBox,uncertainty:ch.inkUncertainty}))});
        captures.push(`{"capabilities":${capabilities},"facts":${facts}}`);
        call('e1_close',page);
    }
    call('e1_close',doc);assert.equal(m._e1_live(),0);
    const batch=Buffer.from(`{"schemaVersion":1,"documentSha256":"${sha(pdf)}","pages":[${captures.join(',')}]}`);
    fs.mkdirSync(out,{recursive:true});
    fs.writeFileSync(path.join(out,'p16-pagination-native-v2.json'),batch);
    fs.writeFileSync(path.join(out,'lineage.json'),JSON.stringify({pdf:pin(pdf),batch:pin(batch),runtime:{loader:pin(loader),wasm:pin(wasm)},pages:summary},null,2)+'\n');
    console.log(JSON.stringify(summary));
})().catch(error=>{console.error(error);process.exitCode=1;});
