"use strict";
// Real native combined project/carry admission, independent of browser policy.
const {fs,path,assert,hashBytes,engine}=require('../../synctex-fixtures/xe/runtime.cjs');
const target=Number(process.argv[2]),limit=Number(process.argv[3]||256),out=process.env.RESULTS;
assert.ok(out&&[192,256].includes(limit)&&target>128);fs.mkdirSync(out,{recursive:true});
(async()=>{
 const lines=[],x=await engine(lines),{m,call,seed,map}=x;
 if(limit===256)assert.equal(call('oc_xe_work_file_limit'),256);else assert.equal(typeof m._oc_xe_work_file_limit,'undefined');
 const main='тези/головний 😀.tex',include=[];const inputs=[];
 for(let i=0;i<127;i++){const name='частини/розділ'+i+'.tex',b=Buffer.from('% captured '+i+'\n');inputs.push([name,b]);include.push('\\input{'+name+'}');}
 const src=Buffer.from('\\documentclass{article}\n\\begin{document}\nCapacity \\ref{known}.\n'+include.join('\n')+'\n\\end{document}\n');inputs.unshift([main,src]);
 const carry=[['головний 😀.aux',Buffer.from('\\relax\n\\newlabel{known}{{1}{1}}\n')]];
 for(let i=1;i<target-128;i++)carry.push(['state/previous'+i+'.aux',Buffer.from('\\relax\n')]);
 assert.equal(inputs.length,128);assert.equal(inputs.length+carry.length,target);
 for(const[n,b]of [...inputs,...carry])seed('/work/'+n,b);
 assert.equal(call('oc_mem_configure',['number','number','number'],[1,1048575,919]),1);assert.equal(call('setMainEntry',['string'],[main]),0);
 const opened=new Set(),open=m.FS.open;m.FS.open=function(n,...a){const r=open.call(this,n,...a);if(typeof n==='string')opened.add(path.posix.resolve(m.FS.cwd(),n));return r;};
 const code=call('compileLaTeX'),raw=map(919);fs.writeFileSync(path.join(out,'native.log'),lines.join('\n'));
 const expected=target<=limit?0:-4;assert.equal(code,expected,lines.slice(-10).join('\n'));
 if(!code){for(const[n]of inputs)assert.ok(opened.has('/work/'+n),n);assert.ok(opened.has('/work/головний 😀.aux'));assert.ok(!lines.some(s=>s.includes('undefined references')));const b=Buffer.from(m.FS.readFile('/work/головний 😀.xdv'));assert.equal(b[0],247);assert.equal(b[1],7);fs.writeFileSync(path.join(out,'result.xdv'),b);assert.ok(raw.length);fs.writeFileSync(path.join(out,'map.synctex'),raw);}
 else{assert.equal(raw.length,0);assert.throws(()=>m.FS.readFile('/work/головний 😀.xdv'));}
 // AUX is read-only input in these fixtures; engine's final AUX may replace it.
 for(const[n,b]of inputs)assert.deepEqual(Buffer.from(m.FS.readFile('/work/'+n)),b);
 const receipt={status:'pass',scope:'actual native256 joint budget; 128 original captured files and real prior root AUX plus bounded carry',limit,target,captured:inputs.length,carry:carry.length,nativeReturn:code,testSource:{length:fs.statSync(__filename).size,sha256:hashBytes(fs.readFileSync(__filename))},identities:x.identities,inputIdentities:[...inputs,...carry].map(([name,b])=>({name,length:b.length,sha256:hashBytes(b)})),sourceMap:raw.length?{length:raw.length,sha256:hashBytes(raw)}:null};
 fs.writeFileSync(path.join(out,'receipt.json'),JSON.stringify(receipt,null,2)+'\n');console.log(JSON.stringify({status:'pass',target,limit,code}));
})().catch(e=>{console.error(e);process.exitCode=1;});
