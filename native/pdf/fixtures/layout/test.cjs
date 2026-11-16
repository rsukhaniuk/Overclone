// Actual E1/PDFium native ABI fixtures. Usage: node test.cjs /candidate/e1.js.
// Every fixture is a complete PDF object/xref graph; no mocked PDF facts.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const {createHash} = require('node:crypto');
const sha=bytes=>createHash('sha256').update(bytes).digest('hex');
function pdf({unit='',parentUnit='',media='0 0 72 144',crop='',rotate=0,text='A',extra=[],content=null,font=null}={}) {
    // PDFium bounds each PDF string at32767 chars. Use separate in-page text
    // objects to exercise the adapter's document-independent100000-char cap.
    const chunks=text.match(/.{1,30000}/g)??[''];
    const stream=content??chunks.map((s,i)=>`BT /F1 12 Tf 10 ${20+i*20} Td (${s}) Tj ET`).join('\n');
    const objects = [
        '<< /Type /Catalog /Pages 2 0 R >>',
        `<< /Type /Pages /Kids [3 0 R] /Count 1 ${parentUnit ? `/UserUnit ${parentUnit}` : ''} >>`,
        `<< /Type /Page /Parent 2 0 R /MediaBox [${media}] /Rotate ${rotate} ${unit ? `/UserUnit ${unit}` : ''} ${crop ? `/CropBox [${crop}]` : ''} /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>`,
        `<< /Length ${Buffer.byteLength(stream)} >>\nstream\n${stream}\nendstream`,
        font??'<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>', ...extra
    ];
    let body='%PDF-1.7\n';const offsets=[0];
    for(let i=0;i<objects.length;i++){offsets.push(Buffer.byteLength(body));body+=`${i+1} 0 obj\n${objects[i]}\nendobj\n`;}
    const xref=Buffer.byteLength(body);
    body+=`xref\n0 ${objects.length+1}\n0000000000 65535 f \n`;
    for(const offset of offsets.slice(1))body+=`${String(offset).padStart(10,'0')} 00000 n \n`;
    body+=`trailer\n<< /Size ${objects.length+1} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
    return Buffer.from(body);
}
(async()=>{
    const loader=path.resolve(process.argv[2]);const create=require(loader);
    const m=await create({wasmBinary:fs.readFileSync(loader.replace(/\.js$/,'.wasm')),print:()=>{},printErr:()=>{}});
    const fixtures=[];
    const allocate=bytes=>{const h=m._e1_alloc(bytes.length);assert.ok(h);m.HEAPU8.set(bytes,m._e1_data(h));return h;};
    const takeBytes=()=>{const h=m._e1_result(),n=m._e1_size(h),d=m._e1_alloc(n);assert.ok(d);assert.equal(m._e1_copy(h,d),0);
        const bytes=m.HEAPU8.slice(m._e1_data(d),m._e1_data(d)+n);m._e1_release(d);m._e1_release(h);return bytes;};
    const take=()=>JSON.parse(new TextDecoder('utf8',{fatal:true}).decode(takeBytes()));
    function inspect(options,outputBytes=16777216){
        const budget=Buffer.alloc(36);[1,36,8388608,512,100000,256,4000000,outputBytes,64].forEach((v,i)=>budget.writeUInt32LE(v,i*4));
        const bytes=pdf(options),input=allocate(bytes),b=allocate(budget);assert.equal(m._e1_open(input,b),0);const doc=m._e1_result();m._e1_release(input);m._e1_release(b);
        assert.equal(m._e1_page(doc,0),0);const page=m._e1_result();assert.equal(m._e1_layout_capabilities(page),0);const capabilities=take();
        const status=m._e1_layout_facts(page);const facts=status===0?take():null;
        if(options?.legacy){
            assert.equal(m._e1_facts(page),0);assert.deepEqual(take().media,[0,0,72,144]);
            assert.equal(m._e1_text(page),0);assert.equal(take().characters[0].unicode,65);
            const query=allocate(Buffer.from([65,0,0,0]));assert.equal(m._e1_search(page,query),0);assert.equal(take().length,1);m._e1_release(query);
            assert.equal(m._e1_raster(page,32,64),0);const raster=takeBytes();assert.equal(raster.length,32*64*4+16);
            assert.equal(new DataView(raster.buffer).getUint32(0,true),0x31424752);assert.ok(raster.slice(16).some(b=>b!==255));
            assert.equal(m._e1_capabilities(page),0);take();assert.equal(m._e1_actions(page),0);assert.deepEqual(take().links,[]);
        }
        assert.equal(m._e1_close(page),0);assert.equal(m._e1_close(doc),0);assert.equal(m._e1_live(),0);
        fixtures.push({sha256:sha(bytes),bytes:bytes.length,outputBudget:outputBytes,status});
        return {capabilities,facts,status};
    }
    let cases=0;
    for(const [options,status,value,method] of [
        [{},'available',1,'default-userunit'],[{unit:'2'},'available',2,'page-dictionary-userunit'],
        [{unit:'6 0 R',extra:['2']},'available',2,'page-dictionary-userunit'],
        [{parentUnit:'3'},'available',1,'default-userunit'],
        ...['0','-1','75001','(2)','null','[2]','/Two'].map(unit=>[{unit},'indeterminate',null,'invalid']),
    ]){
        const result=inspect(options);assert.equal(result.status,0);assert.equal(result.facts.userUnit.status,status);
        assert.equal(result.facts.userUnit.value,value);assert.equal(result.facts.userUnit.method,method);
        assert.equal(result.capabilities.physicalScale,status);assert.equal(result.facts.complete,true);cases++;
    }
    for(const rotate of [0,90,180,270]){
        const {facts,status}=inspect({unit:'2',media:'-10 -20 72 144',crop:'0 0 70 140',rotate});assert.equal(status,0);
        assert.deepEqual(facts.media,[-10,-20,72,144]);assert.deepEqual(facts.effective,[0,0,70,140]);assert.equal(facts.rotation,rotate);
        // Independent literal raw+UserUnit oracle: 72 raw units at scale2 is50.8mm.
        assert.equal(72*facts.userUnit.value*25.4/72,50.8);
        const a=facts.characters.find(c=>c.unicode===65);assert.ok(a);assert.equal(a.nominalSize,12);assert.equal(a.matrix.length,6);
        assert.equal(a.inkBox,null);assert.equal(a.inkStatus,'unsupported');assert.equal(a.styleStatus,'unsupported');cases++;
    }
    const bounded=inspect({text:'A'.repeat(100001),media:'0 0 1000000 144'});assert.equal(bounded.status,3,`native characters: ${bounded.facts?.characters?.length}`);assert.equal(bounded.facts,null);cases++;
    const output=inspect({text:'A'.repeat(200),media:'0 0 10000 144'},4096);assert.equal(output.status,3);assert.equal(output.facts,null);cases++;
    for(const content of [
        'q 2 0 0 3 5 7 cm BT /F1 12 Tf 10 20 Td (A) Tj ET Q',
        'q 0 0 1 1 re W n BT /F1 12 Tf 10 20 Td (A) Tj ET Q',
        'BT /F1 12 Tf 3 Tr 10 20 Td (A) Tj ET',
        'BT /F1 12 Tf 2 Tr 2 w 10 20 Td (A) Tj ET',
    ]){
        const result=inspect({content});assert.equal(result.status,0);
        for(const ch of result.facts.characters){assert.equal(ch.inkBox,null);assert.equal(ch.inkStatus,'unsupported');assert.equal(ch.styleStatus,'unsupported');}
        if(content.includes('3 Tr'))assert.ok(result.facts.characters.some(ch=>ch.renderMode===3));
        if(content.includes('2 Tr'))assert.ok(result.facts.characters.some(ch=>ch.renderMode===2));
        cases++;
    }
    const proc='600 0 d0 0 0 600 800 re f';
    const type3=inspect({font:'<< /Type /Font /Subtype /Type3 /FontBBox [0 0 600 800] /FontMatrix [0.001 0 0 0.001 0 0] /CharProcs << /A 6 0 R >> /Encoding << /Type /Encoding /Differences [65 /A] >> /FirstChar 65 /LastChar 65 /Widths [600] /Resources << >> >>',
        extra:[`<< /Length ${Buffer.byteLength(proc)} >>\nstream\n${proc}\nendstream`]});
    assert.equal(type3.status,0);for(const ch of type3.facts.characters)assert.equal(ch.inkBox,null);cases++;
    assert.equal(inspect({legacy:true}).status,0);cases++;
    // Legacy facts/text/raster exports remain available on the same candidate.
    assert.equal(typeof m._e1_facts,'function');assert.equal(typeof m._e1_text,'function');assert.equal(typeof m._e1_raster,'function');
    m._e1_reset();assert.equal(m._e1_live(),0);
    console.log(JSON.stringify({status:'passed',cases,measurement:'parsed-page-dictionary-and-native-text',glyphInk:'unsupported',textStyle:'unsupported',fixtures}));
})().catch(error=>{console.error(error);process.exitCode=1;});
