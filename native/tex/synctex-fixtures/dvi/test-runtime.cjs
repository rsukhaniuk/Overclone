'use strict';
const {fs,path,assert,hashBytes,engine}=require('./runtime.cjs');
const mode=process.argv[2]||'positive',tag=Number(process.env.PASS_TAG||601);
const out=process.env.RESULTS||'/control/r2-dvi-maps/evidence/runtime';
const fixtureRoot='розділ/my thesis.tex',root=mode==='bibtex'?'main.tex':fixtureRoot,project=path.join(__dirname,'project');
(async()=>{
 fs.mkdirSync(out,{recursive:true});const lines=[];
 const {m,call,seed,map,identities}=await engine(lines);
 const protectedFont=Buffer.from(m.FS.readFile('/tex/cmr10.tfm'));
 let checks=0;const eq=(a,b)=>{assert.deepEqual(a,b);++checks;};
 eq(call('oc_synctex_length',['number'],[tag]),0);
 const exported=WebAssembly.Module.exports(new WebAssembly.Module(fs.readFileSync(path.join(process.env.ENGINE||'/control/r2-dvi-maps/build/engine-fourth','engine.wasm')))).map(x=>x.name);
 for(const n of ['oc_synctex_finish','oc_synctex_pdf_close','oc_mem_stage_checked','oc_synctex_begin','oc_mem_abort']){assert.ok(!exported.includes(n));++checks;}
 const config=(mode,cap,t=tag)=>call('oc_mem_configure',['number','number','number'],[mode,cap,t]);
 if(mode!=='default-off')eq(config(mode==='off'?0:1,mode==='off'?0:mode==='cap'?1:Number(process.env.MAP_CAP||1048575)),1);
 if(mode==='invalid-config')eq(config(2,1048575),0);
 if(mode==='repeat-config')eq(config(1,1048575),0);
 let source=fs.readFileSync(path.join(project,fixtureRoot));let text=source.toString('utf8');
 if(['off','default-off'].includes(mode))text=text.replace('\\begin{document}','\\synctex=1\r\n\\begin{document}');
 if(mode==='mag1200')text=text.replace('\\documentclass','\\mag=1200\r\n\\documentclass');
 if(mode==='bounds')text=text.replace('width 300bp height 200bp','width 3000000bp height 200bp');
 if(mode==='changed')text=text.replace('ROOTMARK','ROOTSHIFT').replace('\\kern20bp','\\kern27bp');
 const specials={unsafe:'pdf:literal direct 1 0 0 1 11 13 cm',noninvertible:'pdf:literal direct 0 0 0 0 0 0 cm','bbox-program':'pdf:bop 1 0 0 1 11 13 cm'};
 if(specials[mode])text=text.replace('\\kern20bp','\\special{'+specials[mode]+'}\r\n\\kern20bp');
 const attributes={'media-override':'/MediaBox [-20 -30 280 170]','dict-injection':'/OCNativeControl 1','rotate-name':'/Rotate90 0','crop-name':'/CropBoxExtra [5 10 290 190]'};
 if(attributes[mode])text=text.replace('/CropBox [5 10 290 190] /Rotate 0',attributes[mode]);
 if(mode==='failure')text=text.replace('\\shipout\\vbox to 170bp','\\errmessage{INTENTIONAL-AFTER-SHIPOUT}\r\n\\shipout\\vbox to 170bp');
 if(mode==='runaway')text=text.replace('\\end{document}',String.raw`\immediate\write16{RUNAWAY-BEGIN}\loop\iftrue\repeat`);
 if(mode==='quota')text=text.replace('\\end{document}',String.raw`\newcount\flood\flood=0\loop\advance\flood by1\immediate\write16{`+'X'.repeat(1024)+String.raw`}\ifnum\flood<300\repeat\end{document}`);
 if(mode==='read-traversal'){seed('/secret.tex',Buffer.from('\\errmessage{SECRET-READ}'));text=text.replace('\\end{document}',String.raw`\input{../secret.tex}\end{document}`);}
 if(mode==='write-resource')text=text.replace('\\end{document}',String.raw`\newwrite\attack\immediate\openout\attack=/tex/cmr10.tfm\immediate\write\attack{CORRUPTION}\end{document}`);
 if(mode==='pages512'||mode==='pages513')text=text.slice(0,text.indexOf('\\shipout'))+String.raw`\newcount\fixturepage\fixturepage=0\loop\advance\fixturepage by1
\shipout\vbox{\hbox{MANYMARK}}
\ifnum\fixturepage<`+(mode==='pages512'?'512':'513')+String.raw`\repeat\end{document}`;
 if(mode==='bibtex'){
  seed('/work/main.aux',Buffer.from('\\relax\n\\citation{fixture}\n\\bibstyle{plain}\n\\bibdata{references}\n'));
  seed('/work/references.bib',Buffer.from('@book{fixture,title={Fixture},author={Author, A},publisher={Fixture Press},year={2025}}\n'));
 }
 source=Buffer.from(text);
 const files={};
 for(const rel of [root,'стилі/local class.cls','стилі/local style.sty','глави/chapter one.tex']){
  let b=rel===root?source:fs.readFileSync(path.join(project,rel));
  if(mode==='inline-include'&&rel==='глави/chapter one.tex')b=Buffer.from(b.toString().replace('\\hbox{INCLUDEMARK}','INCLUDEMARK'));
  seed('/work/'+rel,b);files[rel]={length:b.length,sha256:hashBytes(b)};
 }
 eq(call('setMainEntry',['string'],[mode==='bibtex'?root:'"'+root+'"']),0);
 if(mode==='postamble'){const golden=fs.readFileSync(path.join(out,'positive.synctex'));assert.ok(Number(process.env.MAP_CAP)>golden.indexOf('Postamble:')&&Number(process.env.MAP_CAP)<golden.length);++checks;}
 let rc;
 if(mode==='format'){
  seed('/tex/pdflatex.ini',fs.readFileSync(path.join(__dirname,'../../latex-dvi/latex-dvi.ini')));
  rc=call('compileFormat');
 }else if(mode==='bibtex'){
  rc=call('compileBibtex');
 }else rc=call('compileLaTeX');
 fs.writeFileSync(path.join(out,mode+'.console.txt'),lines.join('\n'));
 fs.writeFileSync(path.join(out,mode+'.source.tex'),source);
 if(mode==='format'){
  const p='/work/pdflatex.fmt';assert.ok(m.FS.analyzePath(p).exists);++checks;
  fs.writeFileSync(path.join(out,mode+'.fmt'),Buffer.from(m.FS.readFile(p)));
 }
 if(mode==='bibtex'){
  for(const n of ['main.bbl','main.blg'])if(m.FS.analyzePath('/work/'+n).exists)fs.writeFileSync(path.join(out,mode+'.'+n.split('.').pop()),Buffer.from(m.FS.readFile('/work/'+n)));
  eq(rc,0);assert.ok(Buffer.from(m.FS.readFile('/work/main.bbl')).includes(Buffer.from('Fixture')));++checks;
 }
 eq(Buffer.from(m.FS.readFile('/work/'+root)),source);
 eq(Buffer.from(m.FS.readFile('/tex/cmr10.tfm')),protectedFont);
 const artifacts={};
 for(const name of m.FS.readdir('/work')){
  if(!/\.(dvi|log|pdf|aux|bbl|blg)$/.test(name))continue;
  const b=Buffer.from(m.FS.readFile('/work/'+name));fs.writeFileSync(path.join(out,mode+'.'+name.split('.').pop()),b);artifacts[name]={length:b.length,sha256:hashBytes(b)};
 }
 const failure=['failure','read-traversal','write-resource'].includes(mode);
 if(failure){assert.notEqual(rc,0);++checks;assert.ok(lines.some(x=>x.includes('[1]')));++checks;}
 else if(mode!=='bibtex')eq(rc,0);
 let raw=map(tag);
 const noMap=failure||['off','default-off','cap','postamble','invalid-config','repeat-config','quota','bibtex','format'].includes(mode);
 if(noMap)eq(raw.length,0);
 else{
  assert.ok(raw.length);++checks;eq(call('oc_synctex_status'),1);
  assert.match(raw.toString(),/Output:dvi\nMagnification:(1000|1200)\nUnit:1\nX Offset:4736287\nY Offset:4736287\n/);++checks;
  eq(call('oc_synctex_length',['number'],[0]),0);eq(call('oc_synctex_length',['number'],[tag+1]),0);
  const p=m._malloc(raw.length);m.HEAPU8.fill(0x5a,p,p+raw.length);
  for(const [t,c]of[[tag,raw.length-1],[tag+1,raw.length],[tag,1048576]]){eq(call('oc_synctex_copy',['number','number','number'],[t,p,c]),0);eq(m.HEAPU8.subarray(p,p+raw.length).every(v=>v===0x5a),true);}m._free(p);
  fs.writeFileSync(path.join(out,mode+'.synctex'),raw);
  if(mode==='api-error')eq(call('setMainEntry',['string'],['again.tex']),-1);
  else if(mode==='post-config')eq(config(1,1048575),0);
  else if(mode==='bibtex-after-success')eq(call('compileBibtex'),-1);
  else eq(call('compileLaTeX'),-1);
  eq(call('oc_synctex_length',['number'],[tag]),0);
 }
 if(mode==='format'||mode==='bibtex'){
  eq(call('compileLaTeX'),-1);eq(call('compileBibtex'),-1);eq(call('compileFormat'),-1);
  eq(call('oc_synctex_length',['number'],[tag]),0);
 }
 const markerFiles={ROOTMARK:root,CLASSMARK:'стилі/local class.cls',STYLEMARK:'стилі/local style.sty',INCLUDEMARK:'глави/chapter one.tex',TWOMARK:root,THREEMARK:root,FOURMARK:root};
 const expected=Object.entries(markerFiles).map(([word,file])=>{
  const sourceText=file===root?text:fs.readFileSync(path.join(project,file),'utf8');const needle=mode==='changed'&&word==='ROOTMARK'?'ROOTSHIFT':word;
  const line=sourceText.split(/\r?\n/).findIndex(s=>s.includes(needle))+1;
  return {word:needle,file,line};
 });
 const formatBuild=mode==='format'?{iniSha256:hashBytes(fs.readFileSync(path.join(__dirname,'../../latex-dvi/latex-dvi.ini'))),outputSha256:hashBytes(fs.readFileSync(path.join(out,'format.fmt'))),outputBytes:fs.statSync(path.join(out,'format.fmt')).size}:null;
 const receipt={schemaVersion:1,status:'pass',mode,nativeReturn:rc,checks,skips:0,rawBytes:raw.length,files,expected,artifacts,identities,formatBuild};
 fs.writeFileSync(path.join(out,mode+'.json'),JSON.stringify(receipt,null,2)+'\n');console.log(JSON.stringify(receipt));
})().catch(e=>{console.error(e);process.exitCode=1;});
