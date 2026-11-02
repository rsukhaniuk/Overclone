// Genuine staged P9 WASM through actual product worker glue, not a browser,
// managed orchestration, query/renderer or live navigation acceptance claim.
const assert = require('node:assert/strict'), fs = require('node:fs'), path = require('node:path'), vm = require('node:vm');
const { webcrypto, createHash } = require('node:crypto');
const root = process.env.XE_NAVIGATION_RUNTIME_DIR;
if (!root) { console.log('SKIP real Xe transport WASM: set XE_NAVIGATION_RUNTIME_DIR to the exact complete P9 stage.'); process.exit(0); }
const hash = b => createHash('sha256').update(b).digest('hex');
const pins = JSON.parse(fs.readFileSync(path.join(root, 'pins.json'))), byName = new Map(pins.map(p => [p.name, p]));
const closure = hash(Buffer.from([...pins].sort((a,b) => a.name < b.name ? -1 : 1).map(p => `${p.name}\t${p.length}\t${p.sha256}`).join('\n')));
assert.equal(closure, 'e82466563a5865fb5c400b3fbda82518f709a78ed40ae6d8289998e75fbeb9f7');
assert.equal(pins.length, 3709); assert.equal(pins.reduce((n,p) => n + p.length, 0), 192565170);
function resource(name) {
    const p = byName.get(name), b = fs.readFileSync(path.join(root, 'files', name));
    assert.equal(b.length, p.length); assert.equal(hash(b), p.sha256); return new Uint8Array(b);
}
async function run(worker, stem, frame) {
    const messages = []; let readyResolve, readyReject;
    const ready = new Promise((resolve,reject) => {readyResolve=resolve;readyReject=reject;});
    const context = vm.createContext({ Uint8Array, Int8Array, Uint16Array, Int16Array, Uint32Array, Int32Array,
        Float32Array, Float64Array, ArrayBuffer, TextEncoder, TextDecoder, WebAssembly, crypto: webcrypto,
        performance, console: {log(){},warn(){},error(){}}, setTimeout, clearTimeout, URL, Response, WorkerGlobalScope: function(){} });
    context.self = context; context.location = {href:'https://native-test.invalid/worker.js'};
    context.postMessage = message => {messages.push(message);if(message.kind==='ready')readyResolve();if(message.kind==='boot-error')readyReject(new Error(JSON.stringify(message)));};
    context.importScripts = () => vm.runInContext(fs.readFileSync(path.join(root,'files',stem+'.js'),'utf8'), context);
    vm.runInContext(fs.readFileSync(path.join(__dirname,'../../src/Client/wwwroot/js',worker),'utf8'), context);
    const wasm = resource(stem+'.wasm'), module = new WebAssembly.Module(wasm);
    const exports = WebAssembly.Module.exports(module).map(e => e.name);
    // The unchanged legacy pdfTeX loader maps minified exports to public C names;
    // its real worker call/ABI check and BBL below validate that accepted binding.
    for (const required of stem === 'xetex' ? ['oc_xe_maps_abi','oc_mem_configure','oc_synctex_abi','oc_synctex_length','oc_synctex_copy']
        : stem === 'engine' ? [] : ['oc_dvi_abi_version','oc_dvi_geometry_configure','oc_dvi_geometry_length','oc_dvi_geometry_copy']) assert.ok(exports.includes(required));
    assert.ok(!exports.some(n => /seal|finish|geo_begin|geo_end|pdf_close/.test(n)));
    const timer=setTimeout(()=>readyReject(new Error('Actual native worker boot timeout')),15000);
    try {await Promise.all([context.onmessage({data:{operation:'boot',loaderUrl:'blob:verified-test-loader',loader:resource(stem+'.js'),wasm}}),ready]);}
    finally {clearTimeout(timer);}
    assert.ok(messages.some(m => m.kind === 'ready'), JSON.stringify(messages));
    await context.onmessage({data:frame});
    const reply = messages.findLast(m => m.kind === 'done')?.reply; assert.ok(reply, JSON.stringify(messages));
    assert.equal(reply.outcome,'native-return',JSON.stringify({outcome:reply.outcome,log:reply.log}));
    assert.equal(reply.nativeReturn,0,reply.log); assert.deepEqual(reply.tag,frame.tag);
    assert.equal(reply.wasmSha256,byName.get(stem+'.wasm').sha256); assert.equal(reply.loaderSha256,byName.get(stem+'.js').sha256);
    for(const f of frame.files) {
        const fact = reply.inputs.find(i => i.name === f.name);
        assert.deepEqual(fact && [fact.length,fact.sha256],[f.bytes.length,hash(f.bytes)]);
    }
    return reply;
}
const withBibtex=process.env.XE_NAVIGATION_BIBTEX==='1';
const prefix='__oop_runtime__/', selectedRoot='тези/my thesis.tex';
const text=s=>new TextEncoder().encode(s);
const project=[
    {name:selectedRoot,bytes:text('\\documentclass{класи/локальний}\n\\usepackage{стилі/локальний}\n\\begin{document}\nRoot boxed marker.\\par\\copy\\ClassBox\\par\\copy\\StyleBox\\par\n\\input{розділи/part one.tex}\n'+(withBibtex?'\\cite{entry}\\bibliographystyle{plain}\\bibliography{references}\n':'')+'\\end{document}\n')},
    {name:'класи/локальний.cls',bytes:text('\\NeedsTeXFormat{LaTeX2e}\n\\ProvidesClass{класи/локальний}\n\\LoadClass{article}\n\\newbox\\ClassBox\n\\setbox\\ClassBox=\\hbox{Class source-owned box}\n')},
    {name:'стилі/локальний.sty',bytes:text('\\ProvidesPackage{стилі/локальний}\n\\newbox\\StyleBox\n\\setbox\\StyleBox=\\hbox{Style source-owned box}\n')},
    {name:'розділи/part one.tex',bytes:text('\\par\\noindent\\hbox{Included source-owned box}\\par\n')}
];
if(withBibtex)project.push({name:'references.bib',bytes:text('@book{entry,author={Synthetic Author},title={Genuine native BibTeX transfer},publisher={Synthetic Press},year={2025}}\n')});
function frame(tool,ordinal,files,resources,outputs,entry,generated=[]) {
    return {operation:tool,tag:{profile:9,version:13,engineId:'xelatex',tool,ordinal,job:'job',epoch:1,request:'request',capture:'capture',
        digest:'a'.repeat(64),nonce:'operation-'+ordinal,runtimeIdentity:'b'.repeat(64)},selectedRoot,mainEntry:entry,
        files,runtimeResourceNames:resources,generatedInputNames:generated,outputs,maxLogBytes:262144,maxOutputBytes:8388608};
}
(async()=>{
    const nativeResources=pins.filter(p=>p.name==='xelatex.fmt'||!/\.(wasm|js|fmt)$/.test(p.name));
    const resources=nativeResources.map(p=>({name:prefix+p.name,bytes:resource(p.name)}));
    const outputs=['.pdf','.aux','.bbl','.blg','.log','.toc','.lof','.lot','.out','.xdv'].map(ext=>'my thesis'+ext).concat('my_thesis-blx.bib','розділи/part one.aux');
    let finalOrdinal=withBibtex?1:7;
    let producer=await run('unicode-tex-port-worker.js','xetex',frame('latex',finalOrdinal,[...resources,...project],resources.map(f=>f.name),outputs,selectedRoot));
    let bibliography=null;
    if(withBibtex){
        const state=files=>files.filter(f=>/\.(aux|bbl|toc|lof|lot|out)$/.test(f.name)||f.name==='my_thesis-blx.bib');
        let carried=state(producer.files);
        const firstAux=hash(carried.find(f=>f.name==='my thesis.aux').bytes);
        const pdfResources=pins.filter(p=>p.name==='pdflatex.fmt'||!/\.(wasm|js|fmt)$/.test(p.name)&&!['icudt68l.dat','xetexfontlist.txt'].includes(p.name))
            .map(p=>({name:prefix+(p.name==='pdflatex.fmt'?'swiftlatexpdftex.fmt':p.name),bytes:resource(p.name)}));
        const backend=await run('compile-port-worker.js','engine',frame('bibtex',2,[...pdfResources,...project,...carried],pdfResources.map(f=>f.name),outputs,'my thesis.tex',carried.map(f=>f.name)));
        assert.equal(backend.sourceMapPayload,null);assert.equal(backend.sourceMapGeometryPayload,undefined);
        const bbl=backend.files.find(f=>f.name==='my thesis.bbl');assert.ok(bbl?.bytes.length>0);
        for(const f of state(backend.files).filter(f=>f.name!=='my thesis.bbl'))assert.equal(hash(f.bytes),hash(carried.find(c=>c.name===f.name).bytes));
        carried=[...carried.filter(f=>f.name!=='my thesis.bbl'),bbl];
        const second=await run('unicode-tex-port-worker.js','xetex',frame('latex',3,[...resources,...project,...carried],resources.map(f=>f.name),outputs,selectedRoot,carried.map(f=>f.name)));
        carried=state(second.files);
        assert.notEqual(hash(carried.find(f=>f.name==='my thesis.aux').bytes),firstAux,'native bibcite changes control input and requires the existing finite rerun path');
        const rerun=await run('compile-port-worker.js','engine',frame('bibtex',4,[...pdfResources,...project,...carried],pdfResources.map(f=>f.name),outputs,'my thesis.tex',carried.map(f=>f.name)));
        const nextBbl=rerun.files.find(f=>f.name==='my thesis.bbl');assert.ok(nextBbl?.bytes.length>0);
        for(const f of state(rerun.files).filter(f=>f.name!=='my thesis.bbl'))assert.equal(hash(f.bytes),hash(carried.find(c=>c.name===f.name).bytes));
        carried=[...carried.filter(f=>f.name!=='my thesis.bbl'),nextBbl];
        const third=await run('unicode-tex-port-worker.js','xetex',frame('latex',5,[...resources,...project,...carried],resources.map(f=>f.name),outputs,selectedRoot,carried.map(f=>f.name)));
        carried=state(third.files);finalOrdinal=6;
        producer=await run('unicode-tex-port-worker.js','xetex',frame('latex',6,[...resources,...project,...carried],resources.map(f=>f.name),outputs,selectedRoot,carried.map(f=>f.name)));
        assert.equal(hash(producer.files.find(f=>f.name==='my thesis.aux').bytes),hash(carried.find(f=>f.name==='my thesis.aux').bytes));
        bibliography={actualBackendOrdinals:[2,4],finalLatexOrdinal:6,backendWasm:byName.get('engine.wasm').sha256,bbl:hash(nextBbl.bytes),finalAux:hash(producer.files.find(f=>f.name==='my thesis.aux').bytes)};
    }
    const raw=producer.sourceMapPayload; assert.ok(raw?.length>0&&raw.length<=1048575);
    assert.equal(producer.sourceMapGeometryPayload,null,'Xe producer supplies raw map, not converter geometry');
    const xdv=producer.files.find(f=>f.name==='my thesis.xdv').bytes; assert.equal(xdv[0],247); assert.equal(xdv[1],7);
    const names=[...new TextDecoder().decode(raw).matchAll(/^Input:\d+:(.*)$/gm)].map(m=>m[1].replace(/\r$/,''));
    for(const f of project.filter(f=>!f.name.endsWith('.bib'))) assert.ok(names.includes(f.name)||names.includes('./'+f.name),JSON.stringify({captured:f.name,names}));
    assert.ok(!names.some(n=>n.startsWith('/work/')),'only the fixed native /work/ prefix is translated by the producer');
    const converterResources=pins.filter(p=>['glyphlist.txt','pdfglyphlist.txt'].includes(p.name)||/\.(tfm|pfb|ttf|otf|vf|map|enc)$/.test(p.name));
    const conversionFiles=converterResources.map(p=>({name:prefix+p.name,bytes:resource(p.name)}));
    const converted=await run('dvi-port-worker.js','xdv-map-converter',frame('dvipdfmx',finalOrdinal+1,
        [...conversionFiles,...project,{name:'my thesis.xdv',bytes:xdv}],conversionFiles.map(f=>f.name),['my thesis.pdf'],'my thesis.xdv'));
    assert.equal(converted.sourceMapPayload,null,'converter has no earlier raw map');
    const pdf=converted.files[0].bytes,sidecar=converted.sourceMapGeometryPayload; assert.ok(sidecar?.length>0&&sidecar.length<=131072);
    const geometry=JSON.parse(new TextDecoder().decode(sidecar)); assert.equal(geometry.schemaVersion,1); assert.ok(geometry.pages.length<=512);
    assert.equal(geometry.pages[0].status,'available'); assert.equal(geometry.pages[0].writerToPdfRaw.length,6);
    // Independent DATA wrapper oracle for the corresponding managed codec.
    // Native sidecar matrices are copied unchanged, never inferred or recomposed.
    const wrapper={schemaVersion:1,engineId:'xelatex',finalPass:finalOrdinal,intermediateKind:'xdv',intermediateSha256:hash(xdv),
        pdfSha256:hash(pdf),mapSha256:hash(raw),pages:geometry.pages};
    const receipt={kind:'actual-P9-Xe-worker-WASM-not-browser-or-managed-acceptance',closure,
        producerOrdinal:finalOrdinal,converterOrdinal:finalOrdinal+1,bibliography,componentPins:{producer:byName.get('xetex.wasm'),format:byName.get('xelatex.fmt'),converter:byName.get('xdv-map-converter.wasm')},
        openedCapturedNames:names.filter(n=>project.some(p=>n===p.name||n==='./'+p.name)),
        outputs:{map:hash(raw),xdv:hash(xdv),pdf:hash(pdf),sidecar:hash(sidecar),wrapperData:hash(Buffer.from(JSON.stringify(wrapper)))},pages:geometry.pages.length};
    if(process.env.XE_NAVIGATION_RESULTS_DIR){
        const out=process.env.XE_NAVIGATION_RESULTS_DIR;fs.mkdirSync(out,{recursive:true});
        for(const [name,b]of [['map.synctex',raw],['output.xdv',xdv],['output.pdf',pdf],['native-geometry.json',sidecar],['wrapper-data.json',Buffer.from(JSON.stringify(wrapper))]])fs.writeFileSync(path.join(out,name),b);
    }
    console.log(JSON.stringify(receipt,null,2));
})().catch(error=>{console.error(error);process.exitCode=1;});
