'use strict';
// Actual native exports only. No public hook can supply geometry/neutral proof.
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict'),{createHash}=require('node:crypto');
const [runtime,lua,dvi,xe,query,out]=process.argv.slice(2);assert.ok(!fs.existsSync(out));fs.mkdirSync(out,{recursive:true});
const pin=b=>({length:b.length,sha256:createHash('sha256').update(b).digest('hex')});
(async()=>{
 const rows=[];
 for(const[kind,dir,stem,modern]of [['lua',path.join(runtime,'files'),'luatex',false],['lua',lua,'luatex',true],['dvi',path.join(runtime,'files'),'dvi-map-converter',false],['dvi',dvi,'oc-dvi',true],['xe',path.join(runtime,'files'),'xdv-map-converter',false],['xe',xe,'oc-dvi',true],['query',path.join(runtime,'files'),'synctex-query',false],['query',query,'synctex-query',true]]){
  const loader=fs.readFileSync(path.join(dir,stem+'.js')),wasm=fs.readFileSync(path.join(dir,stem+'.wasm'));
  const m=await require(path.join(dir,stem+'.js'))({noInitialRun:true,wasmBinary:wasm,instantiateWasm(i,d){const w=new WebAssembly.Instance(new WebAssembly.Module(wasm),i);d(w);return w.exports;}});
  if(kind==='query'){assert.equal(m._sq_version(),1);if(modern)assert.equal(m._sq_area64_abi(),1);else assert.equal(typeof m._sq_area64_abi,'undefined');}
  else{
   assert.equal(kind==='lua'?m._oc_luatex_abi():m._oc_dvi_abi_version(),kind==='lua'?5:kind==='dvi'?2:3);
   assert.equal(kind==='lua'?m._oc_lua_graphics_abi():m._oc_dvi_graphics_abi(),1);
   const origin=m[kind==='lua'?'_oc_lua_origin_abi':'_oc_dvi_origin_abi'],neutral=m[kind==='lua'?'_oc_lua_neutral_abi':'_oc_dvi_neutral_abi'];
   if(modern){assert.equal(origin(),1);assert.equal(neutral(),1);}else{assert.equal(typeof origin,'undefined');assert.equal(typeof neutral,'undefined');}
   if(kind==='lua'){assert.equal(m._oc_lua_harfbuzz_abi(),1);assert.equal(m._oc_lua_font_closure_abi(),1);assert.equal(m._oc_lua_text_semantics_abi(),1);assert.equal(m._oc_lua_output_declaration_limit(),256);assert.equal(m._oc_synctex_geometry_length(997),0);}
   else{assert.equal(m._oc_dvi_geometry_length(997),0);if(kind==='dvi')assert.equal(m._oc_dvi_font_semantics_abi(),1);}
  }
  for(const name of ['oc_dvi_geo_neutral','oc_dvi_geo_neutral_end','oc_dvi_neutral_color_depth','oc_dvi_origin_unsafe','oc_dvi_origin_final','oc_lua_neutral_tokens_begin','oc_lua_neutral_tokens','oc_lua_neutral_tokens_end','oc_lua_neutral_color_begin','oc_lua_neutral_color_program','oc_lua_neutral_color_end','oc_lua_origin_page','oc_lua_origin_root_begin','oc_lua_geometry_unsafe','oc_synctex_finish'])assert.equal(typeof m['_'+name],'undefined',name+' must remain private');
  rows.push({kind,modern,loader:pin(loader),wasm:pin(wasm),privateAuthorityExportsAbsent:true});
 }
 fs.writeFileSync(path.join(out,'receipt.json'),JSON.stringify({status:'pass',skips:0,testSource:pin(fs.readFileSync(__filename)),rows},null,2)+'\n');console.log('Actual P14/P15 capabilities and private authority export guards pass');
})().catch(e=>{console.error(e);process.exitCode=1;});
