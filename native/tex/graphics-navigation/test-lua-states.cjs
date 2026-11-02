'use strict';
// Synthetic negative/state controls, separate from UNCHANGED portable acceptance.
const {load,fs,path,assert,sha}=require('../synctex-fixtures/lua/runtime.cjs');
const cases={
 'scoped-axis':String.raw`\scalebox{.5}{SCALED}`,
 'raw-balanced':String.raw`\pdfextension literal direct {q 1 0 0 1 0 0 cm Q}`,
 'matrix-suffix':String.raw`\pdfextension save\pdfextension setmatrix{1 0 0 1 Q}\rlap{INNER}\pdfextension restore`,
 'matrix-exponent':String.raw`\pdfextension save\pdfextension setmatrix{1e0 0 0 1}\rlap{INNER}\pdfextension restore`,
 'matrix-short':String.raw`\pdfextension save\pdfextension setmatrix{1 0 0}\rlap{INNER}\pdfextension restore`,
 'matrix-singular':String.raw`\pdfextension save\pdfextension setmatrix{0 0 0 0}\rlap{INNER}\pdfextension restore`,
 'matrix-reflect':String.raw`\pdfextension save\pdfextension setmatrix{-1 0 0 1}\rlap{INNER}\pdfextension restore`,
 'matrix-shear':String.raw`\pdfextension save\pdfextension setmatrix{1 .1 0 1}\rlap{INNER}\pdfextension restore`,
 'missing-restore':String.raw`\pdfextension save\pdfextension setmatrix{.5 0 0 .5}\rlap{INNER}`,
 'missing-save':String.raw`\pdfextension restore`,
 'misplaced-restore':String.raw`\pdfextension save\pdfextension setmatrix{.5 0 0 .5}INNER\pdfextension restore`,
};
const name=process.argv[2];assert.ok(Object.hasOwn(cases,name));
(async()=>{
 const out=process.env.RESULTS;assert.ok(out);fs.mkdirSync(out,{recursive:true});const root='тези/state test.tex',tag=701,lines=[];
 const {m,call,seed,read,identities}=await load(root,lines,5);assert.equal(m._oc_lua_graphics_abi(),1);
 const b=Buffer.from('\\documentclass{article}\n\\usepackage{graphicx}\n\\pagestyle{empty}\n\\begin{document}\nBEFORE\\par\n'+cases[name]+'\n\\par AFTER\n\\end{document}\n');seed(1,root,b);assert.equal(call('oc_mem_configure',['number','number','number'],[1,1048575,tag]),1);
 const rc=call('oc_luatex_compile_latex');
 if(name==='missing-restore'){
  assert.notEqual(rc,0);assert.equal(call('oc_synctex_length',['number'],[tag]),0);assert.equal(call('oc_synctex_geometry_length',['number'],[tag]),0);
  fs.writeFileSync(path.join(out,name+'.log'),lines.join('\n'));fs.writeFileSync(path.join(out,name+'.json'),JSON.stringify({schemaVersion:1,status:'pass',name,nativeReturn:rc,expectedNativeFailure:true,rawMapBytes:0,geometryBytes:0,identities,syntheticNotPortableFixture:true},null,2)+'\n');return;
 }
 assert.equal(rc,0,lines.join('\n'));const raw=read('oc_synctex_length','oc_synctex_copy',tag);assert.ok(raw.length);
 const geo=read('oc_synctex_geometry_length','oc_synctex_geometry_copy',tag,131072);assert.ok(geo.length);let g;try{g=JSON.parse(geo);}catch(e){throw new Error('bad native geometry',{cause:e});}assert.equal(g.pages.length,1);
 fs.writeFileSync(path.join(out,name+'.log'),lines.join('\n'));fs.writeFileSync(path.join(out,name+'.geometry.json'),geo);
 assert.equal(g.pages[0].status,name==='scoped-axis'?'available':'unsupported');if(name!=='scoped-axis'){assert.equal(g.pages[0].reason,'out-of-domain');assert.equal(g.pages[0].writerToPdfRaw,null);}
 assert.equal(call('oc_synctex_geometry_length',['number'],[tag+1]),0);
 fs.writeFileSync(path.join(out,name+'.tex'),b);fs.writeFileSync(path.join(out,name+'.synctex'),raw);fs.writeFileSync(path.join(out,name+'.geometry.json'),geo);fs.writeFileSync(path.join(out,name+'.log'),lines.join('\n'));
 assert.notEqual(call('oc_luatex_compile_latex'),0);assert.equal(call('oc_synctex_length',['number'],[tag]),0);assert.equal(call('oc_synctex_geometry_length',['number'],[tag]),0);
 fs.writeFileSync(path.join(out,name+'.json'),JSON.stringify({schemaVersion:1,status:'pass',name,nativeReturn:rc,sourceSha256:sha(b),rawSha256:sha(raw),geometry:g,identities,syntheticNotPortableFixture:true},null,2)+'\n');console.log(name+' native state pass');
})().catch(e=>{console.error(e);process.exitCode=1;});
