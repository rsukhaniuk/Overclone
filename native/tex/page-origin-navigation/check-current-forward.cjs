'use strict';
// Add strict DEFAULT forward-box containment to the frozen independent glyph
// oracle. The PDF point is selected before any map query; no candidate picking.
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict'),{createHash}=require('node:crypto'),Module=require('node:module');
const parent=path.join(__dirname,'check-corpus-navigation.cjs');let text=fs.readFileSync(parent,'utf8');
assert.equal(createHash('sha256').update(text).digest('hex'),'cd05c7b92e29b85928551ea069ba93e0360e07238d52b7ea6b3c11dbe5933e32');
const before="   if(queryPoint){assert.equal(q._sq_inverse";assert.equal(text.split(before).length,2);
text=text.replace(before,`   let forwardProof=null;
   if(queryPoint){
    const f=forward.matches[0];assert.equal(f.page,hit.page,'default forward page');
    const b=f.box;assert.ok(b.width>0&&b.height+b.depth>0,'native nonempty forward box');
    const m=c.kind==='pdf'?[1,0,0,-1,0,hit.pg.facts.media[3]]:native.writerToPdfRaw;
    const points=[];for(const x of [b.h,b.h+b.width])for(const y of [b.v-b.height,b.v+b.depth])points.push([m[0]*x+m[2]*y+m[4],m[1]*x+m[3]*y+m[5]]);
    const bounds=[Math.min(...points.map(p=>p[0])),Math.min(...points.map(p=>p[1])),Math.max(...points.map(p=>p[0])),Math.max(...points.map(p=>p[1]))];
    const tolerance=.01;assert.ok(pdfPoint[0]>=bounds[0]-tolerance&&pdfPoint[0]<=bounds[2]+tolerance&&pdfPoint[1]>=bounds[1]-tolerance&&pdfPoint[1]<=bounds[3]+tolerance,c.name+' '+word+' default native forward box misses actual E1 glyph point');
    forwardProof={defaultCandidate:0,pdfBounds:bounds,toleranceBp:tolerance,containsIndependentGlyph:true};
   }
   if(queryPoint){assert.equal(q._sq_inverse`);
const row='observations.push({word,file,line,page:hit.page';assert.equal(text.split(row).length,2);text=text.replace(row,'observations.push({forwardProof,word,file,line,page:hit.page');
const child=new Module(parent,module);child.filename=parent;child.paths=Module._nodeModulePaths(path.dirname(parent));child._compile(text,parent);
