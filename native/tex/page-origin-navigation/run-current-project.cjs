'use strict';
// QA-only exact-seam reuse of the real native FFI fixture. No product workers,
// replies, native functions, source/font/bibliography bytes or maps are replaced.
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict'),crypto=require('node:crypto'),Module=require('node:module');
const parent=path.join(__dirname,'../graphics-navigation/reproduce.cjs');let text=fs.readFileSync(parent,'utf8');
assert.equal(crypto.createHash('sha256').update(text).digest('hex'),'5df2186805af3d5cef745f4ae0da0f94544271b797de87777515680b78c5d6db');
function replace(a,b){assert.equal(text.split(a).length,2,a.slice(0,100));text=text.replace(a,b);}
replace("['dvi','xe','lua'].includes(kind)","['pdf','dvi','xe','lua'].includes(kind)");
const start=text.indexOf('const project={};'),end=text.indexOf('\nconst names=',start);assert.ok(start>=0&&end>start);
text=text.slice(0,start)+`const project={};
if(process.env.PROJECT){
 const base=path.resolve(process.env.PROJECT);
 function walk(d){for(const e of fs.readdirSync(d,{withFileTypes:true})){const p=path.join(d,e.name);assert.ok(!e.isSymbolicLink());if(e.isDirectory())walk(p);else{assert.ok(e.isFile());project[path.relative(base,p).split(path.sep).join('/')]=fs.readFileSync(p);}}}walk(base);assert.ok(project[root]);
}else{
 const fixture=process.env.CORPUS_CASE||'portable';assert.ok(['portable','packages','biblatex'].includes(fixture));
 const directory=fixture==='packages'?'PackageFontFixtures':fixture==='biblatex'?'CompilerBibliographyFixtures':'CompilerSuiteFixtures';
 project[root]=fs.readFileSync(path.join(__dirname,'../../../tests',directory,['xe','lua'].includes(kind)?'main-unicode.tex':'main-latin.tex'));
 for(const n of ['chapter.tex','references.bib','figure.xbb'])project[n]=fs.readFileSync(path.join(__dirname,'../../../tests/CompilerSuiteFixtures',n));
 project['figure.png']=fs.readFileSync(path.join(__dirname,'../../../src/Client/wwwroot/icons/icon-192.png'));
}
// Same data-only projection as NativeRuntime.SuiteFiles; pin bytes remain exact.
resources=resources.filter(p=>!p.name.startsWith('notices/')&&p.name!=='biber.data'&&(kind==='xe'||!['icudt68l.dat','xetexfontlist.txt'].includes(p.name)));
`+text.slice(end);
replace("const names=kind==='dvi'?['dvi-map-engine.js','dvi-map-engine.wasm']:","const names=['pdf','dvi'].includes(kind)?['semantic-engine.js','semantic-engine.wasm']:");
replace("const fmt=kind==='dvi'?'latex-dvi.fmt':'xelatex.fmt';","const fmt=kind==='pdf'?'pdflatex.fmt':kind==='dvi'?'latex-dvi.fmt':'xelatex.fmt';");
replace("(kind==='dvi'?'swiftlatexpdftex.fmt':'xelatex.fmt')","(['dvi','pdf'].includes(kind)?'swiftlatexpdftex.fmt':'xelatex.fmt')");
replace("(kind==='dvi'?'\"'+root+'\"':root)","(['dvi','pdf'].includes(kind)?'\"'+root+'\"':root)");
replace("if(/\\.(js|wasm|fmt)$/.test(x.name)||x.name.includes('/'))continue;","if(!(/\\.(tfm|pfb|ttf|otf|vf|map|enc)$/.test(x.name)||['glyphlist.txt','pdfglyphlist.txt'].includes(x.name)))continue;");
// Keep actual generated BibLaTeX control and ordinary selected state. Original
// references.bib remains a project input, never replaced by generated carry.
replace("if(/\\.(aux|bbl|blg)$/.test(n))","if(/\\.(aux|bbl|blg|toc|lof|lot|out)$/.test(n)||n.endsWith('-blx.bib'))");
replace("assert.equal(call('oc_luatex_declare_output',['string'],['chapter.aux']),0);","assert.equal(call('oc_luatex_declare_output',['string'],['chapter.aux']),0);assert.equal(call('oc_luatex_declare_output',['string'],['result-blx.bib']),0);");
replace("['result.aux','chapter.aux','result.bbl','result.blg']","['result.aux','chapter.aux','result.bbl','result.blg','result.out','result.toc','result.lof','result.lot','result-blx.bib']");
replace('assert.equal(instantiated,1);',`assert.equal(instantiated,1);
 if(process.env.REQUIRE_ORIGIN==='1'&&(operation==='convert'||(kind==='lua'&&operation==='latex'))){assert.equal(kind==='lua'?m._oc_lua_origin_abi():m._oc_dvi_origin_abi(),1);}
 if(kind==='lua'&&operation==='latex'){assert.equal(m._oc_lua_harfbuzz_abi(),1);assert.equal(m._oc_lua_text_semantics_abi(),1);assert.equal(m._oc_lua_output_declaration_limit(),256);}
 if(kind==='dvi'&&operation==='convert'&&process.env.REQUIRE_ORIGIN==='1')assert.equal(m._oc_dvi_font_semantics_abi(),1);
`);
// Optional direct pdfTeX observer under test. Legacy callers keep their exact
// component and raw-only path, and the derivative is never loaded for DVI.
if(process.env.PDF_CANDIDATE){
 assert.equal(process.argv[2],'pdf');assert.equal(process.argv[3],'latex');
 if(process.env.PDF_DIRECT_NO_MAP==='1') replace("  assert.equal(call('setMainEntry',['string'],[entry]),0);assert.equal(call('oc_mem_configure',['number','number','number'],[1,1048575,tag]),1);", "  assert.equal(call('setMainEntry',['string'],[entry]),0);assert.equal(call('oc_mem_configure',['number','number','number'],[0,0,tag]),1);");
 replace("const moduleDir=operation==='convert'", "const moduleDir=kind==='pdf'?process.env.PDF_CANDIDATE:operation==='convert'");
 replace("if(operation==='convert'&&moduleDir!==dir)", "if(kind==='pdf')names.splice(0,2,'engine.js','engine.wasm');if(operation==='convert'&&moduleDir!==dir)");
 replace(" const call=(n,t=[],a=[])=>m.ccall(n,'number',t,a);", ` const call=(n,t=[],a=[])=>m.ccall(n,'number',t,a);
 assert.equal(m._oc_pdftex_origin_abi(),1);
 assert.equal(m._oc_synctex_geometry_length(tag),0);
 assert.equal(m._oc_synctex_geometry_length(tag+1),0);
 assert.equal(m._oc_pdftex_legacy_zero_origin(tag),0);
 `);
 replace("  raw=readToken('oc_synctex_length','oc_synctex_copy',tag,1048575);\n }",
 `  raw=readToken('oc_synctex_length','oc_synctex_copy',tag,1048575);
  geometry=readToken('oc_synctex_geometry_length','oc_synctex_geometry_copy',tag,131072);
  assert.equal(m._oc_synctex_geometry_length(tag+1),0);
  assert.equal(m._oc_pdftex_legacy_zero_origin(tag+1),0);
  const legacyZeroOrigin=m._oc_pdftex_legacy_zero_origin(tag);
  assert.ok(legacyZeroOrigin===0||legacyZeroOrigin===1);
  if(legacyZeroOrigin)assert.ok(raw.length>0&&geometry.length===0);
  if(!raw.length)assert.ok(!legacyZeroOrigin&&!geometry.length);
  fs.writeFileSync(path.join(out,'origin-attestation.json'),JSON.stringify({
   capability:m._oc_pdftex_origin_abi(),tag,legacyZeroOrigin,rawLength:raw.length,geometryLength:geometry.length
  },null,2)+'\\n');
  if(geometry.length){
   const copy=m._malloc(geometry.length);
   assert.equal(m._oc_synctex_geometry_copy(tag+1,copy,geometry.length),0);
   assert.equal(m._oc_synctex_geometry_copy(tag,0,geometry.length),0);
   assert.equal(m._oc_synctex_geometry_copy(tag,copy,geometry.length-1),0);
   assert.equal(m._oc_synctex_geometry_copy(tag,copy,131073),0);
   assert.equal(m._oc_synctex_geometry_copy(tag,copy,geometry.length),geometry.length);
   assert.deepEqual(Buffer.from(m.HEAPU8.subarray(copy,copy+geometry.length)),geometry);
   m._free(copy);
  }
 }`);
}
const child=new Module(parent,module);child.filename=parent;child.paths=Module._nodeModulePaths(path.dirname(parent));child._compile(text,parent);
